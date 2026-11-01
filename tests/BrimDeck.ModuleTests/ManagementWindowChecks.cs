using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Modules;

internal static partial class Program
{
    private static void ManagementWindowProcess(string repository)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in new[] { typeof(Program).Assembly.Location, "--management-window", repository, Path.Combine(_root, "window") }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20000)) { process.Kill(true); throw new TimeoutException("Isolated management window did not close."); }
        Console.WriteLine(output.GetAwaiter().GetResult()); Console.WriteLine(error.GetAwaiter().GetResult());
        Check("isolated production management window checks", process.ExitCode == 0);
    }
    // Constructing Application queues Startup before the first dispatcher frame; never run the production startup here.
    private sealed class IsolatedManagementApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }
    private sealed class OfflineUpdates : IAppUpdateBackend
    {
        public string CurrentVersion => "1.0.0";
        public bool CanInstall => false;
        public AppRelease? PendingRelease => null;
        public Task<AppRelease?> CheckAsync(CancellationToken cancellation) => throw new InvalidOperationException("Network is forbidden in this fixture.");
        public Task DownloadAsync(AppRelease release, Action<int> progress, CancellationToken cancellation) => throw new InvalidOperationException();
        public void PrepareRestart() => throw new InvalidOperationException();
    }
    private static int ManagementWindowChecks(string repository, string root)
    {
        _root = root; Directory.CreateDirectory(root);
        using var host = Host();
        var app = new IsolatedManagementApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string xaml = File.ReadAllText(Path.Combine(repository, "src", "BrimDeck", "App.xaml"));
        int first = xaml.IndexOf("<ResourceDictionary>", StringComparison.Ordinal) + "<ResourceDictionary>".Length;
        string dictionary = "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:ui=\"clr-namespace:BrimDeck;assembly=BrimDeck.UI\" xmlns:sys=\"clr-namespace:System;assembly=System.Runtime\">" + xaml[first..xaml.LastIndexOf("</ResourceDictionary>", StringComparison.Ordinal)] + "</ResourceDictionary>";
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary);
        void Set(string property, object value) => typeof(App).GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(app, value);
        Set("Exiting", true); Set("Store", new SettingsStore(root)); Set("Modules", host);
        Set("ModuleFiles", new ModuleFiles(root)); Set("ModuleCatalog", new ModuleCatalog(Path.Combine(root, "mods"), Path.Combine(root, "modules-cache"), []));
        using var prices = new Pricing(root); Set("Prices", prices); Set("Updates", new AppUpdates(new OfflineUpdates(), root));
        using var inputs = new InputMethodController(action => action(), _ => false); Set("InputMethods", inputs);
        var options = app.Settings; options.Language = Loc.Chinese; options.Theme = SettingsTheme.Dark; Loc.Use(options.Language);
        options.Modules.EnabledModules.AddRange(["window-test", "no-settings"]);
        var settings = new ModuleSettingsStore("Synthetic settings", "Synthetic settings", [new("text", "Text", "Text", ModuleSettingKind.Text) { DefaultValue = "synthetic" }], new ModuleStorage(Path.Combine(root, "modules-data", "window-test")));
        host.ReconcileAsync([Def("window-test", c => { c.Register<IModuleSettings>("settings", settings); return ValueTask.CompletedTask; }), Def("no-settings", Done)]).GetAwaiter().GetResult();
        SettingsWindow? window = null;
        try
        {
            window = new SettingsWindow(app, SettingsWindow.ModulesPage); window.Show(); Pump();
            Check("production management page renders installed plugin search", Descendants(window).OfType<TextBox>().Any(t => AutomationProperties.GetName(t) == "搜索插件"));
            window.ShowModuleSettings("window-test:settings"); Pump();
            Check("plugin deep link reaches its detail rather than bouncing to management", Descendants(window).OfType<TextBox>().Any(t => t.Text == "synthetic"));
            window.ShowModuleSettings("no-settings"); Pump();
            Check("enabled plugin without settings still has a dedicated detail page", Descendants(window).OfType<TextBlock>().Any(t => t.Text.Contains("没有自定义设置", StringComparison.Ordinal)));
            options.Modules.Enabled = false; host.ReconcileAsync([]).GetAwaiter().GetResult(); window.ShowPage(SettingsWindow.ModulesPage); Pump();
            Check("system off hides runtime cards and exposes explicit maintenance", !Descendants(window).OfType<CheckBox>().Any(c => AutomationProperties.GetAutomationId(c).StartsWith("module-enable-", StringComparison.Ordinal)) && Descendants(window).OfType<Expander>().Any());
            var picture = new System.Windows.Shapes.Rectangle { Fill = new System.Windows.Media.VisualBrush(window.RootVisual) { Stretch = System.Windows.Media.Stretch.Fill } };
            Render(picture, new Size(960, 780), "management-off.png");
            foreach (string failure in Failures) Console.Error.WriteLine("FAIL: " + failure);
            Console.WriteLine($"Management window checks: {_checks}, failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { window?.Close(); host.ReconcileAsync([]).GetAwaiter().GetResult(); app.Shutdown(); }
    }
}
