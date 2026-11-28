using System.Globalization;
using System.Text;

namespace BrimDeck.Core.Agenda;

// How the collapsed row's planner part chooses among the kinds switched on (DeckSettings.AgendaStripEvents, AgendaStripTodos,
// AgendaStripNote). Auto: one kind at a time, the one most worth a glance now (AgendaStrip.Shown). All: every kind that has
// something, side by side. Saved as numbers, so new members go at the end.
public enum AgendaStripMode { Auto, All }
// The collapsed event: its time and its title, or its time alone.
public enum AgendaStripEventForm { Title, Time }
// The collapsed to-dos: how many are unfinished, or the first one's words with how many more.
public enum AgendaStripTodoForm { Count, Text }
// The first day of the planner's weeks.
public enum AgendaWeekStart { Monday, Sunday }
// Unfinished to-dos of earlier days without a due day. Ask: today's list offers "移到今天" (AgendaCarryOver). Move: they move to
// today by themselves.
public enum AgendaLeftTodos { Ask, Move }
// A day's ticked to-dos: where they were, after the unfinished ones, or not shown.
public enum AgendaDoneTodos { Stay, Last, Hidden }

public enum AgendaStripKind { Event, Todo, Note }

// Where today's event stands at the time asked for. Later: it starts in more than AgendaStrip.Soon. Soon: within it. Running:
// under way. AllDay: an all-day event, shown while no timed one is under way or still to come.
public enum AgendaStripState { Later, Soon, Running, AllDay }

// The event the collapsed row shows: one time of it (AgendaEvent.Occurrence) and its state. Progress: how far a running event
// is, 0 to 1.
public sealed record AgendaStripEvent(AgendaEvent Event, AgendaStripState State, double Progress);

// The collapsed to-dos. Open: today's unfinished ones, those past their due day among them (Overdue of them). Text: the first
// of today's own; when only overdue ones are left, the first of those, and OverdueDays is how long it is overdue (0 otherwise).
public sealed record AgendaStripTodos(int Open, int Overdue, string Text, int OverdueDays)
{
    public int More => Open - 1;
}

// What the planner has for the collapsed row now, whichever of it the settings show.
public sealed record AgendaStripContent(AgendaStripEvent? Event, AgendaStripTodos? Todos, string? Note);

// The collapsed row's planner part (CompactPart.Agenda): what it has to show, which of it shows, and its words. Nothing here
// reads the clock.
public static class AgendaStrip
{
    // An event that starts within this long shows how long is left to it, and comes first in Auto.
    public static readonly TimeSpan Soon = TimeSpan.FromHours(1);
    // The longest text the row shows, counted as Units counts: twenty Chinese characters or forty letters.
    public const int MaxUnits = 40;

    public static AgendaStripContent Content(IEnumerable<AgendaEvent> events, IEnumerable<AgendaTodo> todos, IReadOnlyDictionary<DateOnly, string> notes, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        return new(EventAt(events, now), TodosAt(todos, today), NoteOf(notes.GetValueOrDefault(today)));
    }

    // Today's event at now: the one under way (the one that ends first), else the next to start today, else an all-day one.
    // Null when today has no event left.
    public static AgendaStripEvent? EventAt(IEnumerable<AgendaEvent> events, DateTime now)
    {
        var segments = AgendaQuery.Segments(events, DateOnly.FromDateTime(now));
        var running = segments.Where(segment => !segment.AllDay && segment.Start <= now && now < segment.End).OrderBy(segment => segment.Event.End).ThenBy(segment => segment.Start).FirstOrDefault();
        if (running is not null)
        {
            var item = running.Event;
            double whole = (item.End - item.Start).TotalMinutes;
            return new(item, AgendaStripState.Running, whole > 0 ? Math.Clamp((now - item.Start).TotalMinutes / whole, 0, 1) : 0);
        }
        var next = segments.Where(segment => !segment.AllDay && !segment.FromEarlier && segment.Start > now).OrderBy(segment => segment.Start).FirstOrDefault();
        if (next is not null) return new(next.Event, next.Start - now <= Soon ? AgendaStripState.Soon : AgendaStripState.Later, 0);
        return segments.FirstOrDefault(segment => segment.AllDay) is { } allDay ? new(allDay.Event, AgendaStripState.AllDay, 0) : null;
    }

    // Today's unfinished to-dos as the planner page lists them for today (AgendaQuery.Todos): those of the day and every one
    // with a due day. Null when none is left.
    public static AgendaStripTodos? TodosAt(IEnumerable<AgendaTodo> todos, DateOnly today)
    {
        var open = AgendaQuery.Todos(todos, today, today).Main.Where(todo => !todo.Done).ToList();
        if (open.Count == 0) return null;
        int overdue = open.Count(todo => AgendaQuery.Overdue(todo, today));
        if (open.FirstOrDefault(todo => !AgendaQuery.Overdue(todo, today)) is { } first) return new(open.Count, overdue, OneLine(first.Text), 0);
        var late = open[0];
        return new(open.Count, overdue, OneLine(late.Text), today.DayNumber - late.Due!.Value.DayNumber);
    }

    // A day's note as one line: its lines joined by a space; null when it says nothing.
    public static string? NoteOf(string? note)
    {
        var line = OneLine(note ?? "");
        return line.Length == 0 ? null : line;
    }

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // The kinds the row shows, in the order it shows them. All: the event, the to-dos, the note, as the planner page's switches
    // stand. Auto: the first that has something of an event under way or about to start, the note, the to-dos, a later or
    // all-day event. A kind switched off or with nothing is left out.
    public static IReadOnlyList<AgendaStripKind> Shown(AgendaStripContent content, AgendaStripMode mode, bool events, bool todos, bool note)
    {
        bool hasEvent = events && content.Event is not null, hasTodos = todos && content.Todos is not null, hasNote = note && content.Note is not null;
        if (mode == AgendaStripMode.All)
        {
            var all = new List<AgendaStripKind>(3);
            if (hasEvent) all.Add(AgendaStripKind.Event);
            if (hasTodos) all.Add(AgendaStripKind.Todo);
            if (hasNote) all.Add(AgendaStripKind.Note);
            return all;
        }
        if (hasEvent && content.Event!.State is AgendaStripState.Running or AgendaStripState.Soon) return [AgendaStripKind.Event];
        if (hasNote) return [AgendaStripKind.Note];
        if (hasTodos) return [AgendaStripKind.Todo];
        return hasEvent ? [AgendaStripKind.Event] : [];
    }

    // The next time what Content gives may change by the clock alone: the start of the next minute while an event is under way
    // or soon (its words count minutes), else the moment the next event comes within Soon or begins, or one ends, and midnight.
    public static DateTime NextChange(IEnumerable<AgendaEvent> events, DateTime now)
    {
        var minute = AgendaTime.Minute(now).AddMinutes(1);
        var midnight = AgendaTime.Midnight(DateOnly.FromDateTime(now).AddDays(1));
        if (EventAt(events, now) is not { } shown) return midnight;
        if (shown.State is AgendaStripState.Running or AgendaStripState.Soon) return minute;
        var next = midnight;
        foreach (var segment in AgendaQuery.Segments(events, DateOnly.FromDateTime(now)))
        {
            if (segment.AllDay) continue;
            foreach (var at in new[] { segment.Start - Soon, segment.Start, segment.End })
                if (at > now && at < next) next = at;
        }
        return next < minute ? minute : next;
    }

    // ---- Words ----

    // The event's time: "16:30"; soon "20 分钟后"; under way "还剩 40 分钟" ("进行中" when it ends on a later day); all day "全天".
    public static string Time(AgendaStripEvent shown, DateTime now)
    {
        var item = shown.Event;
        return shown.State switch
        {
            AgendaStripState.AllDay => Loc.T("全天", "All day"),
            AgendaStripState.Running when item.LastDay == DateOnly.FromDateTime(now) => Loc.T($"还剩 {AgendaText.Length(item.End - now)}", $"{AgendaText.Length(item.End - now)} left"),
            AgendaStripState.Running => Loc.T("进行中", "In progress"),
            AgendaStripState.Soon => Loc.T($"{AgendaText.Length(item.Start - now)}后", $"In {AgendaText.Length(item.Start - now)}"),
            _ => AgendaText.Clock(item.Start, item.FirstDay)
        };
    }

    // After the to-dos' count or words: "过期 1" / "1 overdue".
    public static string Overdue(int count) => Loc.T($"过期 {count}", $"{count} overdue");
    // After the words of a to-do when only overdue ones are left: "过期 1 天" / "1 day overdue".
    public static string OverdueFor(int days) => Loc.T($"过期 {days} 天", $"{Loc.Count(days, "day", "days")} overdue");

    // What a screen reader and the tooltip say of the part: every kind shown, in full.
    public static string Describe(AgendaStripContent content, IReadOnlyList<AgendaStripKind> shown, DateTime now)
    {
        var lines = new List<string>();
        foreach (var kind in shown)
        {
            switch (kind)
            {
                case AgendaStripKind.Event when content.Event is { } item:
                    lines.Add(Loc.T("日程：", "Event: ") + Time(item, now) + " " + item.Event.Title); break;
                case AgendaStripKind.Todo when content.Todos is { } todos:
                    lines.Add(Loc.T($"待办：{todos.Open} 项未完成", $"To-dos: {todos.Open} unfinished") + (todos.Overdue > 0 ? Loc.T("，", ", ") + Overdue(todos.Overdue) : "")); break;
                case AgendaStripKind.Note when content.Note is { } note:
                    lines.Add(Loc.T("便签：", "Note: ") + note); break;
            }
        }
        return string.Join("\n", lines);
    }

    // ---- The longest text ----

    // How wide a text counts: a character as wide as a Chinese one (CJK, kana, Hangul, full-width forms, emoji, "…") is 2, any other 1.
    public static int Units(string text)
    {
        int units = 0;
        foreach (var rune in text.EnumerateRunes()) units += Wide(rune.Value) ? 2 : 1;
        return units;
    }

    // The text as the row shows it: whole within MaxUnits, else cut so that it and "…" (2) count MaxUnits at most. A character
    // and the marks joined to it are kept or dropped together.
    public static string Clip(string text, int units = MaxUnits)
    {
        if (Units(text) <= units) return text;
        var kept = new StringBuilder();
        int used = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int size = Units(element);
            if (used + size > units - 2) break;
            kept.Append(element); used += size;
        }
        return kept.ToString().TrimEnd() + "…";
    }

    // The ellipsis counts as wide: beside Chinese text it is drawn as wide as a character.
    private static bool Wide(int code) => code is 0x2026 or >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
        or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD;
}
