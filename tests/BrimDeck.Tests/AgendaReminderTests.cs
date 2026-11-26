using System.Globalization;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;
using BrimDeck.Core.Notices;

// The planner's reminders (AgendaReminders): the choices and their words, the custom amount, what is kept in range, agenda.json's
// "remind" and "remindedTo", the reminder kept, set and dropped by the book's changes, which reminders fall in a span (a series,
// a skipped day, an all-day event) and the notice a reminder is given as.
static class AgendaReminderTests
{
    private static DateTime T(string text) => DateTime.ParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static readonly DateTime Now = T("2026-11-22 14:20");

    public static void Run(string root, Action<string, bool> check)
    {
        try
        {
            Loc.Use(Loc.Chinese);
            Words(check);
            Book(check);
            File(Path.Combine(root, "agenda-remind"), check);
            Due(check);
            Notice(check);
        }
        finally { Loc.Use(Loc.Chinese); }
    }

    private static void Words(Action<string, bool> check)
    {
        check("Reminders: a timed event's choices read as offered",
            string.Join("|", AgendaReminders.TimedChoices.Select(m => AgendaReminders.Text(m, false))) == "开始时|提前 5 分钟|提前 10 分钟|提前 15 分钟|提前 30 分钟|提前 1 小时|提前 1 天"
            && AgendaReminders.Text(null, false) == "不提醒");
        check("Reminders: an all-day event's choices are clock times of the day and of days before",
            string.Join("|", AgendaReminders.AllDayChoices.Select(m => AgendaReminders.Text(m, true))) == "当天 09:00|前一天 09:00|提前 2 天 09:00|提前 1 周 09:00"
            && AgendaReminders.Text(24 * 60, true) == "前一天 00:00" && AgendaReminders.Text(-(23 * 60 + 30), true) == "当天 23:30");
        check("Reminders: a custom amount is worded in its largest whole unit",
            AgendaReminders.Text(25, false) == "提前 25 分钟" && AgendaReminders.Text(120, false) == "提前 2 小时" && AgendaReminders.Text(3 * 24 * 60, false) == "提前 3 天"
            && AgendaReminders.Text(90, false) == "提前 90 分钟");
        check("Reminders: the custom amount turns into minutes, an all-day one into days before at 09:00",
            AgendaReminders.Custom(25, AgendaReminders.Unit.Minutes, false) == 25 && AgendaReminders.Custom(2, AgendaReminders.Unit.Hours, false) == 120
            && AgendaReminders.Custom(1, AgendaReminders.Unit.Days, false) == 24 * 60 && AgendaReminders.Custom(0, AgendaReminders.Unit.Days, true) == -9 * 60
            && AgendaReminders.Custom(2, AgendaReminders.Unit.Days, true) == 2 * 24 * 60 - 9 * 60);
        check("Reminders: more than four weeks or a negative amount makes no reminder",
            AgendaReminders.Custom(29, AgendaReminders.Unit.Days, false) is null && AgendaReminders.Custom(-1, AgendaReminders.Unit.Minutes, false) is null
            && AgendaReminders.Custom(700, AgendaReminders.Unit.Hours, false) is null);
        check("Reminders: a timed event's reminder is not after its start; an all-day one's is within its first day",
            AgendaReminders.Normalize(-1, false) is null && AgendaReminders.Normalize(0, false) == 0 && AgendaReminders.Normalize(-9 * 60, true) == -9 * 60
            && AgendaReminders.Normalize(-24 * 60, true) is null && AgendaReminders.Normalize(AgendaReminders.Largest + 1, false) is null);
        check("Reminders: the custom field takes a reminder apart",
            AgendaReminders.Parts(120, false) == (2, AgendaReminders.Unit.Hours) && AgendaReminders.Parts(90, false) == (90, AgendaReminders.Unit.Minutes)
            && AgendaReminders.Parts(2 * 24 * 60 - 9 * 60, true) == (2, AgendaReminders.Unit.Days) && AgendaReminders.Parts(24 * 60, true) is null);
        check("Reminders: an event made all-day or timed takes the other kind's default, none stays none",
            AgendaReminders.Switched(15, true) == AgendaReminders.DefaultAllDay && AgendaReminders.Switched(-9 * 60, false) == AgendaReminders.DefaultTimed
            && AgendaReminders.Switched(null, true) is null);
        Loc.Use(Loc.English);
        check("Reminders: the words in English",
            AgendaReminders.Text(15, false) == "15 minutes before" && AgendaReminders.Text(60, false) == "1 hour before" && AgendaReminders.Text(900, true) == "The day before at 09:00"
            && AgendaReminders.Text(null, false) == "No reminder");
        Loc.Use(Loc.Chinese);
    }

    private static void Book(Action<string, bool> check)
    {
        var book = new AgendaBook();
        var item = book.AddEvent("取快递", T("2026-11-22 16:30"), T("2026-11-22 17:00"), false, Now, remind: 15)!;
        check("Reminders: an event keeps its reminder and when it reminds", item.Remind == 15 && item.RemindAt == T("2026-11-22 16:15"));
        var moved = book.UpdateEvent(item.Id, "取快递", T("2026-11-22 18:00"), T("2026-11-22 18:30"), false, null)!;
        check("Reminders: a change that does not name the reminder keeps it, moved with the event", moved.Remind == 15 && moved.RemindAt == T("2026-11-22 17:45"));
        var none = book.UpdateEvent(item.Id, "取快递", T("2026-11-22 18:00"), T("2026-11-22 18:30"), false, null, null, null)!;
        check("Reminders: a change that sets no reminder removes it", none.Remind is null && none.RemindAt is null);
        check("Reminders: a reminder after a timed event's start is refused", book.AddEvent("会", T("2026-11-23 10:00"), null, false, Now, remind: -30)!.Remind is null);
        var todo = book.AddTodo("给小周回电话", D("2026-11-22"), Now)!;
        var made = book.TodoToEvent(todo.Id, T("2026-11-22 19:00"), null, false, remind: 10)!;
        check("Reminders: a to-do given a time takes the form's reminder", made.Remind == 10);
        check("Reminders: an event taken back to a to-do loses its reminder", book.EventToTodo(made.Id, D("2026-11-22")) is not null && book.FindEvent(made.Id) is null);

        var daily = book.AddEvent("站会", T("2026-11-23 09:30"), T("2026-11-23 10:00"), false, Now, repeat: AgendaRepeat.Daily, remind: 5)!;
        var time = AgendaReminders.Find(book.FindEvent(daily.Id), T("2026-11-25 09:30"));
        check("Reminders: each time of a series reminds before its own start", time is { } found && found.RemindAt == T("2026-11-25 09:25") && found.Id == daily.Id);
        check("Reminders: a time the series does not have is not found",
            AgendaReminders.Find(book.FindEvent(daily.Id), T("2026-11-25 10:30")) is null && AgendaReminders.Find(book.FindEvent(daily.Id), T("2026-11-22 09:30")) is null);

        var other = new AgendaBook { RemindedTo = T("2026-11-22 14:00") };
        other.MergeLoaded(new AgendaData { RemindedTo = T("2026-11-22 13:00") });
        var later = new AgendaBook { RemindedTo = T("2026-11-22 12:00") };
        later.MergeLoaded(new AgendaData { RemindedTo = T("2026-11-22 13:00") });
        var loaded = new AgendaBook { RemindedTo = T("2026-11-22 14:00") };
        loaded.Load(new AgendaData());
        check("Reminders: merging what the file held keeps the later time reminded up to; loading takes the file's",
            other.RemindedTo == T("2026-11-22 14:00") && later.RemindedTo == T("2026-11-22 13:00") && loaded.RemindedTo is null);
    }

    private static void File(string folder, Action<string, bool> check)
    {
        Directory.CreateDirectory(folder);
        var store = new AgendaStore(folder);
        var book = new AgendaBook();
        book.AddEvent("取快递", T("2026-11-22 16:30"), T("2026-11-22 17:00"), false, Now, remind: 15);
        book.AddEvent("小周生日", T("2026-11-24 00:00"), null, true, Now, remind: -9 * 60);
        book.AddEvent("整理", T("2026-11-22 10:00"), T("2026-11-22 11:00"), false, Now);
        book.RemindedTo = T("2026-11-22 14:20").AddSeconds(5);
        store.Write(book.Snapshot());
        var text = System.IO.File.ReadAllText(store.FilePath);
        check("Reminders: the file keeps each reminder as minutes before the start, and the time reminded up to",
            text.Contains("\"remind\": 15") && text.Contains("\"remind\": -540") && text.Split("\"remind\"").Length == 3 && text.Contains("\"remindedTo\": \"2026-11-22T14:20:05\""));
        var again = store.Read(Now);
        check("Reminders: reminders and the time reminded up to read back",
            again.State == AgendaReadState.Loaded && again.Data.Events.Select(e => e.Remind).SequenceEqual(new int?[] { 15, -540, null })
            && again.Data.RemindedTo == T("2026-11-22 14:20").AddSeconds(5) && AgendaStore.Serialize(again.Data) == AgendaStore.Serialize(book.Snapshot()));

        const string odd = """
            {
              "format": 1,
              "events": [
                { "id": "a", "title": "会", "start": "2026-11-22T10:00", "end": "2026-11-22T11:00", "remind": "soon" },
                { "id": "b", "title": "会", "start": "2026-11-22T10:00", "end": "2026-11-22T11:00", "remind": -20 },
                { "id": "c", "title": "会", "start": "2026-11-22T10:00", "end": "2026-11-22T11:00", "remind": 10, "repeat": "fortnightly" }
              ]
            }
            """;
        var read = AgendaStore.Parse(odd);
        var written = AgendaStore.Serialize(read);
        check("Reminders: a reminder that cannot be read, or is after a timed start, is dropped and the event kept",
            read.Events.Count == 3 && read.Events[0].Remind is null && read.Events[1].Remind is null);
        check("Reminders: an event with a rule this version does not know keeps its reminder and the rule",
            read.Events[2].Remind == 10 && written.Contains("\"repeat\": \"fortnightly\"") && written.Contains("\"remind\": 10"));
        check("Reminders: a file without the time reminded up to reads it as none", read.RemindedTo is null && !written.Contains("remindedTo"));
    }

    private static void Due(Action<string, bool> check)
    {
        var book = new AgendaBook();
        var parcel = book.AddEvent("取快递", T("2026-11-22 16:30"), T("2026-11-22 17:00"), false, Now, remind: 15)!;
        var plain = book.AddEvent("整理", T("2026-11-22 16:00"), T("2026-11-22 17:00"), false, Now)!;
        var daily = book.AddEvent("站会", T("2026-11-23 09:30"), T("2026-11-23 10:00"), false, Now, repeat: AgendaRepeat.Daily, remind: 0)!;
        book.SkipOccurrence(daily.Id, D("2026-11-24"));
        var birthday = book.AddEvent("小周生日", T("2026-11-25 00:00"), null, true, Now, remind: 24 * 60 - 9 * 60)!;
        var trip = book.AddEvent("出差", T("2026-12-10 08:00"), T("2026-12-12 18:00"), false, Now, remind: 3 * 24 * 60)!;

        var due = AgendaReminders.Due(book.Events, T("2026-11-22 16:00"), T("2026-11-22 16:15"));
        check("Reminders: a reminder in the span is due, with its time; an event without one never is",
            due.Count == 1 && due[0].Event.Id == parcel.Id && due[0].At == T("2026-11-22 16:15") && due.All(d => d.Event.Id != plain.Id));
        check("Reminders: the span leaves out its start", AgendaReminders.Due(book.Events, T("2026-11-22 16:15"), T("2026-11-22 16:20")).Count == 0);
        var morning = AgendaReminders.Due(book.Events, T("2026-11-23 00:00"), T("2026-11-26 12:00"));
        check("Reminders: each time of a series reminds, not on a skipped day, in time order with the others",
            morning.Where(d => d.Event.Id == daily.Id).Select(d => d.At).SequenceEqual([T("2026-11-23 09:30"), T("2026-11-25 09:30"), T("2026-11-26 09:30")])
            && morning.Select(d => d.At).SequenceEqual(morning.Select(d => d.At).Order()));
        check("Reminders: an all-day event reminds at 09:00 of the day before",
            morning.Single(d => d.Event.Id == birthday.Id).At == T("2026-11-24 09:00"));
        check("Reminders: the next reminder is the earliest after the time asked",
            AgendaReminders.Next(book.Events, T("2026-11-22 16:15")) == T("2026-11-23 09:30") && AgendaReminders.Next(book.Events, Now) == T("2026-11-22 16:15"));
        check("Reminders: a reminder days before a far event is found when it comes",
            AgendaReminders.Due(book.Events, T("2026-12-07 07:00"), T("2026-12-07 09:00")).Any(d => d.Event.Id == trip.Id && d.At == T("2026-12-07 08:00")));
        check("Reminders: with no reminders there is no next one", AgendaReminders.Next(new AgendaBook().Events, Now) is null);
    }

    private static void Notice(Action<string, bool> check)
    {
        var book = new AgendaBook();
        var parcel = book.AddEvent("取快递", T("2026-11-22 16:30"), T("2026-11-22 17:00"), false, Now, "菜鸟驿站", remind: 15)!;
        var birthday = book.AddEvent("小周生日", T("2026-11-23 00:00"), null, true, Now, remind: -9 * 60)!;
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var input = AgendaReminders.Notice(parcel, T("2026-11-22 16:15"), false, zone);
        check("Reminders: a reminder is a notice of BrimDeck's own about the event",
            input.Origin == NoticeOrigin.Internal && input.AppKey == "brimdeck:agenda" && input.Title == "取快递" && input.Body == "菜鸟驿站" && input.Context == "今天 16:30–17:00"
            && input.IconRef == "glyph:calendar" && !input.Missed);
        check("Reminders: a timed event's notice carries its times and is an alert that stays, given once",
            input.Timing is { Kind: NoticeTimeKind.Event, Native: false } timing && timing.StartsAt == new DateTimeOffset(T("2026-11-22 16:30"), TimeSpan.FromHours(8))
            && NoticeClock.Alerting(timing) && NoticeClock.NextDue(timing) is null && input.Level == NoticeLevel.Persistent
            && NoticeClock.Live(timing, new DateTimeOffset(T("2026-11-22 16:15"), TimeSpan.FromHours(8)), zone) == "15 分钟后开始");
        check("Reminders: its buttons are to view the event (the main one) and to remind again later",
            input.Actions.Count == 2 && input.Actions[0] is { Key: AgendaReminders.OpenAction, Primary: true, Label: "查看日程" } && input.Actions[1].Key == AgendaReminders.SnoozeAction);
        check("Reminders: the notice names the time of the event it is about",
            AgendaReminders.Parse(input.InstanceKey) is { } key && key.Id == parcel.Id && key.Start == T("2026-11-22 16:30")
            && AgendaReminders.Parse("windows:x") is null && AgendaReminders.Parse("agenda:abc:soon") is null);
        var allDay = AgendaReminders.Notice(birthday, T("2026-11-22 09:00"), true, zone);
        check("Reminders: an all-day event's notice says the day and stays without times; one given late is missed",
            allDay.Timing is null && allDay.Context == "明天 全天" && allDay.Level == NoticeLevel.Persistent && allDay.Missed);
        check("Reminders: an event of another day is named by its date",
            AgendaText.When(book.AddEvent("周会", T("2026-11-26 15:00"), T("2026-11-26 16:00"), false, Now)!, D("2026-11-22")) == "11月26日 周四 15:00–16:00"
            && AgendaText.When(book.AddEvent("值班", T("2026-11-22 22:00"), T("2026-11-23 02:00"), false, Now)!, D("2026-11-22")) == "今天 22:00 – 11月23日 02:00");
    }
}
