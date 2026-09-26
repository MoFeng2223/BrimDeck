using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task VerifyMusicMarqueeAsync(string output, List<string> checks)
    {
        void Check(string name, bool pass) => checks.Add((pass ? "PASS " : "FAIL ") + name);
        var settings = Settings.Copy();
        settings.Style = CompactStyle.Capsule; settings.MusicPage = true; settings.CapsuleMusic = true;
        settings.CapsuleSummary = false; settings.MusicText = CompactMusicText.Lyrics;
        settings.LyricsEnabled = true; settings.Animations = true; settings.MusicTrackNotice = false;
        UpdateSettings(settings); Deck.Preview(true, false);
        var song = new MediaTrack { Id = "marquee-fixture", Source = "网易云音乐", Title = "滚动检查", State = Core.MediaState.Playing,
            SongId = "netease:marquee-fixture", End = TimeSpan.FromSeconds(90), Rate = 0,
            EmbeddedLyrics = "[00:00]月弯弯 满腹愁无处话凄凉 这一句歌词用于检查持续滚动是否平滑\n[01:00]切换后的另一句长歌词应当从开头开始显示并平滑滚动\n[01:15]短句" };
        Deck.ReceiveMediaForTest(song); Deck.UpdateLayout();
        // Do not let the user's existing topmost instance occlude the test window.
        Deck.Top = 120;
        var marquee = SettingsElements(Deck.PanelVisual).OfType<MusicMarquee>().Single(v => v.IsVisible);
        async Task<bool> Until(Func<bool> condition, int timeout = 2500)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition() && deadline.ElapsedMilliseconds < timeout) await Task.Delay(20);
            return condition();
        }
        // Keep physical pointer movement from pausing the measured animation.
        marquee.IsHitTestVisible = false;

        double offset = MarqueeTextOrigin(marquee);
        Check("Overflowing compact lyrics scroll", await Until(() => MarqueeTextOrigin(marquee) < offset - 3, 4000));
        Capture(Deck.PanelVisual, Path.Combine(output, "compact-marquee.png"));
        offset = MarqueeTextOrigin(marquee);
        Deck.ReceiveMediaForTest(song); await Task.Delay(120);
        marquee = SettingsElements(Deck.PanelVisual).OfType<MusicMarquee>().Single(v => v.IsVisible);
        marquee.IsHitTestVisible = false;
        Check("Repeated playback updates do not restart the current lyric", MarqueeTextOrigin(marquee) <= offset + .01);

        marquee.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        await Task.Delay(350); offset = MarqueeTextOrigin(marquee); await Task.Delay(200);
        Check("Hover pauses the lyric without changing its position", Math.Abs(MarqueeTextOrigin(marquee) - offset) < .01);
        marquee.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        Check("Leaving hover resumes from the paused position", await Until(() => MarqueeTextOrigin(marquee) < offset - 3));

        marquee.SetScrolling(false); await Task.Delay(350);
        offset = MarqueeTextOrigin(marquee); await Task.Delay(200);
        Check("Disabling animations stops the lyric", Math.Abs(MarqueeTextOrigin(marquee) - offset) < .01);
        marquee.SetScrolling(true);
        Deck.ReceiveMediaForTest(song with { ReportedPosition = TimeSpan.FromSeconds(60) });
        Deck.UpdateLayout(); await Task.Delay(150);
        marquee = SettingsElements(Deck.PanelVisual).OfType<MusicMarquee>().Single(v => v.IsVisible);
        Check("A new lyric starts at its beginning", marquee.Text.StartsWith("切换后的另一句") && Math.Abs(MarqueeTextOrigin(marquee)) < .01);
        Capture(Deck.PanelVisual, Path.Combine(output, "compact-marquee-rest.png"));
        Check("A new long lyric scrolls after its reading pause", await Until(() => MarqueeTextOrigin(marquee) < -3));
        Deck.ReceiveMediaForTest(song with { ReportedPosition = TimeSpan.FromSeconds(75) });
        Deck.UpdateLayout(); await Task.Delay(100);
        marquee = SettingsElements(Deck.PanelVisual).OfType<MusicMarquee>().Single(v => v.IsVisible);
        offset = MarqueeTextOrigin(marquee); await Task.Delay(200);
        Check("Short lyrics do not scroll", marquee.Text == "短句" && Math.Abs(MarqueeTextOrigin(marquee) - offset) < .01);
        Capture(Deck.PanelVisual, Path.Combine(output, "compact-short-lyric.png"));
    }

    // Inspect the text actually submitted to WPF, including animated drawing transforms.
    private static double MarqueeTextOrigin(MusicMarquee marquee)
    {
        static Point? Origin(Drawing? drawing)
        {
            if (drawing is GlyphRunDrawing glyph) return glyph.GlyphRun.BaselineOrigin;
            if (drawing is DrawingGroup group)
                foreach (var child in group.Children)
                    if (Origin(child) is { } point) return group.Transform?.Transform(point) ?? point;
            return null;
        }
        return Origin(VisualTreeHelper.GetDrawing(marquee))?.X ?? double.NaN;
    }
}
