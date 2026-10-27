using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using BrimDeck.Core;
using BrimDeck.Native;
using BrimDeck;

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
            CheckSettledClip();
            CheckPageToolTips();
            BrimDeck.MainWindow.CheckShadowLifetime();
            CheckGpus();
            var (bounds, work, scale) = WindowsHost.PrimaryScreen();
            Check("primary display has usable bounds and DPI", bounds.Width > 0 && bounds.Height > 0
                && work.Width > 0 && work.Height > 0 && scale > 0);
            TemperatureChecks.Run(Check);

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

    // Reads every graphics adapter as the monitor does and lists them. A machine may have none with counters (a virtual machine
    // with the software rasterizer only), which is not a failure.
    private static void CheckGpus()
    {
        using var pdh = new PdhQuery();
        var provider = new GpuProvider(pdh);
        pdh.Collect();
        Thread.Sleep(1000);
        var reading = provider.Sample(pdh.Collect());
        var gpus = reading.OrDefault ?? [];
        if (!reading.HasValue) Console.WriteLine("SKIP: no GPU readings: " + reading.Reason);
        foreach (var gpu in gpus)
            Console.WriteLine($"GPU: {gpu.Name ?? "(unnamed)"} [{gpu.Kind}] usage {gpu.Usage:P0}, dedicated {Gigabytes(gpu.DedicatedBytes)} of {(gpu.DedicatedTotal is { } total ? $"{total / 1e9:0.0} GB" : "?")}, "
                + $"shared {Gigabytes(gpu.SharedBytes)}, key {gpu.Key}, {GpuReadings.LuidText(gpu.Luid)}, engines {string.Join(" ", gpu.Engines.Select(e => $"{e.Kind} {e.Usage:P0}"))}");
        var (adapters, described, _) = GpuAdapters.Read();
        foreach (var adapter in adapters.Values)
            Console.WriteLine($"DXGI: {adapter.Name} [{adapter.Kind}{(adapter.Software ? ", software" : "")}] {adapter.DedicatedTotal / 1e9:0.0} GB, at {adapter.Location ?? "?"}, key {adapter.Key}, {GpuReadings.LuidText(adapter.Luid)}");
        Check("GPU readings keep only the hardware adapters DXGI describes", gpus.All(gpu => described ? adapters.TryGetValue(gpu.Luid, out var adapter) && !adapter.Software : gpu.Name is null));
        Check("GPU readings give each adapter its own key", gpus.Select(gpu => gpu.Key).Distinct().Count() == gpus.Count);
        if (gpus.Any(gpu => gpu.Kind == GpuKind.Discrete))
            Check("GPU auto shows a discrete adapter", GpuChoice.Auto(gpus, 0)?.Kind == GpuKind.Discrete);
    }
    private static string Gigabytes(Reading<ulong> bytes) => bytes.TryGet(out var value) ? $"{value / 1e9:0.00} GB" : "?";

    // The collapsed island at rest clips to a rectangle (IslandBorder.SettledClip): nothing of it, its rim included, may then reach
    // past the rounded outline it shows with the rounded clip, at the notch's shoulders or the capsule's ends.
    private static void CheckSettledClip()
    {
        foreach (var (name, corners, shoulder) in new[] { ("notch", new CornerRadius(0, 0, 12, 12), 16d), ("capsule", new CornerRadius(18), 0d) })
            foreach (double scale in new[] { 1d, 1.5 })
            {
                byte[] Draw(bool settled)
                {
                    var island = new BrimDeck.IslandBorder { Width = 300, Height = 36, Background = System.Windows.Media.Brushes.Black, CornerRadius = corners,
                        ShoulderRadius = shoulder, MaterialOpacity = 1, RimWidth = 1, RimBrush = System.Windows.Media.Brushes.White, ClipToBounds = true,
                        SettledClip = settled };
                    island.Measure(new Size(300, 36)); island.Arrange(new Rect(0, 0, 300, 36)); island.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(300 * scale), (int)(36 * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(island);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    return pixels;
                }
                byte[] rounded = Draw(false), rest = Draw(true);
                // Without the clip the outline's own antialiased edge may tint a few pixels faintly; the rim's outer half was
                // whole pixels at full strength (99 of them on the notch before the fix).
                int outside = 0, strongest = 0;
                for (int i = 3; i < rounded.Length; i += 4) if (rounded[i] == 0 && rest[i] > 8) { outside++; strongest = Math.Max(strongest, rest[i]); }
                Check($"the {name} at rest draws nothing past its rounded outline at {scale * 100:0}% ({outside} faint edge pixels, strongest alpha {strongest})",
                    strongest <= 64 && outside <= 16);
            }
    }

    // A page left without the pointer moving closes the tooltips opened over it, and only those (BrimDeck.PageToolTips).
    private static void CheckPageToolTips()
    {
        var left = new Border { Width = 40, Height = 20 }; var kept = new Border { Width = 40, Height = 20 };
        var page = new StackPanel(); page.Children.Add(left);
        var other = new StackPanel(); other.Children.Add(kept);
        var root = new StackPanel(); root.Children.Add(page); root.Children.Add(other);
        // Off screen and never activated: nothing shows where the tests run.
        var window = new Window { Content = root, Left = -20000, Top = -20000, Width = 120, Height = 80, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show();
            var leftTip = new ToolTip { Content = "page", PlacementTarget = left, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var keptTip = new ToolTip { Content = "other", PlacementTarget = kept, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            left.ToolTip = leftTip; kept.ToolTip = keptTip;
            leftTip.IsOpen = true; keptTip.IsOpen = true;
            Check("tooltips open for the page check", leftTip.IsOpen && keptTip.IsOpen);
            int closed = BrimDeck.PageToolTips.CloseWithin(page);
            Check("leaving a page closes the tooltip opened over it", closed == 1 && !leftTip.IsOpen);
            Check("leaving a page keeps a tooltip opened elsewhere", keptTip.IsOpen);
            keptTip.IsOpen = false;
            Check("nothing open over the page closes nothing", BrimDeck.PageToolTips.CloseWithin(page) == 0);
        }
        finally { window.Close(); }
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
