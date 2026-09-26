using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task VerifyQuotaCarouselAsync(string output, List<string> checks)
    {
        void Check(string name, bool pass) => checks.Add((pass ? "PASS " : "FAIL ") + name);
        var settings = Settings.Copy();
        settings.Style = CompactStyle.Capsule; settings.MusicPage = true; settings.CapsuleMusic = true; settings.CapsuleSummary = true;
        settings.MusicText = CompactMusicText.Title; settings.Animations = true; settings.MusicTrackNotice = false;
        settings.Apps = Enumerable.Range(0, 5).Select(i => new AppEntry { QuotaSource = ProviderCatalog.BuiltIns[i % 4], DisplayName = "应用 " + (i + 1) }).ToList();
        UpdateSettings(settings); Deck.SetSnapshots(DemoData.Create()); Deck.Preview(true, false);
        var song = new MediaTrack { Id = "carousel-fixture", Source = "网易云音乐", Title = "Little by Little", Artist = "测试", State = Core.MediaState.Playing, End = TimeSpan.FromSeconds(200), Rate = 0 };
        Deck.ReceiveMediaForTest(song); Deck.UpdateLayout();
        Deck.Top = 120;
        var layout = Deck.CurrentCompactLayout!;
        QuotaCarousel Current() => SettingsElements(Deck.PanelVisual).OfType<QuotaCarousel>().Single(v => v.IsVisible);
        var stripField = typeof(QuotaCarousel).GetField("_strip", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Canvas Strip(QuotaCarousel c) => (Canvas)stripField.GetValue(c)!;
        double Shift(QuotaCarousel c) => ((TranslateTransform)Strip(c).RenderTransform).X;
        double travel = Current().Travel;
        Check("Five rings overflow and scroll", layout.ScrollRings && layout.VisibleRings is > 0 and < 5);

        async Task<List<(double Time, double X, double Opacity)>> Record(double seconds)
        {
            var frames = new List<(double, double, double)>();
            TimeSpan? previous = null; var watch = Stopwatch.StartNew();
            void Sample(object? sender, EventArgs e)
            {
                var time = ((RenderingEventArgs)e).RenderingTime;
                if (time == previous) return; previous = time;
                var c = Current();
                frames.Add((watch.Elapsed.TotalSeconds, Shift(c), Strip(c).Opacity));
            }
            CompositionTarget.Rendering += Sample;
            try { await Task.Delay(TimeSpan.FromSeconds(seconds)); }
            finally { CompositionTarget.Rendering -= Sample; }
            return frames;
        }

        // Start from a fresh cycle once the setup has settled.
        await Task.Delay(1500);
        typeof(MainWindow).GetField("_quotaCarousel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Deck, null);
        typeof(MainWindow).GetField("_quotaElapsed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Deck, TimeSpan.Zero);
        Deck.RenderCompact(); Deck.UpdateLayout();
        await Task.Delay(300); Capture(Deck.PanelVisual, Path.Combine(output, "carousel-rest.png"));
        {
            // At rest the first and last visible rings lie outside the edge fades.
            var c = Current(); var rings = Strip(c).Children.Cast<FrameworkElement>().ToArray();
            double Left(FrameworkElement ring) => ring.TranslatePoint(new Point(), c).X;
            double tolerance = .01;
            Check("Resting rings are clear of the edge fades", Left(rings[0]) >= QuotaCarousel.FadeEdge - tolerance
                && Left(rings[layout.VisibleRings - 1]) + layout.RingSize <= c.ActualWidth - QuotaCarousel.FadeEdge + tolerance);
        }
        VerifyEdgeFadePixels(output, checks, layout);
        await Task.Delay(TimeSpan.FromSeconds(QuotaCarousel.StartHold + travel / QuotaCarousel.Speed + QuotaCarousel.EndHold / 2 - .3));
        Capture(Deck.PanelVisual, Path.Combine(output, "carousel-end.png"));
        {
            var c = Current(); var rings = Strip(c).Children.Cast<FrameworkElement>().ToArray();
            double Left(FrameworkElement ring) => ring.TranslatePoint(new Point(), c).X;
            double tolerance = .01;
            Check("At the end the last rings are clear of the edge fades", Left(rings[^layout.VisibleRings]) >= QuotaCarousel.FadeEdge - tolerance
                && Left(rings[^1]) + layout.RingSize <= c.ActualWidth - QuotaCarousel.FadeEdge + tolerance);
        }
        double cycle = QuotaCarousel.StartHold + travel / QuotaCarousel.Speed + QuotaCarousel.EndHold + 2 * QuotaCarousel.Fade;
        var frames = await Record(cycle + 3.2);
        File.WriteAllLines(Path.Combine(output, "carousel-frames.csv"), new[] { "seconds,shiftX,opacity" }.Concat(frames.Select(f => $"{f.Time:F3},{f.X:F3},{f.Opacity:F3}")));
        Check("The strip scrolls to the last ring and returns to the first", Math.Abs(frames.Min(f => f.X) + travel) < .01
            && frames.Zip(frames.Skip(1)).Any(p => p.Second.X > p.First.X + .001));

        await Task.Delay(1200);
        Capture(Deck.PanelVisual, Path.Combine(output, "carousel-scrolled.png"));

        Deck.SetExpanded(true, true); await Task.Delay(100);
        var hidden = Current().Elapsed; await Task.Delay(1000);
        Check("The expanded panel pauses the hidden carousel", Math.Abs((Current().Elapsed - hidden).TotalSeconds) < .05);
        Deck.SetExpanded(false, true); await Task.Delay(600);
        Check("Collapsing resumes the carousel", (Current().Elapsed - hidden).TotalSeconds > .3);
    }
}
