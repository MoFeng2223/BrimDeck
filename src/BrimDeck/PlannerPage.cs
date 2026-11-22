using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;

namespace BrimDeck;

// The planner page ("计划"): a month on the left and, on the right, the chosen day's events, to-dos or note, one at a time
// (AgendaCalendar.LayoutFor gives the narrower forms). It reads and changes the shared AgendaBook (App.Agenda) only.
//
// The page keeps what the user is typing in through every redraw: the note's box, the to-do box, a to-do being changed and the
// event form are kept and put back in the new rows, and the lists keep their scroll positions. It is drawn again only when what it
// shows changed (ShownKey): the data, the day, the minute where times are shown, the size step, the theme or the language.
internal sealed partial class PlannerPage : Grid
{
    private readonly MainWindow _deck;
    private PanelLook Look => _deck.Look;
    private AgendaCenter Center => _deck.AgendaData;
    private AgendaBook Book => Center.Book;
    // The clock the page reads; the README renderer gives it a fixed time.
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;
    private DateTime Now => AgendaTime.Minute(Clock());
    private DateOnly Today => DateOnly.FromDateTime(Clock());

    private DateOnly _day, _month;
    private AgendaView _view;
    private bool _blocks;
    private bool _started;
    // How the page is laid out now, and what its frame was built for.
    private AgendaLayout _layout;
    private string _frameKey = "", _shownKey = "", _lookKey = "";
    private double _width, _height, _cell = AgendaCalendar.CellHeight;

    // The frame's parts; their contents are set by Show.
    private Grid _monthHead = null!, _monthGrid = null!, _dayHead = null!, _week = null!;
    private Border _status = null!, _body = null!, _footer = null!;

    // An item just added from a notice ("刚加入"), marked on its day until the mark times out or the day is left.
    private string? _marked;
    private DateOnly _markedDay;
    private readonly DispatcherTimer _markTimer;
    private static readonly TimeSpan MarkTime = TimeSpan.FromSeconds(8);

    public PlannerPage(MainWindow deck)
    {
        _deck = deck;
        ClipToBounds = false;
        _markTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = MarkTime };
        _markTimer.Tick += (_, _) => { _markTimer.Stop(); _marked = null; Show(); };
        _noteSave = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = NoteDelay };
        _noteSave.Tick += (_, _) => { _noteSave.Stop(); SaveNote(); };
    }

    // ---- Drawing ----

    // panelWidth: the page's whole width; width and height: what the page itself has, inside the panel's margins.
    public void Render(double panelWidth, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        var settings = _deck.PanelSettings;
        if (!_started)
        {
            _started = true;
            _day = Today; _month = new DateOnly(_day.Year, _day.Month, 1);
            _view = settings.AgendaView; _blocks = settings.AgendaBlocks;
        }
        _width = width; _height = height;
        _layout = AgendaCalendar.LayoutFor(panelWidth, height);
        _cell = _layout == AgendaLayout.Wide ? AgendaCalendar.CellHeightFor(height) : AgendaCalendar.CellHeight;
        string look = string.Join('|', Look.Generation, Loc.Language, SystemParameters.HighContrast);
        if (look != _lookKey) { _lookKey = look; Restyle(); }
        string frame = string.Join('|', _layout, _cell, look);
        if (frame != _frameKey) { _frameKey = frame; BuildFrame(); _shownKey = ""; }
        Show();
    }

    // What the page shows besides its frame; a change of any of it draws the page's contents again.
    private string ShownKey()
    {
        var now = Now;
        bool timed = _view == AgendaView.Events && _day == Today;
        return string.Join('|', Book.Revision, _day, _month, _view, _blocks, Today, timed ? now.Ticks : 0, Center.ReadState, Center.SaveFailed, _marked,
            _formFor, _editingTodo, _todoLongTerm);
    }

    // Draws the contents again if anything they show changed.
    public void Show(bool force = false)
    {
        if (!_started || _body is null) return;
        if (_marked is not null && _markedDay != _day) { _marked = null; _markTimer.Stop(); }
        string key = ShownKey();
        if (!force && key == _shownKey) return;
        _shownKey = key;
        if (_view != AgendaView.Note) SaveNote();
        ShowHeads();
        ShowStatus();
        ShowBody();
        ShowFooter();
    }

    private void BuildFrame()
    {
        Children.Clear(); RowDefinitions.Clear(); ColumnDefinitions.Clear();
        _monthHead = new Grid { Height = 24 };
        _dayHead = new Grid { Height = 24 };
        _monthGrid = new Grid();
        _week = new Grid { Height = AgendaCalendar.WeekHeight, Margin = new Thickness(0, 6, 0, 0) };
        _status = new Border { Visibility = Visibility.Collapsed };
        _body = new Border { ClipToBounds = true };
        _footer = new Border { Margin = new Thickness(0, 6, 0, 0) };
        // The lists are made anew for the new frame; what is being typed is kept (the boxes move into it).
        _eventScroll = null; _todoScroll = null; _blockScroll = null; _formScroll = null;
        DetachTyping();
        switch (_layout)
        {
            case AgendaLayout.Wide:
            {
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AgendaCalendar.MonthWidth) });
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(29) });
                ColumnDefinitions.Add(new ColumnDefinition());
                var month = new Grid();
                month.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
                month.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
                month.RowDefinitions.Add(new RowDefinition());
                month.Children.Add(_monthHead);
                var weekdays = Weekdays(); weekdays.Margin = new Thickness(0, 4, 0, 0); Grid.SetRow(weekdays, 1); month.Children.Add(weekdays);
                Grid.SetRow(_monthGrid, 2); month.Children.Add(_monthGrid);
                Children.Add(month);
                var line = new Border { Width = 1, Background = UI.Brush(Look.Hairline), HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetColumn(line, 1); Children.Add(line);
                var day = DayStack(_dayHead, null, 8);
                Grid.SetColumn(day, 2); Children.Add(day);
                break;
            }
            case AgendaLayout.Narrow:
                Children.Add(DayStack(_monthHead, _week, 6));
                break;
            default:
                Children.Add(DayStack(_dayHead, null, 8));
                break;
        }
    }

    // The day's column: its head, the week under it in the narrow form, the line about the file, the contents and the add bar.
    private Grid DayStack(FrameworkElement head, FrameworkElement? week, double gap)
    {
        var grid = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        grid.Children.Add(head);
        if (week is not null) { Grid.SetRow(week, 1); grid.Children.Add(week); }
        _status.Margin = new Thickness(0, gap, 0, 0);
        Grid.SetRow(_status, 2); grid.Children.Add(_status);
        _body.Margin = new Thickness(0, gap, 0, 0);
        Grid.SetRow(_body, 3); grid.Children.Add(_body);
        Grid.SetRow(_footer, 4); grid.Children.Add(_footer);
        return grid;
    }

    private Grid Weekdays()
    {
        var row = new Grid { Height = 18 };
        for (int i = 0; i < 7; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var name = AgendaDraw.Words(Weekday(i), 11, 18, Look.Tertiary);
            name.TextAlignment = TextAlignment.Center;
            Grid.SetColumn(name, i); row.Children.Add(name);
        }
        return row;
    }

    // Monday first: "一" … "日" / "Mon" … "Sun" (the narrow week's small names too).
    private static string Weekday(int column) => Loc.IsEnglish ? AgendaText.Weekday((DayOfWeek)((column + 1) % 7)) : "一二三四五六日"[column].ToString();

    // ---- The heads ----

    private void ShowHeads()
    {
        var today = Today;
        switch (_layout)
        {
            case AgendaLayout.Wide:
                Head(_monthHead, Title(AgendaText.Month(_month.Year, _month.Month)), null, _month != new DateOnly(today.Year, today.Month, 1), Loc.T("上个月", "Previous month"), Loc.T("下个月", "Next month"), -1, false);
                Head(_dayHead, DayTitle(), Switches(), false, null, null, 0, false);
                ShowMonth();
                break;
            case AgendaLayout.Narrow:
                Head(_monthHead, Title(AgendaText.Month(_day.Year, _day.Month)), null, AgendaCalendar.WeekStart(_day) != AgendaCalendar.WeekStart(today), Loc.T("上一周", "Previous week"), Loc.T("下一周", "Next week"), -7, true);
                ShowWeek();
                break;
            default:
                Head(_dayHead, DayTitle(), null, _day != today, Loc.T("前一天", "Previous day"), Loc.T("后一天", "Next day"), -1, true, days: true);
                break;
        }
    }

    private TextBlock Title(string text)
    {
        var title = AgendaDraw.Words(text, 15, 24, Look.Primary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        return title;
    }

    // The day's title and how far it is from today: "11月22日 周日  今天", one line that ends in an ellipsis where the head is short of room.
    private FrameworkElement DayTitle()
    {
        var title = Title(AgendaText.DayTitle(_day, Today));
        if (AgendaText.Relative(_day, Today) is { } relative)
        {
            title.Inlines.Add(new System.Windows.Documents.Run(" "));
            title.Inlines.Add(new System.Windows.Documents.Run(relative) { FontSize = 11.5, FontWeight = FontWeights.Normal, Foreground = UI.Brush(Look.Tertiary) });
        }
        return title;
    }

    // A head row: its title on the left; on the right "今天" when away from today, the two arrows (when step is not 0) and the switches.
    // step: what an arrow moves by, months (-1 with days false and the wide month), or days.
    private void Head(Grid head, FrameworkElement title, FrameworkElement? right, bool away, string? back, string? forward, int step, bool switches, bool days = false)
    {
        head.Children.Clear(); head.ColumnDefinitions.Clear();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 40 });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (title is TextBlock text) text.TextTrimming = TextTrimming.CharacterEllipsis;
        head.Children.Add(title);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (away)
        {
            var label = AgendaDraw.Words(Loc.T("今天", "Today"), 11.5, 16, Look.Secondary);
            label.VerticalAlignment = VerticalAlignment.Center;
            var today = AgendaDraw.Face(Look, label, Loc.T("回到今天", "Go to today"), GoToday, 20, 6, new Thickness(8, 0, 8, 0), Look.Fill);
            today.Margin = new Thickness(0, 0, 4, 0);
            tools.Children.Add(today);
        }
        if (step != 0)
        {
            bool weeks = step == -7;
            tools.Children.Add(AgendaDraw.IconButton(Look, AgendaDraw.Left, back!, () => Step(-1, weeks, days)));
            var next = AgendaDraw.IconButton(Look, AgendaDraw.Right, forward!, () => Step(1, weeks, days));
            next.Margin = new Thickness(2, 0, 0, 0);
            tools.Children.Add(next);
        }
        if (switches) { var group = Switches(); group.Margin = new Thickness(8, 0, 0, 0); tools.Children.Add(group); }
        if (right is not null) tools.Children.Add(right);
        Grid.SetColumn(tools, 1);
        head.Children.Add(tools);
    }

    // The three switches: 日程 with the day's events, 待办 with its open to-dos, 便签 with a dot when it has a note.
    private FrameworkElement Switches()
    {
        var counts = Book.Counts(_day, Today);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (view, name) in new[] { (AgendaView.Events, Loc.T("日程", "Events")), (AgendaView.Todos, Loc.T("待办", "To-dos")), (AgendaView.Note, Loc.T("便签", "Note")) })
        {
            bool on = view == _view;
            var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var words = AgendaDraw.Words(name, 12, 16, on ? Look.Primary : Look.Secondary);
            words.VerticalAlignment = VerticalAlignment.Center;
            label.Children.Add(words);
            int count = view switch { AgendaView.Events => counts.Events, AgendaView.Todos => counts.OpenTodos, _ => 0 };
            string spoken = name;
            if (count > 0)
            {
                var figure = AgendaDraw.Words(count.ToString(CultureInfo.InvariantCulture), 12, 16, on ? Look.Secondary : Look.Tertiary);
                figure.VerticalAlignment = VerticalAlignment.Center; figure.Margin = new Thickness(5, 0, 0, 0);
                label.Children.Add(figure);
                spoken += " " + count.ToString(CultureInfo.InvariantCulture);
            }
            else if (view == AgendaView.Note && counts.Note)
            {
                label.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = UI.Brush(on ? Look.Secondary : Look.Tertiary), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                spoken += Loc.T("，已写", ", written");
            }
            var chosen = view;
            var button = AgendaDraw.Face(Look, label, spoken, () => SetView(chosen), 22, 7, new Thickness(11, 0, 11, 0), on ? Look.FillSelected : null, on ? Look.FillSelected : null);
            if (row.Children.Count > 0) button.Margin = new Thickness(1, 0, 0, 0);
            row.Children.Add(button);
        }
        return new Border { Background = UI.Brush(Look.Fill), CornerRadius = new CornerRadius(Look.Corner(8)), Padding = new Thickness(1), Child = row, VerticalAlignment = VerticalAlignment.Center };
    }

    // ---- Moving about ----

    private void Step(int direction, bool weeks, bool days)
    {
        if (days) { Choose(_day.AddDays(direction)); return; }
        if (weeks) { Choose(_day.AddDays(7 * direction)); return; }
        _month = _month.AddMonths(direction);
        Show();
    }

    private void GoToday() => Choose(Today);

    // Chooses a day: the month turns to it; what was typed for the day left is kept first.
    private void Choose(DateOnly day)
    {
        if (day == _day && _month.Month == day.Month && _month.Year == day.Year) return;
        SaveNote();
        CommitTodo();
        CloseForm();
        _day = day;
        _month = new DateOnly(day.Year, day.Month, 1);
        _eventOffset = _todoOffset = 0; _blockMinute = null;
        Show();
    }

    private void SetView(AgendaView view)
    {
        if (view == _view) return;
        SaveNote();
        CommitTodo();
        CloseForm();
        _view = view;
        _eventOffset = _todoOffset = 0; _blockMinute = null;
        var settings = _deck.PanelSettings;
        if (settings.AgendaView != view) { settings.AgendaView = view; _deck.AgendaSettingsEdited(); }
        Show();
    }

    private void SetBlocks(bool blocks)
    {
        if (blocks == _blocks) return;
        _blocks = blocks;
        _blockMinute = null;
        var settings = _deck.PanelSettings;
        if (settings.AgendaBlocks != blocks) { settings.AgendaBlocks = blocks; _deck.AgendaSettingsEdited(); }
        Show();
    }

    // "查看" from a notice: the day, switched to the view, with the item marked "刚加入" for a while.
    public void Reveal(AgendaReveal reveal)
    {
        if (!_started) { _started = true; _blocks = _deck.PanelSettings.AgendaBlocks; }
        SaveNote(); CommitTodo(); CloseForm();
        _day = reveal.Day; _month = new DateOnly(reveal.Day.Year, reveal.Day.Month, 1);
        _view = reveal.View;
        var settings = _deck.PanelSettings;
        if (settings.AgendaView != reveal.View) { settings.AgendaView = reveal.View; _deck.AgendaSettingsEdited(); }
        _eventOffset = _todoOffset = 0; _blockMinute = null;
        _marked = reveal.ItemId; _markedDay = reveal.Day;
        _markTimer.Stop();
        if (_marked is not null) _markTimer.Start();
        _revealPending = _marked is not null;
        Show(force: true);
    }
    private bool _revealPending;

    // Brings the marked row into view once it is laid out.
    private void RevealMarked(FrameworkElement row)
    {
        if (!_revealPending) return;
        _revealPending = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (row.IsLoaded) row.BringIntoView(); });
    }

    // ---- The month and the week ----

    private void ShowMonth()
    {
        _monthGrid.Children.Clear(); _monthGrid.RowDefinitions.Clear(); _monthGrid.ColumnDefinitions.Clear();
        for (int i = 0; i < 7; i++) _monthGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 6; i++) _monthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_cell) });
        var first = AgendaCalendar.FirstShown(_month.Year, _month.Month);
        int rows = AgendaCalendar.Rows(_month.Year, _month.Month);
        var marks = Book.Marks(first, rows * 7);
        for (int i = 0; i < rows * 7; i++)
        {
            var cell = DayCell(marks[i], marks[i].Day.Month == _month.Month, _cell, week: false);
            Grid.SetRow(cell, i / 7); Grid.SetColumn(cell, i % 7);
            _monthGrid.Children.Add(cell);
        }
    }

    private void ShowWeek()
    {
        _week.Children.Clear(); _week.ColumnDefinitions.Clear();
        var start = AgendaCalendar.WeekStart(_day);
        var marks = Book.Marks(start, 7);
        for (int i = 0; i < 7; i++)
        {
            _week.ColumnDefinitions.Add(new ColumnDefinition());
            var cell = DayCell(marks[i], true, AgendaCalendar.WeekHeight, week: true);
            Grid.SetColumn(cell, i);
            _week.Children.Add(cell);
        }
    }

    // A day of the month or the week: its date (today's in the accent, bold), the marks under it, the line of a longer event along
    // its foot, and the chosen day's fill. week: the narrow form's cell, with the weekday's name over the date.
    private FrameworkElement DayCell(AgendaDayMark mark, bool inMonth, double height, bool week)
    {
        var day = mark.Day;
        bool chosen = day == _day, today = day == Today;
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        if (week)
        {
            var name = AgendaDraw.Words(Weekday(((int)day.DayOfWeek + 6) % 7), 10.5, 14, Look.Tertiary);
            name.TextAlignment = TextAlignment.Center;
            stack.Children.Add(name);
            stack.Margin = new Thickness(0, 3, 0, 0);
        }
        else stack.Margin = new Thickness(0, 6 + Math.Floor((height - AgendaCalendar.CellHeight) / 2), 0, 0);
        var figure = AgendaDraw.Words(day.Day.ToString(CultureInfo.InvariantCulture), 12.5, 17, today ? Look.Accent : inMonth ? Look.Primary : Look.Tertiary, today ? FontWeights.Bold : null);
        figure.TextAlignment = TextAlignment.Center;
        stack.Children.Add(figure);
        var marks = new StackPanel { Orientation = Orientation.Horizontal, Height = 5, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        void Add(FrameworkElement shape) { if (marks.Children.Count > 0) shape.Margin = new Thickness(3, 0, 0, 0); marks.Children.Add(shape); }
        if (mark.DayEvents) Add(new Ellipse { Width = 5, Height = 5, Fill = UI.Brush(Look.Accent) });
        if (mark.OpenTodos) Add(new Ellipse { Width = 5, Height = 5, Stroke = UI.Brush(AgendaDraw.Tint(Look.Primary, .8)), StrokeThickness = 1.2 });
        if (mark.Note) Add(new Border { Width = 6, Height = 1.5, CornerRadius = new CornerRadius(1), Background = UI.Brush(AgendaDraw.Tint(Look.Primary, .7)), VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(marks);
        var cell = new Grid { Height = height };
        cell.Children.Add(stack);
        if (mark.InSpan)
            cell.Children.Add(new Border
            {
                Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Background = UI.Brush(Look.Accent), IsHitTestVisible = false,
                Margin = new Thickness(mark.SpanStart ? 9 : 0, 0, mark.SpanEnd ? 9 : 0, 0), CornerRadius = new CornerRadius(mark.SpanStart ? 1 : 0, mark.SpanEnd ? 1 : 0, mark.SpanEnd ? 1 : 0, mark.SpanStart ? 1 : 0)
            });
        string name2 = AgendaText.DayTitle(day, Today) + (today ? Loc.T("，今天", ", today") : "");
        var button = AgendaDraw.Face(Look, cell, name2, () => Choose(day), height, 8, new Thickness(0), chosen ? Look.FillSelected : null, chosen ? Look.FillSelected : null);
        return button;
    }

    // ---- The line about the file ----

    private void ShowStatus()
    {
        string? said = Center.ReadState switch
        {
            AgendaReadState.Unavailable => Loc.T("暂时无法读取计划文件，稍后会重试", "The planner file cannot be read now; trying again shortly"),
            AgendaReadState.Denied => Loc.T("没有读取计划文件的权限", "No permission to read the planner file"),
            _ => Center.SaveFailed ? Loc.T("计划未能保存，稍后会重试", "The planner could not be saved; trying again shortly") : null
        };
        if (said is null) { _status.Visibility = Visibility.Collapsed; _status.Child = null; return; }
        var line = AgendaDraw.Words(said, 11.5, 16, Look.Hex("danger"));
        line.ToolTip = Center.FilePath;
        _status.Child = line;
        _status.Visibility = Visibility.Visible;
    }
}
