using System.Text;
using BrimDeck.Core;

internal static class NeteaseLogTests
{
    public static void Run(Action<string, bool> check)
    {
        const int pid = 12345;
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        string Line(long tick, string payload) => $"[{pid}:6789:0101/120000:{tick}:INFO:app.cpp(1231)] [2026-01-01 12:00:00] 【playing】,{payload}";
        NeteaseLogEvent? Parse(long tick, string payload) => NeteaseLog.Parse(Line(tick, payload), pid, 100_000);
        NeteaseLogState Apply(NeteaseLogState state, long tick, string payload) => state.Apply(Parse(tick, payload)!, now, 100_000);
        const string track = """
            "playOneTrackInPlayingList",{"track":{"id":"42","name":"曲目甲","duration":180000,"artists":[{"name":"歌手甲"},{"name":"歌手乙"}],"album":{"name":"专辑","picUrl":"https://p1.music.126.net/cover.jpg"}},"resourceUrl":"https://invalid.example/private"}
            """;
        var e = Parse(1000, track)!;
        check("Log track metadata is limited to the music fields", e.Title == "曲目甲" && e.SongId == "42" && e.DurationSeconds == 180 && e.Artist == "歌手甲 / 歌手乙" && e.Album == "专辑");
        check("Log covers accept the music artwork host only", e.CoverUrl == "https://p1.music.126.net/cover.jpg" && NeteaseLog.SafeCover("http://localhost/private") is null);
        check("Log ignores child processes and future timestamps", NeteaseLog.Parse(Line(1000, track), 999, 100_000) is null && NeteaseLog.Parse(Line(100001, track), pid, 100_000) is null);
        check("Log rejects previous process generations", NeteaseLog.Parse(Line(1000, track), pid, 100_000, 1001) is null);
        check("Non-JSON setPlaying is ignored", Parse(1000, "\"setPlaying\",\"enter nativePlay\"") is null);
        check("Malformed and unrelated events are ignored", Parse(1000, "\"nativePlay\",{") is null && Parse(1000, "\"requestUrl\",{\"url\":\"private\"}") is null);
        check("Native play and JSON setPlaying accept track data", Parse(1000, track.Replace("playOneTrackInPlayingList", "nativePlay"))?.Kind == NeteaseLogKind.Track && Parse(1000, track.Replace("playOneTrackInPlayingList", "setPlaying"))?.Kind == NeteaseLogKind.Track);
        var state = new NeteaseLogState().Apply(e, now, 100_000);
        check("New song waits for load without borrowing progress", state.Track is { TimelinePending: true, PositionKnown: false, CanSeek: false, IsNeteaseLog: true });
        state = Apply(state, 2000, "\"native播放资源load完成，开始播放\",{\"songId\":\"42\"}");
        state = Apply(state, 2100, "\"native播放state\",1,\"42_first\"");
        check("Playback advances from event time instead of read time", Math.Abs(state.Track!.Position(now.AddMilliseconds(-95000)).TotalSeconds - 3) < .001);
        state = Apply(state, 6000, "\"pause\",\"42_first\",\"42\"");
        check("Pause freezes the estimated position", state.Track!.Position(now.AddHours(1)).TotalSeconds == 4);
        state = Apply(state, 7000, "\"setPlayingPosition\",25.5882352");
        state = Apply(state, 8000, "\"resume\",\"42_first\",\"42\"");
        state = Apply(state, 9000, "\"PlayerBuffering\",\"42_first\",1");
        double buffered = state.Track!.ReportedPosition.TotalSeconds;
        check("Buffering stops the clock without pretending to pause", state.Buffering && state.Track.State == MediaState.Playing && state.Track.Position(now).TotalSeconds == buffered);
        state = Apply(state, 12000, "\"PlayerBufferingEnd\",\"42_first\",0");
        check("Buffering end resumes from the frozen anchor", !state.Buffering && Math.Abs(state.Track!.Position(now.AddMilliseconds(-87000)).TotalSeconds - buffered - 1) < .001);
        var before = state.Track;
        state = Apply(state, 13000, "\"native播放state\",2,\"41_old\"");
        check("Old song state cannot pause the new song", state.Track == before);
        state = Apply(state, 14000, "\"native播放state\",2,\"42_old\"");
        check("Old play generation cannot corrupt same-song playback", state.Track == before);
        var repeated = Apply(state, 14500, track);
        repeated = Apply(repeated, 14501, "\"pause\",\"42_first\",\"42\"");
        check("Same-song repeat retires the prior play generation", repeated.Track is { TimelinePending: true, State: MediaState.Unknown } && repeated.PlayId == "");
        repeated = Apply(repeated, 14502, "\"native播放资源load完成，开始播放\",{\"songId\":\"42\"}");
        repeated = Apply(repeated, 14503, "\"native播放state\",1,\"42_repeat\"");
        check("Same-song repeat accepts its new playId", repeated.PlayId == "42_repeat" && repeated.Track?.State == MediaState.Playing);
        state = Apply(state, 15000, "\"播放模式切换\",\"playCycle\",\"playFm\"");
        check("FM removes previous while log modes remain read-only", state.Track is { PreviousRestricted: true, Shuffle: false, CanShuffle: false, CanRepeat: false });
        state = Apply(state, 16000, "\"播放模式切换\",\"playFm\",\"playOneCycle\"");
        check("Leaving FM restores previous without reporting shuffle or repeat", state.Track is { PreviousRestricted: false, Shuffle: false, Repeat: MediaRepeat.None, CanShuffle: false, CanRepeat: false });
        state = Apply(state, 17000, track.Replace("\"42\"", "\"43\"").Replace("曲目甲", "曲目乙"));
        state = Apply(state, 18000, "\"stop playId=\",\"42_first\"");
        check("Late stop after a track event does not stop the new song", state.Track is { Title: "曲目乙", TimelinePending: true, State: MediaState.Unknown });
        check("Unlabelled call completion is not a current-song end", Parse(19000, "\"onPlayEnd handle reason\",{\"reason\":\"call\",\"code\":-1}") is null);
        state = Apply(state, 20000, "\"native播放资源load完成，开始播放\",{\"songId\":\"43\"}");
        state = Apply(state, 21000, "\"native播放state\",1,\"43_second\"");
        state = Apply(state, 22000, "\"stop playId=\",\"43_second\"");
        check("Current playId end stops its clock", state.Track?.State == MediaState.Stopped);
        const string resume = "\"appRestore，播放信息恢复\",{\"id\":\"42\",\"current\":52.5,\"pause\":true}";
        string older = $"[{pid + 1}:6789:0101/120000:900:INFO:app.cpp(1231)] [2026-01-01 12:00:00] 【playing】,{track}";
        check("Track metadata is readable across process generations", NeteaseLog.Parse(older, pid, 100_000) is null && NeteaseLog.ParseTrack(older)?.Title == "曲目甲" && NeteaseLog.ParseTrack(Line(1000, resume)) is null);
        var known = new NeteaseTrackCache(); known.Remember(NeteaseLog.ParseTrack(older));
        var resumed = new NeteaseLogState().Apply(Parse(1000, resume)!, now, 100_000, known);
        check("Startup restoration completes its song from the last known metadata",
            resumed.Track is { Title: "曲目甲", Artist: "歌手甲 / 歌手乙", State: MediaState.Paused, PositionKnown: true, TimelinePending: false } &&
            resumed.Track.ReportedPosition.TotalSeconds == 52.5 && resumed.Track.Duration.TotalSeconds == 180 && resumed.CoverUrl == "https://p1.music.126.net/cover.jpg");
        resumed = Apply(resumed, 2000, "\"setPlayingPosition\",57.25");
        check("A completed restoration keeps following later position events", resumed.Track is { State: MediaState.Paused } && resumed.Track.ReportedPosition.TotalSeconds == 57.25);
        check("Restoration without known metadata leaves no media", new NeteaseLogState().Apply(Parse(1000, resume)!, now, 100_000, new NeteaseTrackCache()).Track is null && Apply(new(), 1000, resume).Track is null);
        var capped = new NeteaseTrackCache();
        for (int i = 0; i < 260; i++) capped.Remember(NeteaseLog.ParseTrack(Line(1000, track.Replace("\"42\"", $"\"{i}\""))));
        check("The metadata cache keeps a bounded number of songs", capped.Count < 260 && capped.Find("259") is not null && capped.Find("0") is null);
        var restore = Apply(new(), 1000, resume);
        restore = Apply(restore, 2000, track);
        check("Startup restoration before metadata rebuilds paused state", restore.Track is { State: MediaState.Paused, PositionKnown: true } && restore.Track.ReportedPosition.TotalSeconds == 52.5);
        check("Invalid positions do not poison the state", Parse(4000, "\"setPlayingPosition\",-1") is null && Parse(4000, "\"setPlayingPosition\",\"NaN\"") is null);
        var exit = NeteaseLog.Parse($"[{pid}:1:0101/120000:5000:INFO:app.cpp(1)] 【app】,{{\"actionId\":\"exitApp\"}}", pid, 100_000)!;
        restore = restore.Apply(exit, now, 100_000);
        check("Exit clears media and later old-process events cannot revive it", restore.Exited && restore.Track is null && Apply(restore, 6000, track).Track is null);

        // A literal encoded sample in the player's log format, separate from the implementation.
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "netease-position.elog");
        var encoded = File.ReadAllBytes(fixture); NeteaseLog.Decode(encoded);
        var decoded = Encoding.UTF8.GetString(encoded).TrimEnd('\r', '\n');
        var expected = File.ReadAllText(Path.ChangeExtension(fixture, ".txt")).TrimEnd('\r', '\n');
        check("Byte substitution matches an encoded log sample", decoded == expected);
        check("The encoded sample parses its exact decimal position", NeteaseLog.Parse(decoded, pid, 1_000_000_000)?.Seconds == 25.5882352);
    }
}
