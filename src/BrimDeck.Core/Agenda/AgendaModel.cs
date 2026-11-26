using System.Globalization;
using System.Text.Json.Nodes;
using BrimDeck.Core.Notices;

namespace BrimDeck.Core.Agenda;

// The planner page ("计划" / "Planner"): events, to-dos and one note a day, kept in agenda.json (AgendaStore). Every time here
// is local wall time, a DateTime of Kind Unspecified to the minute: "14:00" is 14:00 wherever the PC is, and nothing is ever
// turned into UTC. Nothing in this namespace reads the clock; "now" and "today" are always passed in.

// Which of the three the planner shows for a day.
public enum AgendaView { Events, Todos, Note }

// A request to show Day in the planner, switched to View, with the item ItemId (an event's or a to-do's) marked. From: whoever
// asked (the panel whose notices page asked), for a page to tell a request meant for its own panel; null for any.
public sealed record AgendaReveal(DateOnly Day, AgendaView View, string? ItemId = null, object? From = null);

// Where an item added from a notice came from: enough to show "Outlook · 王敏" with the application's icon after the notice is
// gone, and the notice's Id to find it again (and the items it added) while it is still kept. Icon is the notice's IconRef, in
// the forms NoticeIcons reads.
public sealed record AgendaSource
{
    public required string NoticeId { get; init; }
    public string AppKey { get; init; } = "";
    public string AppName { get; init; } = "";
    // The notice's title: the sender of a chat or mail, or the subject where the application puts no sender there.
    public string? Sender { get; init; }
    public string? Icon { get; init; }
    internal JsonObject? Extra { get; init; }

    public static AgendaSource From(Notice notice) => new()
    {
        NoticeId = notice.Id, AppKey = notice.AppKey, AppName = notice.AppName.Trim(),
        Sender = string.IsNullOrWhiteSpace(notice.Title) ? null : notice.Title.Trim(), Icon = notice.IconRef
    };
}

// How an event repeats. Each time starts on a day the rule gives, at the first time's clock time, and lasts as long as it.
// Weekly: on the first day's weekday. Monthly: on the first day's day of the month, so a series from the 31st skips months
// without one. Yearly: on the first day's month and day (a 29 February one only in leap years). Weekdays: Monday to Friday.
public enum AgendaRepeat { None, Daily, Weekdays, Weekly, Monthly, Yearly }

// An event. A timed one runs from Start to End (End later than Start, any number of days later). An all-day one covers the days
// from Start's date to End's date, both included; both are kept at 00:00. Detail is the one extra line (a place, a meeting link).
// A repeating one (Repeat) is a series: Start and End are its first time; it occurs on its first day and on every later day the
// rule gives, up to Until when there is one, except the days in Skipped (a time deleted alone). The planner shows each time as
// a copy of the series moved to its day (Occurrence), with the series' Id.
public sealed record AgendaEvent
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public bool AllDay { get; init; }
    public string? Detail { get; init; }
    public AgendaSource? Source { get; init; }
    public DateTime Created { get; init; }
    public AgendaRepeat Repeat { get; init; }
    public DateOnly? Until { get; init; }
    public IReadOnlyList<DateOnly> Skipped { get; init; } = [];
    // How many minutes before its start BrimDeck reminds of each time of the event; null: no reminder. 0 is at the start. An
    // all-day event's start is 00:00 of its first day, so its reminder may be later than that, a negative number: -540 is 09:00 of
    // the day itself, 900 is 09:00 of the day before (AgendaReminders).
    public int? Remind { get; init; }
    internal JsonObject? Extra { get; init; }

    // When BrimDeck reminds of this time of the event, or null when it does not.
    public DateTime? RemindAt => Remind is { } minutes ? Start.AddMinutes(-minutes) : null;

    public DateOnly FirstDay => DateOnly.FromDateTime(Start);
    // The last day the event shows on: an all-day event's last date; a timed one ending at exactly 00:00 does not reach that day.
    // For a series, the last day of its first time.
    public DateOnly LastDay => AllDay ? DateOnly.FromDateTime(End) : DateOnly.FromDateTime(End.TimeOfDay == TimeSpan.Zero ? End.AddDays(-1) : End);
    public int DayCount => LastDay.DayNumber - FirstDay.DayNumber + 1;
    // Whether this event (one time of it, for a series) shows on day.
    public bool Covers(DateOnly day) => day >= FirstDay && day <= LastDay;

    // Whether a time of the event starts on day.
    public bool Occurs(DateOnly day)
    {
        if (Repeat == AgendaRepeat.None) return day == FirstDay;
        if (day < FirstDay || Until is { } until && day > until || Skipped.Contains(day)) return false;
        if (day == FirstDay) return true;
        return Repeat switch
        {
            AgendaRepeat.Daily => true,
            AgendaRepeat.Weekdays => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            AgendaRepeat.Weekly => day.DayOfWeek == FirstDay.DayOfWeek,
            AgendaRepeat.Monthly => day.Day == FirstDay.Day,
            AgendaRepeat.Yearly => day.Month == FirstDay.Month && day.Day == FirstDay.Day,
            _ => false
        };
    }

    // The time of the event that starts on day: the series moved there whole (call it for a day it Occurs on).
    public AgendaEvent Occurrence(DateOnly day)
    {
        int days = day.DayNumber - FirstDay.DayNumber;
        return days == 0 ? this : this with { Start = Start.AddDays(days), End = End.AddDays(days) };
    }

    // The times of the event that show on any day from first to last, in order of their start.
    public IEnumerable<AgendaEvent> Occurrences(DateOnly first, DateOnly last)
    {
        if (Repeat == AgendaRepeat.None)
        {
            if (FirstDay <= last && LastDay >= first) yield return this;
            yield break;
        }
        var from = first.AddDays(-(DayCount - 1));
        if (from < FirstDay) from = FirstDay;
        if (Until is { } until && last > until) last = until;
        for (var day = from; day <= last; day = day.AddDays(1))
            if (Occurs(day)) yield return Occurrence(day);
    }

    // Whether the series has a time before day (to tell whether deleting the times from a day on leaves any).
    public bool OccursBefore(DateOnly day)
    {
        for (var at = FirstDay; at < day; at = at.AddDays(1)) if (Occurs(at)) return true;
        return false;
    }
}

// A to-do. Day: the day it belongs to, or null for "长期" (no day). Due: the day it must be done by, or null. DoneAt: when it was
// ticked, null while open.
public sealed record AgendaTodo
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public DateOnly? Day { get; init; }
    public DateOnly? Due { get; init; }
    public bool Done { get; init; }
    public DateTime? DoneAt { get; init; }
    public AgendaSource? Source { get; init; }
    public DateTime Created { get; init; }
    internal JsonObject? Extra { get; init; }

    // The day it was ticked, for a done to-do without a day of its own.
    public DateOnly? DoneDay => Done && DoneAt is { } at ? DateOnly.FromDateTime(at) : null;
}

// Everything kept: what AgendaStore reads and writes and AgendaBook holds. Notes are one text a day; a blank one is not kept.
// Extra keeps what a later version wrote that this one does not know, so saving here does not drop it.
public sealed class AgendaData
{
    public List<AgendaEvent> Events { get; init; } = [];
    public List<AgendaTodo> Todos { get; init; } = [];
    public Dictionary<DateOnly, string> Notes { get; init; } = [];
    // The reminders up to this time were given (or BrimDeck was running and passed them): a later start gives only those after
    // it, the ones missed while BrimDeck was not running among them. Null before the first start that reminds.
    public DateTime? RemindedTo { get; init; }
    internal JsonObject? Extra { get; init; }
}

// Wall times as the file keeps them, and the minute every time is cut to.
public static class AgendaTime
{
    public const string MinuteFormat = "yyyy-MM-dd'T'HH:mm", SecondFormat = "yyyy-MM-dd'T'HH:mm:ss", DayFormat = "yyyy-MM-dd";
    public static DateTime Minute(DateTime time) => new(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, DateTimeKind.Unspecified);
    public static DateTime Second(DateTime time) => new(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second, DateTimeKind.Unspecified);
    public static DateTime Midnight(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
    public static string Text(DateTime time) => time.ToString(time.Second == 0 ? MinuteFormat : SecondFormat, CultureInfo.InvariantCulture);
    public static string Text(DateOnly day) => day.ToString(DayFormat, CultureInfo.InvariantCulture);
    // "2026-11-22T14:00" or with seconds; also a bare date (00:00). Null for anything else, a time zone included.
    public static DateTime? Parse(string? text)
    {
        if (text is null) return null;
        if (DateTime.TryParseExact(text, [MinuteFormat, SecondFormat, DayFormat], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
        return null;
    }
    public static DateOnly? ParseDay(string? text)
        => text is not null && DateOnly.TryParseExact(text, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
}
