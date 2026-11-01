using System.Globalization;

namespace BrimDeck.Calendar;

public enum EventCategory { Work = 0, Personal = 1, Important = 2, Health = 3, Other = 4 }

// Stored shape. Date stays a yyyy-MM-dd string so the host's JSON serializer never has to know
// about DateOnly, and a hand-edited file keeps working.
public sealed class CalendarEvent
{
    public string Id { get; set; } = "";
    public string Date { get; set; } = "";
    public string Title { get; set; } = "";
    public bool AllDay { get; set; }
    public int Start { get; set; } = 9 * 60;
    public int Duration { get; set; } = 60;
    public EventCategory Category { get; set; }
    public bool Done { get; set; }
    // Empty for events written in this module; "vivo" for the read-only mirror of an external calendar.
    public string Source { get; set; } = "";

    public DateOnly Day => Parse(Date);
    public DateTime StartAt(DateOnly day) => day.ToDateTime(TimeOnly.MinValue).AddMinutes(Math.Clamp(Start, 0, 1439));
    public DateTime EndAt(DateOnly day) => StartAt(day).AddMinutes(Math.Clamp(Duration, 1, 1440));
    public string Clock => $"{Math.Clamp(Start, 0, 1439) / 60:00}:{Math.Clamp(Start, 0, 1439) % 60:00}";

    public static DateOnly Parse(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day : default;

    public static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Tolerates hand-edited files: bad rows are dropped, the rest are clamped instead of rejected.
    public bool Normalize()
    {
        var day = Parse(Date);
        if (day == default) return false;
        Date = Key(day);
        Title = (Title ?? "").Trim();
        if (Title.Length > 120) Title = Title[..120];
        Start = Math.Clamp(Start, 0, 1439);
        Duration = Math.Clamp(Duration, 1, 1440);
        if (!Enum.IsDefined(Category)) Category = EventCategory.Other;
        Source = (Source ?? "").Trim();
        if (Source.Length > 16) Source = Source[..16];
        return true;
    }
}

public sealed class EventFile
{
    public int Version { get; set; } = 1;
    public List<CalendarEvent> Events { get; set; } = [];
}

public sealed class CalendarOptions
{
    public bool PageEnabled { get; set; } = true;
    public bool ActivityEnabled { get; set; } = true;
    // 0 = Sunday, 1 = Monday.
    public int FirstDay { get; set; } = 1;
    public bool ShowLunar { get; set; } = true;
    public bool ShowHoliday { get; set; } = true;
    // Minutes before the start; -1 turns reminders off.
    public int ReminderMinutes { get; set; } = 10;
    // Experimental: mirror the calendar database of vivo's PC suite. Off by default, never written to.
    public bool VivoEnabled { get; set; }

    public void Normalize()
    {
        if (FirstDay is not (0 or 1)) FirstDay = 1;
        if (ReminderMinutes < -1 || ReminderMinutes > 1440) ReminderMinutes = 10;
    }

    public CalendarOptions Copy() => (CalendarOptions)MemberwiseClone();
}

public sealed class ReminderFile
{
    public List<string> Marks { get; set; } = [];
}
