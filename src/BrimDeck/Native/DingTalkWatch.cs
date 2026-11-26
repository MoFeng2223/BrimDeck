using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// DingTalk's ringing calls. Windows tells when a window is shown or hidden (a WinEvent hook, out of context, so nothing is
// loaded into DingTalk); a top-level Qt window of DingTalk's call program is then asked through UI Automation what it is,
// since Windows' own class for it is Qt's generic one, and a call window is read for who is calling (DingTalkPopups).
// The notice stays until the window goes: answered, declined or given up. Nothing is sent to DingTalk, nothing in it is
// changed or clicked, and no timer reads anything while no call window shows. DingTalk's messages are read from its own
// store instead (DingTalkMessages).
internal sealed class DingTalkWatch : IDisposable
{
    private const uint ObjectDestroy = 0x8001, ObjectShow = 0x8002, ObjectHide = 0x8003, OutOfContext = 0, SkipOwnProcess = 2;
    // A call window's texts are filled in just after it is shown; it is read after the first wait and, while it says nothing
    // yet, after the next ones.
    private static readonly int[] ReadWaits = [30, 150, 400, 900];
    private readonly Action<NoticeInput> _received;
    private readonly Action<string> _ended;
    private readonly WinEventDelegate _callback;
    private readonly nint _hook;
    private readonly HashSet<nint> _shown = [];
    private readonly Dictionary<nint, string> _calls = [];
    private readonly Dictionary<uint, string> _programs = [];
    private long _count;
    private bool _disposed;

    public bool Failed => _hook == 0;
    // Whether a call of DingTalk's is ringing now, so that the flash of its taskbar button says nothing.
    public bool Ringing => _calls.Count > 0;

    public DingTalkWatch(Dispatcher dispatcher, Action<NoticeInput> received, Action<string> ended)
    {
        _received = received; _ended = ended;
        _callback = OnEvent;
        _hook = SetWinEventHook(ObjectDestroy, ObjectHide, 0, _callback, 0, 0, OutOfContext | SkipOwnProcess);
    }

    // On the UI thread, from Windows' message loop.
    private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || window == 0 || objectId != 0 || childId != 0) return;
        if (kind != ObjectShow)
        {
            _shown.Remove(window);
            if (_calls.Remove(window, out var key)) _ended(key);
            return;
        }
        if (GetAncestor(window, 2) != window) return;
        var name = new StringBuilder(32);
        if (GetClassName(window, name, name.Capacity) < 2 || name[0] != 'Q' || name[1] != 't') return;
        if (GetWindowThreadProcessId(window, out uint pid) == 0 || Program(pid) is not { } program) return;
        if (!_shown.Add(window)) return;
        _ = ReadAsync(window, program);
    }

    // DingTalk's call program for a process of DingTalk's, else null; remembered by process id.
    private string? Program(uint pid)
    {
        if (!_programs.TryGetValue(pid, out var program))
        {
            program = System.IO.Path.GetFileName(AppIdentity.ImagePath((int)pid));
            if (_programs.Count > 256) _programs.Clear();
            _programs[pid] = program;
        }
        return string.Equals(program, DingTalkPopups.CallProgram, StringComparison.OrdinalIgnoreCase) ? program : null;
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
                    instanceKey = $"dingtalk:call:{++_count}:{DateTime.UtcNow.Ticks}";
                    _calls[window] = instanceKey;
                    if (input is null) continue;
                    input = input with { InstanceKey = instanceKey };
                }
                if (input is null) continue;
                // A call that ended while it was read is not told as ringing.
                if (!_calls.ContainsKey(window)) return;
                _received(input);
                return;
            }
        }
        finally { if (!_calls.ContainsKey(window)) _shown.Remove(window); }
    }

    // The window's class as UI Automation gives it and, for a call window, the notice its texts say; off the UI thread.
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
                for (int at = children.Count - 1; at >= 0; at--) stack.Push(children[at]);
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
        foreach (string key in _calls.Values) _ended(key);
        _calls.Clear(); _shown.Clear();
    }

    private delegate void WinEventDelegate(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
