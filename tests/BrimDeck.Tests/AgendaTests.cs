using System.Globalization;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;
using BrimDeck.Core.Notices;

// The planner's data (BrimDeck.Core.Agenda): an event's part on each day (from an earlier day, on to a later one, whole days
// between, ending at 00:00, over a month's and a year's end), the columns of the block view, which day's list each to-do is
// in and the numbers on the switches, the month marks and their lines, turning to-dos into events and back, agenda.json
// read and written (unknown fields kept, bad items dropped, a broken file moved aside, a locked one left alone), and the words.
static class AgendaTests
{
    private static DateTime T(string text) => DateTime.ParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static readonly DateTime Now = T("2026-11-22 14:20");
    private static readonly DateOnly Today = D("2026-11-22");

    public static void Run(string root, Action<string, bool> check)
    {
        try
        {
            Loc.Use(Loc.Chinese);
            Segments(check);
            Columns(check);
            Todos(check);
            Marks(check);
            Changes(check);
            Conversions(check);
            Files(Path.Combine(root, "agenda"), check);
            Words(check);
            English(check);
        }
        finally { Loc.Use(Loc.Chinese); }
    }

    private static AgendaEvent Event(AgendaBook book, string title, string start, string end, bool allDay = false, int createdMinute = 0)
        => book.AddEvent(title, allDay ? T(start + " 00:00") : T(start), allDay ? T(end + " 00:00") : T(end), allDay, Now.AddMinutes(createdMinute))
            ?? throw new InvalidOperationException("event refused: " + title);

    private static void Segments(Action<string, bool> check)
    {
        var book = new AgendaBook();
        var trip = Event(book, "出差上海", "2026-11-23 20:00", "2026-11-25 12:00");
        var duty = Event(book, "发布值守", "2026-11-23 22:00", "2026-11-24 02:00");
        var birthday = Event(book, "小周生日", "2026-11-24", "2026-11-24", allDay: true);
        var standup = Event(book, "站会", "2026-11-24 09:00", "2026-11-24 09:30");
        var late = Event(book, "晚间", "2026-11-22 20:00", "2026-11-23 00:00");

        var first = book.Segments(D("2026-11-23")).Single(s => s.Event == trip);
        check("Agenda: a segment on the first day runs from the start to 24:00 and goes on to later days",
            first.Start == T("2026-11-23 20:00") && first.End == T("2026-11-24 00:00") && !first.AllDay && !first.FromEarlier && first.ContinuesLater
            && first.DayIndex == 1 && first.DayCount == 3);
        var middle = book.Segments(D("2026-11-24")).Single(s => s.Event == trip);
        check("Agenda: a day covered whole is an all-day segment, day 2 of 3, from earlier and going on",
            middle.AllDay && middle.FromEarlier && middle.ContinuesLater && middle.DayIndex == 2 && middle.DayCount == 3
            && middle.Start == T("2026-11-24 00:00") && middle.End == T("2026-11-25 00:00"));
        var last = book.Segments(D("2026-11-25")).Single(s => s.Event == trip);
        check("Agenda: a segment on the last day runs from 00:00 to the end",
            last.Start == T("2026-11-25 00:00") && last.End == T("2026-11-25 12:00") && !last.AllDay && last.FromEarlier && !last.ContinuesLater && last.DayIndex == 3);
        check("Agenda: an event shows on no day outside its own", book.Segments(D("2026-11-26")).Count == 0
            && book.Segments(D("2026-11-22")).All(s => s.Event != trip));
        var yesterday = book.Segments(D("2026-11-24")).Single(s => s.Event == duty);
        check("Agenda: an event from the evening before shows from 00:00, day 2 of 2",
            yesterday.Start == T("2026-11-24 00:00") && yesterday.End == T("2026-11-24 02:00") && yesterday.FromEarlier && yesterday.DayCount == 2 && !yesterday.AllDay);
        check("Agenda: an event ending at exactly 00:00 does not show on the day it ends on",
            book.Segments(D("2026-11-23")).All(s => s.Event != late) && late.DayCount == 1);
        var lateSegment = book.Segments(D("2026-11-22")).Single(s => s.Event == late);
        check("Agenda: such an event ends its day at 24:00", lateSegment.End == T("2026-11-23 00:00") && !lateSegment.ContinuesLater
            && AgendaText.Clock(lateSegment.End, lateSegment.Day) == "24:00" && AgendaText.Clock(lateSegment.Start, lateSegment.Day) == "20:00");
        var order = book.Segments(D("2026-11-24")).Select(s => s.Event.Title).ToList();
        check("Agenda: all-day segments come first (by the event's start), then by the segment's start",
            order.SequenceEqual(["出差上海", "小周生日", "发布值守", "站会"]));

        var tie = new AgendaBook();
        var b = Event(tie, "B", "2026-11-22 10:00", "2026-11-22 12:00", createdMinute: 1);
        var a = Event(tie, "A", "2026-11-22 10:00", "2026-11-22 11:00", createdMinute: 2);
        var c = Event(tie, "C", "2026-11-22 10:00", "2026-11-22 11:00", createdMinute: 0);
        check("Agenda: segments starting together go by their end, then by when they were made",
            tie.Segments(Today).Select(s => s.Event.Title).SequenceEqual(["C", "A", "B"]));

        var month = new AgendaBook();
        var overnight = Event(month, "跨月", "2026-11-30 22:00", "2026-12-01 01:00");
        var december = month.Segments(D("2026-12-01")).Single();
        check("Agenda: an event over a month's end shows on the first of the next month",
            december.Start == T("2026-12-01 00:00") && december.End == T("2026-12-01 01:00") && december.DayIndex == 2 && overnight.DayCount == 2);
        var year = Event(month, "跨年", "2026-12-31 20:00", "2027-01-02 10:00");
        var newYear = month.Segments(D("2027-01-01")).Single(s => s.Event == year);
        check("Agenda: an event over a year's end covers New Year's Day whole, day 2 of 3",
            newYear.AllDay && newYear.DayIndex == 2 && newYear.DayCount == 3 && month.Segments(D("2027-01-02")).Single().End == T("2027-01-02 10:00"));

        var allDays = new AgendaBook();
        var holiday = Event(allDays, "假期", "2026-11-24", "2026-11-26", allDay: true);
        var holidayMiddle = allDays.Segments(D("2026-11-25")).Single();
        check("Agenda: an all-day event over three days is all day on each, counting its days",
            holiday.DayCount == 3 && holidayMiddle.AllDay && holidayMiddle.DayIndex == 2 && holidayMiddle.FromEarlier && holidayMiddle.ContinuesLater
            && allDays.Segments(D("2026-11-27")).Count == 0 && allDays.Segments(D("2026-11-26")).Single().AllDay);
        var whole = Event(allDays, "整天", "2026-11-22 00:00", "2026-11-23 00:00");
        check("Agenda: a timed event filling one day exactly shows as all day on it", allDays.Segments(Today).Single(s => s.Event == whole).AllDay && whole.DayCount == 1);
    }

    private static void Columns(Action<string, bool> check)
    {
        var book = new AgendaBook();
        Event(book, "第四季度产品评审会", "2026-11-24 09:30", "2026-11-24 11:30");
        Event(book, "面试", "2026-11-24 11:00", "2026-11-24 12:00");
        Event(book, "站会", "2026-11-24 09:00", "2026-11-24 09:30");
        Event(book, "设计走查", "2026-11-24 10:30", "2026-11-24 12:30");
        Event(book, "供应商电话", "2026-11-24 10:00", "2026-11-24 11:00");
        Event(book, "和财务核对 10 月报销", "2026-11-24 13:30", "2026-11-24 14:00");
        Event(book, "小周生日", "2026-11-24", "2026-11-24", allDay: true);
        var segments = book.Segments(D("2026-11-24")).Reverse().ToList();
        var placed = AgendaQuery.Columns(segments, TimeSpan.FromMinutes(30));
        (int, int) At(string title) { var p = placed.Single(p => p.Segment.Event.Title == title); return (p.Column, p.Columns); }
        check("Agenda: the block view leaves all-day segments out and keeps the order given",
            placed.Count == 6 && placed.Select(p => p.Segment).SequenceEqual(segments.Where(s => !s.AllDay)));
        check("Agenda: the design's four overlapping events take three columns, 11:00 back in the second",
            At("第四季度产品评审会") == (0, 3) && At("供应商电话") == (1, 3) && At("设计走查") == (2, 3) && At("面试") == (1, 3));
        check("Agenda: events touching the group end to end, or overlapping nothing, have the whole width",
            At("站会") == (0, 1) && At("和财务核对 10 月报销") == (0, 1));

        var touching = new AgendaBook();
        Event(touching, "一", "2026-11-22 09:00", "2026-11-22 10:00");
        Event(touching, "二", "2026-11-22 10:00", "2026-11-22 11:00");
        check("Agenda: one event ending as the next begins does not overlap it",
            AgendaQuery.Columns(touching.Segments(Today), TimeSpan.FromMinutes(30)).All(p => p.Column == 0 && p.Columns == 1));

        var shortOne = new AgendaBook();
        Event(shortOne, "短", "2026-11-22 09:00", "2026-11-22 09:10");
        Event(shortOne, "后", "2026-11-22 09:20", "2026-11-22 10:00");
        var withMinimum = AgendaQuery.Columns(shortOne.Segments(Today), TimeSpan.FromMinutes(30));
        var without = AgendaQuery.Columns(shortOne.Segments(Today), TimeSpan.Zero);
        check("Agenda: a short event counts at least the shortest length shown, so the next one sits beside it",
            withMinimum.Select(p => (p.Column, p.Columns)).SequenceEqual([(0, 2), (1, 2)]) && without.All(p => p.Column == 0 && p.Columns == 1));

        var chain = new AgendaBook();
        Event(chain, "A", "2026-11-22 09:00", "2026-11-22 10:00");
        Event(chain, "B", "2026-11-22 09:30", "2026-11-22 10:30");
        Event(chain, "C", "2026-11-22 10:00", "2026-11-22 11:00");
        check("Agenda: a chain of overlaps is one group, as wide as its most at one moment, reusing a freed column",
            AgendaQuery.Columns(chain.Segments(Today), TimeSpan.FromMinutes(30)).Select(p => (p.Column, p.Columns)).SequenceEqual([(0, 2), (1, 2), (0, 2)]));
        var overnight = new AgendaBook();
        Event(overnight, "夜", "2026-11-21 22:00", "2026-11-22 02:00");
        Event(overnight, "早", "2026-11-22 01:00", "2026-11-22 03:00");
        check("Agenda: the part of an event from the day before takes its column from 00:00",
            AgendaQuery.Columns(overnight.Segments(Today), TimeSpan.FromMinutes(30)).Select(p => (p.Column, p.Columns)).SequenceEqual([(0, 2), (1, 2)]));
    }

    private static void Todos(Action<string, bool> check)
    {
        var book = new AgendaBook();
        int minute = 0;
        AgendaTodo Add(string text, string? day, string? due = null) => book.AddTodo(text, day is null ? null : D(day), Now.AddMinutes(minute++), due is null ? null : D(due))!;
        var mine = Add("今天的", "2026-11-22");
        var leftYesterday = Add("昨天没做完", "2026-11-21");
        var leftEarlier = Add("前天没做完", "2026-11-20");
        var doneYesterday = Add("昨天做完", "2026-11-21");
        book.SetDone(doneYesterday.Id, true, T("2026-11-21 18:00"));
        var dueLater = Add("长期但有截止", null, "2026-11-30");
        var oldDue = Add("早先的有截止", "2026-11-15", "2026-11-25");
        var someday = Add("换显示器支架", null);
        var somedayDone = Add("今天做完的长期", null);
        book.SetDone(somedayDone.Id, true, T("2026-11-22 09:00"));
        var somedayOld = Add("前天做完的长期", null);
        book.SetDone(somedayOld.Id, true, T("2026-11-20 09:00"));
        var dueTicked = Add("今天勾掉的截止", null, "2026-11-28");
        book.SetDone(dueTicked.Id, true, T("2026-11-22 10:00"));
        var datedDueTicked = Add("有日期也有截止，今天勾掉", "2026-11-18", "2026-11-26");
        book.SetDone(datedDueTicked.Id, true, T("2026-11-22 11:00"));

        var today = book.TodosOf(Today, Today);
        check("Agenda: today's list has its own, every unfinished one with a due day, and those with a due day ticked today, in the order made",
            today.Main.SequenceEqual([mine, book.FindTodo(dueLater.Id)!, oldDue, book.FindTodo(dueTicked.Id)!, book.FindTodo(datedDueTicked.Id)!]));
        check("Agenda: today's switch counts its unfinished to-dos", today.Open == 3 && book.Counts(Today, Today).OpenTodos == 3);
        check("Agenda: the long-term group has the open ones and those ticked that day",
            today.LongTerm.SequenceEqual([someday, book.FindTodo(somedayDone.Id)!]));
        check("Agenda: a long-term to-do ticked on another day shows on that day only",
            book.TodosOf(D("2026-11-20"), Today).LongTerm.Contains(book.FindTodo(somedayOld.Id)!) && !book.TodosOf(D("2026-11-21"), Today).LongTerm.Any(t => t.Id == somedayOld.Id));
        check("Agenda: today's prompt counts unfinished earlier to-dos without a due day",
            today.CarryOver == new AgendaCarryOver(2, false) && AgendaText.CarryOver(today.CarryOver!) == "之前有 2 项未完成");
        check("Agenda: another day has no prompt", book.TodosOf(D("2026-11-23"), Today).CarryOver is null);
        var due25 = book.TodosOf(D("2026-11-25"), Today);
        check("Agenda: a day that is an unfinished to-do's due day shows it; a later day does not show other due to-dos",
            due25.Main.SequenceEqual([oldDue]) && due25.Open == 1);
        check("Agenda: its own day still shows a to-do with a due day", book.TodosOf(D("2026-11-15"), Today).Main.SequenceEqual([oldDue]));
        var past = book.TodosOf(D("2026-11-21"), Today);
        check("Agenda: a ticked to-do stays in place in its day's list", past.Main.SequenceEqual([leftYesterday, book.FindTodo(doneYesterday.Id)!]) && past.Open == 1);
        check("Agenda: a due day does not show a ticked to-do", !book.TodosOf(D("2026-11-28"), Today).Main.Any());
        check("Agenda: an overdue to-do is told apart", AgendaQuery.Overdue(oldDue, D("2026-11-26")) && !AgendaQuery.Overdue(oldDue, Today));

        book.RemoveTodo(leftEarlier.Id);
        var onlyYesterday = book.TodosOf(Today, Today).CarryOver;
        check("Agenda: the prompt says yesterday when every one left is yesterday's",
            onlyYesterday == new AgendaCarryOver(1, true) && AgendaText.CarryOver(onlyYesterday!) == "昨天有 1 项未完成");
        book.Restore(leftEarlier);
        int changes = 0;
        book.Changed += () => changes++;
        check("Agenda: moving to today moves the ones counted", book.MoveToToday(Today) == 2 && changes == 1
            && book.FindTodo(leftYesterday.Id)!.Day == Today && book.FindTodo(leftEarlier.Id)!.Day == Today && book.TodosOf(Today, Today).CarryOver is null);
        check("Agenda: moving again moves nothing and changes nothing", book.MoveToToday(Today) == 0 && changes == 1);
        check("Agenda: today's count follows the move", book.Counts(Today, Today).OpenTodos == 5);
    }

    private static void Marks(Action<string, bool> check)
    {
        var book = new AgendaBook();
        Event(book, "出差上海", "2026-11-23 20:00", "2026-11-25 12:00");
        Event(book, "发布值守", "2026-11-23 22:00", "2026-11-24 02:00");
        Event(book, "周会", "2026-11-26 15:00", "2026-11-26 16:00");
        Event(book, "露营", "2026-11-28", "2026-11-29", allDay: true);
        Event(book, "跨月", "2026-11-30 20:00", "2026-12-01 08:00");
        Event(book, "上旬", "2026-11-03 10:00", "2026-11-03 11:00");
        book.AddTodo("五号的", D("2026-11-05"), Now);
        book.AddTodo("十二号截止", null, Now, D("2026-11-12"));
        var done = book.AddTodo("十三号做完", D("2026-11-13"), Now)!;
        book.SetDone(done.Id, true, Now);
        book.SetNote(D("2026-11-12"), "便签");
        var marks = book.Marks(D("2026-10-26"), 42);
        AgendaDayMark M(string day) => marks.Single(m => m.Day == D(day));
        check("Agenda: the month view gets one mark per day asked for", marks.Count == 42 && marks[0].Day == D("2026-10-26") && marks[^1].Day == D("2026-12-06"));
        check("Agenda: days with events are marked; a day of a longer event only has the line, not its own event",
            M("2026-11-03").Events && M("2026-11-03").DayEvents && M("2026-11-23").Events && !M("2026-11-23").DayEvents && M("2026-11-26").DayEvents && !M("2026-11-27").Events);
        check("Agenda: unfinished to-dos mark their day and their due day, ticked ones nothing",
            M("2026-11-05").OpenTodos && M("2026-11-12").OpenTodos && !M("2026-11-13").OpenTodos && !M("2026-11-22").OpenTodos);
        check("Agenda: a note marks its day", M("2026-11-12").Note && !M("2026-11-05").Note);
        check("Agenda: overlapping events over several days share one line",
            M("2026-11-23") is { InSpan: true, SpanStart: true, SpanEnd: false } && M("2026-11-24") is { InSpan: true, SpanStart: false, SpanEnd: false }
            && M("2026-11-25") is { InSpan: true, SpanStart: false, SpanEnd: true } && !M("2026-11-26").InSpan && !M("2026-11-22").InSpan);
        check("Agenda: lines that only meet end to start stay two",
            M("2026-11-28").SpanStart && M("2026-11-29").SpanEnd && M("2026-11-30").SpanStart && M("2026-12-01").SpanEnd && !M("2026-11-29").SpanStart);
        var week = book.Marks(D("2026-11-24"), 7);
        check("Agenda: a line begun before the range has no start inside it", week.Count == 7 && week[0] is { InSpan: true, SpanStart: false });
        var counts = book.Counts(D("2026-11-24"), Today);
        check("Agenda: the events switch counts the day's segments", counts.Events == 2 && counts.Events == book.Segments(D("2026-11-24")).Count && !counts.Note
            && book.Counts(D("2026-11-12"), Today) == new AgendaCounts(0, 1, true));
    }

    private static void Changes(Action<string, bool> check)
    {
        var book = new AgendaBook();
        int changes = 0;
        book.Changed += () => changes++;
        check("Agenda: an event without a title is refused", book.AddEvent("  ", T("2026-11-22 10:00"), null, false, Now) is null && changes == 0);
        check("Agenda: an event ending as it starts is refused", book.AddEvent("会", T("2026-11-22 10:00"), T("2026-11-22 10:00"), false, Now) is null);
        check("Agenda: an all-day event ending before its first day is refused", book.AddEvent("会", T("2026-11-22 00:00"), T("2026-11-21 00:00"), true, Now) is null);
        var hour = book.AddEvent(" 周会 ", T("2026-11-22 15:00").AddSeconds(42), null, false, Now, detail: "  ")!;
        check("Agenda: an event without an end lasts an hour, cut to the minute, trimmed",
            hour.Start == T("2026-11-22 15:00") && hour.End == T("2026-11-22 16:00") && hour.Title == "周会" && hour.Detail is null && changes == 1 && book.Revision == 1);
        var allDay = book.AddEvent("假期", T("2026-11-24 13:00"), T("2026-11-25 09:00"), true, Now)!;
        check("Agenda: an all-day event keeps only its dates", allDay.Start == T("2026-11-24 00:00") && allDay.End == T("2026-11-25 00:00") && allDay.DayCount == 2);

        var notice = new Notice { Id = "n1", AppKey = "windows:Microsoft.Outlook", AppName = "Outlook", Title = " 王敏 ", IconRef = "aumid:outlook" };
        var source = AgendaSource.From(notice);
        check("Agenda: a source keeps the notice's id, application, sender and icon",
            source is { NoticeId: "n1", AppKey: "windows:Microsoft.Outlook", AppName: "Outlook", Sender: "王敏", Icon: "aumid:outlook" } && AgendaText.Source(source) == "Outlook · 王敏");
        var meeting = book.AddEvent("评审", T("2026-11-26 15:00"), T("2026-11-26 16:00"), false, Now, "5 楼", source)!;
        var moved = book.UpdateEvent(meeting.Id, "评审会", T("2026-11-26 16:00"), T("2026-11-26 17:30"), false, "6 楼");
        check("Agenda: changing an event keeps its id, source and creation", moved is { Title: "评审会", Detail: "6 楼" } && moved.Source == source
            && moved.Created == meeting.Created && moved.Start == T("2026-11-26 16:00") && book.FindEvent(meeting.Id) == moved);
        int before = changes;
        check("Agenda: a refused change leaves the event as it was",
            book.UpdateEvent(meeting.Id, "评审会", T("2026-11-26 18:00"), T("2026-11-26 17:00"), false, null) is null && book.FindEvent(meeting.Id) == moved && changes == before);
        check("Agenda: a change to nothing new raises nothing", book.UpdateEvent(meeting.Id, "评审会", T("2026-11-26 16:00"), T("2026-11-26 17:30"), false, "6 楼") == moved && changes == before);
        check("Agenda: changing an event that is not there gives null", book.UpdateEvent("none", "x", Now, null, false, null) is null);

        var todo = book.AddTodo("回邮件", Today, Now, source: source)!;
        var ticked = book.SetDone(todo.Id, true, T("2026-11-22 15:01"))!;
        check("Agenda: ticking keeps when it was done", ticked.Done && ticked.DoneAt == T("2026-11-22 15:01") && ticked.DoneDay == Today);
        var unticked = book.SetDone(todo.Id, false, T("2026-11-22 15:02"))!;
        check("Agenda: unticking clears it", !unticked.Done && unticked.DoneAt is null && unticked.DoneDay is null);
        check("Agenda: a to-do's text changes, a blank text is refused",
            book.SetTodoText(todo.Id, " 回复邮件 ")!.Text == "回复邮件" && book.SetTodoText(todo.Id, " ") is null && book.FindTodo(todo.Id)!.Text == "回复邮件");
        check("Agenda: a due day is set and cleared", book.SetDue(todo.Id, D("2026-11-30"))!.Due == D("2026-11-30") && book.SetDue(todo.Id, null)!.Due is null);
        check("Agenda: a to-do moves to long-term and back", book.SetTodoDay(todo.Id, null)!.Day is null && book.SetTodoDay(todo.Id, Today)!.Day == Today);
        check("Agenda: a blank to-do is refused", book.AddTodo(" \n ", Today, Now) is null);

        before = changes;
        check("Agenda: a note is set, the same text again changes nothing",
            book.SetNote(Today, "每天就记一段话") && !book.SetNote(Today, "每天就记一段话") && book.Note(Today) == "每天就记一段话" && changes == before + 1);
        check("Agenda: a blank note removes it", book.SetNote(Today, "  \n") && book.Note(Today) is null && !book.Notes.ContainsKey(Today) && !book.SetNote(Today, ""));

        var found = book.FromNotice("n1");
        check("Agenda: the items a notice added are found by its id", found.Any && found.Events.Single().Id == meeting.Id && found.Todos.Single().Id == todo.Id
            && !book.FromNotice("n2").Any);
        var removed = book.RemoveEvent(meeting.Id)!;
        check("Agenda: a removed event can be put back with its id", book.FindEvent(meeting.Id) is null && book.Restore(removed) && book.FindEvent(meeting.Id) == removed && !book.Restore(removed));
        check("Agenda: undoing a notice removes everything it added", book.RemoveFromNotice("n1") == 2 && !book.FromNotice("n1").Any && book.RemoveFromNotice("n1") == 0);
        check("Agenda: removing what is not there gives null", book.RemoveEvent("none") is null && book.RemoveTodo("none") is null);

        // What was changed before the file could be read is kept; the rest of the file joins it.
        var early = new AgendaBook();
        var kept = early.AddTodo("先加的", Today, Now)!;
        early.SetNote(Today, "这里写的");
        var file = new AgendaData();
        file.Todos.Add(new AgendaTodo { Id = kept.Id, Text = "文件里的旧版本", Created = Now });
        file.Todos.Add(new AgendaTodo { Id = "other", Text = "文件里的", Created = Now });
        file.Events.Add(new AgendaEvent { Id = "e", Title = "文件里的日程", Start = T("2026-11-22 09:00"), End = T("2026-11-22 10:00"), Created = Now });
        file.Notes[Today] = "文件里的便签";
        file.Notes[D("2026-11-21")] = "昨天的便签";
        early.MergeLoaded(file);
        check("Agenda: merging the file keeps what was made here and adds the rest",
            early.FindTodo(kept.Id)!.Text == "先加的" && early.FindTodo("other") is not null && early.FindEvent("e") is not null
            && early.Note(Today) == "这里写的" && early.Note(D("2026-11-21")) == "昨天的便签" && early.Todos.Count == 2);
    }

    private static void Conversions(Action<string, bool> check)
    {
        var book = new AgendaBook();
        var source = new AgendaSource { NoticeId = "n9", AppName = "QQ", Sender = "产品群" };
        var todo = book.AddTodo("给小周回更新说明", Today, Now, source: source)!;
        check("Agenda: a to-do given times that are refused stays a to-do",
            book.TodoToEvent(todo.Id, T("2026-11-22 15:00"), T("2026-11-22 14:00"), false) is null && book.FindTodo(todo.Id) is not null && book.Events.Count == 0);
        var item = book.TodoToEvent(todo.Id, T("2026-11-22 15:00"), null, false)!;
        check("Agenda: a to-do given a time becomes an event and is gone from the to-dos",
            book.FindTodo(todo.Id) is null && book.Todos.Count == 0 && item.Id == todo.Id && item.Title == todo.Text && item.Source == source
            && item.Start == T("2026-11-22 15:00") && item.End == T("2026-11-22 16:00") && item.Created == todo.Created);
        check("Agenda: the notice still finds it as an event", book.FromNotice("n9").Events.Single() == item && book.FromNotice("n9").Todos.Count == 0);
        var back = book.EventToTodo(item.Id, Today)!;
        check("Agenda: an event goes back to a to-do of a day, with its title, id and source",
            book.Events.Count == 0 && back.Id == item.Id && back.Text == item.Title && back.Day == Today && back.Source == source && !back.Done);
        var allDay = book.TodoToEvent(back.Id, T("2026-11-24 00:00"), T("2026-11-25 00:00"), true)!;
        check("Agenda: a to-do can become an all-day event over days", allDay.AllDay && allDay.DayCount == 2);
        check("Agenda: an event can go back to long-term", book.EventToTodo(allDay.Id, null)!.Day is null);
        check("Agenda: turning what is not there gives null", book.TodoToEvent("none", Now, null, false) is null && book.EventToTodo("none", Today) is null);
    }

    private static void Files(string folder, Action<string, bool> check)
    {
        Directory.CreateDirectory(folder);
        var store = new AgendaStore(folder);
        check("Agenda: no file reads as empty and may be written", store.Read(Now) is { State: AgendaReadState.Empty, Writable: true } read && read.Data.Events.Count == 0);

        var book = new AgendaBook();
        var source = new AgendaSource { NoticeId = "n1", AppKey = "windows:Outlook", AppName = "Outlook", Sender = "王敏", Icon = "aumid:x" };
        book.AddEvent("出差上海", T("2026-11-23 20:00"), T("2026-11-25 12:00"), false, Now, "虹桥", source);
        book.AddEvent("小周生日", T("2026-11-24 00:00"), null, true, Now);
        var todo = book.AddTodo("整理宣传视频素材", null, Now, D("2026-11-30"))!;
        book.SetDone(book.AddTodo("订会议室", Today, Now)!.Id, true, T("2026-11-22 09:15").AddSeconds(7));
        book.SetNote(Today, "第一行\n第二行 \"引号\"");
        store.Write(book.Snapshot());
        var text = File.ReadAllText(store.FilePath);
        check("Agenda: the file is readable JSON with wall times and plain Chinese",
            text.Contains("\"format\": 1") && text.Contains("\"start\": \"2026-11-23T20:00\"") && text.Contains("\"start\": \"2026-11-24\"")
            && text.Contains("\"end\": \"2026-11-24\"") && text.Contains("出差上海") && text.Contains("\"doneAt\": \"2026-11-22T09:15:07\"") && text.Contains("\"2026-11-22\": ")
            && !File.Exists(store.FilePath + ".tmp"));
        var again = store.Read(Now);
        check("Agenda: what is written reads back the same", again.State == AgendaReadState.Loaded
            && AgendaStore.Serialize(again.Data) == AgendaStore.Serialize(book.Snapshot())
            && again.Data.Events[0] == book.Events[0] && again.Data.Todos.Single(t => t.Id == todo.Id) == todo && again.Data.Notes[Today] == book.Note(Today));

        const string future = """
            {
              "format": 7,
              "reminders": [ { "at": "2026-11-22T09:00" } ],
              "events": [
                { "id": "e1", "title": "会", "start": "2026-11-22T10:00", "end": "2026-11-22T11:00", "color": "#FF0000",
                  "source": { "notice": "n1", "appName": "Outlook", "avatar": "x.png" } },
                { "id": "e2", "start": "2026-11-22T10:00", "end": "2026-11-22T11:00" },
                { "id": "e3", "title": "坏开始", "start": "明天", "end": "2026-11-22T11:00" },
                { "id": "e4", "title": "倒着", "start": "2026-11-22T12:00", "end": "2026-11-22T11:00" },
                { "id": "e5", "title": "没结束", "start": "2026-11-22T12:00" },
                { "id": "e6", "title": "时区", "start": "2026-11-22T12:00+08:00", "end": "2026-11-22T13:00" },
                { "id": "e1", "title": "重复", "start": "2026-11-22T10:00", "end": "2026-11-22T11:00" },
                { "title": "没有 id", "start": "2026-11-23", "end": "2026-11-23", "allDay": true },
                "不是对象",
              ],
              "todos": [
                { "id": "t1", "text": "好的", "day": "2026-11-22", "priority": 2 },
                { "id": "t2", "text": "坏日期", "day": "11/22" },
                { "id": "t3", "text": "  " },
                { "id": "t4", "text": "长期", "day": null, "done": true }
              ],
              "notes": { "2026-11-22": "便签", "bad": "x", "2026-11-21": "   ", "2026-11-20": 5 }
            }
            """;
        var parsed = AgendaStore.Parse(future);
        check("Agenda: a later format is read for what it holds; each bad item is dropped alone",
            parsed.Events.Select(e => e.Id).SequenceEqual(["e1", parsed.Events[^1].Id]) && parsed.Events.Count == 2 && parsed.Events[^1] is { Title: "没有 id", AllDay: true }
            && parsed.Todos.Select(t => t.Id).SequenceEqual(["t1", "t4"]) && parsed.Notes.Count == 1 && parsed.Notes[Today] == "便签");
        check("Agenda: a ticked to-do without a time takes its creation", parsed.Todos[1] is { Done: true, DoneAt: not null });
        var written = AgendaStore.Serialize(parsed);
        check("Agenda: fields this version does not know are written back",
            written.Contains("\"reminders\"") && written.Contains("\"color\": \"#FF0000\"") && written.Contains("\"avatar\": \"x.png\"") && written.Contains("\"priority\": 2")
            && written.Contains("\"format\": 1"));
        var reparsed = new AgendaBook(AgendaStore.Parse(written));
        reparsed.UpdateEvent("e1", "会议", T("2026-11-22 10:00"), T("2026-11-22 11:00"), false, null);
        check("Agenda: a changed item keeps its unknown fields", AgendaStore.Serialize(reparsed.Snapshot()).Contains("\"color\": \"#FF0000\""));

        File.WriteAllText(store.FilePath, "{ \"events\": [ { \"id\": ");
        var broken = store.Read(T("2026-11-22 14:20"));
        check("Agenda: a file that cannot be understood is moved aside and the planner starts empty",
            broken is { State: AgendaReadState.Recovered, Writable: true } && broken.KeptAs == "agenda.broken-20261122-142000.json"
            && !File.Exists(store.FilePath) && File.ReadAllText(Path.Combine(folder, broken.KeptAs)) == "{ \"events\": [ { \"id\": " && broken.Data.Events.Count == 0);
        File.WriteAllText(store.FilePath, "[1, 2]");
        var second = store.Read(T("2026-11-22 14:20"));
        check("Agenda: a second broken file in the same second gets its own name", second.State == AgendaReadState.Recovered && second.KeptAs == "agenda.broken-20261122-142000-2.json");
        File.WriteAllText(store.FilePath, "{\"events\":[], \"events\":[]}");
        check("Agenda: a file with a key twice is moved aside too", store.Read(T("2026-11-22 14:21")).State == AgendaReadState.Recovered);

        store.Write(book.Snapshot());
        AgendaRead locked;
        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) locked = store.Read(Now);
        check("Agenda: a file in use is not read, not moved and may not be written",
            locked is { State: AgendaReadState.Unavailable, Writable: false } && File.Exists(store.FilePath) && store.Read(Now).State == AgendaReadState.Loaded);
    }

    private static void Words(Action<string, bool> check)
    {
        check("Agenda: the day's heading", AgendaText.DayTitle(Today, Today) == "11月22日 周日" && AgendaText.DayTitle(D("2027-01-03"), Today) == "2027年1月3日 周日");
        check("Agenda: the days around today are named",
            AgendaText.Relative(Today, Today) == "今天" && AgendaText.Relative(D("2026-11-23"), Today) == "明天" && AgendaText.Relative(D("2026-11-24"), Today) == "后天"
            && AgendaText.Relative(D("2026-11-21"), Today) == "昨天" && AgendaText.Relative(D("2026-11-20"), Today) == "前天"
            && AgendaText.Relative(D("2026-11-26"), Today) == "4 天后" && AgendaText.Relative(D("2026-11-19"), Today) == "3 天前"
            && AgendaText.Relative(D("2026-12-23"), Today) is null);
        check("Agenda: the month title and weekdays", AgendaText.Month(2026, 11) == "2026年11月" && AgendaText.Weekday(DayOfWeek.Monday) == "周一");

        var book = new AgendaBook();
        var ended = Event(book, "整理本周计划", "2026-11-22 10:00", "2026-11-22 11:00");
        var now = Event(book, "和小林对设计稿", "2026-11-22 14:00", "2026-11-22 15:00");
        var soon = Event(book, "取快递", "2026-11-22 16:30", "2026-11-22 17:00");
        var evening = Event(book, "写周报", "2026-11-22 20:00", "2026-11-22 21:30");
        var deadline = Event(book, "Alpha 测试截止", "2026-11-22", "2026-11-22", allDay: true);
        var tomorrow = Event(book, "周会", "2026-11-23 15:00", "2026-11-23 16:00");
        string? S(AgendaEvent item, string day, DateTime at) => AgendaText.Status(AgendaQuery.Segment(item, D(day))!, at);
        check("Agenda: today's events say whether they ended, are under way or how long until they start",
            S(ended, "2026-11-22", Now) == "已结束" && S(now, "2026-11-22", Now) == "进行中 · 还剩 40 分钟" && S(soon, "2026-11-22", Now) == "2 小时后"
            && S(evening, "2026-11-22", Now) == "5 小时后");
        check("Agenda: an all-day event of one day and another day's event have no status",
            S(deadline, "2026-11-22", Now) is null && S(tomorrow, "2026-11-23", Now) is null);
        check("Agenda: phases grey ended segments", AgendaQuery.Phase(AgendaQuery.Segment(ended, Today)!, Now) == AgendaPhase.Past
            && AgendaQuery.Phase(AgendaQuery.Segment(now, Today)!, Now) == AgendaPhase.Ongoing && AgendaQuery.Phase(soon, Now) == AgendaPhase.Upcoming
            && AgendaQuery.Phase(deadline, Now) == AgendaPhase.Ongoing);
        check("Agenda: times left round up to the minute", AgendaText.Length(TimeSpan.FromSeconds(30)) == "1 分钟" && AgendaText.Length(TimeSpan.FromMinutes(59.5)) == "1 小时" && AgendaText.Length(TimeSpan.FromMinutes(58.5)) == "59 分钟"
            && AgendaText.Length(TimeSpan.FromMinutes(61)) == "1 小时" && AgendaText.Length(TimeSpan.FromMinutes(179)) == "2 小时");

        var trip = Event(book, "出差上海", "2026-11-23 20:00", "2026-11-25 12:00");
        var duty = Event(book, "发布值守", "2026-11-23 22:00", "2026-11-24 02:00");
        check("Agenda: a day inside a longer event names its day and the whole range",
            S(trip, "2026-11-24", Now) == "第 2 天，共 3 天 · 11月23日 20:00 – 11月25日 12:00" && S(trip, "2026-11-23", Now) == "第 1 天，共 3 天 · 11月23日 20:00 – 11月25日 12:00");
        check("Agenda: an event over two days says where it comes from or goes to",
            S(duty, "2026-11-24", Now) == "从前一天 22:00 开始" && S(duty, "2026-11-23", Now) == "到次日 02:00");
        check("Agenda: on today it says yesterday and tomorrow, after the words of the time",
            S(duty, "2026-11-24", T("2026-11-24 01:00")) == "进行中 · 还剩 1 小时 · 从昨天 22:00 开始"
            && S(duty, "2026-11-23", T("2026-11-23 14:00")) == "8 小时后 · 到明天 02:00"
            && S(trip, "2026-11-23", T("2026-11-23 21:00")) == "进行中 · 第 1 天，共 3 天 · 11月23日 20:00 – 11月25日 12:00");
        var newYear = Event(book, "跨年", "2026-12-31 20:00", "2027-01-02 10:00");
        var camping = Event(book, "露营", "2026-11-28", "2026-11-29", allDay: true);
        check("Agenda: a range over a year's end writes both years; an all-day range only dates",
            S(newYear, "2027-01-01", Now) == "第 2 天，共 3 天 · 2026年12月31日 20:00 – 2027年1月2日 10:00" && S(camping, "2026-11-29", Now) == "第 2 天，共 2 天 · 11月28日 – 11月29日");

        check("Agenda: due days",
            AgendaText.Due(D("2026-11-30"), Today) == "11月30日截止 · 还有 8 天" && AgendaText.Due(Today, Today) == "今天截止"
            && AgendaText.Due(D("2026-11-23"), Today) == "明天截止" && AgendaText.Due(D("2026-11-20"), Today) == "11月20日截止 · 已过期 2 天"
            && AgendaText.Due(D("2026-11-30"), Today, done: true) == "11月30日截止" && AgendaText.Due(D("2027-01-03"), Today) == "2027年1月3日截止 · 还有 42 天");
        check("Agenda: a source without a sender shows the application alone, one without a name says notice",
            AgendaText.Source(new AgendaSource { NoticeId = "x", AppName = "QQ" }) == "QQ" && AgendaText.Source(new AgendaSource { NoticeId = "x", Sender = "王敏" }) == "通知 · 王敏");
    }

    private static void English(Action<string, bool> check)
    {
        Loc.Use(Loc.English);
        var book = new AgendaBook();
        var now = Event(book, "Design review", "2026-11-22 14:00", "2026-11-22 15:00");
        var soon = Event(book, "Parcel", "2026-11-22 16:30", "2026-11-22 17:00");
        var ended = Event(book, "Plan", "2026-11-22 10:00", "2026-11-22 11:00");
        var trip = Event(book, "Trip", "2026-11-23 20:00", "2026-11-25 12:00");
        var duty = Event(book, "On call", "2026-11-23 22:00", "2026-11-24 02:00");
        string? S(AgendaEvent item, string day, DateTime at) => AgendaText.Status(AgendaQuery.Segment(item, D(day))!, at);
        check("Agenda (English): headings and day names",
            AgendaText.DayTitle(Today, Today) == "Sun, Nov 22" && AgendaText.DayTitle(D("2027-01-03"), Today) == "Sun, Jan 3, 2027" && AgendaText.Month(2026, 11) == "November 2026"
            && AgendaText.Relative(Today, Today) == "Today" && AgendaText.Relative(D("2026-11-26"), Today) == "In 4 days" && AgendaText.Relative(D("2026-11-21"), Today) == "Yesterday");
        check("Agenda (English): statuses",
            S(now, "2026-11-22", Now) == "In progress · 40 minutes left" && S(soon, "2026-11-22", Now) == "In 2 hours" && S(ended, "2026-11-22", Now) == "Ended"
            && AgendaText.Length(TimeSpan.FromSeconds(10)) == "1 minute" && AgendaText.Length(TimeSpan.FromMinutes(61)) == "1 hour");
        check("Agenda (English): events over several days",
            S(trip, "2026-11-24", Now) == "Day 2 of 3 · Nov 23 20:00 – Nov 25 12:00" && S(duty, "2026-11-24", Now) == "Started the day before at 22:00"
            && S(duty, "2026-11-24", T("2026-11-24 00:30")) == "In progress · 1 hour left · Started yesterday at 22:00"
            && S(duty, "2026-11-23", T("2026-11-23 12:00")) == "In 10 hours · Until tomorrow at 02:00" && S(duty, "2026-11-23", Now) == "Until the next day at 02:00");
        check("Agenda (English): due days and the prompt",
            AgendaText.Due(D("2026-11-30"), Today) == "Due Nov 30 · 8 days left" && AgendaText.Due(D("2026-11-21"), Today) == "Due Nov 21 · 1 day overdue"
            && AgendaText.Due(Today, Today) == "Due today" && AgendaText.CarryOver(new AgendaCarryOver(2, true)) == "2 unfinished to-dos from yesterday"
            && AgendaText.CarryOver(new AgendaCarryOver(1, false)) == "1 unfinished to-do from earlier days");
        check("Agenda (English): a source without a name", AgendaText.Source(new AgendaSource { NoticeId = "x" }) == "Notice");
    }
}
