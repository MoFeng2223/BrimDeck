using BrimDeck.Core;
using BrimDeck.Core.Agenda;
using BrimDeck.Core.Notices;

// What the notices page offers for a notice (NoticeAgenda): which notices offer times, a code or a to-do, a reminder's own event
// before the times in its words, the past dropped, the times already added, and the titles and words taken. A made-up zone.
internal static class NoticeAgendaTests
{
    private static readonly TimeZoneInfo China = TimeZoneInfo.CreateCustomTimeZone("Test/AgendaChina", TimeSpan.FromHours(8), "Test China", "Test China");
    // 2026-11-22 14:18 in UTC+8, a Sunday, as in the design.
    private static readonly DateTimeOffset Sunday = new(2026, 11, 22, 14, 18, 0, TimeSpan.FromHours(8));
    private static readonly DateTime Now = new(2026, 11, 22, 14, 20, 0);

    private static Notice Made(string title, string body, NoticeTiming? timing = null, bool call = false) => new()
    {
        AppKey = "windows:Outlook", AppName = "Outlook", Title = title, Body = body, CreatedAt = Sunday, ReceivedAt = Sunday, Timing = timing, Call = call
    };

    public static void Run(Action<string, bool> check)
    {
        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try { Checks(check); }
        finally { Loc.Use(language); }
    }

    private static void Checks(Action<string, bool> check)
    {
        // Kinds.
        var meeting = Made("周会改到周四下午三点", "王敏：会议室改到 5 楼，请确认能否参加。");
        var read = NoticeAgenda.Read(meeting, China);
        check("Notice agenda: a time in the title is offered", read.Kind == NoticeAgendaKind.Times && read.Times is [{ FromReminder: false } only] && only.Moment.Start == new DateTime(2026, 11, 26, 15, 0, 0));
        check("Notice agenda: a code is offered as a code, with no times",
            NoticeAgenda.Read(Made("GitHub", "你的验证码是 482913，10 分钟内有效"), China) is { Kind: NoticeAgendaKind.Code, Times.Count: 0 });
        check("Notice agenda: plain words can become a to-do", NoticeAgenda.Read(Made("产品群", "小周：好的，更新说明我来写"), China).Kind == NoticeAgendaKind.Text);
        check("Notice agenda: a title alone can become a to-do", NoticeAgenda.Read(Made("记得交周报", ""), China).Kind == NoticeAgendaKind.Text);
        check("Notice agenda: a notice without words offers nothing", NoticeAgenda.Read(Made("", " "), China).Kind == NoticeAgendaKind.None);
        check("Notice agenda: an incoming call offers nothing", NoticeAgenda.Read(Made("王敏", "明天下午三点开会", call: true), China).Kind == NoticeAgendaKind.None);
        foreach (var kind in new[] { NoticeTimeKind.Alarm, NoticeTimeKind.Stopwatch, NoticeTimeKind.Countdown })
            check($"Notice agenda: a {kind} offers nothing", NoticeAgenda.Read(Made("闹钟", "明天下午三点", new NoticeTiming { Kind = kind, StartsAt = Sunday }), China).Kind == NoticeAgendaKind.None);
        check("Notice agenda: an event BrimDeck reminds of itself offers nothing",
            NoticeAgenda.Read(Made("站会", "明天上午十点", new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = Sunday.AddDays(1), Native = true }), China).Kind == NoticeAgendaKind.None);

        // A reminder's own event comes first, in wall time; the same moment in its words is not offered again.
        var reminder = Made("评审会", "周四下午三点 · 3 号会议室", new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = new DateTimeOffset(2026, 11, 26, 7, 0, 0, TimeSpan.Zero), EndsAt = new DateTimeOffset(2026, 11, 26, 8, 30, 0, TimeSpan.Zero) });
        var reminded = NoticeAgenda.Read(reminder, China);
        check("Notice agenda: a reminder's event is offered first, in local wall time, with its end",
            reminded.Times is [{ FromReminder: true } first] && first.Moment.Start == new DateTime(2026, 11, 26, 15, 0, 0) && first.Moment.End == new DateTime(2026, 11, 26, 16, 30, 0) && !first.Moment.AllDay);
        check("Notice agenda: a reminder says where its time is from", reminded.Times[0].Detail == "来自提醒");
        var other = Made("评审会", "周五上午十点", new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = new DateTimeOffset(2026, 11, 26, 7, 0, 0, TimeSpan.Zero) });
        check("Notice agenda: a different time in a reminder's words follows its event", NoticeAgenda.Read(other, China).Times is [{ FromReminder: true }, { FromReminder: false } second] && second.Moment.Start == new DateTime(2026, 11, 27, 10, 0, 0));
        var allDay = Made("年假", "", new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = new DateTimeOffset(2026, 11, 25, 16, 0, 0, TimeSpan.Zero), EndsAt = new DateTimeOffset(2026, 11, 27, 16, 0, 0, TimeSpan.Zero) });
        check("Notice agenda: a reminder from midnight to midnight is all day, its end the last day",
            NoticeAgenda.Read(allDay, China).Times is [var whole] && whole.Moment.AllDay && whole.Moment.Start == new DateTime(2026, 11, 26) && whole.Moment.End == new DateTime(2026, 11, 27));
        var oneDay = Made("年假", "", new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = new DateTimeOffset(2026, 11, 25, 16, 0, 0, TimeSpan.Zero), EndsAt = new DateTimeOffset(2026, 11, 26, 16, 0, 0, TimeSpan.Zero) });
        check("Notice agenda: a one-day all-day reminder has no end", NoticeAgenda.Read(oneDay, China).Times is [{ Moment: { AllDay: true, End: null } }]);

        // The past is not offered; with nothing left, the notice is one of words.
        var soon = Made("站会", "今天下午三点开站会");
        var soonRead = NoticeAgenda.Read(soon, China);
        check("Notice agenda: a time later today is open", NoticeAgenda.Open(soonRead, Now).Times.Count == 1);
        check("Notice agenda: a time just begun is still open", NoticeAgenda.Open(soonRead, new DateTime(2026, 11, 22, 15, 9, 0)).Times.Count == 1);
        check("Notice agenda: a time long begun is over, and the notice becomes one of words",
            NoticeAgenda.Open(soonRead, new DateTime(2026, 11, 22, 15, 10, 0)) is { Kind: NoticeAgendaKind.Text, Times.Count: 0 });
        var ranged = new NoticeMoment(new DateTime(2026, 11, 22, 15, 0, 0), new DateTime(2026, 11, 22, 17, 0, 0), false, false, 0, 0, "");
        check("Notice agenda: a time with an end is open until its end", !NoticeAgenda.Over(ranged, new DateTime(2026, 11, 22, 16, 59, 0)) && NoticeAgenda.Over(ranged, new DateTime(2026, 11, 22, 17, 0, 0)));
        var days = new NoticeMoment(new DateTime(2026, 11, 22), new DateTime(2026, 11, 23), true, false, 0, 0, "");
        check("Notice agenda: an all-day time is open through its last day", !NoticeAgenda.Over(days, new DateTime(2026, 11, 23, 23, 59, 0)) && NoticeAgenda.Over(days, new DateTime(2026, 11, 24)));

        // Added.
        var book = new AgendaBook();
        var two = Made("评审时间", "周四下午三点或者周五上午十点");
        var both = NoticeAgenda.Read(two, China).Times;
        check("Notice agenda: two times are both offered", both.Count == 2);
        check("Notice agenda: nothing added, both are open", NoticeAgenda.NotAdded(both, book.FromNotice(two.Id)).Count == 2);
        var added = book.AddEvent(NoticeAgenda.EventTitle(two, both[0]), both[0].Moment.Start, both[0].Moment.End, both[0].Moment.AllDay, Now, source: AgendaSource.From(two));
        var from = book.FromNotice(two.Id);
        check("Notice agenda: the added time is found by its start", NoticeAgenda.Added(both[0].Moment, from)?.Id == added?.Id && NoticeAgenda.Added(both[1].Moment, from) is null);
        check("Notice agenda: the other time is still offered", NoticeAgenda.NotAdded(both, from) is [var left] && left == both[1]);
        book.UpdateEvent(added!.Id, added.Title, new DateTime(2026, 11, 28, 9, 0, 0), null, false, null);
        check("Notice agenda: an added event moved elsewhere still leaves the other time offered", NoticeAgenda.NotAdded(both, book.FromNotice(two.Id)).Count == 2);
        book.AddTodo("记一下", new DateOnly(2026, 11, 22), Now.AddMinutes(1), source: AgendaSource.From(two));
        check("Notice agenda: as many items as times, every time counts as added", NoticeAgenda.NotAdded(both, book.FromNotice(two.Id)).Count == 0);
        check("Notice agenda: the latest item is the to-do added last", NoticeAgenda.Latest(book.FromNotice(two.Id)) is { Event: null, Todo.Text: "记一下" });
        check("Notice agenda: nothing added has no latest", NoticeAgenda.Latest(book.FromNotice("none")) is { Event: null, Todo: null });

        // Titles and words.
        check("Notice agenda: a time from the title takes the title", NoticeAgenda.EventTitle(meeting, read.Times[0]) == "周会改到周四下午三点");
        var inBody = Made("王敏", "周四下午三点开周会\n地点：5 楼");
        check("Notice agenda: a time from the body takes the body's first line", NoticeAgenda.EventTitle(inBody, NoticeAgenda.Read(inBody, China).Times[0]) == "周四下午三点开周会");
        var untitled = Made("", "周四下午三点开周会");
        check("Notice agenda: a reminder without a title takes the body", NoticeAgenda.EventTitle(untitled, new NoticeAgendaTime(read.Times[0].Moment, true)) == "周四下午三点开周会");
        var bare = Made("", "");
        check("Notice agenda: without words an event takes the application's name", NoticeAgenda.EventTitle(bare, read.Times[0]) == "Outlook");
        string longTitle = new('长', 60);
        string clipped = NoticeAgenda.EventTitle(Made(longTitle + "周四下午三点", ""), new NoticeAgendaTime(read.Times[0].Moment with { InTitle = true }, false));
        check("Notice agenda: a long title is cut with an ellipsis", clipped.Length == NoticeAgenda.TitleLimit && clipped.EndsWith('…'));
        check("Notice agenda: a to-do takes the body's first line, its spaces collapsed", NoticeAgenda.TodoText(Made("产品群", "  小周：好的，   更新说明我来写\n第二行")) == "小周：好的， 更新说明我来写");
        check("Notice agenda: a to-do without a body takes the title", NoticeAgenda.TodoText(Made("记得交周报", "")) == "记得交周报");
        check("Notice agenda: a long to-do is cut", NoticeAgenda.TodoText(Made("", new string('字', 200))).Length == NoticeAgenda.TodoLimit);
        check("Notice agenda: a cut never splits a surrogate pair", NoticeAgenda.Clip("ab😀cd", 4) == "ab…" && NoticeAgenda.Clip("abc", 3) == "abc");
    }
}
