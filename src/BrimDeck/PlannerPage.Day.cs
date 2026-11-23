using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;

namespace BrimDeck;

// The chosen day: its events as a list or as blocks on a time line, its to-dos, its note; the add bar or the event form under them;
// and what is typed in any of them.
internal sealed partial class PlannerPage
{
    private ScrollViewer? _eventScroll, _todoScroll, _blockScroll;
    private double _eventOffset, _todoOffset;
    // The minute of the day at the top of the time line; null until the view opens for a day (AgendaCalendar.OpeningMinute).
    private double? _blockMinute;
    // While the page puts its rows back, moves of its scroll positions and focus are its own.
    private bool _rebuilding;
    // A press on the panel is under way (PressedAt to Released), and contents changed meanwhile wait for its end (Show). Drawn
    // during the press, they replaced the button pressed, and its release clicked nothing: a press on "待办" after typing in the
    // note saved the note, the saved note drew the page again, and the switch took a second press.
    private bool _pressing, _showAfterPress;
    // The list or blocks choice the add row's switch was last drawn for: drawn for the other, its fill glides over.
    private bool? _blocksDrawn;

    // The event form, and what it is for: "new", "event:<id>" or "todo:<id>" (giving a to-do a time).
    private AgendaEventForm? _form;
    private string? _formFor;
    // A to-do whose words are being changed, in its box.
    private string? _editingTodo;
    private TextBox? _todoEdit;
    // The to-do bar, and whether what is typed there goes to the day or to "长期".
    private Border? _todoBar;
    private TextBox? _todoInput;
    private InkCentredText? _todoScope;
    private bool _todoLongTerm;
    // The due day's month, and the to-do it is open for.
    private AgendaDatePopup? _duePopup;
    private string? _dueFor;
    // The note: one box for the page, holding the day it was filled for; typed words are saved a moment after the typing stops.
    private Grid? _noteHost;
    private TextBox? _noteBox;
    private DateOnly? _noteDay;
    private bool _noteDirty, _noteLoading;
    private readonly System.Windows.Threading.DispatcherTimer _noteSave;
    private static readonly TimeSpan NoteDelay = TimeSpan.FromSeconds(.8);

    private const double HourHeight = 32, LineTop = 8, AxisWidth = 40, BlockLeft = 44, BlockGap = 3;

    // ---- What the panel asks ----

    // The page holds the panel open while a month of it is open, and while the user types in it (the panel has the foreground).
    public bool Holds(bool active) => _duePopup?.IsOpen == true || _form?.PopupOpen == true || active && (_form is not null || _editingTodo is not null || IsKeyboardFocusWithin);

    // Esc leaves a month, the form, a to-do's words and the typing, in that order, before it closes the panel.
    public bool Escape()
    {
        if (_duePopup?.IsOpen == true) { _duePopup.Close(); return true; }
        if (_form?.PopupOpen == true) { _form.ClosePopups(); return true; }
        if (_form is not null) { CloseForm(); Show(); return true; }
        if (_editingTodo is not null && _todoEdit is { } box)
        {
            _editingTodo = null; _todoEdit = null;
            Detach(box);
            _deck.AgendaEndTyping();
            Show();
            return true;
        }
        if (IsKeyboardFocusWithin) { SaveNote(); _deck.AgendaEndTyping(); Keyboard.ClearFocus(); return true; }
        return false;
    }

    // A press on the panel outside the box being typed in: what was typed is kept and the keyboard goes back to where it came from.
    // A press in the form, or in another of the page's boxes, is the typing's own.
    public void PressedAt(object? source)
    {
        _pressing = true;
        if (!IsKeyboardFocusWithin || source is not DependencyObject pressed) return;
        if (_form is { } form && Within(pressed, form)) return;
        for (var node = pressed; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is TextBoxBase && Within(node, this)) return;
        SaveNote();
        CommitTodo();
        _deck.AgendaEndTyping();
        Keyboard.ClearFocus();
    }

    // The press is over (released on the panel, or the pointer left it with nothing held): contents that waited for it are drawn,
    // after the release has clicked what was pressed.
    public void Released()
    {
        _pressing = false;
        if (!_showAfterPress) return;
        _showAfterPress = false;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { if (IsVisible) Show(); });
    }

    // The panel leaves the page or closes, or the user goes to another window: what was typed is kept.
    public void Flush() { SaveNote(); CommitTodo(); }

    public void Closed()
    {
        Flush();
        _duePopup?.Close();
        if (_form is not null) { _form.ClosePopups(); Detach(_form); _form = null; _formFor = null; }
        _marked = null; _markTimer.Stop();
        _shownKey = "";
    }

    private static bool Within(DependencyObject node, DependencyObject ancestor)
    {
        for (var at = node; at is not null; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (ReferenceEquals(at, ancestor)) return true;
        return false;
    }

    private static void Detach(FrameworkElement? element)
    {
        switch (element?.Parent)
        {
            case Panel panel: panel.Children.Remove(element); break;
            case Decorator decorator: decorator.Child = null; break;
            case ContentControl content: content.Content = null; break;
        }
    }

    // Another theme or language: the kept parts drew their colors and words from the old one, so they are made anew, with what was
    // typed in them kept (the note saved, a to-do's words kept, the to-do box's words carried over). An open form closes.
    private void Restyle()
    {
        SaveNote();
        CommitTodo();
        _duePopup?.Close(); _duePopup = null;
        if (_form is not null) CloseForm();
        string typed = _todoInput?.Text ?? "";
        Detach(_todoBar); Detach(_noteHost);
        _todoBar = null; _todoInput = null; _todoScope = null;
        _noteHost = null; _noteBox = null; _noteDay = null;
        if (typed.Length > 0) { TodoBar(); _todoInput!.Text = typed; }
        _eventScroll = _todoScroll = _blockScroll = null;
    }

    // The frame is built again: the kept parts leave the old one.
    private void DetachTyping()
    {
        Detach(_form); Detach(_todoBar); Detach(_noteHost); Detach(_todoEdit);
    }

    // ---- The body and the foot ----

    private void ShowBody()
    {
        var now = Now;
        FrameworkElement content = _view switch
        {
            AgendaView.Todos => TodoList(),
            AgendaView.Note => NoteView(),
            _ => _blocks ? Blocks(now) : EventList(now)
        };
        if (!ReferenceEquals(_body.Child, content)) { _rebuilding = true; try { _body.Child = content; } finally { _rebuilding = false; } }
    }

    private void ShowFooter()
    {
        FrameworkElement? foot = _form ?? (_view switch { AgendaView.Events => AddRow(), AgendaView.Todos => TodoBar(), _ => null });
        // On a page too low for the day's head, its list and the form together, the form takes the page alone (with the start's day
        // in it), scrolling where even it does not fit.
        bool alone = _layout == AgendaLayout.Short && _form is not null;
        _dayHead.Visibility = alone ? Visibility.Collapsed : Visibility.Visible;
        _body.Visibility = alone ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(_footer, alone ? 3 : 4);
        _footer.Margin = new Thickness(0, alone ? 0 : 6, 0, 0);
        if (alone)
        {
            _formScroll ??= new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Background = Brushes.Transparent };
            if (!ReferenceEquals(_formScroll.Content, foot)) { _rebuilding = true; try { Detach(foot); _formScroll.Content = foot; } finally { _rebuilding = false; } }
            foot = _formScroll;
        }
        else if (_formScroll is not null && _formScroll.Content is not null) { _rebuilding = true; try { _formScroll.Content = null; } finally { _rebuilding = false; } }
        _footer.Visibility = foot is null ? Visibility.Collapsed : Visibility.Visible;
        if (!ReferenceEquals(_footer.Child, foot)) { _rebuilding = true; try { Detach(foot); _footer.Child = foot; } finally { _rebuilding = false; } }
    }
    private ScrollViewer? _formScroll;

    private ScrollViewer NewScroll(Action<double> remember)
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
            PanningMode = PanningMode.VerticalOnly, Background = Brushes.Transparent
        };
        NoticeFade.Foot(scroll, 18);
        scroll.ScrollChanged += (_, e) => { if (!_rebuilding && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) remember(scroll.VerticalOffset); };
        return scroll;
    }

    // Puts the rows in and scrolls back to where the list was, keeping the focus of a box among them.
    private void Fill(ScrollViewer scroll, FrameworkElement content, double offset)
    {
        var focused = Keyboard.FocusedElement as UIElement;
        bool within = focused is not null && Within(focused, scroll);
        _rebuilding = true;
        try
        {
            scroll.Content = content;
            if (within && focused is not null && Within(focused, content)) focused.Focus();
            if (offset > 0 && scroll.IsLoaded) { scroll.UpdateLayout(); scroll.ScrollToVerticalOffset(offset); }
            else if (offset > 0) scroll.Loaded += Restore;
        }
        finally { _rebuilding = false; }
        void Restore(object sender, RoutedEventArgs e) { scroll.Loaded -= Restore; scroll.ScrollToVerticalOffset(offset); }
    }

    private TextBlock Empty(string text)
    {
        var line = AgendaDraw.Words(text, 12, 18, Look.Tertiary);
        line.Margin = new Thickness(0, 4, 0, 4);
        return line;
    }

    // ---- Events as a list ----

    private FrameworkElement EventList(DateTime now)
    {
        var scroll = _eventScroll ??= NewScroll(offset => _eventOffset = offset);
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var segments = Book.Segments(_day);
        if (segments.Count == 0) list.Children.Add(Empty(Loc.T("没有日程", "No events")));
        foreach (var segment in segments) list.Children.Add(EventRow(segment, now));
        Fill(scroll, list, _eventOffset);
        return scroll;
    }

    // An event's row: its times (or "全天"), the bar, its title and one line of what else there is to say. Ended ones are grey. A click
    // opens the form on it.
    private FrameworkElement EventRow(AgendaSegment segment, DateTime now)
    {
        var item = segment.Event;
        bool past = AgendaQuery.Phase(segment, now) == AgendaPhase.Past, marked = _marked == item.Id, editing = _formFor == "event:" + item.Id;
        var grid = new Grid();
        foreach (var width in new[] { 36.0, 8, 2, 8 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (segment.AllDay)
        {
            var all = AgendaDraw.Words(Loc.T("全天", "All day"), Loc.IsEnglish ? 10.5 : 12, 17, Look.Secondary);
            all.TextAlignment = TextAlignment.Right; all.TextTrimming = TextTrimming.None;
            grid.Children.Add(all);
        }
        else
        {
            var start = AgendaDraw.Words(AgendaText.Clock(segment.Start, segment.Day), 12, 17, past ? Look.Secondary : Look.Primary);
            var end = AgendaDraw.Words(AgendaText.Clock(segment.End, segment.Day), 11, 15, Look.Tertiary);
            start.TextAlignment = TextAlignment.Right; end.TextAlignment = TextAlignment.Right;
            grid.Children.Add(new StackPanel { Children = { start, end } });
        }
        var bar = new Border { Width = 2, CornerRadius = new CornerRadius(1), Background = UI.Brush(past ? Look.Track : Look.Accent) };
        Grid.SetColumn(bar, 2); grid.Children.Add(bar);
        var text = new StackPanel();
        text.Children.Add(AgendaDraw.Words(item.Title, 12.5, 17, past ? Look.Secondary : Look.Primary, past ? null : FontWeights.SemiBold));
        var phase = AgendaQuery.Phase(segment, now);
        string? status = AgendaText.Status(segment, now);
        if (Second(item.Source, [item.Detail], status, phase == AgendaPhase.Ongoing && segment.Day == Today, marked, 11.5, 15, Look.Secondary) is { } second) text.Children.Add(second);
        Grid.SetColumn(text, 4); grid.Children.Add(text);

        string rest = marked ? AgendaDraw.Tint(Look.Accent, .16) : editing ? Look.Fill : "#00000000";
        var row = new Border { Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(-6, 0, -6, 0), CornerRadius = new CornerRadius(Look.Corner(8)), Background = UI.Brush(rest), Child = grid, Cursor = Cursors.Hand };
        Hover(row, rest);
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenEventForm(item); };
        AutomationProperties.SetName(row, item.Title + " " + (segment.AllDay ? Loc.T("全天", "All day") : AgendaText.Times(segment)));
        if (marked) RevealMarked(row);
        return row;
    }

    private void Hover(Border row, string rest)
    {
        Feedback.SetOwnHover(row, true);
        row.MouseEnter += (_, _) => row.Background = UI.Brush(Look.Hex("hover"));
        row.MouseLeave += (_, _) => row.Background = UI.Brush(rest);
    }

    // The second line: the source (its application's icon and "Outlook · 王敏", which opens the notice while it is kept), the other
    // words, the status (in the accent while the event is under way) and "刚加入". Null when there is nothing to say.
    private TextBlock? Second(AgendaSource? source, IEnumerable<string?> words, string? status, bool live, bool marked, double size, double line, string color)
    {
        var block = AgendaDraw.Words("", size, line, color);
        bool any = false;
        void Gap() { if (any) block.Inlines.Add(new Run(" · ")); any = true; }
        if (source is not null)
        {
            var icon = _deck.AgendaSourceIcon(source, 14);
            icon.Margin = new Thickness(0, 0, 5, 0);
            block.Inlines.Add(new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center });
            var run = new Run(AgendaText.Source(source));
            if (_deck.AgendaNoticeKept(source.NoticeId))
            {
                run.Cursor = Cursors.Hand;
                run.MouseEnter += (_, _) => run.TextDecorations = TextDecorations.Underline;
                run.MouseLeave += (_, _) => run.TextDecorations = null;
                run.MouseLeftButtonDown += (_, e) => e.Handled = true;
                run.MouseLeftButtonUp += (_, e) => { e.Handled = true; Flush(); _deck.AgendaOpenNotice(source.NoticeId); };
                run.ToolTip = Loc.T("查看这条通知", "Show this notice");
            }
            block.Inlines.Add(run);
            any = true;
        }
        foreach (var word in words) if (!string.IsNullOrWhiteSpace(word)) { Gap(); block.Inlines.Add(new Run(word.Trim())); }
        if (status is not null) { Gap(); block.Inlines.Add(new Run(status) { Foreground = live ? UI.Brush(Look.Accent) : block.Foreground }); }
        if (marked) { Gap(); block.Inlines.Add(new Run(Loc.T("刚加入", "Just added"))); }
        return any ? block : null;
    }

    // ---- Events as blocks ----

    private FrameworkElement Blocks(DateTime now)
    {
        var segments = Book.Segments(_day);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        var whole = segments.Where(segment => segment.AllDay).ToList();
        if (whole.Count > 0)
        {
            // The all-day events and the whole days of longer ones, in one row over the time line; as many as fit.
            var chips = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var segment in whole)
            {
                var item = segment.Event;
                bool marked = _marked == item.Id;
                string words = segment.MultiDay ? $"{item.Title} · {AgendaText.DayOf(segment)}" : item.Title;
                var label = AgendaDraw.Words(words, 11.5, 18, Look.Primary);
                label.VerticalAlignment = VerticalAlignment.Center;
                var chip = new Border
                {
                    Height = 20, CornerRadius = new CornerRadius(Look.Corner(6)), Padding = new Thickness(8, 0, 8, 0), MaxWidth = 280, Margin = new Thickness(0, 0, 6, 0),
                    Background = UI.Brush(AgendaDraw.Tint(Look.Accent, marked ? .32 : .2)), BorderBrush = UI.Brush(Look.Accent), BorderThickness = new Thickness(2, 0, 0, 0), Child = label, Cursor = Cursors.Hand
                };
                chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenEventForm(item); };
                AutomationProperties.SetName(chip, words);
                chips.Children.Add(chip);
            }
            grid.Children.Add(new Border { Height = 20, ClipToBounds = true, Child = chips, Margin = new Thickness(0, 0, 0, 6) });
        }
        var scroll = _blockScroll ??= NewScroll(offset => _blockMinute = offset * 60 / HourHeight);
        var line = new PlannerTimeline { Height = LineTop * 2 + 24 * HourHeight };
        for (int hour = 0; hour <= 24; hour++)
        {
            double y = LineTop + hour * HourHeight;
            line.Add(new Border { Height = 1, Background = UI.Brush(Look.Hairline) }, new(PlannerTimeline.Kind.Line, y, y + 1));
            var label = AgendaDraw.Words($"{hour:00}:00", 10.5, 12, Look.Tertiary);
            label.TextAlignment = TextAlignment.Right;
            line.Add(label, new(PlannerTimeline.Kind.Label, y, y));
        }
        if (_day == Today)
        {
            double y = LineTop + now.TimeOfDay.TotalMinutes * HourHeight / 60;
            var mark = new Grid { Height = 6, IsHitTestVisible = false };
            mark.Children.Add(new Border { Height = 1.5, Background = UI.Brush(Look.Hex("danger")), VerticalAlignment = VerticalAlignment.Center });
            mark.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(Look.Hex("danger")), HorizontalAlignment = HorizontalAlignment.Left });
            line.Add(mark, new(PlannerTimeline.Kind.Now, y, y));
        }
        foreach (var placement in AgendaQuery.Columns(segments, TimeSpan.FromMinutes(30))) line.Add(Block(placement.Segment, now), Slot(placement));
        _blockMinute ??= AgendaCalendar.OpeningMinute(_day, now, segments);
        Grid.SetRow(scroll, 1);
        _rebuilding = true;
        try { Detach(scroll); grid.Children.Add(scroll); } finally { _rebuilding = false; }
        Fill(scroll, line, _blockMinute.Value * HourHeight / 60);
        return grid;
    }

    private static PlannerTimeline.Slot Slot(AgendaPlacement placement)
    {
        var segment = placement.Segment;
        double from = (segment.Start - AgendaTime.Midnight(segment.Day)).TotalMinutes, to = (segment.End - AgendaTime.Midnight(segment.Day)).TotalMinutes;
        to = Math.Max(to, from + 30);
        return new(PlannerTimeline.Kind.Block, LineTop + from * HourHeight / 60, LineTop + to * HourHeight / 60 - 1, placement.Column, placement.Columns);
    }

    // A block: its title and times on two lines, on one where it is under an hour high; ended ones faint.
    private FrameworkElement Block(AgendaSegment segment, DateTime now)
    {
        var item = segment.Event;
        bool past = AgendaQuery.Phase(segment, now) == AgendaPhase.Past, marked = _marked == item.Id;
        string surface = Look.Hex("surface");
        string rest = AgendaDraw.Mix(surface, Look.Accent, past ? .14 : marked ? .45 : .3), over = AgendaDraw.Mix(surface, Look.Accent, past ? .2 : marked ? .5 : .38);
        string title = past ? Look.Secondary : Look.Primary;
        var times = AgendaText.Times(segment);
        if (AgendaText.Left(segment, now) is { } left) times += " · " + left;
        if (marked) times += " · " + Loc.T("刚加入", "Just added");
        double minutes = Math.Max(30, (segment.End - segment.Start).TotalMinutes);
        bool two = minutes * HourHeight / 60 - 1 >= 30;
        FrameworkElement content;
        if (two)
        {
            var name = AgendaDraw.Words(item.Title, 11.5, 15, title, FontWeights.SemiBold);
            var when = AgendaDraw.Words(times, 11, 14, Look.Secondary);
            content = new StackPanel { Margin = new Thickness(0, 1, 0, 0), Children = { name, when } };
        }
        else
        {
            var one = AgendaDraw.Words("", 11.5, 15, title, FontWeights.SemiBold);
            one.Inlines.Add(new Run(item.Title));
            one.Inlines.Add(new Run(" " + times) { FontSize = 11, FontWeight = FontWeights.Normal, Foreground = UI.Brush(Look.Secondary) });
            one.VerticalAlignment = VerticalAlignment.Center;
            content = one;
        }
        var block = new Border
        {
            CornerRadius = new CornerRadius(Look.Corner(5)), Background = UI.Brush(rest), BorderBrush = UI.Brush(past ? AgendaDraw.Tint(Look.Accent, .5) : Look.Accent),
            BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0), Child = content, ClipToBounds = true, Cursor = Cursors.Hand
        };
        Feedback.SetOwnHover(block, true);
        block.MouseEnter += (_, _) => block.Background = UI.Brush(over);
        block.MouseLeave += (_, _) => block.Background = UI.Brush(rest);
        block.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenEventForm(item); };
        AutomationProperties.SetName(block, item.Title + " " + times);
        if (marked) RevealMarked(block);
        return block;
    }

    // ---- The add bar and the event form ----

    private FrameworkElement AddRow()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var plus = AgendaDraw.CentredIcon(AgendaDraw.Plus, 13, Look.Tertiary);
        var words = AgendaDraw.Label(Loc.T("添加日程", "Add event"), 12, Look.Tertiary);
        words.Margin = new Thickness(8, 0, 0, 0);
        var add = AgendaDraw.Face(Look, new StackPanel { Orientation = Orientation.Horizontal, Children = { plus, words } }, Loc.T("添加日程", "Add event"), () => OpenEventForm(null), 28, 8, new Thickness(8, 0, 8, 0), Look.Fill);
        AgendaDraw.Fit(add, words, 28);
        add.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ((Border)add.Content).HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.Children.Add(add);
        var segments = new List<Button>();
        foreach (var (blocks, data, name) in new[] { (false, "M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01", Loc.T("列表", "List")), (true, "M4 4h6v7H4zM14 8h6v9h-6zM4 15h6v5H4z", Loc.T("方块", "Blocks")) })
        {
            bool on = blocks == _blocks;
            var icon = AgendaDraw.CentredIcon(data, 13, on ? Look.Primary : Look.Secondary);
            bool choice = blocks;
            var button = AgendaDraw.Segment(Look, icon, name, on, () => SetBlocks(choice), bright => ((Path)((Canvas)icon.Child).Children[0]).Stroke = UI.Brush(bright ? Look.Primary : Look.Secondary),
                24, new Thickness(7, 0, 7, 0));
            button.ToolTip = name;
            segments.Add(button);
        }
        // Two pixels of track around 24 high segments: as tall as the add bar beside it. Drawn for the other choice than last
        // time, the fill glides over.
        int? from = _blocksDrawn is { } drawn && drawn != _blocks ? (drawn ? 1 : 0) : null;
        _blocksDrawn = _blocks;
        var group = AgendaDraw.Segments(Look, segments, _blocks ? 1 : 0, from, Soft.For(_deck.PanelSettings));
        group.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(group, 1);
        grid.Children.Add(group);
        return grid;
    }

    // The form opens in place of the add bar: empty for a new event, filled for an event (with "删除"), or with a to-do's words.
    private void OpenEventForm(AgendaEvent? item, AgendaTodo? todo = null)
    {
        CommitTodo();
        if (_form is not null) { _form.ClosePopups(); Detach(_form); _form = null; }
        bool elsewhere = item is not null && item.FirstDay != _day || _layout == AgendaLayout.Short;
        string submit = item is null && todo is null ? Loc.T("添加", "Add") : Loc.T("保存", "Save");
        var form = new AgendaEventForm(Look, _deck.AgendaKeyboard, submit, delete: item is not null, showStartDay: elsewhere) { Today = () => Today };
        if (item is not null) form.Fill(item.Title, item.Start, item.End, item.AllDay);
        else
        {
            var start = AgendaCalendar.NewEventStart(_day, Now);
            form.Fill(todo?.Text ?? "", start, start.AddHours(1), false);
        }
        _formFor = item is not null ? "event:" + item.Id : todo is not null ? "todo:" + todo.Id : "new";
        form.Submitted += SubmitForm;
        form.Cancelled += () => { CloseForm(); Show(); };
        form.Deleted += () =>
        {
            if (_formFor is { } what && what.StartsWith("event:", StringComparison.Ordinal)) { CloseForm(); Book.RemoveEvent(what[6..]); }
            Show();
        };
        _form = form;
        Show(force: true);
        form.FocusTitle(_deck.AgendaBeginTyping);
    }

    private void SubmitForm()
    {
        if (_form?.Value is not { } value || _formFor is not { } what) return;
        CloseForm();
        var now = Now;
        if (what.StartsWith("event:", StringComparison.Ordinal))
        {
            string id = what[6..];
            if (Book.UpdateEvent(id, value.Title, value.Start, value.End, value.AllDay, Book.FindEvent(id)?.Detail) is null) Book.AddEvent(value.Title, value.Start, value.End, value.AllDay, now);
        }
        else if (what.StartsWith("todo:", StringComparison.Ordinal))
        {
            if (Book.TodoToEvent(what[5..], value.Start, value.End, value.AllDay) is null) Book.AddEvent(value.Title, value.Start, value.End, value.AllDay, now);
        }
        else Book.AddEvent(value.Title, value.Start, value.End, value.AllDay, now);
        Show();
    }

    // Closes the form; the keyboard goes back if it was in the form.
    private void CloseForm()
    {
        if (_form is not { } form) return;
        bool typing = form.IsKeyboardFocusWithin;
        form.ClosePopups();
        _form = null; _formFor = null;
        Detach(form);
        if (typing) _deck.AgendaEndTyping();
    }

    // ---- To-dos ----

    private FrameworkElement TodoList()
    {
        var scroll = _todoScroll ??= NewScroll(offset => _todoOffset = offset);
        var today = Today;
        var todos = Book.TodosOf(_day, today);
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (todos.CarryOver is { } carry)
        {
            var row = new Grid { Height = 22 };
            // Both words centred in the row by their letters as drawn, so they sit on one line.
            var said = AgendaDraw.Label(AgendaText.CarryOver(carry), 11.5, Look.Tertiary);
            said.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(said);
            var words = AgendaDraw.Label(Loc.T("移到今天", "Move to today"), 11.5, Look.Accent);
            var move = AgendaDraw.Face(Look, words, Loc.T("移到今天", "Move to today"), () => Book.MoveToToday(Today), 20, 6, new Thickness(6, 0, 6, 0));
            AgendaDraw.Fit(move, words, 20);
            move.VerticalAlignment = VerticalAlignment.Center;
            move.HorizontalAlignment = HorizontalAlignment.Right; move.Margin = new Thickness(0, 0, -6, 0);
            row.Children.Add(move);
            list.Children.Add(row);
        }
        foreach (var todo in todos.Main) list.Children.Add(TodoRow(todo, today));
        if (todos.Main.Count == 0) list.Children.Add(Empty(Loc.T("没有待办", "No to-dos")));
        if (todos.LongTerm.Count > 0)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(AgendaDraw.Words(Loc.T("长期", "Anytime"), 11.5, 18, Look.Secondary, FontWeights.SemiBold));
            int open = todos.LongTerm.Count(todo => !todo.Done);
            if (open > 0) { var figure = AgendaDraw.Words(open.ToString(CultureInfo.InvariantCulture), 11.5, 18, Look.Tertiary); figure.Margin = new Thickness(6, 0, 0, 0); head.Children.Add(figure); }
            list.Children.Add(new Border { BorderBrush = UI.Brush(Look.Hairline), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(0, 6, 0, 0), Height = 30, Child = head });
            foreach (var todo in todos.LongTerm) list.Children.Add(TodoRow(todo, today));
        }
        Fill(scroll, list, _todoOffset);
        return scroll;
    }

    // A to-do: its box, its words (wrapping; struck through when done), the source under them, and its due day at the end. Under the
    // pointer the end shows its actions instead: give it a time, a due day, delete it. A click on the words changes them in place.
    private FrameworkElement TodoRow(AgendaTodo todo, DateOnly today)
    {
        bool marked = _marked == todo.Id;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var check = AgendaFields.Check(Look, todo.Done, Loc.T("完成：", "Done: ") + todo.Text);
        string id = todo.Id;
        check.Checked += (_, _) => Book.SetDone(id, true, Now);
        check.Unchecked += (_, _) => Book.SetDone(id, false, Now);
        // The box and the due day level with the middle of the first line's letters (InkLineAligned), at any display scale.
        var firstLine = AgendaDraw.Words(todo.Text, 12.5, 18, Look.Primary);
        grid.Children.Add(new InkLineAligned(firstLine, check));
        var text = new StackPanel();
        if (_editingTodo == todo.Id && _todoEdit is { } edit) { Detach(edit); text.Children.Add(edit); }
        else
        {
            var words = AgendaDraw.Words(todo.Text, 12.5, 18, todo.Done ? Look.Tertiary : Look.Primary, wrap: true);
            if (todo.Done) words.TextDecorations = TextDecorations.Strikethrough;
            words.Cursor = Cursors.IBeam; words.Background = Brushes.Transparent;
            words.MouseLeftButtonUp += (_, e) => { e.Handled = true; BeginTodoEdit(todo); };
            text.Children.Add(words);
        }
        if (Second(todo.Source, [], null, false, marked, 11, 15, Look.Tertiary) is { } second) { second.Margin = new Thickness(0, 1, 0, 0); text.Children.Add(second); }
        Grid.SetColumn(text, 2); grid.Children.Add(text);
        FrameworkElement? due = null;
        if (todo.Due is { } day)
        {
            var dueWords = AgendaDraw.Label(AgendaText.Due(day, today, todo.Done), 11.5, AgendaQuery.Overdue(todo, today) ? Look.Hex("danger") : Look.Tertiary);
            dueWords.Text.TextTrimming = TextTrimming.None; dueWords.Height = 18;
            due = new InkLineAligned(firstLine, dueWords) { Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(due, 3); grid.Children.Add(due);
        }
        // The actions lie over the end of the row, on the row's own hover fill over the surface, so the words under them are hidden.
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(AgendaDraw.IconButton(Look, "M12 2.5a9.5 9.5 0 1 1 0 19 9.5 9.5 0 1 1 0-19zM12 6.5V12l3.5 2", Loc.T("设置时间", "Set a time"), () => OpenEventForm(null, Book.FindTodo(id))));
        Button flag = null!;
        flag = AgendaDraw.IconButton(Look, "M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1zM4 22v-7", Loc.T("设置截止日", "Set a due date"), () => PickDue(id, flag));
        flag.Margin = new Thickness(2, 0, 0, 0);
        actions.Children.Add(flag);
        var remove = AgendaDraw.IconButton(Look, "M3 6h18M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2M10 11v6M14 11v6", Loc.T("删除", "Delete"), () => Book.RemoveTodo(id));
        remove.Margin = new Thickness(2, 0, 0, 0);
        actions.Children.Add(remove);
        var cover = new Border { Background = UI.Brush(Look.Hex("hover")), Padding = new Thickness(8, 0, 0, 0), Child = actions };
        var shield = new Border { Background = UI.Brush(Look.Hex("surface")), Child = cover, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1, 0, 0), Visibility = Visibility.Collapsed };
        Grid.SetColumn(shield, 2); Grid.SetColumnSpan(shield, 2);
        grid.Children.Add(shield);

        string rest = marked ? AgendaDraw.Tint(Look.Accent, .16) : "#00000000";
        var row = new Border { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(-6, 0, -6, 0), CornerRadius = new CornerRadius(Look.Corner(8)), Background = UI.Brush(rest), Child = grid };
        Feedback.SetOwnHover(row, true);
        void Paint(bool over)
        {
            bool shown = over || _dueFor == id;
            row.Background = UI.Brush(shown ? Look.Hex("hover") : rest);
            shield.Visibility = shown && _editingTodo != id ? Visibility.Visible : Visibility.Collapsed;
            if (due is not null) due.Visibility = shield.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        }
        row.MouseEnter += (_, _) => Paint(true);
        row.MouseLeave += (_, _) => Paint(false);
        if (_dueFor == id) Paint(false);
        AutomationProperties.SetName(row, todo.Text);
        if (marked) RevealMarked(row);
        return row;
    }

    private void PickDue(string id, FrameworkElement anchor)
    {
        if (Book.FindTodo(id) is not { } todo) return;
        _duePopup ??= new AgendaDatePopup(Look);
        _duePopup.Closed -= DueClosed; _duePopup.Closed += DueClosed;
        _dueFor = id;
        _duePopup.Show(anchor, todo.Due ?? (_day < Today ? Today : _day), Today, day => Book.SetDue(id, day), todo.Due is null ? null : Loc.T("不设截止日", "No due date"));
    }

    private void DueClosed() { _dueFor = null; Show(force: true); }

    private void BeginTodoEdit(AgendaTodo todo)
    {
        CommitTodo();
        CloseForm();
        var box = AgendaFields.Box(Look, _deck.AgendaKeyboard, 12.5, ring: false);
        box.TextWrapping = TextWrapping.Wrap; box.Text = todo.Text;
        TextBlock.SetLineHeight(box, 18); TextBlock.SetLineStackingStrategy(box, LineStackingStrategy.BlockLineHeight);
        box.Margin = new Thickness(-2, 0, -2, 0);
        AutomationProperties.SetName(box, Loc.T("待办内容", "To-do"));
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitTodo(endTyping: true); } };
        box.LostKeyboardFocus += (_, _) => { if (!_rebuilding && ReferenceEquals(_todoEdit, box)) Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_todoEdit, box) && !box.IsKeyboardFocusWithin) CommitTodo(); }); };
        _editingTodo = todo.Id; _todoEdit = box;
        Show(force: true);
        _deck.AgendaBeginTyping(box);
    }

    // Keeps the words of the to-do being changed (blank words leave it as it was).
    private void CommitTodo(bool endTyping = false)
    {
        if (_editingTodo is not { } id || _todoEdit is not { } box) return;
        bool typing = box.IsKeyboardFocusWithin;
        _editingTodo = null; _todoEdit = null;
        // During a press the page is drawn again only after it (Show): the box stays in its row until then, not a blank row.
        if (!_pressing) Detach(box);
        if (box.Text.Trim().Length > 0) Book.SetTodoText(id, box.Text.Trim());
        if (typing && endTyping) _deck.AgendaEndTyping();
        Show();
    }

    // The bar under the to-dos: type and press Enter to add one to the day or, with "长期" chosen at its end, to no day.
    private FrameworkElement TodoBar()
    {
        if (_todoBar is null)
        {
            // The plus in the middle of the bar, and the typed words and the hint centred by their letters (LetterBox, Label), as the
            // add bar of the events is.
            var plus = AgendaDraw.CentredIcon(AgendaDraw.Plus, 13, Look.Tertiary);
            var input = AgendaFields.Box(Look, _deck.AgendaKeyboard, 12, ring: false);
            ((LetterBox)input).Sample = "中";
            input.Margin = new Thickness(6, 0, 0, 0);
            AutomationProperties.SetName(input, Loc.T("添加待办", "Add to-do"));
            var hint = AgendaFields.Hint(input, Loc.T("添加待办", "Add to-do"), Look.Tertiary);
            input.TextChanged += (_, _) => hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            input.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                if (input.Text.Trim().Length == 0) return;
                string text = input.Text;
                input.Text = "";
                Book.AddTodo(text, _todoLongTerm ? null : _day, Now);
            };
            _todoScope = AgendaDraw.Label("", 11.5, Look.Secondary);
            var chevron = AgendaDraw.CentredIcon(AgendaDraw.Chevron, 11, Look.Tertiary);
            chevron.Margin = new Thickness(2, 0, 0, 0);
            var scope = AgendaDraw.Face(Look, new StackPanel { Orientation = Orientation.Horizontal, Children = { _todoScope, chevron } }, Loc.T("加到哪里", "Add to"),
                () => { _todoLongTerm = !_todoLongTerm; Show(); }, 20, 6, new Thickness(6, 0, 6, 0));
            AgendaDraw.Fit(scope, _todoScope, 20, Loc.T("长期", "Anytime"), Loc.T("这一天", "This day"));
            scope.Margin = new Thickness(0, 0, -4, 0);
            scope.VerticalAlignment = VerticalAlignment.Center;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(plus);
            var field = new Grid { Children = { input, hint } };
            Grid.SetColumn(field, 1); grid.Children.Add(field);
            Grid.SetColumn(scope, 2); grid.Children.Add(scope);
            var bar = new Border { Height = 28, CornerRadius = new CornerRadius(Look.Corner(8)), Background = UI.Brush(Look.Fill), Padding = new Thickness(8, 0, 8, 0), Child = grid,
                BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Transparent, Cursor = Cursors.IBeam };
            input.GotKeyboardFocus += (_, _) => bar.BorderBrush = UI.Brush(Look.Accent);
            input.LostKeyboardFocus += (_, _) => bar.BorderBrush = Brushes.Transparent;
            bar.Padding = new Thickness(6.5, 0, 6.5, 0);
            _todoBar = bar; _todoInput = input;
        }
        _todoScope!.Text.Text = _todoLongTerm ? Loc.T("长期", "Anytime") : Loc.T("这一天", "This day");
        _todoScope.InvalidateMeasure(); _todoScope.InvalidateArrange();
        return _todoBar;
    }

    // ---- The note ----

    private FrameworkElement NoteView()
    {
        if (_noteHost is null)
        {
            var box = AgendaFields.Box(Look, _deck.AgendaKeyboard, 13, ring: false);
            box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; box.VerticalContentAlignment = VerticalAlignment.Top;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            TextBlock.SetLineHeight(box, 21); TextBlock.SetLineStackingStrategy(box, LineStackingStrategy.BlockLineHeight);
            box.Margin = new Thickness(-2, 0, -2, 0);
            AutomationProperties.SetName(box, Loc.T("便签", "Note"));
            var hint = AgendaDraw.Words(Loc.T("写点什么", "Write something"), 13, 21, Look.Tertiary);
            hint.IsHitTestVisible = false; hint.VerticalAlignment = VerticalAlignment.Top;
            box.TextChanged += (_, _) =>
            {
                hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (_noteLoading) return;
                _noteDirty = true;
                _noteSave.Stop(); _noteSave.Start();
            };
            box.LostKeyboardFocus += (_, _) => { if (!_rebuilding) SaveNote(); };
            _noteHost = new Grid { Margin = new Thickness(0, 2, 0, 0), Children = { box, hint }, Background = Brushes.Transparent, Cursor = Cursors.IBeam };
            _noteBox = box;
        }
        var note = _noteBox!;
        string kept = Book.Note(_day) ?? "";
        if (_noteDay != _day) { SaveNote(); Load(); }
        // Changed elsewhere (another panel) while not being typed in here: the box shows it.
        else if (!_noteDirty && !note.IsKeyboardFocusWithin && note.Text != kept) Load();
        return _noteHost;

        void Load()
        {
            _noteLoading = true;
            try { note.Text = kept; note.CaretIndex = note.Text.Length; note.ScrollToHome(); }
            finally { _noteLoading = false; }
            _noteDay = _day; _noteDirty = false;
        }
    }

    // Saves the note being typed (a blank one is removed).
    private void SaveNote()
    {
        _noteSave.Stop();
        if (!_noteDirty || _noteBox is null || _noteDay is not { } day) return;
        _noteDirty = false;
        Book.SetNote(day, _noteBox.Text);
    }
}

// The block view's time line: the hours' lines and names, the "now" line and the blocks, each placed by its Slot; the blocks share
// the width right of the axis by their columns (AgendaQuery.Columns), with a small gap between them.
internal sealed class PlannerTimeline : Panel
{
    public enum Kind { Line, Label, Now, Block }
    public sealed record Slot(Kind Kind, double Top, double Bottom, int Column = 0, int Columns = 1);
    private readonly Dictionary<UIElement, Slot> _slots = [];
    private const double AxisWidth = 40, LabelWidth = 34, BlockLeft = 44, Gap = 3, NowLeft = 36;

    public void Add(UIElement child, Slot slot)
    {
        _slots[child] = slot;
        // The now line is drawn under the blocks: it goes in before them.
        if (slot.Kind == Kind.Block) Children.Add(child);
        else Children.Insert(Children.Cast<UIElement>().TakeWhile(c => _slots[c].Kind != Kind.Block).Count(), child);
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsFinite(available.Width) ? available.Width : 300;
        foreach (UIElement child in Children)
        {
            var (rect, _) = Place(_slots[child], width);
            child.Measure(rect.Size);
        }
        return new Size(width, double.IsFinite(Height) ? Height : 0);
    }

    protected override Size ArrangeOverride(Size final)
    {
        foreach (UIElement child in Children) child.Arrange(Place(_slots[child], final.Width).Rect);
        return final;
    }

    private static (Rect Rect, bool) Place(Slot slot, double width) => slot.Kind switch
    {
        Kind.Line => (new Rect(AxisWidth, slot.Top, Math.Max(0, width - AxisWidth), 1), true),
        Kind.Label => (new Rect(0, slot.Top - 5, LabelWidth, 12), true),
        Kind.Now => (new Rect(NowLeft, slot.Top - 3, Math.Max(0, width - NowLeft), 6), true),
        _ => (Block(slot, width), true)
    };

    private static Rect Block(Slot slot, double width)
    {
        double room = Math.Max(0, width - BlockLeft), each = (room - Gap * (slot.Columns - 1)) / Math.Max(1, slot.Columns);
        double left = BlockLeft + slot.Column * (each + Gap);
        return new Rect(left, slot.Top, Math.Max(0, each), Math.Max(1, slot.Bottom - slot.Top));
    }
}
