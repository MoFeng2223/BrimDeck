using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using MediaState = BrimDeck.Core.MediaState;

namespace BrimDeck;

public partial class App
{
    // Screenshots for visual self-review of the music layout; only the seek time readout is asserted.
    private async Task VerifyMusicDesignAsync(string output, List<string> checks, MediaTrack song, BitmapSource cover, Lyrics lyric)
    {
        void Check(string name, bool value) => checks.Add((value ? "PASS " : "FAIL ") + name);
        IEnumerable<FrameworkElement> Views() => SettingsElements(Deck.PanelVisual).OfType<FrameworkElement>().Where(v => v.IsVisible);
        async Task Snap(string name) { await Task.Delay(60); Deck.UpdateLayout(); Capture(Deck.PanelVisual, Path.Combine(output, "design-" + name + ".png")); }
        var sizes = new[] { (440, 140), (520, 200), (760, 300), (770, 147), (551, 197), (551, 308), (1200, 140), (1200, 400), (440, 400), (520, 180), (520, 195) };
        foreach (var (width, height) in sizes)
        foreach (bool mode in new[] { false, true })
        {
            var settings = Settings.Copy(); settings.MusicWidth = width; settings.MusicHeight = height; UpdateSettings(settings);
            Deck.SetMediaForTest(song with { Shuffle = false, Repeat = MediaRepeat.List, CanShuffle = mode, CanRepeat = mode }, cover, lyric);
            await Snap($"{width}x{height}" + (mode ? "-mode" : "-transport"));
        }
        var standard = Settings.Copy(); standard.MusicWidth = 520; standard.MusicHeight = 200; UpdateSettings(standard);
        var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Deck.SetMediaForTest(song, cover, lyric); Deck.UpdateLayout();
        var progress = Views().OfType<MusicSeekSlider>().Single();
        progress.SetValue(hoverKey, true); await Snap("seek-hover");
        progress.Seeking = true; progress.Value = .62; await Snap("seek-drag");
        Check("Progress dragging shows the selected time", progress.BubbleText == "2:46");
        progress.Seeking = false; progress.SetValue(hoverKey, false); await Snap("seek-rest");
        Deck.SetMediaForTest(song with { CanSeek = false, CanPrevious = false }, cover, lyric); await Snap("unavailable-previous-seek");
        Deck.SetMediaForTest(song with { State = MediaState.Paused }, cover); await Snap("paused-no-lyrics");
        Deck.SetMediaForTest(song, null, lyric); await Snap("no-cover");
        var grey = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 112, 115, 110, 255 }, 4);
        Deck.SetMediaForTest(song, grey, lyric); await Snap("grey-cover");
        foreach (var mode in Enum.GetValues<MusicPlaybackMode>())
        {
            Deck.SetMediaForTest(song with { Shuffle = mode == MusicPlaybackMode.Shuffle, Repeat = mode == MusicPlaybackMode.Track ? MediaRepeat.Track : mode == MusicPlaybackMode.List ? MediaRepeat.List : MediaRepeat.None }, cover, lyric);
            await Snap("mode-" + mode);
        }
        var wide = Settings.Copy(); wide.MusicWidth = 760; wide.MusicHeight = 300; UpdateSettings(wide);
        Deck.SetMediaForTest(song with { IsNeteaseLog = true, CanSeek = false, CanRepeat = false, CanShuffle = false, CanToggle = false, CanNext = false, CanPrevious = false }, cover);
        await Snap("wide-log-mode");
    }
}
