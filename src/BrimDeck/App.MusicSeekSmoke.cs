using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;
using MediaState = BrimDeck.Core.MediaState;

namespace BrimDeck;

public partial class App
{
    private async Task VerifyMusicSeekAsync(string output, List<string> checks)
    {
        if (!Environment.GetCommandLineArgs().Contains("--media-live"))
            throw new InvalidOperationException("The QQ seek check requires --media-live.");
        void Check(string name, bool pass) => checks.Add((pass ? "PASS " : "FAIL ") + name);
        async Task<bool> Until(Func<bool> ready, int milliseconds = 6000)
        {
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
            while (!ready() && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
            return ready();
        }
        var settings = Settings.Copy(); settings.MusicPage = true; settings.Animations = false;
        UpdateSettings(settings); Deck.SetSnapshots(DemoData.Create()); Deck.Preview(true); Deck.SelectPage(DeckPage.Music);
        using var media = new MediaSessions(Dispatcher);
        await media.StartAsync();
        if (!await Until(() => media.Tracks.Any(t => t.Source == "QQ 音乐" && t.HasTimeline && t.CanSeek)))
            throw new InvalidOperationException("QQ Music has no available seekable track.");
        var original = media.Tracks.First(t => t.Source == "QQ 音乐" && t.HasTimeline && t.CanSeek);
        media.Select(original.Id); await media.RefreshAsync();
        var field = typeof(MainWindow).GetField("_media", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = field.GetValue(Deck);
        var trace = new List<string>();
        void Render() { Deck.ReceiveMediaForTest(media.Current, media.Cover); Deck.UpdateLayout(); }
        Slider Slider() => SettingsElements(Deck.PanelVisual).OfType<Slider>().Single(s => AutomationProperties.GetName(s) == "播放进度");
        async Task State(MediaState wanted)
        {
            if (media.Current?.State != wanted) await media.CommandAsync(original.Id, MediaCommand.PlayPause);
            if (!await Until(() => media.Current?.State == wanted)) throw new InvalidOperationException("QQ playback state was not confirmed.");
            Render();
        }
        async Task Seek(string name, double fraction)
        {
            var track = media.Current!;
            if (track.Id != original.Id || !MediaRouting.SameSong(track, original)) throw new InvalidOperationException("QQ changed songs during the seek check.");
            var slider = Slider();
            double target = TimeSpan.FromTicks(track.SeekTicks(fraction)).TotalSeconds;
            var start = DateTimeOffset.UtcNow;
            bool monitoring = false, confirmed = false, held = true, noRollback = true;
            int pendingSamples = 0;
            double confirmedShown = 0;
            void Sample()
            {
                if (!monitoring || media.Current is not { } current) return;
                double shown = Slider().Value * current.Duration.TotalSeconds;
                double raw = current.ReportedPosition.TotalSeconds;
                bool reached = current.PositionAt >= start && Math.Abs(raw - target) <= 2;
                if (!confirmed && reached) { confirmed = true; confirmedShown = shown; }
                if (!confirmed) { pendingSamples++; held &= Math.Abs(shown - target) < .05; }
                noRollback &= Math.Abs(shown - target) <= (DateTimeOffset.UtcNow - start).TotalSeconds + 2;
                trace.Add($"{name}: ms={(DateTimeOffset.UtcNow - start).TotalMilliseconds:F1}; raw={raw:F3}; shown={shown:F3}; target={target:F3}; confirmed={confirmed}");
            }
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) => Sample();
            RoutedPropertyChangedEventHandler<double> changed = (_, _) => Sample();
            slider.ValueChanged += changed;
            try
            {
                slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                slider.Value = fraction;
                start = DateTimeOffset.UtcNow; monitoring = true; timer.Start();
                slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                await Until(() => confirmed);
                await Task.Delay(700); Sample();
                Check(name + " holds the selected position until native confirmation", held && pendingSamples > 0);
                Check(name + " receives native confirmation without a rollback", confirmed && noRollback);
                if (track.State == MediaState.Playing)
                    Check(name + " resumes movement after native confirmation", Slider().Value * track.Duration.TotalSeconds > confirmedShown + .2);
                Capture(Deck.PanelVisual, Path.Combine(output, name + ".png"));
            }
            finally { monitoring = false; timer.Stop(); slider.ValueChanged -= changed; }
        }
        try
        {
            field.SetValue(Deck, media); media.Changed += Render; Render();
            await State(MediaState.Paused);
            await Seek("qq-paused-forward", .6);
            await Seek("qq-paused-backward", .25);
            await State(MediaState.Playing);
            await Seek("qq-playing-forward", .65);
            await Seek("qq-playing-backward", .3);
        }
        finally
        {
            if (media.Current is { HasTimeline: true } current && current.Id == original.Id && MediaRouting.SameSong(current, original))
            {
                await media.SeekAsync(current, original.Position(DateTimeOffset.UtcNow).TotalSeconds / original.Duration.TotalSeconds);
                await Until(() => media.Current is { } restored && Math.Abs((restored.ReportedPosition - original.Position(DateTimeOffset.UtcNow)).TotalSeconds) < 2);
                if (original.State is MediaState.Paused or MediaState.Playing) await State(original.State);
            }
            media.Changed -= Render; field.SetValue(Deck, previous);
            File.WriteAllLines(Path.Combine(output, "seek-trace.txt"), trace);
        }
    }
}
