namespace BrimDeck.Core;

// Turns verified foreground profiles into one notification after a stable change.
// The first observation is a silent baseline; key presses are never evidence of success.
public sealed class InputMethodChanges
{
    private string? _current, _candidate;
    private long _since;

    public string? Observe(string? profile, long milliseconds, bool defer = false)
    {
        if (string.IsNullOrWhiteSpace(profile)) { DropPending(); return null; }
        if (_current is null) { _current = profile; return null; }
        if (string.Equals(profile, _current, StringComparison.OrdinalIgnoreCase)) { DropPending(); return null; }
        if (!string.Equals(profile, _candidate, StringComparison.OrdinalIgnoreCase) || milliseconds < _since)
        { _candidate = profile; _since = milliseconds; return null; }
        if (defer || milliseconds - _since < 150) return null;
        _current = profile; DropPending(); return profile;
    }

    public void DropPending() { _candidate = null; _since = 0; }
    public void Reset() { _current = null; DropPending(); }
}
