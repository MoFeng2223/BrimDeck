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
        if (item is not null) ShowNoticeForm(notice, item.Id, null, false, item.Title, item.Start, item.End, item.AllDay, item.FirstDay, typing, item.Repeat);
        else if (todo is not null)
        {
            var day = todo.Day ?? today;
            var start = AgendaCalendar.NewEventStart(day, DateTime.Now);
            ShowNoticeForm(notice, null, todo.Id, true, todo.Text, start, start.AddHours(1), false, day, typing);
        }
    }

    private void ShowNoticeForm(Notice notice, string? eventId, string? todoId, bool asTodo, string text, DateTime start, DateTime end, bool allDay, DateOnly day, bool typing)
        => ShowNoticeForm(notice, eventId, todoId, asTodo, text, start, end, allDay, day, typing, AgendaRepeat.None);

    // repeat: how the event changed repeats (a series is shown by its first time, and a change applies to every time of it).
    private void ShowNoticeForm(Notice notice, string? eventId, string? todoId, bool asTodo, string text, DateTime start, DateTime end, bool allDay, DateOnly day, bool typing, AgendaRepeat repeat)
    {
        CloseNoticeForm();
        bool existing = eventId is not null || todoId is not null;
        var form = new NoticeAgendaForm(Look, AgendaKeyboard, existing, asTodo) { Today = () => DateOnly.FromDateTime(DateTime.Now) };
        form.Fill(text, start, end, allDay, day, repeat);
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
            else book.UpdateEvent(eventId, value.Text, value.Start, value.End, value.AllDay, item.Detail, value.Repeat);
        }
        else if (state.TodoId is { } todoId && book.FindTodo(todoId) is not null)
        {
            if (value.Todo) { book.SetTodoText(todoId, value.Text); book.SetTodoDay(todoId, value.Day); }
            else if (book.TodoToEvent(todoId, value.Start, value.End, value.AllDay, repeat: value.Repeat) is { } made) book.UpdateEvent(made.Id, value.Text, value.Start, value.End, value.AllDay, null, value.Repeat);
        }
        else if (notice is not null)
        {
            // New, or what it was to change is gone meanwhile (removed on the planner page): added afresh from the notice.
            var source = AgendaSource.From(notice);
            if (value.Todo) book.AddTodo(value.Text, value.Day, now, source: source);
            else book.AddEvent(value.Text, value.Start, value.End, value.AllDay, now, source: source, repeat: value.Repeat);
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

    // A press on the panel outside the form while typing in it: the form stays, the keyboard goes back. A press in the form but not
    // in one of its boxes takes the keyboard from the box and leaves it with the panel, so the form and the panel stay open.
    private void NoticeFormPressedAt(object? source)
    {
        if (_noticeForm is not { } state || !state.Form.IsKeyboardFocusWithin || source is not DependencyObject pressed) return;
        bool inForm = false;
        for (var node = pressed; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is TextBoxBase) return;
            if (ReferenceEquals(node, state.Form)) { inForm = true; break; }
        }
        if (inForm) { Keyboard.ClearFocus(); return; }
        AgendaEndTyping();
        Keyboard.ClearFocus();
    }
}

// The notices page's form. An event takes the planner's own event form (AgendaEventForm, compact): its title, its day and times,
// "全天", how it repeats and the buttons. A to-do takes the same shape: its words, then its day with the buttons. "待办"/"日程" is
// before the title or the words (above them on a narrow column), so it stays in its place when switching; the words and the day go
// along.
internal sealed class NoticeAgendaForm : StackPanel
{
    private readonly PanelLook _look;
    private readonly Border _eventSwitch, _todoSwitch;
    private readonly StackPanel _todoForm;
    private readonly AgendaEventForm _event;
    private readonly AgendaTextField _words;
    private readonly AgendaDateField _day;
    private readonly Button _todoSubmit;
    private bool _todo;
    public event Action? Submitted, Cancelled;

    public NoticeAgendaForm(PanelLook look, Action<TextBox> keyboard, bool existing, bool todo)
    {
        _look = look; _todo = todo;
        _eventSwitch = Switch(); _todoSwitch = Switch();

        _event = new AgendaEventForm(look, keyboard, existing ? Loc.T("保存", "Save") : Loc.T("加入日程", "Add event"), showStartDay: true, compact: true) { Lead = _eventSwitch };
        _event.Submitted += Submit;
        _event.Cancelled += () => Cancelled?.Invoke();
        Children.Add(_event);

        _words = AgendaFields.Text(look, keyboard, Loc.T("待办内容", "To-do"));
        _day = AgendaFields.Date(look, Loc.T("日期", "Date"));
        _day.VerticalAlignment = VerticalAlignment.Center;
        var cancel = AgendaDraw.Pill(look, Loc.T("取消", "Cancel"), () => Cancelled?.Invoke());
        cancel.Margin = new Thickness(0, 0, 6, 0);
        _todoSubmit = AgendaDraw.Pill(look, existing ? Loc.T("保存", "Save") : Loc.T("加入待办", "Add to-do"), Submit);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { cancel, _todoSubmit } };
        var foot = new AgendaSplitRow { Margin = new Thickness(0, 6, 0, 0), Children = { _day, buttons } };
        _todoForm = new StackPanel { Children = { new AgendaLeadRow { Children = { _todoSwitch, _words } }, foot } };
        Children.Add(_todoForm);

        _words.Changed += CheckTodo;
        _words.Box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        Kind(todo);
    }

    public Func<DateOnly> Today { set { _event.Today = value; _day.Today = value; } }

    public bool PopupOpen => _event.PopupOpen || _day.Popup.IsOpen;
    public void ClosePopups() { _event.ClosePopups(); _day.Popup.Close(); }

    // The values to start from: the words (an event's title, a to-do's text), the times of an event and how it repeats, the day of a
    // to-do.
    public void Fill(string text, DateTime start, DateTime end, bool allDay, DateOnly day, AgendaRepeat repeat)
    {
        _event.Fill(text, start, end, allDay, repeat);
        _words.Text = text;
        _day.Date = day;
        CheckTodo();
    }

    // What the form describes: a to-do (its words and day) or an event (its times and how it repeats); null while it is neither.
    public (bool Todo, string Text, DateOnly Day, DateTime Start, DateTime End, bool AllDay, AgendaRepeat Repeat)? Value
    {
        get
        {
            if (_todo)
            {
                string words = _words.Text.Trim();
                return words.Length == 0 ? null : (true, words, _day.Date, default, default, false, AgendaRepeat.None);
            }
            if (_event.Value is not { } item) return null;
            return (false, item.Title, DateOnly.FromDateTime(item.Start), item.Start, item.End, item.AllDay, _event.Repeat.Repeat);
        }
    }

    // The words take the keyboard with their text selected.
    public void FocusText(Action<Control> begin)
    {
        if (!_todo) { _event.FocusTitle(begin); return; }
        begin(_words.Box);
        _words.Box.Dispatcher.BeginInvoke(() => _words.Box.SelectAll(), System.Windows.Threading.DispatcherPriority.Input);
    }

    // Switches between a to-do and an event; the words and the day go along, an event keeping the number of days it runs over.
    private void Kind(bool todo)
    {
        if (todo != _todo)
        {
            if (todo) { _words.Text = _event.Title.Text; _day.Date = _event.StartDay.Date; }
            else
            {
                _event.Title.Text = _words.Text;
                _event.StartDay.Date = _day.Date;
            }
        }
        _todo = todo;
        _event.Visibility = todo ? Visibility.Collapsed : Visibility.Visible;
        _todoForm.Visibility = todo ? Visibility.Visible : Visibility.Collapsed;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Segment(Loc.T("待办", "To-do"), todo, () => Kind(true)));
        row.Children.Add(Segment(Loc.T("日程", "Event"), !todo, () => Kind(false)));
        (todo ? _todoSwitch : _eventSwitch).Child = row;
        (todo ? _eventSwitch : _todoSwitch).Child = null;
        CheckTodo();
    }

    // The switch's track: the fill, with a pixel around its two halves.
    private Border Switch() => new()
    {
        CornerRadius = new CornerRadius(_look.Corner(6)), Background = UI.Brush(_look.Fill), Padding = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
    };

    // One half of the switch, as the notices list's "全部"/"未读": 18 high, the chosen one on the selected fill. The words are centred
    // by their letters as drawn (InkCentredText): centred by their line, Chinese sat low.
    private Button Segment(string text, bool on, Action click)
    {
        var label = AgendaDraw.Label(text, 11, on ? _look.Primary : _look.Secondary);
        string rest = on ? _look.FillSelected : "Transparent";
        var face = new Border { Height = 18, CornerRadius = new CornerRadius(_look.Corner(5)), Padding = new Thickness(9, 0, 9, 0), Background = UI.Brush(rest), Child = label };
        label.FitHeight(face, 18, Loc.T("待办", "To-do"), Loc.T("日程", "Event"));
        var button = AgendaDraw.Bare(face, text, () => { if (!on) click(); }, (over, down) =>
            face.Background = UI.Brush(down ? _look.Hex("pressed") : on ? _look.FillSelected : over ? _look.Hex("hover") : "Transparent"));
        if (on) AutomationProperties.SetItemStatus(button, Loc.T("已选中", "Selected"));
        return button;
    }

    private void CheckTodo() => _todoSubmit.IsEnabled = _words.Text.Trim().Length > 0;

    private void Submit() { if (Value is not null) Submitted?.Invoke(); }
}
