using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;
using DropShelf;
using DropShelf.Models;

internal static partial class Program
{
    private sealed record Sample(string Scenario, double Seconds, double CpuMilliseconds, long PrivateBytes, long WorkingSet,
        int Threads, int Handles, long CacheBytes, int Decodes, int Writes, int Cleans, double P50, double P95);

    // Real App/MainWindow and compiled XAML, with startup suppressed and all durable state under scratch.
    // Clipboard payloads, accounts, network and external receivers are never accessed.
    private static void CheckPerformanceHost(App app, MainWindow window, DeckSettings settings, PanelPalette[] palettes, string scratch, string directory)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Directory.CreateDirectory(directory);
        Set(app, "<Store>k__BackingField", new SettingsStore(scratch));
        Set(app, "_trayStore", new FileTrayStore(scratch)); Set(app, "_moduleDirectory", scratch);
        settings.Clipboard.Enabled = false; settings.Clipboard.Hotkey = ""; settings.Clipboard.RetentionDays = 0;
        settings.Clipboard.MaximumItems = 0; settings.Clipboard.PersistHistory = true;
        settings.Tray.Placement = FileTrayPlacement.Notch; settings.Animations = true;
        settings.Clipboard.Mode = settings.Tray.Mode = ModuleMode.Off;
        HostedResources.ConfigureHost(() => app.Settings.Animations && app.Settings.Tray.Mode == ModuleMode.Full, Path.Combine(scratch, "attachments"));
        var measurements = new List<Sample>();
        var initialized = typeof(App).GetMethod("InitializeShelves", Private)!;
        var clipboardInit = typeof(App).GetMethod("InitializeClipboard", Private)!;
        var change = typeof(App).GetMethod("ChangeModuleModeAsync", Private)!;
        var host = (WindowsHost)Get(window, "_host")!;
        var savedHost = host;
        // Program's ordinary render fixture disposes its watchdog. A fresh host exercises the production event lifecycle.
        host = new WindowsHost(() => WindowsHost.PrimaryMonitor(), () => IntPtr.Zero, () => null); Set(window, "_host", host);
        Set(window, "_preview", true); Set(window, "_previewExpanded", true); Set(window, "_page", DeckPage.Clipboard);
        Set(window, "_trackPointer", true); Set(window, "_context", ScreenContext.Desktop);
        var island = (FrameworkElement)window.FindName("Island");
        bool Change(bool clipboard, ModuleMode mode)
        {
            var task = (Task<bool>)change.Invoke(app, [clipboard, mode])!; CompletePerformance(task); return task.GetAwaiter().GetResult();
        }
        ClipboardService? Service() => app.ClipboardHistoryService;
        void ClearSessions()
        {
            if (Service() is { Loaded: true } service) { service.History.Clear(false); CompletePerformance(service.FlushAsync()); }
            if (app.Shelves is { } shelves)
                foreach (var shelf in shelves.Shelves.OpenShelves) shelf.ViewModel.Clear();
        }
        try
        {
            clipboardInit.Invoke(app, [scratch]); initialized.Invoke(app, [scratch]);
            Check("module cold-Off: neither runtime, history owner nor module hotkey is created", Service() is null && app.Shelves is null
                && Get(app, "_clipboardImages") is null && Get(app, "_shelfImages") is null && !File.Exists(Path.Combine(scratch, "clipboard-history.bin")));
            foreach (var c in Enum.GetValues<ModuleMode>())
            foreach (var s in Enum.GetValues<ModuleMode>())
            {
                Check($"mode matrix {c}/{s}: clipboard switch completes", Change(true, c));
                Check($"mode matrix {c}/{s}: shelf switch completes", Change(false, s));
                Check($"mode matrix {c}/{s}: independent runtime ownership", (Service() is not null) == (c != ModuleMode.Off)
                    && (app.Shelves is not null) == (s != ModuleMode.Off));
                Check($"mode matrix {c}/{s}: capture remains opt-in", Service()?.Listening != true && !app.Settings.Clipboard.Enabled);
                if (c != ModuleMode.Off)
                {
                    var service = Service()!;
                    Check("clipboard lazy load: an unopened library does not restore encrypted history", !service.Loaded);
                    service.OpenedOrResumed();
                    // A fresh service is used in the next combination, preserving the cold-load assertion above.
                    Check("clipboard explicit open: loads once without enabling capture", service.Loaded && !service.Listening);
                }
                if (app.Shelves is { } controller)
                    Check("shelf island-only: no raw-input gesture worker or prewarm", !controller.IsGestureRunning && !controller.Shelves.PrewarmEnabled);
                ClearSessions(); Change(true, ModuleMode.Off); Change(false, ModuleMode.Off);
            }
            Change(true, ModuleMode.Performance);
            Set(window, "_page", DeckPage.Clipboard); Call(window, "ApplyModulePresentation");
            Check("performance header: effective header is always visible without rewriting Edge preference", (ClipboardHeaderMode)typeof(MainWindow).GetProperty("DockHeaderMode", Private)!.GetValue(window)! == ClipboardHeaderMode.Always
                && app.Settings.Clipboard.HeaderMode == ClipboardHeaderMode.Edge && host.EventDriven);
            Set(window, "_trackPointer", true); Set(window, "_preview", false); Set(window, "_previewExpanded", false);
            Set(window, "_expanded", true); Set(window, "_clipboardInput", true);
            Call(window, "UpdatePointerTracking");
            Check("performance pointer: actual non-preview page has no persistent tracker", !((PromptTimer)Get(window, "_pointerTrack")!).IsEnabled);
            // An input-capable expanded fixture takes the TrackPointer branch that never inspects global WindowDrags.
            Change(true, ModuleMode.Full);
            Check("normal page: restoring Full restarts the existing pointer timer and watchdog", ((PromptTimer)Get(window, "_pointerTrack")!).IsEnabled
                && ((DispatcherTimer)Get(host, "_timer")!).IsEnabled && !host.EventDriven);
            Set(window, "_preview", true); Set(window, "_previewExpanded", true); Set(window, "_expanded", false); Set(window, "_clipboardInput", false);
            Call(window, "UpdatePointerTracking"); Change(true, ModuleMode.Off);
            Check("compatibility: missing and invalid mode values preserve Full and legacy placement", new ClipboardSettings().Mode == ModuleMode.Full
                && new TraySettings().Mode == ModuleMode.Full && ModuleRuntimePolicy.For((ModuleMode)999).Mode == ModuleMode.Full);
            Change(true, ModuleMode.Performance);
            var current = Service()!; var preferences = app.Settings.Clipboard;
            Check("clipboard lazy startup: persisted file is not read before explicit access", !current.Loaded);
            var history = current.History;
            using (var retention = new ClipboardService(Path.Combine(scratch, "retention"), () => new ClipboardSettings { Enabled = false, RetentionDays = 30 }, Dispatcher.CurrentDispatcher))
            {
                retention.History.Add(ClipboardContent.FromText("Future expiry fixture"), "Fixture", "", DateTimeOffset.UtcNow, new ClipboardSettings { RetentionDays = 30 });
                Check("clipboard expiration: thirty-day retention uses a bounded one-shot interval", ((DispatcherTimer)Get(retention, "_clean")!).Interval.TotalMilliseconds <= int.MaxValue);
                Call(retention, "ExpirationDue", null!, EventArgs.Empty);
                Check("clipboard expiration: a segmented early wake does not scan or rewrite history", retention.CleanCount == 0 && retention.History.Items.Count == 1);
                CompletePerformance(retention.FlushAsync());
            }
            for (int i = 0; i < 1000; i++) history.Add(ClipboardContent.FromText("Performance burst fixture " + i), "Fixture", "", DateTimeOffset.UtcNow, preferences);
            CompletePerformance(current.FlushAsync());
            Check("clipboard persistence: one burst has one write and no periodic cleanup", current.WriteCount == 1 && current.CleanCount == 0 && history.Items.Count == 1000);
            int writes = current.WriteCount;
            app.Settings.Clipboard.PreviewSensitivity = 17; current.ApplySettings();
            Check("clipboard presentation preference: no history write or listener", current.WriteCount == writes && !current.Listening);
            history.Clear(false); CompletePerformance(current.FlushAsync());
            Check("clipboard clear barrier: no earlier snapshot resurrects deleted entries", new ClipboardHistoryStore(scratch).Load().Items.Count == 0);
            var input = Image(); var paths = new[] { Path.Combine(scratch, "immutable.png") };
            var owned = new ClipboardContent { Kind = ClipboardContentKind.Image, Image = input, Files = paths, PixelWidth = 400, PixelHeight = 300 };
            input[0] ^= 0xff; paths[0] = "changed";
            var imageEntry = history.Add(owned, "Fixture", "", DateTimeOffset.UtcNow, preferences)!;
            var returned = owned.Image; returned[0] ^= 0xff;
            Check("clipboard ownership: external buffers cannot invalidate deduplication", history.Add(owned, "Fixture", "", DateTimeOffset.UtcNow, preferences)!.Id == imageEntry.Id
                && owned.Files[0] != "changed" && history.Items.Count == 1);
            history.Clear(false); CompletePerformance(current.FlushAsync());
            // A failed flush must retain a retryable live owner, not pretend shutdown was durable.
            string blocked = Path.Combine(scratch, "blocked"); File.WriteAllText(blocked, "owned blocker");
            var failureSettings = new ClipboardSettings { Enabled = false, RetentionDays = 0, StorageDirectory = blocked };
            using (var failed = new ClipboardService(scratch, () => failureSettings, Dispatcher.CurrentDispatcher))
            {
                failed.History.Add(ClipboardContent.FromText("Failed write retains this fixture"), "Fixture", "", DateTimeOffset.UtcNow, failureSettings);
                var flush = failed.FlushAsync(); CompletePerformance(flush);
                Check("clipboard failed flush: reports failure without losing live contents", !flush.Result && failed.History.Items.Count == 1 && failed.Status.Length > 0);
                File.Delete(blocked); Directory.CreateDirectory(blocked);
                flush = failed.FlushAsync(); CompletePerformance(flush);
                Check("clipboard failed flush: the same owner can retry successfully", flush.Result && new ClipboardHistoryStore(blocked).Load().Items.Count == 1);
            }
            // Registration can be exercised without reading the clipboard: pause before enabling and never unpause.
            current.TogglePause(); app.Settings.Clipboard.Enabled = true; current.ApplySettings();
            Check("clipboard capture consent: a paused enabled owner registers exactly one listener", current.Listening && current.Paused);
            var listener = Get(current, "_listener"); current.ApplySettings();
            Check("clipboard reapply: unchanged consent retains the same listener", ReferenceEquals(listener, Get(current, "_listener")));
            app.Settings.Clipboard.Enabled = false; current.ApplySettings();
            Check("clipboard capture opt-out: removes listener while retaining library", !current.Listening && ReferenceEquals(history, current.History));
            current.TogglePause();
            // No transient payloads remain, so automated Off transitions cannot dismiss confirmation dialogs.
            ClearSessions(); Change(true, ModuleMode.Off);
            Change(false, ModuleMode.Performance);
            var busyController = app.Shelves!;
            var operation = busyController.Shelves.Operations.Enter();
            var pendingStop = (Task<bool>)change.Invoke(app, [false, ModuleMode.Off])!; Pump();
            Check("shelf shutdown: an active operation keeps its controller alive and rejects new input", !pendingStop.IsCompleted
                && ReferenceEquals(app.Shelves, busyController) && !busyController.Accepting);
            int openBefore = busyController.Shelves.OpenShelves.Count;
            busyController.RestoreAt(new DropShelf.Services.ShelfHistoryEntry(), new DropShelf.Interop.POINT(-20000, -20000));
            Check("shelf shutdown: a late restore cannot create a window", busyController.Shelves.OpenShelves.Count == openBefore);
            operation.Dispose(); CompletePerformance(pendingStop);
            Check("shelf shutdown: the completed operation releases the runtime without polling", pendingStop.Result && app.Shelves is null);
            var resources = new List<(int Threads, int Handles)>();
            for (int i = 0; i < 50; i++)
            {
                Change(true, ModuleMode.Performance); Change(false, ModuleMode.Performance);
                var shelf = app.Shelves!.Shelves.ActiveWindow;
                Check("shelf performance: explicit model has no automatic windows " + i, !app.Shelves.IsGestureRunning && !app.Shelves.Shelves.PrewarmEnabled && !shelf.IsVisible);
                Change(true, ModuleMode.Off); Change(false, ModuleMode.Off); Pump();
                using var process = Process.GetCurrentProcess(); process.Refresh(); resources.Add((process.Threads.Count, process.HandleCount));
            }
            Check("50 switches: modules release dedicated windows and image leases", Service() is null && app.Shelves is null
                && !Application.Current.Windows.OfType<DropShelf.Views.ShelfWindow>().Any()
                && Get(app, "_clipboardImages") is null && Get(app, "_shelfImages") is null);
            Check("50 switches: no linear handle or thread growth", resources[^1].Handles - resources[9].Handles < 80 && resources[^1].Threads - resources[9].Threads < 12);
            Console.WriteLine($"RESOURCE_SWITCH first10={resources[9]} final50={resources[^1]}");
            Change(true, ModuleMode.Performance); current = Service()!; preferences = app.Settings.Clipboard; history = current.History;
            var fixture = new ClipboardSnapshot { Items = Enumerable.Range(0, 10000).Select(i => new ClipboardEntry { Id = "performance-" + i,
                Content = ClipboardContent.FromText("Synthetic search fixture " + i + " category " + i % 17), CopiedAt = DateTimeOffset.UtcNow.AddSeconds(-i) }).ToList() };
            history.Restore(fixture, preferences, DateTimeOffset.UtcNow);
            CompletePerformance(current.FlushAsync());
            Set(window, "_page", DeckPage.Clipboard); Set(window, "_expanded", true);
            Call(window, "PrepareClipboardDock"); Call(window, "RenderClipboardPage", history, (Action)(() => { }), (Action)(() => { }), (Func<(bool, bool, string)>)(() => (false, false, "")));
            var view = (ClipboardView)Get(window, "_clipboardView")!;
            var latencies = new Dictionary<ModuleMode, List<double>> { [ModuleMode.Full] = [], [ModuleMode.Performance] = [] };
            foreach (int count in new[] { 1000, 10000 })
            {
                history.Restore(fixture with { Items = fixture.Items.Take(count).ToList() }, preferences, DateTimeOffset.UtcNow);
                foreach (var mode in new[] { ModuleMode.Full, ModuleMode.Performance })
                {
                    Change(true, mode); view = (ClipboardView)Get(window, "_clipboardView")!;
                    foreach (var palette in palettes)
                    foreach (int percent in new[] { 100, 150 })
                    {
                        Palette(palette); app.Settings.PanelScale = percent; Call(window, "ApplyScale"); Call(window, "ApplyTheme");
                        window.SetExpanded(true, instant: true); Call(window, "ApplyModulePresentation"); view.ApplyLayout(); Pump();
                        var watch = Stopwatch.StartNew(); view.SearchText = "fixture 12"; view.Refresh(); Pump(); latencies[mode].Add(watch.Elapsed.TotalMilliseconds);
                        Check($"performance presentation {mode}/{count}/{palette.Id}/{percent}: query and source remain intact", view.QueryState.Text == "fixture 12" && history.Items.Count == count);
                        if (mode == ModuleMode.Performance)
                            Check("performance shell: timers stop and shared event source is active", !((PromptTimer)Get(window, "_pointerTrack")!).IsEnabled
                                && !((DispatcherTimer)Get(host, "_timer")!).IsEnabled && host.EventDriven && Soft.GetReduced(island));
                        Render((IslandBorder)island, directory, $"performance-{count}-{mode}-{palette.Id}-{percent}.png");
                        view.SearchText = ""; view.Refresh(); Pump();
                    }
                }
            }
            // Same live production host and synthetic history, three 120-second quiet intervals; no forced GC/working-set trim.
            app.Settings.PanelScale = 100; Palette(palettes[0]); Call(window, "ApplyScale"); Call(window, "ApplyTheme");
            foreach (var mode in new[] { ModuleMode.Full, ModuleMode.Performance, ModuleMode.Off })
            {
                Change(true, mode); if (mode != ModuleMode.Off) { Set(window, "_page", DeckPage.Clipboard); Call(window, "RenderClipboard"); }
                Call(window, "ApplyModulePresentation"); Pump();
                if (Service() is { } ready) CompletePerformance(ready.FlushAsync());
                measurements.Add(MeasureIdle(mode.ToString(), Service(), TimeSpan.FromSeconds(120), latencies.GetValueOrDefault(mode) ?? []));
                if (mode == ModuleMode.Performance)
                    Check("120-second performance idle: no cleanup, write or image decode without input", measurements[^1].Writes == 0 && measurements[^1].Cleans == 0 && measurements[^1].Decodes == 0);
            }
            Check("Off after populated persistent history: durable data stays on disk", new ClipboardHistoryStore(scratch).Load().Items.Count == 10000);
            // Two simultaneous host consumers share the event hooks and release them independently.
            int before = (int)typeof(WindowsHost).Assembly.GetType("BrimDeck.Native.WindowContextEvents")!.GetProperty("SubscriberCount", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            using (var other = new WindowsHost(() => WindowsHost.PrimaryMonitor(), () => IntPtr.Zero, () => null))
            { other.EventDriven = true; Check("multi-island: a second consumer shares events", other.EventDriven
                    && (int)typeof(WindowsHost).Assembly.GetType("BrimDeck.Native.WindowContextEvents")!.GetProperty("SubscriberCount", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)! == before + 1); other.EventDriven = false; }
            Check("module preferences: performance did not overwrite animation, header or sensitivity", app.Settings.Animations && app.Settings.Clipboard.PreviewSensitivity == 17 && app.Settings.Clipboard.HeaderMode == ClipboardHeaderMode.Edge);
            File.WriteAllText(Path.Combine(directory, "performance-measurements.json"), JsonSerializer.Serialize(new { Measurements = measurements,
                SwitchResources = resources.Select((value, i) => new { Cycle = i + 1, value.Threads, value.Handles }),
                Scope = "Production App/MainWindow with isolated synthetic fixtures; no real startup, clipboard payload, external receiver, account or network. Scale emulates panel 100/150%; hardware multi-display and actual OS DPI remain manual." }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(measurements));
        }
        finally
        {
            ClearSessions();
            if (Service() is not null) Change(true, ModuleMode.Off);
            if (app.Shelves is not null) Change(false, ModuleMode.Off);
            host.Dispose(); Set(window, "_host", savedHost);
            ((DispatcherTimer)Get(app, "_save")!).Stop(); CompletePerformance((Task)Get(app, "_writing")!);
            Set(window, "_preview", true); Set(window, "_previewExpanded", true);
        }
    }

    private static Sample MeasureIdle(string scenario, ClipboardService? service, TimeSpan duration, List<double> latencies)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var cpu = process.TotalProcessorTime; int decodes = ClipboardPreview.DecodeCount, writes = service?.WriteCount ?? 0, cleans = service?.CleanCount ?? 0;
        var watch = Stopwatch.StartNew();
        Console.WriteLine($"IDLE_BEGIN {scenario} seconds={duration.TotalSeconds}");
        while (watch.Elapsed < duration) { Pump(); Thread.Sleep(20); }
        process.Refresh();
        double[] sorted = latencies.Order().ToArray();
        double Percent(double p) => sorted.Length == 0 ? 0 : sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(sorted.Length * p) - 1)];
        var sample = new Sample(scenario, watch.Elapsed.TotalSeconds, (process.TotalProcessorTime - cpu).TotalMilliseconds, process.PrivateMemorySize64,
            process.WorkingSet64, process.Threads.Count, process.HandleCount, DecodedImageCache.Bytes, ClipboardPreview.DecodeCount - decodes,
            (service?.WriteCount ?? 0) - writes, (service?.CleanCount ?? 0) - cleans, Percent(.5), Percent(.95));
        Console.WriteLine("IDLE_RESULT " + JsonSerializer.Serialize(sample)); return sample;
    }
    private static void CompletePerformance(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30)) { Pump(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Performance host operation did not complete.");
        task.GetAwaiter().GetResult(); Pump();
    }
}
