using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// WeChat in the notices. Without the row's "读取消息内容" only the flash of its taskbar button is told ("有新消息", ChatFlash).
// With it the chat list is read (WeChatSource): each message is a notice under its chat's name, a click opens that chat, and
// the notices page can answer it. Either way a click brings WeChat's window forward through WeChat's own address (ChatRestore).
internal sealed partial class NoticeCenter
{
    private WeChatSource? _wechat;

    // What the reading of WeChat's chat list is doing, for the settings page and the notices page; Off while it is not chosen.
    public WeChatState WeChatState => _wechat?.State ?? WeChatState.Off;
    public WeChatGateResult WeChatRefusal => _wechat?.Gate ?? WeChatGateResult.Open;

    private void ApplyWeChat(bool read)
    {
        string folder = _app.Store.DirectoryPath;
        if (read)
        {
            _wechat ??= new WeChatSource(_dispatcher, folder, input => { if (_enabled && !_disposed) Receive(WithIcon(input)); },
                key => { if (!_disposed) Hub.SourceRemoved(key); }, () => { if (!_disposed) Changed?.Invoke(); });
            return;
        }
        if (_wechat is { } source) { _wechat = null; source.Dispose(); Changed?.Invoke(); }
        // A BrimDeck that ended without putting WeChat's byte back left a record of it; it is put back now.
        else if (!_wechatChecked) _ = Task.Run(() => { try { WeChatGate.Restore(folder); } catch (Exception ex) { Log(ex); } });
        _wechatChecked = true;
    }
    private bool _wechatChecked;

    // A flash of a chat's taskbar button. While WeChat's list is read the reading says what arrived; otherwise the plain notice.
    private void OnFlash(NoticeInput input)
    {
        if (!_enabled || _disposed) return;
        if (input.AppKey == WeChatRows.AppKey && _wechat is { } source && source.Flashed(input)) return;
        Receive(WithIcon(input));
    }

    // A watched chat names no icon itself; its notices carry its program's (ChatAppIcons), else its built-in one (AppBrandIcons).
    private static NoticeInput WithIcon(NoticeInput input)
        => input.IconRef is null && (ChatAppIcons.Ref(input.AppKey) ?? AppBrandIcons.Ref(input.AppKey)) is { } icon ? input with { IconRef = icon } : input;

    // Brings WeChat's window forward and, for a notice that names a chat, opens that chat.
    private async Task<bool> OpenWeChatAsync(Notice notice)
    {
        if (!ChatRestore.TryRestore(notice, true, new ProtocolRegistry(), new ProtocolLauncher())) return false;
        if (WeChatRows.ChatOf(notice) is { } chat && _wechat is { } source) await source.OpenAsync(chat);
        return true;
    }

    // Whether the notices page offers to answer the notice: it names a chat and the list can be read now.
    public bool CanReply(Notice notice) => WeChatRows.ChatOf(notice) is not null && _wechat is { State: WeChatState.Reading };

    // Sends the words to a chat in WeChat, with its window left where it is. When the chat cannot be opened so (the window is
    // minimized, say), WeChat is asked to bring its window forward and it is tried once more. The chat's notices are done with
    // once it is sent.
    public async Task<WeChatSend> ReplyAsync(string chat, string text)
    {
        if (_wechat is not { } source) return WeChatSend.NoWeChat;
        var result = await source.SendAsync(chat, text);
        if (result == WeChatSend.NotOpened && Hub.Notices.FirstOrDefault(n => WeChatRows.ChatOf(n) == chat) is { } notice
            && ChatRestore.TryRestore(notice, true, new ProtocolRegistry(), new ProtocolLauncher()))
            result = await source.SendAsync(chat, text, raised: true);
        if (result == WeChatSend.Sent && !_disposed)
            foreach (var same in Hub.Notices.Where(n => WeChatRows.ChatOf(n) == chat && n.SourcePresent).ToList()) { Withdraw(same.Id); Hub.SourceRemoved(same.InstanceKey); }
        return result;
    }

    public static string SendFailure(WeChatSend result) => result switch
    {
        WeChatSend.NoWeChat => Loc.T("微信未运行或读不到窗口", "WeChat is not running, or its window cannot be read"),
        WeChatSend.NoList => Loc.T("微信没有停在聊天页", "WeChat is not showing its chats"),
        WeChatSend.NoChat => Loc.T("微信聊天列表里看不到这个会话", "This chat is not in view in WeChat's chat list"),
        WeChatSend.SameName => Loc.T("微信里有同名的会话，无法分辨", "WeChat has more than one chat of this name"),
        WeChatSend.NotOpened => Loc.T("未能在微信中打开这个会话", "Could not open this chat in WeChat"),
        WeChatSend.Draft => Loc.T("微信输入框里有未发送的内容", "WeChat's input box holds unsent text"),
        _ => Loc.T("未能发送，请在微信中回复", "Could not send; reply in WeChat")
    };
}
