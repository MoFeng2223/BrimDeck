namespace BrimDeck.Core.Notices;

// A chat application that tells the user about a new message only by flashing its taskbar button (desktop WeChat 4.x sends no
// Windows notification). Executables are matched by file name, case-insensitively; a row that is not Enabled is known but
// never reported, so another application is added by one row and one switch here. RestoreUri, when set, is the address the
// application's own URL scheme brings its window forward with (ChatRestore); only WeChat has one. QuietGap, when set, is how
// long the button must stay still before a flash is another message (ChatFlashFilter); without it a round lasts the filter's
// window from its first flash.
public sealed record ChatFlashApp(string Id, string NameZh, string NameEn, IReadOnlyList<string> Executables, bool Enabled, string? RestoreUri = null, TimeSpan? QuietGap = null)
{
    public string Name => Loc.T(NameZh, NameEn);
}

public static class ChatFlashApps
{
    // DingTalk flashes its button until the message is read: three flashes about 1.06 seconds apart, then still for 1.1 to
    // 3.2 seconds (measured 2026-11-26), and again. Four seconds of stillness end a message, so a flashing that goes on is one.
    public static readonly TimeSpan DingTalkQuiet = TimeSpan.FromSeconds(4);

    // WeChat and DingTalk are on (DingTalk flashes while "消息任务栏图标闪动" is on in its settings). The others are rows waiting
    // to be verified on a real machine (their executable names are not checked).
    public static readonly IReadOnlyList<ChatFlashApp> All =
    [
        new("wechat", "微信", "WeChat", ["Weixin.exe", "WeChat.exe"], true, ChatRestore.WeChatUri),
        new("wecom", "企业微信", "WeCom", ["WXWork.exe"], false),
        new("qq", "QQ", "QQ", ["QQ.exe"], false),
        new("feishu", "飞书", "Feishu", ["Feishu.exe", "Lark.exe"], false),
        new("dingtalk", "钉钉", "DingTalk", ["DingTalk.exe"], true, QuietGap: DingTalkQuiet)
    ];

    public static ChatFlashApp? Find(string? executable, IReadOnlyList<ChatFlashApp>? apps = null)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        string name = Path.GetFileName(executable.Trim());
        return (apps ?? All).FirstOrDefault(app => app.Executables.Any(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase)));
    }

    // Where an installed chat's program is, from the icons Windows' list of installed programs names for its entries
    // (Uninstall\*\DisplayIcon: "D:\Tencent\Weixin\Weixin.exe" in quotes, or D:\Tencent\QQNT\QQ.exe,0): the first that is one
    // of the application's executables, written out in full on a local drive.
    public static string? InstalledPath(ChatFlashApp app, IEnumerable<string?> displayIcons)
    {
        foreach (var icon in displayIcons)
        {
            if (string.IsNullOrWhiteSpace(icon)) continue;
            string path = icon.Trim();
            if (path.StartsWith('"')) path = path.IndexOf('"', 1) is var end and > 0 ? path[1..end] : "";
            else if (path.LastIndexOf(',') is var comma and > 0 && int.TryParse(path[(comma + 1)..], out _)) path = path[..comma];
            path = path.Trim();
            if (NoticeIntake.IsLocalWindowsPath(path) && Find(path, [app]) is not null) return path;
        }
        return null;
    }
}

// Decides what a flashing taskbar button means. The only things it is given are the flashing window and, through the two
// lookups, the file name of that window's process and whether the user is already in that application. It never sees a
// message, a sender or the application's memory or files. Flashes of one application within Window of the first of them make
// up one notice (the same instance, so the hub updates it instead of listing another); the next flash after that starts a new
// one. A window of an unknown or not enabled application, and a flash while the user is already in the application, give
// nothing. Runs on one thread (the UI thread).
public sealed class ChatFlashFilter(Func<nint, string?> executable, Func<nint, bool> inApplication, Func<DateTimeOffset> now, TimeSpan? window = null, IReadOnlyList<ChatFlashApp>? apps = null)
{
    // WeChat flashes its button for each new message, unread ones before it or not: about four flashes within two to three
    // seconds, then nothing until the next message (measured on WeChat 4.x). Flashes within five seconds of the first of a round
    // are that one message; a later flash is another message and is told again. The user chose five seconds.
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _window = window ?? DefaultWindow;
    private readonly Dictionary<string, (DateTimeOffset Start, DateTimeOffset Latest, int Group)> _groups = new(StringComparer.Ordinal);

    public NoticeInput? OnFlash(nint handle)
    {
        if (handle == 0 || ChatFlashApps.Find(executable(handle), apps) is not { Enabled: true } app) return null;
        if (inApplication(handle)) return null;
        var at = now();
        int group = 1;
        if (_groups.TryGetValue(app.Id, out var last))
        {
            // A clock that went back starts a new group rather than merging into one from the future. An application that
            // flashes until read is one message while it keeps flashing; any other, for the window from its first flash.
            bool same = app.QuietGap is { } quiet ? at >= last.Latest && at - last.Latest < quiet : at >= last.Start && at - last.Start < _window;
            if (same) { _groups[app.Id] = last with { Latest = at }; return Input(app, last.Group); }
            group = last.Group + 1;
        }
        _groups[app.Id] = (at, at, group);
        return Input(app, group);
    }

    private static NoticeInput Input(ChatFlashApp app, int group) => new()
    {
        Origin = NoticeOrigin.Windows, AppKey = "flash:" + app.Id, AppName = app.Name, InstanceKey = $"flash:{app.Id}:{group}",
        Title = app.Name, Body = Loc.T("有新消息", "New message"), Category = NoticeCategory.Message
    };
}
