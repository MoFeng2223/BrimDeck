using BrimDeck.Core;

internal static class RollingNumbersTests
{
    internal static void Run(Action<string, bool> check)
    {
        var nine = RollingNumbers.Places(9); var ten = RollingNumbers.Places(10);
        check("Rolling: numeric carry has an unwrapped ones count", nine[^1].Count == 9 && ten[^1].Count == 10 && ten[0].Digit == 1 && ten[^1].Digit == 0);
        var before = RollingNumbers.Places(59, true); var after = RollingNumbers.Places(60, true);
        check("Rolling: seconds use a base-six tens wheel", before[^2] is { Count: 5, Radix: 6 } && after[^2] is { Count: 6, Digit: 0 } && after[^3].Digit == 1);
        var hour = RollingNumbers.Places(3600, true);
        check("Rolling: hour carry renders 1:00:00", hour.Count == 5 && hour.Select(p => p.Digit).SequenceEqual([1, 0, 0, 0, 0]) && hour.Count(p => p.SeparatorAfter) == 2);
        var padded = RollingNumbers.Places(59, true, 4);
        check("Rolling: reserved time digits keep stable layout", padded.Count == 4 && padded.Select(p => p.Digit).SequenceEqual([0, 0, 5, 9]));
        check("Rolling: a zero counter keeps a visible digit", RollingNumbers.Places(0).Count == 1 && RollingNumbers.Places(-10)[0].Digit == 0);
        var forward = new RollingWheel(9); forward.Set(10, 0, 0, true); forward.Step(1 / 60d, .02);
        check("Rolling: nine-to-zero moves forward", forward.Position > 9 && forward.Velocity > 0);
        var backward = new RollingWheel(10); backward.Set(9, 0, 0, true); backward.Step(1 / 60d, .02);
        check("Rolling: zero-to-nine borrow moves backward", backward.Position < 10 && backward.Velocity < 0);
        var stagger = new RollingWheel(0); stagger.Set(1, 0, .07, true); stagger.Step(1 / 60d, .02);
        check("Rolling: carry waits for its stagger", stagger.Position == 0);
        stagger.Step(1 / 60d, .08); check("Rolling: delayed carry starts", stagger.Position > 0);
        for (int i = 0; i < 200; i++) stagger.Step(1 / 60d, .1 + i / 60d);
        check("Rolling: spring settles and rendering can stop", stagger.Position == 1 && stagger.Velocity == 0 && !stagger.Step(1 / 60d, 5));
        var latest = new RollingWheel(); latest.Set(1, 0, .1, true); latest.Set(9, .02, .1, true);
        for (int i = 0; i < 200; i++) latest.Step(1 / 60d, i / 60d);
        check("Rolling: rapid targets settle on the latest value", latest.Position == 9 && latest.Count == 9);
        latest.Set(0, 5, .1, false);
        check("Rolling: reduced motion clears all pending motion", latest.Position == 0 && latest.Velocity == 0 && !latest.Step(1 / 60d, 6));
        var leap = new RollingWheel(0); leap.Set(99999, 0, 0, true); leap.Step(1 / 60d, .02);
        check("Rolling: large changes skip equivalent whole revolutions", leap.Position > 99000 && Math.Abs(leap.Position - leap.Target) < 30);
        var six = new RollingWheel(5, 6); six.Set(6, 0, 0, true);
        for (int i = 0; i < 200; i++) six.Step(1 / 60d, i / 60d);
        check("Rolling: base-six wheel settles without a reverse sweep", six.Position == 6 && six.Count % six.Radix == 0);
    }
}
