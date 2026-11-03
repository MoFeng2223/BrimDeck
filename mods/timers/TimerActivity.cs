using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Loc = BrimDeck.ModuleLocalization;
using UI = BrimDeck.ModuleUi;
using Soft = BrimDeck.ModuleMotion;
using PanelTheme = BrimDeck.ModuleTheme;

namespace BrimDeck.Timers;

internal sealed class TimerActivity(TimerWorkspace workspace) : IModuleActivity
{
    public ActivityState State => new(workspace.Timer.Active, workspace.Timer.Progress, workspace.Timer.Mode == TimerMode.Stopwatch, TimerVisuals.Accent(workspace.Timer), workspace.Completion);
    public event Action? Changed { add => workspace.Changed += value; remove => workspace.Changed -= value; }
    public IModuleView CreateView(ModuleViewContext context, ActivityViewKind kind) => new TimerActivityView(workspace, context, kind);
}

internal sealed class TimerActivityView : Grid, IModuleView, IModuleLayoutView
{
    private readonly TimerWorkspace _workspace;
    private readonly ActivityViewKind _kind;
    private readonly ModuleViewContext _context;
    private RollingDigitsView _number = null!;
    private TextBlock? _caption, _mark;
    private ModuleControlButton? _pause;
    private readonly ScaleTransform _fill = new(0, 1);
    private Border? _progress;
    private Grid? _row;
    private FrameworkElement? _icon;
    private string _playbackKey = "", _color = "";
    private double _scale = -1;
    public FrameworkElement Element => this;
    internal TimerActivityView(TimerWorkspace workspace, ModuleViewContext context, ActivityViewKind kind)
    {
        _workspace = workspace; _context = context; _kind = kind;
        if (kind == ActivityViewKind.Expanded)
        {
            var row = _row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(46) }); row.ColumnDefinitions.Add(new());
            _pause = new ModuleControlButton { Width = 46, Height = 46, Padding = new Thickness(0), AnimationsEnabled = Soft.Enabled };
            if (context.Owner.TryFindResource("ModuleControl") is Style style) _pause.Style = style;
            ToolTipService.SetIsEnabled(_pause, true);
            _pause.Click += (_, e) => { e.Handled = true; if (workspace.Timer.Running) workspace.Timer.Pause(); else workspace.Timer.Resume(); };
            row.Children.Add(_pause);
            var details = TimerVisuals.Button(context.Owner, "", () => context.OpenPage(TimerModule.PageId), Loc.T("打开计时设置与记录", "Open timer settings and laps"));
            details.Height = 54; details.Padding = new Thickness(2, 0, 0, 0); details.HorizontalAlignment = HorizontalAlignment.Right;
            var group = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _caption = TimerVisuals.Text("", 12, "PanelSecondary"); _caption.TextAlignment = TextAlignment.Right; _caption.Margin = new Thickness(0, 0, 8, 0);
            _number = new RollingDigitsView(30); group.Children.Add(_caption); group.Children.Add(_number); details.Content = group;
            SetColumn(details, 1); row.Children.Add(details); Children.Add(row);
        }
        else SetViewport(140, 24);
        workspace.Changed += Refresh; Refresh();
    }
    public void SetViewport(double width, double height, double scale = 1)
    {
        if (_kind == ActivityViewKind.Expanded) return;
        scale = Math.Clamp(scale, .75, 2);
        if (_row is null || Math.Abs(_scale - scale) > .01)
        {
            Children.Clear(); _scale = scale; _icon = null; _color = "";
            _row = new Grid { Height = 24 * scale };
            _mark = TimerVisuals.Text("Ⅱ", 16 * scale); _mark.HorizontalAlignment = HorizontalAlignment.Left;
            _number = new RollingDigitsView(14 * scale); _row.Children.Add(_mark); _row.Children.Add(_number);
            _progress = new Border { Height = Math.Max(1, 2 * scale), CornerRadius = new CornerRadius(1), RenderTransform = _fill, RenderTransformOrigin = new(0, .5) };
            var rail = new Border { Height = Math.Max(1, 2 * scale), VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(1), Child = _progress };
            rail.SetResourceReference(Border.BackgroundProperty, "PanelTrack"); _row.Children.Add(rail); Children.Add(_row);
        }
        _row.Width = Math.Max(1, width); Refresh();
    }
    public void SetViewport(ModuleViewport viewport)
    { if (_kind == ActivityViewKind.Expanded) { if (_row is not null) _row.Margin = new Thickness(viewport.HorizontalInset, 0, viewport.HorizontalInset, 0); } else SetViewport(viewport.Width, viewport.Height, viewport.ContentScale); }
    public void Refresh()
    {
        var timer = _workspace.Timer; bool done = Environment.TickCount64 < _workspace.CompletedUntil;
        string color = TimerVisuals.Accent(timer); _number.Foreground = UI.Brush(color);
        _number.MinimumTimeDigits = timer.Mode == TimerMode.Stopwatch ? 4 : TimerSession.Format(timer.DurationSeconds).Count(char.IsDigit);
        _number.Text = done && _kind == ActivityViewKind.Expanded ? "✓" : TimerVisuals.Time(timer);
        if (_caption is not null)
        {
            int round = timer.Phase == TimerPhase.Focus || timer.Status == TimerStatus.Waiting ? timer.CompletedFocus % timer.Rounds + 1 : Math.Max(1, (timer.CompletedFocus - 1) % timer.Rounds + 1);
            _caption.Text = done ? Loc.T("阶段完成", "Complete") : TimerVisuals.PhaseName(timer) +
                (timer.Status == TimerStatus.Paused ? Loc.T("\n已暂停", "\nPaused") : timer.Mode == TimerMode.Pomodoro ? $"\n{round} / {timer.Rounds}" : "");
        }
        if (_pause is not null)
        {
            string key = timer.Running + color;
            if (_playbackKey != key)
            {
                _playbackKey = key;
                _pause.Content = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = UI.Brush(timer.Phase is TimerPhase.ShortBreak or TimerPhase.LongBreak ? "#304FAF9D" : "#3DFF9F0A"), Child = TimerVisuals.Playback(timer.Running, color) };
            }
            _pause.AnimationsEnabled = Soft.Enabled; _pause.IsEnabled = timer.Status != TimerStatus.Completed;
            _pause.Background = UI.Brush(timer.Phase is TimerPhase.ShortBreak or TimerPhase.LongBreak ? "#304FAF9D" : "#3DFF9F0A");
            _pause.ToolTip = timer.Running ? Loc.T("暂停", "Pause") : Loc.T("继续", "Resume");
            System.Windows.Automation.AutomationProperties.SetName(_pause, (string)_pause.ToolTip);
        }
        if (_mark is not null && _row is not null)
        {
            bool marked = done || timer.Status == TimerStatus.Completed || !timer.Running;
            _mark.Text = done || timer.Status == TimerStatus.Completed ? "✓" : "Ⅱ"; _mark.Foreground = UI.Brush(color); _mark.Visibility = marked ? Visibility.Visible : Visibility.Hidden;
            if (_color != color)
            {
                _color = color; if (_icon is not null) _row.Children.Remove(_icon);
                _icon = TimerVisuals.Icon(color, 17 * _scale); _icon.HorizontalAlignment = HorizontalAlignment.Left; _row.Children.Add(_icon);
            }
            _icon!.Visibility = marked ? Visibility.Hidden : Visibility.Visible;
        }
        if (_progress is not null)
        { _progress.Background = UI.Brush(color); ModuleMotion.Animate(_fill, ScaleTransform.ScaleXProperty, done ? 1 : timer.Progress, Soft.Enabled && IsVisible, Soft.Quick); }
    }
    public void Dispose() { _workspace.Changed -= Refresh; Children.Clear(); }
}
