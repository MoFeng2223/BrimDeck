using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// The identical reflection-only fixture loads either the saved pre-change binaries or the current production binaries.
// No App startup, clipboard payloads, accounts, external receivers or network requests are permitted.
internal static class Program
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Assembly _main = null!, _core = null!, _shelf = null!;
    private static object _app = null!;
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("Usage: <production-binary-directory> <Baseline|Full|Performance|Off> <output-json>"); return 2; }
        string binaries = Path.GetFullPath(args[0]), scenario = args[1], output = Path.GetFullPath(args[2]);
        string scratch = Path.Combine(Path.GetTempPath(), "BrimDeck-Benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Window? window = null; IDisposable? service = null, controller = null, adapter = null, images = null, lyrics = null;
        try
        {
            AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(binaries, name.Name + ".dll"))
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(binaries, name.Name + ".dll")) : null;
            _core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(binaries, "BrimDeck.Core.dll"));
            _shelf = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(binaries, "DropShelf.dll"));
            _main = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(binaries, "BrimDeck.dll"));
            var appType = Type("BrimDeck.App");
            var generated = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("BrimDeck.IsolatedBenchmark"), AssemblyBuilderAccess.Run).DefineDynamicModule("host");
            var subtype = generated.DefineType("IsolatedApplication", TypeAttributes.Public, appType);
            var startup = appType.GetMethod("OnStartup", All)!;
            var suppress = subtype.DefineMethod("OnStartup", MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig, typeof(void), [typeof(StartupEventArgs)]);
            suppress.GetILGenerator().Emit(OpCodes.Ret); subtype.DefineMethodOverride(suppress, startup);
            _app = Activator.CreateInstance(subtype.CreateType()!)!;
            var application = (Application)_app; application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Benchmark.App.xaml")!)
            {
                var xml = System.Xml.Linq.XDocument.Load(source); var resources = xml.Root!.Elements().Single().Elements().Single();
                resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "local", "clr-namespace:BrimDeck;assembly=BrimDeck");
                resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=System.Runtime");
                application.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
            }
            object settings = Get(_app, "Settings")!;
            foreach (var (name, value) in new (string, object)[] { ("Animations", true), ("UsagePage", false), ("MusicPage", false), ("MonitorPage", false), ("NotchMusic", false), ("NotchMonitor", false), ("CapsuleMusic", false), ("CapsuleMonitor", false), ("TrimMemoryWhenIdle", false) }) Put(settings, name, value);
            ((System.Collections.IList)Get(settings, "Apps")!).Clear();
            object clipboard = Get(settings, "Clipboard")!, tray = Get(settings, "Tray")!;
            Put(clipboard, "Enabled", false); Put(clipboard, "MaximumItems", 0); Put(clipboard, "RetentionDays", 0); Put(clipboard, "PersistHistory", true); Put(clipboard, "Hotkey", "");
            Put(tray, "Placement", Enum.Parse(Get(tray, "Placement")!.GetType(), "Notch"));
            string effective = scenario == "Baseline" ? "Full" : scenario;
            SetMode(clipboard, effective); SetMode(tray, effective);
            Put(settings, "LastPage", Enum.Parse(Get(settings, "LastPage")!.GetType(), "Clipboard"));
            Put(_app, "Store", New(Type("BrimDeck.Core.SettingsStore"), scratch));
            Put(_app, "_trayStore", New(Type("BrimDeck.Core.FileTrayStore"), scratch)); PutOptional(_app, "_moduleDirectory", scratch);
            // TrackPointer only consumes Active. No global input hook is needed for an offscreen, explicit-open fixture.
            Put(_app, "WindowDrags", RuntimeHelpers.GetUninitializedObject(Type("BrimDeck.Native.WindowDrags")));
            lyrics = (IDisposable)New(Type("BrimDeck.Core.LyricsService"), null, (Func<IEnumerable<string>>)(() => []), null); Put(_app, "Lyrics", lyrics);
            var backend = DispatchProxy.Create(Type("BrimDeck.Core.IAppUpdateBackend"), typeof(NoUpdates));
            Put(_app, "Updates", New(Type("BrimDeck.Core.AppUpdates"), backend, scratch, null));
            // Use the production palette and shelf presentation without reading or watching the user's theme folder.
            var panelTheme = Type("BrimDeck.PanelTheme");
            Field(panelTheme, "_loaded")!.SetValue(null, true);
            Call(_app, "PublishShelfTheme");
            var hosted = Type("DropShelf.HostedResources");
            foreach (string name in new[] { "DressShelf", "AttachMotion" })
            {
                var property = hosted.GetProperty(name, All)!;
                string method = name == "AttachMotion" ? "AttachShelfMotion" : name;
                var handler = FindMethod(appType, method, name == "DressShelf" ? 2 : 1)!;
                property.SetValue(null, handler.IsStatic ? handler.CreateDelegate(property.PropertyType) : handler.CreateDelegate(property.PropertyType, _app));
            }
            Check("theme initialization has no user-folder watcher", Get(_app, "_themeWatcher") is null);
            // Older binaries configure the same host/cache through AppController's embedded options below.
            hosted.GetMethod("ConfigureHost", All)?.Invoke(null, [(Func<bool>)(() => effective == "Full"), Path.Combine(scratch, "attachments")]);
            if (effective != "Off")
                images = hosted.GetMethod("AcquireImaging", All)?.Invoke(null, null) as IDisposable;
            object? history = null;
            if (effective != "Off")
            {
                service = (IDisposable)New(Type("BrimDeck.ClipboardService"), scratch, Provider(clipboard), Dispatcher.CurrentDispatcher);
                Put(_app, "_clipboard", service); history = Get(service, "History");
                var options = New(Type("DropShelf.EmbeddedHostOptions"));
                Put(options, "DataDirectory", Path.Combine(scratch, "shelves")); Put(options, "CacheDirectory", Path.Combine(scratch, "attachments"));
                Put(options, "InstanceName", "BrimDeck.Benchmark." + Environment.ProcessId);
                Put(options, "FloatingEnabled", (Func<bool>)(() => false)); Put(options, "AnimationsEnabled", (Func<bool>)(() => effective == "Full"));
                PutOptional(options, "AutomaticEnabled", (Func<bool>)(() => effective == "Full"));
                Put(options, "Exit", (Action)(() => { })); Put(options, "ShowSettings", (Action)(() => { }));
                Put(options, "GetStartWithWindows", (Func<bool>)(() => false)); Put(options, "SetStartWithWindows", (Func<bool, bool>)(_ => true));
                controller = (IDisposable)New(Type("DropShelf.AppController"), Dispatcher.CurrentDispatcher, options);
                Put(_app, "Shelves", controller);
                adapter = (IDisposable)New(Type("DropShelf.ShelfTrayAdapter"), Get(controller, "Shelves"));
                Put(_app, "ShelfTray", adapter); Call(controller, "Start", true);
            }
            window = (Window)New(Type("BrimDeck.MainWindow"), _app, null);
            Put(_app, "Deck", window); application.MainWindow = window;
            var eventStore = typeof(UIElement).GetProperty("EventHandlersStore", All)!.GetValue(window)!;
            var handlers = (RoutedEventHandlerInfo[])eventStore.GetType().GetMethod("GetRoutedEventHandlers", All)!.Invoke(eventStore, [FrameworkElement.LoadedEvent])!;
            foreach (var handler in handlers) window.RemoveHandler(FrameworkElement.LoadedEvent, handler.Handler);
            window.ShowInTaskbar = false; window.ShowActivated = false; window.Topmost = false; window.Left = window.Top = -20000;
            Put(window, "_retiring", true); Put(window, "_preview", true); Put(window, "_previewExpanded", true);
            // Keep context detection, but prevent benchmark windows from fighting other desktop windows' z-order or logging it.
            object host = Get(window, "_host")!; Put(host, "_window", (Func<IntPtr>)(() => IntPtr.Zero));
            window.Show(); Pump();
            var latency = new List<object>();
            string file = Path.Combine(scratch, "original.txt"); File.WriteAllText(file, "Synthetic benchmark original, never moved or exported.");
            byte[] png = Image(); object? view = null;
            if (history is not null)
            {
                foreach (int count in new[] { 1000, 10000 })
                {
                    var items = Enumerable.Range(0, count).Select(i => new { Id = "fixture-" + i, Content = new { Kind = 0, Text = "Synthetic benchmark fixture " + i + " category " + i % 17 }, CopiedAt = DateTimeOffset.UtcNow.AddSeconds(-i), SourceName = "Fixture" }).Cast<object>().ToList();
                    for (int i = 0; i < 16; i++) items.Add(new { Id = "image-" + i, Content = new { Kind = 3, Image = png, PixelWidth = 1600, PixelHeight = 900 }, CopiedAt = DateTimeOffset.UtcNow.AddMilliseconds(i), SourceName = "Fixture" });
                    items.Add(new { Id = "file", Content = new { Kind = 4, Files = new[] { file } }, CopiedAt = DateTimeOffset.UtcNow, SourceName = "Fixture" });
                    var snapshot = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { Items = items }), Type("BrimDeck.Core.ClipboardSnapshot"))!;
                    Call(history, "Restore", snapshot, clipboard, DateTimeOffset.UtcNow);
                    Call(window, "PrepareClipboardDock"); Call(window, "RenderClipboardPage", history, (Action)(() => { }), (Action)(() => { }), (Func<(bool, bool, string)>)(() => (false, false, "")));
                    view = Get(window, "_clipboardView")!; Call(window, "SetExpanded", true, true, false); Pump();
                    var timings = new List<double>();
                    for (int i = 0; i < 24; i++)
                    {
                        var watch = Stopwatch.StartNew(); Put(view, "SearchText", "fixture " + (12 + i % 5)); Call(view, "Refresh", false); Pump(); timings.Add(watch.Elapsed.TotalMilliseconds);
                    }
                    Put(view, "SearchText", ""); Call(view, "Refresh", false); Pump();
                    latency.Add(new { Count = count, P50 = Percent(timings, .5), P95 = Percent(timings, .95) });
                    Console.WriteLine($"INTERACTION {scenario} count={count} p50={Percent(timings, .5):0.###} p95={Percent(timings, .95):0.###}");
                }
                awaitFlush(service!);
            }
            Put(window, "_preview", false); Put(window, "_previewExpanded", false); Put(window, "_expanded", true); Put(window, "_clipboardInput", true); Put(window, "_trackPointer", true);
            FindMethod(window.GetType(), "ApplyModulePresentation", 0)?.Invoke(window, null); Call(window, "UpdatePointerTracking"); Pump();
            if (effective == "Performance")
                Check("production performance page stops both persistent trackers", Equals(Get(window, "ReducedModule"), true)
                    && Equals(Get(host, "EventDriven"), true) && Equals(Get(Get(window, "_pointerTrack")!, "IsEnabled"), false)
                    && Equals(Get(Get(host, "_timer")!, "IsEnabled"), false));
            int writes = IntProperty(service, "WriteCount"), cleans = IntProperty(service, "CleanCount");
            long decodes = StaticNumber("BrimDeck.ClipboardPreview", "DecodeCount");
            string historyFile = Path.Combine(scratch, "clipboard-history.bin"); byte[]? before = File.Exists(historyFile) ? SHA256.HashData(File.ReadAllBytes(historyFile)) : null;
            using var process = Process.GetCurrentProcess(); process.Refresh(); TimeSpan cpu = process.TotalProcessorTime;
            var idle = Stopwatch.StartNew(); Console.WriteLine($"IDLE_BEGIN {scenario} seconds=120");
            while (idle.Elapsed < TimeSpan.FromSeconds(120)) { Pump(); Thread.Sleep(20); }
            process.Refresh();
            var result = new
            {
                Scenario = scenario, BinaryDirectory = binaries, MainSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(binaries, "BrimDeck.dll")))) ,
                Seconds = idle.Elapsed.TotalSeconds, CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                PrivateBytes = process.PrivateMemorySize64, WorkingSet = process.WorkingSet64, Threads = process.Threads.Count, Handles = process.HandleCount,
                CacheBytes = StaticNumber("DropShelf.DecodedImageCache", "Bytes"), Decodes = decodes < 0 ? -1 : StaticNumber("BrimDeck.ClipboardPreview", "DecodeCount") - decodes,
                Writes = writes < 0 ? -1 : IntProperty(service, "WriteCount") - writes, Cleans = cleans < 0 ? -1 : IntProperty(service, "CleanCount") - cleans,
                HistoryFileUnchanged = before is null ? !File.Exists(historyFile) : File.Exists(historyFile) && before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(historyFile))),
                Interactions = latency, Scope = "Same reflection-only production WPF fixture, fresh process per scenario, 1000/10000 texts plus 16 generated images and one owned file. No live startup, clipboard payload, network, external receiver, GC or working-set trim. Real OS DPI and hardware multi-display remain manual. Legacy counters absent from old binaries are -1; file hash is the idle persistence check."
            };
            if (effective == "Performance") Check("performance idle does not write, clean or decode", result.Writes == 0 && result.Cleans == 0 && result.Decodes == 0 && result.HistoryFileUnchanged);
            Check("benchmark leaves its original file and capture consent unchanged", File.Exists(file) && Equals(Get(clipboard, "Enabled"), false));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("BENCHMARK_RESULT " + JsonSerializer.Serialize(result)); Console.WriteLine($"BENCHMARK_CHECKS {_checks}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error is TargetInvocationException wrapped ? wrapped.InnerException : error); return 1; }
        finally
        {
            if (window is not null) { ((IDisposable)Get(window, "_host")!).Dispose(); Put(window, "_retiring", true); window.Close(); }
            adapter?.Dispose(); controller?.Dispose(); service?.Dispose(); images?.Dispose(); lyrics?.Dispose();
            if (_app is not null) { (Get(_app, "_save") as DispatcherTimer)?.Stop(); FindMethod(_app.GetType(), "DisposeShelfTheme", 0)?.Invoke(_app, null); }
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }
    private static Func<T> ValueProvider<T>(object value) => () => (T)value;
    private static object Provider(object value) => typeof(Program).GetMethod(nameof(ValueProvider), All)!.MakeGenericMethod(value.GetType()).Invoke(null, [value])!;
    private static Type Type(params string[] names) => names.Select(name => _main?.GetType(name) ?? _core?.GetType(name) ?? _shelf?.GetType(name)).FirstOrDefault(type => type is not null) ?? throw new TypeLoadException(string.Join("/", names));
    private static object New(Type type, params object?[] args)
    {
        var constructor = type.GetConstructors(All).Single(ctor => ctor.GetParameters().Length >= args.Length
            && ctor.GetParameters().Skip(args.Length).All(parameter => parameter.HasDefaultValue));
        return constructor.Invoke(args.Concat(constructor.GetParameters().Skip(args.Length).Select(parameter => parameter.DefaultValue)).ToArray());
    }
    private static FieldInfo? Field(Type type, string name) { for (Type? at = type; at is not null; at = at.BaseType) if (at.GetField(name, All) is { } field) return field; return null; }
    private static object? Get(object target, string name) => target.GetType().GetProperty(name, All)?.GetValue(target) ?? Field(target.GetType(), name)?.GetValue(target);
    private static void Put(object target, string name, object? value)
    {
        if (target.GetType().GetProperty(name, All) is { SetMethod: not null } property) { property.SetValue(target, value); return; }
        (Field(target.GetType(), name) ?? Field(target.GetType(), "<" + name + ">k__BackingField") ?? throw new MissingFieldException(target.GetType().Name, name)).SetValue(target, value);
    }
    private static void PutOptional(object target, string name, object? value) { if (target.GetType().GetProperty(name, All) is not null || Field(target.GetType(), name) is not null) Put(target, name, value); }
    private static MethodInfo? FindMethod(Type type, string name, int count) { for (Type? at = type; at is not null; at = at.BaseType) if (at.GetMethods(All).FirstOrDefault(method => method.Name == name && method.GetParameters().Length == count) is { } method) return method; return null; }
    private static object? Call(object target, string name, params object?[] args) => (FindMethod(target.GetType(), name, args.Length) ?? throw new MissingMethodException(target.GetType().Name, name)).Invoke(target, args);
    private static void SetMode(object settings, string mode) { if (settings.GetType().GetProperty("Mode", All) is { } property) property.SetValue(settings, Enum.Parse(property.PropertyType, mode)); }
    private static int IntProperty(object? target, string name) => target is null ? 0 : Get(target, name) is int value ? value : -1;
    private static long StaticNumber(string type, string name)
    { var found = _main.GetType(type) ?? _shelf.GetType(type); return found?.GetProperty(name, All)?.GetValue(null) is { } value ? Convert.ToInt64(value) : -1; }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void awaitFlush(object service)
    {
        if (FindMethod(service.GetType(), "FlushAsync", 0)?.Invoke(service, null) is not Task task) { var settle = Stopwatch.StartNew(); while (settle.Elapsed < TimeSpan.FromSeconds(1)) { Pump(); Thread.Sleep(5); } return; }
        var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30)) { Pump(); Thread.Sleep(1); }
        task.GetAwaiter().GetResult();
    }
    private static double Percent(List<double> values, double fraction) { var sorted = values.Order().ToArray(); return sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1]; }
    private static void Check(string name, bool condition) { if (!condition) throw new InvalidOperationException("FAIL: " + name); _checks++; Console.WriteLine("PASS: " + name); }
    private static byte[] Image()
    {
        var pixels = new byte[1600 * 900 * 4]; for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 0x9a; pixels[i + 1] = 0x74; pixels[i + 2] = 0x48; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(1600, 900, 96, 96, PixelFormats.Bgra32, null, pixels, 1600 * 4); bitmap.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    public class NoUpdates : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        { "get_CurrentVersion" => "0.0.0", "get_CanInstall" => false, "get_PendingRelease" or "get_LastRestartFailure" => null, _ => throw new InvalidOperationException("Network/update operations are forbidden in the benchmark.") };
    }
}
