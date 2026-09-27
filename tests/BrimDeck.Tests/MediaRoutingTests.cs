using BrimDeck.Core;

internal static class MediaRoutingTests
{
    public static async Task Run(Action<string, bool> check)
    {
        int directCalls = 0, systemCalls = 0;
        async Task<bool> Route(MediaDispatchResult result) => await MediaRouting.DispatchAsync(
            () => { directCalls++; return Task.FromResult(result); },
            () => { systemCalls++; return Task.FromResult(true); });
        check("Dedicated control succeeds without sending a system command", await Route(MediaDispatchResult.Sent) && directCalls == 1 && systemCalls == 0);
        check("Unsent unsupported operation falls back exactly once", await Route(MediaDispatchResult.Unavailable) && directCalls == 2 && systemCalls == 1);
        check("Uncertain dispatch never repeats a next-track command", !await Route(MediaDispatchResult.Uncertain) && directCalls == 3 && systemCalls == 1);
        var primary = new MediaTrack { Id = "netease:1", Source = "网易云音乐", Title = "Song A", Artist = "Artist", PositionKnown = false,
            TimelinePending = true, State = MediaState.Unknown, CanPrevious = true };
        var controls = new MediaTrack { Title = "Song B", Artist = "Other", Album = "Wrong album", End = TimeSpan.FromMinutes(3),
            ReportedPosition = TimeSpan.FromSeconds(30), State = MediaState.Playing, CanToggle = true, CanNext = true, CanSeek = true,
            CanShuffle = true, CanRepeat = true, Shuffle = true, Repeat = MediaRepeat.Track };
        var merged = MediaRouting.Merge(primary, controls);
        check("A supplement adds only basic playback controls", merged.CanToggle && merged.CanNext && merged.CanPrevious && !merged.CanSeek && !merged.CanShuffle && !merged.CanRepeat);
        check("Supplement metadata and state cannot replace the primary", merged.Title == "Song A" && merged.Artist == "Artist" && merged.Album == "" && merged.State == MediaState.Unknown && merged.Id == primary.Id);
        check("Pending primary timeline never borrows system data", !merged.HasTimeline && merged.TimelinePending && merged.Duration == TimeSpan.Zero);
        check("Supplement modes cannot leak into the primary", !merged.Shuffle && merged.Repeat == MediaRepeat.None);
        check("A primary without a supplement is unchanged", MediaRouting.Merge(primary, null) == primary);
        check("FM disables previous even when SMTC advertises it", !MediaRouting.Merge(primary with { PreviousRestricted = true }, controls with { CanPrevious = true }).CanPrevious);
    }
}
