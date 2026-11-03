using System.IO;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Modules;
using Microsoft.Win32;

namespace BrimDeck;

public partial class App
{
    internal ModuleHost Modules { get; private set; } = null!;
    internal ModuleCatalog ModuleCatalog { get; private set; } = null!;
    internal IReadOnlyList<DiscoveredModule> ExternalModules { get; private set; } = [];
    internal string ModuleDirectory => Path.Combine(Store.DirectoryPath, "mods");
    private string _moduleConfiguration = "";
    private int _moduleRefreshPending;
    private int _moduleContributionsDirty;
    private Task<IReadOnlyList<DiscoveredModule>>? _moduleDiscovery;
    private bool _moduleCatalogScanned;

    private void InitializeModules()
    {
        ModuleFiles = new(Store.DirectoryPath);
        Modules = new(Path.Combine(Store.DirectoryPath, "modules-data"), new DispatcherModuleScheduler(Dispatcher), [], ModuleKinds(), HostApiBindings()) { ApplicationVersion = ApplicationVersion.Current.ToString() };
        Modules.RequireCapabilities(ModuleApi.Capabilities);
        InitializeHostApiDemand();
        ModuleCatalog = new(ModuleDirectory, Path.Combine(Store.DirectoryPath, "modules-cache"), [typeof(ModuleUi).Assembly, typeof(DeckSettings).Assembly], ApplicationVersion.Current.ToString());
        Modules.Registry.Changed += QueueModuleViewsRefresh;
        Modules.Changed += QueueModuleStateRefresh;
        SystemEvents.PowerModeChanged += ModulesPowerChanged;
    }
    internal static IReadOnlyList<ModuleKind> ModuleKinds() => [
        ModuleKind.Of<ModulePage>(ModuleApi.UiPages, page => { if (string.IsNullOrWhiteSpace(page.Title) || page.CreateView is null) throw new ArgumentException("A page needs a title and view factory."); if (page.IconPath is { Length: > 0 } path) _ = System.Windows.Media.Geometry.Parse(path); if (page.Size is { } size && new[] { size.Width, size.Height, size.MinWidth, size.MaxWidth, size.MinHeight, size.MaxHeight }.Any(v => !double.IsFinite(v) || v <= 0)) throw new ArgumentException("Page sizes must be finite and positive."); }),
        ModuleKind.Of<IModuleActivity>(ModuleApi.UiActivities), ModuleKind.Of<ModuleWidget>(ModuleApi.UiWidgets, widget =>
        {
            if (widget.Enabled is null || widget.SetEnabled is null || string.IsNullOrWhiteSpace(widget.Name)) throw new ArgumentException("Invalid widget definition.");
            if (widget.IconPath is { } path) { if (path.Length > 4096) throw new ArgumentException("Widget icon is too large."); _ = System.Windows.Media.Geometry.Parse(path); }
        }) with { ValidateOwner = (owner, value) => { if (!((ModuleWidget)value).SettingsId.StartsWith(owner + ":", StringComparison.Ordinal)) throw new ModuleApiException("foreign-contribution-reference", "A widget may reference only its own module's settings."); } },
        ModuleKind.Of<IModuleSettings>(ModuleApi.UiSettings)];
    internal async Task ConfigureModulesAsync(bool refresh = false)
    {
        if (Modules is null || Exiting || _moduleRecoveryPrompting) return;
        if (_moduleFilesBusy) { _moduleConfiguration = ""; return; }
        if (!DecideModuleRecovery()) return;
        if (!ModulesSkippedForSession) await (_initialModuleFileCleanup ??= InitializeModuleFileCleanupAsync());
        if (Exiting) return;
        if (refresh || Settings.Modules.Enabled && !_moduleCatalogScanned)
        {
            var discovery = _moduleDiscovery ??= DiscoverModulesAsync();
            try { ExternalModules = await discovery; _moduleCatalogScanned = true; }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { return; }
            finally { if (ReferenceEquals(_moduleDiscovery, discovery)) _moduleDiscovery = null; }
            if (Exiting) return;
        }
        if (refresh && !Settings.Modules.Enabled && !ModulesSkippedForSession && ModuleFiles.Problem is null)
        {
            await _moduleDiskGate.WaitAsync(Lifetime.Token);
            try { await ProcessModuleFilesAsync(); }
            catch (Exception cleanupError) { Modules.Report("host", "clean module files", cleanupError); ModuleFileProblem = cleanupError.Message; }
            finally { _moduleDiskGate.Release(); }
        }
        if (Exiting) return;
        if (_moduleFilesBusy) { _moduleConfiguration = ""; return; }
        string configuration = Settings.Modules.Enabled + "/" + string.Join(',', Settings.Modules.EnabledModules.Order()) + "/" + string.Join(',', Settings.Modules.Approvals.OrderBy(p => p.Key).Select(p => p.Key + p.Value));
        if (!refresh && configuration == _moduleConfiguration) return;
        _moduleConfiguration = configuration;
        var definitions = new List<ModuleDefinition>();
        foreach (var module in ExternalModules)
        {
            if (!_moduleRemovalIds.Contains(module.Manifest.Id) && Settings.Modules.EnabledModules.Contains(module.Manifest.Id)) Modules.ResumeAfterMaintenance(module.Manifest.Id);
            definitions.Add(ModuleCatalog.Definition(module, Settings.Modules.Enabled && Settings.Modules.EnabledModules.Contains(module.Manifest.Id)
                && !_moduleRemovalIds.Contains(module.Manifest.Id) && Settings.Modules.Approvals.GetValueOrDefault(module.Manifest.Id) == module.Digest));
        }
        bool allowed = AllowModuleStartup(definitions);
        if (!allowed) definitions = definitions.Select(d => d with { Enabled = false }).ToList();
        _moduleStartupOperations++;
        try { await Modules.ReconcileAsync(definitions, Lifetime.Token); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Modules.Report("host", "configure modules", ex); }
        finally { _moduleStartupOperations--; }
        QueueModuleStateRefresh();
    }
    private async Task<IReadOnlyList<DiscoveredModule>> DiscoverModulesAsync()
    {
        await _moduleDiskGate.WaitAsync(Lifetime.Token);
        try { return await DiscoverModuleFilesAsync(); }
        finally { _moduleDiskGate.Release(); }
    }
    private async Task<IReadOnlyList<DiscoveredModule>> DiscoverModuleFilesAsync()
    {
        if (!ModulesSkippedForSession && ModuleFiles.Problem is null)
        {
            await RecoverModuleInstallationsAsync();
            var before = await Task.Run(ModuleCatalog.Discover, Lifetime.Token);
            try { await ProcessModuleFilesAsync(ModuleCatalog.Failures.Count == 0 ? before : null); }
            catch (Exception cleanupError) { Modules.Report("host", "clean module files", cleanupError); ModuleFileProblem = cleanupError.Message; }
        }
        var receipts = new Dictionary<string, string>(Settings.Modules.BundledPackages);
        var result = await Task.Run(() =>
        {
            IReadOnlyList<ModuleBundleChange> changes = [];
            Exception? bundleError = null;
            try { if (Settings.Modules.Enabled && !ModulesSkippedForSession && ModuleFiles.Problem is null) changes = ModuleBundleInstaller.Install(Path.Combine(AppContext.BaseDirectory, "mods"), ModuleDirectory, receipts, ModuleFiles.SuppressedBundles); }
            catch (Exception error) { bundleError = error; }
            return (Changes: changes, Modules: ModuleCatalog.Discover(), BundleError: bundleError);
        }, Lifetime.Token);
        if (result.BundleError is { } error) Modules.Report("host", "install bundled modules", error);
        foreach (var change in result.Changes)
        {
            Settings.Modules.BundledPackages[change.Id] = change.Digest;
            if (change.PreviousDigest is null)
            {
                if (!Settings.Modules.EnabledModules.Contains(change.Id)) Settings.Modules.EnabledModules.Add(change.Id);
                Settings.Modules.Approvals[change.Id] = change.Digest;
            }
            else if (Settings.Modules.Approvals.GetValueOrDefault(change.Id) == change.PreviousDigest)
                Settings.Modules.Approvals[change.Id] = change.Digest;
        }
        if (result.Changes.Count > 0) QueueSave();
        if (!ModulesSkippedForSession && ModuleFiles.Problem is null)
        {
            try { await Task.Run(() => ModuleFiles.Remember(result.Modules)); await UpdateModuleFileStateAsync(); }
            catch (Exception recordError) { Modules.Report("host", "record module files", recordError); ModuleFileProblem = recordError.Message; }
        }
        return result.Modules;
    }
    private void QueueModuleViewsRefresh()
    { Interlocked.Exchange(ref _moduleContributionsDirty, 1); QueueModuleStateRefresh(); }
    private void QueueModuleStateRefresh()
    {
        if (Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _moduleRefreshPending, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _moduleRefreshPending, 0);
            if (Exiting || Deck is null) return;
            if (Interlocked.Exchange(ref _moduleContributionsDirty, 0) == 1)
                foreach (var deck in Decks.ToList()) deck.RefreshModuleContributions();
            _settingsWindow?.RefreshModules();
        });
    }
    private void ModulesPowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode != PowerModes.Suspend || Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) Modules.Publish(new ModuleSuspending());
        else Dispatcher.Invoke(() => Modules.Publish(new ModuleSuspending()));
    }
    private void PauseModulesForExit() => Modules?.Publish(new ModuleSuspending());
    private void DisposeModules()
    {
        SystemEvents.PowerModeChanged -= ModulesPowerChanged;
        if (Modules is null) return;
        Modules.Registry.Changed -= QueueModuleViewsRefresh; Modules.Changed -= QueueModuleStateRefresh; Modules.Dispose();
    }
    internal void OpenModuleSettings(string id)
    {
        ShowSettings(false);
        _settingsWindow?.ShowModuleSettings(id);
    }

    private sealed class DispatcherModuleScheduler(Dispatcher dispatcher) : IModuleScheduler
    {
        public IDisposable Every(TimeSpan interval, Action callback)
        {
            var timer = new PromptTimer(dispatcher, callback) { Interval = interval }; timer.Start();
            return new ModuleAction(timer.Stop);
        }
    }
}
