using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Modules;

internal static partial class Program
{
    private sealed class ProbeView : IModuleView, IModuleLayoutView
    {
        internal readonly Grid Root;
        internal int Disposals, Refreshes, DisposalThread;
        internal bool ThrowOnRefresh;
        internal ModuleViewport? Viewport;
        internal double LegacyScale;
        internal ProbeView(Grid? root = null) => Root = root ?? new();
        public FrameworkElement Element => Root;
        public void SetViewport(double width, double height, double scale = 1) => LegacyScale = scale;
        public void SetViewport(ModuleViewport viewport) => Viewport = viewport;
        public void Refresh() { Refreshes++; if (ThrowOnRefresh) throw new Exception("first refresh failed"); }
        public void Dispose() { Root.Dispatcher.VerifyAccess(); Interlocked.Increment(ref Disposals); DisposalThread = Environment.CurrentManagedThreadId; Root.Children.Clear(); }
    }
    private sealed class AsyncActivity(Func<ModuleViewContext, ValueTask<IModuleView>> create) : IModuleAsyncActivity
    {
        public ActivityState State => new(true, .5, false, "#0A84FF");
        public event Action? Changed { add { } remove { } }
        public ValueTask<IModuleView> CreateViewAsync(ModuleViewContext context, ActivityViewKind kind) => create(context);
    }
    private static void PumpUntil(string name, Func<bool> done)
    { long start = Stopwatch.GetTimestamp(); while (!done() && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(2)) Pump(); Check(name, done()); }
    private static TaskCompletionSource<IModuleView> ViewResult() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ModuleViewContext ViewContext() => new(new Grid(), _ => { }, _ => { }, _ => { });
    private static void AsyncViewChecks()
    {
        using var host = Host(); var definition = Def("async-views", Done); host.ReconcileAsync([definition]).GetAwaiter().GetResult();
        var pending = ViewResult(); var context = ViewContext(); int ticks = 0;
        using (var mount = new ModuleViewHost(host, "async-views", context, _ => throw new Exception("sync factory selected"), c =>
        { c.Every(TimeSpan.FromMilliseconds(16), () => ticks++); return new(pending.Task); }))
        {
            Check("async mount immediately exposes loading surface", mount.IsLoading && mount.Children.Count == 2);
            mount.SetViewport(410, 160, 1.25); mount.SetViewport(new ModuleViewport(640, 250) { DeviceScale = 1.5, ContentScale = 2, HorizontalInset = 18, CornerRadius = 20 });
            mount.Refresh(); Pump(); Check("pending view timers do not start before commit", ticks == 0);
            var result = new ProbeView(); pending.SetResult(result);
            PumpUntil("async view commits on owner dispatcher", () => !mount.IsLoading && result.Refreshes > 0);
            Check("async view receives latest pending viewport", result.Viewport is { Width: 640, Height: 250, DeviceScale: 1.5, ContentScale: 2, HorizontalInset: 18, CornerRadius: 20 });
            Pump(); Check("view timers start after mount commit", ticks > 0);
            mount.Dispose(); mount.Dispose(); int stopped = ticks; Pump();
            Check("view disposal cancels timers and releases once", result.Disposals == 1 && context.Lifetime.IsCancellationRequested && ticks == stopped && mount.Children.Count == 0);
        }
        pending = ViewResult(); context = ViewContext();
        using (var mount = new ModuleViewHost(host, "async-views", context, _ => throw new Exception(), _ => new(pending.Task)))
        {
            mount.Dispose(); Check("navigation away cancels pending context immediately", context.Lifetime.IsCancellationRequested);
            var late = new ProbeView(); pending.SetResult(late);
            PumpUntil("late result after navigation is disposed", () => late.Disposals == 1);
            Check("late result never remounts detached content", mount.Children.Count == 0 && host.Faults.Count == 0);
        }
        pending = ViewResult(); context = ViewContext();
        using (var mount = new ModuleViewHost(host, "async-views", context, _ => throw new Exception(), _ => new(pending.Task)))
        {
            context.Dispose(); Check("ending view context releases pending host", mount.Children.Count == 0 && !mount.IsLoading);
            var late = new ProbeView(); pending.SetResult(late); PumpUntil("context cancellation cleans later result", () => late.Disposals == 1);
        }
        pending = ViewResult(); context = ViewContext();
        using (var obsolete = new ModuleViewHost(host, "async-views", context, _ => throw new Exception(), _ => new(pending.Task)))
        {
            Task.Run(() => host.ReconcileAsync([])).GetAwaiter().GetResult();
            Check("background stop revokes view token before dispatcher cleanup", context.Lifetime.IsCancellationRequested);
            host.ReconcileAsync([definition with { Revision = "v2" }]).GetAwaiter().GetResult();
            var current = new ProbeView(); using var replacement = new ModuleViewHost(host, "async-views", ViewContext(), _ => current);
            var old = new ProbeView(); pending.SetResult(old);
            PumpUntil("module reload cleans late old view", () => old.Disposals == 1 && obsolete.Children.Count == 0);
            Check("old mount cannot remove replacement view", current.Disposals == 0 && current.Refreshes > 0 && host.Modules.Single().Status == ModuleStatus.Ready && host.Faults.Count == 0);
        }
        pending = ViewResult(); context = ViewContext();
        using (var mount = new ModuleViewHost(host, "async-views", context, _ => throw new Exception(), _ => new(pending.Task)))
        {
            mount.Dispose(); pending.SetException(new Exception("retired mount error")); Pump();
            Check("late cancelled mount error does not pollute module diagnostics", host.Faults.Count == 0);
        }
        pending = ViewResult(); context = ViewContext(); ticks = 0;
        using (var mount = new ModuleViewHost(host, "async-views", context, _ => throw new Exception(), c => { c.Every(TimeSpan.FromMilliseconds(16), () => ticks++); return new(pending.Task); }))
        {
            Guid generation = host.Modules.Single().Generation; pending.SetException(new Exception("real async failure"));
            PumpUntil("genuine async failure shows local fallback", () => !mount.IsLoading && context.Lifetime.IsCancellationRequested);
            Check("mount failure has owning generation", host.Faults.Last() is { Operation: "mount view" } fault && fault.Generation == generation);
            Check("fallback remains attached and pending resources stopped", mount.Children.Count == 2 && Descendants(mount).OfType<TextBlock>().Any() && ticks == 0);
        }
        pending = ViewResult(); context = ViewContext(); var bad = new ProbeView { ThrowOnRefresh = true };
        using (var mount = new ModuleViewHost(host, "async-views", context, _ => throw new Exception(), _ => new(pending.Task)))
        {
            pending.SetResult(bad); PumpUntil("partially mounted async view rolls back", () => context.Lifetime.IsCancellationRequested);
            Check("rollback and async catch do not double dispose view", bad.Disposals == 1);
        }
        var shared = new ProbeView();
        using (var first = new ModuleViewHost(host, "async-views", ViewContext(), _ => shared))
        using (var second = new ModuleViewHost(host, "async-views", ViewContext(), _ => shared))
        {
            Check("duplicate view rejected without disposing live owner", shared.Disposals == 0 && shared.Refreshes == 1);
            first.Refresh(); Check("original view remains functional after duplicate return", shared.Refreshes == 2);
        }
        Check("shared invalid instance cleaned exactly once", shared.Disposals == 1);
        var root = new Grid(); var wrapper = new ProbeView(root);
        using (var first = new ModuleViewHost(host, "async-views", ViewContext(), _ => wrapper))
        using (var second = new ModuleViewHost(host, "async-views", ViewContext(), _ => new ProbeView(root)))
            Check("second wrapper cannot take another mount root", root.Parent is not null && wrapper.Disposals == 0);
        var firstScreen = new ProbeView(); var secondScreen = new ProbeView();
        using (var one = new ModuleViewHost(host, "async-views", ViewContext(), _ => firstScreen))
        using (var two = new ModuleViewHost(host, "async-views", ViewContext(), _ => secondScreen))
        { one.Dispose(); two.Refresh(); Check("separate presentation hosts keep independent view lifetimes", firstScreen.Disposals == 1 && secondScreen.Disposals == 0 && secondScreen.Refreshes > 1); }
        int calls = 0;
        using (var missing = new ModuleViewHost(host, "not-running", ViewContext(), _ => { calls++; return new ProbeView(); }))
            Check("retired owner cannot start another view factory", calls == 0);
        var asyncResult = new ProbeView(); var activity = new AsyncActivity(_ => new(asyncResult));
        using (var mount = new ModuleViewHost(host, "async-views", ViewContext(), c => ((IModuleActivity)activity).CreateView(c, ActivityViewKind.Expanded), c => activity.CreateViewAsync(c, ActivityViewKind.Expanded)))
            Check("async activity does not require sync factory implementation", asyncResult.Refreshes == 1 && !mount.IsLoading);
        Check("async activity view cleaned", asyncResult.Disposals == 1);
        var page = ModulePage.Async("Async", "Async", _ => new(new ProbeView()));
        Check("async page builder preserves title and order", page.CreateViewAsync is not null && page.Order == 500);
        Throws("async page refuses null factory", () => ModulePage.Async("Async", "Async", null!));
        var normalized = new ModuleViewport(double.NaN, -1) { DeviceScale = double.PositiveInfinity, ContentScale = -10, HorizontalInset = 100, CornerRadius = double.NaN }.Normalize();
        Check("viewport validation separates units and bounds", normalized is { Width: 1, Height: 1, DeviceScale: 1, ContentScale: .25, HorizontalInset: .5, CornerRadius: 0 });
        Check("viewport maximum bounded without infinite layout", new ModuleViewport(double.MaxValue, double.MaxValue).Normalize() is { Width: 32768, Height: 32768 });
        ForeignDispatcherCheck(host);
        LateForeignDispatcherCheck(host);
    }
    private static void LateForeignDispatcherCheck(ModuleHost host)
    {
        ProbeView? probe = null; Dispatcher? dispatcher = null; using var started = new ManualResetEventSlim();
        var worker = new Thread(() => { dispatcher = Dispatcher.CurrentDispatcher; probe = new(); started.Set(); Dispatcher.Run(); }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        if (!started.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Late view dispatcher did not start.");
        try
        {
            var pending = ViewResult();
            using var mount = new ModuleViewHost(host, "async-views", ViewContext(), _ => throw new Exception(), _ => new(pending.Task));
            mount.Dispose(); pending.SetResult(probe!);
            PumpUntil("cancelled async mount releases foreign-dispatcher late result", () => Volatile.Read(ref probe!.Disposals) == 1);
            Check("late foreign result is released on its element dispatcher", probe!.DisposalThread != 0 && probe.DisposalThread != Environment.CurrentManagedThreadId);
        }
        finally { dispatcher!.BeginInvokeShutdown(DispatcherPriority.Background); if (!worker.Join(TimeSpan.FromSeconds(2))) throw new TimeoutException("Late view dispatcher did not stop."); }
    }
    private static void ForeignDispatcherCheck(ModuleHost host)
    {
        ProbeView? probe = null; Dispatcher? dispatcher = null; using var started = new ManualResetEventSlim();
        var worker = new Thread(() => { dispatcher = Dispatcher.CurrentDispatcher; probe = new(); started.Set(); Dispatcher.Run(); }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        if (!started.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("View dispatcher did not start.");
        try
        {
            using var mount = new ModuleViewHost(host, "async-views", ViewContext(), _ => probe!);
            PumpUntil("wrong-dispatcher view rejected and disposed on its own thread", () => Volatile.Read(ref probe!.Disposals) == 1);
            Check("foreign cleanup does not run on host dispatcher", probe!.DisposalThread != Environment.CurrentManagedThreadId);
        }
        finally { dispatcher!.BeginInvokeShutdown(DispatcherPriority.Background); if (!worker.Join(TimeSpan.FromSeconds(2))) throw new TimeoutException("View dispatcher did not stop."); }
    }
}
