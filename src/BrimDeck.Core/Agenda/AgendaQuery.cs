namespace BrimDeck.Core.Agenda;

// The part of an event that falls on one day. Start and End are this part's own (00:00 of the next day where it runs past
// midnight). AllDay: shown in the all-day row, an all-day event or a day the event covers whole. FromEarlier: the event began on
// an earlier day; ContinuesLater: it goes on to a later one. DayIndex counts from 1 among the DayCount days it shows on.
public sealed record AgendaSegment(AgendaEvent Event, DateOnly Day, DateTime Start, DateTime End, bool AllDay, bool FromEarlier, bool ContinuesLater, int DayIndex, int DayCount)
{
    public bool MultiDay => DayCount > 1;
}

// Where a timed segment sits in the block view: Column (from 0) of the Columns its group of overlapping segments is split into.
public sealed record AgendaPlacement(AgendaSegment Segment, int Column, int Columns);

// The to-dos one day shows: Main, the day's list, and LongTerm, the "长期" group. Open counts the unfinished ones in Main.
// CarryOver is set on today only, when to-dos of earlier days were left unfinished.
public sealed record AgendaTodoDay(IReadOnlyList<AgendaTodo> Main, IReadOnlyList<AgendaTodo> LongTerm, int Open, AgendaCarryOver? CarryOver);

// Unfinished to-dos of days before today without a due day; AllYesterday: every one of them belongs to yesterday.
public sealed record AgendaCarryOver(int Count, bool AllYesterday);

// The numbers on the three switches: the day's segments, its unfinished to-dos (Main), and whether it has a note.
public sealed record AgendaCounts(int Events, int OpenTodos, bool Note);

// One day of the month view. Events: any event shows on the day; DayEvents: an event that starts and ends on this day (the
// days of a longer one are marked by the line instead). OpenTodos: an unfinished to-do belongs to the day or is due on it.
// InSpan: the day lies under a line joining the days of events that run over several days (overlapping ones share one line);
// SpanStart and SpanEnd: the line begins or ends on this day, wherever the range asked for begins or ends.
public sealed record AgendaDayMark(DateOnly Day, bool Events, bool DayEvents, bool OpenTodos, bool Note, bool InSpan, bool SpanStart, bool SpanEnd);

public enum AgendaPhase { Upcoming, Ongoing, Past }

public static class AgendaQuery
{
    public static IReadOnlyList<AgendaSegment> Segments(IEnumerable<AgendaEvent> events, DateOnly day)
    {
        var list = new List<(AgendaSegment Segment, int Order)>();
        int order = 0;
        foreach (var item in events) { if (Segment(item, day) is { } segment) list.Add((segment, order)); order++; }
        return list.OrderBy(pair => pair.Segment.AllDay ? 0 : 1)
            .ThenBy(pair => pair.Segment.AllDay ? pair.Segment.Event.Start : pair.Segment.Start)
            .ThenBy(pair => pair.Segment.AllDay ? pair.Segment.Event.End : pair.Segment.End)
            .ThenBy(pair => pair.Segment.Event.Created).ThenBy(pair => pair.Order)
            .Select(pair => pair.Segment).ToList();
    }

    public static AgendaSegment? Segment(AgendaEvent item, DateOnly day)
    {
        if (!item.Covers(day)) return null;
        DateTime midnight = AgendaTime.Midnight(day), next = midnight.AddDays(1);
        int index = day.DayNumber - item.FirstDay.DayNumber + 1;
        bool earlier = day > item.FirstDay, later = day < item.LastDay;
        if (item.AllDay) return new(item, day, midnight, next, true, earlier, later, index, item.DayCount);
        var start = item.Start > midnight ? item.Start : midnight;
        var end = item.End < next ? item.End : next;
        return new(item, day, start, end, start == midnight && end == next, earlier, later, index, item.DayCount);
    }

    // Splits the timed segments of a day into columns: segments whose times overlap (one ending as the next begins does not)
    // form a group, and a group takes as many columns as the most segments it has at one moment; a segment overlapping no
    // other has the whole width (one column). Each segment counts at least minimum long, so a short one does not sit under
    // the next on screen. All-day segments are left out. The result keeps the order of the segments given.
    public static IReadOnlyList<AgendaPlacement> Columns(IEnumerable<AgendaSegment> segments, TimeSpan minimum)
    {
        var timed = segments.Where(segment => !segment.AllDay).ToList();
        var sorted = Enumerable.Range(0, timed.Count).OrderBy(i => timed[i].Start).ThenBy(i => timed[i].End).ThenBy(i => i).ToList();
        var column = new int[timed.Count];
        var columns = new int[timed.Count];
        DateTime Until(int i) => timed[i].End - timed[i].Start < minimum ? timed[i].Start + minimum : timed[i].End;
        var group = new List<int>();
        var ends = new List<DateTime>();
        DateTime groupEnd = DateTime.MinValue;
        void Close() { foreach (var i in group) columns[i] = ends.Count; group.Clear(); ends.Clear(); }
        foreach (var i in sorted)
        {
            if (group.Count > 0 && timed[i].Start >= groupEnd) Close();
            int free = ends.FindIndex(end => end <= timed[i].Start);
            if (free < 0) { free = ends.Count; ends.Add(Until(i)); } else ends[free] = Until(i);
            column[i] = free;
            group.Add(i);
            if (group.Count == 1 || Until(i) > groupEnd) groupEnd = Until(i);
        }
        Close();
        return timed.Select((segment, i) => new AgendaPlacement(segment, column[i], columns[i])).ToList();
    }

    // The to-dos of a day, in the order they were made; a ticked one stays where it was.
    // Main: those of the day; on today, every unfinished one with a due day as well; on a due day, the unfinished ones due then;
    // a ticked one with a due day also shows on the day it was ticked, so ticking it today does not take it out of today's list.
    // LongTerm: those without a day or a due day, unfinished ones on every day, ticked ones on the day they were ticked.
    public static AgendaTodoDay Todos(IEnumerable<AgendaTodo> todos, DateOnly day, DateOnly today)
    {
        var all = todos.Select((todo, order) => (Todo: todo, Order: order)).OrderBy(pair => pair.Todo.Created).ThenBy(pair => pair.Order).Select(pair => pair.Todo).ToList();
        var main = all.Where(todo => InMain(todo, day, today)).ToList();
        var longTerm = all.Where(todo => todo.Day is null && todo.Due is null && (!todo.Done || todo.DoneDay == day)).ToList();
        AgendaCarryOver? carry = null;
        if (day == today)
        {
            var left = all.Where(todo => CarriesOver(todo, today)).ToList();
            if (left.Count > 0) carry = new(left.Count, left.All(todo => todo.Day == today.AddDays(-1)));
        }
        return new(main, longTerm, main.Count(todo => !todo.Done), carry);
    }

    private static bool InMain(AgendaTodo todo, DateOnly day, DateOnly today)
        => todo.Day == day
        || todo.Due is not null && (todo.Done ? todo.DoneDay == day : day == today || todo.Due == day);

    // Left behind: an unfinished to-do of a day before today without a due day (one with a due day shows today already).
    public static bool CarriesOver(AgendaTodo todo, DateOnly today) => !todo.Done && todo.Due is null && todo.Day is { } day && day < today;

    public static bool Overdue(AgendaTodo todo, DateOnly today) => !todo.Done && todo.Due is { } due && due < today;

    public static AgendaCounts Counts(AgendaData data, DateOnly day, DateOnly today)
        => new(data.Events.Count(item => item.Covers(day)), Todos(data.Todos, day, today).Open, data.Notes.ContainsKey(day));

    // The marks of the days from first on, count of them (42 for a month of six rows, 7 for a week).
    public static IReadOnlyList<AgendaDayMark> Marks(AgendaData data, DateOnly first, int count)
    {
        var last = first.AddDays(Math.Max(count, 1) - 1);
        var shown = data.Events.Where(item => item.FirstDay <= last && item.LastDay >= first).ToList();
        // The lines: the day ranges of events over several days, overlapping ones merged. Taken from every event, not only those
        // in range, so a line's ends are its own and not the range's.
        var lines = new List<(DateOnly First, DateOnly Last)>();
        foreach (var item in data.Events.Where(item => item.DayCount > 1).OrderBy(item => item.FirstDay))
        {
            if (lines.Count > 0 && item.FirstDay <= lines[^1].Last)
            { if (item.LastDay > lines[^1].Last) lines[^1] = (lines[^1].First, item.LastDay); }
            else lines.Add((item.FirstDay, item.LastDay));
        }
        var open = new HashSet<DateOnly>();
        foreach (var todo in data.Todos.Where(todo => !todo.Done))
        {
            if (todo.Day is { } day) open.Add(day);
            if (todo.Due is { } due) open.Add(due);
        }
        var marks = new List<AgendaDayMark>(count);
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var current = day;
            var line = lines.FirstOrDefault(range => range.First <= current && range.Last >= current);
            bool inSpan = line != default;
            marks.Add(new(day, shown.Any(item => item.Covers(current)), shown.Any(item => item.DayCount == 1 && item.FirstDay == current),
                open.Contains(day), data.Notes.ContainsKey(day), inSpan, inSpan && line.First == day, inSpan && line.Last == day));
        }
        return marks;
    }

    // Whether a segment is over, under way or still to come at now: for greying ended ones and the words of AgendaText.Status.
    public static AgendaPhase Phase(AgendaSegment segment, DateTime now)
        => now >= segment.End ? AgendaPhase.Past : now >= segment.Start ? AgendaPhase.Ongoing : AgendaPhase.Upcoming;
    public static AgendaPhase Phase(AgendaEvent item, DateTime now)
    {
        var end = item.AllDay ? AgendaTime.Midnight(item.LastDay.AddDays(1)) : item.End;
        return now >= end ? AgendaPhase.Past : now >= item.Start ? AgendaPhase.Ongoing : AgendaPhase.Upcoming;
    }
}
