using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// DingTalk in the notices. It sends no Windows notification. Without the row's "读取消息内容" only the flash of its taskbar
// button is told ("有新消息", ChatFlash). With it, its messages are read from its own store with the chat, the sender and the
// words (DingTalkMessages), its ringing calls from the windows it draws for them (DingTalkWatch), and the taskbar flash is
// left out while the messages are being read -- it would only repeat what the store already said.
internal sealed partial class NoticeCenter
{
    private DingTalkMessages? _dingTalkMessages;
    private DingTalkWatch? _dingTalkCalls;

    // How the reading of DingTalk goes, for the settings page; Off while it is not chosen.
    public DingTalkState DingTalkState => _dingTalkMessages?.State ?? DingTalkState.Off;
    // False when Windows refused the hook that tells of its call windows.
    public bool DingTalkCalls => _dingTalkCalls is not { Failed: true };

    private void ApplyDingTalk(bool read)
    {
        if (read)
        {
            if (_dingTalkMessages is null)
                _dingTalkMessages = new DingTalkMessages(_dispatcher, input => { if (_enabled && !_disposed) Receive(WithIcon(input)); }, () => { if (!_disposed) Changed?.Invoke(); });
            _dingTalkCalls ??= new DingTalkWatch(_dispatcher, input => { if (_enabled && !_disposed) Receive(WithIcon(input)); },
                key => { if (!_disposed) Hub.SourceRemoved(key); });
            Changed?.Invoke();
            return;
        }
        if (_dingTalkMessages is null && _dingTalkCalls is null) return;
        _dingTalkMessages?.Dispose(); _dingTalkMessages = null;
        _dingTalkCalls?.Dispose(); _dingTalkCalls = null;
        Changed?.Invoke();
    }

    // A flash of DingTalk's taskbar button: nothing to say while its messages are being read or a call of its is ringing.
    private bool DingTalkCovers() => _dingTalkMessages is { Covering: true } || _dingTalkCalls is { Ringing: true };
}
