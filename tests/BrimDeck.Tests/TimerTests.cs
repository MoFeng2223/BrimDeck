using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Timers;

internal static class TimerTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }
    internal static void Run(string root, Action<string, bool> check)
    {
        var clock = new Clock(); var timer = new TimerSession(clock); var options = new TimerOptions();
        int changed = 0, completed = 0; timer.Changed += () => changed++; timer.PhaseCompleted += _ => completed++;
        check("Timer: starts idle", !timer.Active && !timer.Running);
        check("Timer: Pomodoro defaults", options is { FocusMinutes: 25, ShortBreakMinutes: 5, LongBreakMinutes: 15, Rounds: 4 });
        check("Timer: starts focus", timer.Start(TimerMode.Pomodoro, options) && timer.Phase == TimerPhase.Focus && timer.Running);
        check("Timer: only one active session", !timer.Start(TimerMode.Countdown, options) && timer.Mode == TimerMode.Pomodoro);
        clock.Advance(2.4); timer.Tick();
        check("Timer: elapsed time comes from clock, not ticks", Math.Abs(timer.RemainingSeconds - 1497.6) < .001 && changed == 1);
        timer.Pause(); double paused = timer.RemainingSeconds; clock.Advance(3600); timer.Tick();
        check("Timer: sleep pause excludes sleeping time", timer.Status == TimerStatus.Paused && timer.RemainingSeconds == paused);
        timer.Resume(); clock.Advance(1.6);
        check("Timer: resume preserves fraction", Math.Abs(timer.ElapsedSeconds - 4) < .001);
        timer.Stop(); check("Timer: stop returns idle", !timer.Active && timer.Laps.Count == 0);

        options.FocusMinutes = options.ShortBreakMinutes = options.LongBreakMinutes = 1; options.Rounds = 2;
        timer.Start(TimerMode.Pomodoro, options); options.FocusMinutes = 90;
        check("Timer: session owns a copy of preferences", timer.DurationSeconds == 60);
        clock.Advance(60.25); timer.Tick();
        check("Timer: focus automatically enters short break", timer is { Phase: TimerPhase.ShortBreak, CompletedFocus: 1, Status: TimerStatus.Running } && Math.Abs(timer.ElapsedSeconds - .25) < .001);
        int boundaries = completed; timer.Tick(); timer.Tick();
        check("Timer: phase boundary fires once", completed == boundaries);
        clock.Advance(60); timer.Tick();
        check("Timer: break waits for next focus", timer.Status == TimerStatus.Waiting && !timer.Running);
        clock.Advance(500); timer.Tick();
        check("Timer: waiting never starts another focus", timer.Status == TimerStatus.Waiting && timer.CompletedFocus == 1);
        timer.Resume(); check("Timer: manual next focus", timer.Phase == TimerPhase.Focus && timer.Running && timer.ElapsedSeconds == 0);
        clock.Advance(60); timer.Tick();
        check("Timer: correct long break round", timer.Phase == TimerPhase.LongBreak && timer.CompletedFocus == 2 && timer.Running);
        clock.Advance(60); timer.Tick(); timer.Resume(); clock.Advance(60); timer.Tick();
        check("Timer: short breaks resume after long break", timer.Phase == TimerPhase.ShortBreak && timer.CompletedFocus == 3);
        timer.Stop(); options.FocusMinutes = 1; timer.Start(TimerMode.Pomodoro, options); clock.Advance(1000); timer.Tick();
        check("Timer: delayed tick stops at next-focus boundary", timer.Status == TimerStatus.Waiting && timer.CompletedFocus == 1);

        timer.Stop(); options.CountdownSeconds = 3; timer.Start(TimerMode.Countdown, options); clock.Advance(2.1);
        check("Timer: countdown rounds remaining time upward", TimerVisual(timer) == "0:01");
        clock.Advance(1); timer.Tick();
        check("Timer: countdown ends at zero", timer.Status == TimerStatus.Completed && timer.RemainingSeconds == 0);
        boundaries = completed; timer.Resume(); timer.Tick();
        check("Timer: completed countdown cannot accidentally restart", timer.Status == TimerStatus.Completed && completed == boundaries);
        timer.Stop(); timer.Start(TimerMode.Stopwatch, options); clock.Advance(12.5); timer.Lap(); clock.Advance(8.25); timer.Lap();
        check("Timer: stopwatch laps contain elapsed and split", timer.Laps.Count == 2 && timer.Laps[0].ElapsedSeconds == 12.5 && timer.Laps[1].SplitSeconds == 8.25);
        var snapshot = timer.Snapshot(); snapshot.Options.Rounds = 12; snapshot.Laps.Clear();
        check("Timer: snapshot does not expose mutable state", timer.Rounds == 2 && timer.Laps.Count == 2);
        timer.Pause(); timer.Lap(); check("Timer: paused stopwatch cannot record laps", timer.Laps.Count == 2);
        timer.ResetStopwatch(); check("Timer: reset stopwatch preserves pause", timer.ElapsedSeconds == 0 && timer.Status == TimerStatus.Paused && timer.Laps.Count == 0);
        timer.Resume(); clock.Advance(2); timer.ResetStopwatch(); clock.Advance(1);
        check("Timer: reset running stopwatch starts a new origin", timer.Running && timer.ElapsedSeconds == 1);
        for (int i = 0; i < 250; i++) { clock.Advance(1); timer.Lap(); }
        check("Timer: lap history bounded", timer.Laps.Count == 200);

        var store = new TimerSessionStore(Path.Combine(root, "timer")); store.Save(timer.Snapshot());
        var restored = new TimerSession(clock); restored.Restore(store.Load());
        check("Timer: restart restores progress paused", restored.Status == TimerStatus.Paused && restored.ElapsedSeconds == timer.ElapsedSeconds && restored.Laps.Count == 200);
        clock.Advance(10000); check("Timer: restored session does not count time offline", restored.ElapsedSeconds < timer.ElapsedSeconds);
        restored.Resume(); clock.Advance(3); check("Timer: restored session can resume", restored.Running && restored.ElapsedSeconds > 0);
        restored.Stop(); store.Save(restored.Snapshot());
        var stopped = new TimerSession(clock); stopped.Restore(store.Load()); check("Timer: stopped sessions stay stopped on restart", !stopped.Active);
        File.WriteAllText(store.FilePath, "broken json"); check("Timer: damaged snapshot ignored", store.Load() is null);
        var invalid = new TimerSession(clock); invalid.Restore(new() { Mode = TimerMode.Countdown, Phase = TimerPhase.Focus, Status = TimerStatus.Running });
        check("Timer: inconsistent snapshot ignored", !invalid.Active);
        invalid.Restore(new() { Mode = TimerMode.Countdown, Phase = TimerPhase.Countdown, Status = TimerStatus.Running, ElapsedSeconds = double.NaN });
        check("Timer: invalid elapsed sanitized", invalid.Status == TimerStatus.Paused && invalid.ElapsedSeconds == 0);
        check("Timer: clock formats seconds and hours", TimerSession.Format(299) == "4:59" && TimerSession.Format(3661) == "1:01:01" && TimerSession.Format(-1) == "0:00");
        check("Timer: remaining subsecond remains visible", TimerSession.Format(.1, true) == "0:01");
        check("Timer: settings copy independent", new DeckSettings().Copy().MiniApps is { Enabled: true });
        var settings = new DeckSettings(); var timerOptions = new TimerOptions(); var copiedOptions = timerOptions.Copy(); copiedOptions.FocusMinutes = 30;
        check("Timer: preferences deeply copied", timerOptions.FocusMinutes == 25);
        var bad = new TimerOptions { Width = double.NaN, Height = 900, Mode = (TimerMode)77, Rounds = -1, CountdownSeconds = 0 }; bad.Normalize();
        check("Timer: settings normalized", bad is { Width: 520, Height: 400, Mode: TimerMode.Pomodoro, Rounds: 1, CountdownSeconds: 1 });
        var old = JsonSerializer.Deserialize<DeckSettings>("{}")!; old.Normalize();
        check("Timer: old settings load with defaults", old.MiniApps.Enabled && timerOptions.FocusMinutes == 25);
        var legacy = JsonSerializer.Deserialize<DeckSettings>("{\"MiniApps\":{\"FocusMinutes\":35,\"Order\":[2,0,1]}}")!;
        var legacyCopy = legacy.Copy(); legacyCopy.MiniApps.LegacyValues!.Clear();
        check("Mini-apps: legacy timer settings round-trip independently", JsonSerializer.Serialize(legacy).Contains("FocusMinutes") && legacy.MiniApps.LegacyValues!.Count == 2);
        check("Timer: page enum appended", (int)DeckPage.MiniApps == 3 && (int)DefaultDeckPage.MiniApps == 4 && (int)DeckPage.Monitor == 2);
        check("Timer: mini-app page opening supported", DeckPages.Opening(DefaultDeckPage.MiniApps) == DeckPage.MiniApps);
        DeckPages.SetSize(settings, DeckPage.MiniApps, 560, 350);
        check("Timer: page owns its size", DeckPages.Size(settings, DeckPage.MiniApps) == (560, 350) && settings.MusicWidth == 440);
        settings.MiniApps.Enabled = false;
        check("Timer: disabled page excluded", !DeckPages.Visible(settings).Contains(DeckPage.MiniApps) && DeckPages.Resolve(settings, DeckPage.MiniApps) == DeckPage.Usage);
        var widgets = JsonSerializer.Deserialize<TimerOptions>("{}")!;
        check("Mini-apps: legacy preferences enable all three widgets", widgets.Modes.SequenceEqual(Enum.GetValues<TimerMode>()) && widgets.StopwatchLaps && widgets.StopwatchHundredths && widgets.StopwatchLapLimit == 200);
        widgets.PomodoroEnabled = false; widgets.Normalize();
        check("Mini-apps: disabled default falls back to first available widget", widgets.Mode == TimerMode.Countdown && !widgets.Allows(TimerMode.Pomodoro));
        widgets.Mode = TimerMode.Stopwatch; widgets.Enabled = false; widgets.Normalize();
        check("Mini-apps: module switch preserves selected widget", widgets.Mode == TimerMode.Stopwatch && !widgets.Allows(TimerMode.Stopwatch));
        var disabled = new TimerSession(clock);
        check("Mini-apps: core rejects disabled module", !disabled.Start(TimerMode.Stopwatch, widgets));
        widgets.Enabled = true;
        check("Mini-apps: core rejects disabled widget", !disabled.Start(TimerMode.Pomodoro, widgets));
        widgets.CountdownEnabled = widgets.StopwatchEnabled = false; widgets.Normalize();
        check("Mini-apps: every widget can be hidden without normalization enabling one", !widgets.Modes.Any());
        widgets.StopwatchEnabled = true; widgets.StopwatchLaps = false;
        disabled.Start(TimerMode.Stopwatch, widgets); clock.Advance(2); disabled.Lap();
        check("Mini-apps: disabled lap recording has no side effects", disabled.Laps.Count == 0 && !disabled.CanLap);
        disabled.Stop(); widgets.StopwatchLaps = true; widgets.StopwatchLapLimit = 2;
        disabled.Start(TimerMode.Stopwatch, widgets); widgets.StopwatchLapLimit = 1;
        clock.Advance(2); disabled.Lap(); clock.Advance(2); disabled.Lap(); disabled.Lap();
        check("Mini-apps: lap limit belongs to the active session", disabled.Laps.Count == 2 && !disabled.CanLap);
        var owned = disabled.Options; owned.FocusMinutes = 100;
        check("Mini-apps: presentation cannot mutate session preferences", disabled.Options.FocusMinutes == 25);
        widgets.StopwatchLapLimit = 500; widgets.Normalize();
        check("Mini-apps: lap limit is bounded", widgets.StopwatchLapLimit == 200);
        var persisted = JsonSerializer.Deserialize<TimerOptions>(JsonSerializer.Serialize(widgets))!;
        check("Mini-apps: widget preferences survive JSON roundtrip", !persisted.PomodoroEnabled && persisted.StopwatchEnabled && persisted.StopwatchLapLimit == 200);
        foreach (var (width, height) in new[] { (400d, 104d), (403d, 223d), (480d, 270d), (1160d, 104d), (1160d, 350d), (400d, 350d), (680d, 150d) })
        {
            var layout = MiniAppsLayout.For(width, height);
            check($"Mini-apps layout {width}x{height}: cards remain inside available bounds", layout.DetailHeight >= 30 && layout.DetailWidth >= 300 && layout.HeroHeight > 0 &&
                (layout.SideBySide ? layout.HeroWidth + layout.Gap + layout.DetailWidth <= width : layout.HeroHeight + layout.Gap + layout.DetailHeight + layout.Tabs + layout.Gap <= height));
            int rows = layout.LapRows, count = 200;
            var records = Enumerable.Range(0, MiniAppsLayout.PageCount(count, rows)).SelectMany(page => Enumerable.Range(page * rows, Math.Min(rows, count - page * rows))).ToArray();
            check($"Mini-apps layout {width}x{height}: every lap reachable without scrolling", records.SequenceEqual(Enumerable.Range(0, count)));
        }
        check("Mini-apps: narrow panels keep a vertical arrangement", !MiniAppsLayout.For(403, 223).SideBySide);
        check("Mini-apps: wide short panels use horizontal space", MiniAppsLayout.For(1160, 104).SideBySide && MiniAppsLayout.For(1160, 104).FullFields);
        bool editors = false, monotonic = true;
        for (int height = 104; height <= 350; height++)
        {
            bool visible = MiniAppsLayout.For(403, height).FullFields;
            if (editors && !visible) monotonic = false;
            editors = visible;
        }
        check("Mini-apps: growing the panel never hides its visible Pomodoro editors", monotonic && editors);
        var opening = new TimerOptions { DefaultMode = TimerMode.Countdown, Mode = TimerMode.Pomodoro };
        opening.ApplyDefaultMode();
        check("Mini-apps: fixed opening widget overrides last selection on entry", opening.Mode == TimerMode.Countdown);
        opening.Mode = TimerMode.Stopwatch;
        check("Mini-apps: browsing does not overwrite the configured default", opening.DefaultMode == TimerMode.Countdown);
        opening.ApplyDefaultMode();
        check("Mini-apps: reentry restores the configured default", opening.Mode == TimerMode.Countdown);
        opening.CountdownEnabled = false; opening.Normalize();
        check("Mini-apps: a hidden default falls back to remembering available selection", opening.DefaultMode is null && opening.Mode == TimerMode.Pomodoro);
        opening.DefaultMode = (TimerMode)999; opening.Normalize();
        check("Mini-apps: invalid opening widget is normalized", opening.DefaultMode is null);
        var legacyOpening = JsonSerializer.Deserialize<TimerOptions>("{\"Mode\":2}")!; legacyOpening.ApplyDefaultMode();
        check("Mini-apps: old preferences retain last used behavior", legacyOpening.DefaultMode is null && legacyOpening.Mode == TimerMode.Stopwatch);
        var longSession = new TimerSession(clock);
        longSession.Restore(new() { Mode = TimerMode.Stopwatch, Phase = TimerPhase.Stopwatch, Status = TimerStatus.Running, ElapsedSeconds = 360001.5,
            Laps = [new(1,360000,360000)] });
        check("Timer: long stopwatch restores exact elapsed time and laps after 100 hours", longSession.ElapsedSeconds == 360001.5 && longSession.Laps.Count == 1 && !longSession.Running);
        check("Timer: long stopwatch formatting does not silently stop at 99 hours", TimerSession.Format(longSession.ElapsedSeconds) == "100:00:01");
        var ordered = new TimerOptions { Order = [TimerMode.Stopwatch, TimerMode.Countdown, TimerMode.Pomodoro] };
        check("Mini-apps: configured order drives visible widget order", ordered.Modes.SequenceEqual(new[] { TimerMode.Stopwatch, TimerMode.Countdown, TimerMode.Pomodoro }));
        var orderedCopy = ordered.Copy(); orderedCopy.Order.Reverse();
        check("Mini-apps: widget order is deeply copied", ordered.Order[0] == TimerMode.Stopwatch && orderedCopy.Order[0] == TimerMode.Pomodoro);
        ordered.CountdownEnabled = false;
        check("Mini-apps: disabling a widget hides it without losing its place", ordered.Modes.SequenceEqual(new[] { TimerMode.Stopwatch, TimerMode.Pomodoro }) && ordered.Order[1] == TimerMode.Countdown);
        ordered.CountdownEnabled = true;
        check("Mini-apps: reenabled widgets return to their configured position", ordered.Modes.ElementAt(1) == TimerMode.Countdown);
        ordered.Order = [(TimerMode)99, TimerMode.Stopwatch, TimerMode.Stopwatch]; ordered.Normalize();
        check("Mini-apps: invalid and duplicate order entries normalize safely", ordered.Order.SequenceEqual(new[] { TimerMode.Stopwatch, TimerMode.Pomodoro, TimerMode.Countdown }));
        ordered.Order = null!; ordered.Normalize();
        check("Mini-apps: a null order restores all default positions", ordered.Order.SequenceEqual(Enum.GetValues<TimerMode>()));
        var orderJson = JsonSerializer.Serialize(ordered);
        check("Mini-apps: only the stored order is serialized", orderJson.Contains("\"Order\"") && !orderJson.Contains("\"Widgets\"") && !orderJson.Contains("\"Modes\""));
    }
    private static string TimerVisual(TimerSession timer) => TimerSession.Format(timer.RemainingSeconds, true);
}
