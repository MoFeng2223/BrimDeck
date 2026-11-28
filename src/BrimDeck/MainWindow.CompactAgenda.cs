using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;
using BrimDeck.Native;

namespace BrimDeck;

// The collapsed row's planner part (CompactPart.Agenda): today's event, to-dos and note, as the settings choose (AgendaStrip
// says which of them show, AgendaStripView draws them). It shows only while there is something; a pointer on it opens the
// planner page on today, switched to the kind under the pointer.
//
// The row is built again when what the part shows changes: with the planner's data (AgendaCenter.Changed) and with the clock, by
// one timer set for the next moment the clock alone changes it (AgendaStrip.NextChange): each minute while an event is under
// way or within the hour, else when the next event comes that near, and at midnight. While the panel is open nothing is built;
// closing it builds the row.
public partial class MainWindow
{
    private FrameworkElement? _compactAgenda;
    // Each kind's stretch along the part, for the page the panel opens on.
    private IReadOnlyList<(AgendaStripKind Kind, double Left, double Right)> _compactAgendaKinds = [];
    // The part runs down a side edge's column, so the kinds' stretches are along its height.
    private bool _compactAgendaDown;
    // What the part was last built from (AgendaStripKey): a change of it builds the collapsed row again.
    private string _agendaStripShown = "";
    private PromptTimer? _agendaStripClock;
    // The gliding text that has glided its laps, so it rests when the row is built again with the same text.
    private string? _agendaStripRested;

    private void InitializeAgendaStrip()
    {
        if (_app.Agenda is not { } agenda) return;
        agenda.Changed += AgendaStripChanged;
        _agendaStripClock = new PromptTimer(Dispatcher, AgendaStripTick);
        Closed += (_, _) => { agenda.Changed -= AgendaStripChanged; _agendaStripClock?.Stop(); };
    }

    private DateTime AgendaStripNow => (_agendaClock ?? (() => DateTime.Now))();

    // What the part has to show now and which kinds of it show; null when the part is off or has nothing.
    private (AgendaStripContent Content, IReadOnlyList<AgendaStripKind> Shown, DateTime Now)? AgendaStripContent()
    {
        if (_app.Agenda is not { } agenda || !Settings.ShowsAgenda(EffectiveStyle)) return null;
        var now = AgendaStripNow;
        var book = agenda.Book;
        var content = AgendaStrip.Content(book.Events, book.Todos, book.Notes, now);
        var shown = AgendaStrip.Shown(content, Settings.AgendaStripMode, Settings.AgendaStripEvents, Settings.AgendaStripTodos, Settings.AgendaStripNote);
        return shown.Count == 0 ? null : (content, shown, now);
    }

    // Everything the part draws, as text: the kinds in order, the event's words as they read now, the to-dos and the note.
    private string AgendaStripKey((AgendaStripContent Content, IReadOnlyList<AgendaStripKind> Shown, DateTime Now)? state)
    {
        if (state is not var (content, shown, now)) return "";
        var parts = new List<string> { Settings.AgendaStripEventForm.ToString(), Settings.AgendaStripTodoForm.ToString() };
        foreach (var kind in shown)
        {
            parts.Add(kind switch
            {
                AgendaStripKind.Event when content.Event is { } item => $"e:{item.State}:{AgendaStrip.Time(item, now)}:{Math.Round(item.Progress * 24)}:{item.Event.Title}",
                AgendaStripKind.Todo when content.Todos is { } todos => $"t:{todos.Open}:{todos.Overdue}:{todos.OverdueDays}:{todos.Text}",
                AgendaStripKind.Note => "n:" + content.Note,
                _ => ""
            });
        }
        return string.Join('\u001f', parts);
    }

    // The planner's data changed or the clock reached the part's next change.
    private void AgendaStripChanged()
    {
        if (_expanded) return;
        if (AgendaStripKey(AgendaStripContent()) != _agendaStripShown) RenderCompact();
        else ScheduleAgendaStrip();
    }

    private void AgendaStripTick()
    {
        _agendaStripClock?.Stop();
        // An open panel hides the row; it is built, and the clock set again, as the panel closes.
        if (!_expanded) AgendaStripChanged();
    }

    // Sets the one timer for the next moment the clock alone changes the part; none while the part is off.
    private void ScheduleAgendaStrip()
    {
        if (_agendaStripClock is not { } clock) return;
        clock.Stop();
        if (_app.Agenda is not { } agenda || !Settings.ShowsAgenda(EffectiveStyle) || !Settings.AgendaStripEvents && _agendaStripShown.Length == 0 && agenda.Book.Todos.Count == 0 && agenda.Book.Notes.Count == 0) return;
        var now = AgendaStripNow;
        var wait = AgendaStrip.NextChange(Settings.AgendaStripEvents ? agenda.Book.Events : [], now) - now;
        // A little past the moment, so the minute has turned when the row is built.
        clock.Interval = (wait < TimeSpan.Zero ? TimeSpan.Zero : wait) + TimeSpan.FromMilliseconds(200);
        clock.Start();
    }

    // The part for the row. level and slot: what it gives up on a surface that keeps its width (AgendaStripView).
    private FrameworkElement? CompactAgenda(double scale, int level = 0, double slot = 0)
    {
        var state = AgendaStripContent();
        _agendaStripShown = AgendaStripKey(state); _compactAgenda = null; _compactAgendaKinds = []; _compactAgendaDown = false;
        ScheduleAgendaStrip();
        if (state is not var (content, shown, now)) return null;
        var built = AgendaStripView.Row(content, shown, now, Settings.AgendaStripEventForm, Settings.AgendaStripTodoForm, level, slot, scale, VisualTreeHelper.GetDpi(this).DpiScaleX, Look, _agendaStripRested);
        if (built.Marquee is { } marquee)
        {
            if (_agendaStripRested != marquee.Text) _agendaStripRested = null;
            marquee.Finished += () => { if (ReferenceEquals(_compactAgenda, built.Element)) _agendaStripRested = marquee.Text; };
        }
        return AgendaStripPart(built, content, shown, now);
    }

    // The part for a side edge's column.
    private FrameworkElement? CompactAgendaColumn(double scale, double column)
    {
        var state = AgendaStripContent();
        _agendaStripShown = AgendaStripKey(state); _compactAgenda = null; _compactAgendaKinds = []; _compactAgendaDown = true; _agendaStripRested = null;
        ScheduleAgendaStrip();
        if (state is not var (content, shown, now)) return null;
        return AgendaStripPart(AgendaStripView.Column(content, shown, now, column, scale, VisualTreeHelper.GetDpi(this).DpiScaleX, Look), content, shown, now);
    }

    private FrameworkElement AgendaStripPart(AgendaStripView.Built built, AgendaStripContent content, IReadOnlyList<AgendaStripKind> shown, DateTime now)
    {
        string tip = AgendaStrip.Describe(content, shown, now);
        built.Element.ToolTip = tip; AutomationProperties.SetName(built.Element, tip.Replace("\n", Loc.T("；", "; ")));
        _compactAgenda = built.Element; _compactAgendaKinds = built.Kinds;
        return built.Element;
    }

    // The part fitted into room, the width a surface that keeps its own width has for it: the first level that fits; at the
    // gliding level, the text glides in what the rest of the part leaves. Too wide even as marks alone, the surface widens as it
    // does for any other fixed part.
    private FrameworkElement? CompactAgendaFitted(double scale, FrameworkElement whole, double room)
    {
        if (whole.Width <= room + .5) return whole;
        FrameworkElement? part = whole;
        for (int level = 1; level < AgendaStripView.Levels; level++)
        {
            if (level == AgendaStripView.ScrollLevel)
            {
                // Without the gliding text first, to learn what it leaves.
                double rest = CompactAgenda(scale, level, 0)?.Width ?? 0, slot = room - rest - CompactMusicLayout.Whole(5 * CompactMusicLayout.SafeScale(scale));
                if (slot < AgendaStripView.MinimumSlot(scale)) continue;
                part = CompactAgenda(scale, level, slot);
            }
            else part = CompactAgenda(scale, level);
            if (part is null || part.Width <= room + .5) return part;
        }
        return part;
    }

    // The kind of the part under the pointer, as the planner's view; null when the pointer is not on the part.
    private AgendaView? PointerAgendaView()
    {
        if (_compactAgenda is not { IsVisible: true } part || _compactAgendaKinds.Count == 0 || PresentationSource.FromVisual(part) is null) return null;
        var rect = new Rect(part.PointToScreen(new Point()), part.PointToScreen(new Point(part.ActualWidth, part.ActualHeight)));
        var cursor = WindowsHost.Cursor();
        var near = rect; near.Inflate(4, 6);
        if (!near.Contains(cursor) || rect.Width <= 0 || rect.Height <= 0) return null;
        // Along the part in its own units: the row runs across, a side edge's column down.
        double along = _compactAgendaDown ? (cursor.Y - rect.Top) / rect.Height * part.ActualHeight : (cursor.X - rect.Left) / rect.Width * part.ActualWidth;
        // The kinds share the part between them, meeting halfway across each gap.
        var kind = _compactAgendaKinds[^1].Kind;
        for (int i = 0; i < _compactAgendaKinds.Count - 1; i++)
            if (along < (_compactAgendaKinds[i].Right + _compactAgendaKinds[i + 1].Left) / 2) { kind = _compactAgendaKinds[i].Kind; break; }
        return kind switch { AgendaStripKind.Todo => AgendaView.Todos, AgendaStripKind.Note => AgendaView.Note, _ => AgendaView.Events };
    }

    // The panel opens on the planner page from the part: on today, switched to view (taken by the page as it is drawn).
    private void OpenAgendaFromStrip(AgendaView view) => _app.Agenda?.Reveal(DateOnly.FromDateTime(AgendaStripNow), view, null, this);
}
