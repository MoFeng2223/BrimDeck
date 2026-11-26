using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// Feishu's two ways of being noticed, under its opened row on the "应用" page, each with what to turn on in Feishu, in Feishu's
// own words (its interface text in webcontent\setting.asar: "新消息通知", "横幅消息通知", "显示消息详情"; its settings are reached
// from the avatar). Taskbar flash: the flash of its taskbar button alone. Read message content: Feishu's banners are read and
// hidden (FeishuWatch); with the banner off in Feishu a message still only flashes, so the flash stays on as well.
public sealed partial class SettingsWindow
{
    private FrameworkElement FeishuModes(NoticeCenter center, NoticeAppRule? rule, string name)
    {
        bool reading = rule?.ReadContent == true;
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
        var heading = TextLine(Loc.T("提示方式", "How it is noticed"), 12, TextSecondary); heading.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(heading);
        var flash = ModeChoice("FeishuMode", Loc.T("任务栏闪烁", "Taskbar flash"),
            Loc.T("飞书图标闪烁时提示“有新消息”，无具体消息内容。", "\"New message\" when Feishu's taskbar button flashes, without the message's content."), null, null);
        var read = ModeChoice("FeishuMode", Loc.T("读取消息内容", "Read message content"),
            Loc.T("读取飞书弹出的消息横幅，显示会话名和消息，并隐藏飞书自己的横幅。",
                "Reads the message banners Feishu shows, for the chat's name and the message, and hides Feishu's own banner."),
            Loc.T("飞书不为免打扰的会话弹出横幅，也不闪烁。在飞书中关闭横幅后，仍按任务栏闪烁提示“有新消息”；选择“隐藏消息详情”时读不到消息内容。",
                "Feishu shows no banner and no flash for a muted chat. With the banner off in Feishu, messages are still noticed by the taskbar flash; with \"Hide message details\" chosen, no message content can be read."),
            null);
        flash.IsChecked = !reading; read.IsChecked = reading;
        flash.Checked += (_, _) => { EditRule(FeishuBanners.AppKey, name, r => r.ReadContent = false); ShowPage(_page); };
        read.Checked += (_, _) => { EditRule(FeishuBanners.AppKey, name, r => r.ReadContent = true); ShowPage(_page); };
        panel.Children.Add(flash); panel.Children.Add(read);
        var stack = new StackPanel();
        stack.Children.Add(new Border { Child = panel, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0) });
        var (title, steps) = FeishuSteps(reading);
        stack.Children.Add(Steps(title, steps));
        return stack;
    }

    private static (string Heading, string[] Steps) FeishuSteps(bool reading)
    {
        string open = Loc.T("单击飞书左上角的头像，选择“设置”，进入“通知”。", "In Feishu, click your avatar at the top left, choose \"Settings\", then \"Notifications\".");
        if (!reading)
            return (Loc.T("在飞书中打开新消息通知", "Turn on message notifications in Feishu"),
                [open, Loc.T("勾选“新消息通知”。", "Select \"Message notifications\".")]);
        return (Loc.T("在飞书中打开横幅消息通知", "Turn on banner notifications in Feishu"),
            [open, Loc.T("勾选“新消息通知”和“横幅消息通知”，并选择“显示消息详情”。",
                "Select \"Message notifications\" and \"Banner notification\", and choose \"Show message details\".")]);
    }

    // How Feishu goes, under its name. show opens the row (null while it is open).
    private (NoteDot, string, string?, Action?) FeishuShort(NoticeCenter center, Action? show)
    {
        if (center.ChatFlashError is { } error) return (NoteDot.Problem, Loc.T("无法监视任务栏：", "Could not watch the taskbar: ") + error, null, null);
        if (center.FeishuReading == false) return (NoteDot.Problem, Loc.T("无法读取飞书的横幅，仍按任务栏闪烁提示", "Feishu's banners cannot be read; the taskbar flash is used"), null, null);
        string mode = S.NoticeRule(FeishuBanners.AppKey)?.ReadContent == true ? Loc.T("读取消息内容 · ", "Read message content · ") : Loc.T("任务栏闪烁 · ", "Taskbar flash · ");
        if (center.Hub.KnownApps.FirstOrDefault(known => known.Key == FeishuBanners.AppKey) is { } seen)
            return (NoteDot.Good, mode + Loc.T("正在接收 · ", "receiving · ") + NoticeText.Relative(seen.LastSeen, DateTimeOffset.UtcNow), null, null);
        return (NoteDot.Waiting, mode + Loc.T("还没有收到通知", "no notice yet"), show is null ? null : Loc.T("查看开启方法", "How to turn it on"), show);
    }
}
