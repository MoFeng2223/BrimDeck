using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using MediaState = BrimDeck.Core.MediaState;

namespace BrimDeck;

public partial class App
{
    private async Task VerifyMusicAsync(string output, List<string> checks, bool live)
    {
        void Check(string name, bool value) => checks.Add((value ? "PASS " : "FAIL ") + name);
        VerifyMediaArtwork(checks);
        var settings = Settings.Copy(); settings.LyricsEnabled = true; settings.Animations = false; settings.MusicPage = true; settings.Style = CompactStyle.Notch; settings.MusicTrackNotice = false; UpdateSettings(settings);
        Deck.SetSnapshots(DemoData.Create()); Deck.Preview(true); Deck.SelectPage(DeckPage.Music);
        var now = DateTimeOffset.UtcNow;
        var song = new MediaTrack { Id = "fake-a", Source = "网易云音乐", Title = "晴天", Artist = "周杰伦", Album = "叶惠美", State = MediaState.Playing, End = TimeSpan.FromSeconds(269), ReportedPosition = TimeSpan.FromSeconds(102), PositionAt = now, Rate = 0,
            CanToggle = true, CanNext = true, CanPrevious = true, CanSeek = true, CanRepeat = true, CanShuffle = true, Shuffle = true };
        var lyric = Lyrics.Parse("[01:38]好想再问一遍 你会等待还是离开\n[01:42]刮风这天 我试过握着你手\n[01:46]但偏偏 雨渐渐 大到我看你不见", null, 269, "测试歌词");
        var art = new DrawingVisual(); using (var dc = art.RenderOpen()) { dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(25, 56, 70), Color.FromRgb(111, 193, 174), 45), null, new Rect(0, 0, 256, 256)); dc.DrawEllipse(new RadialGradientBrush(Color.FromRgb(222, 213, 176), Colors.Transparent), null, new Point(58, 50), 80, 80); }
        var cover = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32); cover.Render(art); cover.Freeze();
        IEnumerable<FrameworkElement> Views() => SettingsElements(Deck.PanelVisual).OfType<FrameworkElement>();
        IEnumerable<Button> Buttons() => Views().OfType<Button>().Where(b => b.IsVisible);
        async Task Snap(string name) { await Task.Delay(60); Deck.UpdateLayout(); Capture(Deck.PanelVisual, Path.Combine(output, name + ".png")); }
        bool Shows(string text) => Views().OfType<TextBlock>().Any(t => t.IsVisible && t.Text == text);
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var next = Settings.Copy(); (next.MusicWidth, next.MusicHeight) = MusicSizes.For(size); UpdateSettings(next); Deck.SelectPage(DeckPage.Music); Deck.SetMediaForTest(song with { PositionAt = DateTimeOffset.UtcNow }, cover, lyric); await Snap("music-" + size);
            Check($"{size} shows the current synced lyric", Shows("刮风这天 我试过握着你手"));
        }
        await VerifyMusicDesignAsync(output, checks, song, cover, lyric);
        var standard = Settings.Copy(); standard.MusicWidth = 520; standard.MusicHeight = 200; UpdateSettings(standard);
        var delayedTrack = song with { Id = "native-late", State = MediaState.Paused, SongId = "netease:1", PositionAt = DateTimeOffset.UtcNow };
        Deck.ReceiveMediaForTest(delayedTrack, cover); await Snap("music-native-before-lyrics");
        Check("Track metadata can appear before native lyrics", Shows("晴天") && !Shows("稍后收到的歌词"));
        delayedTrack = delayedTrack with { EmbeddedLyrics = "[01:40]稍后收到的歌词\n[01:48]下一行歌词" };
        Deck.ReceiveMediaForTest(delayedTrack, cover); await Snap("music-native-late-lyrics");
        Check("Later native lyrics update the same song immediately", Shows("稍后收到的歌词"));
        Deck.ReceiveMediaForTest(delayedTrack with { EmbeddedLyrics = "[01:40]更新后的歌词\n[01:48]下一行歌词" }, cover); await Snap("music-native-updated-lyrics");
        Check("Corrected native lyrics replace an earlier payload", Shows("更新后的歌词"));
        Deck.ReceiveMediaForTest(delayedTrack with { SongId = "netease:2", EmbeddedLyrics = "" }, cover);
        Check("A changed song ID invalidates earlier lyrics", !Shows("更新后的歌词"));
        var qqSong = song with { Id = "qq-pipe-test", Source = "QQ 音乐", State = MediaState.Paused,
            Title = "管道歌词测试", End = TimeSpan.FromMilliseconds(60055), PositionKnown = false, CanSeek = false,
            CurrentLyric = "管道当前歌词", EmbeddedLyrics = "[00:00]不应显示的完整歌词" };
        Deck.ReceiveMediaForTest(qqSong, cover); await Snap("music-qq-pipe");
        Check("QQ current line renders while paused without a timeline", Shows("管道当前歌词") &&
            !Views().OfType<TextBlock>().Any(t => t.IsVisible && t.Text.Contains("不应显示的完整歌词")));
        Deck.ReceiveMediaForTest(qqSong with { CurrentLyric = "管道下一句" }, cover); Deck.UpdateLayout();
        Check("QQ lyric events update the displayed line", Shows("管道下一句"));
        var lyricsOff = Settings.Copy(); lyricsOff.LyricsEnabled = false; UpdateSettings(lyricsOff); Deck.UpdateLayout();
        Check("Disabling QQ lyrics immediately hides the current line", !Shows("管道下一句"));
        lyricsOff.LyricsEnabled = true; UpdateSettings(lyricsOff); Deck.UpdateLayout();
        Check("Reenabling QQ lyrics displays the latest paused line", Shows("管道下一句"));
        Deck.ReceiveMediaForTest(qqSong with { CurrentLyric = "" }, cover); Deck.UpdateLayout();
        Check("QQ empty line clears displayed lyrics", !Shows("管道下一句"));
        Deck.ReceiveMediaForTest(qqSong, cover);
        Deck.ReceiveMediaForTest(qqSong with { Title = "下一首曲目", CurrentLyric = null }, cover); Deck.UpdateLayout();
        Check("QQ song change clears the previous song line", !Shows("管道当前歌词"));
        Deck.ReceiveMediaForTest(qqSong, cover);
        Deck.ReceiveMediaForTest(qqSong with { State = MediaState.Unknown, CurrentLyric = null }, cover); Deck.UpdateLayout();
        Check("QQ disconnect clears stale lyrics", !Shows("管道当前歌词"));
        Deck.ReceiveMediaForTest(qqSong, cover);
        Deck.ReceiveMediaForTest(qqSong with { State = MediaState.Stopped, CurrentLyric = null }, cover); Deck.UpdateLayout();
        Check("QQ stop clears stale lyrics", !Shows("管道当前歌词"));
        var qqFull = Lyrics.Parse("[00:02]QQ 上一句\n[00:08]QQ 当前句\n[00:15]QQ 下一句\n[00:24]QQ 后续句", null, 60, "QQ 音乐");
        var qqTimed = qqSong with { EmbeddedLyrics = "", PositionKnown = true, ReportedPosition = TimeSpan.FromSeconds(10), CurrentLyric = "管道不应覆盖完整歌词" };
        var spacious = Settings.Copy(); (spacious.MusicWidth, spacious.MusicHeight) = MusicSizes.For(PanelSize.Spacious); UpdateSettings(spacious);
        Deck.SetMediaForTest(qqTimed, cover, qqFull); await Snap("music-qq-full");
        Check("QQ complete lyrics take priority over pipe events", Shows("QQ 当前句") && !Shows("管道不应覆盖完整歌词"));
        Check("QQ complete lyrics render both previous and next lines", Shows("QQ 上一句") && Shows("QQ 下一句"));
        Deck.ReceiveMediaForTest(qqTimed with { ReportedPosition = TimeSpan.FromSeconds(16), CurrentLyric = "新的管道单句" }, cover); Deck.UpdateLayout();
        Check("QQ progress advances complete lyrics", Shows("QQ 后续句"));
        Deck.ReceiveMediaForTest(qqTimed with { Title = "QQ 换歌", CurrentLyric = null }, cover); Deck.UpdateLayout();
        Check("QQ song changes discard all previous complete lyrics", !Views().OfType<TextBlock>().Any(t => t.IsVisible && t.Text is "QQ 上一句" or "QQ 当前句" or "QQ 下一句"));
        var previewLyrics = Lyrics.Parse("[00:08]第一段之前\n[00:15]试听重复句\n[00:22]第一段之后\n[01:38]第二段之前\n[01:45]试听重复句\n[01:52]第二段之后", null, 211, "QQ 音乐") with { QqPreview = true };
        var previewTrack = qqTimed with { CurrentLyric = "试听重复句", CurrentLyricStart = TimeSpan.FromSeconds(104.95), CanSeek = true };
        var previewSettings = Settings.Copy(); previewSettings.MusicWidth = 520; previewSettings.MusicHeight = 250; UpdateSettings(previewSettings);
        Deck.SetMediaForTest(previewTrack, cover, previewLyrics); await Snap("music-qq-preview-paused");
        Check("QQ one-minute preview displays three full-song lines at the actual cue", new[] { "第二段之前", "试听重复句", "第二段之后" }.All(Shows));
        Deck.ReceiveMediaForTest(previewTrack with { CurrentLyricStart = TimeSpan.FromSeconds(15) }, cover); Deck.UpdateLayout();
        Check("QQ repeated chorus cue changes refresh neighboring lines", new[] { "第一段之前", "试听重复句", "第一段之后" }.All(Shows));
        Deck.ReceiveMediaForTest(previewTrack with { Title = "试听结束后换歌", CurrentLyric = null, CurrentLyricStart = null }, cover); Deck.UpdateLayout();
        Check("QQ preview song changes clear full-song neighbors and anchors", !Views().OfType<TextBlock>().Any(t => t.IsVisible && t.Text.Contains("段之")));
        UpdateSettings(standard);
        Deck.SetMediaForTest(song with { State = MediaState.Paused }, cover); await Snap("music-paused");
        Check("Paused media offers play", Buttons().Any(b => AutomationProperties.GetName(b) == "播放"));
        Deck.ReceiveMediaForTest(song with { State = MediaState.Stopped }, cover); Deck.UpdateLayout();
        Check("A transient stopped state preserves known song metadata", Shows(song.Title) && !Shows("没有正在播放的媒体"));
        foreach (var repeat in new[] { MediaRepeat.Track, MediaRepeat.List, MediaRepeat.None, MediaRepeat.Track })
        {
            Deck.ReceiveMediaForTest(song with { Repeat = repeat, Shuffle = false }, cover); Deck.UpdateLayout();
        }
        Check("Switching repeat modes keeps one playback-mode button", Buttons().Count(b => AutomationProperties.GetName(b).StartsWith("播放模式")) == 1);
        Deck.SetMediaForTest(song with { Source = "Microsoft Edge", CanRepeat = false, CanShuffle = false, CanSeek = false }, null, tracks: [song, song with { Id = "fake-b", Source = "Microsoft Edge" }]); await Snap("music-multiple-no-cover");
        Check("Unsupported shuffle and repeat are absent", Buttons().All(b => !AutomationProperties.GetName(b).Contains("随机") && !AutomationProperties.GetName(b).Contains("循环")));
        Check("Unsupported seek remains disabled", Views().OfType<Slider>().Single(s => AutomationProperties.GetName(s) == "播放进度").IsEnabled == false);
        var fullLyrics = Lyrics.Parse(string.Join("\n", Enumerable.Range(1, 12).Select(i => $"[00:{i:00}]第 {i} 行歌词")), null, 269, "测试歌词");
        Deck.SetMediaForTest(song with { PositionKnown = false }, cover, fullLyrics); await Snap("music-no-timeline");
        Check("Unavailable playback position hides the whole progress control", Views().OfType<Slider>().All(s => AutomationProperties.GetName(s) != "播放进度"));
        var lyricScroll = Views().OfType<ScrollViewer>().Single(v => AutomationProperties.GetName(v) == "歌词");
        Check("Timestamped lyrics without playback position can be scrolled manually", lyricScroll.ViewportHeight > 0 && lyricScroll.ExtentHeight > lyricScroll.ViewportHeight && ((TextBlock)lyricScroll.Content).Text.Contains("第 12 行歌词"));
        Check("Missing timeline preserves available transport controls", new[] { "上一首", "暂停", "下一首" }.All(name => Buttons().Any(b => AutomationProperties.GetName(b) == name && b.IsEnabled)));
        Deck.SetMediaForTest(song, cover, Lyrics.Parse(null, "第一行普通歌词\n第二行普通歌词", 269, "测试歌词")); await Snap("music-plain-lyrics");
        Check("Plain lyrics display their full text without simulated synchronization", Views().OfType<ScrollViewer>().Any(v => AutomationProperties.GetName(v) == "歌词" && ((TextBlock)v.Content).Text == "第一行普通歌词\n第二行普通歌词"));
        Deck.SetMediaForTest(null); await Snap("music-empty"); Check("No media has an explicit empty state", Shows("没有正在播放的媒体"));
        var logState = new NeteaseLogState();
        foreach (var logEvent in new[]
        {
            new NeteaseLogEvent(NeteaseLogKind.Track, 1000) { SongId = "42", Title = "日志曲目", Artist = "测试歌手", DurationSeconds = 180 },
            new NeteaseLogEvent(NeteaseLogKind.Loaded, 2000) { SongId = "42" },
            new NeteaseLogEvent(NeteaseLogKind.Position, 3000) { Seconds = 45 },
            new NeteaseLogEvent(NeteaseLogKind.Pause, 4000) { SongId = "42", PlayId = "42_test" }
        }) logState = logState.Apply(logEvent, now, 4000);
        bool HintShown() => Views().OfType<TextBlock>().Any(t => t.IsVisible && AutomationProperties.GetName(t) == "SMTC 提示");
        Deck.SetMediaForTest(logState.Track! with { Id = "log-test" }, cover); await Snap("music-netease-log");
        Check("Log replay supplies a frozen estimated timeline", logState.Track!.Position(now.AddMinutes(1)).TotalSeconds == 46);
        Check("Log mode without SMTC shows guidance and the full-control entry instead of transport buttons", Buttons().Any(b => AutomationProperties.GetName(b) == "启用完整控制") && HintShown()
            && !Buttons().Any(b => AutomationProperties.GetName(b) is "播放" or "上一首" or "下一首"));
        Check("Log mode timeline cannot be dragged", Views().OfType<Slider>().Single(s => AutomationProperties.GetName(s) == "播放进度").IsEnabled == false);
        var logLyric = Lyrics.Parse("[00:10]日志歌词第一行\n[00:40]日志歌词当前行\n[01:20]日志歌词下一行", null, 180, "测试歌词");
        Deck.SetMediaForTest(logState.Track! with { Id = "log-test" }, cover, logLyric); await Snap("music-netease-log-lyrics");
        Check("Log mode keeps synced lyrics alongside the guidance", Shows("日志歌词当前行") && HintShown());
        var spaciousLog = Settings.Copy(); (spaciousLog.MusicWidth, spaciousLog.MusicHeight) = MusicSizes.For(PanelSize.Spacious); UpdateSettings(spaciousLog);
        Deck.SetMediaForTest(logState.Track! with { Id = "log-test" }, cover, logLyric); await Snap("music-netease-log-lyrics-spacious");
        UpdateSettings(standard);
        Deck.SetMediaForTest(MediaRouting.Merge(logState.Track! with { Id = "log-test" }, song), cover); await Snap("music-netease-log-smtc");
        Check("SMTC supplies basic controls without seek or modes", Buttons().Where(b => AutomationProperties.GetName(b) is "播放" or "上一首" or "下一首").All(b => b.IsEnabled) && Buttons().All(b => !AutomationProperties.GetName(b).Contains("随机") && !AutomationProperties.GetName(b).Contains("循环")));
        Check("Available transport controls replace the SMTC hint", !HintShown() && Buttons().Any(b => AutomationProperties.GetName(b) is "播放" or "暂停"));
        var narrowTall = Settings.Copy(); narrowTall.MusicWidth = 440; narrowTall.MusicHeight = 300; UpdateSettings(narrowTall);
        Deck.SetMediaForTest(MediaRouting.Merge(logState.Track! with { Id = "log-test" }, song), cover); await Snap("music-netease-log-smtc-narrow");
        var compactLog = Settings.Copy(); (compactLog.MusicWidth, compactLog.MusicHeight) = MusicSizes.For(PanelSize.Compact); UpdateSettings(compactLog);
        Deck.SetMediaForTest(logState.Track! with { Id = "log-test" }, cover); await Snap("music-netease-log-compact");
        UpdateSettings(standard);
        var consent = new NeteaseControlWindow(Settings.Theme); consent.Show(); await Task.Delay(60);
        Capture((FrameworkElement)consent.Content, Path.Combine(output, "music-full-control-consent.png")); consent.Close();
        Deck.SetMediaForTest(song, cover, lyric); Deck.Preview(true, false);
        var compact = Settings.Copy(); compact.MusicText = CompactMusicText.Lyrics;
        compact.Apps = Enumerable.Range(0, 7).Select(i => new AppEntry { QuotaSource = ProviderCatalog.BuiltIns[i % 4], DisplayName = "应用 " + (i + 1) }).ToList(); UpdateSettings(compact); Deck.SetMediaForTest(song, cover, lyric); await Snap("compact-seven-lyrics");
        string compactLyric = Views().OfType<MusicMarquee>().Single(v => v.IsVisible).Text;
        var compactOnly = compact.Copy(); compactOnly.MusicPage = false; compactOnly.LyricsEnabled = false; UpdateSettings(compactOnly);
        Deck.SetMediaForTest(song, cover, lyric); await Snap("compact-lyrics-without-music-page");
        Check("Compact music and lyrics work without the music page or expanded lyrics", compactLyric != song.Caption &&
            Views().OfType<MusicMarquee>().Any(v => v.IsVisible && v.Text == compactLyric));
        UpdateSettings(compact); Deck.SetMediaForTest(song, cover, lyric);
        Deck.SetMediaForTest(song with { PositionKnown = false }, cover, lyric); await Snap("compact-no-timeline");
        Check("Compact lyrics fall back to the song caption without a real timeline", Views().OfType<MusicMarquee>().Any(v => v.IsVisible && v.Text == song.Caption));
        var longLineLyrics = Lyrics.Parse("[00:10]这一句还没有唱完\n[00:24]\n[00:30]下一句歌词", null, 90, "网易云音乐");
        var longLineTrack = song with { SongId = "netease:long-line-test", ReportedPosition = TimeSpan.FromSeconds(10), PositionAt = now, Rate = 0 };
        var lyricOnly = Settings.Copy(); lyricOnly.NotchSummary = lyricOnly.CapsuleSummary = false; UpdateSettings(lyricOnly);
        Deck.SetMediaForTest(longLineTrack, cover, longLineLyrics); Deck.UpdateLayout();
        string CompactText() => Views().OfType<MusicMarquee>().Single(v => v.IsVisible).Text;
        var mediaField = typeof(MainWindow).GetField("_mediaTrack", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var positionUpdate = typeof(MainWindow).GetMethod("UpdateMusicPosition", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool followsTimestamps = true;
        foreach (var (seconds, expected) in new[] { (14.99, "这一句还没有唱完"), (15d, "这一句还没有唱完"), (18d, "这一句还没有唱完"),
            (23.99, "这一句还没有唱完"), (24d, "♪"), (30d, "下一句歌词"), (18d, "这一句还没有唱完") })
        {
            mediaField.SetValue(Deck, longLineTrack with { ReportedPosition = TimeSpan.FromSeconds(seconds) });
            positionUpdate.Invoke(Deck, null); Deck.UpdateLayout();
            followsTimestamps &= CompactText() == expected;
        }
        Check("Compact Netease lyrics follow their timestamps as playback moves", followsTimestamps);
        await Snap("compact-netease-long-line");
        UpdateSettings(compact);
        Deck.ReceiveMediaForTest(qqSong, cover); await Snap("compact-qq-pipe");
        bool CompactShows(string text) => Views().OfType<MusicMarquee>().Any(v => v.IsVisible && v.Text == text);
        Check("Compact QQ lyric uses the pipe even without a timeline", CompactShows("管道当前歌词"));
        Deck.ReceiveMediaForTest(qqSong with { CurrentLyric = "暂停时更新的歌词" }, cover); Deck.UpdateLayout();
        Check("Paused compact QQ lyrics update", CompactShows("暂停时更新的歌词"));
        Deck.ReceiveMediaForTest(qqSong with { CurrentLyric = "" }, cover); Deck.UpdateLayout();
        Check("Empty QQ pipe lyrics restore the compact caption", CompactShows(qqSong.Caption));
        Deck.SetMediaForTest(song, cover, lyric);
        var entry = Settings.EnabledApps[0]; Deck.ShowAlert(entry, "每周", 91, now.AddDays(2)); await Snap("compact-quota-alert");
        Check("Quota alert hides music", Deck.AlertVisible && ((FrameworkElement)Deck.FindName("CompactRings")).Visibility == Visibility.Collapsed);
        Deck.ClearAlert(); Deck.SetExpanded(false, true); await Snap("compact-restored"); Check("Music returns after quota alert", !Deck.AlertVisible && ((FrameworkElement)Deck.FindName("CompactRings")).Visibility == Visibility.Visible);
        compact = Settings.Copy(); compact.Style = CompactStyle.Capsule; UpdateSettings(compact); await Snap("compact-capsule");
        var dimensions = Settings.Copy(); dimensions.Apps = new DeckSettings().Apps; dimensions.Width = 760; dimensions.Height = 240; dimensions.MusicWidth = 520; dimensions.MusicHeight = 200; UpdateSettings(dimensions); Deck.Preview(true);
        Deck.SelectPage(DeckPage.Usage); await Snap("page-ai");
        Deck.SelectPage(DeckPage.Music); await Snap("page-music"); Check("Page switch preserves both saved sizes", Settings.Width == 760 && Settings.Height == 240 && Settings.MusicWidth == 520 && Settings.MusicHeight == 200);
        Deck.MediaSourceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Snap("music-source-menu");
        Deck.MediaSourceButton.ContextMenu!.IsOpen = false;
        var window = new SettingsWindow(this); window.Show();
        foreach (int page in new[] { 1, 2, 3, 5, 6 }) { window.ShowPage(page); await Task.Delay(40); Capture(window.RootVisual, Path.Combine(output, $"settings-music-{page}.png")); }
        window.Close();
        await VerifyWindowsMediaFixture(output, checks);
        if (live)
        {
            using var media = new Native.MediaSessions(Dispatcher); await media.StartAsync();
            Check("Windows media manager can be queried", media.Error is null);
            // Native connections and artwork arrive after StartAsync. Wait for their observations,
            // without changing playback in any user-owned source.
            var discoveryDeadline = DateTimeOffset.UtcNow.AddSeconds(8);
            var unchangedSince = DateTimeOffset.UtcNow;
            string previousSources = "";
            while (DateTimeOffset.UtcNow < discoveryDeadline)
            {
                await Task.Delay(100); await media.RefreshAsync();
                string sources = string.Join("\n", media.Tracks.Select(t => $"{t.Id}|{t.SongKey}|{t.SongId}|{t.HasTimeline}"));
                if (sources != previousSources) { previousSources = sources; unchangedSince = DateTimeOffset.UtcNow; }
                if (sources.Length > 0 && DateTimeOffset.UtcNow - unchangedSince > TimeSpan.FromSeconds(3)) break;
            }
            using var lyricsService = new LyricsService();
            var report = new List<string>();
            var sourceIds = media.Tracks.Where(t => !t.Title.StartsWith("BrimDeck isolated media test", StringComparison.Ordinal))
                .Where(t => !Environment.GetCommandLineArgs().Contains("--media-live-qq") || t.IsQqMusic).Select(t => t.Id).ToArray();
            for (int index = 0; index < sourceIds.Length; index++)
            {
                string id = sourceIds[index]; media.Select(id);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
                unchangedSince = DateTimeOffset.UtcNow;
                string stableKey = ""; BitmapSource? previousCover = null;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(100); await media.RefreshAsync();
                    if (media.Current is not { } candidate || candidate.Id != id) continue;
                    string key = $"{candidate.SongKey}|{candidate.SongId}|{candidate.EmbeddedLyrics}|{candidate.HasTimeline}|{candidate.CanSeek}";
                    if (key != stableKey || previousCover != media.Cover)
                    { stableKey = key; previousCover = media.Cover; unchangedSince = DateTimeOffset.UtcNow; }
                    if ((media.Cover is not null || candidate.Title.Length == 0) && DateTimeOffset.UtcNow - unchangedSince > TimeSpan.FromSeconds(1)) break;
                }
                if (media.Current is not { } current || current.Id != id) { report.Add($"source {index + 1}: closed before capture"); continue; }
                var liveLyrics = Lyrics.Empty;
                string lyricStatus = "complete";
                using (var request = new CancellationTokenSource(TimeSpan.FromSeconds(current.IsQqMusic ? 30 : 15)))
                {
                    try { liveLyrics = await lyricsService.FindAsync(current, true, request.Token); }
                    catch (OperationCanceledException) { lyricStatus = "timeout"; }
                    catch (Exception ex) { lyricStatus = ex.GetType().Name; }
                }
                // A song can change during the read-only lyrics request. Never render its old lyrics.
                if (media.Current is not { } latest || latest.Id != id) { report.Add($"source {index + 1}: closed during lyrics lookup"); continue; }
                if (latest.SongKey != current.SongKey || latest.SongId != current.SongId)
                { liveLyrics = Lyrics.Empty; lyricStatus = "song changed during lookup"; }
                current = latest;
                if (current.IsQqMusic)
                {
                    report.Add($"QQ preview={liveLyrics.QqPreview}; nativeLyricStart={current.CurrentLyricStart}; lastLrcTimestamp={liveLyrics.Lines.LastOrDefault()?.Seconds}");
                    if (liveLyrics.QqPreview && !string.IsNullOrWhiteSpace(current.CurrentLyric))
                        Check("Live QQ preview full lyrics follow the native current line", string.Concat(liveLyrics.At(current, DateTimeOffset.UtcNow).Current.Where(char.IsLetterOrDigit)) ==
                            string.Concat(current.CurrentLyric.Where(char.IsLetterOrDigit)));
                }
                report.Add($"{current.Source}: {current.Caption}; id={current.SongId}; state={current.State}; logMode={current.IsNeteaseLog}; duration={current.Duration}; position={current.Position(DateTimeOffset.UtcNow)}; timeline={current.HasTimeline}; cover={media.Cover is not null}; lyricLines={liveLyrics.Lines.Count}; lyricSource={liveLyrics.Source}; synchronized={liveLyrics.CanSynchronize(current)}; lyricLookup={lyricStatus}; qqMusic={current.IsQqMusic}; currentLyric={current.CurrentLyric}; seek={current.CanSeek}; previous={current.CanPrevious}; next={current.CanNext}; shuffle={current.CanShuffle}; repeat={current.CanRepeat}");
                if (current.IsQqMusic && lyricStatus == "complete")
                    Check("Live QQ obtains complete synchronized lyrics from QQ only", liveLyrics.Source == "QQ 音乐" && liveLyrics.Synced && liveLyrics.Lines.Count > 1);
                foreach (var size in new[] { PanelSize.Compact, PanelSize.Standard, PanelSize.Spacious })
                {
                    var liveSettings = Settings.Copy(); (liveSettings.MusicWidth, liveSettings.MusicHeight) = MusicSizes.For(size); UpdateSettings(liveSettings);
                    var captured = current with { ReportedPosition = current.Start + current.Position(DateTimeOffset.UtcNow), Rate = 0, PositionAt = DateTimeOffset.UtcNow };
                    Deck.Preview(true); Deck.SelectPage(DeckPage.Music); Deck.SetMediaForTest(captured, media.Cover, liveLyrics, media.Tracks);
                    await Snap($"music-live-{index + 1:00}-{size}");
                    if (size != PanelSize.Standard || !captured.IsQqMusic) continue;
                    if (captured.State != MediaState.Stopped && liveLyrics.Lines.Count > 0 && liveLyrics.CanSynchronize(captured))
                    {
                        string text = liveLyrics.At(captured, DateTimeOffset.UtcNow).Current;
                        if (text.Length > 0) Check("Live QQ renders the complete lyric timeline", Shows(text));
                    }
                    else if (liveLyrics.Lines.Count == 0 && !string.IsNullOrWhiteSpace(captured.CurrentLyric))
                        Check("Live QQ preserves its own pipe fallback", Shows(captured.CurrentLyric));
                }
            }
            report.AddRange(media.ConnectionDiagnostics());
            File.WriteAllLines(Path.Combine(output, "live-media.txt"), report);
            if (Environment.GetCommandLineArgs().Contains("--media-control"))
                await VerifyLiveMusicControls(output, checks, media);
            checks.Add($"DATA Windows media sources: {media.Tracks.Count}; output peak: {media.Peak():0.000}");
        }
    }
    private static void VerifyMediaArtwork(List<string> checks)
    {
        void Check(string name, bool value) => checks.Add((value ? "PASS " : "FAIL ") + name);
        var pixels = new byte[] { 40, 80, 200, 200, 80, 40, 80, 200, 40, 200, 40, 80 };
        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Rgb24, null, pixels, 6);
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream(); encoder.Save(stream);
        byte[] jpeg = stream.ToArray();
        // Reproduce the empty APP1/EXIF segment returned for QQ's live album artwork.
        byte[] emptyExif = [0xff, 0xe1, 0, 8, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0];
        byte[] qqJpeg = [.. jpeg.AsSpan(0, 2), .. emptyExif, .. jpeg.AsSpan(2)];
        var normal = Native.MediaSessions.DecodeCover(jpeg);
        var restored = Native.MediaSessions.DecodeCover(qqJpeg);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(source));
        using var pngStream = new MemoryStream(); png.Save(pngStream);
        Check("Ordinary JPEG and PNG covers decode", normal is not null && Native.MediaSessions.DecodeCover(pngStream.ToArray()) is not null);
        static byte[] Rgb(BitmapSource image)
        {
            var rgb = new FormatConvertedBitmap(image, PixelFormats.Rgb24, null, 0);
            int stride = rgb.PixelWidth * 3;
            var result = new byte[stride * rgb.PixelHeight]; rgb.CopyPixels(result, stride, 0); return result;
        }
        Check("QQ cover with empty EXIF decodes to the same album pixels", normal is not null && restored is not null && Rgb(normal).SequenceEqual(Rgb(restored)));
        Check("Missing and invalid cover data remain empty", Native.MediaSessions.DecodeCover(null) is null && Native.MediaSessions.DecodeCover([1, 2, 3]) is null);
    }

    // Explicit opt-in integration test: real WPF buttons, normal service routing,
    // and real player observations. Ordinary --media-live remains observation-only.
    private async Task VerifyLiveMusicControls(string output, List<string> checks, Native.MediaSessions media)
    {
        var field = typeof(MainWindow).GetField("_media", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousService = field.GetValue(Deck);
        var trace = new List<string>();
        var watch = System.Diagnostics.Stopwatch.StartNew(); long previousTick = 0, maximumGap = 0; int ticks = 0;
        var heartbeat = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        nint expectedForeground = 0;
        bool foregroundChanged = false;
        heartbeat.Tick += (_, _) =>
        {
            long now = watch.ElapsedMilliseconds; if (ticks++ > 0) maximumGap = Math.Max(maximumGap, now - previousTick); previousTick = now;
            if (expectedForeground != 0 && MediaForegroundWindow() != expectedForeground) foregroundChanged = true;
        };
        void Render() { Deck.ReceiveMediaForTest(media.Current, media.Cover); Deck.UpdateLayout(); }
        void Check(string name, bool pass) { checks.Add((pass ? "PASS " : "FAIL ") + name); trace.Add(checks[^1]); File.WriteAllLines(Path.Combine(output, "live-controls.txt"), trace); }
        async Task<bool> Wait(Func<bool> predicate, int seconds = 5)
        {
            var end = DateTimeOffset.UtcNow.AddSeconds(seconds);
            while (DateTimeOffset.UtcNow < end) { await Task.Delay(100); if (predicate()) return true; }
            return false;
        }
        void Click(string name)
        {
            Render();
            var button = SettingsElements(Deck.PanelVisual).OfType<Button>().Single(b => b.IsVisible && AutomationProperties.GetName(b) == name);
            if (!button.IsEnabled) throw new InvalidOperationException(name + " is disabled.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        try
        {
            field.SetValue(Deck, media); media.Changed += Render;
            Deck.Preview(true); Deck.SelectPage(DeckPage.Music); heartbeat.Start();
            expectedForeground = MediaForegroundWindow();
            foreach (var original in media.Tracks.Where(t => t.Source is "网易云音乐" or "QQ 音乐")
                .Where(t => !Environment.GetCommandLineArgs().Contains("--media-control-qq") || t.Source == "QQ 音乐").ToArray())
            {
                media.Select(original.Id); await media.RefreshAsync(); Render();
                string label = original.Source;
                Check(label + " has a native timeline and transport", media.Current is { HasTimeline: true } && (original.IsNeteaseLog || media.Current.CanSeek && media.Current.CanToggle));
                if (media.Current is not { HasTimeline: true, CanToggle: true }) continue;
                bool toggled = true;
                for (int i = 0; i < 4 && toggled; i++)
                {
                    var expected = media.Current?.State == MediaState.Playing ? MediaState.Paused : MediaState.Playing;
                    Click(expected == MediaState.Paused ? "暂停" : "播放");
                    toggled = await Wait(() => media.Current?.State == expected);
                }
                Check(label + " UI play/pause toggles the native state", toggled);
                if (media.Current?.State == MediaState.Playing) { Click("暂停"); await Wait(() => media.Current?.State == MediaState.Paused); }
                var before = media.Current!;
                Click("下一首"); Check(label + " UI next changes the native track", await Wait(() => media.Current is { CanPrevious: true } next && next.Title.Length > 0 && next.Title != before.Title, 8));
                Click("上一首"); Check(label + " UI previous returns to the prior track", await Wait(() => media.Current?.Title == before.Title, 8));
                await Wait(() => media.Current is { HasTimeline: true });
                if (media.Current is { HasTimeline: true, CanSeek: true } track)
                {
                    trace.Add(label + " seek before: " + track.SongKey + " position=" + track.ReportedPosition);
                    trace.AddRange(media.ConnectionDiagnostics());
                    var seek = SettingsElements(Deck.PanelVisual).OfType<Slider>().Single(s => AutomationProperties.GetName(s) == "播放进度");
                    seek.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                        System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                    seek.Value = .3;
                    seek.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                        System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    bool confirmed = await Wait(() => media.Current is { } current && Math.Abs(current.ReportedPosition.TotalSeconds - track.Duration.TotalSeconds * .3) < 3);
                    trace.Add(label + " seek after: " + media.Current?.SongKey + " position=" + media.Current?.ReportedPosition + " errors=" + string.Join("|", SettingsElements(Deck.PanelVisual).OfType<TextBlock>().Where(t => t.IsVisible && t.Text.Contains("播放器未接受")).Select(t => t.Text)));
                    Check(label + " UI seek is confirmed by native progress", confirmed);
                }
                for (int i = 0; i < 4 && media.Current is { } modeTrack && MusicPlaybackModes.Supported(modeTrack).Count > 0; i++)
                {
                    var expected = MusicPlaybackModes.Next(modeTrack);
                    Click("播放模式 · " + MusicPlaybackModes.Label(MusicPlaybackModes.Current(modeTrack)));
                    Check(label + " UI mode " + expected, await Wait(() => media.Current is { } readback && MusicPlaybackModes.Current(readback) == expected));
                    await Task.Delay(180);
                }
                Capture(Deck.PanelVisual, Path.Combine(output, label == "网易云音乐" ? "netease-after-controls.png" : "qq-after-controls.png"));
                if (media.Current?.Title == original.Title && original.Duration > TimeSpan.Zero)
                    await media.SeekAsync(media.Current, original.ReportedPosition.TotalSeconds / original.Duration.TotalSeconds);
                if (media.Current?.State != original.State && original.State is MediaState.Playing or MediaState.Paused)
                { Click(original.State == MediaState.Playing ? "播放" : "暂停"); await Wait(() => media.Current?.State == original.State); }
            }
            Check($"WPF stays responsive during native operations (max gap {maximumGap} ms, {ticks} ticks)", ticks > 20 && maximumGap < 1000);
            Check("Media controls preserve the foreground window", !foregroundChanged && MediaForegroundWindow() == expectedForeground);
            trace.AddRange(media.ConnectionDiagnostics()); File.WriteAllLines(Path.Combine(output, "live-controls.txt"), trace);
        }
        finally { trace.AddRange(media.ConnectionDiagnostics()); File.WriteAllLines(Path.Combine(output, "live-controls.txt"), trace); heartbeat.Stop(); media.Changed -= Render; field.SetValue(Deck, previousService); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint MediaForegroundWindow();
    private async Task VerifyWindowsMediaFixture(string output, List<string> checks)
    {
        void Check(string name, bool value) => checks.Add((value ? "PASS " : "FAIL ") + name);
        SetCurrentProcessExplicitAppUserModelID("BrimDeck.MusicTests");
        using var player = new global::Windows.Media.Playback.MediaPlayer();
        player.CommandManager.IsEnabled = false;
        var controls = player.SystemMediaTransportControls;
        controls.IsEnabled = true; controls.IsPlayEnabled = true; controls.IsPauseEnabled = true; controls.IsNextEnabled = true; controls.IsPreviousEnabled = true;
        controls.ShuffleEnabled = false; controls.AutoRepeatMode = global::Windows.Media.MediaPlaybackAutoRepeatMode.List;
        controls.ShuffleEnabledChangeRequested += (_, e) => Dispatcher.BeginInvoke(() => { controls.ShuffleEnabled = e.RequestedShuffleEnabled; });
        controls.AutoRepeatModeChangeRequested += (_, e) => Dispatcher.BeginInvoke(() => { controls.AutoRepeatMode = e.RequestedAutoRepeatMode; });
        controls.DisplayUpdater.Type = global::Windows.Media.MediaPlaybackType.Music;
        controls.DisplayUpdater.MusicProperties.Title = "BrimDeck isolated media test";
        controls.DisplayUpdater.MusicProperties.Artist = "Local fixture";
        controls.DisplayUpdater.MusicProperties.AlbumTitle = "Smoke test";
        controls.DisplayUpdater.Update();
        controls.PlaybackStatus = global::Windows.Media.MediaPlaybackStatus.Playing;
        var timeline = new global::Windows.Media.SystemMediaTransportControlsTimelineProperties { StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromSeconds(200), MinSeekTime = TimeSpan.Zero, MaxSeekTime = TimeSpan.FromSeconds(200), Position = TimeSpan.FromSeconds(20) };
        controls.UpdateTimelineProperties(timeline);
        int next = 0, previous = 0;
        controls.ButtonPressed += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            if (e.Button == global::Windows.Media.SystemMediaTransportControlsButton.Play) controls.PlaybackStatus = global::Windows.Media.MediaPlaybackStatus.Playing;
            if (e.Button == global::Windows.Media.SystemMediaTransportControlsButton.Pause) controls.PlaybackStatus = global::Windows.Media.MediaPlaybackStatus.Paused;
            if (e.Button == global::Windows.Media.SystemMediaTransportControlsButton.Next) next++;
            if (e.Button == global::Windows.Media.SystemMediaTransportControlsButton.Previous) previous++;
        });
        controls.PlaybackPositionChangeRequested += (_, e) => Dispatcher.BeginInvoke(() => { timeline.Position = e.RequestedPlaybackPosition; controls.UpdateTimelineProperties(timeline); });
        using var service = new Native.MediaSessions(Dispatcher, connectPlayers: false); await service.StartAsync();
        MediaTrack? track = null;
        for (int i = 0; i < 12 && track is null; i++) { await Task.Delay(250); await service.RefreshAsync(); track = service.Tracks.FirstOrDefault(t => t.Title == "BrimDeck isolated media test"); }
        Check("Windows event source discovers isolated media metadata", track is not null && track.Artist == "Local fixture" && track.Duration.TotalSeconds == 200);
        if (track is not null)
        {
            bool accepted = await service.CommandAsync(track.Id, MediaCommand.PlayPause); await Task.Delay(250); await service.RefreshAsync();
            Check("Windows play-pause reaches the selected session", accepted && controls.PlaybackStatus == global::Windows.Media.MediaPlaybackStatus.Paused);
            await service.CommandAsync(track.Id, MediaCommand.Next); await service.CommandAsync(track.Id, MediaCommand.Previous); await Task.Delay(250);
            Check("Windows next and previous reach only the test source", next == 1 && previous == 1);
            accepted = await service.SeekAsync(track, .6); await Task.Delay(250);
            Check("Windows seeking uses advertised duration and ticks", accepted && timeline.Position.TotalSeconds == 120);
            service.Select(track.Id); await Task.Delay(150); await service.RefreshAsync();
            Check("Manual source selection remains selected", !service.FollowsSystem && service.Current?.Id == track.Id);

            // Use the rendered controls and normal event handlers against our own SMTC fixture only.
            // Never select or send commands to another source discovered by this service.
            var mediaField = Deck.GetType().GetField("_media", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var coverField = Deck.GetType().GetField("_mediaCover", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var originalService = mediaField.GetValue(Deck);
            var originalTrack = Deck.CurrentMedia;
            var originalTracks = Deck.MediaTracks;
            var originalCover = coverField.GetValue(Deck) as BitmapSource;
            var busyField = Deck.GetType().GetField("_commandBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
            try
            {
                mediaField.SetValue(Deck, service);
                Deck.Preview(true); Deck.SelectPage(DeckPage.Music);
                async Task RenderFixture()
                {
                    await service.RefreshAsync();
                    var selected = service.Tracks.Single(t => t.Id == track.Id);
                    Deck.SetMediaForTest(selected, tracks: [selected]);
                    Deck.UpdateLayout();
                }
                async Task AwaitFixture(string name, Func<bool> condition)
                {
                    bool Ready() => condition() && busyField.GetValue(Deck) is false;
                    for (int i = 0; i < 100 && !Ready(); i++) await Task.Delay(50);
                    Check(name, Ready());
                }
                void Click(string name)
                {
                    var button = SettingsElements(Deck.PanelVisual).OfType<Button>()
                        .Single(b => b.IsVisible && AutomationProperties.GetName(b) == name);
                    if (!button.IsEnabled) throw new InvalidOperationException("Disabled fixture control: " + name);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                }
                await RenderFixture();
                int previousNext = next, previousPrevious = previous;
                Click("下一首");
                await AwaitFixture("Music next button routes through the selected Windows session", () => next == previousNext + 1);
                Click("上一首");
                await AwaitFixture("Music previous button routes through the selected Windows session", () => previous == previousPrevious + 1);
                Click("播放");
                await AwaitFixture("Music play button updates the selected Windows session", () => controls.PlaybackStatus == global::Windows.Media.MediaPlaybackStatus.Playing && service.Tracks.Any(t => t.Id == track.Id && t.State == MediaState.Playing));
                await RenderFixture();
                Click("暂停");
                await AwaitFixture("Music pause button updates the selected Windows session", () => controls.PlaybackStatus == global::Windows.Media.MediaPlaybackStatus.Paused && service.Tracks.Any(t => t.Id == track.Id && t.State == MediaState.Paused));
                await RenderFixture();

                foreach (var expected in new[] { MusicPlaybackMode.Track, MusicPlaybackMode.Shuffle, MusicPlaybackMode.Order, MusicPlaybackMode.List })
                {
                    Click("播放模式 · " + MusicPlaybackModes.Label(MusicPlaybackModes.Current(service.Current!)));
                    await AwaitFixture("Combined playback-mode button selects " + expected, () => service.Current is { } current && MusicPlaybackModes.Current(current) == expected);
                    await RenderFixture();
                }
                await service.SetPlaybackModeAsync(track.Id, MusicPlaybackMode.Shuffle);
                await AwaitFixture("Windows fixture enters shuffle for the two-command transition", () => service.Current?.Shuffle == true);
                controls.AutoRepeatMode = global::Windows.Media.MediaPlaybackAutoRepeatMode.None;
                await AwaitFixture("Windows publishes the starting repeat mode", () => service.Current?.Repeat == MediaRepeat.None);
                await service.SetPlaybackModeAsync(track.Id, MusicPlaybackMode.List);
                await AwaitFixture("Leaving shuffle can change both shuffle and repeat", () => service.Current is { Shuffle: false, Repeat: MediaRepeat.List });
                await RenderFixture();

                var seek = SettingsElements(Deck.PanelVisual).OfType<Slider>()
                    .Single(s => s.IsVisible && AutomationProperties.GetName(s) == "播放进度");
                Check("Windows fixture exposes an enabled seek slider", seek.IsEnabled);
                seek.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                seek.Value = .35;
                seek.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                await AwaitFixture("Music progress click reaches Windows with the expected position", () => timeline.Position == TimeSpan.FromSeconds(70));

                // A drag previews multiple values but commits only the released position.
                int seekRequests = 0;
                void CountSeek(global::Windows.Media.SystemMediaTransportControls _, global::Windows.Media.PlaybackPositionChangeRequestedEventArgs __) => Interlocked.Increment(ref seekRequests);
                controls.PlaybackPositionChangeRequested += CountSeek;
                try
                {
                    var sliderTrack = (Track)seek.Template.FindName("PART_Track", seek);
                    var thumb = sliderTrack.Thumb;
                    void StartDrag() => thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                    void DragTo(double fraction) => thumb.RaiseEvent(new DragDeltaEventArgs((fraction - seek.Value) * (sliderTrack.ActualWidth - thumb.ActualWidth), 0) { RoutedEvent = Thumb.DragDeltaEvent });
                    void EndDrag(bool canceled = false) => thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled) { RoutedEvent = Thumb.DragCompletedEvent });
                    StartDrag(); DragTo(.45); DragTo(.65); DragTo(.8);
                    Check("Music drag does not send intermediate positions", Volatile.Read(ref seekRequests) == 0 && timeline.Position == TimeSpan.FromSeconds(70));
                    EndDrag();
                    await AwaitFixture("Music drag commits the final position once", () => Volatile.Read(ref seekRequests) == 1 && timeline.Position == TimeSpan.FromSeconds(160));
                    StartDrag(); DragTo(.5); EndDrag(canceled: true);
                    Check("Canceled drag restores the current position without a request", Math.Abs(seek.Value - .8) < .000001 && Volatile.Read(ref seekRequests) == 1);

                    // Begin a drag on the current UI, then let the source publish a new song
                    // before the UI receives that update. The service must reject the old seek.
                    StartDrag(); DragTo(.9);
                    controls.DisplayUpdater.MusicProperties.Title = "BrimDeck isolated media test next song";
                    controls.DisplayUpdater.Update();
                    timeline.Position = TimeSpan.FromSeconds(12); controls.UpdateTimelineProperties(timeline);
                    await AwaitFixture("Windows reports the new song before the previous drag ends", () => service.Tracks.Any(t => t.Id == track.Id && t.Title == "BrimDeck isolated media test next song"));
                    int requestsBeforeRelease = Volatile.Read(ref seekRequests);
                    EndDrag(); await Task.Delay(300);
                    await AwaitFixture("A drag from the old song leaves the new song position untouched", () => Volatile.Read(ref seekRequests) == requestsBeforeRelease && timeline.Position == TimeSpan.FromSeconds(12));
                }
                finally { controls.PlaybackPositionChangeRequested -= CountSeek; }
            }
            finally
            {
                mediaField.SetValue(Deck, originalService);
                Deck.SetMediaForTest(originalTrack, originalCover, tracks: originalTracks);
            }
        }
        controls.PlaybackStatus = global::Windows.Media.MediaPlaybackStatus.Closed; controls.IsEnabled = false;
    }
}


