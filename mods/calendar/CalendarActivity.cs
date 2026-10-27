using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Loc = BrimDeck.ModuleLocalization;

namespace BrimDeck.Calendar;

internal sealed class CalendarActivity(CalendarWorkspace workspace) : IModuleActivity
{
    public ActivityState State
    {
        get
        {
            var focus = workspace.Focus();
            double progress = 0;
            if (focus.Running is { } running)
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                var start = running.StartAt(today);
                double total = Math.Max(1, (running.EndAt(today) - start).TotalMinutes);
                progress = Math.Clamp((DateTime.Now - start).TotalMinutes / total, 0, 1);
            }
            var accent = CalendarVisuals.Color((focus.Running ?? focus.Upcoming)?.Category ?? EventCategory.Work);
            return new ActivityState(focus.Active, progress, false, accent);
        }
    }

    public event Action? Changed
    {
        add => workspace.Changed += value;
        remove => workspace.Changed -= value;
    }

    public IModuleView CreateView(ModuleViewContext context, ActivityViewKind kind) =>
        kind == ActivityViewKind.Expanded
            ? new CalendarExpanded(workspace, context)
            : new CalendarCompact(workspace, context);
}

// The collapsed strip sits next to the other island indicators: only the distance to the next event
// today, or "now" while one is running. Nothing to show means the strip reports itself inactive.
internal sealed class CalendarCompact : Grid, IModuleView
{
    private readonly CalendarWorkspace _workspace;
    private readonly Border _dot;
    private readonly TextBlock _text;

    internal CalendarCompact(CalendarWorkspace workspace, ModuleViewContext context)
    {
        _workspace = workspace;
        _dot = new Border
        {
            Width = 4, Height = 4, CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0)
        };
        _text = CalendarVisuals.Label("", 11, "PanelPrimary");
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        row.Children.Add(_dot);
        row.Children.Add(_text);
        Children.Add(row);
        workspace.Changed += Refresh;
        Refresh();
    }

    public FrameworkElement Element => this;

    public void SetViewport(double width, double height, double scale = 1)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
    }

    public void Refresh()
    {
        var focus = _workspace.Focus();
        if (focus.Running is { } running)
        {
            _text.Text = Loc.T("进行中", "Now");
            _dot.Background = ModuleUi.Brush(CalendarVisuals.Color(running.Category));
        }
        else if (focus.Upcoming is { } next)
        {
            _text.Text = CalendarVisuals.Span(focus.Until);
            _dot.Background = ModuleUi.Brush(CalendarVisuals.Color(next.Category));
        }
        else if (focus.AllDayToday)
        {
            _text.Text = Loc.T("全天", "All day");
            _dot.Background = ModuleUi.Brush(ModuleUi.Accent);
        }
        else
        {
            _text.Text = "";
            _dot.Background = Brushes.Transparent;
        }
    }

    public void Dispose()
    {
        _workspace.Changed -= Refresh;
        Children.Clear();
    }
}

internal sealed class CalendarExpanded : Grid, IModuleView, IModuleLayoutView
{
    private readonly CalendarWorkspace _workspace;
    private readonly TextBlock _number, _week, _note, _next;

    internal CalendarExpanded(CalendarWorkspace workspace, ModuleViewContext context)
    {
        _workspace = workspace;
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _number = CalendarVisuals.Label("", 22, "PanelPrimary", FontWeights.SemiBold);
        _week = CalendarVisuals.Label("", 9, "PanelSecondary");
        left.Children.Add(_number);
        left.Children.Add(_week);
        Children.Add(left);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _note = CalendarVisuals.Label("", 9, "PanelTertiary");
        _next = CalendarVisuals.Label("", 12, "PanelPrimary");
        right.Children.Add(_note);
        right.Children.Add(_next);
        SetColumn(right, 1);
        Children.Add(right);

        workspace.Changed += Refresh;
        Refresh();
    }

    public FrameworkElement Element => this;

    public void SetViewport(double width, double height, double scale = 1)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
    }

    public void SetViewport(ModuleViewport viewport) =>
        Margin = new Thickness(viewport.HorizontalInset, 0, viewport.HorizontalInset, 0);

    public void Refresh()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var info = ChineseCalendar.Describe(today);
        var focus = _workspace.Focus();
        _number.Text = today.Day.ToString();
        _week.Text = CalendarVisuals.Weekday(today);
        string note = info.Festival == Festival.None ? info.Lunar : CalendarVisuals.FestivalName(info.Festival);
        _note.Text = string.IsNullOrEmpty(note)
            ? Loc.T($"{focus.TodayCount} 项日程", $"{focus.TodayCount} events")
            : note + " · " + Loc.T($"{focus.TodayCount} 项", $"{focus.TodayCount} events");
        _next.Text = focus.Running is { } running
            ? Loc.T($"进行中 · {running.Title}", $"Now · {running.Title}")
            : focus.Upcoming is { } next
                ? $"{next.Clock}  {next.Title}"
                : focus.AllDayToday ? Loc.T("今天有全天日程", "All-day today") : Loc.T("暂无日程", "Nothing scheduled");
        if (focus.Running is not null || focus.Upcoming is not null) _next.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary");
        else _next.SetResourceReference(TextBlock.ForegroundProperty, "PanelTertiary");
        _next.ToolTip = _next.Text;
        AutomationProperties.SetName(this, $"{CalendarVisuals.DayTitle(today)} {_note.Text} {_next.Text}");
    }

    public void Dispose()
    {
        _workspace.Changed -= Refresh;
        Children.Clear();
    }
}
