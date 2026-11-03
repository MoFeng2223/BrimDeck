using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace BrimDeck.Native;

internal readonly record struct InputMethodTarget(nint Window, uint Thread, uint Process);
internal sealed record TargetInputMethodResult(bool Confirmed, string? ActiveId, int Error = 0, bool ReachedTarget = false);

// Queries and activates TSF on a foreground window's own UI thread. The helper is a tiny
// native call-window hook, installed only for this request and always removed in finally.
// No keyboard text is observed, no executable memory is written and no session defaults change.
internal static class TargetInputMethod
{
    private const uint Magic = 0x4244494D;
    private static readonly Lazy<nint> Module = new(() => NativeLibrary.TryLoad(
        Path.Combine(AppContext.BaseDirectory, "BrimDeck.InputMethods.Native.dll"), out var handle) ? handle : 0);
    private static readonly uint Message = RegisterWindowMessage("BrimDeck.InputMethods.Request.v1");

    internal static bool Available => Module.Value != 0;
    internal static int RequestSize => Marshal.SizeOf<NativeRequest>();
    internal static InputMethodTarget Foreground()
    {
        nint window = GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(window, out uint process);
        // Hosted/Chromium windows can receive input on a focused child, on another thread.
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (thread != 0 && GetGUIThreadInfo(thread, ref info) && info.Focus != 0 && GetAncestor(info.Focus, 2) == window)
        { window = info.Focus; thread = GetWindowThreadProcessId(window, out process); }
        return new(window, thread, process);
    }
    internal static bool IsCurrent(InputMethodTarget target) => target.Window != 0 && target == Foreground();
    internal static nint Monitor(InputMethodTarget target) => MonitorFromWindow(target.Window, 2);
    // Observation does not intercept keys. Wait for physical modifiers to be released before
    // presenting a stable profile, including Windows' native hold-to-cycle switcher.
    internal static bool SwitchModifiersHeld() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    internal static TargetInputMethodResult Read(InputMethodTarget target) => Request(target, null);
    internal static TargetInputMethodResult Activate(InputMethodTarget target, InstalledInputMethod profile)
        => Request(target, profile);

    private static TargetInputMethodResult Request(InputMethodTarget target, InstalledInputMethod? profile)
    {
        if (!Available || !IsCurrent(target) || !Is64Bit(target.Process)) return new(false, null);
        string name = "Local\\BrimDeck.Ime." + Guid.NewGuid().ToString("N");
        using var mapping = MemoryMappedFile.CreateNew(name, Marshal.SizeOf<NativeRequest>(), MemoryMappedFileAccess.ReadWrite);
        using var view = mapping.CreateViewAccessor();
        var request = new NativeRequest
        {
            Magic = Magic, Version = 1, Window = (ulong)(long)target.Window, Thread = target.Thread,
            Command = profile is null ? 0u : 1u, Deadline = GetTickCount64() + 1000,
            Desired = profile is null ? default : new() { Type = profile.Type, Language = profile.Language,
                ClassId = profile.ClassId, ProfileId = profile.ProfileId, Layout = (ulong)(long)profile.Layout }
        };
        view.Write(0, ref request);
        ushort atom = GlobalAddAtom(name);
        if (atom == 0) return new(false, null, Marshal.GetLastWin32Error());
        nint hook = 0;
        try
        {
            hook = SetWindowsHookEx(4, NativeLibrary.GetExport(Module.Value, "BrimDeckImeHook"), Module.Value, target.Thread);
            if (hook == 0) return new(false, null, Marshal.GetLastWin32Error());
            if (!IsCurrent(target)) return new(false, null);
            nint sent = SendMessageTimeout(target.Window, Message, atom, (nint)Magic, 0x0002 | 0x0020, 1000, out _);
            view.Read(0, out request);
            string? active = request.Completed == 1 && request.Result >= 0 && request.Actual.Type != 0
                ? Id(request.Actual) : null;
            bool confirmed = sent != 0 && active is not null && IsCurrent(target)
                && (profile is null || string.Equals(active, profile.Id, StringComparison.OrdinalIgnoreCase));
            return new(confirmed, active, sent == 0 ? Marshal.GetLastWin32Error() : request.Result,
                sent != 0 && request.Completed == 1 && IsCurrent(target));
        }
        finally
        {
            if (hook != 0) UnhookWindowsHookEx(hook);
            GlobalDeleteAtom(atom);
        }
    }

    private static string Id(Identity p) => p.Type == 1
        ? $"tip:{p.Language:X4}:{p.ClassId:D}:{p.ProfileId:D}" : $"layout:{(uint)p.Layout:X8}";

    private static bool Is64Bit(uint process)
    {
        nint handle = OpenProcess(0x1000, false, process);
        if (handle == 0) return false;
        try { return IsWow64Process2(handle, out ushort machine, out ushort native) && machine == 0 && native == 0x8664; }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Identity { public uint Type, Language; public Guid ClassId, ProfileId; public ulong Layout; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRequest
    {
        public uint Magic, Version;
        public ulong Window;
        public uint Thread, Command;
        public ulong Deadline;
        public Identity Desired, Actual;
        public int Result, Completed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, nint callback, nint module, uint thread);
    [DllImport("user32")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32", EntryPoint = "SendMessageTimeoutW", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nuint result);
    [DllImport("kernel32", EntryPoint = "GlobalAddAtomW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort GlobalAddAtom(string value);
    [DllImport("kernel32")] private static extern ushort GlobalDeleteAtom(ushort atom);
    [DllImport("kernel32")] private static extern ulong GetTickCount64();
    [DllImport("kernel32")] private static extern nint OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32")] private static extern bool IsWow64Process2(nint process, out ushort machine, out ushort native);
    [DllImport("kernel32")] private static extern bool CloseHandle(nint handle);
}
