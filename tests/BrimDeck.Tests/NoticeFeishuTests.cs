using BrimDeck.Core;
using BrimDeck.Core.Notices;

// Feishu's banners as UI Automation read them on 2026-11-26 (FeishuBanners), its taskbar flash and its setting.
internal static class NoticeFeishuTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 21, 0, 0, TimeSpan.Zero);

    public static void Run(Action<string, bool> check)
    {
        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try { Banners(check); Placement(check); Flash(check); Setting(check); NoWaiting(check); }
        finally { Loc.Use(language); }
    }

    private static FeishuPart L(string name) => new("Text", "Label", name);

    // The banner's parts under its WidgetDelegateView, in order: three labels, then its buttons and the quick reply box.
    private static FeishuPart[] Banner(string first, string third) =>
        [L(first), L(""), L(third), new("Button", "LabelButton", ""), new("Button", "ImageButton", ""), new("Pane", "View", ""), new("Edit", "Textfield", ""), new("Button", "LabelButton", "")];

    private static void Banners(Action<string, bool> check)
    {
        var (kind, input) = FeishuBanners.Read(Banner("用户546325", "h1"), "k1");
        check("Feishu: a banner is the chat and its message, as a Feishu message notice", kind == FeishuWindowKind.Banner
            && input is { AppKey: FeishuBanners.AppKey, AppName: "飞书", Title: "用户546325", Body: "h1", InstanceKey: "k1", Category: NoticeCategory.Message, Call: false, Origin: NoticeOrigin.Windows });
        check("Feishu: the words are trimmed and a long message is kept whole",
            FeishuBanners.Read(Banner("  豆包工作 ", " 你好像发了「1」，是想测试一下吗？如果有需要处理的工作、问题或者想了解的内容，随时可以告诉我。 "), "k").Input is { Title: "豆包工作", Body: "你好像发了「1」，是想测试一下吗？如果有需要处理的工作、问题或者想了解的内容，随时可以告诉我。" });
        check("Feishu: with message details hidden, its banner says only that a message came",
            FeishuBanners.Read(Banner("飞书", "你收到了一条消息"), "k").Input is { Title: "飞书", Body: "你收到了一条消息" });
        check("Feishu: its notice that an update is ready is not a message", FeishuBanners.Read(Banner("8.9.2", "新版本已就绪，重新启动生效"), "k") is (FeishuWindowKind.Other, null)
            && FeishuBanners.Read(Banner("9.0", "新版本已就绪"), "k").Kind == FeishuWindowKind.Other);
        check("Feishu: a chat named with digits and words is still a message", FeishuBanners.Read(Banner("8.9.2 版本群", "已发布"), "k").Kind == FeishuWindowKind.Banner);
        check("Feishu: a banner whose words are not there yet is read again", FeishuBanners.Read(Banner("", ""), "k") is (FeishuWindowKind.NotReady, null)
            && FeishuBanners.Read(Banner("用户546325", " "), "k").Kind == FeishuWindowKind.NotReady);
        // The menu of the tray icon: a title, a button and a list, no quick reply box.
        FeishuPart[] tray = [L("消息"), new("Button", "IgnoreButtonView", "全部忽略"), new("Pane", "ScrollView", "")];
        check("Feishu: a window without the quick reply box is not a banner", FeishuBanners.Read(tray, "k") is (FeishuWindowKind.Other, null)
            && FeishuBanners.Read([], "k").Kind == FeishuWindowKind.Other
            && FeishuBanners.Read([new("Edit", "Textfield", ""), new("Text", "RichLabel", "复制成功")], "k").Kind == FeishuWindowKind.Other);
        var hint = NoticeAppWindows.Hint(FeishuBanners.StartMenuId, null);
        check("Feishu: opening it looks for a window of Feishu.exe or Lark.exe before starting its Start menu entry", WindowsNoticeList.IsAppId(FeishuBanners.StartMenuId)
            && hint.Matches(@"D:\Programs\Feishu\app\Feishu.exe") && hint.Matches(@"C:\Lark\Lark.exe") && !hint.Matches(@"D:\Programs\Feishu\app\file_search_host.exe"));
        check("Feishu: a banner's, a flash's and a DingTalk popup's notice is never called closed at the source; a Windows notification's can be",
            NoticeKeys.NeverClosed("feishu:msg:3:638999") && NoticeKeys.NeverClosed("flash:feishu:ab12cd34:2") && NoticeKeys.NeverClosed("dingtalk:msg:1:638999")
            && !NoticeKeys.NeverClosed("dingtalk:call:1:638999") && !NoticeKeys.NeverClosed("windows:QQ:5") && !NoticeKeys.NeverClosed("") && !NoticeKeys.NeverClosed(null));
        check("Feishu: only Feishu.exe and Lark.exe are Feishu", FeishuBanners.IsProgram("Feishu.exe") && FeishuBanners.IsProgram("LARK.EXE")
            && !FeishuBanners.IsProgram("FeishuUpdate.exe") && !FeishuBanners.IsProgram("") && !FeishuBanners.IsProgram(null));
    }

    // Where and how large a window is asked: the banner is 360 by 80, 12 pixels in from the work area's corner; the tray icon's
    // menu (368 by 120) sits 95 pixels from the right; the main window is far larger.
    private static void Placement(Action<string, bool> check)
    {
        check("Feishu: a banner's size is asked at 100 % and at 150 % alike", FeishuBanners.MaySize(360, 80) && FeishuBanners.MaySize(540 / 1.5, 120 / 1.5) && FeishuBanners.MaySize(368, 120));
        check("Feishu: the main window and a narrow list are not", !FeishuBanners.MaySize(1316, 1197) && !FeishuBanners.MaySize(192, 388) && !FeishuBanners.MaySize(360, 30));
        check("Feishu: a banner in the corner is hidden before it is read; the tray icon's menu is not", FeishuBanners.InCorner(12, 12) && FeishuBanners.InCorner(0, 0)
            && !FeishuBanners.InCorner(95, 134) && !FeishuBanners.InCorner(1600, 600));
    }

    private static void Flash(Action<string, bool> check)
    {
        check("Feishu: its taskbar flash is on", ChatFlashApps.All.Single(app => app.Id == "feishu") is { Enabled: true, QuietGap: null, RestoreUri: null });
        var now = T0;
        var filter = new ChatFlashFilter(_ => @"D:\Programs\Feishu\app\Feishu.exe", _ => false, () => now);
        var first = filter.OnFlash(1);
        check("Feishu: its flashes are Feishu's notices", first is { AppKey: FeishuBanners.AppKey, Title: "飞书", Body: "有新消息" });
        // Measured: five flashes within 3.7 seconds for one message, then still.
        bool one = true;
        foreach (double at in new[] { 1.06, 2.12, 3.19, 3.72 }) { now = T0.AddSeconds(at); one &= filter.OnFlash(1)!.InstanceKey == first!.InstanceKey; }
        check("Feishu: the flashes of one message are one notice", one);
        now = T0.AddSeconds(15);
        check("Feishu: the next message's flashes are another notice", filter.OnFlash(1)!.InstanceKey != first!.InstanceKey);
        check("Feishu: no flash is told while the user is in Feishu", new ChatFlashFilter(_ => "Feishu.exe", _ => true, () => T0).OnFlash(1) is null);
    }

    // The banner watch sees every window that shows, and opening an application, finding the player's or a process's window and
    // listing the running programs look at every window there is: a call that waits for a window's own answer hangs BrimDeck on
    // a suspended app's window (the Settings app's did, 2026-11-26, through GetWindowTextLength on the UI thread). Their titles
    // are read from what Windows keeps, and the banner is hidden by a posted ShowWindowAsync. Bringing the chosen window forward
    // may still restore that one window (ShowWindow), the application's own.
    private static void NoWaiting(Action<string, bool> check)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BrimDeck.sln"))) dir = dir.Parent;
        if (dir is null) { check("The source tree is found for the window calls", false); return; }
        // The code only: the comments name the calls that are not used.
        string Code(string name) => string.Join('\n', File.ReadAllText(Path.Combine(dir.FullName, "src", "BrimDeck", "Native", name)).Split('\n').Select(line => line.Split("//")[0]));
        var waits = new System.Text.RegularExpressions.Regex(@"\bGetWindowTextLength\b|\bGetWindowText\b|\bSendMessage\b");
        foreach (var file in new[] { "FeishuWatch.cs", "AppWindowSystem.cs", "MediaAppWindows.cs", "ProcessWindows.cs", "NoticeSenderIds.cs", "RunningApps.cs" })
            check($"Window calls: {file} reads no window by asking it and waiting", !waits.IsMatch(Code(file)));
        check("Window calls: the banner is hidden and shown again without waiting for Feishu", !System.Text.RegularExpressions.Regex.IsMatch(Code("FeishuWatch.cs"), @"\bShowWindow\("));
    }

    private static void Setting(Action<string, bool> check)
    {
        var defaults = new DeckSettings();
        defaults.Normalize();
        check("Feishu: listed for a new user, by its taskbar flash", defaults.NoticeRule(FeishuBanners.AppKey) is { ReadContent: false, Enabled: true } && defaults.NoticeReceives(FeishuBanners.AppKey));
    }
}
