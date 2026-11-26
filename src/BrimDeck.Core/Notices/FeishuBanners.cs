using System.Text.RegularExpressions;

namespace BrimDeck.Core.Notices;

// One part of a Feishu window as UI Automation gives it, in the window's order: its control type ("Text", "Edit", "Button",
// "Pane"), its views class and its words.
public readonly record struct FeishuPart(string Kind, string Class, string Name);

public enum FeishuWindowKind { Other, NotReady, Banner }

// Feishu sends no Windows notifications: a new message shows a banner window of its own in the corner of the screen (read
// 2026-11-26 on Feishu 8.9.0.29, Windows 11), and the "读取消息内容" choice of its row on the "应用" page reads it and hides
// it, so the island shows the message in its place (FeishuWatch). The banner is a window of its own, so it is read whatever
// page Feishu's main window shows, minimized or in the tray. It is a top-level Chrome_WidgetWin_1 of Feishu.exe without a
// title, 360 by 80 at 100 %; under its RootView a WidgetDelegateView holds three Labels (the chat's name, an empty one, the
// message) and the quick reply box (a Textfield) among its buttons. Feishu shows it only while "新消息通知" and "横幅消息通知"
// are on, not for a chat set to "免打扰" and not for the chat the user is looking at; with "隐藏消息详情" it says "你收到了一条
// 消息". Feishu's notice that an update is ready is the same window with the version as its first label ("8.9.2"), and is left
// to show. The menu of the tray icon is of the same size but puts its WidgetDelegateView under a NonClientView, without a
// Textfield. Each banner is a new window; a newer one takes the place of the one before.
public static partial class FeishuBanners
{
    public const string AppKey = "flash:feishu";
    // The id of Feishu's Start menu entry: "打开应用" brings its window forward, or starts it by this entry while it is in the tray.
    public const string StartMenuId = "com.electron.Feishu";
    public static readonly string[] Programs = ["Feishu.exe", "Lark.exe"];
    private const string LabelClass = "Label", ReplyClass = "Textfield";

    [GeneratedRegex(@"^\d+(\.\d+){1,3}$")]
    private static partial Regex Version();

    public static bool IsProgram(string? fileName)
        => fileName is not null && Programs.Any(program => string.Equals(program, fileName, StringComparison.OrdinalIgnoreCase));

    // Whether a window of this size (in device-independent pixels) may be a banner, so that its parts are asked.
    public static bool MaySize(double width, double height) => width is >= 300 and <= 440 && height is >= 50 and <= 160;

    // Whether the window sits where Feishu puts its banners: in the bottom right corner of the work area of its screen (12
    // pixels in from each edge on the machine it was read on). Such a window is hidden at once, before its parts are read, so
    // the banner does not show at all; one elsewhere is read first and hidden only once it is known to be a banner.
    public static bool InCorner(double rightGap, double bottomGap) => rightGap is >= -4 and <= 60 && bottomGap is >= -4 and <= 60;

    // What the parts under the window's WidgetDelegateView say: a banner with its notice, an update notice or any other window
    // (Other), or one whose texts are not filled in yet (NotReady).
    public static (FeishuWindowKind Kind, NoticeInput? Input) Read(IReadOnlyList<FeishuPart> parts, string instanceKey)
    {
        if (!parts.Any(part => part.Kind == "Edit" && part.Class == ReplyClass) || !parts.Any(part => part.Kind == "Text" && part.Class == LabelClass))
            return (FeishuWindowKind.Other, null);
        var labels = parts.Where(part => part.Kind == "Text" && part.Class == LabelClass).Select(part => (part.Name ?? "").Trim()).Where(name => name.Length > 0).ToList();
        if (labels.Count == 0) return (FeishuWindowKind.NotReady, null);
        if (Version().IsMatch(labels[0])) return (FeishuWindowKind.Other, null);
        if (labels.Count < 2) return (FeishuWindowKind.NotReady, null);
        return (FeishuWindowKind.Banner, new NoticeInput
        {
            Origin = NoticeOrigin.Windows, AppKey = AppKey, AppName = Loc.T("飞书", "Feishu"), InstanceKey = instanceKey,
            Title = labels[0], Body = string.Join("\n", labels.Skip(1)), Category = NoticeCategory.Message
        });
    }
}
