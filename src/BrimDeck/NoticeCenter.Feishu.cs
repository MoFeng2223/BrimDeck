using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// Feishu in the notices. It sends no Windows notifications. Without the row's "读取消息内容" only the flash of its taskbar
// button is told ("有新消息", ChatFlash); with it Feishu's own banners are read and hidden (FeishuWatch), a flash is told only
// when no banner came with it (the banner is off in Feishu), and its ringing calls are read from the windows it shows for
// them (CallWindowWatch) until they are answered, declined or given up.
internal sealed partial class NoticeCenter
{
    private FeishuWatch? _feishu;
    private CallWindowWatch? _feishuCalls;

    // Whether Feishu's banners are read; null while it is not chosen. False when Windows refused the hook.
    public bool? FeishuReading => _feishu is { } watch ? !watch.Failed : null;

    private void ApplyFeishu(bool read)
    {
        if (read)
        {
            if (_feishu is not null) return;
            _feishu = new FeishuWatch(_dispatcher, input => { if (_enabled && !_disposed) Receive(WithIcon(input)); });
            _feishuCalls ??= new CallWindowWatch(CallWindowWatch.Feishu, input => { if (_enabled && !_disposed) Receive(WithIcon(input)); },
                key => { if (!_disposed) Hub.SourceRemoved(key); });
            Changed?.Invoke();
            return;
        }
        _feishuCalls?.Dispose(); _feishuCalls = null;
        if (_feishu is { } watch) { _feishu = null; watch.Dispose(); Changed?.Invoke(); }
    }
}
