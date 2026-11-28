using System.IO;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;

namespace ReadmeMedia;

// The collapsed row's planner part (MainWindow.CompactAgenda.cs, AgendaStripView) with invented data: an event later today, within
// the hour, under way, as its time alone, with a title longer than the row shows, an all-day one; the note, a long one; the to-dos
// as a number and as words, with overdue ones, with only overdue ones left; Auto through a day and All with every kind; a capsule
// with a width of its own that narrows, where the part gives up a step at a time; the notch, and the notch on a side edge. The
// data lives in a folder of this run. For checking.
internal sealed partial class Renderer
{
    private async Task AgendaStripScene()
    {
        const double scale = 3;
        Program.SetProperty(app, "Exiting", true);
        var folder = Path.Combine(Path.GetTempPath(), "BrimDeck.AgendaStripScene." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var center = Activator.CreateInstance(Type.GetType("BrimDeck.AgendaCenter, BrimDeck")!, Program.Any, null, [folder, Dispatcher.CurrentDispatcher], null)!;
        Program.SetProperty(app, "Agenda", center);
        for (int i = 0; i < 200 && Program.GetProperty(center, "ReadState") is null; i++) await Task.Delay(20);
        var book = (AgendaBook)Program.GetProperty(center, "Book")!;
        var day = new DateTime(2026, 11, 22);
        var now = day.AddHours(15).AddMinutes(10);
        Program.SetProperty(_deck, "AgendaClock", (Func<DateTime>)(() => now));
        DateTime At(int hour, int minute) => day.AddHours(hour).AddMinutes(minute);
        var today = DateOnly.FromDateTime(day);

        bool animations = _settings.Animations;
        var occupied = _settings.Occupied;
        _settings.Animations = false; _settings.Occupied = WindowBehavior.Normal; _settings.AgendaPage = true;
        _settings.NotchAgenda = true; _settings.CapsuleAgenda = true; _settings.NotchNotices = false; _settings.CapsuleNotices = false;
        _settings.NotchTransfer = false; _settings.CapsuleTransfer = false; _settings.NotchClock = true; _settings.CapsuleClock = true;
        _settings.AgendaStripMode = AgendaStripMode.Auto; _settings.AgendaStripEvents = true; _settings.AgendaStripTodos = true; _settings.AgendaStripNote = true;
        _settings.AgendaStripEventForm = AgendaStripEventForm.Title; _settings.AgendaStripTodoForm = AgendaStripTodoForm.Count;
        _settings.Style = CompactStyle.Capsule; _settings.CapsuleAutoWidth = true;
        _deck.SetExpanded(false, true);

        async Task Shot(string name)
        {
            Refresh(); await Settle(); await Task.Delay(400); Refresh(); await Settle();
            await Save("agenda-strip-" + name, scale);
        }
        void Only(bool events, bool todos, bool note) { _settings.AgendaStripEvents = events; _settings.AgendaStripTodos = todos; _settings.AgendaStripNote = note; }

        // An event.
        Only(true, false, false);
        book.AddEvent(Loc.T("整理本周计划", "Plan the week"), At(10, 0), At(11, 0), false, now);
        var meeting = book.AddEvent(Loc.T("和小林对设计稿", "Review the design with Lin"), At(14, 0), At(15, 0), false, now)!;
        var parcel = book.AddEvent(Loc.T("取快递", "Pick up the parcel"), At(16, 30), At(17, 0), false, now)!;
        await Shot("event-later");
        now = At(16, 10); await Shot("event-soon");
        now = At(14, 20); await Shot("event-running");
        now = At(15, 10);
        _settings.AgendaStripEventForm = AgendaStripEventForm.Time; await Shot("event-time-only");
        _settings.AgendaStripEventForm = AgendaStripEventForm.Title;
        book.UpdateEvent(parcel.Id, Loc.T("和设计、开发、测试一起过下个版本的需求和排期", "Go through the next version's requirements and schedule with design, development and testing"), At(16, 30), At(17, 0), false, null);
        await Shot("event-long");
        book.UpdateEvent(parcel.Id, Loc.T("取快递", "Pick up the parcel"), At(16, 30), At(17, 0), false, null);
        var allDay = book.AddEvent(Loc.T("Alpha 测试截止", "Alpha testing ends"), day, day, true, now)!;
        now = At(17, 30); await Shot("event-all-day");
        now = At(15, 10);

        // The note.
        Only(false, false, true);
        book.SetNote(today, Loc.T("下午把周报发给老陈", "Send Chen the weekly report this afternoon"));
        await Shot("note");
        book.SetNote(today, Loc.T("记得给妈妈回电话，晚上八点前订好周末回家的车票\n还要买猫粮", "Call mum back, and book the weekend's train home before eight tonight\nand buy cat food"));
        await Shot("note-long");
        book.SetNote(today, Loc.T("下午把周报发给老陈", "Send Chen the weekly report this afternoon"));

        // The to-dos.
        Only(false, true, false);
        var rent = book.AddTodo(Loc.T("交房租", "Pay the rent"), today, now)!;
        var mail = book.AddTodo(Loc.T("给小林回邮件", "Answer Lin's mail"), today, now)!;
        var demo = book.AddTodo(Loc.T("准备周一的演示", "Prepare Monday's demo"), today, now)!;
        await Shot("todos-count");
        _settings.AgendaStripTodoForm = AgendaStripTodoForm.Text; await Shot("todos-text");
        var report = book.AddTodo(Loc.T("交报告", "Hand in the report"), null, now, due: today.AddDays(-1))!;
        await Shot("todos-text-overdue");
        _settings.AgendaStripTodoForm = AgendaStripTodoForm.Count; await Shot("todos-count-overdue");
        foreach (var todo in new[] { rent, mail, demo }) book.SetDone(todo.Id, true, now);
        _settings.AgendaStripTodoForm = AgendaStripTodoForm.Text; await Shot("todos-only-overdue");
        foreach (var todo in new[] { rent, mail, demo }) book.SetDone(todo.Id, false, now);
        book.RemoveTodo(report.Id);
        _settings.AgendaStripTodoForm = AgendaStripTodoForm.Count;

        // Auto through a day, and All.
        Only(true, true, true);
        now = At(9, 10); await Shot("auto-0910-note");
        now = At(13, 5); await Shot("auto-1305-soon");
        now = At(14, 20); await Shot("auto-1420-running");
        now = At(15, 5); await Shot("auto-1505-note");
        book.SetNote(today, null); await Shot("auto-no-note-todos");
        foreach (var todo in new[] { rent, mail, demo }) book.SetDone(todo.Id, true, now);
        await Shot("auto-no-note-no-todos-event");
        foreach (var todo in new[] { rent, mail, demo }) book.SetDone(todo.Id, false, now);
        book.SetNote(today, Loc.T("周报发给老陈", "Report to Chen"));
        now = At(15, 10);
        _settings.AgendaStripMode = AgendaStripMode.All;
        await Shot("all");
        now = At(14, 20); await Shot("all-running");
        now = At(15, 10);

        // A capsule with a width of its own, narrowing: all three kinds, then the note alone, then an event alone.
        _settings.CapsuleAutoWidth = false;
        book.SetNote(today, Loc.T("下午把周报发给老陈，顺便问一下预算", "Send Chen the weekly report this afternoon and ask about the budget"));
        _settings.AgendaStripTodoForm = AgendaStripTodoForm.Text;
        foreach (var width in new[] { 520, 420, 340, 280, 230, 190, 150, 110 })
        {
            _settings.CapsuleWidth = width;
            await Shot("fixed-all-" + width);
        }
        _settings.AgendaStripTodoForm = AgendaStripTodoForm.Count;
        Only(false, false, true);
        foreach (var width in new[] { 300, 200, 140, 100 })
        {
            _settings.CapsuleWidth = width;
            await Shot("fixed-note-" + width);
        }
        Only(true, false, false);
        book.UpdateEvent(parcel.Id, Loc.T("和设计、开发一起过需求", "Go through the requirements with design and development"), At(16, 30), At(17, 0), false, null);
        foreach (var width in new[] { 300, 200, 140, 100 })
        {
            _settings.CapsuleWidth = width;
            await Shot("fixed-event-" + width);
        }
        _settings.CapsuleAutoWidth = true; _settings.CapsuleWidth = CollapsedLimits.CapsuleWidthDefault;

        // The notch, and the notch on a side edge.
        Only(true, true, true);
        _settings.Style = CompactStyle.Notch;
        await Shot("notch-all");
        _settings.AgendaStripMode = AgendaStripMode.Auto;
        await Shot("notch-auto");
        _settings.AgendaStripMode = AgendaStripMode.All;
        _settings.NotchEdge = ScreenEdge.Left;
        await Shot("column-all");
        _settings.NotchEdge = ScreenEdge.Top;

        // The planner's settings page (Auto, then All with the to-dos as words and the note off) and the part's row in the
        // collapsed view's content.
        var settingsTheme = _settings.Theme;
        _settings.AgendaStripMode = AgendaStripMode.Auto; _settings.Style = CompactStyle.Capsule;
        await SettingsPage("agenda-strip-settings", 23, height: 1180);
        _settings.AgendaStripMode = AgendaStripMode.All; _settings.AgendaStripTodoForm = AgendaStripTodoForm.Text; _settings.AgendaStripNote = false;
        await SettingsPage("agenda-strip-settings-all-light", 23, height: 1180, theme: SettingsTheme.Light);
        _settings.AgendaStripTodoForm = AgendaStripTodoForm.Count; _settings.AgendaStripNote = true;
        await SettingsPage("agenda-strip-settings-collapsed", 1, height: 1500);
        _settings.Theme = settingsTheme;

        book.RemoveEvent(meeting.Id); book.RemoveEvent(parcel.Id); book.RemoveEvent(allDay.Id);
        _settings.AgendaStripMode = AgendaStripMode.Auto; _settings.Style = CompactStyle.Capsule;
        _settings.Occupied = occupied; _settings.Animations = animations; Refresh();
        Program.SetProperty(app, "Exiting", false);
        await Settle();
    }
}
