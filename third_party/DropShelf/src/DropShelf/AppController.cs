using System.Windows;
using System.Windows.Threading;
using DropShelf.Interop;
using DropShelf.Models;
using DropShelf.Services;
using DropShelf.Services.Gestures;
using DropShelf.Shelves;
using DropShelf.Views;

namespace DropShelf;

/// <summary>应用总控：持有设置并协调手势、置物架、托盘、快捷键和各个窗口。</summary>
public sealed class AppController : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly EmbeddedHostOptions? _host;
    private bool _enabled = true, _hotkeysPaused, _started;
    private IDisposable? _images;
    public bool Accepting { get => Shelves.Accepting; set { Shelves.Accepting = value; RefreshHostState(); } }
    public bool ShutdownPrepared { get; set; }
    private bool Automatic => _host?.AutomaticEnabled?.Invoke() != false;
    public async Task<bool> FlushAsync()
    {
        var snapshot = _settings.Clone();
        bool saved = await Task.Run(() => _settingsService.TrySave(snapshot));
        return await History.FlushAsync() && saved;
    }
    private readonly StartupService _startupService = new();
    private readonly Dispatcher _dispatcher;
    private AppSettings _settings;
    private MessageWindow? _messageWindow;
    private TrayIconService? _tray;
    private HotkeyService? _hotkey;
    private GestureService? _gestures;
    private ForegroundAppWatcher? _appWatcher;
    private ProbeWindow? _probe;
    private MainWindow? _mainWindow;
    private bool _showingMainWindow;
    private OnboardingWindow? _onboarding;
    private bool _hasShownTrayHint;
    private bool _exiting;
    private ScreenEdge _pendingEdge;

    public AppController(Dispatcher dispatcher, EmbeddedHostOptions? host = null)
    {
        _dispatcher = dispatcher; _host = host; _enabled = host?.Enabled ?? true;
        _settingsService = new SettingsService(host is null ? null : System.IO.Path.Combine(host.DataDirectory, "settings.json"));
        if (host is not null)
        {
            HostedResources.Configure(host.AnimationsEnabled);
            VirtualFileDropService.ConfigureCacheRoot(host.CacheDirectory);
            LogService.HostDirectory = System.IO.Path.Combine(host.DataDirectory, "Logs");
        }
        _settings = _settingsService.Load();
        if (host is not null && _settings.HostIntegrationVersion < 1)
        {
            // 保留升级前真正生效的复制/移动与空架规则，避免删除旧界面后意外移动源文件。
            var backup = _settingsService.SettingsPath + ".pre-host-integration.bak";
            if (System.IO.File.Exists(_settingsService.SettingsPath) && !System.IO.File.Exists(backup))
                System.IO.File.Copy(_settingsService.SettingsPath, backup);
            if (host.LegacyDragOutMode is { } mode) _settings.DragOutMode = mode;
            if (host.LegacyCloseWhenEmpty is { } close) _settings.CloseWhenEmpty = close;
            _settings.HostIntegrationVersion = 1;
            _settingsService.Save(_settings);
        }
        History = new ShelfHistoryService(System.IO.Path.Combine(_settingsService.DataDirectory, "history.json"));
        Shelves = new ShelfManager(() => _settings, ShowMainWindow, UpdateSettings, History, dispatcher);
        Current = this;
    }

    public static AppController? Current { get; private set; }

    /// <summary>开发调试时可通过环境变量使用独立实例，避免与已安装的版本冲突。</summary>
    public static string InstanceSuffix =>
        Environment.GetEnvironmentVariable("DROPSHELF_INSTANCE") is { Length: > 0 } suffix ? "." + suffix : string.Empty;

    public static string MessageWindowTitle => "DropShelfMessageWindow" + InstanceSuffix;

    /// <summary>命令行包含 --shelf 或直接带有文件路径（“发送到”菜单、拖到程序图标上）。</summary>
    public static bool IsShelfRequest(IReadOnlyList<string> arguments) =>
        arguments.Any(argument => string.Equals(argument, "--shelf", StringComparison.OrdinalIgnoreCase)) ||
        GetPathArguments(arguments).Count > 0;

    public static IReadOnlyList<string> GetPathArguments(IReadOnlyList<string> arguments) => arguments
        .Where(argument => !argument.StartsWith("--", StringComparison.Ordinal))
        .Where(argument => System.IO.File.Exists(argument) || System.IO.Directory.Exists(argument))
        .ToArray();

    public AppSettings Settings => _settings;
    public ShelfManager Shelves { get; }
    public ShelfHistoryService History { get; }
    public bool IsGestureRunning => _gestures?.IsRunning == true;
    public bool IsHotkeyRegistered { get; private set; }
    public string SettingsPath => _settingsService.SettingsPath;
    public bool IsEmbedded => _host is not null;
    internal bool TryReportUninstallFailure()
    {
        if (_host?.UninstallFailed is not { } report) return false;
        report(); return true;
    }
    public bool IsFloatingAvailable => _enabled && (_host?.FloatingEnabled?.Invoke() ?? true);
    public bool IsHotkeyConflict => _host?.HotkeyConflicts?.Invoke(_settings.HotkeyGesture) == true;
    public bool AreHotkeysPaused => _hotkeysPaused;
    public bool IsForegroundBlocked => _appWatcher?.IsBlocked == true;
    public event EventHandler? StatusChanged;

    /// <summary>位置规则约束所有展示入口；录制和屏蔽应用只暂停注册，不覆盖功能配置。</summary>
    public void RefreshHostState()
    {
        Shelves.ShowIsland = _host?.ShowIsland;
        Shelves.IsIslandBusy = _host?.IsIslandBusy;
        Shelves.PrewarmEnabled = Automatic && IsFloatingAvailable && Accepting;
        Shelves.SetFloatingEnabled(IsFloatingAvailable);
        HostedResources.AutomaticEnabled = Automatic;
        HostedResources.ThumbnailLimit = _host?.ThumbnailLimit;
        if (_started) ApplyAutomaticServices();
        _gestures?.SetPaused(!IsFloatingAvailable || !Accepting || IsForegroundBlocked);
        bool paused = !IsFloatingAvailable || !Accepting || _hotkeysPaused || IsForegroundBlocked || IsHotkeyConflict;
        _hotkey?.SetPaused(paused);
        IsHotkeyRegistered = !paused && _hotkey?.Register(_settings.HotkeyGesture) == true;
        if (!IsFloatingAvailable || !Accepting) _probe?.HideProbe();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>宿主开关只暂停入口，不丢弃已接收的内容，也不创建第二套进程或托盘。</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        RefreshHostState();
    }

    public void SetHotkeysPaused(bool paused)
    {
        _hotkeysPaused = paused;
        RefreshHostState();
    }

    public event EventHandler? SettingsChanged;

    public void Start(bool startInBackground)
    {
        if (_started || _exiting) return;
        _started = true; _images = ImagingThread.Acquire();
        LanguageService.SetMode(_host?.Language?.Invoke() ?? _settings.Language);
        if (_host is null) ThemeService.Initialize(_settings.Theme, _settings.AccentColor);
        LogService.CleanupOldLogs();
        _ = VirtualFileDropService.CleanupStaleCacheAsync();
        if (_host is null) RestoreStartupRegistration();

        _messageWindow = new MessageWindow(_host?.InstanceName ?? MessageWindowTitle);
        _messageWindow.ArgumentsReceived += HandleArguments;
        if (_host is null) _tray = new TrayIconService(_messageWindow,
            () => new TrayMenuState(_settings.HotkeyGesture.ToString(), History.Entries),
            ShowMainWindow, () => Shelves.OpenAtCursor(activate: true), entry => Shelves.Restore(entry), History.Clear, Exit);
        _hotkey = new HotkeyService(_messageWindow);
        _hotkey.Pressed += (_, _) =>
        {
            if (!_exiting && Accepting && IsFloatingAvailable && !_hotkeysPaused && !IsHotkeyConflict && _appWatcher?.CheckForeground() != true) Shelves.OpenAtCursor(activate: true);
        };
        RefreshHostState();

        ApplyAutomaticServices();

        var watcher = new ForegroundAppWatcher();
        watcher.BlockedChanged += (_, _) =>
        {
            RefreshHostState();
        };
        watcher.SetBlockedApps(_settings.BlockedApps!);
        _appWatcher = watcher;

        SetEnabled(_enabled);
        if (_enabled && Automatic && IsFloatingAvailable) Shelves.Prewarm();

        if (!startInBackground)
        {
            ShowMainWindow();
            if (!_settings.HasCompletedFirstRun) _dispatcher.BeginInvoke(ShowOnboarding, DispatcherPriority.Loaded);
        }
    }

    private void ApplyAutomaticServices()
    {
        if (!Automatic || !IsFloatingAvailable || !Accepting)
        {
            _gestures?.Dispose(); _gestures = null;
            _probe?.Close(); _probe = null;
            return;
        }
        if (_gestures is not null) return;
        _probe = new ProbeWindow();
        _probe.DragDetected += (_, _) =>
        {
            if (_exiting || !Accepting || !Automatic) return;
            Shelves.OpenAt(WindowPlacement.GetCursor(), openedByGesture: true, activate: false, _pendingEdge);
            if (_pendingEdge != ScreenEdge.None) _dispatcher.BeginInvoke(NativeMethods.NudgeMouse, DispatcherPriority.Background);
        };
        _gestures = new GestureService(_dispatcher, _settings.ShakeSensitivity);
        _gestures.SetTrigger(_settings.AutoOpenTrigger); _gestures.SetEdgeDwell(_settings.EdgeDwell);
        _gestures.ShakeDetected += OnShakeDetected; _gestures.EdgeDwellDetected += OnEdgeDwellDetected;
        _gestures.DragReleased += OnDragReleased; _gestures.Start();
    }

    /// <summary>打开置物架并放入命令行中的文件。</summary>
    public void HandleArguments(IReadOnlyList<string> arguments)
    {
        if (_exiting || !Accepting) return;
        if (!IsShelfRequest(arguments))
        {
            ShowMainWindow();
            return;
        }
        var shelf = Shelves.OpenAtCursor(activate: true);
        var paths = GetPathArguments(arguments);
        if (paths.Count == 0) return;
        var summary = shelf.ViewModel.AddFiles(paths);
        if (summary.Duplicates > 0 || summary.Unavailable > 0) shelf.ViewModel.Notify(summary.Message, summary.Added > 0);
    }

    /// <summary>修改设置并立即生效、自动保存。</summary>
    public void UpdateSettings(Action<AppSettings> change)
    {
        var previous = _settings.Clone();
        var next = _settings.Clone();
        change(next);
        _settings = SettingsService.Normalize(next);
        Apply(previous, _settings);
        _settingsService.Save(_settings);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>修改开机启动；写入注册表失败时保持原值。</summary>
    public bool SetStartWithWindows(bool enabled)
    {
        if (_host is not null)
        {
            if (!_host.SetStartWithWindows(enabled)) return false;
            UpdateSettings(settings => settings.StartWithWindows = _host.GetStartWithWindows());
            return true;
        }
        var result = _startupService.SetEnabled(enabled);
        if (!result.Succeeded)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
        UpdateSettings(settings => settings.StartWithWindows = enabled);
        return true;
    }

    public void SaveWindowBounds(Rect bounds)
    {
        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settingsService.Save(_settings);
    }

    public void ShowMainWindow()
    {
        if (_host?.ShowSettings is { } showSettings) { showSettings(); return; }
        // Show() 创建窗口时会处理消息，连续点击托盘图标可能在此期间再次进入；第二次 Show() 会抛出异常。
        if (_exiting || _showingMainWindow) return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(ShowMainWindow);
            return;
        }
        _showingMainWindow = true;
        try
        {
            if (_mainWindow is null)
            {
                _mainWindow = new MainWindow(this);
                _mainWindow.Closed += OnMainWindowClosed;
            }
            if (!_mainWindow.IsVisible)
            {
                try { _mainWindow.Show(); }
                catch
                {
                    // Show() 中途出错后窗口停在半初始化状态，再次 Show() 只会报“VisualTarget 的根 Visual 不能具有父级”，
                    // 主窗口从此打不开；丢弃这个窗口，下次重新创建。
                    var broken = _mainWindow;
                    _mainWindow = null;
                    broken.Closed -= OnMainWindowClosed;
                    try { broken.Close(); } catch (InvalidOperationException) { }
                    throw;
                }
            }
            if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
        finally { _showingMainWindow = false; }
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _mainWindow = null;
        if (_exiting) return;
        // 窗口占用的内存早已进入老一代，空闲时主动回收一次，否则长时间待在通知区域时不会释放。
        _dispatcher.BeginInvoke(() => GC.Collect(), DispatcherPriority.ApplicationIdle);
        if (_hasShownTrayHint) return;
        _hasShownTrayHint = true;
        _tray?.ShowStillRunningHint();
    }

    public void ShowOnboarding()
    {
        if (_host?.ShowHelp is { } showHelp) { showHelp(); return; }
        if (_exiting) return;
        if (_onboarding is { IsVisible: true })
        {
            _onboarding.Activate();
            return;
        }
        _onboarding = new OnboardingWindow();
        if (_mainWindow is { IsVisible: true }) _onboarding.Owner = _mainWindow;
        else _onboarding.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _onboarding.Closed += (_, _) =>
        {
            _onboarding = null;
            if (!_settings.HasCompletedFirstRun) UpdateSettings(settings => settings.HasCompletedFirstRun = true);
        };
        _onboarding.Show();
    }

    public ShelfWindow? OpenShelfNearMainWindow()
    {
        if (_exiting || !Accepting) return null;
        return (_host?.SettingsWindow?.Invoke() ?? _mainWindow) is { IsVisible: true } window
            ? Shelves.OpenNextTo(window, activate: true)
            : Shelves.OpenAtCursor(activate: true);
    }

    /// <summary>从设置窗口恢复最近关闭的置物架，在主窗口旁边打开。</summary>
    public void RestoreNearMainWindow(ShelfHistoryEntry entry)
    {
        if (_exiting || !Accepting) return;
        Shelves.Restore(entry, OpenShelfNearMainWindow());
    }

    /// <summary>把最近关闭的置物架拖出设置窗口后，在松手的位置打开。</summary>
    public void RestoreAt(ShelfHistoryEntry entry, POINT center)
    {
        if (_exiting || !Accepting) return;
        Shelves.Restore(entry, Shelves.OpenAt(center, openedByGesture: false, activate: true));
    }

    public void Exit()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(Exit);
            return;
        }
        if (_host is not null) { _host.Exit(); return; }
        PrepareForShutdown();
        System.Windows.Application.Current.Shutdown();
    }

    public void PrepareForShutdown()
    {
        if (_exiting) return;
        _exiting = true;
        if (!ShutdownPrepared) _settingsService.Save(_settings);
        _appWatcher?.Dispose();
        _gestures?.Dispose();
        _probe?.Close();
        Shelves.RecordOnClose = !ShutdownPrepared;
        Shelves.CloseAll();
        if (!ShutdownPrepared) History.Flush();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _onboarding?.Close();
        _mainWindow?.Close();
        _messageWindow?.Dispose();
        _images?.Dispose(); _images = null;
        DecodedImageCache.RemovePrefix("shelf:");
        if (_host is null) ThemeService.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    public void Dispose() => PrepareForShutdown();

    private void Apply(AppSettings previous, AppSettings current)
    {
        if (_host is null && (previous.Theme != current.Theme || previous.AccentColor != current.AccentColor))
            ThemeService.SetMode(current.Theme, current.AccentColor);
        if (_host is null && previous.Language != current.Language)
        {
            LanguageService.SetMode(current.Language);
            _tray?.RefreshText();
        }
        if (_gestures is not null)
        {
            _gestures.SetSensitivity(current.ShakeSensitivity);
            _gestures.SetTrigger(current.AutoOpenTrigger);
            _gestures.SetEdgeDwell(current.EdgeDwell);
        }
        RefreshHostState();
        if (!previous.BlockedApps!.SequenceEqual(current.BlockedApps!, StringComparer.OrdinalIgnoreCase))
            _appWatcher?.SetBlockedApps(current.BlockedApps!);
        if (ShelfAppearanceChanged(previous, current)) Shelves.ApplySettings(current);
    }

    /// <summary>尺寸、快速操作或拖出方式变化时，已打开的置物架需要立即更新。</summary>
    private static bool ShelfAppearanceChanged(AppSettings previous, AppSettings current) =>
        previous.QuickShelfSize != current.QuickShelfSize ||
        previous.DragOutMode != current.DragOutMode ||
        previous.QuickActionLayout != current.QuickActionLayout ||
        !previous.QuickActionSlots!.SequenceEqual(current.QuickActionSlots!) ||
        !System.Text.Json.JsonSerializer.Serialize(previous.CustomActions).Equals(System.Text.Json.JsonSerializer.Serialize(current.CustomActions));

    private void OnShakeDetected(object? sender, GestureTriggeredEventArgs e)
    {
        if (_exiting || !Accepting || !Automatic || !IsFloatingAvailable || Shelves.OpenShelves.Any(shelf => shelf.IsDraggingOut) || IsBlockedAt(e)) return;
        LogService.Trace($"识别到晃动 ({e.X}, {e.Y})，显示探针窗口");
        _pendingEdge = ScreenEdge.None;
        _probe?.ShowAt(new POINT(e.X, e.Y));
    }

    private void OnEdgeDwellDetected(object? sender, GestureTriggeredEventArgs e)
    {
        if (_exiting || !Accepting || !Automatic || !IsFloatingAvailable || Shelves.OpenShelves.Any(shelf => shelf.IsDraggingOut) || IsBlockedAt(e)) return;
        LogService.Trace($"在屏幕边缘停留 ({e.X}, {e.Y}, {e.Edge})，显示探针窗口");
        _pendingEdge = e.Edge;
        _probe?.ShowAt(new POINT(e.X, e.Y));
        _dispatcher.BeginInvoke(NativeMethods.NudgeMouse, DispatcherPriority.Background);
    }

    private bool IsBlockedAt(GestureTriggeredEventArgs e)
    {
        if (_appWatcher?.IsBlockedAt(new POINT(e.X, e.Y)) != true) return false;
        LogService.Trace($"识别到手势 ({e.X}, {e.Y})，但前台或光标下是屏蔽的应用，不弹出置物架");
        return true;
    }

    private void OnDragReleased(object? sender, EventArgs e)
    {
        LogService.Trace("晃动后松开鼠标");
        _probe?.HideProbe();
        Shelves.OnDragReleased();
    }

    private void RestoreStartupRegistration()
    {
        if (!_settings.StartWithWindows) return;
        var result = _startupService.SetEnabled(true);
        if (result.Succeeded) return;
        _settings.StartWithWindows = false;
        _settingsService.Save(_settings);
    }
}
