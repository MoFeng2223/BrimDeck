using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

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
            Loc.T("钉钉图标闪烁时提示“有新消息”，不读取消息内容。", "\"New message\" when DingTalk's taskbar button flashes; no message content is read."), null, null);
        var read = ModeChoice("DingTalkMode", Loc.T("读取消息内容", "Read message content"),
            Loc.T("读取钉钉弹出的消息提示和来电窗口，显示会话名、消息和来电的人。不修改钉钉。",
                "Reads the message popups and call windows DingTalk shows, for the chat's name, the message and who calls. DingTalk is not changed."),
            Loc.T("钉钉只为单聊、@我、特别关注人和红包弹出提示，群里的其他消息仍按任务栏闪烁提示“有新消息”。钉钉选择“隐藏消息详情”时读不到消息内容。",
                "DingTalk shows popups only for one-on-one chats, mentions, people you follow and red envelopes; a group's other messages are still noticed by the taskbar flash. With \"Hide message details\" chosen in DingTalk, no message content can be read."),
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
        return (Loc.T("在钉钉中打开桌面通知", "Turn on desktop notifications in DingTalk"),
            [open,
             Loc.T("进入“新消息通知”，勾选“消息桌面通知”并选择“显示消息详情”，再勾选“消息任务栏图标闪动”。",
                 "Go to \"Notifications\", select \"Message Desktop Notifications\" and choose \"Show message details\", then select \"Message taskbar icon blinks\"."),
             Loc.T("需要来电提示时，在同一页的“通话与会议通知”中勾选“显示通话/会议邀请通知”。",
                 "For calls, on the same page under \"Calls & Meetings Notifications\", select \"Display call/meeting invitation notifications\".")]);
    }

    // How DingTalk goes, under its name. show opens the row (null while it is open).
    private (NoteDot, string, string?, Action?) DingTalkShort(NoticeCenter center, Action? show)
    {
        if (center.ChatFlashError is { } error) return (NoteDot.Problem, Loc.T("无法监视任务栏：", "Could not watch the taskbar: ") + error, null, null);
        if (center.DingTalkReading == false) return (NoteDot.Problem, Loc.T("无法读取钉钉的提示窗口，仍按任务栏闪烁提示", "DingTalk's popups cannot be read; the taskbar flash is used"), null, null);
        string mode = S.NoticeRule(DingTalkPopups.AppKey)?.ReadContent == true ? Loc.T("读取消息内容 · ", "Read message content · ") : Loc.T("任务栏闪烁 · ", "Taskbar flash · ");
        if (center.Hub.KnownApps.FirstOrDefault(known => known.Key == DingTalkPopups.AppKey) is { } seen)
            return (NoteDot.Good, mode + Loc.T("正在接收 · ", "receiving · ") + NoticeText.Relative(seen.LastSeen, DateTimeOffset.UtcNow), null, null);
        return (NoteDot.Waiting, mode + Loc.T("还没有收到通知", "no notice yet"), show is null ? null : Loc.T("查看开启方法", "How to turn it on"), show);
    }
}
