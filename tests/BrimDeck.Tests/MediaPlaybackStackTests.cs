using BrimDeck.Core;

internal static class MediaPlaybackStackTests
{
    public static void Run(Action<string, bool> check)
    {
        var t0 = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        static MediaTrack T(string id, MediaState state, string? source = null) => new() { Id = id, Source = source ?? id, State = state };
        const MediaState P = MediaState.Playing, S = MediaState.Paused;

        var stack = new MediaPlaybackStack();
        check("A single playing source is shown", stack.Update([T("A", P)], t0).Current == "A");
        var b = stack.Update([T("A", P), T("B", P)], t0.AddSeconds(10));
        check("A later source is shown above an earlier one", b.Current == "B" && b.Started == "B");
        var bPaused = stack.Update([T("A", P), T("B", S)], t0.AddSeconds(20));
        check("A source that just stopped is held briefly", bPaused.Current == "B" && bPaused.RecheckAt == t0.AddSeconds(20) + MediaPlaybackStack.Grace);
        check("After the hold the earlier playing source returns", stack.Update([T("A", P), T("B", S)], t0.AddSeconds(24)).Current == "A");
        var c = stack.Update([T("A", P), T("B", S), T("C", P)], t0.AddSeconds(30));
        check("A new source goes on top of the returned one", c.Current == "C" && c.Started == "C");
        stack.Update([T("A", S), T("B", S), T("C", P)], t0.AddSeconds(40));
        check("Stopping a lower source leaves the top unchanged", stack.Update([T("A", S), T("B", S), T("C", P)], t0.AddSeconds(50)).Current == "C");
        check("Resuming an older source moves it to the top", stack.Update([T("A", P), T("B", S), T("C", P)], t0.AddSeconds(60)).Current == "A");

        var gap = new MediaPlaybackStack();
        gap.Update([T("A", P)], t0); gap.Update([T("A", P), T("B", P)], t0.AddSeconds(1));
        gap.Update([T("A", MediaState.Unknown), T("B", P)], t0.AddSeconds(100));
        var songChange = gap.Update([T("A", P), T("B", P)], t0.AddSeconds(101));
        check("A song change in an older player does not move it above a newer one", songChange.Current == "B" && songChange.Started is null);
        gap.Update([T("A", P), T("B", MediaState.Unknown)], t0.AddSeconds(200));
        check("A song change in the top player does not show the older one", gap.Update([T("A", P), T("B", MediaState.Unknown)], t0.AddSeconds(201)).Current == "B");

        var closed = new MediaPlaybackStack();
        closed.Update([T("A", P)], t0); closed.Update([T("A", P), T("B", P)], t0.AddSeconds(1));
        check("Closing the top player shows the next playing one at once", closed.Update([T("A", P)], t0.AddSeconds(2)).Current == "A");

        var idle = new MediaPlaybackStack();
        idle.Update([T("A", P)], t0); idle.Update([T("A", P), T("B", P)], t0.AddSeconds(1));
        idle.Update([T("A", P), T("B", S)], t0.AddSeconds(2));
        idle.Update([T("A", S), T("B", S)], t0.AddSeconds(10));
        check("With nothing playing the last shown source stays", idle.Update([T("A", S), T("B", S)], t0.AddSeconds(20), previous: "A").Current == "A");
        check("A paused source that appears later does not take over", idle.Update([T("A", P), T("B", S), T("D", S)], t0.AddSeconds(30)).Current == "A");

        var startup = new MediaPlaybackStack();
        check("At startup the Windows current session is treated as most recent",
            startup.Update([T("A", P), T("B", P), T("C", P)], t0, systemCurrent: "B").Current == "B");

        var renamed = new MediaPlaybackStack();
        renamed.Update([T("qq:1", P, "QQ 音乐")], t0); renamed.Update([T("qq:1", P, "QQ 音乐"), T("B", P)], t0.AddSeconds(1));
        var fallback = renamed.Update([T("system:q", P, "QQ 音乐"), T("B", P)], t0.AddSeconds(2));
        check("A player whose route changes identity keeps its place", fallback.Current == "B" && fallback.Started is null);

        MediaTrack[] before = [T("qq:1", P, "QQ 音乐"), T("B", P)];
        check("A pin stays on a source that is still present", MediaPlaybackStack.FollowPin("qq:1", "QQ 音乐", before, before) == "qq:1");
        check("A pin follows the same player to its new route",
            MediaPlaybackStack.FollowPin("qq:1", "QQ 音乐", [T("system:q", S, "QQ 音乐"), T("B", P)], before) == "system:q");
        check("A pin is released when the pinned player closes", MediaPlaybackStack.FollowPin("qq:1", "QQ 音乐", [T("B", P)], before) is null);
        check("A pin never moves to a different player that was already open",
            MediaPlaybackStack.FollowPin("qq:1", "QQ 音乐", [T("B", P, "QQ 音乐")], before) is null);
    }
}
