using System.Runtime.InteropServices;
using System.Windows.Interop;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Global hotkeys registered on a message-only window that the app owns for its whole run. The panels are closed and opened
// again when monitors change, so their windows cannot hold the registrations; this window has no monitor, no position and
// no DPI, and Explorer restarting does not affect it. A hotkey may hold several combinations, told apart by their slot (the
// resize shortcut's four arrow keys).
//
// Plugin commands use the same window and take their registration ids from their slot in the caller's command list: the ids
// only have to be told apart for the lifetime of one registration pass, which is all the atom RegisterHotKey sees.
internal sealed class GlobalHotkeys : IDisposable
{
    // Slots per hotkey in the registration ids: room for more combinations than the four arrow keys need.
    private const int WmHotkey = 0x0312, ErrorHotkeyAlreadyRegistered = 1409, Slots = 8;
    // Command ids start here so they never collide with a HotkeyKind's ids.
    private const int CommandBase = 0x4000;
    private readonly HwndSource _source;
    private readonly HashSet<(HotkeyKind Kind, int Slot)> _registered = [];
    private readonly Dictionary<int, string> _commands = [];
    public event Action<HotkeyKind, int>? Pressed;
    // Raised with the command id whose combination was pressed. Commands carry no slot: a command owns one combination.
    public event Action<string>? CommandPressed;

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

    // Registers a command's combination. The caller passes one index per registered command, which addresses it in the hook.
    public HotkeyStatus RegisterCommand(int index, string commandId, uint fsModifiers, uint vk)
    {
        UnregisterCommand(index);
        if (!RegisterHotKey(_source.Handle, CommandBase + index, fsModifiers, vk)) return Marshal.GetLastWin32Error() == ErrorHotkeyAlreadyRegistered ? HotkeyStatus.InUse : HotkeyStatus.Invalid;
        _commands[CommandBase + index] = commandId;
        return HotkeyStatus.Active;
    }

    public void UnregisterCommand(int index)
    {
        if (_commands.Remove(CommandBase + index)) UnregisterHotKey(_source.Handle, CommandBase + index);
    }

    // Every command registration goes at once, before a new pass registers its own.
    public void UnregisterCommands()
    {
        foreach (int id in _commands.Keys.ToList()) UnregisterHotKey(_source.Handle, id);
        _commands.Clear();
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

    // The monitor under the mouse pointer, or the nearest one.
    public static IntPtr CursorMonitor() { GetCursorPos(out var point); return MonitorFromPoint(point, 2); }

    public void Dispose()
    {
        foreach (var (kind, slot) in _registered.ToList()) Unregister(kind, slot);
        UnregisterCommands();
        _source.RemoveHook(Hook); _source.Dispose();
    }

    private static int Id(HotkeyKind kind, int slot) => 0xB100 + (int)kind * Slots + slot;

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey) return IntPtr.Zero;
        int id = wParam.ToInt32();
        foreach (var (kind, slot) in _registered)
            if (id == Id(kind, slot)) { handled = true; Pressed?.Invoke(kind, slot); return IntPtr.Zero; }
        if (_commands.TryGetValue(id, out var command)) { handled = true; CommandPressed?.Invoke(command); }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
}
