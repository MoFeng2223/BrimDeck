using System.Windows.Threading;
using BrimDeck.Modules;

namespace BrimDeck;

public partial class App
{
    private readonly ModuleNoticeQueue _moduleNotices = new();
    private DispatcherTimer? _moduleNoticePump;
    private ModuleNoticeResult PostModuleNotice(string owner, CancellationToken lifetime, ModuleNotice notice)
    {
        ModuleNoticeQueue.Normalize(notice);
        if (Exiting || Dispatcher.HasShutdownStarted) return ModuleNoticeResult.Unavailable;
        if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(() => PostModuleNotice(owner, lifetime, notice));
        bool wasEmpty = _moduleNotices.Count == 0;
        var result = _moduleNotices.Enqueue(owner, lifetime, notice, DateTimeOffset.UtcNow);
        if (result != ModuleNoticeResult.Queued) return result;
        bool shown = FlushModuleNotices();
        if (_moduleNotices.Count > 0)
        {
            _moduleNoticePump ??= new(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => FlushModuleNotices(), Dispatcher);
            _moduleNoticePump.Start();
        }
        return shown && wasEmpty ? ModuleNoticeResult.Shown : ModuleNoticeResult.Queued;
    }
    private bool FlushModuleNotices()
    {
        if (Exiting) { _moduleNoticePump?.Stop(); return false; }
        _moduleNotices.Prune(DateTimeOffset.UtcNow);
        bool shown = false;
        if (Deck?.CanShowModuleNotice == true && _moduleNotices.Take(DateTimeOffset.UtcNow) is { } ticket)
        { Deck.ShowModuleNotice(ticket); shown = true; }
        if (_moduleNotices.Count == 0) _moduleNoticePump?.Stop();
        return shown;
    }
    private void DropModuleNotices(string owner, CancellationToken lifetime)
    {
        if (Dispatcher.HasShutdownStarted) return;
        void Drop() { _moduleNotices.Remove(owner, lifetime); Deck?.DismissModuleNotice(owner, lifetime); if (_moduleNotices.Count == 0) _moduleNoticePump?.Stop(); }
        if (Dispatcher.CheckAccess()) Drop(); else Dispatcher.BeginInvoke(Drop);
    }
}
