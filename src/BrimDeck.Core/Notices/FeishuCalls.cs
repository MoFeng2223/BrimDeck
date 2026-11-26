namespace BrimDeck.Core.Notices;

// Feishu's ringing calls. Feishu shows a small window of its own for a call at the screen's corner, from a process it starts for
// the call (read 2026-11-27 on Feishu 8.9.0.29, Windows 11, its interface in traditional Chinese): a top-level window of class
// Chrome_WidgetWin_0 (not the Chrome_WidgetWin_1 of its banners) titled "飛書會議", 384 by 102 at 100 %. Under its
// RingingDialog a view named "你正在被呼叫" holds the caller's name and the invitation as Labels ("用户546325", "邀請你進行視訊通話"),
// then the buttons "拒絕", "視訊接聽" and "語音接聽" for a video call; two hidden Labels say in English that a call in progress
// ends on answering. It is gone once the call is answered, declined or given up. Read only with the "读取消息内容" choice, as
// the banners are (CallWindowWatch).
public static class FeishuCalls
{
    public const string AppKey = FeishuBanners.AppKey;
    public const string WindowClass = "Chrome_WidgetWin_0";
    public const string KeyPrefix = "feishu:call:";
    // The buttons that decline, that answer as the caller asked, and that answer a video call with voice only, in simplified and
    // traditional Chinese. Only the traditional ones of a video call were seen; the others are Feishu's usual words for them.
    public static readonly string[] Decline = ["拒绝", "拒絕", "挂断", "掛斷", "Decline", "Reject"];
    public static readonly string[] Accept = ["接听", "接聽", "视频接听", "視訊接聽", "視頻接聽", "Answer", "Accept"];
    public static readonly string[] Voice = ["语音接听", "語音接聽"];
    private static readonly string[] InviteMarks = ["邀请你", "邀請你", "邀请您", "邀請您", "invite", "is calling", "calling you"];

    // Whether a window of this program and class may be Feishu's ringing call; the program is a file name.
    public static bool Watched(string? program, string? className) => FeishuBanners.IsProgram(program) && className == WindowClass;

    // The notice a shown call window says: the label before the invitation is the caller. Null while the window holds no
    // invitation or no button that declines it (Feishu's other windows of that class, or one not filled in yet).
    public static NoticeInput? Read(string className, string windowTitle, IReadOnlyList<PopupText> texts, string instanceKey)
    {
        if (className != WindowClass) return null;
        if (!texts.Any(text => text.Button && Decline.Contains(text.Name.Trim(), StringComparer.OrdinalIgnoreCase))) return null;
        var labels = texts.Where(text => !text.Button).Select(text => (text.Name ?? "").Trim()).Where(name => name.Length > 0).ToList();
        int invite = labels.FindIndex(label => InviteMarks.Any(mark => label.Contains(mark, StringComparison.OrdinalIgnoreCase)));
        if (invite < 0) return null;
        string caller = invite > 0 ? labels[invite - 1] : "";
        return new NoticeInput
        {
            Origin = NoticeOrigin.Windows, AppKey = AppKey, AppName = Loc.T("飞书", "Feishu"), InstanceKey = instanceKey,
            Title = caller.Length > 0 ? caller : Loc.T("飞书", "Feishu"), Body = labels[invite], Category = NoticeCategory.Message, Call = true
        };
    }
}
