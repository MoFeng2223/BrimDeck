using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// DingTalk in the notices. It sends no Windows notifications. Without the row's "读取消息内容" only the flash of its taskbar
// button is told ("有新消息", ChatFlash); with it the popups and call windows DingTalk shows are read (DingTalkWatch), and a
// flash is told only when no popup came with it (a group's message that does not mention the user).
internal sealed partial class NoticeCenter
{
    private DingTalkWatch? _dingTalk;

    // Whether the windows of DingTalk are read; null while it is not chosen. False when Windows refused the hook.
    public bool? DingTalkReading => _dingTalk is { } watch ? !watch.Failed : null;

    private void ApplyDingTalk(bool read)
    {
        if (read)
        {
            if (_dingTalk is not null) return;
            _dingTalk = new DingTalkWatch(_dispatcher, input => { if (_enabled && !_disposed) Receive(WithIcon(input)); },
                key => { if (!_disposed) Hub.SourceRemoved(key); });
            Changed?.Invoke();
            return;
        }
        if (_dingTalk is { } watch) { _dingTalk = null; watch.Dispose(); Changed?.Invoke(); }
    }
}
