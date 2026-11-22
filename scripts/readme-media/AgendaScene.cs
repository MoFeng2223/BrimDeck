using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;

namespace ReadmeMedia;

// The planner page with invented data, at the design's states: today's events, to-dos (one row under the pointer) and note, an
// event just added from a notice, the add form, an empty day of a month of five weeks, a busy day with events over several days, the
// narrowest panel, an empty note, and the block view of a busy day and of today; then the light theme, 150 %, the largest and the
// lowest sizes, and the narrow and low forms of each view. The clock is fixed at 2026-11-22 14:20. The data lives in a folder of this
// run; no window is activated (the forms are put in place without taking the keyboard). For checking, not for the README.
internal sealed partial class Renderer
{
    private async Task AgendaScene()
    {
        const double scale = 2;
        var folder = Path.Combine(Path.GetTempPath(), "BrimDeck.AgendaScene." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var center = Activator.CreateInstance(Type.GetType("BrimDeck.AgendaCenter, BrimDeck")!, Program.Any, null, [folder, Dispatcher.CurrentDispatcher], null)!;
        Program.SetProperty(app, "Agenda", center);
        for (int i = 0; i < 200 && Program.GetProperty(center, "ReadState") is null; i++) await Task.Delay(20);
        var book = (AgendaBook)Program.GetProperty(center, "Book")!;
        var now = new DateTime(2026, 11, 22, 14, 20, 0);
        Program.SetProperty(_deck, "AgendaClock", (Func<DateTime>)(() => now));
        var ids = FillAgenda(book, now);
        bool animations = _settings.Animations, notices = _settings.NoticesEnabled;
        string theme = _settings.PanelTheme;
        int panelScale = _settings.PanelScale;
        _settings.Animations = false; _settings.NoticesEnabled = true; _settings.AgendaPage = true;
        _settings.AgendaView = AgendaView.Events; _settings.AgendaBlocks = false;
        var use = Type.GetType("BrimDeck.PanelTheme, BrimDeck")!.GetMethod("Use", Program.Any)!;

        async Task Resize(double width, double height) { _settings.AgendaWidth = width; _settings.AgendaHeight = height; Refresh(); await Settle(); }
        object Page() => Program.Field(_deck, "_agenda")!;
        void Choose(DateOnly day) => Program.Call(Page(), "Choose", [typeof(DateOnly)], day);
        void View(AgendaView view) => Program.Call(Page(), "SetView", [typeof(AgendaView)], view);
        void Blocks(bool blocks) => Program.Call(Page(), "SetBlocks", [typeof(bool)], blocks);
        void Redraw() => Program.Call(Page(), "Show", [typeof(bool)], true);
        async Task Shot(string name) { Redraw(); await Settle(); await Save("agenda-" + name, scale); }

        SelectPage(DeckPage.Agenda);
        await Resize(760, 330);
        _deck.SetExpanded(true, true);
        await Settle();
        var today = DateOnly.FromDateTime(now);

        await Shot("p1-today");
        View(AgendaView.Todos); await Settle();
        Hover(_stage, Loc.T("给小周回更新说明", "Reply to Zhou about the release notes"));
        await Settle(); await Save("agenda-p6-todos", scale);
        View(AgendaView.Note);
        await Shot("p8-note");
        View(AgendaView.Events);
        Program.Call(Page(), "Reveal", [typeof(AgendaReveal)], new AgendaReveal(new DateOnly(2026, 11, 26), AgendaView.Events, ids.Added));
        await Settle(); await Save("agenda-p2-from-notice", scale);
        Program.SetField(Page(), "_marked", null);
        Choose(today);
        Form(Loc.T("取体检报告", "Pick up the health report"), now.Date.AddHours(17.5), now.Date.AddHours(18), "new", Loc.T("添加", "Add"), false, false);
        await Shot("p3-add");
        CloseForm();
        Choose(new DateOnly(2026, 12, 3));
        await Shot("p4-empty");
        View(AgendaView.Note);
        await Shot("p9-note-empty");
        View(AgendaView.Events);
        Choose(new DateOnly(2026, 11, 24));
        await Shot("p5-busy");
        Blocks(true);
        await Shot("p10-blocks");
        Choose(today);
        await Shot("p12-blocks-today");
        Blocks(false);

        // A change of an event that began on an earlier day (the start's day shows, with 删除), and a to-do given a time.
        Choose(new DateOnly(2026, 11, 24));
        var trip = book.FindEvent(ids.Trip)!;
        Form(trip.Title, trip.Start, trip.End, "event:" + trip.Id, Loc.T("保存", "Save"), true, true);
        await Shot("edit-earlier-start");
        CloseForm();
        View(AgendaView.Todos);
        await Shot("busy-todos");
        View(AgendaView.Events);

        await Resize(460, 280);
        Choose(today);
        await Shot("p7-narrow");
        View(AgendaView.Todos); await Shot("narrow-todos");
        View(AgendaView.Note); await Shot("narrow-note");
        View(AgendaView.Events); Blocks(true); await Shot("narrow-blocks"); Blocks(false);
        Form(Loc.T("取体检报告", "Pick up the health report"), now.Date.AddHours(17.5), now.Date.AddHours(18), "new", Loc.T("添加", "Add"), false, false);
        await Shot("narrow-add");
        CloseForm();
        Choose(new DateOnly(2026, 12, 3)); await Shot("narrow-other-week");
        Choose(today);

        await Resize(760, 140); await Shot("760x140-events");
        View(AgendaView.Todos); await Shot("760x140-todos");
        View(AgendaView.Note); await Shot("760x140-note");
        View(AgendaView.Events);
        Form(Loc.T("取体检报告", "Pick up the health report"), now.Date.AddHours(17.5), now.Date.AddHours(18), "new", Loc.T("添加", "Add"), false, true);
        await Shot("760x140-add");
        CloseForm();
        await Resize(440, 140);
        Form(Loc.T("取体检报告", "Pick up the health report"), now.Date.AddHours(17.5), now.Date.AddHours(18), "new", Loc.T("添加", "Add"), false, true);
        await Shot("440x140-add");
        await Resize(760, 140);
        CloseForm();
        Choose(today.AddDays(3)); await Shot("760x140-other-day"); Choose(today);
        await Resize(440, 140); await Shot("440x140-events");
        await Resize(440, 210); await Shot("440x210-events");

        await Resize(1200, 400); await Shot("1200x400-events");
        Blocks(true); await Shot("1200x400-blocks"); Blocks(false);
        View(AgendaView.Todos); await Shot("1200x400-todos"); View(AgendaView.Events);
        await Resize(600, 316); await Shot("600x316-events");

        await Resize(760, 330);
        _settings.PanelScale = 150; Refresh(); await Settle();
        await Shot("scale150");
        View(AgendaView.Todos); await Shot("scale150-todos"); View(AgendaView.Events);
        _settings.PanelScale = panelScale; Refresh(); await Settle();

        _settings.PanelTheme = PanelThemes.Light; use.Invoke(null, [_settings, false, false]); Refresh(); await Settle();
        await Shot("light");
        View(AgendaView.Todos); await Settle(); Hover(_stage, Loc.T("给小周回更新说明", "Reply to Zhou about the release notes")); await Settle(); await Save("agenda-light-todos", scale);
        View(AgendaView.Events); Blocks(true); await Shot("light-blocks"); Blocks(false);
        Choose(new DateOnly(2026, 11, 24)); await Shot("light-busy"); Choose(today);
        _settings.PanelTheme = theme; use.Invoke(null, [_settings, false, false]); Refresh(); await Settle();

        // The line about a file that cannot be read or saved.
        Program.SetProperty(center, "SaveFailed", true); await Shot("save-failed"); Program.SetProperty(center, "SaveFailed", false);

        SelectPage(DeckPage.Usage);
        _deck.SetExpanded(false, true);
        _settings.Animations = animations; _settings.NoticesEnabled = notices; _settings.AgendaView = AgendaView.Events; _settings.AgendaBlocks = false;
        Refresh(); await Settle();
        try { Directory.Delete(folder, true); } catch (IOException) { }

        // The form, put in place as a click would, without taking the keyboard.
        void Form(string title, DateTime start, DateTime end, string what, string submit, bool delete, bool startDay)
        {
            var type = Type.GetType("BrimDeck.AgendaEventForm, BrimDeck")!;
            var look = Program.GetProperty(_deck, "Look");
            var form = Activator.CreateInstance(type, Program.Any, null, [look, (Action<TextBox>)(_ => { }), submit, delete, startDay], null)!;
            Program.SetProperty(form, "Today", (Func<DateOnly>)(() => today));
            Program.Call(form, "Fill", [typeof(string), typeof(DateTime), typeof(DateTime), typeof(bool)], title, start, end, false);
            Program.SetField(Page(), "_form", form);
            Program.SetField(Page(), "_formFor", what);
        }
        void CloseForm() => Program.Call(Page(), "CloseForm", []);
    }

    // The pointer resting on the element named name (its own hover, as MouseEnter gives it).
    private static void Hover(DependencyObject root, string name)
    {
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is UIElement element && AutomationProperties.GetName(node) == name && node is Border)
            {
                element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
                Console.WriteLine("hover: " + name);
                return;
            }
            for (int i = 0, count = VisualTreeHelper.GetChildrenCount(node); i < count; i++) pending.Push(VisualTreeHelper.GetChild(node, i));
        }
        Console.WriteLine("hover: not found: " + name);
    }

    // The design's data: today (2026-11-22) with five events, four to-dos, two left from yesterday and a note; a busy 24th with an
    // event over three days, one from the evening before, overlapping meetings and a very long title; the 26th with a meeting
    // added from a notice; marks over the month and into December.
    private static (string Added, string Trip) FillAgenda(AgendaBook book, DateTime now)
    {
        DateTime T(int month, int day, double hour) => new DateTime(2026, month, day).AddHours(hour);
        DateOnly D(int month, int day) => new(2026, month, day);
        void E(string zh, string en, DateTime start, DateTime end, bool allDay = false, string? detail = null, AgendaSource? source = null)
            => book.AddEvent(Loc.T(zh, en), start, end, allDay, now, detail, source);
        AgendaSource Source(string app, string? sender, string key = "") => new() { NoticeId = Guid.NewGuid().ToString("N"), AppKey = "windows:" + (key.Length > 0 ? key : app), AppName = app, Sender = sender };

        E("Alpha 测试截止", "Alpha testing ends", T(11, 22, 0), T(11, 22, 0), allDay: true);
        E("整理本周计划", "Plan the week", T(11, 22, 10), T(11, 22, 11));
        E("和小林对设计稿", "Design review with Lin", T(11, 22, 14), T(11, 22, 15));
        E("取快递", "Pick up the parcel", T(11, 22, 16.5), T(11, 22, 17));
        E("写周报", "Write the weekly report", T(11, 22, 20), T(11, 22, 21.5));

        var trip = book.AddEvent(Loc.T("出差上海", "Trip to Shanghai"), T(11, 23, 20), T(11, 25, 12), false, now)!;
        E("小周生日", "Zhou's birthday", T(11, 24, 0), T(11, 24, 0), allDay: true);
        E("发布值守", "Release watch", T(11, 23, 22), T(11, 24, 2));
        E("站会", "Stand-up", T(11, 24, 9), T(11, 24, 9.5), detail: Loc.T("腾讯会议", "Online meeting"));
        E("第四季度产品评审会暨下季度规划讨论（含供应商沟通环节与预算复核）", "Fourth-quarter product review and next quarter's planning (with the suppliers' session and the budget check)",
            T(11, 24, 9.5), T(11, 24, 11.5), detail: Loc.T("5 楼大会议室", "Big room, 5th floor"), source: Source("Outlook", null));
        E("供应商电话", "Supplier call", T(11, 24, 10), T(11, 24, 11));
        E("设计走查", "Design walkthrough", T(11, 24, 10.5), T(11, 24, 12.5));
        E("面试：前端工程师候选人", "Interview: front-end engineer", T(11, 24, 11), T(11, 24, 12));
        E("和财务核对 10 月报销", "Check October's expenses with finance", T(11, 24, 13.5), T(11, 24, 14));
        E("产品周会", "Product weekly", T(11, 24, 15), T(11, 24, 16));
        E("代码评审", "Code review", T(11, 24, 17), T(11, 24, 18));
        E("交接文档", "Hand-over notes", T(11, 25, 15), T(11, 25, 16));

        E("版本规划", "Release planning", T(11, 26, 10), T(11, 26, 11), detail: Loc.T("腾讯会议", "Online meeting"));
        var added = book.AddEvent(Loc.T("周会", "Weekly meeting"), T(11, 26, 15), T(11, 26, 16), false, now, null, Source("Outlook", Loc.T("王敏", "Wang Min")))!;

        foreach (var (month, day) in new[] { (10, 30), (11, 3), (11, 9), (11, 13), (11, 16), (11, 18), (11, 20), (12, 8), (12, 31) })
            E("例会", "Regular meeting", T(month, day, 10), T(month, day, 11));

        void Todo(string zh, string en, DateOnly? day, DateOnly? due = null, AgendaSource? source = null, bool done = false)
        {
            var todo = book.AddTodo(Loc.T(zh, en), day, now, due, source)!;
            if (done) book.SetDone(todo.Id, true, now);
        }
        Todo("回复房东的邮件", "Answer the landlord's mail", D(11, 21));
        Todo("寄回维修的耳机", "Send the headphones for repair", D(11, 21));
        Todo("给小周回更新说明", "Reply to Zhou about the release notes", D(11, 22), source: Source("QQ", Loc.T("产品群", "Product team")));
        Todo("订周四的会议室", "Book the room for Thursday", D(11, 22), done: true);
        Todo("整理宣传视频素材", "Sort the promo video footage", null, due: D(11, 30));
        Todo("把宣传视频的配乐换成先铺垫再进主旋律的那一版，并重新导出三种分辨率，发给小林确认", "Swap the promo video's music for the version that builds up before the main theme, export the three resolutions again and send them to Lin",
            D(11, 22));
        foreach (var (zh, en) in new[] { ("换显示器支架", "Get a new monitor arm"), ("整理照片备份", "Sort the photo backups"), ("学会做红烧肉", "Learn to cook braised pork"),
                     ("读完《人月神话》", "Finish The Mythical Man-Month"), ("清理旧的云盘文件", "Clear out old cloud files"), ("给阳台的花换盆", "Repot the balcony plants") })
            Todo(zh, en, null);
        Todo("买咖啡豆", "Buy coffee beans", D(11, 5));
        Todo("交水电费", "Pay the bills", D(11, 9));
        Todo("预约体检", "Book a check-up", D(12, 15));
        for (int i = 1; i <= 9; i++) Todo($"会议准备第 {i} 项", $"Meeting preparation, item {i}", D(11, 24));

        book.SetNote(D(11, 22), Loc.T("评审时带上上次的反馈记录，重点说竖排的最小宽度。\n\n快递在 3 号柜，驿站 21:00 关门。\n\n周会要讲的三件事：通知识别的进度、计划页的设计稿、下周的发布安排。",
            "Bring last time's feedback to the review; mostly about the narrowest width of the vertical layout.\n\nThe parcel is in locker 3; the shop closes at 21:00.\n\nThree things for the weekly: notice recognition, the planner design, next week's release."));
        foreach (var day in new[] { D(11, 12), D(11, 18), D(11, 24), D(11, 26) }) book.SetNote(day, Loc.T("记一笔", "A note"));
        return (added.Id, trip.Id);
    }
}
