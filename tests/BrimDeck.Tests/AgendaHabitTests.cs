using System.Globalization;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;
using BrimDeck.Core.Notices;

// The planner following its settings: where ticked to-dos are listed (AgendaQuery.Arranged), a reminder made the other kind with
// the settings' defaults (AgendaReminders.Switched), the island's snooze button, a notice read without looking for times
// (NoticeAgenda.Read), and the length a time without an end is taken to last (NoticeAgendaTime.DetailFor, AgendaText.Length).
static class AgendaHabitTests
{
    private static DateTime T(string text) => DateTime.ParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    // A Sunday.
    private static readonly DateTime Now = T("2026-11-22 14:20");
    private static readonly DateOnly Today = D("2026-11-22");
    private static readonly TimeZoneInfo China = TimeZoneInfo.CreateCustomTimeZone("Test/HabitChina", TimeSpan.FromHours(8), "Test China", "Test China");
    private static readonly DateTimeOffset Sunday = new(2026, 11, 22, 14, 18, 0, TimeSpan.FromHours(8));

    public static void Run(Action<string, bool> check)
    {
        string language = Loc.Language;
        try
        {
            Loc.Use(Loc.Chinese);
            DoneTodos(check);
            Reminders(check);
            Snooze(check);
            Notices(check);
        }
        finally { Loc.Use(language); }
    }

    private static void DoneTodos(Action<string, bool> check)
    {
        var book = new AgendaBook();
        var first = book.AddTodo("交房租", Today, Now)!;
        var second = book.AddTodo("给小林回邮件", Today, Now.AddMinutes(1))!;
        var third = book.AddTodo("订车票", Today, Now.AddMinutes(2))!;
        var fourth = book.AddTodo("取快递", Today, Now.AddMinutes(3))!;
        var someday = book.AddTodo("整理书架", null, Now.AddMinutes(4))!;
        var later = book.AddTodo("学吉他", null, Now.AddMinutes(5))!;
        book.SetDone(first.Id, true, Now);
        book.SetDone(third.Id, true, Now);
        book.SetDone(someday.Id, true, Now);
        var todos = book.TodosOf(Today, Today);
        static string Ids(IEnumerable<AgendaTodo> list) => string.Join(",", list.Select(todo => todo.Text));

        var stay = AgendaQuery.Arranged(todos, AgendaDoneTodos.Stay);
        check("Planner habits: ticked to-dos stay in place by default",
            ReferenceEquals(stay, todos) && Ids(stay.Main) == "交房租,给小林回邮件,订车票,取快递" && Ids(stay.LongTerm) == "整理书架,学吉他");
        var last = AgendaQuery.Arranged(todos, AgendaDoneTodos.Last);
        check("Planner habits: Last lists unfinished to-dos first and ticked ones after them, each in their order",
            Ids(last.Main) == "给小林回邮件,取快递,交房租,订车票");
        check("Planner habits: Last orders the long-term group on its own", Ids(last.LongTerm) == "学吉他,整理书架");
        var hidden = AgendaQuery.Arranged(todos, AgendaDoneTodos.Hidden);
        check("Planner habits: Hidden leaves ticked to-dos out of both lists", Ids(hidden.Main) == "给小林回邮件,取快递" && Ids(hidden.LongTerm) == "学吉他");
        check("Planner habits: where ticked to-dos go does not change the counts",
            last.Open == todos.Open && hidden.Open == todos.Open && todos.Open == 2 && book.Counts(Today, Today).OpenTodos == 2);
        var allDone = new AgendaBook();
        var only = allDone.AddTodo("写周报", Today, Now)!;
        allDone.SetDone(only.Id, true, Now);
        check("Planner habits: a day whose to-dos are all ticked lists none when they are hidden",
            AgendaQuery.Arranged(allDone.TodosOf(Today, Today), AgendaDoneTodos.Hidden) is { Main.Count: 0, LongTerm.Count: 0, Open: 0 });
        // The row of to-dos left on earlier days is kept as it was.
        var left = new AgendaBook();
        left.AddTodo("昨天的事", Today.AddDays(-1), Now.AddDays(-1));
        check("Planner habits: the row of to-dos left on earlier days stays whatever the choice",
            AgendaQuery.Arranged(left.TodosOf(Today, Today), AgendaDoneTodos.Hidden).CarryOver is { Count: 1, AllYesterday: true });
        check("Planner habits: moving to today leaves nothing for the row", left.MoveToToday(Today) == 1 && left.TodosOf(Today, Today).CarryOver is null);
    }

    private static void Reminders(Action<string, bool> check)
    {
        var settings = new DeckSettings();
        check("Planner habits: a new event's reminders are the defaults until changed",
            settings.AgendaRemind(false) == AgendaReminders.DefaultTimed && settings.AgendaRemind(true) == AgendaReminders.DefaultAllDay);
        check("Planner habits: made all day, a reminder takes the settings' all-day default",
            AgendaReminders.Switched(30, true, new DeckSettings { AgendaRemindAllDay = 24 * 60 - 9 * 60 }) == 24 * 60 - 9 * 60);
        check("Planner habits: made timed, a reminder takes the settings' timed default",
            AgendaReminders.Switched(-9 * 60, false, new DeckSettings { AgendaRemindTimed = 60 }) == 60);
        check("Planner habits: no reminder stays none, whatever the settings",
            AgendaReminders.Switched(null, true, settings) is null && AgendaReminders.Switched(null, false, settings) is null);
        check("Planner habits: where the settings' default for the kind is none, the reminder becomes none",
            AgendaReminders.Switched(15, true, new DeckSettings { AgendaRemindAllDay = null }) is null);
        check("Planner habits: without settings the switch takes the built-in defaults",
            AgendaReminders.Switched(15, true) == AgendaReminders.DefaultAllDay && AgendaReminders.Switched(15, true, settings) == AgendaReminders.DefaultAllDay);
    }

    private static void Snooze(Action<string, bool> check)
    {
        var book = new AgendaBook();
        var parcel = book.AddEvent("取快递", T("2026-11-22 16:30"), T("2026-11-22 17:00"), false, Now, remind: 15)!;
        var plain = AgendaReminders.Notice(parcel, T("2026-11-22 16:15"), false, China);
        var later = AgendaReminders.Notice(parcel, T("2026-11-22 16:15"), false, China, 15);
        check("Planner habits: the island's snooze button says 5 minutes by default", plain.Actions[1] is { Key: AgendaReminders.SnoozeAction, Label: "5 分钟后再提醒" });
        check("Planner habits: the snooze button says the minutes the settings have", later.Actions[1] is { Key: AgendaReminders.SnoozeAction, Label: "15 分钟后再提醒" });
        check("Planner habits: every snooze choice is used as it is, another one as the default",
            AgendaReminders.SnoozeChoices.All(minutes => AgendaReminders.SnoozeMinutes(minutes) == minutes) && AgendaReminders.SnoozeMinutes(7) == AgendaReminders.DefaultSnooze
            && AgendaReminders.SnoozeText(0) == "5 分钟后再提醒");
        Loc.Use(Loc.English);
        check("Planner habits: the snooze button in English", AgendaReminders.SnoozeText(30) == "Remind in 30 min");
        Loc.Use(Loc.Chinese);
    }

    private static Notice Made(string title, string body, NoticeTiming? timing = null, bool call = false) => new()
    {
        AppKey = "windows:Outlook", AppName = "Outlook", Title = title, Body = body, CreatedAt = Sunday, ReceivedAt = Sunday, Timing = timing, Call = call
    };

    private static void Notices(Action<string, bool> check)
    {
        var meeting = Made("产品群", "周四下午三点开周会");
        check("Planner habits: a notice that names a time offers it while times are looked for", NoticeAgenda.Read(meeting, China).Kind == NoticeAgendaKind.Times);
        check("Planner habits: not looking for times, a notice that names one is one of words, which can be added by hand",
            NoticeAgenda.Read(meeting, China, findTimes: false) is { Kind: NoticeAgendaKind.Text, Times.Count: 0 });
        var reminder = Made("周会", "会议室 A", new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = Sunday.AddDays(1) });
        check("Planner habits: not looking for times, a calendar reminder's own time is not offered either",
            NoticeAgenda.Read(reminder, China, findTimes: false).Kind == NoticeAgendaKind.Text && NoticeAgenda.Read(reminder, China).Kind == NoticeAgendaKind.Times);
        check("Planner habits: a verification code is found whether times are looked for or not",
            NoticeAgenda.Read(Made("GitHub", "你的验证码是 482913，10 分钟内有效"), China, findTimes: false).Kind == NoticeAgendaKind.Code);
        check("Planner habits: an incoming call and a notice without words still offer nothing",
            NoticeAgenda.Read(Made("王敏", "明天下午三点开会", call: true), China, findTimes: false).Kind == NoticeAgendaKind.None
            && NoticeAgenda.Read(Made("", " "), China, findTimes: false).Kind == NoticeAgendaKind.None);

        var time = NoticeAgenda.Read(meeting, China).Times[0];
        check("Planner habits: a time without an end is said to last the new event's length",
            time.DetailFor(30) == "从正文认出 · 时长按 30 分钟" && time.DetailFor(90) == "从正文认出 · 时长按 1.5 小时");
        check("Planner habits: at the default length the words are the reader's own", time.DetailFor(60) == time.Detail && time.DetailFor(7) == time.Detail);
        var ranged = NoticeAgenda.Read(Made("周会", "周四 15:00-16:30 开周会"), China).Times[0];
        check("Planner habits: a time with an end says nothing of a length", ranged.Moment.End is not null && ranged.DetailFor(30) == ranged.Detail);
        check("Planner habits: lengths are written in minutes and hours",
            AgendaText.Length(15) == "15 分钟" && AgendaText.Length(60) == "1 小时" && AgendaText.Length(120) == "2 小时");
        Loc.Use(Loc.English);
        check("Planner habits: lengths and the assumed length in English",
            AgendaText.Length(45) == "45 minutes" && AgendaText.Length(60) == "1 hour" && AgendaText.Length(90) == "1.5 hours" && time.DetailFor(45) == "From the body · 45 minutes assumed");
        Loc.Use(Loc.Chinese);
    }
}
