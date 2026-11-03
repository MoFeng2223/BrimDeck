using System.Windows.Input;
using BrimDeck.Core;

namespace BrimDeck;

// Uses the app's existing key naming/formatting for ordinary chords, adding modifier-only chords.
internal static class InputMethodGesture
{
    internal static bool TryParse(string? text, out InputMethodBinding binding, out string canonical)
    {
        binding = new(HotkeyModifiers.Win, 0x20); canonical = "";
        if (!InputMethodBinding.TryParse(text, out var modifiers, out string name)) return false;
        if (name.Length == 0)
        {
            var tap = InputMethodBinding.TapModifier(modifiers);
            uint trigger = tap switch { HotkeyModifiers.Shift => 0xA0, HotkeyModifiers.Alt => 0xA4, HotkeyModifiers.Ctrl => 0xA2, _ => 0x5B };
            binding = new(modifiers & ~tap, trigger); canonical = InputMethodBinding.Normalize(text); return true;
        }
        if (!HotkeyGesture.TryParse(text!, out var wpf, out var key)) return false;
        // Avoid ordinary typing and combinations reserved for session/security or window management.
        if ((modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0
            || (modifiers.HasFlag(HotkeyModifiers.Win) && key == Key.L)
            || (modifiers.HasFlag(HotkeyModifiers.Ctrl) && modifiers.HasFlag(HotkeyModifiers.Alt) && key == Key.Delete)
            || (modifiers.HasFlag(HotkeyModifiers.Ctrl) && modifiers.HasFlag(HotkeyModifiers.Shift) && key == Key.Escape)
            || (modifiers.HasFlag(HotkeyModifiers.Alt) && key is Key.Tab or Key.F4)) return false;
        binding = new(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        canonical = HotkeyGesture.Format(wpf, key); return true;
    }

    internal static string Recorded(InputMethodRecordingDecision decision)
    {
        if (decision.Key == 0) return HotkeyText.Format(decision.Modifiers, "").TrimEnd('+');
        return HotkeyText.Format(decision.Modifiers, KeyInterop.KeyFromVirtualKey((int)decision.Key).ToString());
    }

    internal static string Display(string text) => text.Replace("Space", Loc.T("空格", "Space"));
    internal static string Hint(string text)
    {
        if (!TryParse(text, out var binding, out _)) return Loc.T("请先录制快捷键", "Record a shortcut first");
        string hold = HotkeyText.Format(binding.Hold, "").TrimEnd('+');
        var tapModifier = InputMethodBinding.Modifier(binding.Trigger);
        string tap = tapModifier != 0 ? HotkeyText.Format(tapModifier, "").TrimEnd('+')
            : binding.Trigger == 0x20 ? Loc.T("空格", "Space") : KeyInterop.KeyFromVirtualKey((int)binding.Trigger).ToString();
        return Loc.T($"按住 {hold}，点按 {tap} 切换，松开确认", $"Hold {hold} · Tap {tap} to cycle · Release to apply");
    }
}
