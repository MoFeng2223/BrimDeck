namespace BrimDeck.Core.Notices;

// One text of a DingTalk window as UI Automation gives it: the Qt class of the label and its words, in the window's order.
public readonly record struct DingTalkText(string Class, string Name);

// DingTalk sends no Windows notifications: it shows windows of its own (read 2026-11-26 on DingTalk 8.x, Windows 11, Chinese
// interface), and the "读取消息内容" choice of its row on the "应用" page reads them (DingTalkWatch). The classes named here are
// the ones UI Automation gives (Qt's class names); Windows' own class of each is Qt's generic one. They are windows of their
// own, so they are read whatever page DingTalk's main window shows, minimized or in the tray:
// - A message, and a call that was not answered: DingTalk.exe's window of class MessageClass in the corner of the screen, shown
//   for about four seconds each time and then hidden again. Its two ddesign::Label texts are the chat and the message
//   ("余昊,余昊" and "[有人@你]余昊: @余昊 "), or "钉钉电话" and "未接来电：余昊、余昊". DingTalk shows it only for one-on-one chats,
//   mentions, people followed and red envelopes, and only while "消息桌面通知" is on; with "隐藏消息详情" it says "你收到了一条消息".
// - A call ringing: a window of plugins\tblive\...\tblive.exe, a new process for each call. A one-on-one call is CallClass,
//   titled "语音通话", with the caller's name and "邀请你语音通话"; a meeting is MeetingClass, titled "钉钉视频会议" or "钉钉语音会议",
//   with "余昊 邀请你加入" and the meeting's name. Both show only while "显示通话/会议邀请通知" is on, and are gone once the call
//   is answered, declined or given up. The texts of their buttons (接听, 挂断, 拒绝, 麦克风已开) come after the others.
public static class DingTalkPopups
{
    public const string AppKey = "flash:dingtalk";
    public const string MessageClass = "desktop_msg_notification::DesktopMsgNotificationView";
    public const string CallClass = "qui::VoIPNew";
    public const string MeetingClass = "ConfNewRecievePrepareViewV2";
    public const string MainProgram = "DingTalk.exe", CallProgram = "tblive.exe";
    private const string LabelClass = "ddesign::Label", MeetingLabelClass = "dtguitoolkit::AutoAdjustLabel";
    private static readonly string[] InviteMarks = ["邀请你", "邀请您", "invite"];
    private static readonly string[] Buttons = ["接听", "挂断", "拒绝", "通话纪要", "麦克风已开", "麦克风已关", "摄像头已开", "摄像头已关", "Answer", "Decline", "Hang up"];

    // Whether a window of this program and class is one of DingTalk's that is read; the program is a file name.
    public static bool Watched(string? program, string? className) => program is not null && className is not null
        && (string.Equals(program, MainProgram, StringComparison.OrdinalIgnoreCase) && className == MessageClass
            || string.Equals(program, CallProgram, StringComparison.OrdinalIgnoreCase) && className is CallClass or MeetingClass);

    // The notice a shown window says; null when its texts are not as expected (DingTalk changed them, or the window was empty).
    public static NoticeInput? Read(string className, string windowTitle, IReadOnlyList<DingTalkText> texts, string instanceKey)
    {
        var words = texts.Select(text => text with { Name = (text.Name ?? "").Trim() }).Where(text => text.Name.Length > 0).ToList();
        return className switch
        {
            MessageClass => Message(words, instanceKey),
            CallClass => Call(windowTitle, words, instanceKey),
            MeetingClass => Meeting(windowTitle, words, instanceKey),
            _ => null
        };
    }

    private static NoticeInput? Message(List<DingTalkText> words, string instanceKey)
    {
        var labels = words.Where(text => text.Class == LabelClass).Select(text => text.Name).ToList();
        if (labels.Count < 2) return null;
        return Input(instanceKey, labels[0], string.Join("\n", labels.Skip(1)), call: false);
    }

    // "余昊", "邀请你语音通话": the caller is the text before the invitation.
    private static NoticeInput? Call(string windowTitle, List<DingTalkText> words, string instanceKey)
    {
        var names = words.Select(text => text.Name).Where(name => !Buttons.Contains(name) && name != windowTitle).ToList();
        int invite = names.FindIndex(IsInvite);
        if (invite < 0) return null;
        string caller = invite > 0 ? names[invite - 1] : windowTitle;
        return Input(instanceKey, caller, names[invite], call: true);
    }

    // "余昊 邀请你加入", "余昊", "余昊发起的视频会议_1126": who invites, then the meeting's name, last of the labels.
    private static NoticeInput? Meeting(string windowTitle, List<DingTalkText> words, string instanceKey)
    {
        var labels = words.Where(text => text.Class == MeetingLabelClass).Select(text => text.Name).ToList();
        int invite = labels.FindIndex(IsInvite);
        if (invite < 0) return null;
        string line = labels[invite];
        int mark = InviteMarks.Select(m => line.IndexOf(m, StringComparison.OrdinalIgnoreCase)).Where(at => at > 0).DefaultIfEmpty(-1).Min();
        string inviter = mark > 0 ? line[..mark].Trim() : "";
        string meeting = labels.Count - 1 > invite ? labels[^1] : "";
        string kind = windowTitle.Trim();
        string body = meeting.Length > 0 && kind.Length > 0 ? kind + " · " + meeting : meeting.Length > 0 ? meeting : kind.Length > 0 ? kind : line;
        return Input(instanceKey, inviter.Length > 0 ? inviter : line, body, call: true);
    }

    private static bool IsInvite(string text) => InviteMarks.Any(mark => text.Contains(mark, StringComparison.OrdinalIgnoreCase));

    private static NoticeInput Input(string instanceKey, string title, string body, bool call) => new()
    {
        Origin = NoticeOrigin.Windows, AppKey = AppKey, AppName = Loc.T("钉钉", "DingTalk"), InstanceKey = instanceKey,
        Title = title, Body = body, Category = NoticeCategory.Message, Call = call
    };
}
