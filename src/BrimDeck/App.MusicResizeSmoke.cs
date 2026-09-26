using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using MediaState = BrimDeck.Core.MediaState;

namespace BrimDeck;

public partial class App
{
    // Continuous resizing of the music page with animations enabled; screenshots are kept for visual review.
    private async Task VerifyMusicResizeAsync(string output, List<string> checks)
    {
        void Check(string name, bool pass) => checks.Add((pass ? "PASS " : "FAIL ") + name);
        var settings = Settings.Copy(); settings.MusicPage = true; settings.Animations = true; settings.LyricsEnabled = false;
        UpdateSettings(settings); Deck.SetSnapshots(DemoData.Create()); Deck.SelectPage(DeckPage.Music); Deck.Preview(true);
        Deck.PanelVisual.IsHitTestVisible = false;
        var cover = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 95, 145, 40, 255 }, 4); cover.Freeze();
        var song = new MediaTrack { Id = "resize-fixture", Source = "网易云音乐", Title = "进度条颜色检查", Artist = "连续调整尺寸",
            State = MediaState.Paused, End = TimeSpan.FromSeconds(200), ReportedPosition = TimeSpan.FromSeconds(150),
            PositionAt = DateTimeOffset.UtcNow, Rate = 0, CanToggle = true, CanPrevious = true, CanNext = true };
        foreach (bool seekable in new[] { false, true })
        foreach (string axis in new[] { "width", "height", "both" })
        {
            string name = (seekable ? "enabled-" : "disabled-") + axis;
            Deck.SetMediaForTest(song with { CanSeek = seekable }, cover);
            for (int step = 0; step < 18; step++)
            {
                var next = Settings.Copy(); next.MusicWidth = axis == "height" ? 700 : 440 + step * 28;
                next.MusicHeight = axis == "width" ? 300 : 140 + step * 14;
                UpdateSettings(next); Deck.UpdateLayout();
                if (step == 0) Capture(Deck.PanelVisual, Path.Combine(output, name + "-initial.png"));
                await Task.Delay(20);
                if (step == 8) Capture(Deck.PanelVisual, Path.Combine(output, name + "-resizing.png"));
            }
            await Task.Delay(180); Capture(Deck.PanelVisual, Path.Combine(output, name + "-settled.png"));
            Check(name + " keeps one progress control that matches seek availability",
                SettingsElements(Deck.PanelVisual).OfType<MusicSeekSlider>().Where(s => s.IsVisible).ToArray() is [var progress] && progress.IsEnabled == seekable);
        }
    }
}
