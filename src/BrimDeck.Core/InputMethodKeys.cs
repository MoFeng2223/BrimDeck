namespace BrimDeck.Core;

public readonly record struct InputMethodKeyDecision(bool Block = false, bool Next = false, bool Commit = false,
    bool Cancel = false, bool MaskMenu = false, bool Begin = false);

// Every swallowed trigger-down owns its matching up, even after cancellation or modifier release.
public sealed class InputMethodKeys
{
    private readonly bool[] _keys = new bool[256];
    private readonly InputMethodBinding _binding;
    private HotkeyModifiers _hold;
    private uint _trigger;
    private bool _active, _triggerBlocked, _bypassHold;
    public InputMethodKeys(InputMethodBinding? binding = null)
    { _binding = binding ?? new(HotkeyModifiers.Win, 0x20); _hold = _binding.Hold; _trigger = _binding.Trigger; }
    private HotkeyModifiers Held
    {
        get { HotkeyModifiers held = 0; for (uint i = 0; i < 256; i++) if (_keys[i]) held |= InputMethodBinding.Modifier(i); return held; }
    }
    private bool Holding => (Held & _hold) == _hold;
    private bool Trigger(uint key) => InputMethodBinding.Modifier(_trigger) is var modifier && modifier != 0
        ? InputMethodBinding.Modifier(key) == modifier : key == _trigger;

    public void SeedHeldKey(uint key) { if (key < 256) _keys[key] = true; }
    public InputMethodKeyDecision Handle(uint key, bool down, bool canStart = true, bool injected = false)
    {
        if (injected || key >= 256) return default;
        bool alreadyDown = _keys[key]; _keys[key] = down;
        var chord = _binding.Hold | InputMethodBinding.Modifier(_binding.Trigger);
        // For modifier-only chords the last modifier pressed is tapped; the rest are held.
        if (InputMethodBinding.Modifier(_binding.Trigger) != 0 && !_active && !_triggerBlocked && !_bypassHold
            && down && !alreadyDown && InputMethodBinding.Modifier(key) != 0 && Held == chord)
        { _trigger = key; _hold = chord & ~InputMethodBinding.Modifier(key); }
        bool trigger = Trigger(key);
        if (!Holding)
        {
            _bypassHold = false;
            if (_active)
            {
                _active = false;
                if (trigger && !down) { bool owned = _triggerBlocked; _triggerBlocked = false; return new(Block: owned, Commit: true); }
                return new(Commit: true);
            }
        }
        if (trigger)
        {
            if (!down) { bool owned = _triggerBlocked; _triggerBlocked = false; return new(Block: owned); }
            if (_triggerBlocked) return new(Block: true);
            var triggerModifier = InputMethodBinding.Modifier(_trigger);
            bool exact = Held == (_hold | triggerModifier);
            if (Holding && exact && !alreadyDown && (_active || canStart) && !_bypassHold)
            {
                bool first = !_active; _active = _triggerBlocked = true;
                return new(Block: true, Next: true, Begin: first, MaskMenu: first && (Held & (HotkeyModifiers.Win | HotkeyModifiers.Alt)) != 0);
            }
            if (Holding) _bypassHold = true;
            return default;
        }
        if (down && _active && (InputMethodBinding.Modifier(key) & _hold) == 0)
        { _active = false; _bypassHold = Holding; return new(Cancel: true); }
        return default;
    }
    public void AbortUnblockedSpace() { _active = _triggerBlocked = false; _bypassHold = Holding; }
    public void Cancel() { _active = false; _bypassHold = Holding; }
}
