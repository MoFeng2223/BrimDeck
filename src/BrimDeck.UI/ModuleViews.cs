using System.Windows;
using System.Windows.Controls;
using BrimDeck.Modules;

namespace BrimDeck;

public sealed partial class ModuleViewContext(FrameworkElement owner, Action<string> openPage, Action<string> openSettings, Action<string> showActivity) : IDisposable
{
    private readonly Dictionary<long, IDisposable> _owned = [];
    private long _next;
    private bool _ended;
    private bool _attached, _ready;
    private readonly List<Action> _pendingStart = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    public FrameworkElement Owner { get; private set; } = owner;
    internal void Attach(FrameworkElement localOwner, string moduleId)
    {
        ObjectDisposedException.ThrowIf(_ended || _lifetime.IsCancellationRequested, this);
        if (_attached) throw new InvalidOperationException("A view context belongs to one mount.");
        _attached = true;
        Owner = localOwner;
        Action<string> Guard(Action<string> action) => target =>
        {
            ObjectDisposedException.ThrowIf(_ended || _lifetime.IsCancellationRequested, this);
            if (!Owner.Dispatcher.CheckAccess()) throw new ModuleApiException("ui-thread-required", "View navigation must run on the UI thread.");
            if (Appearance.Preview) throw new ModuleApiException("preview-navigation-disabled", "Preview views cannot request host navigation.");
            ArgumentNullException.ThrowIfNull(target);
            if (target != moduleId && !target.StartsWith(moduleId + ":", StringComparison.Ordinal))
                throw new ModuleApiException("navigation-owner-mismatch", "A view can navigate only to its own module contributions.");
            action(target);
        };
        OpenPage = Guard(OpenPage); OpenSettings = Guard(OpenSettings); ShowActivity = Guard(ShowActivity);
    }
    public Action<string> OpenPage { get; private set; } = openPage;
    public Action<string> OpenSettings { get; private set; } = openSettings;
    public Action<string> ShowActivity { get; private set; } = showActivity;
    public CancellationToken Lifetime => _lifetime.Token;
    internal Action<Exception>? Fault { get; set; }
    internal Action<Action>? RunCallback { get; set; }
    internal void BindLifetime(CancellationToken parent) => Own(parent.Register(() => { try { _lifetime.Cancel(); } catch (Exception error) { Fault?.Invoke(error); } }));
    internal void CompleteMount()
    {
        Action[] pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_ended || _lifetime.IsCancellationRequested, this);
            _ready = true; pending = _pendingStart.ToArray(); _pendingStart.Clear();
        }
        foreach (var start in pending) start();
    }
    public IDisposable Own(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (_gate)
        {
            if (!_ended && !_lifetime.IsCancellationRequested)
            {
                long id = ++_next; _owned.Add(id, resource);
                return new ModuleAction(() => { IDisposable? value; lock (_gate) _owned.Remove(id, out value); if (value is not null) Clean(value); });
            }
        }
        Clean(resource); return new ModuleAction(() => { });
    }
    public IDisposable OnDispose(Action cleanup) => Own(new ModuleAction(cleanup));
    public IDisposable Every(TimeSpan interval, Action callback)
    {
        ObjectDisposedException.ThrowIf(_ended || _lifetime.IsCancellationRequested, this);
        if (interval < TimeSpan.FromMilliseconds(16) || interval > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(interval));
        ArgumentNullException.ThrowIfNull(callback);
        bool stopped = false;
        var timer = new PromptTimer(Owner.Dispatcher, () => { if (_ended || _lifetime.IsCancellationRequested || _attached && !_ready) return; try { if (RunCallback is { } run) run(callback); else callback(); } catch (Exception ex) { Fault?.Invoke(ex); } }) { Interval = interval };
        var handle = Own(new ModuleAction(() => { stopped = true; timer.Stop(); }));
        lock (_gate) { if (_attached && !_ready) _pendingStart.Add(() => { if (!stopped && !_ended && !_lifetime.IsCancellationRequested) timer.Start(); }); else if (!stopped) timer.Start(); }
        return handle;
    }
    private void Clean(IDisposable item) { try { item.Dispose(); } catch (Exception ex) { Fault?.Invoke(ex); } }
    public void Dispose()
    {
        IDisposable[] resources;
        lock (_gate) { if (_ended) return; _ended = true; resources = _owned.Reverse().Select(p => p.Value).ToArray(); _owned.Clear(); _pendingStart.Clear(); }
        EndVisibility();
        try { _lifetime.Cancel(); } catch (Exception ex) { Fault?.Invoke(ex); }
        foreach (var resource in resources) Clean(resource);
    }
}
public interface IModuleView : IDisposable
{
    FrameworkElement Element { get; }
    void SetViewport(double width, double height, double scale = 1);
    void Refresh();
    bool IsAdjusting => false;
    bool CancelAdjustment() => false;
}
public sealed record ModuleViewport(double Width, double Height)
{
    public double DeviceScale { get; init; } = 1;
    public double ContentScale { get; init; } = 1;
    public double HorizontalInset { get; init; }
    public double CornerRadius { get; init; }
    public ModuleViewport Normalize()
    {
        double width = double.IsFinite(Width) ? Math.Clamp(Width, 1, 32768) : 1, height = double.IsFinite(Height) ? Math.Clamp(Height, 1, 32768) : 1;
        static double Scale(double value) => double.IsFinite(value) ? Math.Clamp(value, .25, 8) : 1;
        return this with { Width = width, Height = height, DeviceScale = Scale(DeviceScale), ContentScale = Scale(ContentScale),
            HorizontalInset = double.IsFinite(HorizontalInset) ? Math.Clamp(HorizontalInset, 0, width / 2) : 0,
            CornerRadius = double.IsFinite(CornerRadius) ? Math.Clamp(CornerRadius, 0, Math.Min(width, height) / 2) : 0 };
    }
}
public interface IModuleLayoutView { void SetViewport(ModuleViewport viewport); }
public sealed record ModulePageSize(double Width = 480, double Height = 266, double MinWidth = 440, double MaxWidth = 1200, double MinHeight = 140, double MaxHeight = 400);
public sealed record ModulePage(string Title, string TitleEn, Func<ModuleViewContext, IModuleView> CreateView, int Order = 500)
{
    public string? IconPath { get; init; }
    public ModulePageSize? Size { get; init; }
    public bool SupportsLocalTheme { get; init; }
    public Func<ModuleViewContext, ValueTask<IModuleView>>? CreateViewAsync { get; init; }
    public static ModulePage Async(string title, string titleEn, Func<ModuleViewContext, ValueTask<IModuleView>> create, int order = 500)
    {
        ArgumentNullException.ThrowIfNull(create);
        return new(title, titleEn, _ => throw new ModuleApiException("async-view-required", "This page requires asynchronous mounting."), order) { CreateViewAsync = create };
    }
}
public enum ActivityViewKind { Compact, Expanded }
public sealed record ActivityState(bool Active, double Progress, bool Indeterminate, string Accent, int Completion = 0);
public interface IModuleActivity
{
    ActivityState State { get; }
    event Action? Changed;
    IModuleView CreateView(ModuleViewContext context, ActivityViewKind kind);
}
public interface IModuleAsyncActivity : IModuleActivity
{
    ValueTask<IModuleView> CreateViewAsync(ModuleViewContext context, ActivityViewKind kind);
    IModuleView IModuleActivity.CreateView(ModuleViewContext context, ActivityViewKind kind)
        => throw new ModuleApiException("async-view-required", "This activity requires asynchronous mounting.");
}
public sealed record ModuleWidget(string Name, string NameEn, string SettingsId, Func<bool> Enabled, Action<bool> SetEnabled, int Order = 500)
{
    public Func<int>? ReadOrder { get; init; }
    public Action<int>? MoveTo { get; init; }
    public string? IconPath { get; init; }
}
public enum ModuleSettingKind { Number, Toggle, Choice, Text, Color }
public sealed record ModuleSettingOption(int Value, string Label, string LabelEn);
public sealed record ModuleSetting(string Key, string Label, string LabelEn, ModuleSettingKind Kind, double Minimum = 0, double Maximum = 1, string Unit = "", string UnitEn = "")
{
    public IReadOnlyList<ModuleSettingOption> Options { get; init; } = [];
    public object? DefaultValue { get; init; }
    public double Step { get; init; } = 1;
    public int MaximumLength { get; init; } = 256;
    public string Group { get; init; } = "";
    public string GroupEn { get; init; } = "";
    public string Description { get; init; } = "";
    public string DescriptionEn { get; init; } = "";
    public string Placeholder { get; init; } = "";
    public string PlaceholderEn { get; init; } = "";
}
public interface IModuleSettings
{
    string Name { get; }
    string NameEn { get; }
    IReadOnlyList<ModuleSetting> Fields { get; }
    object Read(string key);
    void Write(string key, object value);
    void Reset();
    bool Visible { get; }
}

public enum ModuleSettingsPlacement { Automatic, Modules, MiniApps }

// Presentation metadata is independent of widget registration and business settings.
public interface IModuleSettingsNavigation : IModuleSettings
{
    ModuleSettingsPlacement Placement => ModuleSettingsPlacement.Automatic;
    string? IconPath => null;
    int Order => 500;
}

// Public entry points to the same controls and motion used by built-in pages.
public static partial class ModuleUi
{
    public static bool AnimationsEnabled => Soft.Enabled;
    public static bool Dark => PanelTheme.Current.IsDark;
    public static string Primary => UI.Primary;
    public static string Accent => UI.Accent;
    public static TextBlock Text(string text, double size = 13)
    {
        var result = UI.Centered(text, size, UI.Primary); result.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary"); return result;
    }
    public static void Appear(UIElement element) => PageHandover.Appear(element);
}
