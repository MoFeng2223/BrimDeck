using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using BrimDeck.Native;
using BrimDeck;
using BrimDeck.Core;

internal static class Program
{
    // Exercises the native adapters without starting the app, reading account
    // credentials, changing playback, or creating application settings.
    [STAThread]
    private static int Main()
    {
        try
        {
            Console.WriteLine($"Windows native smoke tests: {Environment.OSVersion.Version}");
            CheckWindowChrome();
            CheckInputMethods();
            CheckInputMethodShortcuts();
            CheckInputMethodModes();
            var (bounds, work, scale) = WindowsHost.PrimaryScreen();
            Check("primary display has usable bounds and DPI", bounds.Width > 0 && bounds.Height > 0
                && work.Width > 0 && work.Height > 0 && scale > 0);

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            {
                using var capture = ProcessLoopbackCapture.TryStart((uint)Environment.ProcessId);
                Check("unsupported process capture returns control to the session peak fallback", capture is null);
            }
            else Console.WriteLine("SKIP: unsupported process capture check requires Windows 10 build < 20348.");

            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void CheckWindowChrome()
    {
        var window = new Window { Width = 400, Height = 300, ShowInTaskbar = false };
        try
        {
            SettingsChrome.Apply(window);
            // Theme changes must also be safe before a native handle exists.
            SettingsChrome.SetTheme(window, true);
            Check("settings theme does not create a native handle", new WindowInteropHelper(window).Handle == IntPtr.Zero);
            var handle = new WindowInteropHelper(window).EnsureHandle();
            Check("settings window creates a native handle", handle != IntPtr.Zero);
            SettingsChrome.SetTheme(window, true);
            SettingsChrome.SetTheme(window, false);
            var chrome = WindowChrome.GetWindowChrome(window);
            Check("settings window remains resizable", window.ResizeMode == ResizeMode.CanResize && chrome.ResizeBorderThickness.Left > 0);
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                Check("Windows 10 settings use a square outer frame", chrome.CornerRadius == new CornerRadius(0));
            else
                Check("Windows 11 settings retain rounded corners", chrome.CornerRadius == new CornerRadius(12));
        }
        finally { window.Close(); }
    }

    private static void CheckInputMethods()
    {
        using var profiles = new InputMethodProfiles();
        var installed = profiles.Enumerate();
        Check("TSF enumerates named profiles", installed.Count > 0 && installed.All(p => p.Id.Length > 0 && p.Name.Length > 0));
        Check("TSF profile identities are unique", installed.DistinctBy(p => p.Id).Count() == installed.Count);
        Check("TSF profiles retain language and service identities", installed.Where(p => p.Type == 1).All(p => p.Language != 0 && p.ClassId != Guid.Empty && p.ProfileId != Guid.Empty));
        Check("native input-method adapter is included", TargetInputMethod.Available);
        Check("native request ABI matches the C++ adapter", TargetInputMethod.RequestSize == 136);
        Check("invalid target is rejected without activation", !TargetInputMethod.Read(default).Confirmed);
    }

    private static void Check(string name, bool condition)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private static void CheckInputMethodShortcuts()
    {
        Check("input shortcut maps Win Space to native keys", InputMethodGesture.TryParse("Win+Space", out var win, out _) && win == new InputMethodBinding(HotkeyModifiers.Win, 0x20));
        Check("input shortcut maps Ctrl Shift as a modifier chord", InputMethodGesture.TryParse("Shift+Control", out var chord, out string text)
            && text == "Ctrl+Shift" && chord == new InputMethodBinding(HotkeyModifiers.Ctrl, 0xA0));
        Check("input shortcut maps multi-modifier keys", InputMethodGesture.TryParse("Control+Shift+k", out var multi, out text)
            && multi == new InputMethodBinding(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x4B) && text == "Ctrl+Shift+K");
        Check("input shortcut rejects typing and undefined key names", !InputMethodGesture.TryParse("Shift+Space", out _, out _)
            && !InputMethodGesture.TryParse("Ctrl+NotAKey", out _, out _) && !InputMethodGesture.TryParse("F9", out _, out _));
        Check("input shortcut rejects Windows security and window-management chords", new[] { "Win+L", "Ctrl+Alt+Delete", "Ctrl+Shift+Escape", "Alt+Tab", "Alt+F4" }.All(t => !InputMethodGesture.TryParse(t, out _, out _)));
        Check("recorded virtual keys reuse canonical gesture names", InputMethodGesture.Recorded(new(true, HotkeyModifiers.Ctrl, 0x20)) == "Ctrl+Space");
        using (var shortcut = new InputMethodShortcut(win, _ => { }, () => { }))
        {
            Check("input shortcut starts on a dedicated keyboard thread", shortcut.Running && shortcut.Error is null);
            shortcut.Arm(null); // No input is intercepted by this smoke check.
            shortcut.Dispose();
            Check("input shortcut releases its hook and accepts repeated disposal", !shortcut.Running);
        }
    }

    private static void CheckInputMethodModes()
    {
        using var controller = new InputMethodController(action => action(), _ => false);
        controller.RefreshAsync().GetAwaiter().GetResult();
        var profile = controller.Installed.First();
        var settings = new InputMethodSettings { Enabled = true, Shortcut = "Ctrl+Space",
            Profiles = [new() { Id = profile.Id, Name = profile.Name }] };
        var shortcutField = typeof(InputMethodController).GetField("_shortcut", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        controller.Configure(settings);
        var shortcut = (InputMethodShortcut?)shortcutField.GetValue(controller);
        Check("control mode starts its own shortcut service", shortcut is { Running: true });
        settings.Mode = InputMethodMode.Notify; settings.Shortcut = ""; settings.Profiles.Clear();
        controller.Configure(settings, conflict: true);
        Check("notify mode releases the previous keyboard hook", shortcut?.Running == false && shortcutField.GetValue(controller) is null);
        Check("notify mode works without a shortcut or cycle participants", controller.Problem is null);
        int selections = 0; controller.Started += _ => selections++;
        var handler = typeof(InputMethodController).GetMethod("Handle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        handler.Invoke(controller, [new InputMethodKeyEvent(1, default, new(Next: true, Begin: true))]);
        Check("notify mode ignores queued control-mode shortcut intents", selections == 0);
        controller.Tick();
        Check("notify observation never recreates a keyboard shortcut", shortcutField.GetValue(controller) is null);
    }
}
