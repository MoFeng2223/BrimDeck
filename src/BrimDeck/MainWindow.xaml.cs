using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

public partial class MainWindow : Window
{
    private readonly App _app;
    private readonly DispatcherTimer _hover = new();
    private readonly DispatcherTimer _leave = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _alertTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly WindowsHost _host = new();
    // Animated in place so the indicator style can fade its surface in and out instead of switching brushes.
    private readonly SolidColorBrush _surface = new(Colors.Black);
    private const double CapsuleGap = 5;
    private VirtualDesktopPresence? _virtualDesktops;
    private bool _expanded, _refreshing, _preview, _refreshRequested, _pointerInside;
    private int _days = 1;
    private bool _details;
    private int _modelPage;
    private readonly Dictionary<ProviderId, int> _quotaPages = new();
    private string _antigravityGroup = "Gemini";
    private DateTimeOffset? _updatedAt;
    private ScreenContext _context;
    // Threshold levels seen per quota; a rise after the first reading briefly widens the compact island.
    private readonly Dictionary<string, int> _levels = new();
    private bool _levelsSeeded;
    private CompactAlertInfo? _alert;
    private DeckSettings Settings => _app.Settings;
    public bool IsExpanded => _expanded;
    public bool IsSuppressed => !_preview && Settings.Behavior(_context) == WindowBehavior.Hide;
    public FrameworkElement PanelVisual => Island;
    internal FrameworkElement SurfaceVisual => Stage;
    internal byte SurfaceAlpha => _surface.Color.A;
    internal VirtualDesktopPresence? VirtualDesktops => _virtualDesktops;
    internal bool AlertVisible => _alert is not null;
    public IReadOnlyList<ProviderSnapshot> Snapshots { get; private set; } = [];
    public string? LastRefreshError { get; private set; }

    private sealed record CompactAlertInfo(AppEntry Entry, string Name, string Label, double Used, DateTimeOffset? ResetAt);

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Island.Background = _surface;
        _hover.Tick += (_, _) => { _hover.Stop(); if (!IsSuppressed && _pointerInside) SetExpanded(true); };
        _leave.Tick += (_, _) => { _leave.Stop(); if (!_pointerInside && !_preview) SetExpanded(false); };
        _refresh.Tick += async (_, _) => await RefreshAsync();
        _clock.Tick += (_, _) => { UpdateClock(); if (_expanded) RenderUsage(); };
        _alertTimer.Tick += (_, _) => { _alertTimer.Stop(); _alert = null; RenderCompact(); if (!_expanded) SetExpanded(false); };
        _host.ContextChanged += context => { _context = context; if (!_preview) _expanded = false; ApplySettings(); };
        SourceInitialized += (_, _) => WindowsHost.ConfigureOverlay(this);
        Loaded += async (_, _) =>
        {
            _virtualDesktops ??= new VirtualDesktopPresence(this);
            _context = WindowsHost.DetectContext(); ApplySettings();
            if (!_app.SmokeMode) { _refresh.Start(); _clock.Start(); await RefreshAsync(); }
        };
        Closing += (_, e) => { if (!_app.Exiting) { e.Cancel = true; SetExpanded(false); } };
        Closed += (_, _) => { _virtualDesktops?.Dispose(); _host.Dispose(); _hover.Stop(); _leave.Stop(); _clock.Stop(); _refresh.Stop(); _alertTimer.Stop(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; SetExpanded(false); } };
        MouseRightButtonUp += (_, e) => { _app.OpenSettings(); e.Handled = true; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SetExpanded(false); e.Handled = true; } };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
    }

    private void DisplayChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(ApplySettings);
    private void UpdateClock()
    {
        var now = DateTime.Now.ToString("HH:mm");
        ClockLabel.Text = now; CompactTime.Text = now;
    }
    public void ApplySettings()
    {
        var (bounds, work, scale) = WindowsHost.PrimaryScreen();
        Width = Math.Max(240, Math.Min(Math.Max(Settings.Width, Settings.MinimumWidth), work.Width / scale - 24));
        Height = Math.Max(140, Math.Min(Settings.Height + 16, work.Height / scale - 16));
        Left = (bounds.Left + bounds.Width / 2) / scale - Width / 2;
        Top = bounds.Top / scale;
        UpdateClock();
        RenderCompact();
        if (IsSuppressed)
        {
            _hover.Stop(); _leave.Stop(); _pointerInside = false; SetExpanded(false, true); Hide();
        }
        else
        {
            if (!IsVisible) Show();
            SetExpanded(_expanded, true);
        }
        RenderUsage();
    }
    public void Preview(bool enabled, bool expanded = true)
    { _preview = enabled; ApplySettings(); SetExpanded(enabled && expanded, true); }
    public void TestContext(ScreenContext context) { _context = context; ApplySettings(); }
    public void SetSnapshots(IReadOnlyList<ProviderSnapshot> snapshots)
    {
        Snapshots = snapshots; _updatedAt = DateTimeOffset.Now;
        DetectThresholds();
        RenderCompact(); RenderUsage();
    }

    // The compact island shows one ring per enabled application: the highest used share among its quotas.
    internal void RenderCompact()
    {
        CompactRings.Children.Clear(); CompactAlert.Children.Clear();
        bool capsule = EffectiveStyle == CompactStyle.Capsule;
        CompactTime.Visibility = capsule && Settings.CompactSummary && _alert is null ? Visibility.Visible : Visibility.Collapsed;
        CompactRings.HorizontalAlignment = capsule ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        if (_alert is { } alert)
        {
            CompactAlert.Visibility = Visibility.Visible; CompactRings.Visibility = Visibility.Collapsed;
            var ring = UI.Ring(alert.Used, AppPresets.QuotaColor(alert.Entry, alert.Used), 14, 2.2); ring.Margin = new Thickness(0, 0, 8, 0); ring.VerticalAlignment = VerticalAlignment.Center;
            CompactAlert.Children.Add(ring);
            CompactAlert.Children.Add(UI.Fixed(alert.Name, 11.5, 16, UI.Primary, FontWeights.SemiBold));
            var text = UI.Fixed($"{alert.Label}已用 {alert.Used:0}%", 11.5, 16, UI.Secondary); text.Margin = new Thickness(8, 0, 0, 0); CompactAlert.Children.Add(text);
            var reset = UI.ShortReset(alert.ResetAt);
            if (reset.Length > 0) { var sub = UI.Fixed(reset + "重置", 11.5, 16, UI.Tertiary); sub.Margin = new Thickness(8, 0, 0, 0); CompactAlert.Children.Add(sub); }
            return;
        }
        CompactAlert.Visibility = Visibility.Collapsed; CompactRings.Visibility = Visibility.Visible;
        if (!Settings.CompactSummary) return;
        var apps = Settings.EnabledApps;
        for (int i = 0; i < apps.Count; i++)
        {
            var entry = apps[i];
            var snapshot = Snapshots.FirstOrDefault(x => x.Id == entry.Id);
            double used = snapshot is { Quotas.Count: > 0 } ? snapshot.Quotas.Max(q => Math.Clamp(q.UsedPercent, 0, 100)) : 0;
            // Rings identify applications, so they keep the theme color even past the warning thresholds.
            var ring = UI.Ring(used, entry.Theme, 12, 2);
            ring.Margin = new Thickness(0, 0, i < apps.Count - 1 ? 9 : 0, 0);
            ring.ToolTip = snapshot is { Quotas.Count: > 0 } ? $"{AppPresets.Name(entry.Id)} · 已用 {used:0.#}%" : $"{AppPresets.Name(entry.Id)} · {snapshot?.StatusLabel ?? "等待更新"}";
            CompactRings.Children.Add(ring);
        }
    }

    private void DetectThresholds()
    {
        CompactAlertInfo? risen = null;
        foreach (var entry in Settings.EnabledApps)
        {
            var snapshot = Snapshots.FirstOrDefault(x => x.Id == entry.Id);
            if (snapshot is null) continue;
            foreach (var quota in snapshot.Quotas)
            {
                var key = $"{entry.Id}:{quota.Label}";
                int level = AppPresets.Level(quota.UsedPercent);
                bool rose = _levelsSeeded && _levels.TryGetValue(key, out var previous) && level > previous;
                _levels[key] = level;
                if (rose && level > 0 && (risen is null || quota.UsedPercent > risen.Used))
                    risen = new CompactAlertInfo(entry, AppPresets.Name(entry.Id), UI.QuotaLabel(quota) + " ", Math.Clamp(quota.UsedPercent, 0, 100), quota.ResetAt);
            }
        }
        _levelsSeeded = true;
        if (risen is not null) ShowAlert(risen);
    }

    internal void ShowAlert(AppEntry entry, string label, double used, DateTimeOffset? resetAt)
        => ShowAlert(new CompactAlertInfo(entry, AppPresets.Name(entry.Id), label + " ", used, resetAt));
    internal void ClearAlert() { _alertTimer.Stop(); _alert = null; RenderCompact(); }
    private void ShowAlert(CompactAlertInfo alert)
    {
        _alert = alert; _alertTimer.Stop(); _alertTimer.Start();
        RenderCompact();
        if (!_expanded && !IsSuppressed) SetExpanded(false);
    }

    private CompactStyle EffectiveStyle => !_preview && Settings.Behavior(_context) == WindowBehavior.Line ? CompactStyle.Line : Settings.Style;
    private double CompactWidth(CompactStyle style) => style switch
    {
        CompactStyle.Line => 72,
        CompactStyle.Capsule => _alert is null ? 196 : 300,
        _ => _alert is null ? 238 : 324
    };
    public void SetExpanded(bool expanded, bool instant = false)
    {
        if (expanded && IsSuppressed) return;
        _expanded = expanded;
        var style = EffectiveStyle;
        double width = expanded ? Width : CompactWidth(style);
        double height = expanded ? Math.Min(Settings.Height, Height - 12) : style == CompactStyle.Line ? 10 : 32;
        // Every compact style opens into the same panel hanging from the screen edge.
        var radius = expanded ? new CornerRadius(0, 0, 24, 24) : style == CompactStyle.Notch ? new CornerRadius(0, 0, 12, 12) : new CornerRadius(18);
        double gap = !expanded && style == CompactStyle.Capsule ? CapsuleGap : 0;
        Compact.Margin = new Thickness(style == CompactStyle.Notch ? 30 : 16, 0, style == CompactStyle.Notch ? 30 : 16, 0);
        Island.BorderThickness = new Thickness(0);
        ExpandedContent.Width = Width;
        ExpandedContent.Height = Math.Min(Settings.Height, Height - 12);
        // Lay out in device-independent pixels. Column typography adapts to its available space.
        ContentCanvas.Width = ExpandedContent.Width;
        ContentCanvas.Height = ExpandedContent.Height;
        ExpandedContent.HorizontalAlignment = HorizontalAlignment.Center;
        ExpandedContent.VerticalAlignment = VerticalAlignment.Center;
        int duration = !instant && Settings.Animations && SystemParameters.ClientAreaAnimation ? Settings.AnimationDuration : 0;
        ExpandedContent.IsHitTestVisible = expanded;
        if (expanded) ExpandedContent.Visibility = Visibility.Visible;
        Compact.Visibility = style != CompactStyle.Line ? Visibility.Visible : Visibility.Hidden;
        LineVisual.Visibility = style == CompactStyle.Line ? Visibility.Visible : Visibility.Hidden;
        Animate(Compact, OpacityProperty, expanded ? 0 : 1, duration);
        // The indicator dissolves early while the surface grows in around it, and returns as the surface fades out.
        Animate(LineVisual, OpacityProperty, expanded ? 0 : 1, expanded ? duration * 2 / 5 : duration);
        Animate(Island, WidthProperty, width, duration);
        Animate(Island, HeightProperty, height, duration);
        Animate(HoverExtension, HeightProperty, gap, duration);
        Animate(Island, IslandBorder.ShoulderRadiusProperty, !expanded && style == CompactStyle.Notch ? 16 : 0, duration);
        Animate(Island, IslandBorder.MaterialOpacityProperty, style != CompactStyle.Line || expanded ? 1 : 0, duration);
        Animate(IslandShadow, OpacityProperty, !expanded && style != CompactStyle.Line ? 1 : 0, duration);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        Island.BeginAnimation(MarginProperty, new ThicknessAnimation { To = new Thickness(0, gap, 0, 0), Duration = TimeSpan.FromMilliseconds(duration), EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        var surface = new ColorAnimation { To = !expanded && style == CompactStyle.Line ? Color.FromArgb(1, 0, 0, 0) : Colors.Black, Duration = TimeSpan.FromMilliseconds(duration), EasingFunction = easing };
        _surface.BeginAnimation(SolidColorBrush.ColorProperty, surface, HandoffBehavior.SnapshotAndReplace);
        var corner = new CornerRadiusAnimation { To = radius, Duration = TimeSpan.FromMilliseconds(duration), EasingFunction = easing };
        Island.BeginAnimation(Border.CornerRadiusProperty, corner, HandoffBehavior.SnapshotAndReplace);
        var fade = new DoubleAnimation { To = expanded ? 1 : 0, Duration = TimeSpan.FromMilliseconds(duration) };
        fade.Completed += (_, _) => { if (!_expanded) ExpandedContent.Visibility = Visibility.Hidden; };
        ExpandedContent.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        if (expanded) RenderUsage();
    }
    private static void Animate(FrameworkElement target, DependencyProperty property, double value, int duration)
    {
        var animation = new DoubleAnimation { To = value, Duration = TimeSpan.FromMilliseconds(duration), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private void Island_MouseEnter(object sender, MouseEventArgs e)
        => PointerChanged(true);
    private void Island_MouseLeave(object sender, MouseEventArgs e)
        => PointerChanged(false);
    internal void PointerChanged(bool inside)
    {
        _pointerInside = inside;
        if (inside)
        {
            _leave.Stop(); if (_expanded || IsSuppressed) return;
            _hover.Stop(); _hover.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Settings.OpenDelay)); _hover.Start();
        }
        else
        {
            _hover.Stop(); if (!_expanded || _preview) return;
            _leave.Stop(); _leave.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Settings.CloseDelay)); _leave.Start();
        }
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => _app.OpenSettings();
    public async Task RefreshAsync()
    {
        if (_refreshing) { _refreshRequested = true; return; }
        _refreshing = true; LastRefreshError = null;
        try
        {
            var settings = Settings.Copy();
            var prices = _app.Prices.RefreshAsync(cancellation: _app.Lifetime.Token);
            var usage = _app.DemoMode ? Task.FromResult(DemoData.Create()) : Task.Run(() => _app.Usage.RefreshAsync(settings, _app.Lifetime.Token));
            await Task.WhenAll(prices, usage);
            var result = await usage;
            SetSnapshots(_app.DemoMode ? DemoData.Create() : result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastRefreshError = ex.GetType().Name + "\n" + ex.StackTrace; }
        finally
        {
            _refreshing = false; RenderUsage();
            if (_refreshRequested)
            {
                _refreshRequested = false; _ = RefreshAsync();
            }
        }
    }

}

public sealed class CornerRadiusAnimation : AnimationTimeline
{
    public CornerRadius To { get; set; }
    public IEasingFunction? EasingFunction { get; set; }
    public override Type TargetPropertyType => typeof(CornerRadius);
    protected override Freezable CreateInstanceCore() => new CornerRadiusAnimation { To = To, EasingFunction = EasingFunction };
    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var from = (CornerRadius)defaultOriginValue;
        var p = animationClock.CurrentProgress ?? 0;
        p = EasingFunction?.Ease(p) ?? p;
        double L(double a, double b) => a + (b - a) * p;
        return new CornerRadius(L(from.TopLeft, To.TopLeft), L(from.TopRight, To.TopRight), L(from.BottomRight, To.BottomRight), L(from.BottomLeft, To.BottomLeft));
    }
}

internal static class DemoData
{
    public static List<ProviderSnapshot> Create()
    {
        var now = DateTimeOffset.Now;
        return Enum.GetValues<ProviderId>().Select((id, i) => new ProviderSnapshot(id)
        {
            Plan = id == ProviderId.Claude ? "Max 5x" : id == ProviderId.Codex ? "prolite" : id == ProviderId.Cursor ? "Free" : "Pro", Status = "演示数据", Source = "内置示例", LiveQuota = false, QuotaTime = now, UsageAvailable = true, UsageNote = "示例数据",
            Quotas = id == ProviderId.Antigravity
                ? [new("Gemini · 5 小时", 2, now.AddHours(4), 300), new("Gemini · 每周", 16, now.AddDays(4), 10080), new("Claude · 5 小时", 35, now.AddHours(2), 300), new("Claude · 每周", 88, now.AddDays(3), 10080)]
                : [new(id == ProviderId.Cursor ? "Cursor 模型" : "5 小时额度", id == ProviderId.Claude ? 47 : 1 + i * 9, now.AddHours(2).AddMinutes(18 + i * 7), id == ProviderId.Cursor ? null : 300), new(id == ProviderId.Cursor ? "其他模型" : "每周额度", id == ProviderId.Claude ? 6 : 16 + i * 7, now.AddDays(3).AddHours(i), id == ProviderId.Cursor ? null : 10080)],
            Entries = Enumerable.Range(0, 30).Select(d => new TokenEntry($"demo:{id}:{d}", now.AddDays(-d), id == ProviderId.Claude ? "claude-sonnet-4-6" : "gpt-6-astra", 184000 + i * 10000, 740000, 18000, 0, 27000)).ToList()
        }).ToList();
    }
}
