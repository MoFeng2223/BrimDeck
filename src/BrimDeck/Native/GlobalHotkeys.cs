using System.Runtime.InteropServices;
using System.Windows.Interop;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Global hotkeys registered on a message-only window that the app owns for its whole run. The panels are closed and opened
// again when monitors change, so their windows cannot hold the registrations; this window has no monitor, no position and
// no DPI, and Explorer restarting does not affect it. A hotkey may hold several combinations, told apart by their slot (the
// resize shortcut's four arrow keys).
//
// A single key that must still reach the program in use (WatchKey) is not registered as a hotkey: the window asks for the
// keyboard's raw input in the background (RIDEV_INPUTSINK) and is told of each key press, which takes nothing from other
// programs. Nothing is read between presses.
internal sealed class GlobalHotkeys : IDisposable
{
    // Slots per hotkey in the registration ids: room for more combinations than the four arrow keys need.
    private const int WmHotkey = 0x0312, WmInput = 0x00FF, ErrorHotkeyAlreadyRegistered = 1409, Slots = 8;
    private const uint RidevRemove = 0x1, RidevInputSink = 0x100, RidInput = 0x10000003, RimTypeKeyboard = 1;
    private const ushort RiKeyBreak = 1;
    private readonly HwndSource _source;
    private readonly HashSet<(HotkeyKind Kind, int Slot)> _registered = [];
    private int _watchedKey;
    public event Action<HotkeyKind, int>? Pressed;
    // The watched key went down (auto-repeat while it is held included).
    public event Action? WatchedKeyDown;

    public GlobalHotkeys()
    {
        _source = new HwndSource(new HwndSourceParameters("BrimDeck.Hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 }); // HWND_MESSAGE
        _source.AddHook(Hook);
    }

    // Registers a combination given as RegisterHotKey's values; Active, InUse when another program holds it, Invalid otherwise.
    public HotkeyStatus Register(HotkeyKind kind, uint fsModifiers, uint vk, int slot = 0)
    {
        Unregister(kind, slot);
        if (RegisterHotKey(_source.Handle, Id(kind, slot), fsModifiers, vk)) { _registered.Add((kind, slot)); return HotkeyStatus.Active; }
        return Marshal.GetLastWin32Error() == ErrorHotkeyAlreadyRegistered ? HotkeyStatus.InUse : HotkeyStatus.Invalid;
    }

    // Every combination of the hotkey.
    public void Unregister(HotkeyKind kind)
    {
        foreach (var (registered, slot) in _registered.Where(entry => entry.Kind == kind).ToList()) Unregister(registered, slot);
    }

    private void Unregister(HotkeyKind kind, int slot)
    {
        if (_registered.Remove((kind, slot))) UnregisterHotKey(_source.Handle, Id(kind, slot));
    }

    // Watches a virtual-key code without taking it from other programs; false when Windows refuses the raw input registration.
    public bool WatchKey(int vk)
    {
        if (vk == 0) return false;
        if (_watchedKey == 0 && !RegisterKeyboard(RidevInputSink, _source.Handle)) return false;
        _watchedKey = vk;
        return true;
    }

    public void StopWatching()
    {
        if (_watchedKey == 0) return;
        _watchedKey = 0;
        RegisterKeyboard(RidevRemove, IntPtr.Zero);
    }

    // Generic desktop page (0x01), keyboard usage (0x06). With RIDEV_REMOVE the target window must be null.
    private static bool RegisterKeyboard(uint flags, IntPtr target)
    {
        var device = new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x06, Flags = flags, Target = target };
        return RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    // The key a WM_INPUT reports as pressed, or 0 for a release, another device or unreadable data.
    private static int PressedKey(IntPtr input)
    {
        int header = Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        // Asked first for the size, which differs by device.
        if (GetRawInputData(input, RidInput, null, ref size, (uint)header) != 0 || size < header + Marshal.SizeOf<RAWKEYBOARD>()) return 0;
        var buffer = new byte[size];
        if (GetRawInputData(input, RidInput, buffer, ref size, (uint)header) == uint.MaxValue) return 0;
        if (BitConverter.ToUInt32(buffer, 0) != RimTypeKeyboard) return 0;
        // RAWKEYBOARD: MakeCode, Flags, Reserved, VKey (16 bits each).
        ushort flags = BitConverter.ToUInt16(buffer, header + 2), vk = BitConverter.ToUInt16(buffer, header + 6);
        return (flags & RiKeyBreak) != 0 ? 0 : vk;
    }

    // The monitor under the mouse pointer, or the nearest one.
    public static IntPtr CursorMonitor() { GetCursorPos(out var point); return MonitorFromPoint(point, 2); }

    public void Dispose()
    {
        StopWatching();
        foreach (var (kind, slot) in _registered.ToList()) Unregister(kind, slot);
        _source.RemoveHook(Hook); _source.Dispose();
    }

    private static int Id(HotkeyKind kind, int slot) => 0xB100 + (int)kind * Slots + slot;

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_INPUT is left unhandled: DefWindowProc must still see it.
        if (message == WmInput)
        {
            if (_watchedKey != 0 && PressedKey(lParam) == _watchedKey) WatchedKeyDown?.Invoke();
            return IntPtr.Zero;
        }
        if (message != WmHotkey) return IntPtr.Zero;
        foreach (var (kind, slot) in _registered)
            if (wParam.ToInt32() == Id(kind, slot)) { handled = true; Pressed?.Invoke(kind, slot); break; }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }
    [DllImport("user32", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32")] private static extern uint GetRawInputData(IntPtr input, uint command, byte[]? data, ref uint size, uint headerSize);
    [DllImport("user32", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
}
