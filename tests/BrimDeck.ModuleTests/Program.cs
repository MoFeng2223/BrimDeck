using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Timers;
using BrimDeck.Modules;

internal static partial class Program
{
    private static readonly List<string> Failures = [];
    private static int _checks;
    private static string _root = "";
    private static void Check(string name, bool condition) { _checks++; if (!condition) Failures.Add(name); }
    private static void Throws(string name, Action action) { try { action(); Check(name, false); } catch { Check(name, true); } }
    private sealed class Scheduler : IModuleScheduler
    {
        internal readonly List<Action> Callbacks = [];
        public IDisposable Every(TimeSpan interval, Action callback) { Callbacks.Add(callback); return new ModuleAction(() => Callbacks.Remove(callback)); }
        internal void Tick() { foreach (var callback in Callbacks.ToArray()) callback(); }
    }
    private sealed class TestModule(Func<IModuleContext, ValueTask> activate) : IModule
    { public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken) => activate(context); }
    private static ModuleDefinition Def(string id, Func<IModuleContext, ValueTask> activate, string revision = "v1", bool enabled = true, params string[] depends)
        => new(new() { Id = id, Name = id, Version = "1.0.0", Dependencies = depends.Select(x => new ModuleDependency(x)).ToArray() }, revision, enabled, () => new TestModule(activate));
    private static ValueTask Done(IModuleContext context) => ValueTask.CompletedTask;
    private static ModuleHost Host(Scheduler? scheduler = null, params object[] services) => new(Path.Combine(_root, Guid.NewGuid().ToString("N")), scheduler ?? new(), services, App.ModuleKinds(), [ModuleService.Of<IModulePresentation>(ModuleApi.UiPresentation, scope => new TestPresentation(scope))]) { StartTimeout = TimeSpan.FromMilliseconds(250) };
    private static ModulePage Page(string name) => new(name, name, _ => new EmptyView());
    private sealed class UnreadableException : Exception { public override string Message => throw new Exception("bad getter"); }
    private sealed class BlockingSnapshot(ManualResetEventSlim entered, ManualResetEventSlim proceed)
    { public string Value { get { entered.Set(); if (!proceed.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException(); return "old"; } } }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--fingerprint") { Console.WriteLine(ModuleCatalog.Fingerprint(args[1])); return 0; }
        if (args.Length == 2 && args[0] == "--authoring-packages") return AuthoringPackageChecks(args[1]);
        if (args.Length != 3 || !Path.GetFullPath(args[0]).StartsWith("E:\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Pass E: test directory, source repository, and sample build directory.");
        _root = Path.Combine(args[0], Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); Console.WriteLine("Results: " + _root);
        try
        {
            RuntimeChecks().GetAwaiter().GetResult();
            LifecycleChecks().GetAwaiter().GetResult();
            HostApiChecks().GetAwaiter().GetResult();
            CapabilityChecks().GetAwaiter().GetResult();
            PerformanceChecks().GetAwaiter().GetResult();
            LoaderChecks(args[2]).GetAwaiter().GetResult();
            ModuleFileChecks(args[2]).GetAwaiter().GetResult();
            ModuleBoundaryAuditChecks(); ModuleContinuationAuditChecks(args[2]); ModuleOrphanChecks();
            UiChecks(args[1]); AsyncViewChecks(); ViewVisibilityChecks(); SettingsNavigationChecks(); SettingsSchemaChecks(); ModuleDeprecationChecks(); StartupGuardChecks(); PackageInspectorChecks(args[2]); ModuleHostRangeChecks(args[2]); ModuleSignatureChecks(); PlatformChecks().GetAwaiter().GetResult(); TimerPluginChecks(args[2]); BoundaryChecks(args[1]);
        }
        catch (Exception ex) { Failures.Add(ex.ToString()); }
        File.WriteAllText(Path.Combine(_root, "results.json"), JsonSerializer.Serialize(new { checks = _checks, passed = _checks - Failures.Count, failures = Failures }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (string failure in Failures) Console.WriteLine("FAIL: " + failure);
        Console.WriteLine($"Module checks: {_checks}, failures: {Failures.Count}"); return Failures.Count == 0 ? 0 : 1;
    }

    private static async Task RuntimeChecks()
    {
        var scheduler = new Scheduler(); using var host = Host(scheduler); IModuleContext? old = null; IDisposable? handle = null; int starts = 0, events = 0, ticks = 0;
        var a = Def("a", c => { starts++; old = c; handle = c.Register("one", Page("first")); c.Register("two", Page("second")); c.Subscribe<int>(_ => events++); c.Every(TimeSpan.FromMilliseconds(20), () => ticks++); return ValueTask.CompletedTask; });
        var b = Def("b", c => { c.Register("one", Page("other")); return ValueTask.CompletedTask; });
        await host.ReconcileAsync([a, b]); Check("multiple contributions", host.Registry.List<ModulePage>().Count == 3);
        Check("owner namespaces", host.Registry.List<ModulePage>().Select(x => x.Id).SequenceEqual(["a:one", "a:two", "b:one"]));
        await host.ReconcileAsync([a, b]); Check("unchanged scan retains all entries", starts == 1 && host.Registry.List<ModulePage>().Count == 3);
        host.Publish(1); scheduler.Tick(); Check("owned event and timer", events == 1 && ticks == 1);
        await host.ReconcileAsync([a with { Enabled = false }, b]);
        Check("disable removes own entries only", host.Registry.List<ModulePage>().Select(x => x.Id).SequenceEqual(["b:one"]));
        host.Publish(1); scheduler.Tick(); Check("disable cleans listeners and timers", events == 1 && ticks == 1 && scheduler.Callbacks.Count == 0);
        Throws("stale generation cannot register", () => old!.Register("late", Page("bad")));
        Throws("stale generation cannot write over new storage", () => old!.Storage.Write("late", "bad"));
        int lateCleanup = 0; old!.OnDispose(() => lateCleanup++); Check("late disposer runs immediately", lateCleanup == 1);
        var stale = handle; await host.ReconcileAsync([a, b]); stale!.Dispose(); Check("old handle cannot remove replacement", host.Registry.List<ModulePage>().Count == 3);
        Check("only changed module restarts", starts == 2);
        var trace = new List<int>();
        var bad = Def("bad", c => { c.OnDispose(() => trace.Add(1)); c.OnDispose(() => throw new Exception("dispose")); c.OnDispose(() => trace.Add(2)); c.Register("partial", Page("partial")); throw new Exception("activation"); });
        await host.ReconcileAsync([a, b, bad]);
        Check("synchronous activation failure rolls back", !host.Registry.List<ModulePage>().Any(x => x.ModuleId == "bad"));
        Check("cleanup continues in reverse order", trace.SequenceEqual([2, 1]));
        Check("fault attributed", host.Modules.Single(x => x.Id == "bad").Status == ModuleStatus.Failed && host.Faults.Any(x => x.ModuleId == "bad"));
        int healthy = 0;
        var observer = Def("observer", c => { c.Subscribe<int>(_ => throw new Exception("event")); return ValueTask.CompletedTask; });
        var listener = Def("listener", c => { c.Subscribe<int>(_ => healthy++); return ValueTask.CompletedTask; });
        await host.ReconcileAsync([observer, listener]); host.Publish(3); Check("throwing listener does not stop next", healthy == 1);
        int unhealthyDependencyStarts = 0;
        var child = Def("child", _ => { unhealthyDependencyStarts++; return ValueTask.CompletedTask; }, depends: ["bad"]);
        await host.ReconcileAsync([child, bad, b]); Check("failed dependency blocks dependent", unhealthyDependencyStarts == 0 && host.Modules.Single(x => x.Id == "child").Status == ModuleStatus.DependencyFailed);
        Check("healthy graph survives failure", host.Registry.List<ModulePage>().Count == 1);
        await host.ReconcileAsync([Def("unreadable", _ => throw new UnreadableException()), b]);
        Check("exception message getter cannot break the loader", host.Modules.Single(x => x.Id == "unreadable").Status == ModuleStatus.Failed && host.Registry.List<ModulePage>().Single().ModuleId == "b");
        var finish = new TaskCompletionSource();
        var starting = host.ReconcileAsync([Def("transaction", async c => { c.Register("one", Page("not-ready")); await finish.Task; }), b]);
        await Task.Delay(5); Check("starting module contributions are staged", host.Registry.List<ModulePage>().All(x => x.ModuleId != "transaction"));
        finish.SetResult(); await starting; Check("successful start publishes staged contribution", host.Registry.List<ModulePage>().Any(x => x.ModuleId == "transaction"));
        await host.ReconcileAsync([Def("duplicate", c => { c.Register("same", Page("one")); c.Register("same", Page("two")); return ValueTask.CompletedTask; }), b]);
        Check("commit failure rolls back every contribution", host.Modules.Single(x => x.Id == "duplicate").Status == ModuleStatus.Failed && host.Registry.List<ModulePage>().Single().ModuleId == "b");
        await host.ReconcileAsync([Def("x", Done, depends: ["y"]), Def("y", Done, depends: ["x"]), b]);
        Check("cycle does not load either module", host.Modules.Count(x => x.Status == ModuleStatus.DependencyFailed) == 2);
        Check("cycle leaves healthy entry", host.Registry.List<ModulePage>().Single().ModuleId == "b");
        await host.ReconcileAsync([Def("x", Done, depends: ["absent"]), b]); Check("missing dependency isolated", host.Modules.Single(x => x.Id == "x").Status == ModuleStatus.DependencyFailed);
        var pending = new TaskCompletionSource(); int released = 0;
        var slow = Def("slow", async c => { c.Register("partial", Page("pending")); await pending.Task; c.OnDispose(() => released++); });
        await host.ReconcileAsync([slow, b]); Check("timeout rolls back partial activation", host.Modules.Single(x => x.Id == "slow").Status == ModuleStatus.Failed && host.Registry.List<ModulePage>().All(x => x.ModuleId != "slow"));
        pending.SetResult(); await Task.Delay(10); Check("late async cleanup runs once", released == 1);
        var never = new TaskCompletionSource();
        var longStart = Def("interrupt", async c => { await never.Task; c.Register("late", Page("bad")); });
        var activation = host.ReconcileAsync([longStart]); await Task.Delay(5);
        var disabling = host.ReconcileAsync([longStart with { Enabled = false }]);
        await Task.WhenAll(activation, disabling);
        Check("disable interrupts pending activation", host.Modules.Single().Status == ModuleStatus.Disabled && host.Registry.List<ModulePage>().Count == 0);
        never.SetResult(); await Task.Delay(5); Check("interrupted continuation cannot register later", host.Registry.List<ModulePage>().Count == 0);
        var disabled = Def("base", Done, enabled: false);
        await host.ReconcileAsync([disabled, Def("child", Done, depends: ["base"])]);
        Check("disabled dependency not implicitly enabled", host.Modules.Single(x => x.Id == "base").Status == ModuleStatus.Disabled && host.Modules.Single(x => x.Id == "child").Status == ModuleStatus.DependencyFailed);
        var storage = new ModuleStorage(Path.Combine(_root, "storage")); storage.Write("snapshot", new[] { 1, 2 });
        Check("snapshot roundtrip", storage.Read<int[]>("snapshot")!.SequenceEqual([1, 2]));
        Throws("storage refuses traversal", () => storage.Write("../escape", 1));
        Throws("storage has quota", () => storage.Write("large", new string('x', 1024 * 1024 + 1)));
        storage.Write("after", true); Check("storage recovers after failure", storage.Read<bool>("after"));
        var other = new ModuleStorage(Path.Combine(_root, "storage"));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => (i % 2 == 0 ? storage : other).Write("same", new string((char)('a' + i), 100)))));
        Check("cross-generation writes do not collide", storage.Read<string>("same")!.Length == 100);
        using (var dataHost = Host())
        using (var entered = new ManualResetEventSlim())
        using (var proceed = new ManualResetEventSlim())
        {
            IModuleContext? dataContext = null;
            var dataDefinition = Def("writer", c => { dataContext = c; return ValueTask.CompletedTask; });
            await dataHost.ReconcileAsync([dataDefinition]);
            var oldWrite = Task.Run(() => dataContext!.Storage.Write("snapshot", new BlockingSnapshot(entered, proceed)));
            if (!entered.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Writer did not start.");
            var stopping = Task.Run(() => dataHost.ReconcileAsync([]));
            await Task.Delay(20); Check("teardown waits for in-flight scoped storage write", !stopping.IsCompleted);
            proceed.Set(); await Task.WhenAll(oldWrite, stopping);
            await dataHost.ReconcileAsync([dataDefinition]); dataContext!.Storage.Write("snapshot", "new");
            Check("replacement storage survives old generation teardown", dataContext.Storage.Read<string>("snapshot") == "new");
        }
        var options = new DeckSettings(); options.Modules.EnabledModules.Add("sample"); options.Modules.Approvals["sample"] = new string('A', 64);
        var copy = options.Copy(); copy.Modules.EnabledModules.Clear(); copy.Modules.Approvals.Clear();
        Check("module preferences deep copied", options.Modules.EnabledModules.Count == 1 && options.Modules.Approvals.Count == 1);
        var legacy = JsonSerializer.Deserialize<DeckSettings>("{}")!; legacy.Normalize(); Check("old settings enable built-ins without external plugins", legacy.Modules.Enabled && legacy.Modules.EnabledModules.Count == 0 && (int)DeckPage.MiniApps == 3);
    }

    private static async Task LoaderChecks(string binaries)
    {
        string mods = Path.Combine(_root, "mods"), sample = Path.Combine(mods, "sample"); Directory.CreateDirectory(sample);
        foreach (string file in new[] { "BrimDeck.Sample.dll", "BrimDeck.Sample.Helper.dll", "BrimDeck.Sample.deps.json", "module.json" }) File.Copy(Path.Combine(binaries, file), Path.Combine(sample, file));
        var catalog = new ModuleCatalog(mods, Path.Combine(_root, "shadow"), [typeof(ModuleUi).Assembly, typeof(DeckSettings).Assembly]);
        var found = catalog.Discover(); Check("metadata discovery", found.Count == 1 && catalog.Failures.Count == 0);
        using var host = Host(); await host.ReconcileAsync([catalog.Definition(found[0], false)]); Check("disabled DLL not executed", host.Registry.List<ModulePage>().Count == 0);
        var enabled = catalog.Definition(found[0], true); await host.ReconcileAsync([enabled]);
        Check("private helper DLL resolved", host.Registry.List<ModulePage>().Any(x => x.Value.TitleEn == "helper-loaded"));
        Check("one module exports two pages", host.Registry.List<ModulePage>().Count == 2);
        var widget = host.Registry.List<ModuleWidget>().Single().Value;
        widget.SetEnabled(false); Check("widget switch removes page and hides its settings", host.Registry.List<ModulePage>().Count == 1 && !host.Registry.List<IModuleSettings>().Single().Value.Visible);
        widget.SetEnabled(true); Check("widget switch can register page again", host.Registry.List<ModulePage>().Count == 2 && host.Registry.List<IModuleSettings>().Single().Value.Visible);
        await host.ReconcileAsync([catalog.Definition(found[0], true)]); Check("repeat scan keeps all entries", host.Registry.List<ModulePage>().Count == 2 && host.Registry.List<IModuleSettings>().Count == 1);
        File.Copy(Path.Combine(binaries, "BrimDeck.Sample.dll"), Path.Combine(sample, "BrimDeck.Sample.dll"), true); Check("original DLL can be overwritten while active", true);
        await host.ReconcileAsync([]); Check("external module teardown removes every contribution", host.Registry.List<ModulePage>().Count == 0 && host.Registry.List<IModuleSettings>().Count == 0);
        await host.ReconcileAsync([catalog.Definition(found[0], true)]);
        Check("re-enabling reuses one immutable shadow copy", host.Modules.Single().Status == ModuleStatus.Ready && Directory.GetDirectories(Path.Combine(_root, "shadow", found[0].Manifest.Id)).Length == 1);
        await host.ReconcileAsync([]);
        string before = found[0].Digest; File.WriteAllText(Path.Combine(sample, "extra.txt"), "changed"); Check("content change changes fingerprint", ModuleCatalog.Fingerprint(sample) != before);
        await host.ReconcileAsync([catalog.Definition(found[0], true)]); Check("changed bytes cannot run with stale approval", host.Modules.Single().Status == ModuleStatus.Failed);
        string broken = Path.Combine(mods, "broken"); Directory.CreateDirectory(broken); File.WriteAllText(Path.Combine(broken, "module.json"), "broken");
        Check("bad manifest does not hide healthy plugin", catalog.Discover().Count == 1 && catalog.Failures.Count == 1);
    }

    private interface IPrivateHostService { }
    private sealed class PrivateHostService : IPrivateHostService { }
    private sealed class TestPresentation(ModuleServiceScope scope) : IModulePresentation
    { public void Invalidate() => scope.RequireLive(); }
    private static void UiChecks(string repo)
    {
        _ = new Application(); Soft.AnimationsEnabled = false;
        string xaml = File.ReadAllText(Path.Combine(repo, "src", "BrimDeck", "App.xaml"));
        int start = xaml.IndexOf("<ResourceDictionary>", StringComparison.Ordinal) + "<ResourceDictionary>".Length;
        string dictionary = "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:ui=\"clr-namespace:BrimDeck;assembly=BrimDeck.UI\" xmlns:sys=\"clr-namespace:System;assembly=System.Runtime\">" + xaml[start..xaml.LastIndexOf("</ResourceDictionary>", StringComparison.Ordinal)] + "</ResourceDictionary>";
        Application.Current.Resources = (ResourceDictionary)XamlReader.Parse(dictionary);
        var owner = new Grid(); owner.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck.UI;component/PanelControls.xaml", UriKind.Relative) });
        owner.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck.UI;component/TimerResources.xaml", UriKind.Relative) });
        Check("shared styles load through SDK assembly", owner.TryFindResource("ModuleControl") is Style && owner.TryFindResource("TimerStepButton") is Style);
        var scheduler = new Scheduler(); using var host = Host(scheduler);
        TimerWorkspace? workspace = null;
        host.ReconcileAsync([Def("test-timer", c => { workspace = new TimerWorkspace(c); c.Own(workspace); return ValueTask.CompletedTask; }) with { Manifest = new() { Id = "test-timer", Name = "test-timer", Capabilities = [ModuleApi.UiPresentation] } }]).GetAwaiter().GetResult();
        var timer = workspace!; timer.Timer.Start(TimerMode.Stopwatch, timer.Options);
        Check("one session schedules one pulse", scheduler.Callbacks.Count == 1);
        var viewContext = new ModuleViewContext(owner, _ => { }, _ => { }, _ => { });
        using (var page = new TimerPageView(timer, viewContext.Owner, () => { }, () => { }))
        {
            foreach (var size in new[] { new Size(400, 80), new Size(480, 260), new Size(1120, 340) })
            {
                page.SetViewport(size.Width, size.Height); page.Measure(size); page.Arrange(new Rect(size)); page.UpdateLayout();
                Check("bounded mini-app page " + size, page.DesiredSize.Width <= size.Width + .1 && page.DesiredSize.Height <= size.Height + .1);
                Check("no scroll viewer in mini-app page " + size, !Descendants(page).OfType<ScrollViewer>().Any());
            }
        }
        Check("unmounting page keeps session running", timer.Timer.Running && scheduler.Callbacks.Count == 1);
        var activity = new TimerActivity(timer);
        using (var compact = activity.CreateView(viewContext, ActivityViewKind.Compact))
        using (var expanded = activity.CreateView(viewContext, ActivityViewKind.Expanded))
        {
            compact.SetViewport(190, 32, 1.25); ((IModuleLayoutView)expanded).SetViewport(new(320, 76) { HorizontalInset = 18 });
            Check("views share one background session", scheduler.Callbacks.Count == 1);
            Check("expanded controls have no stop button", !Descendants(expanded.Element).OfType<Button>().Any(b => b.ToolTip?.ToString()?.Contains("End") == true));
        }
        host.Publish(new ModuleSuspending()); Check("sleep pauses timer and pulse", !timer.Timer.Running && timer.Timer.Active && scheduler.Callbacks.Count == 0);
        timer.Timer.Resume(); Check("manual resume restarts pulse", scheduler.Callbacks.Count == 1);
        var old = timer.Timer.ElapsedSeconds; timer.Timer.Pause();
        // The external module owns its session snapshot; there is no host preference adapter.
        Check("paused snapshot saved", new ModuleStorage(Path.Combine(GetHostData(host), "test-timer")).Read<TimerSnapshot>("session")?.Status == TimerStatus.Paused);
        int cleaned = 0; var scope = new ModuleViewContext(new Grid(), _ => { }, _ => { }, _ => { });
        scope.OnDispose(() => cleaned++); scope.OnDispose(() => throw new Exception("cleanup")); scope.OnDispose(() => cleaned++);
        scope.Dispose(); scope.Dispose(); Check("view fallback cleanup idempotent and resilient", cleaned == 2);
        scope.OnDispose(() => cleaned++); Check("late view disposer runs immediately", cleaned == 3);
        Throws("ended view refuses new timer", () => scope.Every(TimeSpan.FromSeconds(1), () => { }));
        int timerCalls = 0;
        var ownedContext = new ModuleViewContext(owner, _ => { }, _ => { }, _ => { });
        var brokenView = new ModuleViewHost(host, "test-view", ownedContext, context =>
        { context.Every(TimeSpan.FromMilliseconds(16), () => timerCalls++); throw new Exception("mount failed"); });
        Pump(); Check("mount failure stops forgotten view timer", timerCalls == 0);
        var activeContext = new ModuleViewContext(owner, _ => { }, _ => { }, _ => { });
        activeContext.Every(TimeSpan.FromMilliseconds(16), () => timerCalls++); Pump(); Check("view timer runs while mounted", timerCalls > 0);
        activeContext.Dispose(); int stoppedCalls = timerCalls; Pump(); Check("view disposal stops forgotten timer", timerCalls == stoppedCalls);
        int retiredViews = 0;
        using var viewHost = Host(); viewHost.ReconcileAsync([Def("test-view", Done)]).GetAwaiter().GetResult();
        var hostedContext = new ModuleViewContext(owner, _ => { }, _ => { }, _ => { });
        hostedContext.OnDispose(() => retiredViews++);
        using (var view = new ModuleViewHost(viewHost, "test-view", hostedContext, context => { context.Every(TimeSpan.FromMilliseconds(16), () => { }); return new EmptyView(); }))
        {
            Check("view owner is a local resource-ready container", ReferenceEquals(hostedContext.Owner, view) && view.TryFindResource("ModuleControl") is Style);
            Throws("view cannot navigate to another module", () => hostedContext.OpenPage("another:page"));
            Pump(); Check("view timer contributes to module performance", viewHost.Performance.Single().Calls > 1);
            viewHost.ReconcileAsync([]).GetAwaiter().GetResult();
            Check("stopping module immediately releases hosted view context", retiredViews == 1 && hostedContext.Lifetime.IsCancellationRequested && view.Children.Count == 0);
            Throws("retired view navigation is revoked", () => hostedContext.OpenPage("test-view:page"));
            long stopped = viewHost.Performance.Single().Calls; Pump(); Check("stopped view stops collecting callback time", viewHost.Performance.Single().Calls == stopped);
        }
        foreach (string theme in new[] { "dark", "light" })
        {
            PanelTheme.Use(new DeckSettings { PanelTheme = theme });
            foreach (var mode in Enum.GetValues<TimerMode>())
            {
                timer.Timer.Stop(); timer.Options.Mode = mode;
                using var page = new TimerPageView(timer, owner, () => { }, () => { });
                page.SetViewport(496, 246); Render(page, new Size(520, 270), $"{theme}-{mode}.png");
                Check($"{theme} {mode} settings render", !Descendants(page).OfType<ScrollViewer>().Any());
            }
            timer.Timer.Start(TimerMode.Countdown, timer.Options);
            using var expanded = new TimerActivityView(timer, viewContext, ActivityViewKind.Expanded); expanded.SetViewport(320, 76, 18);
            Render(expanded, new Size(320, 76), theme + "-controls.png");
            using var compact = new TimerActivityView(timer, viewContext, ActivityViewKind.Compact); compact.SetViewport(190, 32, 1);
            Render(compact, new Size(232, 40), theme + "-compact.png");
        }
        host.ReconcileAsync([]).GetAwaiter().GetResult(); Check("module stop releases pulse", scheduler.Callbacks.Count == 0);
        viewContext.Dispose();
    }
    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var stop = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; }; stop.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
    private sealed class EmptyView : IModuleView
    {
        public FrameworkElement Element { get; } = new Grid();
        public void SetViewport(double width, double height, double scale = 1) { }
        public void Refresh() { }
        public void Dispose() { }
    }
    private static void Render(FrameworkElement element, Size size, string file)
    {
        var frame = new Border { Width = size.Width, Height = size.Height, Background = UI.Brush(PanelTheme.Hex("surface")), Child = element };
        frame.Measure(size); frame.Arrange(new Rect(size)); frame.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(frame);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(_root, file)); encoder.Save(stream); frame.Child = null;
    }
    private static string GetHostData(ModuleHost host) => (string)typeof(ModuleHost).GetField("_dataDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var next in Descendants(child)) yield return next; } }
    private static void BoundaryChecks(string repo)
    {
        var references = typeof(TimerModule).Assembly.GetReferencedAssemblies();
        Check("timer plugin has no desktop dependency", references.All(a => a.Name != "BrimDeck"));
        Check("SDK has no desktop dependency", typeof(ModuleUi).Assembly.GetReferencedAssemblies().All(a => a.Name != "BrimDeck"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repo, "mods", "timers"), "*.cs"))
        {
            var source = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), @"//[^\r\n]*", "");
            Check("no host page dependency: " + Path.GetFileName(file), !source.Contains("MainWindow") && !source.Contains("SettingsWindow") && !source.Contains("MusicControlButton") && !source.Contains("App _app"));
        }
    }
}
