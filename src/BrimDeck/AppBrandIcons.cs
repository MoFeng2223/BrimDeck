using System.IO;
using BrimDeck.Core.Mail;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// The icons the settings show for an application before it has sent a notice. The common chats (WeChat, QQ, DingTalk, Feishu)
// have their official icons built in ("brand:<name>", Assets\Apps), so they show one whether installed or not; any other Windows
// application, the Clock among them, has the icon of its Start menu entry ("aumid:<id>", read by NoticeIcons). An icon its
// notices brought, or for WeChat its installed program's, comes first. The mail services' icons are built in the same way
// ("brand:mail-<id>", Assets\Mail, MailProviders.IconRef).
internal static class AppBrandIcons
{
    private static readonly Dictionary<string, string> s_byKey = new(StringComparer.Ordinal)
    {
        ["flash:wechat"] = "wechat",
        ["flash:dingtalk"] = "dingtalk",
        ["flash:feishu"] = "feishu",
        [NoticeKeys.Windows("QQ")] = "qq",
        [NoticeKeys.Windows("ali.dingtalk.release")] = "dingtalk",
        [NoticeKeys.Windows("com.electron.Feishu")] = "feishu"
    };

    // "brand:<name>" for an application with a built-in icon; null for others.
    public static string? Ref(string appKey) => s_byKey.TryGetValue(appKey, out var name) ? "brand:" + name : null;

    // The icon of an application that has not given one through its notices.
    public static string? Fallback(string appKey)
        => ChatAppIcons.Ref(appKey) ?? Ref(appKey)
           ?? (appKey.StartsWith("windows:", StringComparison.Ordinal) && appKey != NoticeKeys.UnknownWindowsApp ? "aumid:" + appKey["windows:".Length..] : null);

    // The PNG of a built-in icon; null for a name that is not one of them.
    public static byte[]? Read(string name)
    {
        string? resource = s_byKey.ContainsValue(name) ? "BrimDeck.Apps." + name + ".png"
            : MailProviders.Listed.Any(provider => "brand:" + name == MailProviders.IconRef(provider)) ? "BrimDeck.Mail." + name["mail-".Length..] + ".png" : null;
        if (resource is null) return null;
        using var stream = typeof(AppBrandIcons).Assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
