namespace BrimDeck.Core;

// A hold-to-cycle gesture: modifiers plus one key, or a chord made only of modifiers.
// For Ctrl+Shift, Ctrl is held and Shift is tapped; left/right variants are accepted.
public sealed record InputMethodBinding(HotkeyModifiers Hold, uint Trigger)
{
    public static HotkeyModifiers Modifier(uint key) => key switch
    {
        0x10 or 0xA0 or 0xA1 => HotkeyModifiers.Shift,
        0x11 or 0xA2 or 0xA3 => HotkeyModifiers.Ctrl,
        0x12 or 0xA4 or 0xA5 => HotkeyModifiers.Alt,
        0x5B or 0x5C => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None
    };

    public static bool TryParse(string? text, out HotkeyModifiers modifiers, out string key)
    {
        modifiers = HotkeyModifiers.None; key = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (string token in text.Split('+'))
        {
            string part = token.Trim();
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl, "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift, "win" or "windows" => HotkeyModifiers.Win, _ => HotkeyModifiers.None
            };
            if (modifier != HotkeyModifiers.None)
            {
                if (key.Length > 0 || modifiers.HasFlag(modifier)) return false;
                modifiers |= modifier;
            }
            else
            {
                if (key.Length > 0 || part.Length == 0 || !char.IsLetter(part[0]) || !part.All(char.IsLetterOrDigit)) return false;
                key = part;
            }
        }
        return modifiers != HotkeyModifiers.None && (key.Length > 0 || ModifierCount(modifiers) >= 2);
    }

    public static string Normalize(string? text) => TryParse(text, out var modifiers, out var key)
        ? HotkeyText.Format(modifiers, key).TrimEnd('+') : "";

    public static HotkeyModifiers TapModifier(HotkeyModifiers modifiers)
        => new[] { HotkeyModifiers.Shift, HotkeyModifiers.Alt, HotkeyModifiers.Ctrl, HotkeyModifiers.Win }.First(m => modifiers.HasFlag(m));

    private static int ModifierCount(HotkeyModifiers modifiers)
        => new[] { HotkeyModifiers.Ctrl, HotkeyModifiers.Alt, HotkeyModifiers.Shift, HotkeyModifiers.Win }.Count(m => modifiers.HasFlag(m));
}

public readonly record struct InputMethodRecordingDecision(bool Block, HotkeyModifiers Modifiers = 0, uint Key = 0, bool Complete = false, bool Cancel = false);

// Keys already held when recording starts pass through until released.
public sealed class InputMethodRecordingKeys
{
    private readonly bool[] _keys = new bool[256], _owned = new bool[256];
    private bool _waiting, _finished, _finishing, _cancel;
    private HotkeyModifiers _chord, _resultModifiers;
    private uint _resultKey;
    public void SeedHeldKey(uint key) { if (key < 256) { _keys[key] = true; _waiting = true; } }
    public InputMethodRecordingDecision Handle(uint key, bool down, bool injected = false)
    {
        if (injected || key >= 256) return default;
        bool repeated = _keys[key]; _keys[key] = down;
        if (_waiting) { _waiting = _keys.Any(k => k); return default; }
        if (_finished) return default;
        if (down) _owned[key] = true;
        bool block = _owned[key];
        var modifier = InputMethodBinding.Modifier(key);
        HotkeyModifiers held = 0;
        for (uint i = 0; i < 256; i++) if (_keys[i]) held |= InputMethodBinding.Modifier(i);
        if (!_finishing && down && !repeated)
        {
            if (modifier != 0) { _chord |= held; return new(block, held); }
            _finishing = true; _resultModifiers = held; _resultKey = key; _cancel = key == 0x1B && held == 0;
        }
        if (!_finishing && !down && modifier != 0 && _chord != 0 && (((int)_chord & ((int)_chord - 1)) != 0))
        { _finishing = true; _resultModifiers = _chord; }
        if (_finishing)
        {
            bool complete = !_keys.Any(k => k); _finished = complete;
            return new(block, _resultModifiers, _resultKey, Complete: complete, Cancel: complete && _cancel);
        }
        if (!down && held == 0) _chord = 0;
        return new(block, held);
    }
}
