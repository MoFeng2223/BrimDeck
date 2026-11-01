using System.Diagnostics;
using BrimDeck.Modules;

internal static partial class Program
{
    private static async Task PerformanceChecks()
    {
        long clock = 1000;
        var counters = new ModulePerformanceTracker(() => clock, 1000);
        counters.Started("a", 120); counters.Call("a", "short", 10); counters.Call("a", "long", 40); counters.Error("a");
        var value = counters.Snapshot().Single();
        Check("performance activation has wall duration and count", value.StartupMilliseconds == 120 && value.Activations == 1);
        Check("performance aggregates calls, peak and slow count", value.Calls == 2 && value.TotalMilliseconds == 50 && value.LongestMilliseconds == 40 && value.SlowCalls == 1 && value.LongestOperation == "long");
        Check("performance uses five-second rate window", value.MillisecondsPerSecond == 10);
        clock = 6100; value = counters.Snapshot().Single();
        Check("idle performance rate expires without background sampling", value.MillisecondsPerSecond == 0 && value.TotalMilliseconds == 50 && value.Errors == 1);
        for (int i = 0; i < 10000; i++) { clock += 100; counters.Call("a", "tick", 1); }
        value = counters.Snapshot().Single(); Check("long session reuses bounded rate window", value.Calls == 10002 && value.MillisecondsPerSecond == 10);
        var scheduler = new Scheduler(); using var host = Host(scheduler);
        var definition = Def("measured", context => { context.Subscribe<int>(_ => { }); context.Every(TimeSpan.FromMilliseconds(20), () => { }); return ValueTask.CompletedTask; });
        await host.ReconcileAsync([definition]); host.Publish(1); scheduler.Tick();
        value = host.Performance.Single();
        Check("host instruments activation, events and timers", value.StartupMilliseconds >= 0 && value.Activations == 1 && value.Calls == 2);
        host.Invoke<bool>("measured", "throw", () => throw new Exception("expected"));
        for (int i = 0; i < 250; i++) host.Report("measured", "reported", new Exception("reported"));
        value = host.Performance.Single();
        Check("performance error total survives bounded error log", value.Errors == 251 && host.Faults.Count == 200 && value.Calls == 3);
        await host.ReconcileAsync([]); scheduler.Tick();
        Check("disabled module keeps totals without invoking callbacks", host.Performance.Single().Calls == 3 && scheduler.Callbacks.Count == 0);
        long started = Stopwatch.GetTimestamp();
        host.Measure("parent", "outer", () => host.Measure("child", "inner", () => { Thread.Sleep(20); return true; }));
        double wall = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var parent = host.Performance.Single(p => p.ModuleId == "parent"); var child = host.Performance.Single(p => p.ModuleId == "child");
        Check("nested measurements do not charge inner callback twice", child.TotalMilliseconds >= 15 && parent.TotalMilliseconds + child.TotalMilliseconds <= wall + 1 && parent.Calls == 1 && child.Calls == 1);
    }
}
