using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _settingsEvent;
    private RegisteredWaitHandle? _listener;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public DeckSettings Settings { get; private set; } = new();
    public SettingsStore Store { get; private set; } = null!;
    public UsageService Usage { get; private set; } = null!;
    public ProviderSecrets Secrets { get; private set; } = null!;
    public Pricing Prices { get; private set; } = null!;
    public AppUpdates Updates { get; internal set; } = null!;
    public MainWindow Deck { get; private set; } = null!;
    public CancellationTokenSource Lifetime { get; } = new();
    public bool Exiting { get; private set; }
    public bool DemoMode { get; set; }
    public bool SmokeMode { get; private set; }
    // Smoke runs look controls up by their Chinese names, so they keep Chinese whatever their settings say.
    // The language scenario clears this to switch languages.
    internal string? FixedLanguage { get; set; }

    // Tooltip delays are not inherited, so the defaults are replaced for every element before any window exists.
    // The system hover time on some machines is a full second; every tooltip here opens after the same short pause.
    static App()
    {
        System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(120));
        System.Windows.Controls.ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(400));
        System.Windows.Controls.ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(20000));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Resources[SystemParameters.VerticalScrollBarWidthKey] = 7.0;
        SmokeMode = e.Args.Contains("--smoke"); DemoMode = e.Args.Contains("--demo");
        var suffix = Environment.UserName;
        var eventName = "Local\\BrimDeck.Settings." + suffix;
        if (!SmokeMode)
        {
            _mutex = new Mutex(true, "Local\\BrimDeck.Instance." + suffix, out var first);
            if (!first)
            {
                try { using var existing = EventWaitHandle.OpenExisting(eventName); existing.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                Shutdown(); return;
            }
            SetCurrentProcessExplicitAppUserModelID("BrimDeck.Desktop");
        }
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck");
        if (SmokeMode) folder = Path.Combine(Path.GetTempPath(), $"BrimDeck-Smoke-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Store = new SettingsStore(folder); Settings = Store.Load();
        if (SmokeMode) FixedLanguage = Loc.Chinese;
        UseLanguage();
        // The language a first start takes from Windows is kept, even if the display language changes later.
        if (Store.IsFirstStart && !SmokeMode) FlushSettings();
        if (!SmokeMode)
        {
            try
            {
                Settings.LaunchAtStartup = StartupRegistration.IsEnabled();
                // When moving from the old EXE to an installed copy, keep an enabled startup entry at the new stable path.
                if (Settings.LaunchAtStartup) StartupRegistration.SetEnabled(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        Prices = new Pricing(folder);
        Updates = new AppUpdates(BrimDeck.Updates.GitHubUpdates.Create(folder), folder);
        Secrets = new ProviderSecrets(folder);
        Usage = new UsageService(new DesktopSources(), secrets: Secrets);
        Deck = new MainWindow(this); MainWindow = Deck; Deck.Show();
        _save.Tick += (_, _) => FlushSettings();
        if (!SmokeMode)
        {
            _tray = new TrayIcon(Deck, OpenSettings, ExitApplication);
            _settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            _listener = ThreadPool.RegisterWaitForSingleObject(_settingsEvent, (_, _) => Dispatcher.BeginInvoke(OpenSettings), null, Timeout.Infinite, false);
        }
        if (e.Args.Contains("--settings")) OpenSettings();
        if (SmokeMode)
        {
            var index = Array.IndexOf(e.Args, "--smoke");
            string output = index + 1 < e.Args.Length ? e.Args[index + 1] : Path.Combine(Path.GetTempPath(), "BrimDeck-QA");
            Dispatcher.BeginInvoke(async () => await RunSmokeAsync(Path.GetFullPath(output), e.Args.Contains("--live")));
        }
    }
    // Names used by XAML templates follow the language through these resources.
    private void UseLanguage()
    {
        Loc.Use(FixedLanguage ?? Settings.Language);
        Resources["PinSourceName"] = Loc.T("固定显示", "Pin");
        Resources["UnpinSourceName"] = Loc.T("取消固定", "Unpin");
    }
    public void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            if (Store.LoadWarning is { } warning) _settingsWindow.SaveStatus(warning, true);
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        if (!SmokeMode) _ = _settingsWindow.CheckUpdatesOnOpenAsync();
    }
    public void UpdateSettings(DeckSettings settings)
    {
        foreach (var previous in Settings.Apps)
        {
            var next = settings.Entry(previous.InstanceId);
            if (previous.SecretRevision != Guid.Empty && (next is null || next.QuotaSource != previous.QuotaSource))
            {
                try { Secrets.Delete(previous.InstanceId); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { _settingsWindow?.SaveStatus(Loc.T("密钥文件无法更新，请检查本机设置目录后重试。", "The key file could not be updated. Check the local settings folder and try again."), true); return; }
            }
        }
        bool dataChanged = !settings.RequiredProviders.SetEquals(Settings.RequiredProviders) ||
            !settings.EnabledApps.Select(e => e.ConfigurationKey).ToHashSet().SetEquals(Settings.EnabledApps.Select(e => e.ConfigurationKey));
        bool syncEnabled = settings.UsageAutoSync && !Settings.UsageAutoSync;
        settings.Normalize(); Settings = settings;
        // Statuses and notes are worded when the data is read, so a new language reads the data again.
        bool languageChanged = Loc.Normalize(FixedLanguage ?? settings.Language) != Loc.Language;
        if (languageChanged) UseLanguage();
        Deck.ApplySettings();
        _save.Stop(); _save.Start();
        if ((dataChanged || syncEnabled || languageChanged) && !SmokeMode) _ = Deck.RefreshAutomaticallyAsync(queueIfBusy: true);
    }
    public void FlushSettings()
    {
        _save.Stop();
        try { Store.Save(Settings); _settingsWindow?.SaveStatus(""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _settingsWindow?.SaveStatus(Loc.T("保存失败，请检查设置目录权限。", "Saving failed. Check the permissions of the settings folder."), true); }
    }
    // The settings window records its size before the final save; closing it during shutdown would be too late.
    public void ExitApplication() { Exiting = true; Lifetime.Cancel(); _settingsWindow?.RememberSize(); FlushSettings(); Shutdown(); }
    internal void InstallUpdate()
    {
        if (Updates.PrepareRestart(() => Store.Save(Settings))) ExitApplication();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        Exiting = true; Lifetime.Cancel(); _save.Stop(); _tray?.Dispose(); _listener?.Unregister(null); _settingsEvent?.Dispose();
        Prices?.Dispose(); _mutex?.Dispose(); base.OnExit(e);
    }
    private async Task RunSmokeAsync(string output, bool live)
    {
        Directory.CreateDirectory(output);
        // Scenarios invoke controls explicitly. Physical clicks during screenshots
        // must not change the selected page underneath an assertion.
        if (!Environment.GetCommandLineArgs().Contains("--music-hover") && !Environment.GetCommandLineArgs().Contains("--source-menu-interactive"))
            Deck.PreviewMouseDown += (_, args) => args.Handled = true;
        var checks = new List<string>();
        bool failed = false;
        try
        {
            DemoMode = !live;
            if (Environment.GetCommandLineArgs().Contains("--control-dialog"))
            {
                await VerifyControlDialogAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--source-menu"))
            {
                await VerifySourceMenuAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--music-marquee"))
            {
                await VerifyMusicMarqueeAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--quota-carousel"))
            {
                await VerifyQuotaCarouselAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--music-hover"))
            {
                await VerifyMusicHoverAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--music-resize"))
            {
                await VerifyMusicResizeAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--music-seek"))
            {
                await VerifyMusicSeekAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--music"))
            {
                await VerifyMusicAsync(output, checks, Environment.GetCommandLineArgs().Contains("--media-live"));
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (live) await Deck.RefreshAsync(); else Deck.SetSnapshots(DemoData.Create());
            if (Environment.GetCommandLineArgs().Contains("--language"))
            {
                await VerifyLanguageAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--preview-dismissal"))
            {
                await VerifyPreviewDismissalAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--hover-tracking"))
            {
                await VerifyHoverTrackingAsync(checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--page-switch"))
            {
                await VerifyPageSwitchFramesAsync(output, checks);
                await VerifyPageSwitchHoverAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--typography"))
            {
                await VerifyTypographyAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--settings-only"))
            {
                await VerifySettingsAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--tooltip-response"))
            {
                await VerifyTooltipResponseAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--providers"))
            {
                await VerifyProvidersAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--app-interactions"))
            {
                await VerifyAppInteractionsAsync(output, checks);
                File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
                failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal));
                return;
            }
            if (live)
            {
                if (new DesktopSources().ReadClaudeDesktop(DataLocations.Detect()).Credentials.Count > 0)
                {
                    var claude = Deck.Snapshots.SingleOrDefault(snapshot => snapshot.Id == ProviderId.Claude);
                    checks.Add(claude is { LiveQuota: true } && claude.Quotas.Any(quota => quota.Minutes == 300 && quota.ResetAt is not null) &&
                        claude.Quotas.Any(quota => quota.Minutes == 10080 && quota.ResetAt is not null)
                        ? "PASS Claude Desktop login reads five-hour and weekly quotas with reset times" : "FAIL Claude Desktop live quotas");
                }
                checks.Add(Prices.ModelCount > 0 && Prices.LastError is null ? $"PASS online price catalog: {Prices.ModelCount} models" : "FAIL online price catalog");
                if (Deck.Snapshots.Any(s => s.Id == ProviderId.Antigravity && s.LiveQuota))
                {
                    var desktop = new DesktopSources();
                    bool stable = true;
                    for (int i = 0; i < 5; i++) stable &= (await Task.Run(() => desktop.FindAntigravityAsync(Lifetime.Token))).Count > 0;
                    checks.Add(stable ? "PASS repeated Antigravity process discovery" : "FAIL repeated Antigravity process discovery");
                    var onlyAntigravity = Settings.Copy();
                    foreach (var id in Enum.GetValues<ProviderId>()) onlyAntigravity.SetEnabled(id, id == ProviderId.Antigravity);
                    var refreshed = (await Task.Run(() => Usage.RefreshAsync(onlyAntigravity, Lifetime.Token))).Single();
                    checks.Add(refreshed.LiveQuota && refreshed.Quotas.Count > 0 ? "PASS Antigravity quotas remain connected on refresh" : "FAIL Antigravity repeat quota refresh");
                }
            }
            if (Deck.LastRefreshError is { } error) File.WriteAllText(Path.Combine(output, "refresh-error.txt"), error);
            Deck.Preview(true); await Task.Delay(200);
            checks.Add(Deck.VirtualDesktops?.EnsurePinned() == true ? "PASS overlay is pinned to all virtual desktops" : $"FAIL virtual desktop pin: {Deck.VirtualDesktops?.LastError:X8}");
            Deck.VirtualDesktops?.Reconnect(); await Task.Delay(100);
            checks.Add(Deck.VirtualDesktops?.EnsurePinned() == true ? "PASS virtual desktop services reconnect without another overlay" : "FAIL virtual desktop reconnect");
            Capture(Deck.PanelVisual, Path.Combine(output, live ? "panel-live.png" : "panel-demo.png"));
            bool HasScrollViewer(DependencyObject element)
            {
                if (element is System.Windows.Controls.ScrollViewer) return true;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) if (HasScrollViewer(VisualTreeHelper.GetChild(element, i))) return true;
                return false;
            }
            Rect ContentBounds()
            {
                Deck.PanelVisual.UpdateLayout();
                return Deck.ScaledContent.TransformToAncestor(Deck.PanelVisual).TransformBounds(new Rect(Deck.ScaledContent.RenderSize));
            }
            bool ContentFits()
            {
                var rect = ContentBounds();
                return rect.Left >= -.5 && rect.Top >= -.5 && rect.Right <= Deck.PanelVisual.ActualWidth + .5 && rect.Bottom <= Deck.PanelVisual.ActualHeight + .5;
            }
            bool ContentFills()
            {
                var rect = ContentBounds();
                return Math.Abs(rect.Left) < .5 && Math.Abs(rect.Top) < .5 &&
                    Math.Abs(rect.Width - Deck.PanelVisual.ActualWidth) < .5 && Math.Abs(rect.Height - Deck.PanelVisual.ActualHeight) < .5;
            }
            IEnumerable<FrameworkElement> Elements(DependencyObject root)
            {
                if (root is FrameworkElement element) yield return element;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                    foreach (var child in Elements(VisualTreeHelper.GetChild(root, i))) yield return child;
            }
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(Deck.PanelVisual).TransformBounds(new Rect(element.RenderSize));
            System.Windows.Controls.Button ToolbarButton(string name) => Elements(Deck.PanelVisual).OfType<System.Windows.Controls.Button>()
                .Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == name);
            System.Windows.Controls.Button GroupButton(string group) => Elements(Deck.PanelVisual).OfType<System.Windows.Controls.Button>()
                .Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Antigravity " + group);
            var options = Elements(Deck.PanelVisual).OfType<System.Windows.Controls.Button>()
                .Where(button => System.Windows.Automation.AutomationProperties.GetName(button).StartsWith("Antigravity ", StringComparison.Ordinal)).ToList();
            if (options.Count == 2)
            {
                GroupButton("Claude").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                checks.Add(GroupButton("Claude").Tag is "Selected" && GroupButton("Gemini").Tag is null
                    ? "PASS Antigravity segment changes selection" : "FAIL Antigravity segment interaction");
            }
            checks.Add(!HasScrollViewer(Deck.PanelVisual) && ContentFits() ? "PASS compact overview fits without scroll containers" : "FAIL overview layout");
            Deck.SelectQuotaGroup("Claude"); await Task.Delay(30); Capture(Deck.PanelVisual, Path.Combine(output, "panel-claude-group.png"));
            Deck.SelectQuotaGroup("Gemini");
            ToolbarButton("查看 Claude 今日明细").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await Task.Delay(30); Capture(Deck.PanelVisual, Path.Combine(output, "panel-models.png"));
            ToolbarButton("返回用量").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await CheckModelDetailsAsync(output, checks);
            await CheckBackgroundRefreshAsync(checks);
            await CheckUsageSynchronizationAsync(output, checks);
            ToolbarButton("设置").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await Task.Delay(150);
            Capture(_settingsWindow!.RootVisual, Path.Combine(output, "settings-appearance.png"));
            _settingsWindow.ShowPage(1); await Task.Delay(180); Capture(_settingsWindow.RootVisual, Path.Combine(output, "settings-interaction.png"));
            _settingsWindow.ShowPage(2); await Task.Delay(100); Capture(_settingsWindow.RootVisual, Path.Combine(output, "settings-behavior.png"));
            _settingsWindow.ShowPage(3); await Task.Delay(100); Capture(_settingsWindow.RootVisual, Path.Combine(output, "settings-pages.png"));
            _settingsWindow.MoveApp(0, 2); await Task.Delay(100); Capture(_settingsWindow.RootVisual, Path.Combine(output, "settings-pages-reordered.png"));
            var names = Enum.GetValues<ProviderId>().Select(AppPresets.Name).ToList();
            var columnOrder = Elements(Deck.PanelVisual).OfType<System.Windows.Controls.TextBlock>().Where(text => names.Contains(text.Text) && text.FontSize >= 12)
                .OrderBy(text => Bounds(text).Left).Select(text => text.Text).ToList();
            checks.Add(Settings.Apps.Select(app => app.Id).SequenceEqual([ProviderId.Codex, ProviderId.Antigravity, ProviderId.Claude, ProviderId.Cursor]) &&
                columnOrder.SequenceEqual(Settings.ConfiguredApps.Select(app => AppPresets.Name(app.Id)))
                ? "PASS application rows reorder and the panel columns follow" : $"FAIL application reorder: {string.Join(",", columnOrder)}");
            _settingsWindow.MoveApp(2, 0); await Task.Delay(50);
            _settingsWindow.ShowPage(4); await Task.Delay(100); Capture(_settingsWindow.RootVisual, Path.Combine(output, "settings-pricing.png"));
            void CaptionClick(string name) => Elements(_settingsWindow!.RootVisual).OfType<System.Windows.Controls.Button>()
                .Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == name)
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            async Task WaitSettingsState(WindowState state)
            {
                var deadline = Environment.TickCount64 + 1500;
                while (_settingsWindow!.WindowState != state && Environment.TickCount64 < deadline) await Task.Delay(25);
                await Task.Delay(80); // Allow the native window transition to finish arranging its content.
            }
            _settingsWindow.ShowPage(1);
            CaptionClick("最大化"); await WaitSettingsState(WindowState.Maximized);
            checks.Add(_settingsWindow.WindowState == WindowState.Maximized ? "PASS custom caption maximizes settings" : "FAIL settings maximize");
            Capture(_settingsWindow.RootVisual, Path.Combine(output, "settings-maximized.png"));
            CaptionClick("还原"); await WaitSettingsState(WindowState.Normal);
            checks.Add(_settingsWindow.WindowState == WindowState.Normal ? "PASS custom caption restores settings" : "FAIL settings restore");
            var normalSettingsSize = new Size(_settingsWindow.Width, _settingsWindow.Height);
            _settingsWindow.Width = _settingsWindow.MinWidth; _settingsWindow.Height = _settingsWindow.MinHeight; await Task.Delay(100);
            Capture(_settingsWindow.RootVisual, Path.Combine(output, "settings-small.png"));
            _settingsWindow.Width = normalSettingsSize.Width; _settingsWindow.Height = normalSettingsSize.Height;
            CaptionClick("最小化"); await WaitSettingsState(WindowState.Minimized);
            checks.Add(_settingsWindow.WindowState == WindowState.Minimized ? "PASS custom caption minimizes settings" : "FAIL settings minimize");
            OpenSettings(); await WaitSettingsState(WindowState.Normal);
            checks.Add(_settingsWindow.WindowState == WindowState.Normal && _settingsWindow.IsVisible ? "PASS settings entry restores minimized window" : "FAIL reopen minimized settings");
            CaptionClick("关闭设置"); await Task.Delay(50);
            checks.Add(_settingsWindow is null && !Exiting ? "PASS closing settings keeps application running" : "FAIL settings close");
            // The size the window was closed at comes back on the next opening, and so does a maximized window.
            OpenSettings(); await Task.Delay(80); _settingsWindow!.Width = 900; _settingsWindow.Height = 700; await Task.Delay(50);
            CaptionClick("关闭设置"); await Task.Delay(50); OpenSettings(); await Task.Delay(80);
            checks.Add(Math.Abs(_settingsWindow!.ActualWidth - 900) < 1 && Math.Abs(_settingsWindow.ActualHeight - 700) < 1
                ? "PASS settings reopen at the size they were closed at" : $"FAIL settings size not kept: {_settingsWindow.ActualWidth}x{_settingsWindow.ActualHeight}");
            CaptionClick("最大化"); await WaitSettingsState(WindowState.Maximized); CaptionClick("关闭设置"); await Task.Delay(50);
            OpenSettings(); await WaitSettingsState(WindowState.Maximized);
            checks.Add(_settingsWindow!.WindowState == WindowState.Maximized && Settings.SettingsWindowWidth == 900
                ? "PASS maximized settings reopen maximized and keep the normal size" : "FAIL settings maximized state not kept");
            CaptionClick("关闭设置"); await Task.Delay(50);
            // A size saved on a larger screen is fitted to the work area of the screen the window opens on.
            var oversized = Settings.Copy(); oversized.SettingsWindowWidth = 20000; oversized.SettingsWindowHeight = 20000; oversized.SettingsWindowMaximized = false; UpdateSettings(oversized);
            OpenSettings(); await Task.Delay(80);
            var (_, settingsWork, settingsScale) = Native.WindowsHost.CursorScreen();
            checks.Add(_settingsWindow!.ActualWidth <= settingsWork.Width / settingsScale - 23 && _settingsWindow.ActualHeight <= settingsWork.Height / settingsScale - 23
                ? "PASS a saved settings size larger than the screen is fitted to the work area" : $"FAIL oversized settings: {_settingsWindow.ActualWidth}x{_settingsWindow.ActualHeight}");
            CaptionClick("关闭设置"); await Task.Delay(50);
            var defaultSize = Settings.Copy(); defaultSize.SettingsWindowWidth = null; defaultSize.SettingsWindowHeight = null; defaultSize.SettingsWindowMaximized = false; UpdateSettings(defaultSize);
            await VerifySettingsAsync(output, checks);
            await VerifyPreviewDismissalAsync(output, checks);
            await VerifySourcesAsync(output, checks);
            await VerifyProvidersAsync(output, checks);
            await VerifyAppInteractionsAsync(output, checks);
            await VerifyAppUpdatesAsync(output, checks);
            Deck.Preview(false);
            var hoverSettings = Settings.Copy(); hoverSettings.OpenDelay = 200; hoverSettings.CloseDelay = 300; UpdateSettings(hoverSettings);
            Deck.TestContext(ScreenContext.Desktop); Deck.SetExpanded(false, true);
            Deck.PointerChanged(true); await Task.Delay(60); checks.Add(!Deck.IsExpanded ? "PASS hover waits for configured delay" : "FAIL hover early");
            Deck.PointerChanged(false); await Task.Delay(240); checks.Add(!Deck.IsExpanded ? "PASS leaving cancels pending expansion" : "FAIL hover cancel");
            Deck.PointerChanged(true); await Task.Delay(280); checks.Add(Deck.IsExpanded ? "PASS hover expands after delay" : "FAIL hover expansion");
            Deck.PointerChanged(false); await Task.Delay(60); Deck.PointerChanged(true); await Task.Delay(340); checks.Add(Deck.IsExpanded ? "PASS reentry cancels collapse" : "FAIL reentry");
            Deck.PointerChanged(false); await Task.Delay(380); checks.Add(!Deck.IsExpanded ? "PASS leave collapses after delay" : "FAIL leave");
            await VerifyHoverTrackingAsync(checks);
            foreach (var style in Enum.GetValues<CompactStyle>())
            {
                var s = Settings.Copy(); s.Style = style; UpdateSettings(s); Deck.TestContext(ScreenContext.Desktop); Deck.SetExpanded(false, true); await Task.Delay(50);
                Capture(Deck.PanelVisual, Path.Combine(output, "compact-" + style.ToString().ToLowerInvariant() + ".png"));
                var surfaceBounds = Deck.PanelVisual.TransformToAncestor(Deck.SurfaceVisual).TransformBounds(new Rect(Deck.PanelVisual.RenderSize));
                var surfaceRegion = new Rect(surfaceBounds.Left, 0, surfaceBounds.Width, surfaceBounds.Bottom);
                if (style != CompactStyle.Line)
                {
                    Capture(Deck.SurfaceVisual, Path.Combine(output, $"compact-{style.ToString().ToLowerInvariant()}-light.png"), UI.Brush("#DDE7EF"), surfaceRegion);
                    Capture(Deck.SurfaceVisual, Path.Combine(output, $"compact-{style.ToString().ToLowerInvariant()}-dark.png"), UI.Brush("#18191D"), surfaceRegion);
                    checks.Add(Deck.SurfaceVisual.InputHitTest(new Point(surfaceBounds.Left + surfaceBounds.Width / 2, surfaceBounds.Bottom + 3)) is null
                        ? $"PASS {style} shadow does not intercept pointer input" : $"FAIL {style} shadow intercepts input");
                    // The whole bounding box from the screen edge hovers: shoulders, rounded corners and the capsule's gap.
                    var hoverZone = (UIElement)Deck.FindName("HoverZone");
                    Point[] hoverPoints =
                    [
                        new(surfaceBounds.Left + 1, 0.5), new(surfaceBounds.Right - 1, 0.5), new(surfaceBounds.Left + surfaceBounds.Width / 2, 0.5),
                        new(surfaceBounds.Left + 1, surfaceBounds.Bottom - 1), new(surfaceBounds.Right - 1, surfaceBounds.Bottom - 1),
                        new(surfaceBounds.Left + 1, surfaceBounds.Top + surfaceBounds.Height / 2), new(surfaceBounds.Right - 1, surfaceBounds.Top + surfaceBounds.Height / 2)
                    ];
                    var missed = hoverPoints.Where(point => Deck.SurfaceVisual.InputHitTest(point) is not DependencyObject hit || !hoverZone.IsAncestorOf(hit)).ToList();
                    checks.Add(missed.Count == 0 ? $"PASS {style} hovers across its full bounding box from the screen edge"
                        : $"FAIL {style} hover misses {string.Join(" ", missed.Select(point => $"({point.X:0.#},{point.Y:0.#})"))}");
                }
                if (style == CompactStyle.Notch)
                {
                    Deck.ShowAlert(Settings.Apps[0], "每周", 88, DateTimeOffset.Now.AddDays(3)); await Task.Delay(420);
                    Capture(Deck.SurfaceVisual, Path.Combine(output, "compact-notch-alert.png"), UI.Brush("#DDE7EF"),
                        new Rect(Deck.PanelVisual.TransformToAncestor(Deck.SurfaceVisual).Transform(new Point()).X, 0, Deck.PanelVisual.ActualWidth, Deck.PanelVisual.ActualHeight));
                    checks.Add(Deck.AlertVisible ? "PASS threshold alert appears on the compact island" : "FAIL threshold alert");
                    Deck.ClearAlert(); Deck.SetExpanded(false, true); await Task.Delay(50);
                }
            }
            if (SystemParameters.ClientAreaAnimation)
            {
                var indicator = Settings.Copy(); indicator.Style = CompactStyle.Line; indicator.AnimationDuration = 400; UpdateSettings(indicator);
                Deck.TestContext(ScreenContext.Desktop); Deck.SetExpanded(false, true); await Task.Delay(50);
                Deck.SetExpanded(true); await Task.Delay(70);
                Capture(Deck.SurfaceVisual, Path.Combine(output, "expand-line-mid.png"), UI.Brush("#DDE7EF"));
                await Task.Delay(400);
                var slow = Settings.Copy(); slow.Style = CompactStyle.Line; slow.AnimationDuration = 800; UpdateSettings(slow);
                Deck.TestContext(ScreenContext.Desktop); Deck.SetExpanded(true, true); await Task.Delay(50);
                Deck.SetExpanded(false); await Task.Delay(400);
                Capture(Deck.SurfaceVisual, Path.Combine(output, "collapse-line-mid.png"), UI.Brush("#DDE7EF"));
                await Task.Delay(500);
                Deck.SetExpanded(false, true);
            }
            var trayMenu = TrayIcon.CreateMenu(() => { }, () => { });
            trayMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint; trayMenu.HorizontalOffset = 200; trayMenu.VerticalOffset = 200;
            trayMenu.IsOpen = true; await Task.Delay(150); trayMenu.UpdateLayout();
            Capture(trayMenu, Path.Combine(output, "tray-menu.png"));
            trayMenu.IsOpen = false;
            UpdateSettings(new DeckSettings());
            Deck.TestContext(ScreenContext.Maximized); checks.Add(Deck.IsVisible && !Deck.IsExpanded ? "PASS maximized remains available" : "FAIL maximized");
            Deck.TestContext(ScreenContext.Borderless); Deck.SetExpanded(true); checks.Add(!Deck.IsVisible && !Deck.IsExpanded ? "PASS borderless hidden and cannot expand" : "FAIL borderless");
            Deck.TestContext(ScreenContext.Exclusive); checks.Add(!Deck.IsVisible ? "PASS exclusive hidden" : "FAIL exclusive");
            var overrideSettings = Settings.Copy(); overrideSettings.Exclusive = WindowBehavior.Normal; UpdateSettings(overrideSettings); Deck.SetExpanded(true, true);
            checks.Add(Deck.IsVisible && Deck.IsExpanded ? "PASS fullscreen user override" : "FAIL override");
            Deck.TestContext(ScreenContext.Desktop);
            await Task.Delay(80);
            checks.Add(Deck.VirtualDesktops?.EnsurePinned() == true ? "PASS virtual desktop pin survives fullscreen hide and show" : "FAIL pin lost after fullscreen");
            var shortPanel = Settings.Copy(); shortPanel.Height = 140; UpdateSettings(shortPanel); Deck.Preview(true); await Task.Delay(80);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-short.png"));
            var small = Settings.Copy(); small.Width = 440; small.Height = 140; UpdateSettings(small); Deck.Preview(true); await Task.Delay(100);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-small.png"));
            checks.Add(ContentFills() && !HasScrollViewer(Deck.PanelVisual) ? "PASS minimum size fills panel without scrolling" : "FAIL minimum size layout");
            var tall = Settings.Copy(); tall.Width = 440; tall.Height = 400; UpdateSettings(tall); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-tall.png"));
            var wide = Settings.Copy(); wide.Width = 1200; wide.Height = 140; UpdateSettings(wide); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-wide.png"));
            UpdateSettings(AllApps(new DeckSettings()));
            foreach (int count in new[] { 3, 2 })
            {
                var fewer = Settings.Copy(); fewer.SetEnabled(ProviderId.Claude, false); fewer.SetEnabled(ProviderId.Cursor, count == 3);
                UpdateSettings(fewer); await Task.Delay(50);
                Capture(Deck.PanelVisual, Path.Combine(output, $"panel-count-{count}.png"));
            }
            var previousSize = Settings.Copy();
            static DeckSettings Without(DeckSettings settings, params ProviderId[] ids) { foreach (var id in ids) settings.SetEnabled(id, false); return settings; }
            // New settings show two applications; the four-column scenarios switch on every built-in one.
            static DeckSettings AllApps(DeckSettings settings) { foreach (var app in settings.Apps) app.Enabled = true; return settings; }
            UpdateSettings(AllApps(new DeckSettings { Width = 700, Height = 180 })); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-compact-four.png"));
            UpdateSettings(Without(AllApps(new DeckSettings { Width = 700, Height = 180 }), ProviderId.Claude)); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-compact-three.png"));
            UpdateSettings(AllApps(new DeckSettings { Width = 760, Height = 240 })); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-readable-four.png"));
            UpdateSettings(Without(new DeckSettings { Width = 760, Height = 240 }, ProviderId.Antigravity, ProviderId.Cursor)); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-readable-two.png"));
            UpdateSettings(Without(new DeckSettings { Width = 770, Height = 180 }, ProviderId.Antigravity, ProviderId.Cursor)); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-readable-two-short.png"));
            UpdateSettings(AllApps(new DeckSettings { Width = 640, Height = 140 })); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-minimum-four.png"));
            foreach (var size in Enum.GetValues<PanelSize>())
            {
                var (presetWidth, presetHeight) = AllApps(new DeckSettings()).PresetSize(size);
                UpdateSettings(AllApps(new DeckSettings { Width = presetWidth, Height = presetHeight })); await Task.Delay(50);
                Capture(Deck.PanelVisual, Path.Combine(output, $"panel-size-{size.ToString().ToLowerInvariant()}.png"));
            }
            var twoStandard = Without(new DeckSettings(), ProviderId.Antigravity, ProviderId.Cursor); var (twoWidth, twoHeight) = twoStandard.PresetSize(PanelSize.Standard);
            twoStandard.Width = twoWidth; twoStandard.Height = twoHeight; UpdateSettings(twoStandard); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-size-standard-two.png"));
            ToolbarButton("查看 Claude 今日明细").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await Task.Delay(30);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-models-short.png"));
            ToolbarButton("返回用量").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            UpdateSettings(previousSize);
            var single = Settings.Copy(); foreach (var id in Enum.GetValues<ProviderId>()) single.SetEnabled(id, id == ProviderId.Codex); UpdateSettings(single); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-single.png"));
            var disabled = Settings.Copy(); disabled.UsagePage = false; UpdateSettings(disabled); await Task.Delay(100); Capture(Deck.PanelVisual, Path.Combine(output, "panel-disabled.png"));
            foreach (var snapshot in Deck.Snapshots)
                checks.Add($"DATA {snapshot.Name}: {snapshot.Status}; plan={snapshot.Plan}; planSource={snapshot.PlanSource}; quotas={snapshot.Quotas.Count}; tokens={snapshot.Entries.Sum(x => x.Total)}; records={snapshot.Entries.Count}; source={snapshot.Source}");
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
            failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal)) || Deck.LastRefreshError is not null;
        }
        catch (Exception ex) { failed = true; File.WriteAllLines(Path.Combine(output, "checks.txt"), checks); File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); }
        finally { Exiting = true; Lifetime.Cancel(); FlushSettings(); Shutdown(failed ? 1 : 0); }
    }
    private static void Capture(FrameworkElement element, string file, Brush? background = null, Rect? region = null)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        double padding = background is null ? 0 : 16;
        var source = region ?? new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var bounds = new Rect(0, 0, source.Width + padding * 2, source.Height + padding);
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(bounds.Width * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(bounds.Height * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            if (background is not null) context.DrawRectangle(background, null, bounds);
            var visual = new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(source.Left - padding, source.Top, bounds.Width, bounds.Height), Stretch = Stretch.Fill };
            context.DrawRectangle(visual, null, bounds);
        }
        image.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(file); encoder.Save(stream);
    }
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
}
