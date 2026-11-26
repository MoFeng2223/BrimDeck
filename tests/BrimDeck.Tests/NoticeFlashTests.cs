using BrimDeck.Core;
using BrimDeck.Core.Notices;

// The taskbar flash source's decisions (ChatFlashFilter) with a fake clock and fake process lookups, and its setting.
internal static class NoticeFlashTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    public static void Run(Action<string, bool> check)
    {
        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try { Decisions(check); Coalescing(check); Table(check); Setting(check); DingTalk(check); WeChatCall(check); FeishuCall(check); }
        finally { Loc.Use(language); }
    }

    private sealed class World
    {
        public DateTimeOffset Now = T0;
        public readonly Dictionary<nint, string?> Images = new() { [10] = "Weixin.exe", [11] = "WeChat.exe", [20] = "notepad.exe", [30] = "QQ.exe" };
        public readonly HashSet<nint> Front = [];
        public ChatFlashFilter Filter(IReadOnlyList<ChatFlashApp>? apps = null) => new(handle => Images.GetValueOrDefault(handle), Front.Contains, () => Now, null, apps);
    }

    private static void Decisions(Action<string, bool> check)
    {
        var world = new World();
        var notice = world.Filter().OnFlash(10);
        check("Flash: a flashing Weixin.exe window is a new-message notice naming WeChat", notice is { Title: "微信", Body: "有新消息", AppName: "微信", Origin: NoticeOrigin.Windows, Category: NoticeCategory.Message });
        check("Flash: the notice carries no sender, text or icon", notice!.Picture is null && notice.IconRef is null && notice.Actions.Count == 0 && notice.Capabilities == NoticeCapabilities.None);
        check("Flash: WeChat.exe is WeChat too, and the file name is matched without case", world.Filter().OnFlash(11)?.AppKey == "flash:wechat"
            && new ChatFlashFilter(_ => @"C:\Program Files\Tencent\WEIXIN.EXE", _ => false, () => T0).OnFlash(1)?.AppKey == "flash:wechat");
        check("Flash: an unknown program gives nothing", world.Filter().OnFlash(20) is null);
        check("Flash: a window that cannot be looked up gives nothing", world.Filter().OnFlash(99) is null && world.Filter().OnFlash(0) is null);
        check("Flash: a row that is not enabled gives nothing (QQ is listed but off)", world.Filter().OnFlash(30) is null);
        var enabledQq = world.Filter([new ChatFlashApp("qq", "QQ", "QQ", ["QQ.exe"], true)]);
        check("Flash: another application is added by one row", enabledQq.OnFlash(30) is { AppKey: "flash:qq", Title: "QQ" });
        Loc.Use(Loc.English);
        var english = world.Filter().OnFlash(11);
        Loc.Use(Loc.Chinese);
        check("Flash: the words follow the language", english is { Title: "WeChat", Body: "New message" });
        world.Front.Add(10);
        check("Flash: nothing while the user is already in the application", world.Filter().OnFlash(10) is null);
    }

    private static void Coalescing(Action<string, bool> check)
    {
        var world = new World();
        var filter = world.Filter();
        check("Flash: a round is five seconds", ChatFlashFilter.DefaultWindow == TimeSpan.FromSeconds(5));
        var first = filter.OnFlash(10)!;
        world.Now = T0.AddSeconds(1);
        var second = filter.OnFlash(10)!;
        world.Now = T0.AddSeconds(4.9);
        var third = filter.OnFlash(11)!;
        check("Flash: the flashes of one message are the same notice (the hub updates it)", first.InstanceKey == second.InstanceKey && second.InstanceKey == third.InstanceKey && first.AppKey == second.AppKey);
        world.Now = T0.AddSeconds(100);
        var later = filter.OnFlash(10)!;
        check("Flash: the next message, while the first is still unread, is a new notice", later.InstanceKey != first.InstanceKey);
        world.Now = T0.AddSeconds(102);
        check("Flash: a round's flashes count from its first one", filter.OnFlash(10)!.InstanceKey == later.InstanceKey);
        world.Now = T0.AddSeconds(105);
        check("Flash: a flash five seconds after a round began is another message, told again", filter.OnFlash(10)!.InstanceKey != later.InstanceKey);
        world.Now = T0.AddMinutes(-1);
        check("Flash: a clock that went back starts a new notice", filter.OnFlash(10)!.InstanceKey != later.InstanceKey);

        // The hub with the same inputs: a burst is listed once and shown once; the next burst is listed beside it.
        var hub = new NoticeHub();
        var arrived = hub.Receive(first, T0);
        var merged = hub.Receive(second, T0.AddSeconds(5));
        check("Flash: the hub lists a burst once and shows it once", arrived is { Receipt: NoticeReceipt.Accepted, Present: true } && merged is { Receipt: NoticeReceipt.Updated, Present: false } && hub.Notices.Count == 1);
        check("Flash: the next burst is shown and listed beside it", hub.Receive(later, T0.AddMinutes(4)).Present && hub.Notices.Count == 2);

        // BrimDeck restarted: the notices of the run before are kept, and a new run's first message is a new notice beside them.
        var restarted = world.Filter().OnFlash(10)!;
        check("Flash: after a restart the first message is not taken for a kept notice of the run before", restarted.InstanceKey != first.InstanceKey
            && hub.Receive(restarted, T0.AddMinutes(30)) is { Receipt: NoticeReceipt.Accepted, Present: true });

        var two = new World();
        two.Images[40] = "Lark.exe";
        var both = two.Filter([new ChatFlashApp("wechat", "微信", "WeChat", ["Weixin.exe"], true), new ChatFlashApp("feishu", "飞书", "Feishu", ["Lark.exe"], true)]);
        check("Flash: two applications flashing are two notices", both.OnFlash(10)!.InstanceKey != both.OnFlash(40)!.InstanceKey);

        var front = new World();
        front.Front.Add(10);
        var ignoring = front.Filter();
        ignoring.OnFlash(10);
        front.Front.Clear();
        front.Now = T0.AddSeconds(10);
        check("Flash: a flash ignored in front does not use up the window", ignoring.OnFlash(10) is not null);
    }

    private static void Table(Action<string, bool> check)
    {
        check("Flash: WeChat, Feishu and DingTalk are enabled", ChatFlashApps.All.Where(app => app.Enabled).Select(app => app.Id).SequenceEqual(["wechat", "feishu", "dingtalk"]));
        check("Flash: ids are unique and every row names executables", ChatFlashApps.All.Select(app => app.Id).Distinct().Count() == ChatFlashApps.All.Count
            && ChatFlashApps.All.All(app => app.Executables.Count > 0 && app.Executables.All(e => e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))));
        check("Flash: lookups by name and by path", ChatFlashApps.Find(@"D:\x\Weixin.exe")?.Id == "wechat" && ChatFlashApps.Find("") is null && ChatFlashApps.Find(null) is null && ChatFlashApps.Find("weixin") is null);
        var wechat = ChatFlashApps.All.First(app => app.Id == "wechat");
        check("Flash: the installed program is found from the installed programs' icons, quoted or with an index",
            ChatFlashApps.InstalledPath(wechat, [null, "", @"D:\Tencent\QQNT\QQ.exe,0", "\"D:\\Tencent\\Weixin\\Weixin.exe\""]) == @"D:\Tencent\Weixin\Weixin.exe"
            && ChatFlashApps.InstalledPath(wechat, [@"C:\Program Files\Tencent\WeChat\WeChat.exe,0"]) == @"C:\Program Files\Tencent\WeChat\WeChat.exe"
            && ChatFlashApps.InstalledPath(wechat, [@"C:\Program Files\Tencent\WeChat\WeChat.exe"]) == @"C:\Program Files\Tencent\WeChat\WeChat.exe");
        check("Flash: other programs, network paths, relative paths and broken quotes are not the installed program",
            ChatFlashApps.InstalledPath(wechat, [@"D:\Tencent\QQNT\QQ.exe", @"\\server\share\Weixin.exe", "Weixin.exe", "\"D:\\Tencent\\Weixin\\Weixin.exe", @"D:\x\..\Weixin.exe", @"D:\Weixin\Weixin.exe.bak"]) is null
            && ChatFlashApps.InstalledPath(wechat, []) is null);
    }

    // WeChat's call windows as UI Automation read them on 2026-11-26 (WeChatCalls).
    private static void WeChatCall(Action<string, bool> check)
    {
        static PopupText T(string className, string id, string name) => new(className, name, id);
        PopupText[] Window(string tips) => [T("mmui::XHBoxView", "voip_caller_view", "泡泡" + tips), T("mmui::XTextView", WeChatCalls.CallerId, "泡泡"), T("mmui::VOIPAnimateTextView", WeChatCalls.TipsId, tips)];
        var voice = WeChatCalls.Read(WeChatCalls.CallClass, "微信", Window("邀请你语音通话.."), "wechatcall:1");
        check("WeChat: a ringing voice call is a call from its caller, without the dots that keep changing",
            voice is { Title: "泡泡", Body: "邀请你语音通话", Call: true, AppKey: WeChatRows.AppKey, AppName: "微信", InstanceKey: "wechatcall:1" });
        check("WeChat: a ringing video call says so", WeChatCalls.Read(WeChatCalls.CallClass, "微信", Window("邀请你视频通话."), "k") is { Title: "泡泡", Body: "邀请你视频通话", Call: true });
        check("WeChat: a call window whose invitation is not written yet says nothing, and other windows say nothing",
            WeChatCalls.Read(WeChatCalls.CallClass, "微信", [T("mmui::XTextView", WeChatCalls.CallerId, "泡泡"), T("mmui::VOIPAnimateTextView", WeChatCalls.TipsId, "..")], "k") is null
            && WeChatCalls.Read("mmui::MainWindow", "微信", Window("邀请你语音通话"), "k") is null);
        check("WeChat: a call without a caller's name is a call from WeChat",
            WeChatCalls.Read(WeChatCalls.CallClass, "微信", [T("mmui::VOIPAnimateTextView", WeChatCalls.TipsId, "邀请你语音通话")], "k") is { Title: "微信", Body: "邀请你语音通话" });
        check("WeChat: only its call window, and only in Weixin.exe, is read",
            WeChatCalls.Watched("WEIXIN.EXE", WeChatCalls.CallClass) && !WeChatCalls.Watched("Weixin.exe", "mmui::MainWindow")
            && !WeChatCalls.Watched("WeChatAppEx.exe", WeChatCalls.CallClass) && !WeChatCalls.Watched(null, WeChatCalls.CallClass));
        check("WeChat: a call's notice is not a chat's, so it offers no reply", WeChatRows.ChatOf(new Notice { AppKey = WeChatRows.AppKey, Title = "泡泡", InstanceKey = "wechatcall:1:2" }) is null);
    }

    // Feishu's call window as UI Automation read it on 2026-11-27, its interface in traditional Chinese (FeishuCalls).
    private static void FeishuCall(Action<string, bool> check)
    {
        static PopupText L(string name) => new("Label", name);
        static PopupText B(string name) => new("LabelButton", name, Button: true);
        PopupText[] video = [new("RoundImageButton", "", Button: true), L("用户546325"), L("邀請你進行視訊通話"), B("拒絕"), B("視訊接聽"), B("語音接聽"),
            L("Your current call will end if you accept this call."), L("Your current call will end if you accept this call.")];
        check("Feishu: a ringing video call is a call from its caller", FeishuCalls.Read(FeishuCalls.WindowClass, "飛書會議", video, "feishu:call:1")
            is { Title: "用户546325", Body: "邀請你進行視訊通話", Call: true, AppKey: FeishuBanners.AppKey, InstanceKey: "feishu:call:1" });
        check("Feishu: a voice call in simplified Chinese is read the same way",
            FeishuCalls.Read(FeishuCalls.WindowClass, "飞书会议", [L("小周"), L("邀请你进行语音通话"), B("拒绝"), B("接听")], "k") is { Title: "小周", Body: "邀请你进行语音通话" });
        check("Feishu: a window without an invitation, or without a button that declines, says nothing",
            FeishuCalls.Read(FeishuCalls.WindowClass, "飛書會議", [L("用户546325"), B("拒絕")], "k") is null
            && FeishuCalls.Read(FeishuCalls.WindowClass, "飛書會議", [L("用户546325"), L("邀請你進行視訊通話")], "k") is null
            && FeishuCalls.Read("Chrome_WidgetWin_1", "飛書會議", video, "k") is null);
        check("Feishu: only its windows of the call window's class are read",
            FeishuCalls.Watched("Feishu.exe", FeishuCalls.WindowClass) && FeishuCalls.Watched("LARK.EXE", FeishuCalls.WindowClass)
            && !FeishuCalls.Watched("Feishu.exe", "Chrome_WidgetWin_1") && !FeishuCalls.Watched("chrome.exe", FeishuCalls.WindowClass) && !FeishuCalls.Watched(null, FeishuCalls.WindowClass));
        check("Feishu: a video call can be answered with video or with voice only, by the buttons' words",
            FeishuCalls.Accept.Contains("視訊接聽") && FeishuCalls.Voice.Contains("語音接聽") && FeishuCalls.Decline.Contains("拒絕") && !FeishuCalls.Accept.Intersect(FeishuCalls.Voice).Any());
    }

    // DingTalk's windows as UI Automation read them on 2026-11-26 (DingTalkPopups).
    private static void DingTalk(Action<string, bool> check)
    {
        static PopupText Q(string name) => new("QLabel", name);
        static PopupText A(string name) => new("dtguitoolkit::AutoAdjustLabel", name);
        var call = DingTalkPopups.Read(DingTalkPopups.CallClass, "语音通话", [Q("语音通话"), Q("余昊"), Q("余昊"), Q("邀请你语音通话"), Q("通话纪要"), Q("挂断"), Q("接听")], "k3");
        check("DingTalk: a ringing call is a call from its caller", call is { Title: "余昊", Body: "邀请你语音通话", Call: true });
        var meeting = DingTalkPopups.Read(DingTalkPopups.MeetingClass, "钉钉视频会议", [A("余昊 邀请你加入"), A("余昊"), A("余昊,余昊_1126"), Q("接听"), Q("拒绝"), Q("麦克风已开"), Q("摄像头已关")], "k4");
        check("DingTalk: a meeting invitation is a call from who invites, with the meeting", meeting is { Title: "余昊", Body: "钉钉视频会议 · 余昊,余昊_1126", Call: true });
        check("DingTalk: a call window without an invitation says nothing", DingTalkPopups.Read(DingTalkPopups.CallClass, "语音通话", [Q("语音通话"), Q("挂断")], "k") is null
            && DingTalkPopups.Read(DingTalkPopups.MeetingClass, "钉钉语音会议", [A("余昊")], "k") is null);
        check("DingTalk: only its call windows, and only in its call program, are read",
            DingTalkPopups.Watched("TBLIVE.EXE", DingTalkPopups.CallClass) && DingTalkPopups.Watched("tblive.exe", DingTalkPopups.MeetingClass)
            && !DingTalkPopups.Watched("DingTalk.exe", DingTalkPopups.CallClass) && !DingTalkPopups.Watched("tblive.exe", "DtMainFrameView")
            && !DingTalkPopups.Watched("other.exe", DingTalkPopups.CallClass) && !DingTalkPopups.Watched(null, DingTalkPopups.CallClass));
        check("DingTalk: its flashes are DingTalk's notices", new ChatFlashFilter(_ => @"D:\Program Files (x86)\DingDing\main\current\DingTalk.exe", _ => false, () => T0).OnFlash(1) is { AppKey: DingTalkPopups.AppKey, Title: "钉钉", Body: "有新消息" });

        // DingTalk flashes until read: three flashes about 1.06 seconds apart, then still for up to 3.2 seconds (measured).
        var now = T0;
        var filter = new ChatFlashFilter(_ => "DingTalk.exe", _ => false, () => now);
        var first = filter.OnFlash(1)!;
        bool one = true;
        double start = 0;
        foreach (double still in new[] { 2.09, 1.97, 3.04, 1.75, 3.17, 1.58, 1.14 })
        {
            foreach (double at in new[] { 1.06, 2.12 }) { now = T0.AddSeconds(start + at); one &= filter.OnFlash(1)!.InstanceKey == first.InstanceKey; }
            start += 2.12 + still;
            now = T0.AddSeconds(start);
            one &= filter.OnFlash(1)!.InstanceKey == first.InstanceKey;
        }
        check("DingTalk: a flashing that goes on until read is one message", one);
        now = now.AddSeconds(4.1);
        check("DingTalk: after four seconds still, a flash is another message", filter.OnFlash(1)!.InstanceKey != first.InstanceKey && ChatFlashApps.DingTalkQuiet == TimeSpan.FromSeconds(4));
    }

    private static void Setting(Action<string, bool> check)
    {
        var defaults = new DeckSettings();
        defaults.Normalize();
        check("Flash: WeChat's and DingTalk's taskbar flash are listed for a new user", defaults.NoticeRule("flash:wechat") is not null && defaults.NoticeReceives("flash:wechat")
            && defaults.NoticeRule(DingTalkPopups.AppKey) is { ReadContent: false } && defaults.NoticeReceives(DingTalkPopups.AppKey));
        string folder = Path.Combine(Path.GetTempPath(), "BrimDeck-FlashTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(folder);
            store.Save(new DeckSettings { NoticeIsland = true, NoticeAppsListed = true, NoticeApps = [] });
            check("Flash: taken off the list, WeChat's flashes are not received", !store.Load().NoticeReceives("flash:wechat"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
