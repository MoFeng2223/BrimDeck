using System.ComponentModel;
using System.Runtime.InteropServices;
using BrimDeck.Core;

namespace BrimDeck.Native;

internal readonly record struct InputMethodKeyEvent(long Session, InputMethodTarget Target, InputMethodKeyDecision Decision);

// Dedicated message pump: the low-level callback only checks state, masks Start and queues an intent.
internal sealed class InputMethodShortcut : IDisposable
{
    private const uint StopMessage = 0x8001;
    private const nuint OwnInputTag = 0x4252494D;
    private readonly HookProc _callback;
    private readonly InputMethodKeys _keys;
    private readonly InputMethodRecordingKeys? _recordingKeys;
    private readonly Action<InputMethodRecordingDecision>? _record;
    private readonly nint _recordingWindow;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Action<InputMethodKeyEvent> _enqueue;
    private readonly WinEventProc _foregroundChanged;
    private sealed record Armed(InputMethodTarget Target);
    private Armed? _armed;
    private InputMethodTarget _capture;
    private bool _capturing;
    private long _session;
    private nint _hook, _foregroundHook;
    private uint _threadId;
    private int _disposed;
    internal Exception? Error { get; private set; }
    internal bool Running { get; private set; }

    internal InputMethodShortcut(InputMethodBinding binding, Action<InputMethodKeyEvent> enqueue, Action foregroundChanged)
    {
        _keys = new(binding);
        _enqueue = enqueue; _callback = Intercept;
        _foregroundChanged = (_, _, _, _, _, _, _) => foregroundChanged();
        _thread = new Thread(Run) { IsBackground = true, Name = "BrimDeck input-method shortcut" };
        _thread.Start();
        _ready.Wait(1000);
    }

    internal InputMethodShortcut(Action<InputMethodRecordingDecision> record)
    {
        _keys = new(); _recordingKeys = new(); _record = record;
        _recordingWindow = TargetInputMethod.Foreground().Window;
        _enqueue = _ => { }; _callback = Intercept; _foregroundChanged = (_, _, _, _, _, _, _) => { };
        _thread = new Thread(Run) { IsBackground = true, Name = "BrimDeck shortcut recorder" };
        _thread.Start(); _ready.Wait(1000);
    }

    internal void Arm(InputMethodTarget? target) => Volatile.Write(ref _armed, target is { } t ? new(t) : null);

    private void Run()
    {
        try
        {
            _threadId = GetCurrentThreadId(); PeekMessage(out _, 0, 0, 0, 0);
            for (uint key = 8; key < 256; key++)
                if (key is not (0x10 or 0x11 or 0x12) && (GetAsyncKeyState((int)key) & 0x8000) != 0)
                { _keys.SeedHeldKey(key); _recordingKeys?.SeedHeldKey(key); }
            _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
            if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _foregroundHook = SetWinEventHook(3, 3, 0, _foregroundChanged, 0, 0, 0);
            Running = true; _ready.Set();
            if (Volatile.Read(ref _disposed) != 0) return;
            int result;
            while ((result = GetMessage(out var message, 0, 0, 0)) > 0)
            {
                if (message.Id == StopMessage) break;
                TranslateMessage(ref message); DispatchMessage(ref message);
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex) { Error = ex; }
        finally { if (_foregroundHook != 0) UnhookWinEvent(_foregroundHook); if (_hook != 0) UnhookWindowsHookEx(_hook); Running = false; _ready.Set(); }
    }

    private nint Intercept(int code, nuint message, nint data)
    {
        if (code < 0) return CallNextHookEx(_hook, code, message, data);
        try
        {
            var key = Marshal.PtrToStructure<KeyboardEvent>(data);
            bool down = message is 0x100 or 0x104, up = message is 0x101 or 0x105;
            if ((!down && !up) || key.Extra == OwnInputTag || (key.Flags & 0x10) != 0)
                return CallNextHookEx(_hook, code, message, data);
            if (_recordingKeys is not null)
            {
                if (TargetInputMethod.Foreground().Window != _recordingWindow)
                    return CallNextHookEx(_hook, code, message, data);
                var recorded = _recordingKeys.Handle(key.Key, down);
                _record!(recorded);
                return recorded.Block ? 1 : CallNextHookEx(_hook, code, message, data);
            }
            var armed = Volatile.Read(ref _armed);
            if (_capturing && (!TargetInputMethod.IsCurrent(_capture) || armed?.Target != _capture))
            {
                _keys.Cancel(); _capturing = false;
                _enqueue(new(_session, _capture, new(Cancel: true)));
            }
            var decision = _keys.Handle(key.Key, down, canStart: armed is not null && TargetInputMethod.IsCurrent(armed.Target));
            if (decision.Begin)
            {
                if (decision.MaskMenu && !MaskStartMenu()) { _keys.AbortUnblockedSpace(); return CallNextHookEx(_hook, code, message, data); }
                _capture = armed!.Target; _capturing = true; _session++;
            }
            if (decision.Next || decision.Commit || decision.Cancel) _enqueue(new(_session, _capture, decision));
            if (decision.Commit || decision.Cancel) _capturing = false;
            if (decision.Block) return 1;
        }
        catch (Exception ex) { Error = ex; _keys.Cancel(); }
        return CallNextHookEx(_hook, code, message, data);
    }

    private static bool MaskStartMenu()
    {
        // PowerToys' reserved dummy-key technique; Win-up is always allowed through.
        Input[] inputs = [new() { Type = 1, Data = new() { Keyboard = new() { Key = 0xFF, Extra = OwnInputTag } } },
            new() { Type = 1, Data = new() { Keyboard = new() { Key = 0xFF, Flags = 2, Extra = OwnInputTag } } }];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Arm(null);
        if (_ready.Wait(500) && _thread.IsAlive) PostThreadMessage(_threadId, StopMessage, 0, 0);
        _thread.Join(750);
    }

    private delegate nint HookProc(int code, nuint message, nint data);
    private delegate void WinEventProc(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardEvent { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [DllImport("user32", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32", EntryPoint = "GetMessageW", SetLastError = true)] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32", EntryPoint = "PostThreadMessageW")] private static extern bool PostThreadMessage(uint thread, uint message, nuint wparam, nint lparam);
    [DllImport("user32", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
