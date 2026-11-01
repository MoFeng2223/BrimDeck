using System.Diagnostics;
using System.IO;
using BrimDeck;
using BrimDeck.Modules;

internal static partial class Program
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(string name, Func<bool> condition)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition() && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(2)) await Task.Delay(5);
        Check(name, condition());
    }
    private static ModuleHost LifecycleHost(TimeSpan? budget = null) => new(Path.Combine(_root, Guid.NewGuid().ToString("N")), new Scheduler(), [], App.ModuleKinds(), [])
        { SlowStartThreshold = budget ?? TimeSpan.FromSeconds(5) };

    private static async Task LifecycleChecks()
    {
        using (var host = LifecycleHost(TimeSpan.FromMilliseconds(30)))
        {
            var release = Signal(); int starts = 0, childStarts = 0; IModuleContext? context = null;
            var slow = Def("slow-async", async c => { starts++; context = c; c.Register("page", Page("late page")); await release.Task; });
            var child = Def("child-async", c => { childStarts++; c.Register("page", Page("dependent")); return ValueTask.CompletedTask; }, depends: ["slow-async"]);
            var healthy = Def("healthy", c => { c.Register("page", Page("healthy")); return ValueTask.CompletedTask; });
            var first = host.ReconcileAsync([child, slow, healthy]);
            Check("independent module ready while another awaits activation", host.Modules.Single(m => m.Id == "healthy").Status == ModuleStatus.Ready && !first.IsCompleted);
            Check("dependency waiter has distinct state without running factory", host.Modules.Single(m => m.Id == "child-async").Status == ModuleStatus.Waiting && childStarts == 0);
            Guid generation = host.Modules.Single(m => m.Id == "slow-async").Generation;
            await Until("slow threshold marks startup without failing or cancellation", () => host.Modules.Single(m => m.Id == "slow-async") is { Status: ModuleStatus.Starting, Slow: true });
            Check("slow activation keeps lifetime and staged resources", !context!.Lifetime.IsCancellationRequested && !host.Registry.List<ModulePage>().Any(p => p.ModuleId == "slow-async") && host.Faults.Count == 0);
            var added = Def("new-independent", c => { c.Register("page", Page("new")); return ValueTask.CompletedTask; });
            var second = host.ReconcileAsync([child, slow, healthy, added]);
            Check("followup can activate unrelated module without waiting", host.Modules.Single(m => m.Id == "new-independent").Status == ModuleStatus.Ready);
            Check("unchanged pending activation retains identity and deadline", starts == 1 && host.Modules.Single(m => m.Id == "slow-async").Generation == generation);
            var third = host.ReconcileAsync([child, slow, healthy with { Enabled = false }, added]);
            Check("followup disables unrelated module immediately", host.Modules.Single(m => m.Id == "healthy").Status == ModuleStatus.Disabled && !host.Registry.List<ModulePage>().Any(p => p.ModuleId == "healthy"));
            release.SetResult(); await Task.WhenAll(first, second, third);
            Check("slow activation eventually commits normally", host.Modules.Single(m => m.Id == "slow-async") is { Status: ModuleStatus.Ready, Slow: true } && host.Registry.List<ModulePage>().Any(p => p.ModuleId == "slow-async"));
            Check("dependent starts after exact dependency generation is ready", childStarts == 1 && host.Modules.Single(m => m.Id == "child-async").Status == ModuleStatus.Ready);
            Check("waiting time does not mark dependent activation as slow", !host.Modules.Single(m => m.Id == "child-async").Slow);
        }
        using (var host = LifecycleHost())
        {
            var release = Signal(); IModuleContext? old = null; int cleanup = 0;
            var oldDefinition = Def("replace", async c => { old = c; c.OnDispose(() => cleanup++); await release.Task; c.Report("late old error", new Exception("obsolete")); throw new Exception("old activation failed late"); });
            var previous = host.ReconcileAsync([oldDefinition]);
            Guid generation = host.Modules.Single().Generation;
            var replacement = Def("replace", c => { c.Register("current", Page("current")); return ValueTask.CompletedTask; }, "v2");
            await host.ReconcileAsync([replacement]); await previous;
            Guid current = host.Modules.Single().Generation;
            Check("replacement creates a new activation generation", current != Guid.Empty && current != generation && cleanup == 1);
            release.SetResult(); await Task.Delay(25);
            Check("late old activation error cannot overwrite replacement", host.Modules.Single() is { Status: ModuleStatus.Ready } && host.Modules.Single().Generation == current && host.Faults.Count == 0);
            old!.Report("late callback", new Exception("retired"));
            Check("late reports do not increase current module errors", host.Faults.Count == 0 && host.Performance.Single().Errors == 0);
            Throws("retired activation cannot publish", () => old.Publish("late"));
            Throws("retired activation cannot commit data", () => old.Storage.Write("snapshot", "late"));
            Check("replacement contribution survives retired completion", host.Registry.List<ModulePage>().Single().Id == "replace:current");
        }
        using (var host = LifecycleHost())
        {
            var release = Signal(); int children = 0, disposed = 0;
            var parent = Def("parent", async c => { c.OnDispose(() => disposed++); await release.Task; });
            var child = Def("dependent", _ => { children++; return ValueTask.CompletedTask; }, depends: ["parent"]);
            var pending = host.ReconcileAsync([child, parent]);
            await host.ReconcileAsync([child with { Enabled = false }, parent with { Enabled = false }]); await pending;
            Check("disable cancels waiting graph without invoking child", children == 0 && disposed == 1 && host.Modules.All(m => m.Status == ModuleStatus.Disabled));
            release.SetResult(); await Task.Delay(15);
            Check("cancelled parent completion does not resurrect disabled graph", host.Modules.All(m => m.Status == ModuleStatus.Disabled) && host.Faults.Count == 0);
        }
        using (var host = LifecycleHost())
        {
            var release = Signal(); int childStarts = 0;
            var parent = Def("versioned-parent", async _ => await release.Task);
            var child = Def("versioned-child", c => { childStarts++; c.Register("page", Page("child")); return ValueTask.CompletedTask; }, depends: ["versioned-parent"]);
            var pending = host.ReconcileAsync([child, parent]); Guid oldChild = host.Modules.Single(m => m.Id == "versioned-child").Generation;
            var newParent = Def("versioned-parent", Done, "v2"); await host.ReconcileAsync([child, newParent]); await pending;
            Check("dependency replacement also replaces waiting dependent", host.Modules.Single(m => m.Id == "versioned-child").Generation != oldChild && childStarts == 1);
            release.SetResult(); await Task.Delay(15); Check("old dependency result cannot activate child twice", childStarts == 1);
        }
        using (var host = LifecycleHost())
        {
            var release = Signal(); int starts = 0;
            var shared = Def("shared-pending", async _ => { starts++; await release.Task; });
            var scans = Enumerable.Range(0, 12).Select(_ => Task.Run(() => host.ReconcileAsync([shared]))).ToArray();
            await Until("concurrent scans share pending activation", () => Volatile.Read(ref starts) == 1);
            release.SetResult(); await Task.WhenAll(scans);
            Check("concurrent scans do not construct duplicate generations", starts == 1 && host.Modules.Single().Status == ModuleStatus.Ready);
        }
        var trace = new List<string>();
        using (var host = LifecycleHost())
        {
            var release = Signal(); int constructed = 0;
            var slow = Def("unrelated-slow-dependency", async _ => await release.Task);
            var failed = Def("failed-dependency", _ => throw new Exception("dependency failed"));
            var dependent = Def("multiple-dependencies", _ => { constructed++; return ValueTask.CompletedTask; }, depends: ["unrelated-slow-dependency", "failed-dependency"]);
            var pending = host.ReconcileAsync([dependent, slow, failed]);
            await Until("failed dependency immediately blocks child despite another pending dependency", () => host.Modules.Single(m => m.Id == "multiple-dependencies").Status == ModuleStatus.DependencyFailed);
            Check("blocked child does not construct while slow sibling keeps running", constructed == 0 && host.Modules.Single(m => m.Id == "unrelated-slow-dependency").Status == ModuleStatus.Starting);
            await host.ReconcileAsync([]); await pending; release.SetResult();
        }
        using (var host = LifecycleHost())
        {
            var parent = Def("shutdown-parent", c => { c.OnDispose(() => trace.Add("parent")); return ValueTask.CompletedTask; });
            var child = Def("shutdown-child", c => { c.OnDispose(() => trace.Add("child")); return ValueTask.CompletedTask; }, depends: ["shutdown-parent"]);
            await host.ReconcileAsync([child, parent]); await host.ReconcileAsync([]);
            Check("teardown releases dependents before their dependency", trace.SequenceEqual(["child", "parent"]));
        }
        using (var host = LifecycleHost())
        using (var cancellation = new CancellationTokenSource())
        {
            var release = Signal(); IModuleContext? context = null;
            var definition = Def("cancelled", async c => { context = c; await release.Task; });
            var pending = host.ReconcileAsync([definition], cancellation.Token); cancellation.Cancel();
            try { await pending; } catch (OperationCanceledException) { }
            await Until("caller cancellation releases pending activation", () => context!.Lifetime.IsCancellationRequested);
            Check("normal activation cancellation is not a plugin fault", host.Faults.Count == 0 && host.Modules.Single().Status == ModuleStatus.Disabled);
            release.SetResult();
        }
        using (var host = LifecycleHost())
        {
            IModuleContext? context = null;
            await host.ReconcileAsync([Def("cleanup-fault", c => { context = c; c.OnDispose(() => throw new Exception("cleanup failed")); return ValueTask.CompletedTask; })]);
            Guid generation = host.Modules.Single().Generation; await host.ReconcileAsync([]);
            Check("cleanup failures retain owning generation", host.Faults.Single() is { Operation: "dispose" } fault && fault.Generation == generation);
            context!.Report("retired cleanup", new Exception("late")); Check("completed teardown rejects later diagnostics", host.Faults.Count == 1);
        }
        Check("existing status enum numbers preserved", (int)ModuleStatus.Disabled == 0 && (int)ModuleStatus.DependencyFailed == 4 && (int)ModuleStatus.Waiting == 5);
    }
}
