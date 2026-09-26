using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Native;
using MediaState = BrimDeck.Core.MediaState;

namespace BrimDeck;

public partial class App
{
    // Manual check: waits for a real click on the previous/next button, then captures the settled panel.
    private async Task VerifyMusicHoverAsync(string output, List<string> checks)
    {
        var args = Environment.GetCommandLineArgs();
        bool live = args.Contains("--media-live"), previous = args.Contains("--hover-previous");
        string name = previous ? "上一首" : "下一首";
        var settings = Settings.Copy(); settings.MusicPage = true; settings.Animations = true; settings.LyricsEnabled = true;
        settings.MusicWidth = 520; settings.MusicHeight = 200; settings.MusicTrackNotice = false;
        UpdateSettings(settings); Deck.SetSnapshots(DemoData.Create()); Deck.Preview(true); Deck.SelectPage(DeckPage.Music);
        Deck.Title = "BrimDeck hover check";
        Deck.ShowInTaskbar = true; Deck.Activate();
        var field = typeof(MainWindow).GetField("_media", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var saved = field.GetValue(Deck);
        using var media = new MediaSessions(Dispatcher, connectPlayers: live);
        async Task<bool> Until(Func<bool> ready, int milliseconds = 8000)
        {
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
            while (!ready() && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
            return ready();
        }
        void Render() { Deck.ReceiveMediaForTest(media.Current, media.Cover); Deck.UpdateLayout(); }
        var fixture = new MediaTrack { Id = "hover-fixture", Source = "QQ 音乐", Title = "Hover fixture", Artist = "Local", SongId = "qq:hover-a",
            State = MediaState.Playing, CanNext = true, CanPrevious = true, CanToggle = true, CanSeek = true, End = TimeSpan.FromSeconds(200) };
        if (live)
        {
            await media.StartAsync();
            string source = args.Contains("--hover-netease") ? "网易云音乐" : "QQ 音乐";
            if (!await Until(() => media.Tracks.Any(t => t.Source == source && (previous ? t.CanPrevious : t.CanNext))))
                throw new InvalidOperationException(source + " has no available transport.");
            media.Select(media.Tracks.First(t => t.Source == source).Id); await media.RefreshAsync();
            field.SetValue(Deck, media); media.Changed += Render; Render();
        }
        else Deck.SetMediaForTest(fixture);
        var original = Deck.CurrentMedia!;
        var clicked = new TaskCompletionSource();
        bool started = false;
        RoutedEventHandler click = (_, e) =>
        {
            if (e.OriginalSource is Button button && AutomationProperties.GetName(button) == name)
            { started = true; clicked.TrySetResult(); }
        };
        Deck.AddHandler(Button.ClickEvent, click, true);
        try
        {
            File.WriteAllText(Path.Combine(output, "status.txt"), "READY " + name);
            await clicked.Task.WaitAsync(TimeSpan.FromSeconds(120));
            File.WriteAllText(Path.Combine(output, "status.txt"), "OBSERVING " + name);
            if (!live)
            {
                var next = fixture with { Title = "Next fixture", SongId = "qq:hover-b", PositionKnown = false, CanSeek = false, TimelinePending = true };
                await Task.Delay(250); Deck.ReceiveMediaForTest(next);
                await Task.Delay(250); next = next with { State = MediaState.Paused }; Deck.ReceiveMediaForTest(next);
                await Task.Delay(250); next = next with { State = MediaState.Playing, PositionKnown = true, CanSeek = true, TimelinePending = false }; Deck.ReceiveMediaForTest(next);
                await Task.Delay(250); next = next with { CurrentLyric = "New lyric" }; Deck.ReceiveMediaForTest(next);
            }
            await Task.Delay(4500);
            checks.Add((Deck.CurrentMedia?.Title != original.Title ? "PASS " : "FAIL ") + name + " changes the song");
            Capture(Deck.PanelVisual, Path.Combine(output, "settled.png"));
        }
        finally
        {
            Deck.RemoveHandler(Button.ClickEvent, click);
            if (live && started && media.Current is { } current && current.Id == original.Id && current.Title != original.Title)
            {
                await media.CommandAsync(original.Id, previous ? MediaCommand.Next : MediaCommand.Previous);
                await Until(() => media.Current?.Title == original.Title);
            }
            if (live && media.Current is { HasTimeline: true } restored && restored.Title == original.Title)
            {
                await media.SeekAsync(restored, original.Position(DateTimeOffset.UtcNow).TotalSeconds / original.Duration.TotalSeconds);
                if (restored.State != original.State && original.State is MediaState.Playing or MediaState.Paused)
                { await media.CommandAsync(original.Id, MediaCommand.PlayPause); await Until(() => media.Current?.State == original.State); }
            }
            media.Changed -= Render; field.SetValue(Deck, saved);
        }
    }
}
