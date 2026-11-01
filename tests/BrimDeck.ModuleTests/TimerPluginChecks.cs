using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Modules;

internal static partial class Program
{
    private static void TimerPluginChecks(string sampleBuild)
    {
        string packaged = Path.GetFullPath(Path.Combine(sampleBuild, "..", "..", "BrimDeck", "release", "mods", "timers"));
        string root = Path.Combine(_root, "external-timers"), installed = Path.Combine(root, "mods"), data = Path.Combine(root, "data");
        var receipts = new Dictionary<string, string>();
        ModuleBundleInstaller.Install(Path.GetDirectoryName(packaged)!, installed, receipts);
        var catalog = new ModuleCatalog(installed, Path.Combine(root, "cache"), [typeof(ModuleUi).Assembly, typeof(DeckSettings).Assembly]);
        var found = catalog.Discover().Single();
        Check("complete timer example is a discoverable ordinary DLL module", found.Manifest.Id == "timers" && found.Manifest.ApiMinor == 5 && catalog.Failures.Count == 0);
        Check("timer package contains its own domain implementation and no SDK copies", File.Exists(Path.Combine(found.Directory, "BrimDeck.Timers.Core.dll"))
            && !Directory.EnumerateFiles(found.Directory).Any(f => Path.GetFileName(f) is "BrimDeck.UI.dll" or "BrimDeck.Core.dll" or "BrimDeck.Modules.Abstractions.dll"));
        var scheduler = new Scheduler();
        using var host = new ModuleHost(data, scheduler, [], App.ModuleKinds(), [ModuleService.Of<IModulePresentation>(ModuleApi.UiPresentation, scope => new TestPresentation(scope))]);
        var definition = catalog.Definition(found, true); host.ReconcileAsync([definition]).GetAwaiter().GetResult();
        Check("timer activates without a builtin factory or private preference service", host.Modules.Single() is { Status: ModuleStatus.Ready, BuiltIn: false });
        Check("all three widgets and four settings contributions use the public registries", host.Registry.List<ModuleWidget>().Count == 3 && host.Registry.List<IModuleSettings>().Count == 4);
        var page = host.Registry.List<ModulePage>().Single(); var activity = host.Registry.List<IModuleActivity>().Single();
        var context = AssemblyLoadContext.GetLoadContext(activity.Value.GetType().Assembly);
        Check("timer DLL is isolated in the external collectible load context", context is { IsCollectible: true } && context != AssemblyLoadContext.Default);
        Check("timer module does not reference the desktop or host Core implementation", activity.Value.GetType().Assembly.GetReferencedAssemblies().All(a => a.Name is not ("BrimDeck" or "BrimDeck.Core")));
        Check("desktop does not reference timer implementation assemblies", typeof(App).Assembly.GetReferencedAssemblies().All(a => a.Name is not ("BrimDeck.Timers" or "BrimDeck.Timers.Core")));
        Check("timer has no special access to shared UI internals", !typeof(ModuleUi).Assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>().Any(a => a.AssemblyName is "BrimDeck.Timers" or "BrimDeck.MiniApps"));
        var general = host.Registry.List<IModuleSettings>().Single(s => s.Id == "timers:general").Value;
        var owner = new Grid(); int controls = 0;
        foreach (int mode in new[] { 0, 1, 2 })
        {
            general.Write("default", mode);
            var viewContext = new ModuleViewContext(owner, _ => { }, _ => { }, _ => controls++);
            using var mounted = new ModuleViewHost(host, "timers", viewContext, page.Value.CreateView);
            foreach (var size in new[] { new Size(496, 246), new Size(400, 90), new Size(1120, 340) })
            {
                mounted.SetViewport(size.Width, size.Height); mounted.Measure(size); mounted.Arrange(new Rect(size)); mounted.UpdateLayout();
                Check($"external timer mode {mode} fits {size} without scrollbars", !Descendants(mounted).OfType<ScrollViewer>().Any() && host.Faults.Count == 0);
            }
            Button Action(string zh, string en) => Descendants(mounted).OfType<Button>().First(b => AutomationProperties.GetName(b) is var name && (name == zh || name == en));
            Action("开始", "Start").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check($"external timer mode {mode} starts one shared activity", activity.Value.State.Active && scheduler.Callbacks.Count == 1 && controls == mode + 1);
            if (mode == 2)
            {
                Action("计次", "Lap").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var snapshot = new ModuleStorage(Path.Combine(data, "timers")).Read<JsonElement>("session");
                Check("external stopwatch records lap data through scoped storage", snapshot.GetProperty("Laps").GetArrayLength() == 1);
            }
            string session = Path.Combine(data, "timers", "session.json");
            var before = File.GetLastWriteTimeUtc(session); scheduler.Tick();
            Check($"external timer mode {mode} display pulse does not write files", File.GetLastWriteTimeUtc(session) == before);
            using var compact = new ModuleViewHost(host, "timers", new(owner, _ => { }, _ => { }, _ => { }), c => activity.Value.CreateView(c, ActivityViewKind.Compact));
            using var expanded = new ModuleViewHost(host, "timers", new(owner, _ => { }, _ => { }, _ => { }), c => activity.Value.CreateView(c, ActivityViewKind.Expanded));
            compact.SetViewport(190, 24); expanded.SetViewport(new(320, 76) { HorizontalInset = 18 });
            Check($"external timer mode {mode} mounts compact and expanded controls without extra pulses", scheduler.Callbacks.Count == 1 && host.Faults.Count == 0);
            host.Publish(new ModuleSuspending());
            Check($"external timer mode {mode} pauses on host suspend event", activity.Value.State.Active && scheduler.Callbacks.Count == 0);
            Action("结束", "End").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check($"external timer mode {mode} ends from its page", !activity.Value.State.Active);
        }
        var widgets = host.Registry.List<ModuleWidget>().ToArray();
        widgets.Single(w => w.Id == "timers:stopwatch").Value.MoveTo!(0);
        Check("generic widget reorder persists inside the module", widgets.OrderBy(w => w.Value.ReadOrder!()).First().Id == "timers:stopwatch");
        var pomodoro = widgets.Single(w => w.Id == "timers:pomodoro").Value; pomodoro.SetEnabled(false);
        Check("generic widget toggle hides settings and default choice", !host.Registry.List<IModuleSettings>().Single(s => s.Id == "timers:pomodoro").Value.Visible && general.Fields.Single().Options.All(o => o.Value != 0));
        var persisted = new ModuleStorage(Path.Combine(data, "timers")).Read<JsonElement>("preferences");
        Check("timer preferences are independent of the application settings file", !persisted.GetProperty("PomodoroEnabled").GetBoolean() && persisted.GetProperty("Order")[0].GetInt32() == 2);
        host.ReconcileAsync([definition with { Enabled = false }]).GetAwaiter().GetResult();
        Check("disabling external timer removes every contribution and pulse", host.Registry.List<ModulePage>().Count == 0 && host.Registry.List<IModuleSettings>().Count == 0 && scheduler.Callbacks.Count == 0);
        host.ReconcileAsync([catalog.Definition(found, true)]).GetAwaiter().GetResult();
        Check("external timer reload restores widget preferences", !host.Registry.List<ModuleWidget>().Single(w => w.Id == "timers:pomodoro").Value.Enabled());
        Check("external timer lifecycle completes without host faults", host.Faults.Count == 0);
        host.ReconcileAsync([]).GetAwaiter().GetResult();
        string damaged = Path.Combine(data, "timers", "session.json"); File.WriteAllText(damaged, "damaged-session");
        host.ReconcileAsync([catalog.Definition(found, true)]).GetAwaiter().GetResult();
        Check("damaged timer data is diagnosed without preventing module startup", host.Modules.Single().Status == ModuleStatus.Ready && host.Faults.Any(f => f.Operation == "read session"));
        host.ReconcileAsync([]).GetAwaiter().GetResult();
        Check("stopping unused timer does not overwrite a damaged session", File.ReadAllText(damaged) == "damaged-session");
    }
}
