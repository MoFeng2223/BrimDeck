using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;
using BrimDeck.Core.Notices;
using ShapePath = System.Windows.Shapes.Path;

namespace BrimDeck;

// The notices page's way into the planner (NoticeAgenda): a strip between the focus's words and its buttons with the times read
// from the notice ("加入日程"), what it already added ("查看", "撤销") or its verification code ("复制验证码"); a round button that
// makes a notice of plain words today's to-do; and the colored words at the start of a row's second line in the list.
public partial class MainWindow
{
    // Whether the page offers to add notices to the planner. The verification code does not depend on it.
    // TODO(merge): 合并计划页分支时改为“计划页是否开启”（设置里关掉计划页时不提供这些入口）。
    private bool AgendaOffered => _app.Agenda is not null;

    // Below this width the strip takes its short form: the time in a few words, "加入" for "加入日程", no second line.
    private const double CompactStripWidth = 300;

    // What NoticeAgenda read from each notice, by its revision: the page is built again every second while a time moves, and a
    // notice is read only once. Its words are only held here in memory.
    private readonly Dictionary<string, (int Revision, NoticeAgendaRead Read)> _agendaReads = new(StringComparer.Ordinal);
    private const int AgendaReadsKept = 512;
    // The items added from each notice, as of the book's revision.
    private Dictionary<string, AgendaFromNotice>? _agendaAdded;
    private int _agendaAddedRevision = -1;
    private bool _agendaSubscribed, _agendaRenderQueued;

    // What the page offers for a notice at a moment: its read with the past times dropped, the times not added yet, what it added,
    // and its verification code (hidden on the page, so it can be copied from the strip).
    private sealed record NoticeOffer(NoticeAgendaRead Open, IReadOnlyList<NoticeAgendaTime> NotAdded, AgendaFromNotice? Added, VerificationCodeHit? Code)
    {
        public bool Todo => Open.Kind == NoticeAgendaKind.Text && Added is not { Any: true };
    }

    private void SubscribeAgenda()
    {
        if (_agendaSubscribed || _app.Agenda is null) return;
        _agendaSubscribed = true;
        _app.Agenda.Changed += NoticeAgendaChanged;
    }

    private void UnsubscribeAgenda()
    {
        if (_agendaSubscribed) _app.Agenda.Changed -= NoticeAgendaChanged;
        _agendaSubscribed = false;
        _agendaReads.Clear(); _agendaAdded = null;
    }

    // An item added or removed here, on another panel or on the planner page: the page on screen follows (a strip goes back to
    // "加入日程" once its event is deleted elsewhere). Several changes in a row are drawn once.
    private void NoticeAgendaChanged()
    {
        if (!_expanded || CurrentPage != DeckPage.Notices || _agendaRenderQueued) return;
        _agendaRenderQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _agendaRenderQueued = false;
            if (_expanded && CurrentPage == DeckPage.Notices && !UpdateNoticeRows()) RenderNotices();
        });
    }

    private NoticeAgendaRead AgendaRead(Notice notice)
    {
        if (_agendaReads.TryGetValue(notice.Id, out var kept) && kept.Revision == notice.Revision) return kept.Read;
        if (_agendaReads.Count >= AgendaReadsKept) _agendaReads.Clear();
        var read = NoticeAgenda.Read(notice, TimeZoneInfo.Local);
        _agendaReads[notice.Id] = (notice.Revision, read);
        return read;
    }

    private AgendaFromNotice AddedFrom(string noticeId)
    {
        var book = _app.Agenda.Book;
        if (_agendaAdded is null || _agendaAddedRevision != book.Revision)
        {
            var events = book.Events.Where(item => item.Source is not null).ToLookup(item => item.Source!.NoticeId);
            var todos = book.Todos.Where(item => item.Source is not null).ToLookup(item => item.Source!.NoticeId);
            _agendaAdded = events.Select(group => group.Key).Union(todos.Select(group => group.Key))
                .ToDictionary(id => id, id => new AgendaFromNotice(events[id].ToList(), todos[id].ToList()), StringComparer.Ordinal);
            _agendaAddedRevision = book.Revision;
        }
        return _agendaAdded.TryGetValue(noticeId, out var added) ? added : new AgendaFromNotice([], []);
    }

    private NoticeOffer OfferOf(Notice notice, DateTime now)
    {
        var code = NoticeCodes.Words(notice, Settings.NoticeCodes).Hit;
        var read = AgendaRead(notice);
        if (!AgendaOffered || read.Kind is NoticeAgendaKind.None or NoticeAgendaKind.Code) return new(NoticeAgendaRead.None, [], null, code);
        var open = NoticeAgenda.Open(read, now);
        var added = AddedFrom(notice.Id);
        return new(open, open.Kind == NoticeAgendaKind.Times ? NoticeAgenda.NotAdded(open.Times, added) : [], added, code);
    }

    // What the strip, the to-do button and the row's mark show for the notice, for FocusKey and RowKey.
    private string AgendaKey(Notice notice)
    {
        var offer = OfferOf(notice, DateTime.Now);
        var (item, todo) = offer.Added is { } added ? NoticeAgenda.Latest(added) : (null, null);
        return string.Join('\u0002', offer.Code is not null, offer.Open.Kind, offer.Todo,
            string.Join(',', offer.Open.Times.Select(time => time.Moment.Start.Ticks + (offer.Added is { } from && NoticeAgenda.Added(time.Moment, from) is not null ? "+" : ""))),
            offer.NotAdded.FirstOrDefault()?.Moment.Start.Ticks, item?.Id, item?.Title, item?.Start, item?.End, item?.AllDay, todo?.Id, todo?.Day);
    }

    // ----- The strip -----

    // Between the words and the buttons; null when there is nothing to offer.
    private FrameworkElement? NoticeAgendaStrip(Notice notice)
    {
        var now = DateTime.Now;
        var offer = OfferOf(notice, now);
        if (offer.Code is { } code) return CodeStrip(code);
        if (offer.NotAdded is [var first, ..]) return TimesStrip(notice, offer, first, now);
        if (offer.Added is { Any: true } added) return AddedStrip(notice, offer, added, now);
        return null;
    }

    // The times read from the notice: the first not added yet, "+N" for the others (a menu that adds or removes each), "加入日程".
    private FrameworkElement TimesStrip(Notice notice, NoticeOffer offer, NoticeAgendaTime time, DateTime now)
    {
        var (frame, texts, actions) = StripFrame(StripGlyph(CalendarIcon, 16, UI.Accent));
        string label = NoticeMoments.Label(time.Moment, now.Date), brief = NoticeMoments.Brief(time.Moment, now.Date);
        var (line, words) = StripTitle(label, offer.Open.Times.Count > 1 ? TimesMenuButton(notice, offer) : null);
        texts.Children.Add(line);
        var detail = StripDetail(time.Detail);
        texts.Children.Add(detail);
        string full = Loc.T("加入日程", "Add event"), shortName = Loc.T("加入", "Add");
        var add = StripButton(full, Loc.T($"加入日程：{label}", $"Add event: {label}"), () => AddTime(notice, time));
        actions.Children.Add(add);
        FitStrip(frame, compact =>
        {
            words.Text = compact ? brief : label;
            detail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            SetStripButtonText(add, compact ? shortName : full, compact);
        });
        return frame;
    }

    // What the notice added last: "已加入日程" with its time and title, or "已加入今天的待办"; "查看" shows it in the planner,
    // "撤销" removes it (and the strip then shows the one added before it, or the times again).
    private FrameworkElement AddedStrip(Notice notice, NoticeOffer offer, AgendaFromNotice added, DateTime now)
    {
        var (frame, texts, actions) = StripFrame(StripGlyph(CheckIcon, 16, CallColor));
        var today = DateOnly.FromDateTime(now);
        var (item, todo) = NoticeAgenda.Latest(added);
        string title;
        string? second = null;
        DateOnly day;
        Action undo;
        if (item is not null)
        {
            title = Loc.T("已加入日程", "Added as an event");
            var moment = new NoticeMoment(item.Start, item.AllDay ? (item.End.Date > item.Start.Date ? item.End : null) : item.End, item.AllDay, false, 0, 0, "");
            second = NoticeMoments.Label(moment, now.Date) + " · " + item.Title;
            day = item.FirstDay;
            undo = () => _app.Agenda.Book.RemoveEvent(item.Id);
        }
        else
        {
            title = todo!.Day is not { } on ? Loc.T("已加入长期待办", "Added to someday to-dos")
                : on == today ? Loc.T("已加入今天的待办", "Added to today's to-dos")
                : Loc.T($"已加入{AgendaText.Date(on, today.Year)}的待办", $"Added to the to-dos of {AgendaText.Date(on, today.Year)}");
            day = todo.Day ?? today;
            undo = () => _app.Agenda.Book.RemoveTodo(todo.Id);
        }
        var (line, words) = StripTitle(title, offer.Open.Times.Count > 1 ? TimesMenuButton(notice, offer) : null);
        texts.Children.Add(line);
        TextBlock? detail = null;
        if (second is not null) texts.Children.Add(detail = StripDetail(second));
        string id = item?.Id ?? todo!.Id;
        var view = item is not null ? AgendaView.Events : AgendaView.Todos;
        actions.Children.Add(StripLink(Loc.T("查看", "View"), Loc.T("在计划页查看", "View in the planner"), () => _app.Agenda.Reveal(day, view, id, this)));
        actions.Children.Add(StripLink(Loc.T("撤销", "Undo"), item is not null ? Loc.T("撤销加入日程", "Remove the added event") : Loc.T("撤销加入待办", "Remove the added to-do"), undo));
        // Narrow, "已加入" alone leaves room for both words that act.
        FitStrip(frame, compact =>
        {
            words.Text = compact ? Loc.T("已加入", "Added") : title;
            detail?.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        });
        return frame;
    }

    // The code, hidden, and how long it stays on the clipboard once copied (CodeClipboardGuard); a click copies it.
    private FrameworkElement CodeStrip(VerificationCodeHit code)
    {
        var (frame, texts, actions) = StripFrame(StripGlyph(KeyIcon, 16, UI.Accent, keyHole: true));
        var line = StripLine(12.5, 18, UI.Primary, FontWeights.SemiBold);
        line.Inlines.Add(new Run(Loc.T("验证码", "Code")));
        int dots = Math.Clamp(code.Code.Count(char.IsLetterOrDigit), 4, 8);
        var hidden = new Run(" " + new string('•', dots)) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontWeight = FontWeights.Normal };
        line.Inlines.Add(hidden);
        AutomationProperties.SetName(line, Loc.T("验证码（已隐藏）", "Verification code (hidden)"));
        texts.Children.Add(line);
        double seconds = CodeClipboardGuard.ClearAfter.TotalSeconds;
        var detail = StripDetail(Loc.T($"复制后 {seconds:0} 秒从剪贴板清除", $"Cleared from the clipboard {seconds:0} s after copying"));
        texts.Children.Add(detail);
        string full = Loc.T("复制验证码", "Copy code"), shortName = Loc.T("复制", "Copy");
        bool compact = false;
        Button? copy = null;
        // Only a click puts the code on the clipboard; the name says what the button does, never the code.
        copy = StripButton(full, full, () => CopyCode(copy!, code.CopyText, compact ? shortName : full));
        actions.Children.Add(copy);
        FitStrip(frame, narrow =>
        {
            compact = narrow;
            // Narrow, the dots give way to the button's word.
            hidden.Text = narrow ? "" : " " + new string('•', dots);
            detail.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            SetStripButtonText(copy, narrow ? shortName : full, narrow);
        });
        return frame;
    }

    // "+N": the other times, in a menu where each one is checked once added; a click adds it, or removes what it added.
    private Button TimesMenuButton(Notice notice, NoticeOffer offer)
    {
        int others = offer.Open.Times.Count - 1;
        string name = Loc.T($"另外 {others} 个时间", Loc.Count(others, "more time", "more times"));
        var face = new Border { CornerRadius = new CornerRadius(PanelTheme.Corner(4)), Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(1, 0, 0, 0), Height = 18, Background = Brushes.Transparent };
        face.Child = UI.Fixed($"+{others}", 12.5, 18, UI.Tertiary);
        var button = new Button { Content = face, Cursor = Cursors.Hand, ToolTip = name, FocusVisualStyle = (Style)Application.Current.FindResource("PanelKeyboardFocus"), Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) } };
        Feel(button, (over, down) => face.Background = down ? UI.Brush(PanelTheme.Hex("pressed")) : over ? UI.Brush(PanelTheme.Hex("hover")) : Brushes.Transparent);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) =>
        {
            var menu = NewMenu();
            var today = DateTime.Today;
            var added = AddedFrom(notice.Id);
            foreach (var time in offer.Open.Times)
            {
                var item = NoticeAgenda.Added(time.Moment, added);
                menu.Items.Add(Item(NoticeMoments.Label(time.Moment, today), () => { if (item is null) AddTime(notice, time); else _app.Agenda.Book.RemoveEvent(item.Id); }, check: item is not null));
            }
            OpenMenu(menu, button);
        };
        return button;
    }

    private void AddTime(Notice notice, NoticeAgendaTime time)
    {
        var moment = time.Moment;
        _app.Agenda.Book.AddEvent(NoticeAgenda.EventTitle(notice, time), moment.Start, moment.End, moment.AllDay, DateTime.Now, source: AgendaSource.From(notice));
    }

    // ----- The to-do button -----

    // After delete and copy in the button row: a notice of plain words, not added yet, becomes today's to-do.
    private Button? NoticeTodoButton(Notice notice)
    {
        if (!OfferOf(notice, DateTime.Now).Todo || NoticeAgenda.TodoText(notice) is not { Length: > 0 } text) return null;
        string name = Loc.T("加入待办", "Add to to-dos");
        var button = PillButton("", name, () => _app.Agenda.Book.AddTodo(text, DateOnly.FromDateTime(DateTime.Today), DateTime.Now, source: AgendaSource.From(notice)));
        var face = (Border)button.Content;
        var glyph = StripGlyph(CalendarAddIcon, 15, UI.Primary);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        face.Child = glyph; face.Padding = new Thickness(0); button.Width = 32; button.ToolTip = name;
        return button;
    }

    // ----- The list -----

    // The colored words that open a row's second line, and plain words after them (the time of an added event): "验证码", a time
    // read ("周四 15:00"), "已加入日程", "已加入待办". Null when there is nothing to say.
    private (string Mark, string Color, string? Then)? NoticeAgendaMark(Notice notice, DateTime now)
    {
        var offer = OfferOf(notice, now);
        if (offer.Code is not null) return (Loc.T("验证码", "Code"), UI.Accent, null);
        if (offer.NotAdded is [var first, ..]) return (NoticeMoments.Brief(first.Moment, now.Date), UI.Accent, null);
        if (offer.Added is not { Any: true } added) return null;
        var (item, _) = NoticeAgenda.Latest(added);
        if (item is null) return (Loc.T("已加入待办", "To-do added"), CallColor, null);
        var moment = new NoticeMoment(item.Start, null, item.AllDay, false, 0, 0, "");
        return (Loc.T("已加入日程", "Event added"), CallColor, NoticeMoments.Brief(moment, now.Date));
    }

    private void AddNoticeAgendaMark(TextBlock line, Notice notice, DateTimeOffset now)
    {
        if (NoticeAgendaMark(notice, now.ToLocalTime().DateTime) is not var (mark, color, then)) return;
        line.Inlines.Add(new Run(mark + " · ") { Foreground = UI.Brush(color) });
        if (then is not null) line.Inlines.Add(new Run(then + " · "));
    }

    private string NoticeAgendaRowKey(Notice notice, DateTimeOffset now)
        => NoticeAgendaMark(notice, now.ToLocalTime().DateTime) is var (mark, _, then) ? mark + "\u0002" + then : "";

    // ----- Parts of the strip -----

    // The strip's frame: an icon, the words (one or two lines) and its buttons on the right.
    private static (Border Frame, StackPanel Texts, StackPanel Actions) StripFrame(FrameworkElement icon)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        icon.VerticalAlignment = VerticalAlignment.Center; icon.Margin = new Thickness(0, 0, 10, 0);
        grid.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(texts, 1); grid.Children.Add(texts);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(actions, 2); grid.Children.Add(actions);
        var frame = new Border { Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 7, 6, 7), CornerRadius = new CornerRadius(PanelTheme.Corner(8)), Background = UI.Brush(UI.Fill), Child = grid };
        return (frame, texts, actions);
    }

    // The strip's first line, cut short where it must, with "+N" right after its words rather than at the far end.
    private static (FrameworkElement Line, TextBlock Words) StripTitle(string text, Button? more)
    {
        var words = StripLine(12.5, 18, UI.Primary, FontWeights.SemiBold);
        words.Text = text;
        if (more is null) return (words, words);
        // Left-aligned, the star column is as wide as the words when they fit and gives way to "+N" when they do not.
        var line = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(words);
        Grid.SetColumn(more, 1); line.Children.Add(more);
        return (line, words);
    }

    private static TextBlock StripDetail(string text)
    {
        var detail = StripLine(11.5, 16, UI.Secondary, FontWeights.Normal);
        detail.Text = text;
        return detail;
    }

    private static TextBlock StripLine(double size, double height, string color, FontWeight weight) => IslandNotice.Directed(new TextBlock
    {
        FontFamily = UI.PanelFont, FontSize = size, LineHeight = height, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Height = height, FontWeight = weight,
        Foreground = UI.Brush(color), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
    });

    // fit(compact) whenever the strip's width crosses CompactStripWidth (the narrowest page, or a panel being made narrower).
    private static void FitStrip(FrameworkElement frame, Action<bool> fit)
    {
        bool? shown = null;
        frame.SizeChanged += (_, e) =>
        {
            bool compact = e.NewSize.Width < CompactStripWidth;
            if (shown == compact) return;
            shown = compact; fit(compact);
        };
    }

    // A small grey capsule button on the strip: 24 high, its words in the face (CopyCode writes into it).
    private static Button StripButton(string text, string name, Action click)
    {
        var face = new Border { CornerRadius = new CornerRadius(PanelTheme.Corner(12)), Background = UI.Brush(UI.FillSelected), Padding = new Thickness(12, 0, 12, 0), Height = 24 };
        face.Child = UI.InkCentered(text, 12, 24, UI.Primary, FontWeights.SemiBold);
        var button = new Button { Content = face, Cursor = Cursors.Hand, FocusVisualStyle = (Style)Application.Current.FindResource("PanelPillKeyboardFocus"), Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) } };
        // On the strip's fill the button is the selected fill; under the pointer and pressed the hover and pressed fills lie over it.
        Feel(button, (over, down) => face.Background = UI.Brush(down ? Stacked("pressed", "fillSelected") : over ? Stacked("hover", "fillSelected") : UI.FillSelected));
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, name);
        return button;
    }

    // compact: the short word on a narrow strip, with a little less room around it.
    private static void SetStripButtonText(Button button, string text, bool compact)
    {
        var face = (Border)button.Content;
        face.Padding = new Thickness(compact ? 10 : 12, 0, compact ? 10 : 12, 0);
        if (face.Child is TextBlock block) block.Text = text;
    }

    // A word on the strip that acts ("查看", "撤销"): no fill until the pointer is on it.
    private static Button StripLink(string text, string name, Action click)
    {
        var face = new Border { CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Padding = new Thickness(6, 2, 6, 2), Background = Brushes.Transparent };
        face.Child = UI.Fixed(text, 11.5, 16, UI.Secondary);
        var button = new Button { Content = face, Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0), FocusVisualStyle = (Style)Application.Current.FindResource("PanelKeyboardFocus"), Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) } };
        Feel(button, (over, down) => face.Background = down ? UI.Brush(PanelTheme.Hex("pressed")) : over ? UI.Brush(PanelTheme.Hex("hover")) : Brushes.Transparent);
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, name);
        return button;
    }

    // The theme's color top laid over its color bottom, both possibly translucent: the result keeps what shows through both.
    private static string Stacked(string top, string bottom)
    {
        var (a, b) = (PanelTheme.Current[top], PanelTheme.Current[bottom]);
        double ta = a.A / 255d, ba = b.A / 255d, alpha = ta + ba * (1 - ta);
        if (alpha <= 0) return "#00000000";
        byte Mix(byte t, byte u) => (byte)Math.Round((t * ta + u * ba * (1 - ta)) / alpha);
        return $"#{(byte)Math.Round(alpha * 255):X2}{Mix(a.R, b.R):X2}{Mix(a.G, b.G):X2}{Mix(a.B, b.B):X2}";
    }

    // Line icons drawn on a 24 box (as the design's): a calendar, a calendar with a plus, a check, a key.
    private const string CalendarIcon = "M 8,2 V 6 M 16,2 V 6 M 5,4 H 19 A 2,2 0 0 1 21,6 V 20 A 2,2 0 0 1 19,22 H 5 A 2,2 0 0 1 3,20 V 6 A 2,2 0 0 1 5,4 Z M 3,10 H 21";
    private const string CalendarAddIcon = "M 8,2 V 6 M 16,2 V 6 M 21,13 V 6 A 2,2 0 0 0 19,4 H 5 A 2,2 0 0 0 3,6 V 20 A 2,2 0 0 0 5,22 H 13 M 3,10 H 21 M 16,19 H 22 M 19,16 V 22";
    private const string CheckIcon = "M 20,6 L 9,17 L 4,12";
    private const string KeyIcon = "M 2.586,17.414 A 2,2 0 0 0 2,18.828 V 21 A 1,1 0 0 0 3,22 H 6 A 1,1 0 0 0 7,21 V 20 A 1,1 0 0 1 8,19 H 9 A 1,1 0 0 0 10,18 V 17 A 1,1 0 0 1 11,16 H 11.172 A 2,2 0 0 0 12.586,15.414 L 13.4,14.6 A 6.5,6.5 0 1 0 9.4,10.6 Z";

    // The icon drawn at its place in the 24 box and the box scaled to size, so it is centred by the box, never by its own bounds.
    private static FrameworkElement StripGlyph(string data, double size, string color, bool keyHole = false)
    {
        var brush = UI.Brush(color);
        var box = new Grid { Width = 24, Height = 24, LayoutTransform = new ScaleTransform(size / 24, size / 24) };
        box.Children.Add(new ShapePath
        {
            Data = Geometry.Parse(data), Stroke = brush, StrokeThickness = 1.65, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.None
        });
        if (keyHole) box.Children.Add(new ShapePath { Data = new EllipseGeometry(new Point(16.5, 7.5), .6, .6), Fill = brush, Stroke = brush, StrokeThickness = 1.65, Stretch = Stretch.None });
        return box;
    }
}
