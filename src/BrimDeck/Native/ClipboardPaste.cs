using System.Runtime.InteropServices;

namespace BrimDeck.Native;

internal readonly record struct ClipboardPasteTarget(IntPtr Window, uint Thread, uint Process, IntPtr Focus)
{
    internal bool IsEmpty => Window == IntPtr.Zero;
}
internal enum ClipboardPasteResult { Waiting, Submitted, Cancelled, NoTarget, TargetUnavailable, TargetChanged, ClipboardChanged, ModifiersTimeout, FocusTimeout, InputBlocked }
internal readonly record struct ClipboardInputResult(uint Sent, int Error);

// This boundary is injectable so ordering and cancellation do not depend on Windows granting foreground activation.
internal interface IClipboardPastePlatform
{
    long Now { get; }
    IntPtr Foreground { get; }
    uint Sequence { get; }
    bool ModifiersReleased { get; }
    bool IsExternal(IntPtr window);
    bool IsValid(ClipboardPasteTarget target);
    bool FocusReady(ClipboardPasteTarget target);
    void RequestForeground(ClipboardPasteTarget target);
    ClipboardInputResult Send(ClipboardPasteTarget target);
}

// A request owns its receiver and clipboard generation. New UI actions cancel it rather than mutating either value.
internal sealed class ClipboardPasteSession(ClipboardPasteTarget target, uint sequence, long started)
{
    internal ClipboardPasteTarget Target { get; } = target;
    internal ClipboardPasteResult Result { get; private set; } = ClipboardPasteResult.Waiting;
    internal ClipboardInputResult Input { get; private set; }
    private bool _restoreRequested;
    internal void Cancel() { if (Result == ClipboardPasteResult.Waiting) Result = ClipboardPasteResult.Cancelled; }
    internal ClipboardPasteResult Advance(IClipboardPastePlatform platform)
    {
        if (Result != ClipboardPasteResult.Waiting) return Result;
        ClipboardPasteResult End(ClipboardPasteResult result) => Result = result;
        if (Target.IsEmpty) return End(ClipboardPasteResult.NoTarget);
        if (!platform.IsValid(Target)) return End(ClipboardPasteResult.TargetUnavailable);
        if (platform.Sequence != sequence) return End(ClipboardPasteResult.ClipboardChanged);
        var foreground = platform.Foreground;
        if (foreground != Target.Window && platform.IsExternal(foreground)) return End(ClipboardPasteResult.TargetChanged);
        if (!platform.ModifiersReleased)
            return platform.Now - started >= 1200 ? End(ClipboardPasteResult.ModifiersTimeout) : Result;
        if (foreground != Target.Window || !platform.FocusReady(Target))
        {
            if (!_restoreRequested && foreground != Target.Window)
            {
                _restoreRequested = true;
                platform.RequestForeground(Target);
            }
            return platform.Now - started >= 1200 ? End(ClipboardPasteResult.FocusTimeout) : Result;
        }
        // Recheck immediately before injection: clipboard/focus can change while an activation request is processed.
        if (!platform.IsValid(Target) || platform.Foreground != Target.Window || !platform.FocusReady(Target)) return End(ClipboardPasteResult.TargetChanged);
        if (platform.Sequence != sequence) return End(ClipboardPasteResult.ClipboardChanged);
        Input = platform.Send(Target);
        return End(Input.Sent == 4 ? ClipboardPasteResult.Submitted : ClipboardPasteResult.InputBlocked);
    }
}

internal static class ClipboardPaste
{
    internal static IClipboardPastePlatform Platform { get; } = new WindowsPlatform();
    internal static IntPtr Foreground => GetForegroundWindow();
    internal static uint Sequence => GetClipboardSequenceNumber();
    internal static bool IsExternalWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return false;
        GetWindowThreadProcessId(window, out uint pid); return pid != 0 && pid != Environment.ProcessId;
    }
    internal static ClipboardPasteTarget Capture(IntPtr window)
    {
        if (!IsExternalWindow(window)) return default;
        uint thread = GetWindowThreadProcessId(window, out uint process);
        var info = new GUITHREADINFO { Size = Marshal.SizeOf<GUITHREADINFO>() };
        var focus = GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
        return new(window, thread, process, focus);
    }
    internal static bool ModifiersReleased => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(key => (GetAsyncKeyState(key) & 0x8000) == 0);
    internal static bool RestoreFocus(IntPtr target) => target != IntPtr.Zero && IsWindow(target) && (Foreground == target || SetForegroundWindow(target));
    internal static bool Restore(IntPtr target) => IsExternalWindow(target) && RestoreFocus(target);
    internal static bool Send(IntPtr target) => Inject(target).Sent == 4;
    private static ClipboardInputResult Inject(IntPtr target)
    {
        if (!ModifiersReleased || !IsExternalWindow(target) || Foreground != target) return default;
        INPUT Key(ushort key, uint flags) => new() { Type = 1, Data = new INPUTUNION { Keyboard = new KEYBDINPUT { Key = key, Flags = flags } } };
        INPUT[] inputs = [Key(0x11, 0), Key(0x56, 0), Key(0x56, 2), Key(0x11, 2)];
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        int error = sent == inputs.Length ? 0 : Marshal.GetLastPInvokeError();
        if (sent is > 0 and < 4)
        {
            // A partial injection must not leave Ctrl held in the user's application.
            INPUT[] release = [Key(0x56, 2), Key(0x11, 2)];
            SendInput((uint)release.Length, release, Marshal.SizeOf<INPUT>());
        }
        return new(sent, error);
    }
    private sealed class WindowsPlatform : IClipboardPastePlatform
    {
        public long Now => Environment.TickCount64;
        public IntPtr Foreground => ClipboardPaste.Foreground;
        public uint Sequence => ClipboardPaste.Sequence;
        public bool ModifiersReleased => ClipboardPaste.ModifiersReleased;
        public bool IsExternal(IntPtr window) => IsExternalWindow(window);
        public bool IsValid(ClipboardPasteTarget target) => IsExternalWindow(target.Window)
            && GetWindowThreadProcessId(target.Window, out uint process) == target.Thread && process == target.Process;
        public bool FocusReady(ClipboardPasteTarget target)
        {
            var info = new GUITHREADINFO { Size = Marshal.SizeOf<GUITHREADINFO>() };
            return GetGUIThreadInfo(target.Thread, ref info) && info.Focus != IntPtr.Zero
                && (target.Focus == IntPtr.Zero || info.Focus == target.Focus)
                && GetAncestor(info.Focus, 2) == target.Window;
        }
        public void RequestForeground(ClipboardPasteTarget target) => SetForegroundWindow(target.Window);
        public ClipboardInputResult Send(ClipboardPasteTarget target) => Inject(target.Window);
    }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Key, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct GUITHREADINFO
    {
        public int Size; public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32")] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
