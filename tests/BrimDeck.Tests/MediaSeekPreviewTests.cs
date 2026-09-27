using BrimDeck.Core;

internal static class MediaSeekPreviewTests
{
    public static void Run(Action<string, bool> check)
    {
        var now = DateTimeOffset.UtcNow;
        var track = new MediaTrack { Id = "qq", SongId = "qq:one", Title = "Song", Artist = "Artist", State = MediaState.Playing,
            CanSeek = true, End = TimeSpan.FromSeconds(200), ReportedPosition = TimeSpan.FromSeconds(60), PositionAt = now.AddSeconds(-1) };
        var preview = new MediaSeekPreview();
        preview.Begin(track, .45, now);
        check("Seek preview immediately holds the target instead of advancing it", preview.Position(track, now.AddSeconds(1)).TotalSeconds == 90);
        var stale = track with { ReportedPosition = TimeSpan.FromSeconds(61), PositionAt = now.AddSeconds(1) };
        check("Old native progress cannot overwrite a forward seek", preview.Position(stale, now.AddSeconds(1)).TotalSeconds == 90 && preview.Pending is not null);
        var oldTarget = track with { ReportedPosition = TimeSpan.FromSeconds(90) };
        check("A sample from before the request cannot confirm its target", preview.Position(oldTarget, now.AddSeconds(1)).TotalSeconds == 90 && preview.Pending is not null);
        var confirmed = track with { ReportedPosition = TimeSpan.FromSeconds(90.2), PositionAt = now.AddSeconds(1.2) };
        check("New native progress confirms a seek and restores normal movement", Math.Abs(preview.Position(confirmed, now.AddSeconds(1.8)).TotalSeconds - 90.8) < .001 && preview.Pending is null);

        preview.Begin(track, .1, now);
        check("Old native progress cannot overwrite a backward seek", preview.Position(stale, now.AddSeconds(1)).TotalSeconds == 20);
        var first = preview.Begin(track, .65, now);
        var second = preview.Begin(track, .2, now.AddMilliseconds(100));
        check("An earlier failed command cannot cancel a newer seek", !preview.Cancel(first) && ReferenceEquals(preview.Pending, second));
        preview.Observe(track with { ReportedPosition = TimeSpan.FromSeconds(130), PositionAt = now.AddMilliseconds(200) }, now.AddMilliseconds(200));
        check("An earlier seek confirmation cannot release a newer target", preview.Pending == second && preview.Position(stale, now.AddSeconds(1)).TotalSeconds == 40);
        check("Rejected latest seek restores real progress", preview.Cancel(second) && preview.Position(track, now).TotalSeconds == 61);

        var paused = track with { State = MediaState.Paused };
        preview.Begin(paused, .45, now);
        check("Paused seek stays at the target while waiting", preview.Position(paused, now.AddSeconds(2)).TotalSeconds == 90);
        var pausedConfirmed = paused with { ReportedPosition = TimeSpan.FromSeconds(90), PositionAt = now.AddSeconds(2) };
        check("Confirmed paused seek remains stationary", preview.Position(pausedConfirmed, now.AddSeconds(3)).TotalSeconds == 90 && preview.Pending is null);

        bool cleared = true;
        foreach (var changed in new MediaTrack?[] { null, track with { SongId = "qq:two" }, track with { CanSeek = false } })
        {
            preview.Begin(track, .45, now); preview.Observe(changed, now.AddSeconds(1));
            cleared &= preview.Pending is null;
        }
        check("Source, song or capability changes clear pending seek", cleared);
        preview.Begin(track, .45, now);
        check("An unconfirmed seek expires and restores the player's position", preview.Position(stale, now.AddSeconds(5)) == stale.Position(now.AddSeconds(5)) && preview.Pending is null);
        var near = track with { ReportedPosition = TimeSpan.FromSeconds(89), PositionAt = now.AddMilliseconds(-100) };
        preview.Begin(near, .45, now);
        check("Clock extrapolation reaching the target cannot confirm a seek", preview.Position(near, now.AddSeconds(2)).TotalSeconds == 90 && preview.Pending is not null);
        var bounded = track with { Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(210), MaxSeek = TimeSpan.FromSeconds(150) };
        preview.Begin(bounded, 1, now);
        check("Seek preview respects native seek bounds and timeline origin", preview.Position(bounded, now).TotalSeconds == 140);
    }
}
