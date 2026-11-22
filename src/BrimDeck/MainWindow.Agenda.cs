using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;

namespace BrimDeck;

// The planner page's place in the panel (PlannerPage draws it). It is drawn whenever the panel draws its page: on a switch, on
// opening, on the panel's minute tick (which moves "还剩 40 分钟", the "now" line and today), and when the shared data changes
// (App.Agenda.Changed); it draws nothing when none of what it shows changed, and it runs no timer of its own while it does not show.
// "查看" on a notices page brings this panel's planner to the item (App.Agenda.Reveal).
public partial class MainWindow
{
    private PlannerPage? _agenda;
    private bool _agendaQueued;
    private AgendaReveal? _agendaReveal;
    private Func<DateTime>? _agendaClock;
    // Icons of the notices the page's items came from that were not loaded yet when it was drawn.
    private readonly HashSet<string> _agendaIcons = new(StringComparer.Ordinal);

    // The time the page reads; the README renderer sets a fixed one.
    internal Func<DateTime>? AgendaClock
    {
        get => _agendaClock;
        set { _agendaClock = value; if (_agenda is not null) _agenda.Clock = value ?? (() => DateTime.Now); }
    }

    internal AgendaCenter AgendaData => _app.Agenda;

    private void InitializeAgenda()
    {
        if (_app.Agenda is not { } agenda) return;
        agenda.Changed += AgendaChanged;
        agenda.RevealRequested += AgendaRevealRequested;
        if (_app.Notices?.Icons is { } icons) icons.Loaded += AgendaIconLoaded;
        // A press elsewhere on the panel lets go of a box of the page (PlannerPage.PressedAt); going to another window keeps what
        // was typed.
        PreviewMouseLeftButtonDown += (_, e) => { if (CurrentPage == DeckPage.Agenda && !PanelKeys.Holding) _agenda?.PressedAt(e.OriginalSource); };
        Deactivated += (_, _) => _agenda?.Flush();
        Closed += (_, _) =>
        {
            agenda.Changed -= AgendaChanged; agenda.RevealRequested -= AgendaRevealRequested;
            if (_app.Notices?.Icons is { } loaded) loaded.Loaded -= AgendaIconLoaded;
            _agenda?.Closed();
        };
    }

    // RenderUsage on the planner page.
    private void RenderAgendaPage()
    {
        if (_app.Agenda is null) return;
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed; ShelfContent.Visibility = Visibility.Collapsed;
        DevicesContent.Visibility = Visibility.Collapsed; NoticesContent.Visibility = Visibility.Collapsed; AgendaContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        ApplyPageHeader(PanelHeader.ForPanel(height));
        AgendaContent.Margin = new Thickness(_header.ContentSide, 0, _header.ContentSide, AgendaBottom);
        if (_agenda is null)
        {
            _agenda = new PlannerPage(this) { Clock = _agendaClock ?? (() => DateTime.Now) };
            AgendaContent.Children.Clear();
            AgendaContent.Children.Add(_agenda);
        }
        var reveal = _agendaReveal;
        _agendaReveal = null;
        if (reveal is null && _expanded && !_preview && _app.Agenda.PendingReveal is { } pending && (pending.From is null || ReferenceEquals(pending.From, this)))
            reveal = _app.Agenda.TakeReveal();
        _agenda.Render(width, width - 2 * _header.ContentSide, height - _header.Row - AgendaBottom);
        if (reveal is not null) _agenda.Reveal(reveal);
    }
    private const double AgendaBottom = 14;

    private void AgendaChanged()
    {
        if (!_expanded || CurrentPage != DeckPage.Agenda || _agenda is null || _agendaQueued) return;
        // Several changes in a row (a save state after a change) are drawn once.
        _agendaQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _agendaQueued = false;
            if (_expanded && CurrentPage == DeckPage.Agenda) RenderAgendaPage();
        });
    }

    // "查看" from a notices page: meant for this panel (or any), while it is open; another open panel may have taken it already.
    private void AgendaRevealRequested(AgendaReveal reveal)
    {
        if (!_expanded || _preview || reveal.From is not null && !ReferenceEquals(reveal.From, this) || !DeckPages.Shown(Settings, DeckPage.Agenda)) return;
        if (!ReferenceEquals(_app.Agenda.PendingReveal, reveal)) return;
        _app.Agenda.TakeReveal();
        _agendaReveal = reveal;
        if (CurrentPage != DeckPage.Agenda) SelectPage(DeckPage.Agenda);
        else RenderAgendaPage();
    }

    private void AgendaIconLoaded(string iconRef)
    {
        if (!_agendaIcons.Remove(iconRef) || !_expanded || CurrentPage != DeckPage.Agenda) return;
        _agenda?.Show(force: true);
    }

    // The page holds the panel open while a month of it is open, and while the user types in it.
    private bool AgendaHolds => _agenda is { } page && page.Holds(IsActive);

    // Esc on the planner page: a month, the form, a to-do's words and the typing go first (PlannerPage.Escape).
    private bool AgendaEscape() => _expanded && CurrentPage == DeckPage.Agenda && _agenda?.Escape() == true;

    // The page is left for another, or the panel closes.
    private void LeaveAgenda() => _agenda?.Flush();
    private void AgendaPanelClosed() => _agenda?.Closed();

    private void ReleaseAgenda()
    {
        _agenda?.Closed();
        _agenda = null;
        _agendaIcons.Clear();
    }

    // ---- What the page asks of the panel ----

    internal void AgendaKeyboard(TextBox box) => TakeKeyboardOnClick(box);
    internal void AgendaBeginTyping(Control box) => BeginTyping(box);
    internal void AgendaEndTyping() => EndTyping();
    internal void AgendaSettingsEdited() => _app.PanelSettingsEdited(this);

    // Whether the notice an item came from is still kept, and can be shown on the notices page.
    internal bool AgendaNoticeKept(string noticeId)
        => DeckPages.Shown(Settings, DeckPage.Notices) && _app.Notices?.Hub.Notices.Any(notice => notice.Id == noticeId) == true;

    internal void AgendaOpenNotice(string noticeId)
    {
        if (!AgendaNoticeKept(noticeId)) return;
        OpenNoticesPage(byKey: false, focus: noticeId);
    }

    // The icon of the application an item came from, as the notices page draws it; its letter until the icon is loaded.
    internal FrameworkElement AgendaSourceIcon(AgendaSource source, double size)
    {
        var plate = new Grid { Width = size, Height = size };
        Color? accent = null;
        if (_app.Notices?.Icons.Get(source.Icon, out accent) is { } image)
        {
            var picture = new System.Windows.Controls.Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            plate.Children.Add(picture);
            return plate;
        }
        if (source.Icon is { Length: > 0 } waiting) _agendaIcons.Add(waiting);
        plate.Children.Add(NoticeLetter.Plate(source.AppName, size, Look.Readable(accent ?? NoticeIcons.Fallback(source.AppKey))));
        return plate;
    }
}
