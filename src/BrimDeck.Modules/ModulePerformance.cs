using System.Diagnostics;

namespace BrimDeck.Modules;

public sealed record ModulePerformance(string ModuleId, double? StartupMilliseconds, long Activations,
    double MillisecondsPerSecond, double TotalMilliseconds, double LongestMilliseconds,
    long Calls, long SlowCalls, long Errors, string? LongestOperation);

// A fixed five-second window. Sampling has no background timer and retains no call history.
internal sealed class ModulePerformanceTracker(Func<long> clock, long frequency)
{
    private sealed class Counter
    {
        internal double? Startup;
        internal long Activations, Total, Longest, Calls, Slow, Errors;
        internal string? Operation;
        internal readonly long[] Epoch = Enumerable.Repeat(long.MinValue, 50).ToArray();
        internal readonly long[] Ticks = new long[50];
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Counter> _counters = [];
    internal ModulePerformanceTracker() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency) { }
    private Counter Get(string id) => _counters.TryGetValue(id, out var value) ? value : _counters[id] = new();
    private long Epoch => clock() / Math.Max(1, frequency / 10);
    internal void Started(string id, long elapsed)
    { lock (_gate) { var value = Get(id); value.Startup = Math.Max(0, elapsed) * 1000d / frequency; value.Activations++; } }
    internal void Error(string id) { lock (_gate) Get(id).Errors++; }
    internal void Call(string id, string operation, long elapsed)
    {
        elapsed = Math.Max(0, elapsed);
        lock (_gate)
        {
            var value = Get(id); value.Total += elapsed; value.Calls++;
            if (elapsed > value.Longest) { value.Longest = elapsed; value.Operation = operation; }
            if (elapsed * 1000d / frequency >= 16) value.Slow++;
            long epoch = Epoch; int slot = (int)(epoch % 50);
            if (value.Epoch[slot] != epoch) { value.Epoch[slot] = epoch; value.Ticks[slot] = 0; }
            value.Ticks[slot] += elapsed;
        }
    }
    internal IReadOnlyList<ModulePerformance> Snapshot()
    {
        lock (_gate)
        {
            long epoch = Epoch;
            return _counters.Select(pair =>
            {
                var value = pair.Value; long recent = 0;
                for (int i = 0; i < 50; i++) if (value.Epoch[i] > epoch - 50 && value.Epoch[i] <= epoch) recent += value.Ticks[i];
                return new ModulePerformance(pair.Key, value.Startup, value.Activations, recent * 1000d / frequency / 5,
                    value.Total * 1000d / frequency, value.Longest * 1000d / frequency, value.Calls, value.Slow, value.Errors, value.Operation);
            }).ToArray();
        }
    }
}

public sealed partial class ModuleHost
{
    private readonly ModulePerformanceTracker _performance = new();
    [ThreadStatic] private static Measurement? _measurement;
    private sealed class Measurement { internal long Children; }
    public IReadOnlyList<ModulePerformance> Performance => _performance.Snapshot();

    // Record exclusive synchronous callback time: nested calls are charged once to their own module.
    public T Measure<T>(string id, string operation, Func<T> callback)
    {
        long start = Stopwatch.GetTimestamp(); var parent = _measurement; var current = new Measurement(); _measurement = current;
        try { return callback(); }
        finally
        {
            long elapsed = Stopwatch.GetTimestamp() - start; _measurement = parent;
            if (parent is not null) parent.Children += elapsed;
            _performance.Call(id, operation, elapsed - current.Children);
        }
    }
}
