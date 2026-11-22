using System.Windows.Input;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// Temporary mouse pass-through while its hotkey is held (HotkeyKind.HoldClickThrough). It leaves the ClickThrough setting alone,
// which its own hotkey, the tray menu and the Compatibility switch turn on and off.
//
// A single key other than F1 to F24 (Esc, CapsLock, …) is not registered as a hotkey, so the program in use still receives it:
// GlobalHotkeys.WatchKey reports each press, and a press with a modifier held is left alone. Combinations, and F1 to F24 alone, are registered as usual. Either way the key is
// checked for its release only while the pass-through lasts; nothing is read between presses.
public partial class App
{
    private const int VkEscape = 0x1B;
    // VK_SHIFT, VK_CONTROL, VK_MENU (Alt), VK_LWIN, VK_RWIN.
    private static readonly int[] ModifierKeysDown = [0x10, 0x11, 0x12, 0x5B, 0x5C];
    private PromptTimer? _holdRelease;
    // The hotkey's main key, from the last registration; 0 while it is unset or invalid.
    private int _holdVk;

    // True while the hotkey is held. MainWindow.ClickThrough counts it as pass-through.
    internal bool HoldPassThrough { get; private set; }

    // A single key that is watched rather than registered.
    private static bool WatchesKey(ModifierKeys modifiers, Key key)
        => modifiers == ModifierKeys.None && !HotkeyText.IsFunctionKey(key.ToString());

    // Called instead of registering the hotkey.
    private HotkeyStatus WatchHoldKey(int vk)
        => _hotkeys is null || _hotkeySuspensions > 0 ? HotkeyStatus.Active : _hotkeys.WatchKey(vk) ? HotkeyStatus.Active : HotkeyStatus.Invalid;

    private void StopHoldClickThrough()
    {
        _hotkeys?.StopWatching();
        EndHoldPassThrough();
    }

    private void HoldKeyDown()
    {
        if (Exiting) return;
        // Pressed with Ctrl, Alt, Shift or Win it belongs to a combination (Ctrl+Shift+Esc, Shift+CapsLock), not to this hotkey.
        if (ModifierKeysDown.Any(PasteInput.KeyDown)) return;
        // The open panel or move mode has Esc for itself (MainWindow.ReservesEscape).
        if (_holdVk == VkEscape && Decks.Any(deck => deck.ReservesEscape)) return;
        BeginHoldPassThrough();
    }

    private void BeginHoldPassThrough()
    {
        if (HoldPassThrough || _holdVk == 0) return;
        HoldPassThrough = true;
        ApplyHoldPassThrough();
        _holdRelease ??= new PromptTimer(Dispatcher, () => { if (!PasteInput.KeyDown(_holdVk)) EndHoldPassThrough(); }) { Interval = TimeSpan.FromMilliseconds(30) };
        _holdRelease.Start();
    }

    private void EndHoldPassThrough()
    {
        _holdRelease?.Stop();
        if (!HoldPassThrough) return;
        HoldPassThrough = false;
        ApplyHoldPassThrough();
    }

    private void ApplyHoldPassThrough()
    {
        foreach (var deck in Decks.ToList()) deck.RefreshHoldPassThrough();
    }
}
