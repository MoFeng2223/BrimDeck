using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// The source of Feishu's messages with their words (the "读取消息内容" choice of Feishu's row on the "应用" page): Feishu's own
// banner is read and hidden, so the island tells the message in its place (FeishuBanners). Windows tells it when a window is
// shown (a WinEvent hook, out of context, so nothing is loaded into Feishu). A top-level window of Feishu.exe of the banner's
// class, without a title and of a banner's size is hidden at once when it sits in the corner where the banners show (measured:
// hidden 2 to 5 ms after it was shown, and not seen), then asked through UI Automation what it is: a banner is read and
// stays hidden until Feishu closes it, as it does when the next one comes or after about ten seconds; any other window
// (Feishu's notice of an update, the menu of its tray icon) is shown again as it was. A window of that size elsewhere is
// read first and hidden once it is known to be a banner. Nothing else of Feishu is touched: nothing is clicked or sent, and
// no timer reads anything while no window shows. Hidden this way, Feishu still flashes its taskbar button and keeps the
// message unread.
// A flash of Feishu's taskbar button tells of a message whose banner did not come (the banner is off in Feishu): it is held a
// moment and dropped when a banner came with it; a banner read after the flash was already told takes the place of the plain
// notice.
internal sealed class FeishuWatch : IDisposable
{
    private const uint ObjectDestroy = 0x8001, ObjectShow = 0x8002, OutOfContext = 0, SkipOwnProcess = 2;
    private const int HideWindow = 0, ShowNoActivate = 4;
    // A banner's texts are there when it is shown (read 40 ms after, every time): the later waits are for a slow machine.
    private static readonly int[] ReadWaits = [30, 120, 300, 700];
    // How long a flash is held for a banner to come with it; how far back a banner still counts for a flash (Feishu flashes
    // about five times over four seconds for one message); how long after a plain notice was told a banner takes its place;
    // how soon the same words again are the same banner.
    private static readonly TimeSpan FlashHold = TimeSpan.FromMilliseconds(800), FlashSpan = TimeSpan.FromSeconds(5),
        Upgrade = TimeSpan.FromSeconds(5), Repeat = TimeSpan.FromSeconds(1);
    private readonly Dispatcher _dispatcher;
    private readonly Action<NoticeInput> _received;
    private readonly WinEventDelegate _callback;
    private readonly nint _hook;
    // The windows being asked or read now; the banners read and hidden, each with its words, until Feishu closes them.
    private readonly HashSet<nint> _reading = [];
    private readonly Dictionary<nint, (string Title, string Body)> _banners = [];
    // The windows hidden and then shown again as not being banners: their showing again (that one too) is left alone.
    private readonly HashSet<nint> _returned = [];
    private readonly Dictionary<uint, bool> _programs = [];
    private readonly HashSet<string> _pendingFlashes = new(StringComparer.Ordinal), _settled = new(StringComparer.Ordinal);
    private DateTime _bannerAt = DateTime.MinValue;
    private (string Key, DateTime At) _lastPlain = ("", DateTime.MinValue);
    private (string Title, string Body, DateTime At) _last;
    private long _count;
    private bool _disposed;

    public bool Failed => _hook == 0;

    public FeishuWatch(Dispatcher dispatcher, Action<NoticeInput> received)
    {
        _dispatcher = dispatcher; _received = received;
        _callback = OnEvent;
        _hook = SetWinEventHook(ObjectDestroy, ObjectShow, 0, _callback, 0, 0, OutOfContext | SkipOwnProcess);
    }

    // Feishu's taskbar button flashed. True when this takes it from here (the plain notice shows only if no banner came with
    // it); false when the watch does not run, and the caller shows the plain notice at once. The filter gives the flashes of
    // one message the same key: a key settled once (told, or covered by a banner) is not looked at again.
    public bool Flashed(NoticeInput plain)
    {
        if (_disposed || Failed) return false;
        if (_settled.Contains(plain.InstanceKey) || !_pendingFlashes.Add(plain.InstanceKey)) return true;
        var at = DateTime.UtcNow;
        _ = Task.Delay(FlashHold).ContinueWith(_ => _dispatcher.BeginInvoke(() =>
        {
            _pendingFlashes.Remove(plain.InstanceKey);
            if (_disposed) return;
            if (_settled.Count > 64) _settled.Clear();
            _settled.Add(plain.InstanceKey);
            if (_bannerAt >= at - FlashSpan) return;
            _lastPlain = (plain.InstanceKey, DateTime.UtcNow);
            _received(plain);
        }), TaskScheduler.Default);
        return true;
    }

    // On the UI thread, from Windows' message loop.
    private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || window == 0 || objectId != 0 || childId != 0) return;
        if (kind == ObjectDestroy) { _banners.Remove(window); _returned.Remove(window); return; }
        if (kind != ObjectShow || _returned.Contains(window)) return;
        // A banner read already that shows again: hidden again, and read again in case it holds another message now.
        if (_banners.TryGetValue(window, out var known))
        {
            ShowWindowAsync(window, HideWindow);
            if (_reading.Add(window)) _ = ReadAsync(window, true, known);
            return;
        }
        // Every window of every program that shows comes here, on the UI thread: nothing asked of it may wait for that program's
        // answer (a suspended app never answers). The class, the process and the title are read from what Windows keeps.
        if (_reading.Contains(window) || GetAncestor(window, 2) != window) return;
        var name = new StringBuilder(32);
        if (GetClassName(window, name, name.Capacity) == 0 || name.ToString() != "Chrome_WidgetWin_1") return;
        if (GetWindowThreadProcessId(window, out uint pid) == 0 || !IsFeishu(pid)) return;
        if (InternalGetWindowText(window, new StringBuilder(2), 2) != 0) return;
        if (!GetWindowRect(window, out var rect)) return;
        double scale = GetDpiForWindow(window) is var dpi and > 0 ? 96.0 / dpi : 1;
        if (!FeishuBanners.MaySize((rect.Right - rect.Left) * scale, (rect.Bottom - rect.Top) * scale)) return;
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        bool corner = GetMonitorInfo(MonitorFromWindow(window, 2), ref info)
            && FeishuBanners.InCorner((info.Work.Right - rect.Right) * scale, (info.Work.Bottom - rect.Bottom) * scale);
        if (corner) ShowWindowAsync(window, HideWindow);
        _reading.Add(window);
        _ = ReadAsync(window, corner, null);
    }

    // Whether a process is Feishu's; remembered by process id (an id reused by another program later is asked again only
    // after BrimDeck restarts, and such a program's windows are then asked once).
    private bool IsFeishu(uint pid)
    {
        if (!_programs.TryGetValue(pid, out bool feishu))
        {
            feishu = FeishuBanners.IsProgram(System.IO.Path.GetFileName(AppIdentity.ImagePath((int)pid)));
            if (_programs.Count > 256) _programs.Clear();
            _programs[pid] = feishu;
        }
        return feishu;
    }

    // Reads a window that was shown; hidden says whether it was hidden before being read. A banner stays hidden and is told
    // unless it says what it said before (known); any other window, or one that never says what it is, is shown again.
    private async Task ReadAsync(nint window, bool hidden, (string Title, string Body)? known)
    {
        bool banner = false;
        try
        {
            int waited = 0;
            foreach (int wait in ReadWaits)
            {
                await Task.Delay(wait - waited); waited = wait;
                if (_disposed) return;
                string instanceKey = $"feishu:msg:{_count + 1}:{DateTime.UtcNow.Ticks}";
                var (kind, input) = await Task.Run(() => Read(window, instanceKey));
                if (_disposed || kind is null) return;
                if (kind == FeishuWindowKind.Other) return;
                if (kind == FeishuWindowKind.NotReady || input is null) continue;
                banner = true;
                if (!hidden) { ShowWindowAsync(window, HideWindow); hidden = true; }
                _banners[window] = (input.Title, input.Body);
                if (known != (input.Title, input.Body)) { _count++; Tell(input); }
                return;
            }
        }
        finally
        {
            _reading.Remove(window);
            // Not a banner, or not known to be one: Feishu's window is given back as it was.
            if (hidden && !banner && known is null && IsWindow(window))
            {
                if (_returned.Count > 64) _returned.Clear();
                _returned.Add(window);
                ShowWindowAsync(window, ShowNoActivate);
            }
        }
    }

    private void Tell(NoticeInput input)
    {
        var now = DateTime.UtcNow;
        if (_last.Title == input.Title && _last.Body == input.Body && now - _last.At < Repeat) return;
        _last = (input.Title, input.Body, now);
        _bannerAt = now;
        // The flash of this message was told already as "有新消息": this takes its place instead of standing beside it.
        if (_lastPlain.Key.Length > 0 && now - _lastPlain.At < Upgrade) { input = input with { Replaces = _lastPlain.Key }; _lastPlain = ("", DateTime.MinValue); }
        _received(input);
    }

    // What the window is, from the parts under its RootView's WidgetDelegateView; off the UI thread. Null when the window is
    // gone or cannot be read.
    private static (FeishuWindowKind? Kind, NoticeInput? Input) Read(nint window, string instanceKey)
    {
        try
        {
            var walker = TreeWalker.RawViewWalker;
            var root = AutomationElement.FromHandle(window);
            var view = Child(walker, root, "RootView");
            if (view is null) return (FeishuWindowKind.NotReady, null);
            var content = Child(walker, view, "WidgetDelegateView");
            if (content is null) return (walker.GetFirstChild(view) is null ? FeishuWindowKind.NotReady : FeishuWindowKind.Other, null);
            var parts = new List<FeishuPart>();
            for (var child = walker.GetFirstChild(content); child is not null && parts.Count < 40; child = walker.GetNextSibling(child))
            {
                var current = child.Current;
                string kind = current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal);
                parts.Add(new FeishuPart(kind, current.ClassName ?? "", current.Name ?? ""));
            }
            return FeishuBanners.Read(parts, instanceKey);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException) { return (null, null); }
    }

    private static AutomationElement? Child(TreeWalker walker, AutomationElement parent, string className)
    {
        for (var child = walker.GetFirstChild(parent); child is not null; child = walker.GetNextSibling(child))
            if (child.Current.ClassName == className) return child;
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook != 0) UnhookWinEvent(_hook);
        _banners.Clear(); _reading.Clear(); _returned.Clear();
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public uint Flags; }
    private delegate void WinEventDelegate(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    // Posted to Feishu's thread: BrimDeck does not wait for Feishu to hide or show its window.
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
}
