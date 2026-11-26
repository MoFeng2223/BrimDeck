using BrimDeck.Core;

namespace BrimDeck;

// Asks before a chat app's messages are read instead of its taskbar flash only (the "读取消息内容" choice on the "应用" page):
// what is then shown, what is done to the app in two lines, and what may go wrong, in the window the players'
// full control asks in (ControlDialog).
internal sealed class ChatReadWindow : ControlDialog
{
    private ChatReadWindow(SettingsTheme theme, string title) : base(theme, title) { }

    // DingTalk: its own store on this PC is read (DingTalkStore); DingTalk itself is not touched.
    public static ChatReadWindow DingTalk(SettingsTheme theme)
    {
        var window = new ChatReadWindow(theme, Loc.T("读取钉钉消息内容", "Read DingTalk's message content"));
        window.Add(Loc.T("开启后，岛上会显示钉钉消息的会话名、发送人和内容；有来电时，岛上会以来电的方式显示。",
            "When on, the island shows DingTalk messages with the chat's name and the sender, and shows incoming calls as calls."), 10);
        window.Add(Loc.T("•  读取钉钉保存在这台电脑上的消息记录", "•  The message records DingTalk keeps on this PC are read"), 10, color: window.Palette.Primary);
        window.Add(Loc.T("•  不修改钉钉，也不改变钉钉的设置", "•  DingTalk and its settings are not changed"), 4, color: window.Palette.Primary);
        var notice = window.AddNotice(Loc.T("开启前请了解", "Before turning it on"), warning: true);
        window.Add(Loc.T("钉钉版本升级后可能无法读取，届时改为任务栏闪烁。使用单位的钉钉账号时，请先确认单位的规定允许这样做。",
            "A DingTalk upgrade may stop this working; the taskbar flash is then used. With an organization's DingTalk account, first check that its rules allow this."), 6, into: notice);
        window.Finish(Loc.T("开启", "Turn on"));
        return window;
    }

    // WeChat: one value in the running WeChat's memory is changed so that its chat list can be read (WeChatRows).
    public static ChatReadWindow WeChat(SettingsTheme theme)
    {
        var window = new ChatReadWindow(theme, Loc.T("读取微信消息内容", "Read WeChat's message content"));
        window.Add(Loc.T("开启后，岛上会显示微信消息的会话名和内容，可以在通知页回复。",
            "When on, the island shows WeChat messages with the chat's name, and you can reply on the notices page."), 10);
        window.Add(Loc.T("•  会修改运行中微信的一处内存数据，不修改微信的文件", "•  One value in WeChat's memory is changed, not its files"), 10, color: window.Palette.Primary);
        window.Add(Loc.T("•  改回“任务栏闪烁”或退出 BrimDeck 时恢复原样", "•  Going back to Taskbar flash or exiting BrimDeck puts it back"), 4, color: window.Palette.Primary);
        var notice = window.AddNotice(Loc.T("开启前请了解", "Before turning it on"), warning: true);
        window.Add(Loc.T("可能使微信不稳定，更新后可能失效，也有很小概率导致账号受限。",
            "It may make WeChat unstable or stop working after a WeChat update, and in rare cases get the account restricted."), 6, into: notice);
        window.Finish(Loc.T("开启", "Turn on"));
        return window;
    }
}
