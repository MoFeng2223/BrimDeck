namespace BrimDeck.Core;

public readonly record struct RollingPlace(long Count, int Radix, bool SeparatorAfter)
{
    public int Digit => (int)(Count % Radix);
}

public static class RollingNumbers
{
    // Time has a base-six tens-of-seconds/minutes wheel. Unwrapped counts make both
    // carry and borrow cross zero in the same direction as the actual elapsed time.
    public static IReadOnlyList<RollingPlace> Places(long value, bool clock = false, int minimumDigits = 1)
    {
        value = Math.Clamp(value, 0, clock ? 359999 : 999999);
        long[] units = clock ? [1, 10, 60, 600, 3600, 36000] : [1, 10, 100, 1000, 10000, 100000];
        int shown = Math.Clamp(minimumDigits, clock ? 3 : 1, units.Length);
        while (shown < units.Length && value >= units[shown]) shown++;
        var places = new List<RollingPlace>();
        for (int i = shown - 1; i >= 0; i--)
            places.Add(new(value / units[i], clock && i is 1 or 3 ? 6 : 10, clock && i is 2 or 4));
        return places;
    }
}

// Adapted from the supplied counter's per-wheel spring. Coalesce delayed targets so
// scrubbing cannot leave a queue of old values playing after the pointer stops.
public sealed class RollingWheel
{
    public double Position { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }
    public long Count { get; private set; }
    public int Radix { get; private set; } = 10;
    private double _due;
    private bool _pending;
    public RollingWheel(long count = 0, int radix = 10) { Radix = Math.Clamp(radix, 2, 10); Snap(count); }
    public void Set(long count, double now, double delay, bool animate)
    {
        if (!animate) { Snap(count); return; }
        if (count == Count) return;
        Count = count; _due = now + Math.Max(0, delay); _pending = true;
    }
    public void Snap(long count)
    { Count = count; Position = Target = count; Velocity = 0; _pending = false; }
    public bool Step(double seconds, double now)
    {
        if (_pending && now >= _due) { Target = Count; _pending = false; }
        double gap = Target - Position;
        if (Math.Abs(gap) > 24) Position += Math.Round((gap - Math.Sign(gap) * 24) / Radix) * Radix;
        double dt = Math.Clamp(seconds * 60, 0, 2.5);
        Velocity += (Target - Position) * .152 * dt;
        Velocity *= Math.Pow(.71, dt);
        Position += Velocity * dt;
        if (Math.Abs(Target - Position) < .004 && Math.Abs(Velocity) < .004)
        { Position = Target; Velocity = 0; return _pending; }
        return true;
    }
}
