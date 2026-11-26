using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// DingTalk's two ways of being noticed, under its opened row on the "应用" page, each with what to turn on in DingTalk, in
// DingTalk's own words (its interface text, i18n\zh_CN.json and en.json; its settings are reached from the avatar menu).
// Taskbar flash: the flash of its taskbar button alone. Read message content: the popups and call windows DingTalk shows are
// read (DingTalkWatch); a group's message that does not mention the user still only flashes, so the flash stays on as well.
public sealed partial class SettingsWindow
{
    private FrameworkElement DingTalkModes(NoticeCenter center, NoticeAppRule? rule, string name)
    {
        bool reading = rule?.ReadContent == true;
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
        var heading = TextLine(Loc.T("提示方式", "How it is noticed"), 12, TextSecondary); heading.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(heading);
        var flash = ModeChoice("DingTalkMode", Loc.T("任务栏闪烁", "Taskbar flash"),
            Loc.T("钉钉图标闪烁时提示“有新消息”，无具体消息内容。", "\"New message\" when DingTalk's taskbar button flashes, without the message's content."), null, null);
        var read = ModeChoice("DingTalkMode", Loc.T("读取消息内容", "Read message content"),
            Loc.T("显示会话名、发送人和消息内容，群里没有 @ 你的消息也显示；来电显示打来的人。不修改钉钉，也不需要钉钉弹出提示。",
                "Shows the chat's name, the sender and the message, a group's messages that do not mention you included, and who is calling. DingTalk is not changed and need not show its own popups."),
            Loc.T("从钉钉在本机留下的记录中读取每个会话的最新一条，不读取聊天记录。同一会话在极短时间内连发多条时，只显示最新一条。钉钉未运行时收不到新消息。钉钉更新后可能读不到，那时自动改回任务栏闪烁。",
                "The newest message of each chat is read from what DingTalk keeps on this PC; the chat history is not read. Of several messages sent to one chat in quick succession, only the newest shows. Nothing arrives while DingTalk is not running. A DingTalk update may stop this working, and the taskbar flash is then used instead."),
            null);
        flash.IsChecked = !reading; read.IsChecked = reading;
        flash.Checked += (_, _) => { EditRule(DingTalkPopups.AppKey, name, r => r.ReadContent = false); ShowPage(_page); };
        read.Checked += (_, _) => { EditRule(DingTalkPopups.AppKey, name, r => r.ReadContent = true); ShowPage(_page); };
        panel.Children.Add(flash); panel.Children.Add(read);
        var stack = new StackPanel();
        stack.Children.Add(new Border { Child = panel, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0) });
        var (title, steps) = DingTalkSteps(reading);
        stack.Children.Add(Steps(title, steps));
        return stack;
    }

    private static (string Heading, string[] Steps) DingTalkSteps(bool reading)
    {
        string open = Loc.T("点击钉钉左上角的头像和名字，在弹出的菜单中选择“设置”。", "In DingTalk, click your avatar and name at the top left, then choose \"Settings\" in the menu.");
        if (!reading)
            return (Loc.T("在钉钉中打开任务栏图标闪动", "Turn on the taskbar icon blink in DingTalk"),
                [open, Loc.T("进入“新消息通知”，勾选“消息任务栏图标闪动”。", "Go to \"Notifications\" and select \"Message taskbar icon blinks\".")]);
        return (Loc.T("在钉钉中打开来电通知", "Turn on call notifications in DingTalk"),
            [open,
             Loc.T("进入“新消息通知”，在“通话与会议通知”中勾选“显示通话/会议邀请通知”。关闭时钉钉不再接收来电，岛上也看不到。",
                 "Go to \"Notifications\" and, under \"Calls & Meetings Notifications\", select \"Display call/meeting invitation notifications\". With it off, DingTalk no longer receives calls at all, so none can show."),
             Loc.T("消息不需要其他设置：钉钉的“消息桌面通知”关着也能读到。",
                 "Messages need nothing else: they are read whether or not DingTalk's own message popups are on.")]);
    }

    // How DingTalk goes, under its name. show opens the row (null while it is open).
    private (NoteDot, string, string?, Action?) DingTalkShort(NoticeCenter center, Action? show)
    {
        if (center.ChatFlashError is { } error) return (NoteDot.Problem, Loc.T("无法监视任务栏：", "Could not watch the taskbar: ") + error, null, null);
        bool reading = S.NoticeRule(DingTalkStore.AppKey)?.ReadContent == true;
        if (reading && DingTalkNote(center) is { } note) return note;
        string mode = reading ? Loc.T("读取消息内容 · ", "Read message content · ") : Loc.T("任务栏闪烁 · ", "Taskbar flash · ");
        if (center.Hub.KnownApps.FirstOrDefault(known => known.Key == DingTalkStore.AppKey) is { } seen)
            return (NoteDot.Good, mode + Loc.T("正在接收 · ", "receiving · ") + NoticeText.Relative(seen.LastSeen, DateTimeOffset.UtcNow), null, null);
        return (NoteDot.Waiting, mode + Loc.T("还没有收到通知", "no notice yet"), show is null ? null : Loc.T("查看开启方法", "How to turn it on"), show);
    }

    // What stands in the way of reading DingTalk's messages; null while nothing does.
    private static (NoteDot, string, string?, Action?)? DingTalkNote(NoticeCenter center) => center.DingTalkState switch
    {
        DingTalkState.NoDingTalk => (NoteDot.Waiting, Loc.T("读取消息内容 · 钉钉未安装或未登录", "Read message content · DingTalk is not installed or not signed in"), null, null),
        DingTalkState.NoKey => (NoteDot.Problem, Loc.T("读取消息内容 · 读不到钉钉的账号信息，仍按任务栏闪烁提示", "Read message content · DingTalk's account cannot be read; the taskbar flash is used"), null, null),
        DingTalkState.Unreadable => (NoteDot.Problem, Loc.T("读取消息内容 · 读不到钉钉的消息，可能是钉钉更新了，仍按任务栏闪烁提示", "Read message content · DingTalk's messages cannot be read, perhaps after a DingTalk update; the taskbar flash is used"), null, null),
        _ => center.DingTalkCalls ? null : (NoteDot.Problem, Loc.T("读取消息内容 · 读不到来电窗口", "Read message content · call windows cannot be read"), null, null)
    };
}
