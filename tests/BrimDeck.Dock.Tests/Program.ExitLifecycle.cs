using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;
using DropShelf;
using DropShelf.Models;

internal static partial class Program
{
    // Shutdown can block the UI dispatcher itself; only the parent can enforce the fixture's deadline.
    private static int CheckExitLifecycleProcess(string scratch)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(scratch, "exit-child")); start.ArgumentList.Add("--exit-lifecycle-child");
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(20000))
        {
            child.Kill(entireProcessTree: true); child.WaitForExit();
            Console.Write(output.GetAwaiter().GetResult()); Console.Error.Write(errors.GetAwaiter().GetResult());
            throw new InvalidOperationException("FAIL: production exit did not finish within 20 seconds; only the owned fixture child was stopped.");
        }
        string log = output.GetAwaiter().GetResult(); Console.Write(log); Console.Error.Write(errors.GetAwaiter().GetResult());
        Check("exit lifecycle: the isolated production shutdown finishes without replacement windows", child.ExitCode == 0 && log.Contains("EXIT CHILD RESULT:", StringComparison.Ordinal));
        Console.WriteLine($"EXIT LIFECYCLE RESULT: {_checks} parent checks passed; production shutdown completed.");
        return 0;
    }

    private static void CheckExitLifecycleHost(App app, MainWindow window, string scratch)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Settings.ShowTrayIcon = false; app.Settings.Clipboard.Enabled = false; app.Settings.Clipboard.Hotkey = "";
        app.Settings.Clipboard.Mode = ModuleMode.Off; app.Settings.Tray.Mode = ModuleMode.Performance; app.Settings.Tray.Placement = FileTrayPlacement.Notch;
        Set(app, "<Store>k__BackingField", new SettingsStore(scratch)); Set(app, "_trayStore", new FileTrayStore(scratch));
        HostedResources.ConfigureHost(() => false, Path.Combine(scratch, "attachments"));
        var controller = new AppController(Dispatcher.CurrentDispatcher, new EmbeddedHostOptions
        {
            DataDirectory = Path.Combine(scratch, "shelves"), CacheDirectory = Path.Combine(scratch, "attachments"),
            InstanceName = "BrimDeck.ExitFixture." + Guid.NewGuid().ToString("N"), AnimationsEnabled = () => false,
            AutomaticEnabled = () => false, FloatingEnabled = () => false, Exit = app.ExitApplication,
            GetStartWithWindows = () => false, SetStartWithWindows = _ => throw new InvalidOperationException("Startup registration is forbidden in this fixture."),
        });
        var tray = new ShelfTrayAdapter(controller.Shelves);
        Set(app, "<Shelves>k__BackingField", controller); Set(app, "<ShelfTray>k__BackingField", tray);
        string original = Path.Combine(scratch, "original.txt"); const string bytes = "Owned exit fixture: ordinary source must survive.";
        File.WriteAllText(original, bytes); tray.Model.AddFiles([original]);
        var shelf = controller.Shelves.ActiveWindow;
        Check("exit lifecycle: the final shelf starts hidden with the real active-model adapter", !shelf.IsVisible && tray.Items.Single().Value == original);
        int shutdowns = 0;
        app.Exit += (_, _) =>
        {
            shutdowns++;
            Check("exit lifecycle: host disposes shelves before WPF finishes closing windows", app.Shelves is null && app.Windows.Count == 0);
        };
        using var operation = controller.Shelves.Operations.Enter();
        app.ExitApplication(); Pump();
        Check("exit lifecycle: an in-flight operation keeps the original owner alive until the barrier", !app.Exiting && ReferenceEquals(app.Shelves, controller) && !controller.Accepting && tray.Items.Single().Value == original);
        operation.Dispose();
        var prompts = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        prompts.Tick += (_, _) =>
        {
            foreach (var prompt in app.Windows.OfType<PromptWindow>().Where(prompt => prompt.IsVisible).ToArray())
                prompt.AcceptButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        };
        prompts.Start();
        try { app.Run(); } finally { prompts.Stop(); }
        Check("exit lifecycle: production OnExit was reached exactly once", shutdowns == 1 && app.Exiting && app.Dispatcher.HasShutdownFinished);
        Check("exit lifecycle: closing windows never overwrites the saved ordinary path", new FileTrayStore(scratch).Load().Items.Single().Path == original);
        Check("exit lifecycle: shutdown preserves ordinary source bytes and capture consent", File.ReadAllText(original) == bytes && !app.Settings.Clipboard.Enabled);
    }
}
