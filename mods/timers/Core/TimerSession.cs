using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Timers;

public enum TimerMode { Pomodoro, Countdown, Stopwatch }
public enum TimerPhase { Focus, ShortBreak, LongBreak, Countdown, Stopwatch }
public enum TimerStatus { Idle, Running, Paused, Waiting, Completed }

public sealed class TimerOptions
{
    public bool Enabled { get; set; } = true;
    public bool PomodoroEnabled { get; set; } = true;
    public bool CountdownEnabled { get; set; } = true;
    public bool StopwatchEnabled { get; set; } = true;
    public List<TimerMode> Order { get; set; } = [TimerMode.Pomodoro, TimerMode.Countdown, TimerMode.Stopwatch];
    public double Width { get; set; } = 520;
    public double Height { get; set; } = 320;
    public TimerMode Mode { get; set; }
    public TimerMode? DefaultMode { get; set; }
    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public int Rounds { get; set; } = 4;
    public int CountdownSeconds { get; set; } = 300;
    public bool StopwatchLaps { get; set; } = true;
    public int StopwatchLapLimit { get; set; } = 200;
    public bool StopwatchHundredths { get; set; } = true;
    public bool Allows(TimerMode mode) => Enabled && WidgetEnabled(mode);
    public bool WidgetEnabled(TimerMode mode) => mode switch
    { TimerMode.Pomodoro => PomodoroEnabled, TimerMode.Countdown => CountdownEnabled, TimerMode.Stopwatch => StopwatchEnabled, _ => false };
    [JsonIgnore] public IEnumerable<TimerMode> Widgets => (Order ?? []).Where(Enum.IsDefined).Concat(Enum.GetValues<TimerMode>()).Distinct();
    [JsonIgnore] public IEnumerable<TimerMode> Modes => Widgets.Where(WidgetEnabled);
    public void ApplyDefaultMode()
    { if (DefaultMode is { } mode && WidgetEnabled(mode)) Mode = mode; }
    public TimerOptions Copy()
    {
        var copy = (TimerOptions)MemberwiseClone(); copy.Order = [.. Widgets]; return copy;
    }
    public void Normalize()
    {
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 440, 1200) : 520;
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 140, 400) : 320;
        Order = [.. Widgets];
        if (!Enum.IsDefined(Mode)) Mode = TimerMode.Pomodoro;
        if (!WidgetEnabled(Mode)) Mode = Modes.FirstOrDefault();
        if (DefaultMode is { } opening && !WidgetEnabled(opening)) DefaultMode = null;
        FocusMinutes = Math.Clamp(FocusMinutes, 1, 180);
        ShortBreakMinutes = Math.Clamp(ShortBreakMinutes, 1, 60);
        LongBreakMinutes = Math.Clamp(LongBreakMinutes, 1, 120);
        Rounds = Math.Clamp(Rounds, 1, 12);
        CountdownSeconds = Math.Clamp(CountdownSeconds, 1, 359999);
        StopwatchLapLimit = Math.Clamp(StopwatchLapLimit, 1, 200);
    }
}

public sealed record TimerLap(int Number, double ElapsedSeconds, double SplitSeconds);

public sealed class TimerSnapshot
{
    public TimerMode Mode { get; set; }
    public TimerPhase Phase { get; set; }
    public TimerStatus Status { get; set; }
    public double ElapsedSeconds { get; set; }
    public int CompletedFocus { get; set; }
    public TimerOptions Options { get; set; } = new();
    public List<TimerLap> Laps { get; set; } = [];
}

// A single session, independent of WPF and its redraw rate. A monotonic clock supplies elapsed time;
// commands and phase boundaries notify persistence, while ordinary display ticks never write files.
public sealed class TimerSession(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _started;
    private double _elapsed;
    private readonly List<TimerLap> _laps = [];
    public TimerMode Mode { get; private set; }
    public TimerPhase Phase { get; private set; }
    public TimerStatus Status { get; private set; }
    public int CompletedFocus { get; private set; }
    private TimerOptions _options = new();
    public int Rounds => _options.Rounds;
    public bool Active => Status != TimerStatus.Idle;
    public bool Running => Status == TimerStatus.Running;
    public IReadOnlyList<TimerLap> Laps => _laps.AsReadOnly();
    public double ElapsedSeconds => _elapsed + (Running ? Math.Max(0, _clock.GetElapsedTime(_started).TotalSeconds) : 0);
    public double DurationSeconds => Phase switch
    {
        TimerPhase.Focus => _options.FocusMinutes * 60,
        TimerPhase.ShortBreak => _options.ShortBreakMinutes * 60,
        TimerPhase.LongBreak => _options.LongBreakMinutes * 60,
        TimerPhase.Countdown => _options.CountdownSeconds,
        _ => 0
    };
    public double RemainingSeconds => Math.Max(0, DurationSeconds - ElapsedSeconds);
    public TimerOptions Options => _options.Copy();
    public bool CanLap => Running && Mode == TimerMode.Stopwatch && _options.StopwatchLaps && _laps.Count < _options.StopwatchLapLimit;
    public double Progress => DurationSeconds > 0 ? Math.Clamp(ElapsedSeconds / DurationSeconds, 0, 1) : 0;
    public event Action? Changed;
    public event Action<TimerPhase>? PhaseCompleted;

    public bool Start(TimerMode mode, TimerOptions options)
    {
        if (Active || !options.Allows(mode)) return false;
        _options = options.Copy(); _options.Normalize(); Mode = mode;
        Phase = mode switch { TimerMode.Countdown => TimerPhase.Countdown, TimerMode.Stopwatch => TimerPhase.Stopwatch, _ => TimerPhase.Focus };
        _elapsed = 0; CompletedFocus = 0; _laps.Clear();
        Status = TimerStatus.Running; _started = _clock.GetTimestamp(); Changed?.Invoke(); return true;
    }
    public void Pause()
    {
        Tick();
        if (!Running) return;
        _elapsed = ElapsedSeconds; Status = TimerStatus.Paused; Changed?.Invoke();
    }
    public void Resume()
    {
        if (Status == TimerStatus.Waiting)
        { Phase = TimerPhase.Focus; _elapsed = 0; }
        else if (Status != TimerStatus.Paused) return;
        Status = TimerStatus.Running; _started = _clock.GetTimestamp(); Changed?.Invoke();
    }
    public void Stop()
    {
        if (!Active) return;
        Status = TimerStatus.Idle; _elapsed = 0; CompletedFocus = 0; _laps.Clear(); Changed?.Invoke();
    }
    public void ResetStopwatch()
    {
        if (!Active || Mode != TimerMode.Stopwatch) return;
        _elapsed = 0; _laps.Clear(); _started = _clock.GetTimestamp(); Changed?.Invoke();
    }
    public void Lap()
    {
        if (!Running || Mode != TimerMode.Stopwatch || !_options.StopwatchLaps) return;
        double elapsed = ElapsedSeconds, last = _laps.Count > 0 ? _laps[^1].ElapsedSeconds : 0;
        if (_laps.Count >= _options.StopwatchLapLimit) return;
        _laps.Add(new(_laps.Count + 1, elapsed, Math.Max(0, elapsed - last))); Changed?.Invoke();
    }
    public void Tick()
    {
        if (!Running || Mode == TimerMode.Stopwatch || ElapsedSeconds < DurationSeconds) return;
        double overflow = ElapsedSeconds - DurationSeconds;
        var finished = Phase;
        if (Mode == TimerMode.Countdown) { _elapsed = DurationSeconds; Status = TimerStatus.Completed; }
        else if (Phase == TimerPhase.Focus)
        {
            CompletedFocus++;
            Phase = CompletedFocus % _options.Rounds == 0 ? TimerPhase.LongBreak : TimerPhase.ShortBreak;
            _elapsed = Math.Min(overflow, DurationSeconds); _started = _clock.GetTimestamp();
            if (overflow >= DurationSeconds) { Status = TimerStatus.Waiting; finished = Phase; }
        }
        else { _elapsed = DurationSeconds; Status = TimerStatus.Waiting; }
        Changed?.Invoke(); PhaseCompleted?.Invoke(finished);
    }
    public TimerSnapshot Snapshot() => new()
    {
        Mode = Mode, Phase = Phase, Status = Status, ElapsedSeconds = ElapsedSeconds,
        CompletedFocus = CompletedFocus, Options = _options.Copy(), Laps = [.. _laps]
    };
    public void Restore(TimerSnapshot? saved)
    {
        if (saved is null || !Enum.IsDefined(saved.Mode) || !Enum.IsDefined(saved.Phase) || !Enum.IsDefined(saved.Status)) return;
        bool validPhase = saved.Mode switch
        {
            TimerMode.Pomodoro => saved.Phase is TimerPhase.Focus or TimerPhase.ShortBreak or TimerPhase.LongBreak,
            TimerMode.Countdown => saved.Phase == TimerPhase.Countdown,
            _ => saved.Phase == TimerPhase.Stopwatch
        };
        if (!validPhase) return;
        _options = saved.Options?.Copy() ?? new(); _options.Normalize();
        Mode = saved.Mode; Phase = saved.Phase;
        Status = saved.Status == TimerStatus.Running ? TimerStatus.Paused : saved.Status;
        if (Status == TimerStatus.Waiting && Mode != TimerMode.Pomodoro) Status = TimerStatus.Paused;
        _elapsed = double.IsFinite(saved.ElapsedSeconds) ? Math.Clamp(saved.ElapsedSeconds, 0, Mode == TimerMode.Stopwatch ? TimeSpan.MaxValue.TotalSeconds : DurationSeconds) : 0;
        CompletedFocus = Math.Clamp(saved.CompletedFocus, 0, 1_000_000);
        _laps.Clear(); double previous = 0;
        foreach (var lap in (saved.Laps ?? []).Take(200))
            if (lap is not null && Mode == TimerMode.Stopwatch && double.IsFinite(lap.ElapsedSeconds) && lap.ElapsedSeconds >= previous && lap.ElapsedSeconds <= _elapsed)
            { _laps.Add(new(_laps.Count + 1, lap.ElapsedSeconds, lap.ElapsedSeconds - previous)); previous = lap.ElapsedSeconds; }
        Changed?.Invoke();
    }
    public static string Format(double seconds, bool countdown = false)
    {
        seconds = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        var total = (long)Math.Min(TimeSpan.MaxValue.TotalSeconds, countdown ? Math.Ceiling(seconds) : Math.Floor(seconds));
        return total >= 3600 ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}" : $"{total / 60}:{total % 60:00}";
    }
}

public sealed class TimerSessionStore(string directory)
{
    public string FilePath => Path.Combine(directory, "timer-session.json");
    public TimerSnapshot? Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<TimerSnapshot>(File.ReadAllText(FilePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public void Save(TimerSnapshot snapshot)
    {
        Directory.CreateDirectory(directory);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
        File.Move(temporary, FilePath, true);
    }
}
