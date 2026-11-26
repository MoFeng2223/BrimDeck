namespace BrimDeck.Core.Notices;

// WeChat's ringing calls. WeChat writes nothing in the chat list while a call rings; it shows a small window of its own at the
// screen's corner instead (read 2026-11-26 on WeChat 4.2.0.13, Windows 11, Chinese interface). The window belongs to Weixin.exe,
// is topmost, and is of UI Automation class CallClass; it holds the caller's name (CallerId) and the invitation (TipsId:
// "邀请你语音通话" or "邀请你视频通话", followed by one to three dots that keep changing), then the buttons "拒绝" and "接听"
// ("展开" too for a video call). It is gone once the call is answered, declined or given up. Its controls can be read only while
// WeChat's accessibility is on (WeChatGate), so calls are read only with the "读取消息内容" choice.
public static class WeChatCalls
{
    public const string AppKey = WeChatRows.AppKey;
    public const string CallClass = "mmui::VOIPTrayWindow";
    public const string Program = "Weixin.exe";
    public const string CallerId = "voip_caller_name";
    public const string TipsId = "voip_caller_tips";
    // Not WeChatRows.KeyPrefix: a call is not a chat's message, and its notice offers no reply.
    public const string KeyPrefix = "wechatcall:";

    // Whether a window of this program and class is WeChat's ringing call; the program is a file name.
    public static bool Watched(string? program, string? className) => program is not null && className is not null
        && string.Equals(program, Program, StringComparison.OrdinalIgnoreCase) && className == CallClass;

    // The notice a shown call window says; null while its invitation is not written yet (or WeChat changed its controls).
    public static NoticeInput? Read(string className, string windowTitle, IReadOnlyList<PopupText> texts, string instanceKey)
    {
        if (className != CallClass) return null;
        string tips = (texts.FirstOrDefault(text => text.Id == TipsId).Name ?? "").Trim().TrimEnd('.', '。', '…', ' ');
        if (tips.Length == 0) return null;
        string caller = (texts.FirstOrDefault(text => text.Id == CallerId).Name ?? "").Trim();
        return new NoticeInput
        {
            Origin = NoticeOrigin.Windows, AppKey = AppKey, AppName = Loc.T("微信", "WeChat"), InstanceKey = instanceKey,
            Title = caller.Length > 0 ? caller : Loc.T("微信", "WeChat"), Body = tips, Category = NoticeCategory.Message, Call = true
        };
    }
}
