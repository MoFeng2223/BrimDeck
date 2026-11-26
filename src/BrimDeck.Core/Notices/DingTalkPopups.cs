namespace BrimDeck.Core.Notices;

// One text or button of a chat program's call window as UI Automation gives it, in the window's order: the class of the label
// or button, its words, its id, and whether it is a button (CallWindowWatch).
public readonly record struct PopupText(string Class, string Name, string Id = "", bool Button = false);

// DingTalk's ringing calls, which it shows in windows of its own (read 2026-11-26 on DingTalk 8.x, Windows 11, Chinese
// interface). Its messages are not read here: DingTalk draws no popup for a group's message that does not name the user, so
// the messages are read from its own store instead (DingTalkStore).
// A call runs in plugins\tblive\bin\64bit\tblive.exe, a new process each time. A one-to-one call is a window of class
// CallClass titled "语音通话", holding the caller's name and "邀请你语音通话"; a meeting invitation is MeetingClass, titled
// "钉钉视频会议" or "钉钉语音会议", holding "<name> 邀请你加入" and the meeting's name. Both show only while "显示通话/会议邀请通知"
// is on in DingTalk, and are gone once the call is answered, declined or given up. The classes named here are the ones UI
// Automation gives (Qt's own); Windows' class for each is Qt's generic one.
public static class DingTalkPopups
{
    public const string AppKey = DingTalkStore.AppKey;
    public const string CallClass = "qui::VoIPNew";
    public const string MeetingClass = "ConfNewRecievePrepareViewV2";
    public const string CallProgram = "tblive.exe";
    private const string MeetingLabelClass = "dtguitoolkit::AutoAdjustLabel";
    private static readonly string[] InviteMarks = ["邀请你", "邀请您", "invite"];
    // The buttons of a call window that answer and that decline: a one-to-one call's say "接听" and "挂断" (given by UI
    // Automation as check boxes, each with a label of the same words beside it), a meeting invitation's "接听" and "拒绝".
    public static readonly string[] Accept = ["接听", "Answer", "Accept"], Decline = ["挂断", "拒绝", "Hang up", "Decline", "Reject"];
    private static readonly string[] Buttons = ["接听", "挂断", "拒绝", "通话纪要", "麦克风已开", "麦克风已关", "摄像头已开", "摄像头已关", "通话结束", "Answer", "Decline", "Hang up"];

    // Whether a window of this program and class is one of DingTalk's call windows; the program is a file name.
    public static bool Watched(string? program, string? className) => program is not null && className is not null
        && string.Equals(program, CallProgram, StringComparison.OrdinalIgnoreCase) && className is CallClass or MeetingClass;

    // The notice a shown call window says; null when its texts are not as expected (DingTalk changed them, or it is empty).
    public static NoticeInput? Read(string className, string windowTitle, IReadOnlyList<PopupText> texts, string instanceKey)
    {
        var words = texts.Where(text => !text.Button).Select(text => text with { Name = (text.Name ?? "").Trim() }).Where(text => text.Name.Length > 0).ToList();
        return className switch
        {
            CallClass => Call(windowTitle, words, instanceKey),
            MeetingClass => Meeting(windowTitle, words, instanceKey),
            _ => null
        };
    }

    // "余昊", "邀请你语音通话": the caller is the text before the invitation.
    private static NoticeInput? Call(string windowTitle, List<PopupText> words, string instanceKey)
    {
        var names = words.Select(text => text.Name).Where(name => !Buttons.Contains(name) && name != windowTitle).ToList();
        int invite = names.FindIndex(IsInvite);
        if (invite < 0) return null;
        return Input(instanceKey, invite > 0 ? names[invite - 1] : windowTitle, names[invite]);
    }

    // "余昊 邀请你加入", "余昊", "余昊发起的视频会议_1126": who invites, then the meeting's name, last of the labels.
    private static NoticeInput? Meeting(string windowTitle, List<PopupText> words, string instanceKey)
    {
        var labels = words.Where(text => text.Class == MeetingLabelClass).Select(text => text.Name).ToList();
        int invite = labels.FindIndex(IsInvite);
        if (invite < 0) return null;
        string line = labels[invite];
        int mark = InviteMarks.Select(found => line.IndexOf(found, StringComparison.OrdinalIgnoreCase)).Where(at => at > 0).DefaultIfEmpty(-1).Min();
        string inviter = mark > 0 ? line[..mark].Trim() : "";
        string meeting = labels.Count - 1 > invite ? labels[^1] : "";
        string kind = windowTitle.Trim();
        string body = meeting.Length > 0 && kind.Length > 0 ? kind + " · " + meeting : meeting.Length > 0 ? meeting : kind.Length > 0 ? kind : line;
        return Input(instanceKey, inviter.Length > 0 ? inviter : line, body);
    }

    private static bool IsInvite(string text) => InviteMarks.Any(mark => text.Contains(mark, StringComparison.OrdinalIgnoreCase));

    private static NoticeInput Input(string instanceKey, string title, string body) => new()
    {
        Origin = NoticeOrigin.Windows, AppKey = AppKey, AppName = Loc.T("钉钉", "DingTalk"), InstanceKey = instanceKey,
        Title = title, Body = body, Category = NoticeCategory.Message, Call = true
    };
}
