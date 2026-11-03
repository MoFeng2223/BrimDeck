namespace BrimDeck.Calendar;

public sealed record FocusInfo(
    CalendarEvent? Running, CalendarEvent? Upcoming,
    TimeSpan Remaining, TimeSpan Until,
    int TodayCount, bool AllDayToday)
{
    public bool Active => Running is not null || Upcoming is not null || AllDayToday;
}

// Day index over the flat event list: a month view asks for 42 days, never for the whole file.
public sealed class Schedule
{
    private readonly Dictionary<DateOnly, List<CalendarEvent>> _days = [];

    public void Rebuild(IEnumerable<CalendarEvent> events)
    {
        _days.Clear();
        foreach (var item in events)
        {
            var day = item.Day;
            if (day == default) continue;
            if (!_days.TryGetValue(day, out var list)) _days[day] = list = [];
            list.Add(item);
        }
        foreach (var list in _days.Values) list.Sort(Compare);
    }

    public IReadOnlyList<CalendarEvent> Day(DateOnly day) =>
        _days.TryGetValue(day, out var list) ? list : [];

    public int CountOn(DateOnly day) => _days.TryGetValue(day, out var list) ? list.Count : 0;

    public FocusInfo Focus(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var items = Day(today);
        CalendarEvent? running = null, upcoming = null;
        bool allDay = false;
        TimeSpan remaining = default, until = default;
        foreach (var item in items)
        {
            if (item.AllDay) { allDay = true; continue; }
            var start = item.StartAt(today);
            var end = item.EndAt(today);
            if (now >= start && now < end)
            {
                if (running is null || item.Start > running.Start) { running = item; remaining = end - now; }
            }
            else if (now < start && upcoming is null) { upcoming = item; until = start - now; }
        }
        return new FocusInfo(running, upcoming, remaining, until, items.Count, allDay);
    }

    private static int Compare(CalendarEvent a, CalendarEvent b)
    {
        if (a.AllDay != b.AllDay) return a.AllDay ? -1 : 1;
        int byStart = a.Start.CompareTo(b.Start);
        return byStart != 0 ? byStart : string.CompareOrdinal(a.Title, b.Title);
    }
}
