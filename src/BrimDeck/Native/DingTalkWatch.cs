using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// The source of DingTalk's messages and calls with their words (the "读取消息内容" choice of DingTalk's row on the "应用" page):
// Windows tells it when a window is shown or hidden (a WinEvent hook, out of context, so nothing is loaded into DingTalk). A
// top-level Qt window of DingTalk.exe or of its call program is then asked through UI Automation what it is: its Win32 class
// is Qt's own ("Qt51511QWindowToolSaveBits"), and only UI Automation gives the class DingTalkPopups names. One of those is read
// at once; any other is left after that one question. Nothing is sent to DingTalk, nothing in it is changed or clicked, and no
// timer reads anything while no window shows.
// A message popup is reused: each time it is shown it is read again as a new notice, and while it shows, new words in it (a
// message that came before it was hidden) are read as another. A ringing call's notice is there until
// its window goes (answered, declined, given up). A flash of DingTalk's taskbar button tells only of a message DingTalk shows no
// popup for (a group's message that does not mention the user): it is held a moment and dropped when a popup came with it; a
// popup read after the flash was already told takes the place of the plain notice.
internal sealed class DingTalkWatch : IDisposable
{
    private const uint ObjectDestroy = 0x8001, ObjectShow = 0x8002, ObjectHide = 0x8003, ObjectNameChange = 0x800C, OutOfContext = 0, SkipOwnProcess = 2;
    // A window's texts are filled in just after it is shown: it is read after the first wait and, while it says nothing yet,
    // after the next ones.
    private static readonly int[] ReadWaits = [30, 150, 400, 900];
    // How long a flash is held for a popup to come with it; how far back a popup still counts for a flash; how long after a
    // plain notice was told a popup takes its place; how soon the same words again are the same popup.
    private static readonly TimeSpan FlashHold = TimeSpan.FromMilliseconds(800), FlashSpan = TimeSpan.FromSeconds(3),
        Upgrade = TimeSpan.FromSeconds(4), Repeat = TimeSpan.FromSeconds(1);
    private readonly Dispatcher _dispatcher;
    private readonly Action<NoticeInput> _received;
    private readonly Action<string> _ended;
    private readonly WinEventDelegate _callback;
    private readonly nint _hook;
    // The windows of DingTalk shown now that are being asked or read; the calls ringing now, with their notice's key.
    private readonly HashSet<nint> _shown = [];
    private readonly Dictionary<nint, string> _calls = [];
    private readonly Dictionary<uint, string> _programs = [];
    private readonly HashSet<string> _pendingFlashes = new(StringComparer.Ordinal), _settled = new(StringComparer.Ordinal);
    // The message popups shown now, each with the words last read from it; a hook per DingTalk process tells when a text in
    // one of them changes (a message that came while it still showed the one before).
    private readonly Dictionary<nint, (string Title, string Body)> _popups = [];
    private readonly Dictionary<uint, nint> _nameHooks = [];
    private readonly HashSet<nint> _rereads = [];
    private DateTime _popupAt = DateTime.MinValue;
    private (string Key, DateTime At) _lastPlain = ("", DateTime.MinValue);
    private (string Title, string Body, DateTime At) _last;
    private long _count;
    private bool _disposed;

    public bool Failed => _hook == 0;

    public DingTalkWatch(Dispatcher dispatcher, Action<NoticeInput> received, Action<string> ended)
    {
        _dispatcher = dispatcher; _received = received; _ended = ended;
        _callback = OnEvent;
        _hook = SetWinEventHook(ObjectDestroy, ObjectHide, 0, _callback, 0, 0, OutOfContext | SkipOwnProcess);
    }

    // DingTalk's taskbar button flashed. True when this takes it from here (the plain notice shows only if no popup or call
    // came with it); false when the watch does not run, and the caller shows the plain notice at once.
    // DingTalk flashes until the message is read, and the filter gives every flash of it the same key: a key settled once (told,
    // or covered by a popup or a call) is not looked at again.
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
            if (_calls.Count > 0 || _popupAt >= at - FlashSpan) return;
            _lastPlain = (plain.InstanceKey, DateTime.UtcNow);
            _received(plain);
        }), TaskScheduler.Default);
        return true;
    }

    // On the UI thread, from Windows' message loop.
    private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || window == 0) return;
        if (kind == ObjectNameChange)
        {
            if (_popups.ContainsKey(window) && _rereads.Add(window)) _ = RereadAsync(window);
            return;
        }
        if (objectId != 0 || childId != 0) return;
        if (kind != ObjectShow)
        {
            _shown.Remove(window);
            if (_calls.Remove(window, out var key)) _ended(key);
            if (_popups.Remove(window) && _popups.Count == 0) StopNameHooks();
            return;
        }
        if (GetAncestor(window, 2) != window) return;
        var name = new StringBuilder(32);
        if (GetClassName(window, name, name.Capacity) < 2 || name[0] != 'Q' || name[1] != 't') return;
        uint threadId = GetWindowThreadProcessId(window, out uint pid);
        if (threadId == 0 || Program(pid) is not { } program) return;
        if (!_shown.Add(window)) return;
        _ = ReadAsync(window, program);
    }

    // DingTalk.exe or its call program for a process of DingTalk's, else null; remembered by process id (an id reused by
    // another program later is asked again only after BrimDeck restarts, and such a program's windows are then asked once).
    private string? Program(uint pid)
    {
        if (!_programs.TryGetValue(pid, out var program))
        {
            program = System.IO.Path.GetFileName(AppIdentity.ImagePath((int)pid));
            if (_programs.Count > 256) _programs.Clear();
            _programs[pid] = program;
        }
        return string.Equals(program, DingTalkPopups.MainProgram, StringComparison.OrdinalIgnoreCase) || string.Equals(program, DingTalkPopups.CallProgram, StringComparison.OrdinalIgnoreCase) ? program : null;
    }

    private async Task ReadAsync(nint window, string program)
    {
        try
        {
            string instanceKey = "";
            int waited = 0;
            foreach (int wait in ReadWaits)
            {
                await Task.Delay(wait - waited); waited = wait;
                if (_disposed || !_shown.Contains(window)) return;
                var (className, input) = await Task.Run(() => Read(window, program, instanceKey));
                if (_disposed || !_shown.Contains(window) || className is null || !DingTalkPopups.Watched(program, className)) return;
                if (instanceKey.Length == 0)
                {
                    bool message = className == DingTalkPopups.MessageClass;
                    instanceKey = $"dingtalk:{(message ? "msg" : "call")}:{++_count}:{DateTime.UtcNow.Ticks}";
                    if (message) _popupAt = DateTime.UtcNow;
                    else _calls[window] = instanceKey;
                    // The class is known now; the texts are read with the key from here on.
                    if (input is null) continue;
                    input = input with { InstanceKey = instanceKey };
                }
                if (input is null) continue;
                Tell(input);
                if (!input.Call) { _popups[window] = (input.Title, input.Body); WatchNames(window); }
                return;
            }
        }
        finally { if (!_calls.ContainsKey(window)) _shown.Remove(window); }
    }

    // While a popup shows, the texts of its process are followed: Qt tells of a label's new words as a name change.
    private void WatchNames(nint window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0 || _nameHooks.ContainsKey(pid)) return;
        nint hook = SetWinEventHook(ObjectNameChange, ObjectNameChange, 0, _callback, pid, 0, OutOfContext);
        if (hook != 0) _nameHooks[pid] = hook;
    }

    private void StopNameHooks()
    {
        foreach (nint hook in _nameHooks.Values) UnhookWinEvent(hook);
        _nameHooks.Clear();
    }

    // A text of a showing popup changed: once its labels have settled it is read again, and new words are a new message.
    private async Task RereadAsync(nint window)
    {
        await Task.Delay(80);
        _rereads.Remove(window);
        if (_disposed || !_popups.TryGetValue(window, out var shown)) return;
        string instanceKey = $"dingtalk:msg:{++_count}:{DateTime.UtcNow.Ticks}";
        var (_, input) = await Task.Run(() => Read(window, DingTalkPopups.MainProgram, instanceKey));
        if (_disposed || input is null || input.Call || !_popups.ContainsKey(window) || (input.Title, input.Body) == shown) return;
        _popups[window] = (input.Title, input.Body);
        Tell(input);
    }

    private void Tell(NoticeInput input)
    {
        if (!input.Call)
        {
            var now = DateTime.UtcNow;
            if (_last.Title == input.Title && _last.Body == input.Body && now - _last.At < Repeat) return;
            _last = (input.Title, input.Body, now);
            _popupAt = now;
            // The flash of this message was told already as "有新消息": this takes its place instead of standing beside it.
            if (_lastPlain.Key.Length > 0 && now - _lastPlain.At < Upgrade) { input = input with { Replaces = _lastPlain.Key }; _lastPlain = ("", DateTime.MinValue); }
        }
        _received(input);
    }

    // The window's class as UI Automation gives it and, for one of DingTalk's that is read, the notice its texts say; off the UI
    // thread. Only the class is asked of any other window.
    private static (string? Class, NoticeInput? Input) Read(nint window, string program, string instanceKey)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var current = root.Current;
            string className = current.ClassName ?? "";
            if (!DingTalkPopups.Watched(program, className)) return (className, null);
            var texts = new List<DingTalkText>();
            var walker = TreeWalker.RawViewWalker;
            var stack = new Stack<AutomationElement>();
            stack.Push(root);
            int seen = 0;
            while (stack.Count > 0 && seen++ < 120)
            {
                var element = stack.Pop();
                var info = element.Current;
                if (info.ControlType == ControlType.Text) texts.Add(new DingTalkText(info.ClassName ?? "", info.Name ?? ""));
                var children = new List<AutomationElement>();
                for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child)) children.Add(child);
                for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
            return (className, DingTalkPopups.Read(className, current.Name ?? "", texts, instanceKey));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException) { return (null, null); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook != 0) UnhookWinEvent(_hook);
        StopNameHooks();
        foreach (string key in _calls.Values) _ended(key);
        _calls.Clear(); _shown.Clear(); _popups.Clear();
    }

    private delegate void WinEventDelegate(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
