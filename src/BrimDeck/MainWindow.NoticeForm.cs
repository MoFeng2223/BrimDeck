using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// The notices page's form for adding a notice to the planner or changing what it added: the strip's pencil (a time read, before it
// is added) and its "修改" (what was added). It opens in the focus column in place of the words and the strip, and its fields are
// the planner's own (AgendaFields), taking the keyboard as the planner does. It stays while the page would draw itself again for a
// tick or a change of data; another notice in front, another page or the panel closing shuts it without saving.
public partial class MainWindow
{
    private NoticeFormState? _noticeForm;
    // Below this panel height the form takes the column's top as well: the status line and the hero give way to it.
    private const double NoticeFormShortPanel = 300;

    private sealed record NoticeFormState(string NoticeId, NoticeAgendaForm Form, string? EventId, string? TodoId);

    // From the strip's pencil: a new event at the time read, or a to-do once switched. typing false leaves the keyboard where it is
    // (the README renderer, whose window must not take the foreground).
    private void OpenNoticeForm(Notice notice, NoticeAgendaTime time, bool typing = true)
    {
        var moment = time.Moment;
        var end = moment.End ?? (moment.AllDay ? moment.Start : moment.Start.AddHours(1));
        ShowNoticeForm(notice, null, null, false, NoticeAgenda.EventTitle(notice, time), moment.Start, end, moment.AllDay, DateOnly.FromDateTime(moment.Start), typing);
    }

    // From "修改": the event or the to-do the notice added.
    private void OpenNoticeForm(Notice notice, AgendaEvent? item, AgendaTodo? todo, bool typing = true)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (item is not null) ShowNoticeForm(notice, item.Id, null, false, item.Title, item.Start, item.End, item.AllDay, item.FirstDay, typing);
        else if (todo is not null)
        {
            var day = todo.Day ?? today;
            var start = AgendaCalendar.NewEventStart(day, DateTime.Now);
            ShowNoticeForm(notice, null, todo.Id, true, todo.Text, start, start.AddHours(1), false, day, typing);
        }
    }

    private void ShowNoticeForm(Notice notice, string? eventId, string? todoId, bool asTodo, string text, DateTime start, DateTime end, bool allDay, DateOnly day, bool typing)
    {
        CloseNoticeForm();
        bool existing = eventId is not null || todoId is not null;
        var form = new NoticeAgendaForm(Look, AgendaKeyboard, existing, asTodo) { Today = () => DateOnly.FromDateTime(DateTime.Now) };
        form.Fill(text, start, end, allDay, day);
        var state = new NoticeFormState(notice.Id, form, eventId, todoId);
        form.Submitted += () => SaveNoticeForm(state);
        form.Cancelled += () => { CloseNoticeForm(); RenderNotices(); };
        _noticeForm = state;
        RenderNotices();
        if (typing) form.FocusText(AgendaBeginTyping);
    }

    private void SaveNoticeForm(NoticeFormState state)
    {
        if (!ReferenceEquals(_noticeForm, state) || state.Form.Value is not { } value) return;
        var notice = _app.Notices.Hub.Notices.FirstOrDefault(n => n.Id == state.NoticeId);
        CloseNoticeForm();
        var book = _app.Agenda.Book;
        var now = DateTime.Now;
        if (state.EventId is { } eventId && book.FindEvent(eventId) is { } item)
        {
            if (value.Todo) { if (book.EventToTodo(eventId, value.Day) is { } made) book.SetTodoText(made.Id, value.Text); }
            else book.UpdateEvent(eventId, value.Text, value.Start, value.End, value.AllDay, item.Detail);
        }
        else if (state.TodoId is { } todoId && book.FindTodo(todoId) is not null)
        {
            if (value.Todo) { book.SetTodoText(todoId, value.Text); book.SetTodoDay(todoId, value.Day); }
            else if (book.TodoToEvent(todoId, value.Start, value.End, value.AllDay) is { } made) book.UpdateEvent(made.Id, value.Text, value.Start, value.End, value.AllDay, null);
        }
        else if (notice is not null)
        {
            // New, or what it was to change is gone meanwhile (removed on the planner page): added afresh from the notice.
            var source = AgendaSource.From(notice);
            if (value.Todo) book.AddTodo(value.Text, value.Day, now, source: source);
            else book.AddEvent(value.Text, value.Start, value.End, value.AllDay, now, source: source);
        }
        RenderNotices();
    }

    // Shuts the form without saving; the keyboard goes back if it was in it.
    private void CloseNoticeForm()
    {
        if (_noticeForm is not { } state) return;
        _noticeForm = null;
        bool typing = state.Form.IsKeyboardFocusWithin;
        state.Form.ClosePopups();
        Detach(state.Form);
        if (typing) AgendaEndTyping();
    }

    // RenderNotices with this notice in front: a form for another one (or none) is shut.
    private void KeepNoticeForm(Notice? focus)
    {
        if (_noticeForm is { } state && state.NoticeId != focus?.Id) CloseNoticeForm();
    }

    // While the form is open for the notice in front, the page is not built again for a tick or for data (UpdateNoticeRows): a
    // rebuild would take the keyboard and the input method's words away from the field being typed in.
    private bool NoticeFormKeepsPage(NoticeCenter center) => _noticeForm is { } state && FocusedNotice(center)?.Id == state.NoticeId;

    // The form in the focus column, in place of the words and the strip; on a short panel the status line and the hero give way.
    // Taller than what is left, it scrolls rather than being cut.
    private FrameworkElement? NoticeFormFor(Notice notice, FrameworkElement status, FrameworkElement hero)
    {
        if (_noticeForm is not { } state || state.NoticeId != notice.Id) return null;
        Detach(state.Form);
        bool short_ = ContentCanvas.Height < NoticeFormShortPanel;
        if (short_) { status.Visibility = Visibility.Collapsed; hero.Visibility = Visibility.Collapsed; }
        state.Form.Margin = new Thickness(0, short_ ? 0 : 10, 0, 0);
        state.Form.Tight = short_;
        return new ScrollViewer { Content = state.Form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
            PanningMode = PanningMode.VerticalOnly, Background = Brushes.Transparent };
    }

    private static void Detach(FrameworkElement form)
    {
        if (form.Parent is Panel panel) panel.Children.Remove(form);
        else if (form.Parent is ContentControl holder) holder.Content = null;
    }

    // The hero's third line while the form is open: the words' first line, which the form hides (as the design shows it).
    private bool NoticeFormOpen(Notice notice) => _noticeForm?.NoticeId == notice.Id;

    // The panel stays open while a month of the form is open, and while the form is open on the panel that has the keyboard.
    private bool NoticeFormHolds => _noticeForm is { } state && (state.Form.PopupOpen || IsActive);

    // Esc closes a month of the form first, then the form, before it closes the panel.
    private bool NoticeFormEscape()
    {
        if (_noticeForm is not { } state) return false;
        if (state.Form.PopupOpen) { state.Form.ClosePopups(); return true; }
        CloseNoticeForm(); RenderNotices();
        return true;
    }

    // A press on the panel outside the form while typing in it: the form stays, the keyboard goes back.
    private void NoticeFormPressedAt(object? source)
    {
        if (_noticeForm is not { } state || !state.Form.IsKeyboardFocusWithin || source is not DependencyObject pressed) return;
        for (var node = pressed; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, state.Form)) return;
        AgendaEndTyping();
        Keyboard.ClearFocus();
    }
}

// The fields of the notices page's form (N3 of the design): "待办"/"日程" and the form's buttons on one line, then the title (or the
// to-do's words) and the day with the times and "全天". Its fields are the planner's (AgendaFields); its rows wrap where the column is
// too narrow for them (the narrowest page). An event that ran over several days keeps its length when only its day or times change.
internal sealed class NoticeAgendaForm : StackPanel
{
    private readonly PanelLook _look;
    private readonly bool _existing;
    private readonly AgendaTextField _title, _words;
    private readonly AgendaDateField _day;
    private readonly AgendaTimeField _start, _end;
    private readonly ToggleButton _allDay;
    private readonly FrameworkElement _times, _allDayRow;
    private readonly TextBlock _textLabel, _dayLabel;
    private readonly Button _submit;
    private readonly Border _segments;
    private readonly Grid _grid;
    private readonly AgendaSplitRow _head;
    private readonly WrapPanel _when;
    private bool _todo;
    private int _spanDays;
    public event Action? Submitted, Cancelled;

    public NoticeAgendaForm(PanelLook look, Action<TextBox> keyboard, bool existing, bool todo)
    {
        _look = look; _existing = existing; _todo = todo;
        _title = AgendaFields.Text(look, keyboard, Loc.T("标题", "Title"));
        _words = AgendaFields.Text(look, keyboard, Loc.T("待办内容", "To-do"));
        _day = AgendaFields.Date(look, Loc.T("日期", "Date"));
        _start = AgendaFields.Time(look, keyboard, Loc.T("开始时间", "Start time"));
        _end = AgendaFields.Time(look, keyboard, Loc.T("结束时间", "End time"), endOfDay: true);
        _allDay = AgendaFields.Check(look, false, Loc.T("全天", "All day"));
        _allDay.VerticalAlignment = VerticalAlignment.Center;

        // "待办" | "日程", then "取消" and the main button at the right end; the buttons go under them where the line is too short.
        _segments = new Border { CornerRadius = new CornerRadius(look.Corner(6)), Background = UI.Brush(look.Fill), Padding = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        var cancel = AgendaDraw.Pill(look, Loc.T("取消", "Cancel"), () => Cancelled?.Invoke());
        cancel.Margin = new Thickness(0, 0, 6, 0);
        _submit = AgendaDraw.Pill(look, "", Submit);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { cancel, _submit } };
        _head = new AgendaSplitRow { Children = { _segments, buttons } };
        Children.Add(_head);

        // The two lines share their labels' column, so the fields start at one edge in either language.
        var grid = _grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 28 });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _textLabel = Label(); _dayLabel = Label();
        _dayLabel.VerticalAlignment = VerticalAlignment.Top; _dayLabel.Margin = new Thickness(0, 6, 8, 0);
        Grid.SetRow(_dayLabel, 2);
        grid.Children.Add(_textLabel); grid.Children.Add(_dayLabel);
        var text = new Grid { Children = { _title, _words } };
        Grid.SetColumn(text, 1); grid.Children.Add(text);

        // The day, the times and "全天" wrap onto further lines on a narrow column, each part whole.
        var dash = AgendaDraw.Words("–", 12.5, 18, look.Tertiary);
        dash.VerticalAlignment = VerticalAlignment.Center; dash.Margin = new Thickness(6, 0, 6, 0);
        _times = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), Children = { _start, dash, _end } };
        var allDayLabel = AgendaDraw.Words(Loc.T("全天", "All day"), 11.5, 16, look.Secondary);
        allDayLabel.VerticalAlignment = VerticalAlignment.Center; allDayLabel.Margin = new Thickness(6, 0, 0, 0);
        // Its gap from the times is its own, so the times never carry a margin past the column's edge when they wrap.
        var allDayRow = new StackPanel { Orientation = Orientation.Horizontal, Height = 28, Margin = new Thickness(12, 0, 0, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand, Children = { _allDay, allDayLabel } };
        allDayRow.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject source && Within(source, _allDay)) return; _allDay.IsChecked = _allDay.IsChecked != true; e.Handled = true; };
        _allDayRow = allDayRow;
        _day.Margin = new Thickness(0, 0, 8, 6);
        var when = _when = new WrapPanel { Margin = new Thickness(0, 0, 0, -6), Children = { _day, _times, _allDayRow } };
        Grid.SetColumn(when, 1); Grid.SetRow(when, 2); grid.Children.Add(when);
        Children.Add(grid);

        _title.Changed += Check; _words.Changed += Check; _start.Changed += Check; _end.Changed += Check; _day.Changed += Check;
        _allDay.Checked += (_, _) => AllDayChanged(); _allDay.Unchecked += (_, _) => AllDayChanged();
        foreach (var box in new[] { _title.Box, _words.Box, _start.Box, _end.Box })
            box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        Kind(todo);
    }

    private TextBlock Label()
    {
        var label = AgendaDraw.Words("", 11.5, 16, _look.Tertiary);
        label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(0, 0, 8, 0);
        return label;
    }

    private static bool Within(DependencyObject node, DependencyObject ancestor)
    {
        for (var at = node; at is not null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (ReferenceEquals(at, ancestor)) return true;
        return false;
    }

    public Func<DateOnly> Today { set => _day.Today = value; }

    // On a short panel the lines sit closer together, so the form fits under the status line's place without scrolling.
    public bool Tight
    {
        set
        {
            double gap = value ? 4 : 6;
            _head.LineGap = gap;
            _grid.Margin = new Thickness(0, value ? 6 : 8, 0, 0);
            _grid.RowDefinitions[1].Height = new GridLength(value ? 6 : 8);
            _day.Margin = new Thickness(0, 0, 8, gap);
            _times.Margin = new Thickness(0, 0, 0, gap);
            _allDayRow.Margin = new Thickness(12, 0, 0, gap);
            _when.Margin = new Thickness(0, 0, 0, -gap);
            _head.InvalidateMeasure();
        }
    }
    public bool PopupOpen => _day.Popup.IsOpen;
    public void ClosePopups() => _day.Popup.Close();

    // The values to start from: the words (an event's title, a to-do's text), the times of an event, the day of a to-do. A timed event
    // ending at 00:00 ends at 24:00 of the day before.
    public void Fill(string text, DateTime start, DateTime end, bool allDay, DateOnly day)
    {
        _title.Text = text; _words.Text = text;
        _day.Date = _todo ? day : DateOnly.FromDateTime(start);
        _start.Set(start.TimeOfDay);
        DateTime lastDay;
        if (allDay) { lastDay = end.Date; var timed = start.AddHours(1); _end.Set(timed.Date > start.Date ? TimeSpan.FromHours(24) : timed.TimeOfDay); }
        else if (end.TimeOfDay == TimeSpan.Zero && end > start) { lastDay = end.Date.AddDays(-1); _end.Set(TimeSpan.FromHours(24)); }
        else { lastDay = end.Date; _end.Set(end.TimeOfDay); }
        _spanDays = Math.Max(0, (lastDay - start.Date).Days);
        _allDay.IsChecked = allDay;
        AllDayChanged();
    }

    // What the form describes: a to-do (its words and day) or an event; null while it is neither.
    public (bool Todo, string Text, DateOnly Day, DateTime Start, DateTime End, bool AllDay)? Value
    {
        get
        {
            string text = (_todo ? _words : _title).Text.Trim();
            if (text.Length == 0) return null;
            var day = _day.Date;
            if (_todo) return (true, text, day, default, default, false);
            var last = AgendaTime.Midnight(day.AddDays(_spanDays));
            if (_allDay.IsChecked == true) return (false, text, day, AgendaTime.Midnight(day), last, true);
            if (_start.Value is not { } from || _end.Value is not { } to || from >= TimeSpan.FromHours(24)) return null;
            var start = AgendaTime.Midnight(day) + from;
            var end = last + to;
            return end > start ? (false, text, day, start, end, false) : null;
        }
    }

    // The words take the keyboard with their text selected.
    public void FocusText(Action<Control> begin)
    {
        var box = (_todo ? _words : _title).Box;
        begin(box);
        box.Dispatcher.BeginInvoke(() => box.SelectAll(), System.Windows.Threading.DispatcherPriority.Input);
    }

    // Switches between a to-do and an event; the words go along.
    private void Kind(bool todo)
    {
        if (todo != _todo) { if (todo) _words.Text = _title.Text; else _title.Text = _words.Text; }
        _todo = todo;
        _title.Visibility = todo ? Visibility.Collapsed : Visibility.Visible;
        _words.Visibility = todo ? Visibility.Visible : Visibility.Collapsed;
        _times.Visibility = _allDayRow.Visibility = todo ? Visibility.Collapsed : Visibility.Visible;
        _textLabel.Text = todo ? Loc.T("内容", "Text") : Loc.T("标题", "Title");
        _dayLabel.Text = todo ? Loc.T("日期", "Date") : Loc.T("时间", "Time");
        string submit = _existing ? Loc.T("保存", "Save") : todo ? Loc.T("加入待办", "Add to-do") : Loc.T("加入日程", "Add event");
        if (((Border)_submit.Content).Child is TextBlock label) label.Text = submit;
        AutomationProperties.SetName(_submit, submit);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Segment(Loc.T("待办", "To-do"), todo, () => Kind(true)));
        row.Children.Add(Segment(Loc.T("日程", "Event"), !todo, () => Kind(false)));
        _segments.Child = row;
        Check();
    }

    // One half of the switch, as the notices list's "全部"/"未读": 18 high, the chosen one on the selected fill.
    private Button Segment(string text, bool on, Action click)
    {
        var label = AgendaDraw.Words(text, 11, 18, on ? _look.Primary : _look.Secondary);
        label.VerticalAlignment = VerticalAlignment.Center;
        string rest = on ? _look.FillSelected : "Transparent";
        var face = new Border { Height = 18, CornerRadius = new CornerRadius(_look.Corner(5)), Padding = new Thickness(9, 0, 9, 0), Background = UI.Brush(rest), Child = label };
        var button = AgendaDraw.Bare(face, text, () => { if (!on) click(); }, (over, down) =>
            face.Background = UI.Brush(down ? _look.Hex("pressed") : on ? _look.FillSelected : over ? _look.Hex("hover") : "Transparent"));
        if (on) AutomationProperties.SetItemStatus(button, Loc.T("已选中", "Selected"));
        return button;
    }

    private void AllDayChanged()
    {
        bool allDay = _allDay.IsChecked == true;
        _start.IsEnabledField = !allDay; _end.IsEnabledField = !allDay;
        Check();
    }

    private void Check() => _submit.IsEnabled = Value is not null;

    private void Submit() { if (Value is not null) Submitted?.Invoke(); }
}
