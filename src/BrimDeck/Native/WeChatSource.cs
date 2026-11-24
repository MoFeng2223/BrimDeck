using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// Off: not switched on. NoWeChat: WeChat is not running or not signed in. Refused: its accessibility could not be turned on
// (Gate says why). InTray: its window is closed to the tray, where it has no controls to read. NoList: its window shows
// another page than the chats. Reading: the chat list is read.
internal enum WeChatState { Off, NoWeChat, Refused, InTray, NoList, Reading }

// The source of WeChat's messages with their words (the "读取消息内容" choice of WeChat's row on the "应用" settings page): it
// turns WeChat's accessibility on (WeChatGate), reads the chat list every Pace on a worker thread (WeChatReader) and hands the
// new messages (WeChatWatch) to the notices on the UI thread. While it reads, a flash of WeChat's taskbar button only makes it
// read at once; the plain "有新消息" of the flash is shown when the reading finds nothing to say (the list is scrolled away from
// the chat, say). While it cannot read, the flash shows as it does without this source. Stop puts WeChat back as it was.
internal sealed class WeChatSource : IDisposable
{
    private static readonly TimeSpan Pace = TimeSpan.FromSeconds(2), SlowPace = TimeSpan.FromSeconds(6), GateRetry = TimeSpan.FromSeconds(30);
    // A flash and the list's change belong together when they are this close; the list is read again this long after a flash.
    private static readonly TimeSpan FlashSpan = TimeSpan.FromSeconds(3);
    private static readonly int[] FlashReads = [0, 400, 1200];
    private readonly Dispatcher _dispatcher;
    private readonly string _folder;
    private readonly Action<NoticeInput> _received;
    private readonly Action<string> _read;
    private readonly Action _changed;
    private readonly SemaphoreSlim _busy = new(1, 1);
    // Held by whatever talks to WeChat on a worker thread, so that Dispose can wait for it without needing the UI thread.
    private readonly object _work = new();
    private readonly WeChatReader _reader = new();
    private readonly WeChatWatch _watch = new(Loc.T("微信", "WeChat"));
    private readonly CancellationTokenSource _stop = new();
    private DateTime _flashAt, _toldAt, _gateTried;
    private int _gatePid;
    private bool _disposed;

    public WeChatState State { get; private set; } = WeChatState.NoWeChat;
    public WeChatGateResult Gate { get; private set; } = WeChatGateResult.Open;

    public WeChatSource(Dispatcher dispatcher, string folder, Action<NoticeInput> received, Action<string> read, Action changed)
    {
        _dispatcher = dispatcher; _folder = folder; _received = received; _read = read; _changed = changed;
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await ReadAsync();
                await Task.Delay(State is WeChatState.Reading or WeChatState.NoList ? Pace : SlowPace, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    // WeChat's taskbar button flashed. True when the reading takes it from here; false while nothing can be read, and the
    // caller shows the plain notice at once.
    public bool Flashed(NoticeInput plain)
    {
        if (_disposed || State != WeChatState.Reading) return false;
        _flashAt = DateTime.UtcNow;
        _ = AfterFlashAsync(plain, _flashAt);
        return true;
    }

    private async Task AfterFlashAsync(NoticeInput plain, DateTime at)
    {
        try
        {
            int waited = 0;
            foreach (int due in FlashReads)
            {
                await Task.Delay(due - waited, _stop.Token); waited = due;
                await ReadAsync();
                if (_toldAt >= at - FlashSpan) return;
            }
            // Nothing in the list told of a message (or the user is in WeChat by now, which the flash filter checks itself).
            if (!_disposed && !InWeChat()) _received(plain);
        }
        catch (OperationCanceledException) { }
    }

    private static bool InWeChat() => WeChatReader.FindMain() is { } main && WeChatReader.InFront(main.Pid);

    // One reading, off the UI thread; what it found is handed over on the UI thread before this returns.
    private async Task ReadAsync()
    {
        await _busy.WaitAsync(_stop.Token);
        try
        {
            bool flashed = DateTime.UtcNow - _flashAt <= FlashSpan;
            var (state, gate, change) = await Task.Run(() => Step(flashed), _stop.Token);
            if (_disposed) return;
            bool moved = state != State || gate != Gate;
            State = state; Gate = gate;
            if (change is not null)
            {
                if (change.Incoming.Count > 0) _toldAt = DateTime.UtcNow;
                foreach (var input in change.Incoming) _received(input);
                foreach (string key in change.Read) _read(key);
            }
            if (moved) _changed();
        }
        finally { _busy.Release(); }
    }

    private (WeChatState, WeChatGateResult, WeChatChange?) Step(bool flashed)
    {
        lock (_work)
        {
            if (_stop.IsCancellationRequested) return (State, Gate, null);
            try { return Read(flashed); }
            // WeChat or its window went away in the middle of a reading.
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException or TimeoutException)
            { return (WeChatState.NoWeChat, WeChatGateResult.Open, null); }
        }
    }

    private (WeChatState, WeChatGateResult, WeChatChange?) Read(bool flashed)
    {
        if (WeChatReader.FindMain() is not { } main) { _watch.Forget(); return (WeChatState.NoWeChat, WeChatGateResult.Open, null); }
        if (!WeChatReader.TreeOpen(main.Handle))
        {
            // A WeChat that refused is asked again only after a while, or when it is another WeChat (it was restarted).
            if (_gatePid == main.Pid && Gate != WeChatGateResult.Open && DateTime.UtcNow - _gateTried < GateRetry) return (WeChatState.Refused, Gate, null);
            _gatePid = main.Pid; _gateTried = DateTime.UtcNow;
            var gate = WeChatGate.Open(main.Pid, () => WeChatReader.TreeOpen(main.Handle), _folder);
            if (gate != WeChatGateResult.Open) return (WeChatState.Refused, gate, null);
        }
        if (_reader.Rows(main.Handle) is not { Count: > 0 } rows) return (main.Visible ? WeChatState.NoList : WeChatState.InTray, WeChatGateResult.Open, null);
        return (WeChatState.Reading, WeChatGateResult.Open, _watch.Observe(rows, flashed, WeChatReader.InFront(main.Pid), DateTime.Now));
    }

    // Opens a chat in WeChat's window (the caller brings the window forward first; the list may need a moment to come back).
    public async Task<bool> OpenAsync(string chat)
    {
        if (_disposed) return false;
        await _busy.WaitAsync(_stop.Token);
        try
        {
            return await Task.Run(() =>
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    lock (_work)
                    {
                        if (_stop.IsCancellationRequested) return false;
                        if (WeChatReader.FindMain() is { } main && WeChatReader.TreeOpen(main.Handle) && _reader.Rows(main.Handle) is { Count: > 0 }) return _reader.Open(main.Handle, chat);
                    }
                    Thread.Sleep(250);
                }
                return false;
            }, _stop.Token);
        }
        catch (OperationCanceledException) { return false; }
        finally { _busy.Release(); }
    }

    public async Task<WeChatSend> SendAsync(string chat, string text)
    {
        if (_disposed) return WeChatSend.NoWeChat;
        await _busy.WaitAsync(_stop.Token);
        try
        {
            return await Task.Run(() =>
            {
                lock (_work)
                    return !_stop.IsCancellationRequested && WeChatReader.FindMain() is { } main && WeChatReader.TreeOpen(main.Handle) ? _reader.Send(main.Handle, chat, text) : WeChatSend.NoWeChat;
            }, _stop.Token);
        }
        catch (OperationCanceledException) { return WeChatSend.NoWeChat; }
        finally { _busy.Release(); }
    }

    // Stops reading and writes WeChat's byte back. A reading under way is given a moment to end first; one that starts
    // afterwards sees the cancellation and does nothing, so the byte is not written again.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        bool free = Monitor.TryEnter(_work, TimeSpan.FromSeconds(4));
        try { WeChatGate.Restore(_folder); }
        finally { if (free) Monitor.Exit(_work); }
        State = WeChatState.Off;
    }
}
