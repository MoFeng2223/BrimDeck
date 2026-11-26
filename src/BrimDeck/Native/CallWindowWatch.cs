using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// How the user answers a ringing call from the notices page: declines it, answers it as the caller asked (video for a video
// call), or answers a video call with voice only (where the call window offers that, as Feishu's does).
internal enum CallAnswer { Decline, Accept, Voice }

// The windows a chat program shows for a ringing call: DingTalk's (DingTalkPopups), WeChat's (WeChatCalls) and Feishu's
// (FeishuCalls). Windows tells when a window is shown or hidden (a WinEvent hook, out of context, so nothing is loaded into the
// program); a top-level window of the program of the kind's Windows class is then asked through UI Automation what it is
// (Windows' own class of a Qt window is Qt's generic one), and a call window is read for who is calling. The notice stays until
// the window goes: answered, declined or given up.
// Nothing is sent to the program and nothing in it is changed, except when the user answers or declines a call from the
// notices page (Press): then the call window's own button is pressed, as the user would.
// No timer reads anything while no call window shows.
internal sealed class CallWindowWatch : IDisposable
{
    // Which programs' windows are watched and of which Windows class, which of them are calls, what a call window's texts and
    // buttons say (null while it says nothing yet), and the names of its buttons that answer, that answer with voice only and
    // that decline; a call's notices are keyed KeyPrefix and a count. InvokeFirst: the buttons are pressed through UI
    // Automation first, and clicked only where that did nothing.
    public sealed record Kind(string[] Programs, Func<string, bool> Shell, Func<string, string, bool> Watched,
        Func<string, string, IReadOnlyList<PopupText>, string, NoticeInput?> Read, string KeyPrefix,
        string[] Accept, string[] Voice, string[] Decline, bool InvokeFirst);
    private static bool Qt(string shell) => shell.StartsWith("Qt", StringComparison.Ordinal);
    public static readonly Kind DingTalk = new([DingTalkPopups.CallProgram], Qt, DingTalkPopups.Watched, DingTalkPopups.Read, "dingtalk:call:",
        DingTalkPopups.Accept, [], DingTalkPopups.Decline, false);
    public static readonly Kind WeChat = new([WeChatCalls.Program], Qt, WeChatCalls.Watched, WeChatCalls.Read, WeChatCalls.KeyPrefix,
        WeChatCalls.Accept, [], WeChatCalls.Decline, false);
    public static readonly Kind Feishu = new(FeishuBanners.Programs, shell => shell == FeishuCalls.WindowClass, FeishuCalls.Watched, FeishuCalls.Read, FeishuCalls.KeyPrefix,
        FeishuCalls.Accept, FeishuCalls.Voice, FeishuCalls.Decline, true);

    private const uint ObjectDestroy = 0x8001, ObjectShow = 0x8002, ObjectHide = 0x8003, OutOfContext = 0, SkipOwnProcess = 2;
    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, SkipInvisible = 1, SkipTransparent = 4;
    private const nint MK_LBUTTON = 1;
    // How long a pressed button is given to take effect (the window goes, or the button does) before it is pressed the other
    // way, and how often that is looked at.
    private static readonly TimeSpan PressWait = TimeSpan.FromMilliseconds(1500);
    private const int PressLook = 100;
    // A call window's texts are filled in just after it is shown; it is read after the first wait and, while it says nothing
    // yet, after the next ones.
    private static readonly int[] ReadWaits = [30, 150, 400, 900];
    private readonly Kind _kind;
    private readonly Action<NoticeInput> _received;
    private readonly Action<string> _ended;
    private readonly WinEventDelegate _callback;
    private readonly nint _hook;
    private readonly HashSet<nint> _shown = [];
    private readonly Dictionary<nint, string> _calls = [];
    // The call windows that offer to answer with voice only besides answering as asked.
    private readonly HashSet<nint> _voice = [];
    private readonly Dictionary<uint, string?> _programs = [];
    private long _count;
    private bool _disposed;

    public bool Failed => _hook == 0;
    // Whether a call of the program is ringing now, so that the flash of its taskbar button says nothing.
    public bool Ringing => _calls.Count > 0;

    public CallWindowWatch(Kind kind, Action<NoticeInput> received, Action<string> ended)
    {
        _kind = kind; _received = received; _ended = ended;
        _callback = OnEvent;
        _hook = SetWinEventHook(ObjectDestroy, ObjectHide, 0, _callback, 0, 0, OutOfContext | SkipOwnProcess);
    }

    // On the UI thread, from Windows' message loop.
    private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || window == 0 || objectId != 0 || childId != 0) return;
        if (kind != ObjectShow)
        {
            _shown.Remove(window); _voice.Remove(window);
            if (_calls.Remove(window, out var key)) _ended(key);
            return;
        }
        if (GetAncestor(window, 2) != window) return;
        var name = new StringBuilder(32);
        if (GetClassName(window, name, name.Capacity) == 0 || !_kind.Shell(name.ToString())) return;
        if (GetWindowThreadProcessId(window, out uint pid) == 0 || Program(pid) is not { } program) return;
        if (!_shown.Add(window)) return;
        _ = ReadAsync(window, program);
    }

    // The watched program's file name for a process of it, else null; remembered by process id.
    private string? Program(uint pid)
    {
        if (!_programs.TryGetValue(pid, out var program))
        {
            string file = System.IO.Path.GetFileName(AppIdentity.ImagePath((int)pid));
            program = _kind.Programs.FirstOrDefault(watched => string.Equals(watched, file, StringComparison.OrdinalIgnoreCase));
            if (_programs.Count > 256) _programs.Clear();
            _programs[pid] = program;
        }
        return program;
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
                var (className, input, voice) = await Task.Run(() => Read(_kind, window, program, instanceKey));
                if (_disposed || !_shown.Contains(window) || className is null || !_kind.Watched(program, className)) return;
                if (instanceKey.Length == 0)
                {
                    instanceKey = $"{_kind.KeyPrefix}{++_count}:{DateTime.UtcNow.Ticks}";
                    _calls[window] = instanceKey;
                    if (input is null) continue;
                    input = input with { InstanceKey = instanceKey };
                }
                if (input is null) continue;
                // A call that ended while it was read is not told as ringing.
                if (!_calls.ContainsKey(window)) return;
                if (voice) _voice.Add(window);
                _received(input);
                return;
            }
        }
        finally { if (!_calls.ContainsKey(window)) _shown.Remove(window); }
    }

    // The window's class as UI Automation gives it and, for a call window, the notice its texts and buttons say and whether it
    // offers to answer with voice only; off the UI thread.
    private static (string? Class, NoticeInput? Input, bool Voice) Read(Kind kind, nint window, string program, string instanceKey)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var current = root.Current;
            string className = current.ClassName ?? "";
            if (!kind.Watched(program, className)) return (className, null, false);
            var texts = new List<PopupText>();
            var walker = TreeWalker.RawViewWalker;
            var stack = new Stack<AutomationElement>();
            stack.Push(root);
            int seen = 0;
            while (stack.Count > 0 && seen++ < 120)
            {
                var element = stack.Pop();
                var info = element.Current;
                if (info.ControlType == ControlType.Text) texts.Add(new PopupText(info.ClassName ?? "", info.Name ?? "", info.AutomationId ?? ""));
                else if (IsButton(info)) texts.Add(new PopupText(info.ClassName ?? "", info.Name ?? "", info.AutomationId ?? "", Button: true));
                var children = new List<AutomationElement>();
                for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child)) children.Add(child);
                for (int at = children.Count - 1; at >= 0; at--) stack.Push(children[at]);
            }
            bool Offers(string[] names) => texts.Any(text => text.Button && names.Contains(text.Name.Trim(), StringComparer.OrdinalIgnoreCase));
            bool voice = kind.Voice.Length > 0 && Offers(kind.Accept) && Offers(kind.Voice);
            return (className, kind.Read(className, current.Name ?? "", texts, instanceKey), voice);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException) { return (null, null, false); }
    }

    // A button, or a button that UI Automation gives as a check box (as DingTalk's are).
    private static bool IsButton(AutomationElement.AutomationElementInformation info) => info.ControlType == ControlType.Button || info.ControlType == ControlType.CheckBox;

    // Whether the call of this notice still rings, so that the notices page offers to answer or decline it here.
    public bool Has(string instanceKey) => !_disposed && _calls.ContainsValue(instanceKey);

    // Whether the call of this notice can also be answered with voice only.
    public bool HasVoice(string instanceKey) => !_disposed && _calls.FirstOrDefault(pair => pair.Value == instanceKey) is { Key: not 0 } call && _voice.Contains(call.Key);

    // Answers or declines the call of this notice with its window's own button. True once the button took effect: the window or
    // the button is gone. The notice of the call is then done with. On the UI thread.
    public async Task<bool> Press(string instanceKey, CallAnswer answer)
    {
        if (_disposed || _calls.FirstOrDefault(pair => pair.Value == instanceKey) is not { Key: not 0 } call) return false;
        nint window = call.Key;
        // Answering as asked takes the window's answering button, else (a window with only a voice one) that one.
        string[] names = answer switch { CallAnswer.Decline => _kind.Decline, CallAnswer.Voice => _kind.Voice, _ => [.. _kind.Accept, .. _kind.Voice] };
        bool invokeFirst = _kind.InvokeFirst;
        bool done = await Task.Run(() => PressButton(window, names, invokeFirst));
        if (done && !_disposed && _calls.Remove(window, out var key)) { _voice.Remove(window); _ended(key); }
        return done;
    }

    // Off the UI thread. The button is pressed one way and, where that has no effect in a moment, the other: by messages posted
    // to the window at its middle (the pointer is not moved and the window is not brought forward; WeChat's buttons do nothing on
    // UI Automation's Invoke, 2026-11-25; a minimized window has nothing to click), or through UI Automation (Invoke, or Toggle
    // for a button given as a check box).
    private static bool PressButton(nint window, string[] names, bool invokeFirst)
    {
        try
        {
            bool tried = false;
            foreach (bool invoke in invokeFirst ? new[] { true, false } : [false, true])
            {
                if (Button(window, names) is not { } button) return tried;
                if (!(invoke ? Invoke(button) : !IsIconic(window) && PostClick(window, button.Current.BoundingRectangle))) continue;
                tried = true;
                if (Took(window, names)) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException) { return !IsWindowVisible(window); }
    }

    private static bool Invoke(AutomationElement button)
    {
        if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) { ((InvokePattern)invoke).Invoke(); return true; }
        if (button.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) { ((TogglePattern)toggle).Toggle(); return true; }
        return false;
    }

    // Whether the pressed button took effect within PressWait: the window is gone or hidden, or no such button is in it now.
    private static bool Took(nint window, string[] names)
    {
        var until = DateTime.UtcNow + PressWait;
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(PressLook);
            if (!IsWindowVisible(window) || Button(window, names) is null) return true;
        }
        return false;
    }

    // The window's enabled button of one of the names, the earliest of the names first.
    private static AutomationElement? Button(nint window, string[] names)
    {
        if (!IsWindowVisible(window)) return null;
        var walker = TreeWalker.RawViewWalker;
        var stack = new Stack<AutomationElement>();
        stack.Push(AutomationElement.FromHandle(window));
        AutomationElement? found = null;
        int rank = names.Length, seen = 0;
        while (stack.Count > 0 && seen++ < 200)
        {
            var element = stack.Pop();
            var info = element.Current;
            if (IsButton(info) && info.IsEnabled)
            {
                int at = Array.FindIndex(names, name => string.Equals(name, (info.Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                if (at >= 0 && at < rank) { found = element; rank = at; }
            }
            for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child)) stack.Push(child);
        }
        return found;
    }

    // A left click posted at the middle of the area (screen pixels), to the innermost visible window of the call window there.
    private static bool PostClick(nint window, System.Windows.Rect area)
    {
        if (area.IsEmpty || !double.IsFinite(area.X) || !double.IsFinite(area.Y) || !double.IsFinite(area.Width) || !double.IsFinite(area.Height)
            || area.Width < 4 || area.Height < 4) return false;
        var screen = new POINT { X = (int)(area.X + area.Width / 2), Y = (int)(area.Y + area.Height / 2) };
        nint target = window;
        for (int depth = 0; depth < 8; depth++)
        {
            var inner = screen;
            if (!ScreenToClient(target, ref inner)) return false;
            nint child = ChildWindowFromPointEx(target, inner, SkipInvisible | SkipTransparent);
            if (child == 0 || child == target) break;
            target = child;
        }
        var point = screen;
        if (!ScreenToClient(target, ref point)) return false;
        nint at = (point.Y << 16) | (point.X & 0xFFFF);
        return PostMessage(target, WM_MOUSEMOVE, 0, at) && PostMessage(target, WM_LBUTTONDOWN, MK_LBUTTON, at) && PostMessage(target, WM_LBUTTONUP, 0, at);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook != 0) UnhookWinEvent(_hook);
        foreach (string key in _calls.Values) _ended(key);
        _calls.Clear(); _shown.Clear(); _voice.Clear();
    }

    private delegate void WinEventDelegate(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref POINT point);
    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint parent, POINT point, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
