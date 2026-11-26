using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// WeChat's two ways of being noticed, under its opened row on the "应用" page. Taskbar flash: its notices are the flash of its
// taskbar button alone. Read message content (after the user confirmed what it does to WeChat): its chat list is read, and the
// choice says how the reading goes (WeChatNote); the row's own line says it in short (WeChatShort).
public sealed partial class SettingsWindow
{
    private FrameworkElement WeChatModes(NoticeCenter center, NoticeAppRule? rule, string name)
    {
        bool reading = rule?.ReadContent == true;
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
        var heading = TextLine(Loc.T("提示方式", "How it is noticed"), 12, TextSecondary); heading.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(heading);
        var flash = ModeChoice("WeChatMode", Loc.T("任务栏闪烁", "Taskbar flash"), Loc.T("微信图标闪烁时提示“有新消息”，不读取消息内容。", "\"New message\" when WeChat's taskbar button flashes; no message content is read."), null, null);
        var read = ModeChoice("WeChatMode", Loc.T("读取消息内容", "Read message content"),
            Loc.T("显示会话名和消息，同一会话合并，可以在通知页直接回复。微信窗口可以最小化，但不能关到托盘。",
                "Shows the chat's name and the message, keeps a chat's messages together, and lets you reply on the notices page. WeChat's window may be minimized but not closed to the tray."),
            Loc.T("只能读到聊天列表中当前显示的会话，每个会话只有最新一条。微信关到托盘时读不到内容，只提示“有新消息”。",
                "Only chats in view in the chat list are read, and only the latest message of each. While WeChat is closed to the tray nothing can be read, and only \"New message\" is shown."),
            reading ? DotLine(() => WeChatNote(center) is var (text, problem) && text.Length > 0
                ? (problem ? NoteDot.Problem : NoteDot.Good, text, null, null) : (NoteDot.None, "", null, null), top: 5) : null);
        flash.IsChecked = !reading; read.IsChecked = reading;
        flash.Checked += (_, _) => { EditRule(WeChatRows.AppKey, name, r => r.ReadContent = false); ShowPage(_page); };
        read.Checked += (_, _) =>
        {
            if (!Confirm(Loc.T("读取微信的消息内容？", "Read WeChat's message content?"),
                Loc.T("BrimDeck 会修改运行中的微信的一处内存数据，让它开放界面信息，再从聊天列表读取消息。微信不支持这种用法：它可能使微信不稳定、在微信更新后失效，或导致账号受到限制。改回“任务栏闪烁”或退出 BrimDeck 时会改回原样。",
                    "BrimDeck changes one value in the memory of the running WeChat so that it exposes its interface, then reads messages from the chat list. WeChat does not support this: it may make WeChat unstable, stop working after a WeChat update, or get the account restricted. Going back to Taskbar flash or exiting BrimDeck puts the value back."),
                Loc.T("开启", "Turn on")))
            { ShowPage(_page); return; }
            EditRule(WeChatRows.AppKey, name, r => r.ReadContent = true);
            ShowPage(_page);
        };
        panel.Children.Add(flash); panel.Children.Add(read);
        return new Border { Child = panel, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    // One way of a chat's being noticed (WeChat's, DingTalk's): a round choice with its name, what it does, behind the info mark
    // what it cannot do, and how it goes.
    private RadioButton ModeChoice(string group, string title, string description, string? help, FrameworkElement? status)
    {
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        titleLine.Children.Add(TextLine(title, 13, TextPrimary));
        if (help is not null) { var info = HelpIcon(title, () => help, 12); info.Margin = new Thickness(6, 0, 0, 0); info.VerticalAlignment = VerticalAlignment.Center; titleLine.Children.Add(info); }
        var text = TextLine(description, 11.5, TextSecondary); text.TextWrapping = TextWrapping.Wrap; text.TextTrimming = TextTrimming.None; text.Margin = new Thickness(0, 2, 0, 0);
        var content = new StackPanel(); content.Children.Add(titleLine); content.Children.Add(text);
        if (status is not null) content.Children.Add(status);
        var choice = new RadioButton { Style = (Style)FindResource("SettingsRadio"), Content = content, GroupName = group, Margin = new Thickness(0, 6, 0, 6) };
        AutomationProperties.SetName(choice, title);
        AutomationProperties.SetHelpText(choice, description);
        return choice;
    }

    // How the reading of WeChat's chat list goes, in full, under the choice.
    private static (string, bool) WeChatNote(NoticeCenter center) => center.WeChatState switch
    {
        WeChatState.Reading => (Loc.T("正在读取聊天列表", "Reading the chat list"), false),
        WeChatState.NoWeChat => (Loc.T("微信未运行或未登录，登录后自动开始读取", "WeChat is not running or not signed in; reading starts once it is"), false),
        WeChatState.InTray => (Loc.T("微信已关到托盘，读不到内容，现在只提示“有新消息”。请打开微信窗口，之后用最小化代替关闭", "WeChat is closed to the tray and cannot be read, so only \"New message\" is shown. Open its window, then minimize it instead of closing"), true),
        WeChatState.NoList => (Loc.T("微信不在聊天页，读不到聊天列表，现在只提示“有新消息”", "WeChat is not on its chats page, so the chat list cannot be read and only \"New message\" is shown"), true),
        WeChatState.Refused => (center.WeChatRefusal switch
        {
            WeChatGateResult.Unsupported => Loc.T("不支持这个版本的微信，仍按任务栏闪烁提示", "This version of WeChat is not supported; the taskbar flash is used"),
            WeChatGateResult.Denied => Loc.T("无法访问微信进程（微信可能以管理员身份运行），仍按任务栏闪烁提示", "WeChat's process cannot be reached (it may run as administrator); the taskbar flash is used"),
            _ => Loc.T("未能让微信开放界面信息，仍按任务栏闪烁提示", "WeChat did not expose its interface; the taskbar flash is used")
        }, true),
        _ => ("", false)
    };

    // The same in short, for WeChat's row while it reads message content.
    private (NoteDot, string, string?, Action?) WeChatShort(NoticeCenter center)
    {
        string mode = Loc.T("读取消息内容 · ", "Read message content · ");
        return center.WeChatState switch
        {
            WeChatState.Reading => (NoteDot.Good, mode + Loc.T("正在读取", "reading"), null, null),
            WeChatState.NoWeChat => (NoteDot.Waiting, mode + Loc.T("微信未运行", "WeChat is not running"), null, null),
            WeChatState.InTray => (NoteDot.Problem, mode + Loc.T("微信已关到托盘", "WeChat is in the tray"), null, null),
            WeChatState.NoList => (NoteDot.Problem, mode + Loc.T("不在聊天页", "not on the chats page"), null, null),
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
