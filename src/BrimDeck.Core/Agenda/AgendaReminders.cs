using System.Globalization;
using BrimDeck.Core.Notices;

namespace BrimDeck.Core.Agenda;

// A reminder: one time of an event (AgendaEvent.Occurrence) and when BrimDeck reminds of it.
public sealed record AgendaReminder(AgendaEvent Event, DateTime At);

// Reminders of events (AgendaEvent.Remind): the choices the forms offer, their words, which reminders fall in a span of time, and
// the notice a reminder is given as. A reminder is a notice of BrimDeck's own (NoticeKeys.Internal), so it shows on the island and
// on the notices page as every other notice does; it stays until handled, as an event's reminder from Outlook does. Nothing here
// reads the clock.
public static class AgendaReminders
{
    // How far before an event a reminder may be: four weeks.
    public const int Largest = 28 * 24 * 60;
    // A new event's reminder: 15 minutes before a timed one, 09:00 of the day of an all-day one.
    public const int DefaultTimed = 15, DefaultAllDay = -9 * 60;
    public static int Default(bool allDay) => allDay ? DefaultAllDay : DefaultTimed;

    // The choices offered besides "不提醒" and "自定义": at the start, 5, 10, 15 and 30 minutes, an hour, a day before; for an all-day
    // event 09:00 of the day, of the day before, two days and a week before.
    public static readonly IReadOnlyList<int> TimedChoices = [0, 5, 10, 15, 30, 60, 24 * 60];
    public static readonly IReadOnlyList<int> AllDayChoices = [-9 * 60, 24 * 60 - 9 * 60, 2 * 24 * 60 - 9 * 60, 7 * 24 * 60 - 9 * 60];
    public static IReadOnlyList<int> Choices(bool allDay) => allDay ? AllDayChoices : TimedChoices;

    // The reminder as kept, or null when there is none or it is out of range: a timed event's from its start to four weeks before;
    // an all-day event's from 23:59 of its first day to four weeks before.
    public static int? Normalize(int? remind, bool allDay) => remind is { } minutes && minutes <= Largest && minutes >= (allDay ? -(24 * 60 - 1) : 0) ? minutes : null;

    // A custom reminder ("提前 25 分钟"): an amount of minutes, hours or days before a timed event; for an all-day event, a number of
    // days before it at 09:00 (0: the day itself). Null when it is out of range.
    public enum Unit { Minutes, Hours, Days }
    public static int? Custom(int amount, Unit unit, bool allDay)
    {
        if (amount < 0) return null;
        long minutes = allDay ? (long)amount * 24 * 60 - 9 * 60 : unit switch { Unit.Hours => amount * 60L, Unit.Days => amount * 24L * 60, _ => amount };
        return minutes > Largest ? null : Normalize((int)minutes, allDay);
    }

    // A reminder taken apart as the custom choice shows it: the amount and its unit (the largest that divides it); for an all-day
    // event, the days before it when it is at 09:00. Null when the custom choice cannot show it.
    public static (int Amount, Unit Unit)? Parts(int remind, bool allDay)
    {
        if (allDay) return (remind + 9 * 60) % (24 * 60) == 0 && remind >= -9 * 60 ? ((remind + 9 * 60) / (24 * 60), Unit.Days) : null;
        if (remind < 0) return null;
        if (remind > 0 && remind % (24 * 60) == 0) return (remind / (24 * 60), Unit.Days);
        if (remind > 0 && remind % 60 == 0) return (remind / 60, Unit.Hours);
        return (remind, Unit.Minutes);
    }

    // An event that becomes all-day or timed in the form takes the other kind's default; none stays none.
    public static int? Switched(int? remind, bool allDay) => remind is null ? null : Default(allDay);

    // "不提醒", "开始时", "提前 15 分钟", "提前 2 小时", "提前 1 天"; all day "当天 09:00", "前一天 09:00", "提前 2 天 09:00", "提前 1 周 09:00".
    public static string Text(int? remind, bool allDay)
    {
        if (remind is not { } minutes) return Loc.T("不提醒", "No reminder");
        if (allDay)
        {
            int days = minutes <= 0 ? 0 : (minutes + 24 * 60 - 1) / (24 * 60);
            int ofDay = days * 24 * 60 - minutes;
            string clock = string.Create(CultureInfo.InvariantCulture, $"{ofDay / 60:00}:{ofDay % 60:00}");
            return days switch
            {
                0 => Loc.T($"当天 {clock}", $"On the day at {clock}"),
                1 => Loc.T($"前一天 {clock}", $"The day before at {clock}"),
                7 => Loc.T($"提前 1 周 {clock}", $"1 week before at {clock}"),
                _ => Loc.T($"提前 {days} 天 {clock}", $"{days} days before at {clock}")
            };
        }
        if (minutes == 0) return Loc.T("开始时", "At start");
        var (amount, unit) = Parts(minutes, false)!.Value;
        return unit switch
        {
            Unit.Days => Loc.T($"提前 {amount} 天", $"{Loc.Count(amount, "day", "days")} before"),
            Unit.Hours => Loc.T($"提前 {amount} 小时", $"{Loc.Count(amount, "hour", "hours")} before"),
            _ => Loc.T($"提前 {amount} 分钟", $"{Loc.Count(amount, "minute", "minutes")} before")
        };
    }

    // The reminders after after and up to upTo, earliest first.
    public static List<AgendaReminder> Due(IEnumerable<AgendaEvent> events, DateTime after, DateTime upTo)
    {
        var due = new List<AgendaReminder>();
        if (upTo <= after) return due;
        // A time reminds at most four weeks before its start, and an all-day one less than a day after it.
        var first = DateOnly.FromDateTime(after).AddDays(-1);
        var last = DateOnly.FromDateTime(upTo).AddDays(Largest / (24 * 60) + 1);
        foreach (var item in events)
        {
            if (item.Remind is null) continue;
            foreach (var time in item.Occurrences(first, last))
                if (time.RemindAt is { } at && at > after && at <= upTo) due.Add(new(time, at));
        }
        due.Sort((a, b) => a.At.CompareTo(b.At));
        return due;
    }

    // The first reminder after after, or null when there is none within four weeks and a day.
    public static DateTime? Next(IEnumerable<AgendaEvent> events, DateTime after)
        => Due(events, after, after.AddDays(Largest / (24 * 60) + 1)).FirstOrDefault()?.At;

    // ---- The notice ----

    public static string AppKey { get; } = NoticeKeys.Internal("agenda");
    // Its buttons: "查看日程" shows the event on the planner page; "5 分钟后再提醒" takes it away and reminds again later (the notices
    // page offers other delays too).
    public const string OpenAction = "brimdeck:agenda-open", SnoozeAction = "brimdeck:agenda-snooze";
    public const string IconRef = "glyph:calendar";

    public static bool Owns(Notice notice) => notice.Origin == NoticeOrigin.Internal && notice.AppKey == AppKey;

    // One time of an event names its notice: a second reminder of it (after "稍后提醒") takes the first one's place.
    public static string InstanceKey(AgendaEvent time) => "agenda:" + time.Id + ":" + time.Start.ToString("yyyyMMdd'T'HHmm", CultureInfo.InvariantCulture);

    // The event and the start of its time a reminder's notice is about, or null for any other key.
    public static (string Id, DateTime Start)? Parse(string instanceKey)
    {
        if (!instanceKey.StartsWith("agenda:", StringComparison.Ordinal)) return null;
        int split = instanceKey.LastIndexOf(':');
        if (split <= 7) return null;
        if (!DateTime.TryParseExact(instanceKey[(split + 1)..], "yyyyMMdd'T'HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) return null;
        return (instanceKey[7..split], DateTime.SpecifyKind(start, DateTimeKind.Unspecified));
    }

    // The time of the event that starts at start, or null when the event has no such time any more (deleted, moved, skipped).
    public static AgendaEvent? Find(AgendaEvent? item, DateTime start)
    {
        if (item is null) return null;
        var day = DateOnly.FromDateTime(start);
        if (!item.Occurs(day)) return null;
        var time = item.Occurrence(day);
        return time.Start == start ? time : null;
    }

    // The notice of a reminder. A timed event's carries its times, so the notice says "15 分钟后开始" and counts down as an event's
    // reminder from Outlook does; it is not BrimDeck's own timing (Native), so it reminds once and not again at the start. An
    // all-day event's says which day. missed: given late (BrimDeck was not running at its time); it is listed unread, not shown.
    public static NoticeInput Notice(AgendaEvent time, DateTime now, bool missed, TimeZoneInfo zone)
    {
        string body = time.Detail ?? (time.Source is { } source ? AgendaText.Source(source) : "");
        return new NoticeInput
        {
            Origin = NoticeOrigin.Internal, AppKey = AppKey, AppName = Loc.T("计划", "Planner"), IconRef = IconRef,
            InstanceKey = InstanceKey(time), Title = time.Title, Body = body, Context = AgendaText.When(time, DateOnly.FromDateTime(now)),
            CreatedAt = Offset(now, zone), Category = NoticeCategory.Message, Level = NoticeLevel.Persistent, Missed = missed,
            Timing = time.AllDay ? null : new NoticeTiming
            {
                Kind = NoticeTimeKind.Event, StartsAt = Offset(time.Start, zone), EndsAt = Offset(time.End, zone),
                RemindAt = time.RemindAt is { } at ? Offset(at, zone) : null
            },
            Actions = [new(OpenAction, Loc.T("查看日程", "View event"), NoticeActionKind.Reply, Primary: true), new(SnoozeAction, Loc.T("5 分钟后再提醒", "Remind in 5 min"))]
        };
    }

    // A wall time of the planner as a moment, read in zone.
    public static DateTimeOffset Offset(DateTime wall, TimeZoneInfo zone)
    {
        var time = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(time)) time = time.AddHours(1);
        return new DateTimeOffset(time, zone.GetUtcOffset(time));
    }
}
