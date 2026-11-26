using System.IO;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;
using BrimDeck.Core.Notices;

namespace ReadmeMedia;

// The notices page's strip between the focus's words and its buttons, with invented notices: a time read from a title ("加入日程"),
// the event added, a notice of plain words with its "加入日程" and "加入待办" buttons and after it was added, a verification code, a long title and
// body with two times, the narrowest page, the English interface and the light theme. The planner's data is made on a folder of its
// own and thrown away; no notice source is started. For checking against the design, not for the README.
internal sealed partial class Renderer
{
    private async Task NoticeAgendaScene()
    {
        const double scale = 2;
        bool animations = _settings.Animations, codes = _settings.NoticeCodes;
        string theme = _settings.PanelTheme, daylight = _settings.ThemeDaylight, language = Loc.IsEnglish ? Loc.English : Loc.Chinese;
        double width = _settings.NoticesWidth, height = _settings.NoticesHeight;
        bool agendaPage = _settings.AgendaPage;
        _settings.Animations = false; _settings.NoticesPage = true; _settings.NoticeCodes = true; _settings.AgendaPage = true;
        foreach (var listed in new[] { "Outlook", "QQ", "Claude" })
            if (_settings.NoticeRule("windows:" + listed) is null) _settings.NoticeApps.Add(new NoticeAppRule { Key = "windows:" + listed, Name = listed });
        _settings.NoticeAppsListed = true;
        var folder = Path.Combine(Path.GetTempPath(), "BrimDeck.ReadmeAgenda." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var agenda = Activator.CreateInstance(Type.GetType("BrimDeck.AgendaCenter, BrimDeck")!, Program.Any, null, [folder, Dispatcher.CurrentDispatcher], null)!;
        Program.SetProperty(app, "Agenda", agenda);
        await Settle();
        var book = (AgendaBook)agenda.GetType().GetProperty("Book")!.GetValue(agenda)!;
        var center = Activator.CreateInstance(Type.GetType("BrimDeck.NoticeCenter, BrimDeck")!, Program.Any, null, [app], null)!;
        Program.SetProperty(app, "Notices", center);
        var hub = (NoticeHub)center.GetType().GetProperty("Hub", Program.Any)!.GetValue(center)!;
        var theme2 = Type.GetType("BrimDeck.PanelTheme, BrimDeck")!.GetMethod("Use", [typeof(DeckSettings), typeof(bool), typeof(bool)])!;
        int serial = 0;
        Notice Add(string appName, string title, string body, int minutesAgo)
        {
            hub.Receive(new NoticeInput
            {
                Origin = NoticeOrigin.Windows, AppKey = "windows:" + appName, AppName = appName, AppId = appName, InstanceKey = "windows:" + appName + ":" + ++serial, Title = title, Body = body,
                Category = NoticeCategory.Message, Capabilities = NoticeCapabilities.OpenApp, CreatedAt = _now.AddMinutes(-minutesAgo)
            }, _now.AddMinutes(-minutesAgo).ToUniversalTime());
            return hub.Notices.First(notice => notice.InstanceKey == "windows:" + appName + ":" + serial);
        }
        var code = Add("Outlook", "GitHub", "请验证你的设备。你的验证码是 482913，10 分钟内有效，请勿告诉他人。", 31);
        Add("QQ", "产品群", "阿杰：下午四点，测试刚过", 18);
        var chat = Add("QQ", "产品群", "小周：好的，更新说明我来写，写完发群里大家看一下有没有遗漏的地方", 12);
        var english = Add("Outlook", "Design review moved to Thursday at 3 PM", "Wang: the room is now on the 5th floor, please confirm.", 7);
        var longer = Add("Outlook", "【重要】评审会改期，请所有参会的同事务必在今天下班之前回复确认能否参加",
            "各位好：原定的评审会改到周四下午三点，如果时间冲突就改到周五上午十点。会议室改到 5 楼东侧，请提前十分钟到场，并带上各自负责部分的最新进展和需要大家讨论的问题。", 5);
        var meeting = Add("Outlook", "周会改到明天下午三点", "王敏：会议室改到 5 楼，请确认能否参加。", 2);
        SelectPage(DeckPage.Notices);

        async Task Shot(string name, Notice focus, double w = 760, double h = 330)
        {
            _settings.NoticesWidth = w; _settings.NoticesHeight = h; Refresh();
            Program.SetField(_deck, "_noticeFocus", focus.Id);
            _deck.SetExpanded(true, true);
            Program.Call(_deck, "RenderNotices", [typeof(bool)], true);
            await Settle();
            await Save("notice-agenda-" + name, scale);
        }
        // The form, opened as the strip's pencil or "修改" opens it but without taking the keyboard.
        async Task FormShot(string name, Notice focus, Action open, double w = 760, double h = 330)
        {
            _settings.NoticesWidth = w; _settings.NoticesHeight = h; Refresh();
            Program.SetField(_deck, "_noticeFocus", focus.Id);
            _deck.SetExpanded(true, true);
            Program.Call(_deck, "RenderNotices", [typeof(bool)], true);
            open();
            await Settle();
            await Save("notice-agenda-" + name, scale);
            Program.Call(_deck, "CloseNoticeForm", []);
        }
        void TimeForm(Notice notice) => Program.Call(_deck, "OpenNoticeForm", [typeof(Notice), typeof(NoticeAgendaTime), typeof(bool)], notice, NoticeAgenda.Read(notice, TimeZoneInfo.Local).Times[0], false);
        void AddedForm(Notice notice)
        {
            var added = book.FromNotice(notice.Id);
            Program.Call(_deck, "OpenNoticeForm", [typeof(Notice), typeof(AgendaEvent), typeof(AgendaTodo), typeof(bool)], notice, added.Events.LastOrDefault(), added.Todos.LastOrDefault(), false);
        }
        // "加入日程" of a notice of plain words: the form as an event at the next half hour.
        void PlainForm(Notice notice)
        {
            var start = AgendaCalendar.NewEventStart(DateOnly.FromDateTime(DateTime.Now), DateTime.Now);
            Program.Call(_deck, "ShowNoticeForm", [typeof(Notice), typeof(string), typeof(string), typeof(bool), typeof(string), typeof(DateTime), typeof(DateTime), typeof(bool), typeof(DateOnly), typeof(bool), typeof(AgendaRepeat), typeof(int?)],
                notice, null, null, false, NoticeAgenda.TodoText(notice), start, start.AddHours(1), false, DateOnly.FromDateTime(start), false, AgendaRepeat.None, AgendaReminders.DefaultTimed);
        }
        void AddEvent(Notice notice, int index = 0)
        {
            var time = NoticeAgenda.Read(notice, TimeZoneInfo.Local).Times[index];
            book.AddEvent(NoticeAgenda.EventTitle(notice, time), time.Moment.Start, time.Moment.End, time.Moment.AllDay, DateTime.Now, source: AgendaSource.From(notice),
                remind: AgendaReminders.Default(time.Moment.AllDay));
        }

        try
        {
            await Shot("1-times", meeting);
            await Shot("2-todo-button", chat);
            await Shot("2b-narrow-todo-button", chat, 460, 280);
            await FormShot("form-0-plain-event", chat, () => PlainForm(chat));
            await Shot("3-code", code);
            await Shot("4-long-two-times", longer);
            await Shot("5-narrow-two-times", longer, 460, 280);
            await Shot("6-narrow-code", code, 460, 280);
            await FormShot("form-1-event", meeting, () => TimeForm(meeting));
            await FormShot("form-2-event-narrow", meeting, () => TimeForm(meeting), 460, 280);
            AddEvent(meeting);
            book.AddTodo(NoticeAgenda.TodoText(chat), DateOnly.FromDateTime(DateTime.Today), DateTime.Now, source: AgendaSource.From(chat));
            await Shot("7-added-event", meeting);
            await Shot("8-added-todo", chat);
            await Shot("9-narrow-added-event", meeting, 460, 280);
            await Shot("9b-narrow-added-todo", chat, 460, 280);
            // A planner reminder as it arrives (AgendaReminders.Notice), on a usual panel and the narrowest.
            var parcel = book.AddEvent("取快递", DateTime.Now.AddMinutes(15), DateTime.Now.AddMinutes(45), false, DateTime.Now, "菜鸟驿站 · 取件码 6-2-3014", remind: 15)!;
            hub.Receive(AgendaReminders.Notice(parcel, DateTime.Now, false, TimeZoneInfo.Local), _now.ToUniversalTime());
            var reminder = hub.Notices.First(notice => notice.InstanceKey == AgendaReminders.InstanceKey(parcel));
            await Shot("18-reminder", reminder);
            await Shot("18b-narrow-reminder", reminder, 460, 280);
            await FormShot("form-3-edit-event", meeting, () => AddedForm(meeting));
            await FormShot("form-4-edit-todo", chat, () => AddedForm(chat));
            await FormShot("form-5-edit-todo-narrow", chat, () => AddedForm(chat), 460, 280);
            AddEvent(longer, 1);
            await Shot("10-one-of-two-added", longer);
            AddEvent(longer, 0);
            await Shot("11-both-added", longer);
            Loc.Use(Loc.English);
            await Shot("12-english-times", english);
            await Shot("13-english-added-event", meeting);
            await Shot("14-english-narrow-code", code, 460, 280);
            await FormShot("form-6-english-event-narrow", english, () => TimeForm(english), 460, 280);
            Loc.Use(language);
            _settings.PanelTheme = PanelThemes.Light; _settings.ThemeDaylight = "";
            theme2.Invoke(null, [_settings, false, false]);
            await Shot("15-light-times", english);
            await Shot("16-light-added-todo", chat);
            await Shot("17-light-code", code);
            await FormShot("form-7-light-edit-event", meeting, () => AddedForm(meeting));
        }
        finally
        {
            Loc.Use(language);
            _settings.PanelTheme = theme; _settings.ThemeDaylight = daylight;
            theme2.Invoke(null, [_settings, false, false]);
            _settings.NoticesWidth = width; _settings.NoticesHeight = height; _settings.Animations = animations; _settings.NoticeCodes = codes; _settings.AgendaPage = agendaPage;
            SelectPage(DeckPage.Usage);
            _deck.SetExpanded(false, true);
            Refresh();
            ((IDisposable)agenda).Dispose();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
