namespace BrimDeck.Calendar;

// One occurrence of an event in an external calendar. Instances carry the device's local day and
// minute-of-day, so no time-zone conversion is needed to place them in the grid.
public sealed record VivoOccurrence(string Key, DateOnly Day, string Title, bool AllDay, int Start, int Duration, EventCategory Category);

// Reads the calendar database of vivo's PC suite (vivo 办公套件) through winsqlite3.dll.
// Read-only, one statement at a time, short busy timeout: while this runs the suite's own writer can
// wait a few milliseconds, which is the same trade any calendar viewer makes. Nothing is ever written.
public static class VivoCalendar
{
    private const int JulianEpoch = 2451545;                    // 2000-01-01
    private static readonly DateOnly EpochDay = new(2000, 1, 1);

    public static string? Locate()
    {
        string? custom = Environment.GetEnvironmentVariable("BRIMDECK_VIVO_DB");
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (string relative in new[] { @"pcsuite\database\CalendarSync.db", @"vivoOffice\database\CalendarSync.db" })
        {
            string path = Path.Combine(appData, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static IReadOnlyList<VivoOccurrence> Read(string path, DateOnly from, DateOnly to, out string? error)
    {
        error = null;
        var list = new List<VivoOccurrence>();
        if (!Sqlite.Available()) { error = "winsqlite3-missing"; return list; }
        try
        {
            using var db = Sqlite.Database.OpenReadOnly(path);
            var calendars = Calendars(db);
            int first = Julian(from), last = Julian(to);

            db.Query($"""
                select e._id, ifnull(e.title,''), ifnull(e.allDay,0), i.startDay, i.startMinute, i.endDay, i.endMinute, ifnull(e.calendar_id,0)
                from Instances i join Events e on e._id = i.event_id
                where ifnull(e.deleted,0) = 0 and i.startDay between {first} and {last}
                """, row =>
            {
                long id = row.Int64(0);
                bool allDay = row.Int64(2) != 0;
                int day = (int)row.Int64(3), startMinute = (int)row.Int64(4);
                int endDay = (int)row.Int64(5), endMinute = (int)row.Int64(6);
                long calendar = row.Int64(7);
                if (day < 1) return;
                var date = EpochDay.AddDays(day - JulianEpoch);
                int start = allDay ? 0 : Math.Clamp(startMinute, 0, 1439);
                int minutes = allDay ? 1440 : (endDay - day) * 1440 + (endMinute - startMinute);
                if (minutes <= 0) minutes = allDay ? 1440 : 30;
                list.Add(new VivoOccurrence($"vivo-{id}-{day}-{startMinute}", date, row.Text(1), allDay, start,
                    Math.Clamp(minutes, 5, 1440), Kind(calendars.GetValueOrDefault(calendar, ""))));
            });

            // Events that were never expanded into instances fall back to their own start and end.
            long fromMs = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue)).ToUnixTimeMilliseconds();
            long toMs = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue)).ToUnixTimeMilliseconds();
            db.Query($"""
                select e._id, ifnull(e.title,''), ifnull(e.allDay,0), ifnull(e.dtStart,0), ifnull(e.dtEnd,0), ifnull(e.calendar_id,0)
                from Events e
                where ifnull(e.deleted,0) = 0 and ifnull(e.dtStart,0) between {fromMs} and {toMs}
                  and not exists (select 1 from Instances i where i.event_id = e._id)
                """, row =>
            {
                long id = row.Int64(0);
                bool allDay = row.Int64(2) != 0;
                long startMs = row.Int64(3), endMs = row.Int64(4);
                long calendar = row.Int64(5);
                if (startMs <= 0) return;
                var local = DateTimeOffset.FromUnixTimeMilliseconds(startMs).LocalDateTime;
                var date = DateOnly.FromDateTime(local);
                int start = allDay ? 0 : local.Hour * 60 + local.Minute;
                int minutes = (int)Math.Clamp((endMs - startMs) / 60000, 5, 1440);
                list.Add(new VivoOccurrence($"vivo-{id}-{startMs}", date, row.Text(1), allDay, start, minutes,
                    Kind(calendars.GetValueOrDefault(calendar, ""))));
            });
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        // A recurring event can expand far into the future; the nearest occurrences are what a calendar shows.
        if (list.Count > 2000) return list.OrderBy(item => item.Day).ThenBy(item => item.Start).Take(2000).ToArray();
        return list;
    }

    private static Dictionary<long, string> Calendars(Sqlite.Database db)
    {
        var map = new Dictionary<long, string>();
        db.Query("select _id, ifnull(displayName,'') from Calendar", row => map[row.Int64(0)] = row.Text(1));
        return map;
    }

    // The suite's own calendar names decide the colour; anything unknown stays neutral.
    private static EventCategory Kind(string calendar)
    {
        if (calendar.Contains("生日", StringComparison.Ordinal) || calendar.Contains("birthday", StringComparison.OrdinalIgnoreCase))
            return EventCategory.Important;
        if (calendar.Contains("工作", StringComparison.Ordinal) || calendar.Contains("work", StringComparison.OrdinalIgnoreCase)
            || calendar.Contains("课程", StringComparison.Ordinal) || calendar.Contains("课表", StringComparison.Ordinal))
            return EventCategory.Work;
        return EventCategory.Other;
    }

    private static int Julian(DateOnly day) => JulianEpoch + day.DayNumber - EpochDay.DayNumber;

    // Stable across processes, so the same content never triggers a second merge.
    public static ulong Print(IReadOnlyList<VivoOccurrence> items)
    {
        ulong hash = 14695981039346656037;
        foreach (var item in items)
        foreach (char c in item.Key)
        {
            hash ^= c;
            hash *= 1099511628211;
        }
        return hash ^ (ulong)items.Count;
    }
}
