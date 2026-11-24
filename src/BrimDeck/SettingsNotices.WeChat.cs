using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// WeChat's own choice on the "应用" page, under its row: "读取消息内容". Off, WeChat's notices are the flash of its taskbar
// button alone. On (after the user confirmed what it does to WeChat), its chat list is read; the row's status line then says
// how the reading goes (WeChatNote).
public sealed partial class SettingsWindow
{
    private FrameworkElement WeChatOptions(NoticeAppRule? rule, string name)
    {
        var row = Toggle(Loc.T("读取消息内容", "Read message content"), rule?.ReadContent == true, value =>
        {
            if (value && !Confirm(Loc.T("读取微信的消息内容？", "Read WeChat's message content?"),
                Loc.T("BrimDeck 会修改运行中的微信的一处内存数据，让它开放界面信息，再从聊天列表读取消息。微信不支持这种用法：它可能使微信不稳定、在微信更新后失效，或导致账号受到限制。关闭此项或退出 BrimDeck 时会改回原样。",
                    "BrimDeck changes one value in the memory of the running WeChat so that it exposes its interface, then reads messages from the chat list. WeChat does not support this: it may make WeChat unstable, stop working after a WeChat update, or get the account restricted. Turning this off or exiting BrimDeck puts the value back."),
                Loc.T("开启", "Turn on")))
            { ShowPage(_page); return; }
            EditRule(WeChatRows.AppKey, name, r => r.ReadContent = value);
            ShowPage(_page);
        }, help: Loc.T("显示会话名和消息内容，同一会话的消息合并在一起，可以在通知页直接回复。微信窗口可以最小化，但不要关到托盘，否则读不到内容，只提示“有新消息”。只能读到聊天列表中当前显示的会话，每个会话只有最新一条。",
            "Shows the chat's name and the message, keeps a chat's messages together, and lets you reply on the notices page. WeChat's window may be minimized but not closed to the tray: there nothing can be read and only \"New message\" is shown. Only chats in view in the chat list are read, and only the latest message of each."));
        return new StackPanel { Margin = new Thickness(32, 0, 0, 4), Children = { row } };
    }

    // How the reading of WeChat's chat list goes, for the row's status line.
    private static (string, bool) WeChatNote(NoticeCenter center) => center.WeChatState switch
    {
        WeChatState.Reading => (Loc.T("正在读取聊天列表", "Reading the chat list"), false),
        WeChatState.NoWeChat => (Loc.T("微信未运行或未登录，登录后自动开始读取", "WeChat is not running or not signed in; reading starts once it is"), false),
        WeChatState.InTray => (Loc.T("微信已关到托盘，读不到内容。请打开微信窗口，之后用最小化代替关闭", "WeChat is closed to the tray and cannot be read. Open its window, then minimize it instead of closing"), true),
        WeChatState.NoList => (Loc.T("微信不在聊天页，读不到聊天列表", "WeChat is not on its chats page, so the chat list cannot be read"), true),
        WeChatState.Refused => (center.WeChatRefusal switch
        {
            WeChatGateResult.Unsupported => Loc.T("不支持这个版本的微信，仍按任务栏闪烁提示", "This version of WeChat is not supported; the taskbar flash is used"),
            WeChatGateResult.Denied => Loc.T("无法访问微信进程（微信可能以管理员身份运行），仍按任务栏闪烁提示", "WeChat's process cannot be reached (it may run as administrator); the taskbar flash is used"),
            _ => Loc.T("未能让微信开放界面信息，仍按任务栏闪烁提示", "WeChat did not expose its interface; the taskbar flash is used")
        }, true),
        _ => ("", false)
    };
}
