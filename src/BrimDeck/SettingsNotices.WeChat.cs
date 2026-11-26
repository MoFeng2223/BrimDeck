using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// WeChat's two ways of being noticed, under its opened row on the "应用" page. Taskbar flash: its notices are the flash of its
// taskbar button alone. Read message content (after the user confirmed what it does to WeChat): its chat list is read, and the
// row's own line says how the reading goes (WeChatShort).
public sealed partial class SettingsWindow
{
    private FrameworkElement WeChatModes(NoticeCenter center, NoticeAppRule? rule, string name)
    {
        bool reading = rule?.ReadContent == true;
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
        var heading = TextLine(Loc.T("提示方式", "How it is noticed"), 12, TextSecondary); heading.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(heading);
        var flash = ModeChoice("WeChatMode", Loc.T("任务栏闪烁", "Taskbar flash"), Loc.T("微信图标闪烁时提示“有新消息”，不读取消息内容。", "\"New message\" when WeChat's taskbar button flashes; no message content is read."), null);
        var read = ModeChoice("WeChatMode", Loc.T("读取消息内容", "Read message content"),
            Loc.T("显示会话名和消息，同一会话合并，可以在通知页直接回复。微信窗口可以最小化，但不能关到托盘。",
                "Shows the chat's name and the message, keeps a chat's messages together, and lets you reply on the notices page. WeChat's window may be minimized but not closed to the tray."), null);
        flash.IsChecked = !reading; read.IsChecked = reading;
        flash.Checked += (_, _) => { EditRule(WeChatRows.AppKey, name, r => r.ReadContent = false); ShowPage(_page); };
        read.Checked += (_, _) =>
        {
            var ask = ChatReadWindow.WeChat(S.Theme); ask.Owner = this;
            if (ask.ShowDialog() != true) { ShowPage(_page); return; }
            EditRule(WeChatRows.AppKey, name, r => r.ReadContent = true);
            ShowPage(_page);
        };
        panel.Children.Add(flash); panel.Children.Add(read);
        return new Border { Child = panel, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    // One way of a chat's being noticed (WeChat's, DingTalk's, Feishu's): a round choice with its name, what it does, and behind
    // the info mark what it cannot do. How it goes is said on the app's own row.
    private RadioButton ModeChoice(string group, string title, string description, string? help)
    {
        // The title stays on the circle's line; its info mark is centered on the title's glyphs.
        FrameworkElement titleLine = TextLine(title, 13, TextPrimary);
        if (help is not null)
        {
            var info = HelpIcon(title, () => help, 12); info.Margin = new Thickness(6, 0, 0, 0);
            titleLine = new HelpHeading((TextBlock)titleLine, info) { LabelHeight = true, HorizontalAlignment = HorizontalAlignment.Left };
        }
        var text = TextLine(description, 11.5, TextSecondary); text.TextWrapping = TextWrapping.Wrap; text.TextTrimming = TextTrimming.None; text.Margin = new Thickness(0, 2, 0, 0);
        var content = new StackPanel(); content.Children.Add(titleLine); content.Children.Add(text);
        var choice = new RadioButton { Style = (Style)FindResource("SettingsRadio"), Content = content, GroupName = group, Margin = new Thickness(0, 6, 0, 6) };
        AutomationProperties.SetName(choice, title);
        AutomationProperties.SetHelpText(choice, description);
        return choice;
    }

    // How the reading of WeChat's chat list goes, on WeChat's row while it reads message content. Closed to the tray or off
    // its chats page, WeChat's messages still come by the taskbar flash, so that is a caution rather than a problem.
    private (NoteDot, string, string?, Action?) WeChatShort(NoticeCenter center)
    {
        string mode = Loc.T("读取消息内容 · ", "Read message content · ");
        return center.WeChatState switch
        {
            WeChatState.Reading => (NoteDot.Good, mode + Loc.T("正在读取", "reading"), null, null),
            WeChatState.NoWeChat => (NoteDot.Waiting, mode + Loc.T("微信未运行", "WeChat is not running"), null, null),
            WeChatState.InTray => (NoteDot.Caution, mode + Loc.T("微信已关到托盘", "WeChat is in the tray"), null, null),
            WeChatState.NoList => (NoteDot.Caution, mode + Loc.T("不在聊天页", "not on the chats page"), null, null),
            WeChatState.Refused => (NoteDot.Problem, mode + (center.WeChatRefusal switch
            {
                WeChatGateResult.Unsupported => Loc.T("不支持此版本", "this version is not supported"),
                WeChatGateResult.Denied => Loc.T("无法访问微信", "WeChat cannot be reached"),
                _ => Loc.T("未能开放界面信息", "the interface was not exposed")
            }), null, null),
            _ => (NoteDot.Good, Loc.T("读取消息内容", "Read message content"), null, null)
        };
    }
}
