using System.Globalization;
using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;

// The collapsed row's planner part (AgendaStrip): today's event at a time (under way, soon, later, all day) and its words, the
// to-dos with those past their due day, the note as one line, which kinds show in Auto and in All, when the clock changes it next,
// and the longest text. Also the planner's settings (their defaults, shape, saving, which belong to a profile), the part's place
// in the collapsed order, weeks from Sunday, a new event's length, and taking in another file's data (AgendaBook.Import).
static class AgendaStripTests
{
    private static DateTime T(string text) => DateTime.ParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    // A Sunday.
    private static readonly DateTime Now = T("2026-11-22 14:20");
    private static readonly DateOnly Today = D("2026-11-22");

    public static void Run(Action<string, bool> check)
    {
        try
        {
            Loc.Use(Loc.Chinese);
            Events(check);
            Todos(check);
            Notes(check);
            Shown(check);
            Clip(check);
            Settings(check);
            Calendar(check);
            Import(check);
        }
        finally { Loc.Use(Loc.Chinese); }
    }

    private static AgendaEvent Add(AgendaBook book, string title, string start, string end, bool allDay = false, AgendaRepeat repeat = AgendaRepeat.None)
        => book.AddEvent(title, allDay ? T(start + " 00:00") : T(start), allDay ? T(end + " 00:00") : T(end), allDay, Now, repeat: repeat)
            ?? throw new InvalidOperationException("event refused: " + title);

    private static void Events(Action<string, bool> check)
    {
        var book = new AgendaBook();
        Add(book, "整理本周计划", "2026-11-22 10:00", "2026-11-22 11:00");
        Add(book, "和小林对设计稿", "2026-11-22 14:00", "2026-11-22 15:00");
        Add(book, "取快递", "2026-11-22 16:30", "2026-11-22 17:00");
        var running = AgendaStrip.EventAt(book.Events, Now);
        check("Strip event: the one under way, with how far it is and how long is left",
            running is { State: AgendaStripState.Running, Event.Title: "和小林对设计稿" } && Math.Abs(running.Progress - 1.0 / 3) < 1e-9 && AgendaStrip.Time(running, Now) == "还剩 40 分钟");
        var later = AgendaStrip.EventAt(book.Events, T("2026-11-22 15:10"));
        check("Strip event: the next to start today, by its clock time while more than an hour away",
            later is { State: AgendaStripState.Later, Event.Title: "取快递" } && AgendaStrip.Time(later, T("2026-11-22 15:10")) == "16:30");
        var soon = AgendaStrip.EventAt(book.Events, T("2026-11-22 16:10"));
        var hour = AgendaStrip.EventAt(book.Events, T("2026-11-22 15:30"));
        check("Strip event: within an hour it says how long is left to it",
            soon is { State: AgendaStripState.Soon } && AgendaStrip.Time(soon, T("2026-11-22 16:10")) == "20 分钟后"
            && hour is { State: AgendaStripState.Soon } && AgendaStrip.Time(hour, T("2026-11-22 15:30")) == "1 小时后"
            && AgendaStrip.EventAt(book.Events, T("2026-11-22 15:29")) is { State: AgendaStripState.Later });
        check("Strip event: an ended event is not shown, and a day with none left has none",
            AgendaStrip.EventAt(book.Events, T("2026-11-22 11:00")) is { Event.Title: "和小林对设计稿", State: AgendaStripState.Later }
            && AgendaStrip.EventAt(book.Events, T("2026-11-22 17:00")) is null && AgendaStrip.EventAt(book.Events, T("2026-11-23 09:00")) is null);
        Add(book, "Alpha 测试截止", "2026-11-22", "2026-11-22", allDay: true);
        var allDay = AgendaStrip.EventAt(book.Events, T("2026-11-22 17:30"));
        check("Strip event: an all-day event shows while no timed one is under way or to come",
            allDay is { State: AgendaStripState.AllDay, Event.Title: "Alpha 测试截止" } && AgendaStrip.Time(allDay, T("2026-11-22 17:30")) == "全天"
            && AgendaStrip.EventAt(book.Events, Now) is { State: AgendaStripState.Running } && AgendaStrip.EventAt(book.Events, T("2026-11-22 15:10")) is { State: AgendaStripState.Later });

        var night = new AgendaBook();
        Add(night, "值守", "2026-11-22 22:00", "2026-11-23 02:00");
        var first = AgendaStrip.EventAt(night.Events, T("2026-11-22 23:00"));
        var second = AgendaStrip.EventAt(night.Events, T("2026-11-23 01:00"));
        check("Strip event: one that ends on a later day is under way without a time left; on its last day it counts down",
            first is { State: AgendaStripState.Running } && AgendaStrip.Time(first, T("2026-11-22 23:00")) == "进行中"
            && second is { State: AgendaStripState.Running } && AgendaStrip.Time(second, T("2026-11-23 01:00")) == "还剩 1 小时");
        var both = new AgendaBook();
        Add(both, "长会", "2026-11-22 13:00", "2026-11-22 18:00");
        Add(both, "短会", "2026-11-22 14:00", "2026-11-22 14:30");
        check("Strip event: of two under way, the one that ends first", AgendaStrip.EventAt(both.Events, Now) is { Event.Title: "短会" });
        var daily = new AgendaBook();
        Add(daily, "站会", "2026-11-20 09:30", "2026-11-20 09:45", repeat: AgendaRepeat.Daily);
        var time = AgendaStrip.EventAt(daily.Events, T("2026-11-22 09:00"));
        check("Strip event: a series shows today's time of it", time is { State: AgendaStripState.Soon } && time.Event.Start == T("2026-11-22 09:30") && AgendaStrip.Time(time, T("2026-11-22 09:00")) == "30 分钟后");

        check("Strip clock: a running or soon event changes by the minute",
            AgendaStrip.NextChange(book.Events, Now) == T("2026-11-22 14:21") && AgendaStrip.NextChange(book.Events, T("2026-11-22 16:10")) == T("2026-11-22 16:11"));
        check("Strip clock: a later event changes when it comes within the hour; none left, at midnight",
            AgendaStrip.NextChange(book.Events, T("2026-11-22 15:10")) == T("2026-11-22 15:30") && AgendaStrip.NextChange(book.Events, T("2026-11-22 17:30")) == T("2026-11-23 00:00")
            && AgendaStrip.NextChange([], Now) == T("2026-11-23 00:00"));
        Loc.Use(Loc.English);
        check("Strip event: the words in English",
            AgendaStrip.Time(running!, Now) == "40 minutes left" && AgendaStrip.Time(soon!, T("2026-11-22 16:10")) == "In 20 minutes" && AgendaStrip.Time(allDay!, Now) == "All day"
            && AgendaStrip.Overdue(2) == "2 overdue" && AgendaStrip.OverdueFor(1) == "1 day overdue");
        Loc.Use(Loc.Chinese);
    }

    private static void Todos(Action<string, bool> check)
    {
        var book = new AgendaBook();
        book.AddTodo("交报告", null, Now, due: D("2026-11-21"));
        book.AddTodo("交房租", Today, Now);
        book.AddTodo("给小林回邮件", Today, Now);
        var done = book.AddTodo("订周末的车票", Today, Now)!;
        book.SetDone(done.Id, true, Now);
        book.AddTodo("整理书架", null, Now);
        book.AddTodo("准备周一的演示", null, Now, due: D("2026-11-30"));
        book.AddTodo("昨天没做完的", D("2026-11-21"), Now);
        var todos = AgendaStrip.TodosAt(book.Todos, Today);
        check("Strip to-dos: today's unfinished ones and every one with a due day are counted; the ticked, the long-term and those left on an earlier day are not",
            todos is { Open: 4, Overdue: 1, More: 3 });
        check("Strip to-dos: the words are the first of today's own, though an overdue one stands before it", todos is { Text: "交房租", OverdueDays: 0 });
        var late = new AgendaBook();
        late.AddTodo("交报告", null, Now, due: D("2026-11-20"));
        check("Strip to-dos: with only overdue ones left, the first of them and how long it is overdue",
            AgendaStrip.TodosAt(late.Todos, Today) is { Open: 1, Overdue: 1, Text: "交报告", OverdueDays: 2, More: 0 });
        check("Strip to-dos: none unfinished, none shown", AgendaStrip.TodosAt([], Today) is null && AgendaStrip.TodosAt(book.Todos.Where(todo => todo.Done), Today) is null);
        check("Strip to-dos: the words of what follows them", AgendaStrip.Overdue(1) == "过期 1" && AgendaStrip.OverdueFor(2) == "过期 2 天");
    }

    private static void Notes(Action<string, bool> check)
    {
        check("Strip note: its lines are joined by one space", AgendaStrip.NoteOf("  本周重点：\n发版\r\n\n周报 \t体检 ") == "本周重点： 发版 周报 体检");
        check("Strip note: a blank one is none", AgendaStrip.NoteOf("  \n ") is null && AgendaStrip.NoteOf(null) is null);
        var book = new AgendaBook();
        book.SetNote(Today, "下午把周报发给老陈");
        book.SetNote(D("2026-11-21"), "昨天的");
        var content = AgendaStrip.Content(book.Events, book.Todos, book.Notes, Now);
        check("Strip content: today's note alone, and nothing else where there is nothing", content is { Note: "下午把周报发给老陈", Event: null, Todos: null });
    }

    private static void Shown(Action<string, bool> check)
    {
        var book = new AgendaBook();
        Add(book, "和小林对设计稿", "2026-11-22 14:00", "2026-11-22 15:00");
        Add(book, "取快递", "2026-11-22 18:30", "2026-11-22 19:00");
        book.AddTodo("交房租", Today, Now);
        book.SetNote(Today, "下午把周报发给老陈");
        AgendaStripContent At(string time) => AgendaStrip.Content(book.Events, book.Todos, book.Notes, T(time));
        static bool Is(IReadOnlyList<AgendaStripKind> shown, params AgendaStripKind[] kinds) => shown.SequenceEqual(kinds);
        check("Strip, all: every kind that has something, in the planner page's order",
            Is(AgendaStrip.Shown(At("2026-11-22 14:20"), AgendaStripMode.All, true, true, true), AgendaStripKind.Event, AgendaStripKind.Todo, AgendaStripKind.Note)
            && Is(AgendaStrip.Shown(At("2026-11-22 19:30"), AgendaStripMode.All, true, true, true), AgendaStripKind.Todo, AgendaStripKind.Note)
            && Is(AgendaStrip.Shown(At("2026-11-22 14:20"), AgendaStripMode.All, true, false, true), AgendaStripKind.Event, AgendaStripKind.Note));
        check("Strip, auto: an event under way or within the hour comes first",
            Is(AgendaStrip.Shown(At("2026-11-22 14:20"), AgendaStripMode.Auto, true, true, true), AgendaStripKind.Event)
            && Is(AgendaStrip.Shown(At("2026-11-22 17:40"), AgendaStripMode.Auto, true, true, true), AgendaStripKind.Event));
        check("Strip, auto: with the event more than an hour away, the note", Is(AgendaStrip.Shown(At("2026-11-22 15:10"), AgendaStripMode.Auto, true, true, true), AgendaStripKind.Note));
        book.SetNote(Today, null);
        check("Strip, auto: without a note, the to-dos", Is(AgendaStrip.Shown(At("2026-11-22 15:10"), AgendaStripMode.Auto, true, true, true), AgendaStripKind.Todo));
        check("Strip, auto: without a note or to-dos, the later event",
            Is(AgendaStrip.Shown(At("2026-11-22 15:10"), AgendaStripMode.Auto, true, false, true), AgendaStripKind.Event)
            && AgendaStrip.Shown(At("2026-11-22 19:30"), AgendaStripMode.Auto, true, false, true).Count == 0);
        book.SetNote(Today, "下午把周报发给老陈");
        check("Strip, auto: a kind switched off is passed over",
            Is(AgendaStrip.Shown(At("2026-11-22 14:20"), AgendaStripMode.Auto, false, true, true), AgendaStripKind.Note)
            && Is(AgendaStrip.Shown(At("2026-11-22 14:20"), AgendaStripMode.Auto, false, true, false), AgendaStripKind.Todo)
            && AgendaStrip.Shown(At("2026-11-22 14:20"), AgendaStripMode.Auto, false, false, false).Count == 0
            && AgendaStrip.Shown(At("2026-11-22 14:20"), AgendaStripMode.All, false, false, false).Count == 0);
        var content = At("2026-11-22 14:20");
        check("Strip: what a screen reader hears names every kind shown in full",
            AgendaStrip.Describe(content, [AgendaStripKind.Event, AgendaStripKind.Todo, AgendaStripKind.Note], Now) == "日程：还剩 40 分钟 和小林对设计稿\n待办：1 项未完成\n便签：下午把周报发给老陈"
            && AgendaStrip.Describe(content, [AgendaStripKind.Note], Now) == "便签：下午把周报发给老陈");
    }

    private static void Clip(Action<string, bool> check)
    {
        string twenty = "一二三四五六七八九十一二三四五六七八九十", forty = new('a', 40);
        check("Strip text: a Chinese character counts two, a letter one",
            AgendaStrip.Units("abc") == 3 && AgendaStrip.Units("中文") == 4 && AgendaStrip.Units("a中，b") == 6 && AgendaStrip.Units("") == 0 && AgendaStrip.Units("😀") == 2);
        check("Strip text: twenty Chinese characters or forty letters show whole", AgendaStrip.Clip(twenty) == twenty && AgendaStrip.Clip(forty) == forty && AgendaStrip.MaxUnits == 40);
        check("Strip text: longer, it is cut so that it and the ellipsis count forty",
            AgendaStrip.Clip(twenty + "一") == twenty[..19] + "…" && AgendaStrip.Units(AgendaStrip.Clip(twenty + "一")) == 40
            && AgendaStrip.Clip(forty + "a") == new string('a', 38) + "…");
        check("Strip text: a mixed text is cut on a whole character, and a space before the ellipsis goes",
            AgendaStrip.Clip("a" + twenty) == "a" + twenty[..18] + "…" && AgendaStrip.Clip(new string('a', 37) + " bcd") == new string('a', 37) + "…");
        check("Strip text: a character of two code units is kept or dropped whole",
            AgendaStrip.Clip(new string('a', 37) + "😀bc") == new string('a', 37) + "…" && AgendaStrip.Clip(new string('a', 36) + "😀bcd") == new string('a', 36) + "😀…"
            && AgendaStrip.Clip(new string('a', 37) + "😀b") == new string('a', 37) + "😀b");
    }

    private static void Settings(Action<string, bool> check)
    {
        var defaults = new DeckSettings();
        check("Planner settings: the part is on for the notch and the capsule, Auto, every kind, the event with its title, the to-dos as a number",
            defaults.NotchAgenda && defaults.CapsuleAgenda && defaults.AgendaStripMode == AgendaStripMode.Auto && defaults.AgendaStripEvents && defaults.AgendaStripTodos && defaults.AgendaStripNote
            && defaults.AgendaStripEventForm == AgendaStripEventForm.Title && defaults.AgendaStripTodoForm == AgendaStripTodoForm.Count);
        check("Planner settings: by default everything is as it was before there were settings",
            defaults.AgendaWeekStart == AgendaWeekStart.Monday && defaults.AgendaEventMinutes == 60 && defaults.AgendaRemindTimed == AgendaReminders.DefaultTimed
            && defaults.AgendaRemindAllDay == AgendaReminders.DefaultAllDay && defaults.AgendaRemind(false) == 15 && defaults.AgendaRemind(true) == -9 * 60
            && defaults.AgendaSnoozeMinutes == 5 && defaults.AgendaLeftTodos == AgendaLeftTodos.Ask && defaults.AgendaDoneTodos == AgendaDoneTodos.Stay && defaults.AgendaFromNotices);
        check("Planner part: shown for the notch and the capsule, never for the line", defaults.ShowsAgenda(CompactStyle.Notch) && defaults.ShowsAgenda(CompactStyle.Capsule) && !defaults.ShowsAgenda(CompactStyle.Line));
        check("Planner part: hides with the planner page, with its own switch, and with every kind switched off",
            !new DeckSettings { AgendaPage = false }.ShowsAgenda(CompactStyle.Notch) && !new DeckSettings { NotchAgenda = false }.ShowsAgenda(CompactStyle.Notch)
            && new DeckSettings { NotchAgenda = false }.ShowsAgenda(CompactStyle.Capsule)
            && !new DeckSettings { AgendaStripEvents = false, AgendaStripTodos = false, AgendaStripNote = false }.ShowsAgenda(CompactStyle.Capsule)
            && new DeckSettings { AgendaStripEvents = false, AgendaStripTodos = false }.ShowsAgenda(CompactStyle.Capsule));
        var odd = new DeckSettings
        {
            AgendaStripMode = (AgendaStripMode)9, AgendaStripEventForm = (AgendaStripEventForm)9, AgendaStripTodoForm = (AgendaStripTodoForm)9, AgendaWeekStart = (AgendaWeekStart)9,
            AgendaEventMinutes = 7, AgendaRemindTimed = -5, AgendaRemindAllDay = AgendaReminders.Largest + 1, AgendaSnoozeMinutes = 99, AgendaLeftTodos = (AgendaLeftTodos)9, AgendaDoneTodos = (AgendaDoneTodos)9
        };
        odd.Normalize();
        check("Planner settings: put in shape; a reminder out of range is none",
            odd.AgendaStripMode == AgendaStripMode.Auto && odd.AgendaStripEventForm == AgendaStripEventForm.Title && odd.AgendaStripTodoForm == AgendaStripTodoForm.Count
            && odd.AgendaWeekStart == AgendaWeekStart.Monday && odd.AgendaEventMinutes == 60 && odd.AgendaRemindTimed is null && odd.AgendaRemindAllDay is null && odd.AgendaSnoozeMinutes == 5
            && odd.AgendaLeftTodos == AgendaLeftTodos.Ask && odd.AgendaDoneTodos == AgendaDoneTodos.Stay);
        var kept = new DeckSettings { AgendaEventMinutes = 90, AgendaSnoozeMinutes = 30, AgendaRemindTimed = 60, AgendaRemindAllDay = 24 * 60 - 9 * 60 };
        kept.Normalize();
        check("Planner settings: the offered choices stay", kept.AgendaEventMinutes == 90 && kept.AgendaSnoozeMinutes == 30 && kept.AgendaRemindTimed == 60 && kept.AgendaRemindAllDay == 900);
        var saved = new DeckSettings
        {
            CapsuleAgenda = false, AgendaStripMode = AgendaStripMode.All, AgendaStripNote = false, AgendaStripEventForm = AgendaStripEventForm.Time, AgendaStripTodoForm = AgendaStripTodoForm.Text,
            AgendaWeekStart = AgendaWeekStart.Sunday, AgendaEventMinutes = 30, AgendaRemindTimed = null, AgendaRemindAllDay = 900, AgendaSnoozeMinutes = 15,
            AgendaLeftTodos = AgendaLeftTodos.Move, AgendaDoneTodos = AgendaDoneTodos.Hidden, AgendaFromNotices = false
        };
        var back = SettingsMigrations.Read(JsonSerializer.Serialize(saved));
        bool Same(DeckSettings read) => !read.CapsuleAgenda && read.NotchAgenda && read.AgendaStripMode == AgendaStripMode.All && !read.AgendaStripNote && read.AgendaStripEvents
            && read.AgendaStripEventForm == AgendaStripEventForm.Time && read.AgendaStripTodoForm == AgendaStripTodoForm.Text && read.AgendaWeekStart == AgendaWeekStart.Sunday
            && read.AgendaEventMinutes == 30 && read.AgendaRemindTimed is null && read.AgendaRemindAllDay == 900 && read.AgendaSnoozeMinutes == 15
            && read.AgendaLeftTodos == AgendaLeftTodos.Move && read.AgendaDoneTodos == AgendaDoneTodos.Hidden && !read.AgendaFromNotices;
        check("Planner settings: survive saving and copying, \"no reminder\" among them", Same(back) && Same(back.Copy()));
        var older = SettingsMigrations.Read("""{"AppVersion":"1.3.0","NotchOrder":[4,0,3,1,2]}""");
        check("Planner settings: a file from before them has the defaults, and the part after the others",
            older.NotchAgenda && older.AgendaRemindTimed == 15 && older.AgendaEventMinutes == 60 && older.NotchOrder[^1] == CompactPart.Agenda && older.NotchOrder[0] == CompactPart.Clock);
        check("Planner settings: what the part shows and the planner's habits are shared by every profile; the part's switches are a profile's",
            !Profiles.IsProfileKey(nameof(DeckSettings.AgendaStripMode)) && !Profiles.IsProfileKey(nameof(DeckSettings.AgendaStripNote)) && !Profiles.IsProfileKey(nameof(DeckSettings.AgendaWeekStart))
            && !Profiles.IsProfileKey(nameof(DeckSettings.AgendaRemindTimed)) && !Profiles.IsProfileKey(nameof(DeckSettings.AgendaFromNotices))
            && Profiles.IsProfileKey(nameof(DeckSettings.NotchAgenda)) && Profiles.IsProfileKey(nameof(DeckSettings.CapsuleAgenda)) && Profiles.IsProfileKey(nameof(DeckSettings.AgendaPage)));
        check("Planner part: number 9, before the equalizer and the clock by default",
            (int)CompactPart.Agenda == 9 && CompactParts.Default[^3] == CompactPart.Agenda && CompactParts.Default[^4] == CompactPart.Notices
            && CompactParts.Normalize([CompactPart.Agenda, CompactPart.Rings])[0] == CompactPart.Agenda);
    }

    private static void Calendar(Action<string, bool> check)
    {
        check("Planner weeks: from Sunday when chosen",
            AgendaCalendar.WeekStart(Today, AgendaWeekStart.Sunday) == Today && AgendaCalendar.WeekStart(D("2026-11-21"), AgendaWeekStart.Sunday) == D("2026-11-15")
            && AgendaCalendar.WeekStart(D("2026-11-21"), AgendaWeekStart.Monday) == D("2026-11-16") && AgendaCalendar.WeekStart(Today) == D("2026-11-16"));
        check("Planner weeks: the month view begins on the week's first day",
            AgendaCalendar.FirstShown(2026, 11, AgendaWeekStart.Sunday) == D("2026-11-01") && AgendaCalendar.FirstShown(2026, 12, AgendaWeekStart.Sunday) == D("2026-11-29")
            && AgendaCalendar.Rows(2026, 11, AgendaWeekStart.Sunday) == 5 && AgendaCalendar.Rows(2026, 2, AgendaWeekStart.Sunday) == 4 && AgendaCalendar.Rows(2026, 8, AgendaWeekStart.Sunday) == 6);
        check("Planner weeks: the weekdays in the order shown",
            AgendaCalendar.Weekdays().SequenceEqual([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday])
            && AgendaCalendar.Weekdays(AgendaWeekStart.Sunday)[0] == DayOfWeek.Sunday && AgendaCalendar.Weekdays(AgendaWeekStart.Sunday)[6] == DayOfWeek.Saturday);
        check("Planner: a new event lasts as long as chosen, an hour for a length not offered, and may end on the next day",
            AgendaCalendar.NewEventEnd(T("2026-11-22 14:30"), 30) == T("2026-11-22 15:00") && AgendaCalendar.NewEventEnd(T("2026-11-22 14:30"), 120) == T("2026-11-22 16:30")
            && AgendaCalendar.NewEventEnd(T("2026-11-22 14:30"), 7) == T("2026-11-22 15:30") && AgendaCalendar.NewEventEnd(T("2026-11-22 23:00"), 90) == T("2026-11-23 00:30")
            && AgendaCalendar.EventLengths.Contains(AgendaCalendar.DefaultEventLength));
        check("Planner: the island's snooze choices include the default", AgendaReminders.SnoozeChoices.Contains(AgendaReminders.DefaultSnooze) && AgendaReminders.DefaultSnooze == 5);
    }

    private static void Import(Action<string, bool> check)
    {
        var mine = new AgendaBook();
        var shared = Add(mine, "周会", "2026-11-23 10:00", "2026-11-23 11:00");
        var task = mine.AddTodo("交房租", Today, Now)!;
        mine.SetNote(Today, "甲");
        mine.SetNote(D("2026-11-20"), "同一段");
        var other = new AgendaBook(mine.Snapshot());
        other.UpdateEvent(shared.Id, "周会（对方改过）", T("2026-11-23 10:00"), T("2026-11-23 12:00"), false, null);
        Add(other, "体检", "2026-11-25 08:00", "2026-11-25 09:00");
        other.AddTodo("买猫粮", null, Now);
        other.RemoveTodo(task.Id);
        other.SetNote(Today, "乙");
        other.SetNote(D("2026-11-24"), "丙");
        int raised = 0;
        mine.Changed += () => raised++;
        var taken = mine.Import(other.Snapshot());
        check("Import: what is not here yet is added, and counted", taken is { Events: 1, Todos: 1, Notes: 2, Any: true } && raised == 1 && mine.Events.Count == 2 && mine.Todos.Count == 2);
        check("Import: nothing here is changed or removed", mine.FindEvent(shared.Id) is { Title: "周会" } && mine.FindTodo(task.Id) is not null);
        check("Import: a note of a day without one is taken; one of a day with another is added under it; the same one is left",
            mine.Note(D("2026-11-24")) == "丙" && mine.Note(Today) == "甲\n乙" && mine.Note(D("2026-11-20")) == "同一段");
        var again = mine.Import(other.Snapshot());
        check("Import: the same file again adds nothing and raises nothing", again is { Events: 0, Todos: 0, Notes: 0, Any: false } && raised == 1 && mine.Note(Today) == "甲\n乙");
        var text = AgendaStore.Serialize(other.Snapshot());
        var fresh = new AgendaBook();
        check("Import: an exported file read back gives everything to an empty planner",
            fresh.Import(AgendaStore.Parse(text)) is { Events: 2, Todos: 1, Notes: 3 } && fresh.FindEvent(shared.Id) is { Title: "周会（对方改过）" });
    }
}
