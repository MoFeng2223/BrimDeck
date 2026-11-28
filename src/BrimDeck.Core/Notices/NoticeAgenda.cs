using BrimDeck.Core.Agenda;

namespace BrimDeck.Core.Notices;

// What the notices page offers for a notice besides its own buttons: its times to add to the planner, its verification code to
// copy, or nothing but its words, which can become today's to-do. None for an incoming call and for the notices whose time
// BrimDeck keeps itself (an alarm, a timer, a stopwatch, an event sent to be reminded of).
public enum NoticeAgendaKind { None, Times, Code, Text }

// A time the notice can be added to the planner at. FromReminder: the notice is itself a reminder of an event (a calendar
// application's), and this is its start and end rather than a time read from its words.
public sealed record NoticeAgendaTime(NoticeMoment Moment, bool FromReminder)
{
    // The strip's second line: where the time comes from and how long it is taken to last.
    public string Detail => FromReminder ? Loc.T("来自提醒", "From the reminder") : NoticeMoments.Detail(Moment);

    // The same, with a time without an end taken to last eventMinutes (DeckSettings.AgendaEventMinutes): "从正文认出 · 时长按 30 分钟".
    public string DetailFor(int eventMinutes)
    {
        if (FromReminder || Moment.AllDay || Moment.End is not null) return Detail;
        string where = Moment.InTitle ? Loc.T("从标题认出", "From the title") : Loc.T("从正文认出", "From the body");
        string length = AgendaText.Length(AgendaCalendar.EventLengths.Contains(eventMinutes) ? eventMinutes : AgendaCalendar.DefaultEventLength);
        return where + Loc.T($" · 时长按 {length}", $" · {length} assumed");
    }
}

// What was read from one revision of a notice. Times are in the order offered, without the past ones only once Open is asked.
public sealed record NoticeAgendaRead(NoticeAgendaKind Kind, IReadOnlyList<NoticeAgendaTime> Times)
{
    public static readonly NoticeAgendaRead None = new(NoticeAgendaKind.None, []);
}

public static class NoticeAgenda
{
    // An event's title and a to-do's words taken from a notice are cut to these lengths (characters), ending in "…".
    public const int TitleLimit = 40, TodoLimit = 80;
    // A timed moment without an end is offered until this long after its start, as NoticeMoments reads one written in the past.
    private static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(10);

    // Reads the notice once per revision (the caller keeps the result): the reminder's own event first, then the times in its words
    // without one at the same moment. zone: the user's time zone, in which the reminder's start becomes wall time.
    // findTimes false (DeckSettings.AgendaFromNotices off): no time is looked for, neither a reminder's own nor one in the words, so
    // a notice that names one is a notice of words, which can still be added by hand; the verification code is found as before.
    public static NoticeAgendaRead Read(Notice notice, TimeZoneInfo zone, bool findTimes = true)
    {
        if (notice.Call) return NoticeAgendaRead.None;
        if (notice.Timing is { } timing && (timing.Native || timing.Kind is NoticeTimeKind.Alarm or NoticeTimeKind.Stopwatch or NoticeTimeKind.Countdown)) return NoticeAgendaRead.None;
        if (VerificationCode.Find(notice.Title, notice.Body) is not null) return new(NoticeAgendaKind.Code, []);
        if (!findTimes) return TodoText(notice).Length > 0 ? new(NoticeAgendaKind.Text, []) : NoticeAgendaRead.None;
        var times = new List<NoticeAgendaTime>();
        if (Reminder(notice.Timing, zone) is { } reminded) times.Add(new(reminded, true));
        foreach (var moment in NoticeMoments.Find(notice.Title, notice.Body, notice.CreatedAt == default ? notice.ReceivedAt : notice.CreatedAt, zone))
        {
            if (times.Count >= NoticeMoments.MaxCount) break;
            if (!times.Any(time => Same(time.Moment, moment))) times.Add(new(moment, false));
        }
        if (times.Count > 0) return new(NoticeAgendaKind.Times, times);
        return TodoText(notice).Length > 0 ? new(NoticeAgendaKind.Text, []) : NoticeAgendaRead.None;
    }

    // The read as it stands at now (local wall time): the times not yet over; with none left, a notice of times is one of words.
    public static NoticeAgendaRead Open(NoticeAgendaRead read, DateTime now)
    {
        if (read.Kind != NoticeAgendaKind.Times) return read;
        var open = read.Times.Where(time => !Over(time.Moment, now)).ToList();
        if (open.Count == read.Times.Count) return read;
        return open.Count > 0 ? new(NoticeAgendaKind.Times, open) : new(NoticeAgendaKind.Text, []);
    }

    // Over: a timed moment once its end has passed (without one, a while after its start); a date once its last day has passed.
    public static bool Over(NoticeMoment moment, DateTime now)
    {
        if (moment.AllDay) return (moment.End ?? moment.Start).Date < now.Date;
        return (moment.End ?? moment.Start + StartGrace) <= now;
    }

    // The times not added yet, given what the notice already added. An event the user moved in the planner no longer has the
    // time it was added at; once the notice added as many items as it offers times, every time counts as added.
    public static IReadOnlyList<NoticeAgendaTime> NotAdded(IReadOnlyList<NoticeAgendaTime> times, AgendaFromNotice added)
    {
        if (added.Events.Count + added.Todos.Count >= times.Count) return [];
        return times.Where(time => Added(time.Moment, added) is null).ToList();
    }

    // The event the notice added at this time, if any.
    public static AgendaEvent? Added(NoticeMoment moment, AgendaFromNotice added)
        => added.Events.FirstOrDefault(item => item.AllDay == moment.AllDay && item.Start == AgendaTime.Minute(moment.Start));

    // What the notice added last (the strip shows it, and its "撤销" takes it away first): an event or a to-do.
    public static (AgendaEvent? Event, AgendaTodo? Todo) Latest(AgendaFromNotice added)
    {
        var item = added.Events.LastOrDefault(); var todo = added.Todos.LastOrDefault();
        if (item is not null && (todo is null || item.Created >= todo.Created)) return (item, null);
        return (null, todo);
    }

    // The title of an event added at this time: the field the time was read from (the title as it is, the body's first line), else
    // the other one, else the application's name. A reminder's event takes the title first.
    public static string EventTitle(Notice notice, NoticeAgendaTime time)
    {
        string title = Line(notice.Title), body = Line(notice.Body);
        bool titleFirst = time.FromReminder || time.Moment.InTitle;
        string chosen = titleFirst ? (title.Length > 0 ? title : body) : (body.Length > 0 ? body : title);
        return Clip(chosen.Length > 0 ? chosen : Line(notice.AppName), TitleLimit);
    }

    // The words of a to-do made from the notice: the body's first line, else the title; empty when it has neither.
    public static string TodoText(Notice notice)
    {
        string body = Line(notice.Body);
        return Clip(body.Length > 0 ? body : Line(notice.Title), TodoLimit);
    }

    // The text cut to limit characters with "…" at its end; a surrogate pair is never split.
    public static string Clip(string text, int limit)
    {
        if (text.Length <= limit) return text;
        int cut = limit - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd() + "…";
    }

    // The start and end of an event the notice reminds of, as wall time in the zone. Starting and ending at midnight days apart,
    // it is all day, its end the last day itself (as NoticeMoment keeps one).
    private static NoticeMoment? Reminder(NoticeTiming? timing, TimeZoneInfo zone)
    {
        if (timing is not { Kind: NoticeTimeKind.Event, StartsAt: { } starts }) return null;
        var start = AgendaTime.Minute(TimeZoneInfo.ConvertTime(starts, zone).DateTime);
        DateTime? end = timing.EndsAt is { } ends && AgendaTime.Minute(TimeZoneInfo.ConvertTime(ends, zone).DateTime) is var local && local > start ? local : null;
        if (start.TimeOfDay == TimeSpan.Zero && end is { } last && last.TimeOfDay == TimeSpan.Zero)
            return new NoticeMoment(start, last.AddDays(-1) is var final && final > start ? final : null, true, false, 0, 0, "");
        return new NoticeMoment(start, end, false, false, 0, 0, "");
    }

    private static bool Same(NoticeMoment a, NoticeMoment b) => a.AllDay == b.AllDay && AgendaTime.Minute(a.Start) == AgendaTime.Minute(b.Start);

    private static string Line(string? text) => NoticeText.Summary(text ?? "");
}
