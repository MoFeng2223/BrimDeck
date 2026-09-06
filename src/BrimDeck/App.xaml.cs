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
    public Pricing Prices { get; private set; } = null!;
    public MainWindow Deck { get; private set; } = null!;
    public CancellationTokenSource Lifetime { get; } = new();
    public bool Exiting { get; private set; }
    public bool DemoMode { get; set; }
    public bool SmokeMode { get; private set; }

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
        if (SmokeMode) folder = Path.Combine(Path.GetTempPath(), "BrimDeck-Smoke-" + Environment.ProcessId);
        Store = new SettingsStore(folder); Settings = Store.Load();
        Prices = new Pricing(folder);
        Usage = new UsageService(new DesktopSources());
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
    }
    public void UpdateSettings(DeckSettings settings)
    {
        bool dataChanged = settings.UsagePage != Settings.UsagePage ||
            !settings.EnabledApps.Select(app => app.Id).SequenceEqual(Settings.EnabledApps.Select(app => app.Id));
        settings.Normalize(); Settings = settings; Deck.ApplySettings();
        _save.Stop(); _save.Start();
        if (dataChanged && !SmokeMode) _ = Deck.RefreshAsync();
    }
    public void FlushSettings()
    {
        _save.Stop();
        try { Store.Save(Settings); _settingsWindow?.SaveStatus("已保存 · " + DateTime.Now.ToString("HH:mm:ss")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _settingsWindow?.SaveStatus("保存失败，请检查设置目录权限。", true); }
    }
    public void ExitApplication() { Exiting = true; Lifetime.Cancel(); FlushSettings(); Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        Exiting = true; Lifetime.Cancel(); _save.Stop(); _tray?.Dispose(); _listener?.Unregister(null); _settingsEvent?.Dispose();
        Prices?.Dispose(); _mutex?.Dispose(); base.OnExit(e);
    }
    private async Task RunSmokeAsync(string output, bool live)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        bool failed = false;
        try
        {
            DemoMode = !live;
            if (live) await Deck.RefreshAsync(); else Deck.SetSnapshots(DemoData.Create());
            var reportedEntry = new TokenEntry("reported", DateTimeOffset.Now, "unknown", 1, 0, 0, 0, 1, 12.3m);
            checks.Add(UI.Cost([reportedEntry], Prices) == "$12.30" ? "PASS reported cost has no approximation sign" : "FAIL reported cost label");
            var unknownEntry = reportedEntry with { Model = "brimdeck-test-unpriced-model", ReportedCostUsd = null };
            checks.Add(UI.Cost([unknownEntry], Prices) == "$0.00 +" && UI.Cost([reportedEntry, unknownEntry], Prices) == "$12.30 +"
                ? "PASS unknown costs show known subtotal followed by plus" : "FAIL incomplete cost label");
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
                var estimate = reportedEntry with { Model = "gpt-6-astra", ReportedCostUsd = null };
                checks.Add(Prices.Estimate(estimate) is { } amount && UI.Cost([estimate], Prices) == "$" + amount.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)
                    ? "PASS calculated cost displays its sum without approximation" : "FAIL calculated cost label");
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
            void RecordTypography(int providers)
            {
                Deck.PanelVisual.UpdateLayout();
                var title = Elements(Deck.PanelVisual).OfType<System.Windows.Controls.TextBlock>().Single(x => x.Text == "Codex");
                var body = Elements(Deck.PanelVisual).OfType<System.Windows.Controls.TextBlock>().First(x => x.Text == "今日");
                double VisibleSize(System.Windows.Controls.TextBlock text) => text.FontSize *
                    text.TransformToAncestor(Deck.PanelVisual).TransformBounds(new Rect(0, 0, 1, 1)).Height;
                checks.Add($"LAYOUT providers={providers}; title={VisibleSize(title):F2} DIP; body={VisibleSize(body):F2} DIP");
            }
            RecordTypography(4);
            System.Windows.Controls.Button ToolbarButton(string name) => Elements(Deck.PanelVisual).OfType<System.Windows.Controls.Button>()
                .Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == name);
            checks.Add(new[] { "AI 用量", "模型明细" }.All(name => ToolbarButton(name).Content is System.Windows.Controls.TextBlock && Bounds(ToolbarButton(name)).Height >= 16)
                ? "PASS view switchers are labeled text segments" : "FAIL view switcher presentation");
            var clock = Elements(Deck.PanelVisual).OfType<System.Windows.Controls.TextBlock>().Single(text => text.Name == "ClockLabel");
            var clockBounds = Bounds(clock);
            checks.Add(Math.Abs((clockBounds.Left + clockBounds.Right) / 2 - Deck.PanelVisual.ActualWidth / 2) < 1 && clock.Text.Length == 5
                ? "PASS toolbar clock is centered" : "FAIL toolbar clock placement");
            var settingsBounds = Bounds(ToolbarButton("设置"));
            checks.Add(settingsBounds.Height >= 30 && Deck.PanelVisual.ActualWidth - settingsBounds.Right >= 20 &&
                !Elements(Deck.PanelVisual).OfType<System.Windows.Controls.Button>().Any(button => System.Windows.Automation.AutomationProperties.GetName(button) == "刷新用量")
                ? "PASS settings has a generous target and edge inset without a refresh button" : "FAIL toolbar settings layout");
            System.Windows.Controls.Button GroupButton(string group) => Elements(Deck.PanelVisual).OfType<System.Windows.Controls.Button>()
                .Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Antigravity " + group);
            var options = Elements(Deck.PanelVisual).OfType<System.Windows.Controls.Button>()
                .Where(button => System.Windows.Automation.AutomationProperties.GetName(button).StartsWith("Antigravity ", StringComparison.Ordinal)).ToList();
            if (options.Count == 2)
            {
                var title = Elements(Deck.PanelVisual).OfType<System.Windows.Controls.TextBlock>().Single(x => x.Text == "Antigravity");
                var titleBounds = Bounds(title); var left = Bounds(options[0]); var right = Bounds(options[1]);
                checks.Add(ReferenceEquals(options[0].Parent, options[1].Parent) && Math.Abs(left.Right - right.Left) < .5 &&
                    left.Left > titleBounds.Right && Math.Abs((left.Top + left.Bottom - titleBounds.Top - titleBounds.Bottom) / 2) < 1.5
                    ? "PASS joined Antigravity selector shares the title row" : "FAIL Antigravity selector layout");
                checks.Add($"LAYOUT Antigravity selector labels: {string.Join("|", options.Select(button => ((System.Windows.Controls.TextBlock)button.Content).Text))}");
                GroupButton("Claude").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                checks.Add(GroupButton("Claude").Tag is "Selected" && GroupButton("Gemini").Tag is null
                    ? "PASS Antigravity segment changes selection" : "FAIL Antigravity segment interaction");
            }
            checks.Add(!HasScrollViewer(Deck.PanelVisual) && ContentFits() ? "PASS compact overview fits without scroll containers" : "FAIL overview layout");
            Deck.SelectQuotaGroup("Claude"); await Task.Delay(30); Capture(Deck.PanelVisual, Path.Combine(output, "panel-claude-group.png"));
            Deck.SelectQuotaGroup("Gemini");
            ToolbarButton("模型明细").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await Task.Delay(30); Capture(Deck.PanelVisual, Path.Combine(output, "panel-models.png"));
            checks.Add(!HasScrollViewer(Deck.PanelVisual) && ContentFits() ? "PASS model view fits without scrolling" : "FAIL model layout");
            ToolbarButton("AI 用量").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
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
                columnOrder.SequenceEqual(Settings.Apps.Select(app => AppPresets.Name(app.Id)))
                ? "PASS application rows reorder and the panel columns follow" : $"FAIL application reorder: {string.Join(",", columnOrder)}");
            _settingsWindow.MoveApp(2, 0); await Task.Delay(50);
            var themed = Settings.Copy(); themed.Apps.Single(app => app.Id == ProviderId.Codex).ThemeColor = "#FF0000"; UpdateSettings(themed); await Task.Delay(50);
            checks.Add(Elements(Deck.PanelVisual).OfType<System.Windows.Controls.Border>().Any(border => border.Background is SolidColorBrush { Color: { R: 255, G: 0, B: 0 } })
                ? "PASS custom theme color reaches the quota track" : "FAIL custom theme color");
            var recolored = Settings.Copy(); recolored.Apps.Single(app => app.Id == ProviderId.Codex).ThemeColor = ""; UpdateSettings(recolored); await Task.Delay(50);
            _settingsWindow.ShowPage(4); await Task.Delay(100); Capture(_settingsWindow.RootVisual, Path.Combine(output, "settings-pricing.png"));
            void CaptionClick(string name) => Elements(_settingsWindow!.RootVisual).OfType<System.Windows.Controls.Button>()
                .Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == name)
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            _settingsWindow.ShowPage(1);
            CaptionClick("最大化"); await Task.Delay(180);
            checks.Add(_settingsWindow.WindowState == WindowState.Maximized ? "PASS custom caption maximizes settings" : "FAIL settings maximize");
            Capture(_settingsWindow.RootVisual, Path.Combine(output, "settings-maximized.png"));
            CaptionClick("还原"); await Task.Delay(100);
            checks.Add(_settingsWindow.WindowState == WindowState.Normal ? "PASS custom caption restores settings" : "FAIL settings restore");
            var normalSettingsSize = new Size(_settingsWindow.Width, _settingsWindow.Height);
            _settingsWindow.Width = _settingsWindow.MinWidth; _settingsWindow.Height = _settingsWindow.MinHeight; await Task.Delay(100);
            Capture(_settingsWindow.RootVisual, Path.Combine(output, "settings-small.png"));
            _settingsWindow.Width = normalSettingsSize.Width; _settingsWindow.Height = normalSettingsSize.Height;
            CaptionClick("最小化"); await Task.Delay(100);
            checks.Add(_settingsWindow.WindowState == WindowState.Minimized ? "PASS custom caption minimizes settings" : "FAIL settings minimize");
            OpenSettings(); await Task.Delay(100);
            checks.Add(_settingsWindow.WindowState == WindowState.Normal && _settingsWindow.IsVisible ? "PASS settings entry restores minimized window" : "FAIL reopen minimized settings");
            CaptionClick("关闭设置"); await Task.Delay(50);
            checks.Add(_settingsWindow is null && !Exiting ? "PASS closing settings keeps application running" : "FAIL settings close");
            Deck.Preview(false);
            Deck.TestContext(ScreenContext.Desktop); Deck.SetExpanded(false, true);
            Deck.PointerChanged(true); await Task.Delay(60); checks.Add(!Deck.IsExpanded ? "PASS hover waits for configured delay" : "FAIL hover early");
            Deck.PointerChanged(false); await Task.Delay(240); checks.Add(!Deck.IsExpanded ? "PASS leaving cancels pending expansion" : "FAIL hover cancel");
            Deck.PointerChanged(true); await Task.Delay(280); checks.Add(Deck.IsExpanded ? "PASS hover expands after delay" : "FAIL hover expansion");
            Deck.PointerChanged(false); await Task.Delay(60); Deck.PointerChanged(true); await Task.Delay(340); checks.Add(Deck.IsExpanded ? "PASS reentry cancels collapse" : "FAIL reentry");
            Deck.PointerChanged(false); await Task.Delay(380); checks.Add(!Deck.IsExpanded ? "PASS leave collapses after delay" : "FAIL leave");
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
                }
                if (style == CompactStyle.Capsule)
                {
                    checks.Add(surfaceBounds.Top > 0 && Deck.SurfaceVisual.InputHitTest(new Point(surfaceBounds.Left + surfaceBounds.Width / 2, .5)) is not null &&
                        Deck.SurfaceVisual.InputHitTest(new Point(surfaceBounds.Left - 3, .5)) is null
                        ? "PASS capsule hover zone reaches the screen edge without widening" : "FAIL capsule hover zone");
                }
                if (style == CompactStyle.Notch)
                {
                    checks.Add(Deck.PanelVisual.InputHitTest(new Point(2, 16)) is null &&
                        Deck.PanelVisual.InputHitTest(new Point(20, 16)) is not null &&
                        Deck.PanelVisual.InputHitTest(new Point(12, .2)) is not null
                        ? "PASS notch shoulders respond to hover while transparent corners do not" : "FAIL notch pointer silhouette");
                    checks.Add(Elements(Deck.PanelVisual).OfType<System.Windows.Shapes.Path>().Count() == Settings.EnabledApps.Count
                        ? "PASS compact notch shows one progress ring per application" : "FAIL compact rings");
                    Deck.ShowAlert(Settings.Apps[0], "每周", 88, DateTimeOffset.Now.AddDays(3)); await Task.Delay(420);
                    Capture(Deck.SurfaceVisual, Path.Combine(output, "compact-notch-alert.png"), UI.Brush("#DDE7EF"),
                        new Rect(Deck.PanelVisual.TransformToAncestor(Deck.SurfaceVisual).Transform(new Point()).X, 0, Deck.PanelVisual.ActualWidth, Deck.PanelVisual.ActualHeight));
                    checks.Add(Deck.AlertVisible && Deck.PanelVisual.ActualWidth > 300 ? "PASS threshold alert widens the compact island" : $"FAIL alert width {Deck.PanelVisual.ActualWidth:F0}");
                    Deck.ClearAlert(); Deck.SetExpanded(false, true); await Task.Delay(50);
                }
                checks.Add("PASS compact " + style);
            }
            if (SystemParameters.ClientAreaAnimation)
            {
                var indicator = Settings.Copy(); indicator.Style = CompactStyle.Line; indicator.AnimationDuration = 400; UpdateSettings(indicator);
                Deck.TestContext(ScreenContext.Desktop); Deck.SetExpanded(false, true); await Task.Delay(50);
                Deck.SetExpanded(true); await Task.Delay(120);
                Capture(Deck.SurfaceVisual, Path.Combine(output, "expand-line-mid.png"), UI.Brush("#DDE7EF"));
                checks.Add(Deck.SurfaceAlpha is > 1 and < 250 && Deck.PanelVisual.ActualWidth > 72 && Deck.PanelVisual.ActualWidth < Deck.Width
                    ? "PASS indicator expansion fades the surface in while growing" : $"FAIL indicator expansion: alpha={Deck.SurfaceAlpha}; width={Deck.PanelVisual.ActualWidth:F0}");
                await Task.Delay(400);
                checks.Add(Deck.SurfaceAlpha == 255 && Math.Abs(Deck.Island.Margin.Top) < .01 && Deck.Island.CornerRadius.TopLeft == 0
                    ? "PASS every style expands into the same edge-attached panel" : "FAIL expanded shape");
                Deck.SetExpanded(false, true);
            }
            UpdateSettings(new DeckSettings());
            Deck.TestContext(ScreenContext.Maximized); checks.Add(Deck.IsVisible && !Deck.IsExpanded ? "PASS maximized remains available" : "FAIL maximized");
            Deck.TestContext(ScreenContext.Borderless); Deck.SetExpanded(true); checks.Add(!Deck.IsVisible && !Deck.IsExpanded ? "PASS borderless hidden and cannot expand" : "FAIL borderless");
            Deck.TestContext(ScreenContext.Exclusive); checks.Add(!Deck.IsVisible ? "PASS exclusive hidden" : "FAIL exclusive");
            var overrideSettings = Settings.Copy(); overrideSettings.Exclusive = WindowBehavior.Normal; UpdateSettings(overrideSettings); Deck.SetExpanded(true, true);
            checks.Add(Deck.IsVisible && Deck.IsExpanded ? "PASS fullscreen user override" : "FAIL override");
            Deck.TestContext(ScreenContext.Desktop);
            await Task.Delay(80);
            checks.Add(Deck.VirtualDesktops?.EnsurePinned() == true ? "PASS virtual desktop pin survives fullscreen hide and show" : "FAIL pin lost after fullscreen");
            var normalBounds = ContentBounds();
            var shortPanel = Settings.Copy(); shortPanel.Height = 140; UpdateSettings(shortPanel); Deck.Preview(true); await Task.Delay(80);
            var shortBounds = ContentBounds();
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-short.png"));
            checks.Add(ContentFills() && Math.Abs(shortBounds.Width - normalBounds.Width) < .5 && shortBounds.Height < normalBounds.Height
                ? "PASS reducing height retains full panel width" : "FAIL independent height resizing");
            var small = Settings.Copy(); small.Width = 440; small.Height = 140; UpdateSettings(small); Deck.Preview(true); await Task.Delay(100);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-small.png"));
            checks.Add(ContentFills() && !HasScrollViewer(Deck.PanelVisual) ? "PASS minimum size fills panel without scrolling" : "FAIL minimum size layout");
            var tall = Settings.Copy(); tall.Width = 440; tall.Height = 400; UpdateSettings(tall); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-tall.png"));
            checks.Add(ContentFills() && !HasScrollViewer(Deck.PanelVisual) ? "PASS narrow tall panel fills both axes without scrolling" : "FAIL tall layout");
            var wide = Settings.Copy(); wide.Width = 1200; wide.Height = 140; UpdateSettings(wide); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-wide.png"));
            checks.Add(ContentFills() && !HasScrollViewer(Deck.PanelVisual) ? "PASS wide short panel fills both axes without scrolling" : "FAIL wide layout");
            UpdateSettings(new DeckSettings());
            foreach (int count in new[] { 3, 2 })
            {
                var fewer = Settings.Copy(); fewer.SetEnabled(ProviderId.Claude, false); fewer.SetEnabled(ProviderId.Cursor, count == 3);
                UpdateSettings(fewer); await Task.Delay(50);
                Capture(Deck.PanelVisual, Path.Combine(output, $"panel-count-{count}.png"));
                RecordTypography(count);
            }
            var previousSize = Settings.Copy();
            UpdateSettings(new DeckSettings { Width = 700, Height = 180 }); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-compact-four.png"));
            UpdateSettings(new DeckSettings { Width = 700, Height = 180, Claude = false }); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-compact-three.png"));
            UpdateSettings(new DeckSettings { Width = 750, Height = 220 }); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-readable-four.png"));
            var fourBodySize = Elements(Deck.PanelVisual).OfType<System.Windows.Controls.TextBlock>().First(text => text.Text == "今日").FontSize;
            UpdateSettings(new DeckSettings { Width = 750, Height = 220, Antigravity = false, Cursor = false }); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-readable-two.png"));
            var twoBodySize = Elements(Deck.PanelVisual).OfType<System.Windows.Controls.TextBlock>().First(text => text.Text == "今日").FontSize;
            checks.Add(fourBodySize >= 10 && twoBodySize > fourBodySize && ContentFills()
                ? "PASS fewer providers gain larger readable text without shrinking the toolbar" : $"FAIL responsive text sizes: four={fourBodySize}; two={twoBodySize}");
            UpdateSettings(new DeckSettings { Width = 770, Height = 180, Antigravity = false, Cursor = false }); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-readable-two-short.png"));
            checks.Add(Elements(Deck.PanelVisual).OfType<System.Windows.Controls.TextBlock>().Single(text => text.Text == "Codex").FontSize >= 12 &&
                Elements(Deck.PanelVisual).OfType<System.Windows.Controls.TextBlock>().Any(text => text.Text == "今日") && Bounds(ToolbarButton("设置")).Height >= 30
                ? "PASS short two-provider panel retains legible text, totals and full-size settings" : "FAIL short panel readability");
            UpdateSettings(new DeckSettings { Width = 640, Height = 140 }); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-minimum-four.png"));
            checks.Add(ContentFills() && !HasScrollViewer(Deck.PanelVisual) && Elements(Deck.PanelVisual).OfType<System.Windows.Controls.TextBlock>().Count(text => text.Text == "5 小时") == 3
                ? "PASS four providers at the minimum size keep every quota visible" : "FAIL minimum four-provider layout");
            ToolbarButton("模型明细").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await Task.Delay(30);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-models-short.png"));
            ToolbarButton("AI 用量").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            UpdateSettings(previousSize);
            var single = Settings.Copy(); foreach (var id in Enum.GetValues<ProviderId>()) single.SetEnabled(id, id == ProviderId.Codex); UpdateSettings(single); await Task.Delay(50);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-single.png"));
            RecordTypography(1);
            checks.Add(ContentFits() && !HasScrollViewer(Deck.PanelVisual) ? "PASS one provider stays accessible" : "FAIL one provider layout");
            var disabled = Settings.Copy(); disabled.UsagePage = false; UpdateSettings(disabled); await Task.Delay(100); Capture(Deck.PanelVisual, Path.Combine(output, "panel-disabled.png"));
            checks.Add("PASS settings pages and size variants rendered");
            foreach (var snapshot in Deck.Snapshots)
                checks.Add($"DATA {snapshot.Name}: {snapshot.Status}; plan={snapshot.Plan}; planSource={snapshot.PlanSource}; quotas={snapshot.Quotas.Count}; tokens={snapshot.Entries.Sum(x => x.Total)}; records={snapshot.Entries.Count}; source={snapshot.Source}");
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
            failed = checks.Any(x => x.StartsWith("FAIL", StringComparison.Ordinal)) || Deck.LastRefreshError is not null;
        }
        catch (Exception ex) { failed = true; File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); }
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
