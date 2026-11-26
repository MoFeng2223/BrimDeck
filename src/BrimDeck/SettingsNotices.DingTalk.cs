using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// DingTalk's two ways of being noticed, under its opened row on the "应用" page. The taskbar flash comes with what to turn on in
// DingTalk, in DingTalk's own words (its interface text, i18n\zh_CN.json and en.json; its settings are reached from the avatar
// menu); reading message content needs nothing turned on.
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
            Loc.T("钉钉图标闪烁时提示“有新消息”，无具体消息内容。", "\"New message\" when DingTalk's taskbar button flashes, without the message's content."), null);
        var read = ModeChoice("DingTalkMode", Loc.T("读取消息内容", "Read message content"),
            Loc.T("显示会话名、发送人和消息内容。", "Shows the chat's name, the sender and the message."),
            Loc.T("同一会话在极短时间内连发多条消息时，只显示最新一条。钉钉版本升级后可能无法读取，届时改为任务栏闪烁。",
                "When one chat receives several messages in quick succession, only the newest shows. A DingTalk upgrade may stop this working; the taskbar flash is then used."));
        flash.IsChecked = !reading; read.IsChecked = reading;
        flash.Checked += (_, _) => { EditRule(DingTalkPopups.AppKey, name, r => r.ReadContent = false); ShowPage(_page); };
        read.Checked += (_, _) =>
        {
            var ask = ChatReadWindow.DingTalk(S.Theme); ask.Owner = this;
            if (ask.ShowDialog() == true) EditRule(DingTalkPopups.AppKey, name, r => r.ReadContent = true);
            ShowPage(_page);
        };
        panel.Children.Add(flash); panel.Children.Add(read);
        var stack = new StackPanel();
        stack.Children.Add(new Border { Child = panel, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0) });
        if (!reading)
            stack.Children.Add(Steps(Loc.T("在钉钉中打开任务栏图标闪动", "Turn on the taskbar icon blink in DingTalk"),
                [Loc.T("点击钉钉左上角的头像和名字，在弹出的菜单中选择“设置”。", "In DingTalk, click your avatar and name at the top left, then choose \"Settings\" in the menu."),
                 Loc.T("进入“新消息通知”，勾选“消息任务栏图标闪动”。", "Go to \"Notifications\" and select \"Message taskbar icon blinks\".")]));
        return stack;
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
