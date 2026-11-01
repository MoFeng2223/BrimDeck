using System.IO;
using BrimDeck.Modules;

namespace BrimDeck;

public partial class App
{
    internal ModuleFiles ModuleFiles { get; private set; } = null!;
    private readonly SemaphoreSlim _moduleDiskGate = new(1, 1);
    private readonly HashSet<string> _moduleRemovalIds = [];
    private bool _moduleFilesBusy;
    private string? _moduleInstallProblem;
    private Task? _initialModuleFileCleanup;
    private HashSet<string>? _bundledModuleIds;
    internal bool ModuleFilesBusy => _moduleFilesBusy;
    internal int PendingModuleCleanup { get; private set; }
    internal bool HasRemovedBundles { get; private set; }
    internal string? ModuleFileProblem { get; private set; }
    private async Task InitializeModuleFileCleanupAsync()
    {
        await _moduleDiskGate.WaitAsync();
        try { if (ModuleFiles.Problem is null) await ProcessModuleFilesAsync(); else ModuleFileProblem = ModuleFiles.Problem; }
        catch (Exception error) { Modules.Report("host", "clean module files", error); ModuleFileProblem = error.Message; }
        finally { _moduleDiskGate.Release(); }
    }

    private async Task SaveModuleRemovalPreferencesAsync(IEnumerable<string> ids)
    {
        var removing = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (removing.Length == 0) return;
        foreach (string id in removing)
        {
            Settings.Modules.EnabledModules.RemoveAll(value => value == id);
            Settings.Modules.Approvals.Remove(id); Settings.Modules.BundledPackages.Remove(id);
        }
        // A previous write may have failed after the in-memory edit. Every retry
        // must persist this state again before filesystem cleanup can proceed.
        _save.Stop(); string text = Store.Serialize(Settings);
        _writing = _writing.ContinueWith(_ => Store.Write(text), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        await _writing; // Save loss of approval before removing any files.
    }
    private async Task<ModuleFileResult> ProcessModuleFilesAsync(IReadOnlyList<DiscoveredModule>? present = null)
    {
        var ids = await Task.Run(() => ModuleFiles.PendingRemovals.Concat(present is null ? [] : ModuleFiles.Missing(present)).Distinct().ToArray());
        foreach (string id in ids)
        {
            _moduleRemovalIds.Add(id);
            await Modules.StopForMaintenanceAsync(id);
            // A retry must retain the journal's original choice; a missing package never authorizes data deletion.
            await Task.Run(() => ModuleFiles.RequestRemoval(id));
        }
        await SaveModuleRemovalPreferencesAsync(ids);
        var result = await Task.Run(() => ModuleCatalog.WithCacheLease(ModuleFiles.ProcessPending));
        await UpdateModuleFileStateAsync();
        ModuleFileProblem = result.Error ?? ModuleFiles.Problem ?? _moduleInstallProblem;
        return result;
    }
    private async Task RecoverModuleInstallationsAsync()
    {
        var installer = new ModulePackageInstaller(Store.DirectoryPath);
        var ids = await Task.Run(installer.PendingIds);
        foreach (string id in ids)
        {
            await Modules.StopForMaintenanceAsync(id);
            Settings.Modules.Approvals.Remove(id); RevokeModulePermissions(id);
        }
        if (ids.Count > 0)
        {
            _save.Stop(); string text = Store.Serialize(Settings);
            _writing = _writing.ContinueWith(_ => Store.Write(text), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            await _writing;
        }
        var results = await Task.Run(installer.Recover);
        _moduleInstallProblem = string.Join("\n", results.Where(r => r.Pending).Select(r => r.Error));
        if (_moduleInstallProblem.Length == 0) _moduleInstallProblem = null;
        ModuleFileProblem = _moduleInstallProblem;
    }
    internal IReadOnlyList<string> ModuleRestorePoints(string id) => new ModulePackageInstaller(Store.DirectoryPath).RestorePoints(id);
    internal async Task<ModuleInstallResult> RestoreModulePackageAsync(string id, string restorePoint)
    {
        if (_moduleFilesBusy) throw new InvalidOperationException("Another plugin operation is in progress.");
        var replacing = ExternalModules.FirstOrDefault(m => m.Manifest.Id == id) ?? throw new IOException("Plugin is no longer installed.");
        _moduleFilesBusy = true; QueueModuleStateRefresh();
        try
        {
            await _moduleDiskGate.WaitAsync();
            try
            {
                await Modules.StopForMaintenanceAsync(id);
                Settings.Modules.Approvals.Remove(id); RevokeModulePermissions(id);
                _save.Stop(); string text = Store.Serialize(Settings);
                _writing = _writing.ContinueWith(_ => Store.Write(text), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                await _writing;
                var result = await Task.Run(() => new ModulePackageInstaller(Store.DirectoryPath).Restore(restorePoint, replacing));
                ModuleFileProblem = result.Error; return result;
            }
            finally { _moduleDiskGate.Release(); }
        }
        finally { _moduleFilesBusy = false; _moduleConfiguration = ""; QueueModuleStateRefresh(); await ConfigureModulesAsync(refresh: true); }
    }
    private async Task UpdateModuleFileStateAsync()
    {
        var state = await Task.Run(() => (Pending: ModuleFiles.PendingRemovals, Suppressed: ModuleFiles.SuppressedBundles, Count: ModuleFiles.PendingCount));
        _bundledModuleIds ??= await Task.Run(() => new ModuleCatalog(Path.Combine(AppContext.BaseDirectory, "mods"), "", []).Discover().Select(m => m.Manifest.Id).ToHashSet(StringComparer.Ordinal));
        _moduleRemovalIds.Clear(); _moduleRemovalIds.UnionWith(state.Pending);
        PendingModuleCleanup = state.Count;
        HasRemovedBundles = state.Suppressed.Any(_bundledModuleIds.Contains);
    }
    internal IReadOnlyList<string> OrphanModuleData() => ModuleFiles.Problem is null ? ModuleFiles.Orphans() : [];
    internal async Task<ModuleFileResult> ClearOrphanDataAsync(string id)
    {
        ModuleIds.Require(id);
        await _moduleDiskGate.WaitAsync();
        try
        {
            if (!ModuleFiles.Orphans().Contains(id)) throw new IOException("Data is no longer unassociated. Refresh before clearing it.");
            await Task.Run(() => ModuleFiles.RequestRemoval(id, deleteData: true));
            var result = await Task.Run(() => ModuleCatalog.WithCacheLease(ModuleFiles.ProcessPending));
            await UpdateModuleFileStateAsync();
            ModuleFileProblem = result.Error ?? ModuleFiles.Problem ?? _moduleInstallProblem;
            return result;
        }
        finally { _moduleDiskGate.Release(); }
    }
    internal async Task<ModuleFileResult> ClearModuleCacheAsync(string id)
    {
        if (_moduleFilesBusy) throw new InvalidOperationException(BrimDeck.Core.Loc.T("正在处理另一个模组操作，请稍后再试。", "Another module operation is in progress. Try again shortly."));
        _moduleFilesBusy = true;
        QueueModuleStateRefresh();
        try
        {
            await _moduleDiskGate.WaitAsync();
            try
            {
                var result = await Task.Run(() => ModuleCatalog.WithCacheLease(paths => ModuleFiles.ClearCache(id, paths)));
                await UpdateModuleFileStateAsync(); ModuleFileProblem = result.Error; return result;
            }
            finally { _moduleDiskGate.Release(); }
        }
        finally { _moduleFilesBusy = false; _moduleConfiguration = ""; QueueModuleStateRefresh(); _ = ConfigureModulesAsync(); }
    }
    internal async Task<ModuleFileResult> UninstallModuleAsync(string id, bool deleteData = false)
    {
        if (_moduleFilesBusy) throw new InvalidOperationException(BrimDeck.Core.Loc.T("正在处理另一个模组操作，请稍后再试。", "Another module operation is in progress. Try again shortly."));
        var external = ExternalModules.FirstOrDefault(m => m.Manifest.Id == id) ?? throw new IOException("Module is no longer installed. Refresh the list.");
        _moduleFilesBusy = true; _moduleRemovalIds.Add(id);
        QueueModuleStateRefresh();
        try
        {
            await _moduleDiskGate.WaitAsync();
            try
            {
                await Modules.StopForMaintenanceAsync(id);
                await Task.Run(() => ModuleFiles.RequestRemoval(id, external, deleteData));
                await SaveModuleRemovalPreferencesAsync([id]);
                var result = await Task.Run(() => ModuleCatalog.WithCacheLease(ModuleFiles.ProcessPending));
                ExternalModules = ExternalModules.Where(m => m.Manifest.Id != id).ToArray();
                await UpdateModuleFileStateAsync(); ModuleFileProblem = result.Error; return result;
            }
            finally { _moduleDiskGate.Release(); }
        }
        finally { _moduleFilesBusy = false; _moduleConfiguration = ""; QueueModuleStateRefresh(); _ = ConfigureModulesAsync(refresh: true); }
    }
    internal async Task<ModuleInstallResult> InstallModuleFolderAsync(string source, string digest, DiscoveredModule? replacing)
    {
        if (_moduleFilesBusy) throw new InvalidOperationException("Another plugin operation is in progress.");
        _moduleFilesBusy = true; QueueModuleStateRefresh();
        try
        {
            await _moduleDiskGate.WaitAsync();
            try
            {
                if (replacing is not null)
                {
                    await Modules.StopForMaintenanceAsync(replacing.Manifest.Id);
                    Settings.Modules.Approvals.Remove(replacing.Manifest.Id); RevokeModulePermissions(replacing.Manifest.Id);
                    _save.Stop(); string preferences = Store.Serialize(Settings);
                    _writing = _writing.ContinueWith(_ => Store.Write(preferences), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                    await _writing;
                }
                var result = await Task.Run(() => new ModulePackageInstaller(Store.DirectoryPath).Install(source, digest, replacing));
                if (!result.Pending)
                {
                    Settings.Modules.Approvals.Remove(result.Id);
                    RevokeModulePermissions(result.Id);
                    _save.Stop(); string text = Store.Serialize(Settings);
                    _writing = _writing.ContinueWith(_ => Store.Write(text), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                    await _writing;
                }
                ModuleFileProblem = result.Error;
                return result;
            }
            finally { _moduleDiskGate.Release(); }
        }
        finally { _moduleFilesBusy = false; _moduleConfiguration = ""; QueueModuleStateRefresh(); await ConfigureModulesAsync(refresh: true); }
    }
    internal async Task ReloadModuleAsync(string id)
    {
        await Modules.StopForMaintenanceAsync(id); Modules.ResumeAfterMaintenance(id);
        _moduleConfiguration = ""; await ConfigureModulesAsync(refresh: true);
    }
    internal async Task RestoreBundledModulesAsync()
    {
        if (_moduleFilesBusy) throw new InvalidOperationException(BrimDeck.Core.Loc.T("正在处理另一个模组操作，请稍后再试。", "Another module operation is in progress. Try again shortly."));
        _moduleFilesBusy = true; QueueModuleStateRefresh();
        try
        {
            await _moduleDiskGate.WaitAsync();
            try
            {
                await Task.Run(ModuleFiles.RestoreBundles);
                ExternalModules = await DiscoverModuleFilesAsync(); _moduleCatalogScanned = true;
                await UpdateModuleFileStateAsync();
            }
            finally { _moduleDiskGate.Release(); }
        }
        finally { _moduleFilesBusy = false; _moduleConfiguration = ""; QueueModuleStateRefresh(); _ = ConfigureModulesAsync(); }
    }
}
