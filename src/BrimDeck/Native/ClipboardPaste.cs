using System.Runtime.InteropServices;

namespace BrimDeck.Native;

// Recall always writes the clipboard; optional Ctrl+V goes only to the still-valid foreground window saved before opening
// the chooser. It never pastes into a different window, an elevated target, or while the global shortcut is still held.
internal static class ClipboardPaste
{
    internal static IntPtr Foreground => GetForegroundWindow();
    internal static bool IsExternalWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return false;
        GetWindowThreadProcessId(window, out uint pid); return pid != 0 && pid != Environment.ProcessId;
    }
    internal static bool ModifiersReleased => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(key => (GetAsyncKeyState(key) & 0x8000) == 0);
    internal static bool RestoreFocus(IntPtr target) => target != IntPtr.Zero && IsWindow(target) && SetForegroundWindow(target);
    internal static bool Restore(IntPtr target) => IsExternalWindow(target) && RestoreFocus(target);
    internal static bool Send(IntPtr target)
    {
        if (!ModifiersReleased || !IsExternalWindow(target) || GetForegroundWindow() != target) return false;
        INPUT Key(ushort key, uint flags) => new() { Type = 1, Data = new INPUTUNION { Keyboard = new KEYBDINPUT { Key = key, Flags = flags } } };
        INPUT[] inputs = [Key(0x11, 0), Key(0x56, 0), Key(0x56, 2), Key(0x11, 2)];
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent is > 0 and < 4)
        {
            // A partial injection must not leave Ctrl held in the user's application.
            INPUT[] release = [Key(0x56, 2), Key(0x11, 2)];
            SendInput((uint)release.Length, release, Marshal.SizeOf<INPUT>());
        }
        return sent == inputs.Length;
    }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Key, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
