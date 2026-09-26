using System.Windows;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    // Drives compact hover through cursor positions only; window mouse messages are disabled for the whole run.
    private async Task VerifyHoverTrackingAsync(List<string> checks)
    {
        var saved = Settings.Copy();
        var hoverZone = (UIElement)Deck.FindName("HoverZone");
        bool hitTestVisible = hoverZone.IsHitTestVisible;
        var position = Deck.PointerPosition;
        var away = new Point(-10000, -10000);
        var cursor = away;
        void Check(string name, bool value) => checks.Add((value ? "PASS " : "FAIL ") + name);
        // Waits for the expected state; the result also records how long it took, so slow runs are distinguishable from wrong ones.
        var slowest = 0L;
        async Task<bool> Until(bool expanded, int limit = 1500)
        {
            var start = Environment.TickCount64;
            while (Deck.IsExpanded != expanded && Environment.TickCount64 - start < limit) await Task.Delay(10);
            slowest = Math.Max(slowest, Environment.TickCount64 - start);
            return Deck.IsExpanded == expanded;
        }
        try
        {
            hoverZone.IsHitTestVisible = false;
            Deck.PointerPosition = () => cursor;
            Deck.PointerTracking = true;
            // Hover size of each compact style in DIPs: the notch and the capsule box from the screen edge, the strip over the indicator.
            foreach (var (style, width, height) in new[] { (CompactStyle.Notch, 238d, 32d), (CompactStyle.Capsule, 196d, 37d), (CompactStyle.Line, 72d, 10d) })
            {
                var s = Settings.Copy(); s.Style = style; s.OpenDelay = 120; s.CloseDelay = 150; s.Animations = false;
                cursor = away; UpdateSettings(s); Deck.TestContext(ScreenContext.Desktop); Deck.SetExpanded(false, true); await Task.Delay(120);
                double center = Deck.SurfaceVisual.ActualWidth / 2;
                Point Screen(double x, double y) => Deck.SurfaceVisual.PointToScreen(new Point(x, y));
                Point[] edges = [Screen(center - width / 2 + 1, 0.5), Screen(center + width / 2 - 1, height - 1), Screen(center - width / 2 + 1, height - 1)];
                bool opened = true, closed = true;
                foreach (var edge in edges)
                {
                    cursor = edge; opened &= await Until(true);
                    cursor = away; closed &= await Until(false);
                }
                Check($"{style} edge hover opens from each corner of its box", opened);
                Check($"{style} panel closes after the cursor jumps away without mouse messages", closed);
                cursor = Screen(center + width / 2 + 3, height / 2); await Task.Delay(320);
                Check($"{style} just outside the box does not open", !Deck.IsExpanded);
                // Rapid entries and exits, then resting at the edge, must still open once the delay passes.
                for (int i = 0; i < 8; i++) { cursor = i % 2 == 0 ? edges[0] : away; await Task.Delay(30); }
                cursor = edges[1];
                Check($"{style} opens after rapid entries and exits", await Until(true));
                // Collapsing with the cursor still on the surface waits for a new entry, as before. The panel may have
                // opened during the rapid phase while a check last saw the cursor outside; let checks see it resting first.
                await Task.Delay(100);
                Deck.SetExpanded(false, true); await Task.Delay(320);
                Check($"{style} stays collapsed while the cursor has not left", !Deck.IsExpanded);
                cursor = away; await Task.Delay(120); cursor = edges[0];
                Check($"{style} reopens after leaving and entering again", await Until(true));
                cursor = away; await Until(false);
            }
            checks.Add($"INFO slowest hover transition {slowest} ms (open delay 120 ms, close delay 150 ms)");
            // Far from the surface the checks slow down; near it they return to the short interval.
            var near = Settings.Copy(); near.Style = CompactStyle.Notch; near.OpenDelay = 120; near.Animations = false; near.Borderless = WindowBehavior.Hide;
            cursor = away; UpdateSettings(near); Deck.TestContext(ScreenContext.Desktop); Deck.SetExpanded(false, true); await Task.Delay(400);
            var far = Deck.PointerTrackInterval;
            cursor = Deck.SurfaceVisual.PointToScreen(new Point(Deck.SurfaceVisual.ActualWidth / 2, 60)); await Task.Delay(400);
            Check($"checks slow to {far.TotalMilliseconds:0} ms far away and return to {Deck.PointerTrackInterval.TotalMilliseconds:0} ms near the surface",
                far.TotalMilliseconds == 160 && Deck.PointerTrackInterval.TotalMilliseconds == 40 && !Deck.IsExpanded);
            // Hidden, the surface cannot be hovered, so the checks stop; showing it again resumes them.
            Deck.TestContext(ScreenContext.Borderless); bool stopped = !Deck.PointerTracking;
            Deck.TestContext(ScreenContext.Desktop);
            Check("checks stop while the surface is hidden and resume when it returns", stopped && Deck.PointerTracking);
            var times = new[] { new DateTime(2026, 9, 26, 14, 30, 0, 0), new DateTime(2026, 9, 26, 14, 30, 59, 990), new DateTime(2026, 9, 26, 14, 30, 17, 250) };
            Check("clock updates just after each minute boundary", times.All(t => { var next = t + BrimDeck.MainWindow.ClockDelay(t); return next.Second == 0 && next.Millisecond is >= 40 and <= 60 && next > t; }));
        }
        finally
        {
            Deck.PointerTracking = false; Deck.PointerPosition = position;
            hoverZone.IsHitTestVisible = hitTestVisible;
            UpdateSettings(saved); Deck.SetExpanded(false, true);
        }
    }
}
