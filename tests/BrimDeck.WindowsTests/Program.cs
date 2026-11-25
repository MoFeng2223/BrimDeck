using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using BrimDeck.Core;
using BrimDeck.Native;

internal static class Program
{
    // Exercises the native adapters without starting the app, reading account
    // credentials, changing playback, or creating application settings.
    [STAThread]
    private static int Main(string[] args)
    {
        // No check writes to the user's real log: the monitor's failure notes go nowhere here (SensorGuardChecks looks at them by itself).
        MonitorLog.Sink = _ => { };
        try
        {
            // "--only power": just the power and battery checks, for a quick look at what this machine reports.
            if (args is ["--only", "power"]) { PowerChecks.Run(Check); return Finish(); }
            if (args is ["--only", "cpu"]) { CpuProviderChecks.Run(Check); return Finish(); }
            if (args is ["--only", "gpu"]) { CheckGpus(); return Finish(); }
            if (args is ["--only", "capture"]) { CaptureChecks.Run(Check); return Finish(); }
            if (args is ["--only", "frames"]) { FrameRateChecks.Run(Check); return Finish(); }
            if (args is ["--only", "ringletter", ..]) { RingLetterChecks.Run(Check, args.Length > 2 ? args[2] : null); return Finish(); }
            if (args is ["--only", "drawn"]) { DrawnAutomationTests.Run(Check); return Finish(); }
            if (args is ["--only", "paste"]) { PasteInputChecks.Run(Check); return Finish(); }
            if (args is ["--only", "settings"]) { SettingsFocusTests.Run(Check); BrimDeck.MainWindow.CheckPageCapsuleFit(Check); return Finish(); }
            if (args is ["--only", "maximize"]) { SettingsMaximizeChecks.Run(Check); return Finish(); }
            if (args is ["--only", "popup"]) { PopupGhostChecks.Run(Check); return Finish(); }
            if (args is ["--only", "themecards"]) { ThemeCardChecks.Run(Check); return Finish(); }
            if (args is ["--only", "themelook"]) { ThemeLookChecks.Run(Check); return Finish(); }
            if (args is ["--only", "hoverowners"]) { HoverOwnerChecks.Run(Check); return Finish(); }
            if (args is ["--only", "fonts"]) { FontFollowTests.Run(Check); return Finish(); }
            if (args is ["--only", "inklabel"]) { InkLabelSettleTests.Run(Check); return Finish(); }
            // "--hosted": only the checks that hold on a CI runner (a virtual machine with a session but no display output, GPU,
            // battery or devices, and no promise about speed). Everything left out reads this machine's hardware, so its outcome
            // says something about the machine, not the code. Without the flag every check runs.
            if (args is ["--only", "rightdrag"]) { RightDragChecks.Run(Check); return Finish(); }
            if (args is ["--only", "droprelay"]) { DropRelayChecks.Run(Check); return Finish(); }
            bool hosted = args is ["--hosted"];
            Console.WriteLine($"Windows native smoke tests{(hosted ? " (hosted subset)" : "")}: {Environment.OSVersion.Version}");
            CheckWindowChrome();
            if (hosted) Console.WriteLine("EXCLUDED on a hosted runner: window capture (not yet known to work on a virtual display).");
            else CaptureChecks.Run(Check);
            CheckSettledClip();
            CheckDockedTopRim();
            CheckPageToolTips();
            LayoutRetryTests.Run(Check);
            InkLabelSettleTests.Run(Check);
            RightDragChecks.Run(Check);
            DropRelayChecks.Run(Check);
            DrawnAutomationTests.Run(Check);
            RingLetterChecks.Run(Check);
            SettingsFocusTests.Run(Check);
            ThemeCardChecks.Run(Check);
            ThemeLookChecks.Run(Check);
            HoverOwnerChecks.Run(Check);
            PopupGhostChecks.Run(Check);
            PanelKeyboardTests.Run(Check);
            BrimDeck.MainWindow.CheckPageCapsule(Check);
            BrimDeck.MainWindow.CheckPageCapsuleFit(Check);
            BrimDeck.MainWindow.CheckPageCapsuleStretch(Check);
            BrimDeck.MainWindow.CheckShadowLifetime();
            DeviceChecks.PushedNames(Check);
            if (hosted) Console.WriteLine("EXCLUDED on a hosted runner: GPU, display, maximized settings window, frame rate, temperature, device battery and power readings, and the application identity the shell resolves (they read this machine or its shell).");
            else
            {
                CheckGpus();
                var (bounds, work, scale) = WindowsHost.PrimaryScreen();
                Check("primary display has usable bounds and DPI", bounds.Width > 0 && bounds.Height > 0
                    && work.Width > 0 && work.Height > 0 && scale > 0);
                SettingsMaximizeChecks.Run(Check);
                AppIdentityChecks.Run(Check);
                FrameRateChecks.Run(Check);
                TemperatureChecks.Run(Check);
                DeviceChecks.Run(Check);
                PowerChecks.Run(Check);
            }
            CpuProviderChecks.Run(Check);
            SensorGuardChecks.Run(Check);
            FontFollowTests.Run(Check);

            return Finish();
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

    // Reads every graphics adapter as the monitor does and lists them. A machine may have none with counters (a virtual machine
    // with the software rasterizer only), which is not a failure.
    private static void CheckGpus()
    {
        using var pdh = new PdhQuery();
        var provider = new GpuProvider(pdh);
        provider.Want(true);
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
        // The drivers' own usage: asked twice, as Intel's counters need two readings; an adapter Windows saw idle is not asked.
        var drivers = new GpuDrivers(() => { });
        drivers.Read(gpus, 0, true, GpuUsageSource.Driver);
        Thread.Sleep(1000);
        foreach (var gpu in drivers.Read(gpus, 1, true, GpuUsageSource.Driver))
            Console.WriteLine($"GPU driver: {gpu.Name ?? "(unnamed)"} usage {gpu.Usage:P0} from {gpu.UsageSource ?? "Windows engines"}, engines {gpu.EngineUsage:P0}"
                + (gpu.DriverWatts is { } watts ? $", {watts:0.0} W" : "") + (gpu.DriverTemperature is { } heat ? $", {heat:0} °C" : ""));
        Check("GPU driver usage is a share, and only for an adapter Windows saw working", drivers.Read(gpus, 2, true, GpuUsageSource.Driver).All(gpu => gpu.Usage is >= 0 and <= 1
            && (gpu.UsageSource is null || gpu.EngineUsage > 0)));
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

    // A surface docked flush to a screen edge (the notch and an expanded panel whose top corners are square) must omit the rim
    // along y = 0 so transparency does not expose a bright hairline along the screen edge, while keeping its sides, bottom and
    // floating shapes rimmed as before.
    private static void CheckDockedTopRim()
    {
        static (byte[] Pixels, int Width, int Height) Draw(double w, double h, CornerRadius corners, double shoulder, double scale, bool settled, double pixel = 0, double rimWidth = 1, System.Windows.Media.Brush? bevel = null, double topRimOpacity = double.NaN)
        {
            var island = new BrimDeck.IslandBorder
            {
                Width = w, Height = h, Background = System.Windows.Media.Brushes.Black, CornerRadius = corners,
                ShoulderRadius = shoulder, MaterialOpacity = 1, SurfaceOpacity = 0.2, RimWidth = rimWidth,
                RimBrush = System.Windows.Media.Brushes.White, Pixel = pixel, BevelBrush = bevel,
                TopRimOpacity = topRimOpacity, ClipToBounds = true, SettledClip = settled
            };
            System.Windows.Media.VisualTreeHelper.SetRootDpi(island, new DpiScale(scale, scale));
            island.Measure(new Size(w, h)); island.Arrange(new Rect(0, 0, w, h)); island.UpdateLayout();
            int pw = (int)(w * scale), ph = (int)(h * scale);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(island);
            var pixels = new byte[pw * ph * 4];
            bitmap.CopyPixels(pixels, pw * 4, 0);
            return (pixels, pw, ph);
        }
        static (byte R, byte A) At(byte[] pixels, int width, int x, int y)
        {
            int i = (y * width + x) * 4;
            return (pixels[i + 2], pixels[i + 3]);
        }

        foreach (double scale in new[] { 1d, 1.5 })
        {
            string pct = $"{scale * 100:0}%";
            foreach (bool settled in new[] { false, true })
            {
                var (px, pw, ph) = Draw(238, 32, new CornerRadius(0, 0, 12, 12), 16, scale, settled);
                var top0 = At(px, pw, pw / 2, 0);
                var top1 = At(px, pw, pw / 2, 1);
                var bottom = At(px, pw, pw / 2, ph - 1);
                Check($"notch ({(settled ? "settled" : "animated")}) omits top rim at {pct} (y0={top0.R}/a{top0.A}, y1={top1.R}/a{top1.A}, bottom={bottom.R}/a{bottom.A})",
                    top0.R == 0 && top0.A == 51 && top1.R == 0 && top1.A == 51 && bottom.R >= 200 && bottom.A == 255);
            }

            {
                var (px, pw, ph) = Draw(300, 120, new CornerRadius(0, 0, 24, 24), 0, scale, false);
                var top0 = At(px, pw, pw / 2, 0);
                var top1 = At(px, pw, pw / 2, 1);
                var topLeft = At(px, pw, 0, 0);
                var topRight = At(px, pw, pw - 1, 0);
                var bottom = At(px, pw, pw / 2, ph - 1);
                Check($"docked expanded panel omits top rim while keeping side and bottom rims up to y=0 at {pct}",
                    top0.R == 0 && top0.A == 51 && top1.R == 0 && top1.A == 51
                    && topLeft.R >= 200 && topLeft.A == 255 && topRight.R >= 200 && topRight.A == 255
                    && bottom.R >= 200 && bottom.A == 255);
            }

            {
                var (px, pw, ph) = Draw(238, 32, new CornerRadius(0, 0, 12, 12), 16, scale, false, pixel: 2, rimWidth: 2, bevel: System.Windows.Media.Brushes.Gray);
                var top0 = At(px, pw, pw / 2, 0);
                var bottom = At(px, pw, pw / 2, ph - 1);
                Check($"pixel-theme docked notch omits top bevel while keeping bottom bevel at {pct}",
                    top0.R == 0 && top0.A == 51 && bottom.R >= 100 && bottom.A == 255);
            }

            foreach (bool settled in new[] { false, true })
            {
                var (px, pw, ph) = Draw(238, 32, new CornerRadius(0, 0, 12, 12), 16, scale, settled, pixel: 2, rimWidth: 2, bevel: null);
                var top0 = At(px, pw, pw / 2, 0);
                var bottom = At(px, pw, pw / 2, ph - 1);
                Check($"pixel-theme docked notch without bevel ({(settled ? "settled" : "animated")}) omits top rim at {pct}",
                    top0.R == 0 && top0.A == 51 && bottom.R >= 200 && bottom.A == 255);
            }

            foreach (var (label, w, h, radius, settled) in new[]
            {
                ("capsule (animated)", 196d, 32d, 16d, false),
                ("capsule (settled)", 196d, 32d, 16d, true),
                ("floating expanded panel", 300d, 120d, 24d, false)
            })
            {
                var (px, pw, _) = Draw(w, h, new CornerRadius(radius), 0, scale, settled);
                var top0 = At(px, pw, pw / 2, 0);
                Check($"{label} keeps its top rim at {pct}", top0.R >= 200 && top0.A == 255);
            }

            // Mid-transition from line or capsule to a docked expanded panel: top corners are still > 0 while TopRimOpacity is 0,
            // so the flat top span must draw no transient rim line while the sides and bottom keep theirs.
            {
                var (px, pw, ph) = Draw(300, 120, new CornerRadius(1.5, 1.5, 20, 20), 0, scale, false, topRimOpacity: 0);
                var top0 = At(px, pw, pw / 2, 0);
                var top1 = At(px, pw, pw / 2, 1);
                var side = At(px, pw, 0, ph / 2);
                var bottom = At(px, pw, pw / 2, ph - 1);
                Check($"opening transition with TopRimOpacity=0 omits top rim even while top corners are rounding down at {pct}",
                    top0.R == 0 && top0.A == 51 && top1.R == 0 && top1.A == 51
                    && side.R >= 200 && side.A == 255 && bottom.R >= 200 && bottom.A == 255);
            }

            // Mid-fade TopRimOpacity (e.g. capsule detaching/attaching) attenuates only the top edge without dimming the bottom.
            {
                var (px, pw, ph) = Draw(200, 40, new CornerRadius(8, 8, 16, 16), 0, scale, false, topRimOpacity: 0.5);
                var top0 = At(px, pw, pw / 2, 0);
                var bottom = At(px, pw, pw / 2, ph - 1);
                Check($"half TopRimOpacity fades only the top edge while keeping the bottom rim full at {pct} (y0={top0.R}/a{top0.A})",
                    top0.R is >= 110 and <= 145 && bottom.R >= 200 && bottom.A == 255);
            }
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

    // Every check runs, so one failure does not hide the rest; the run fails if any did.
    private static int Finish()
    {
        Console.WriteLine(_failed.Count == 0 ? $"{_passed} checks passed." : $"{_passed} checks passed, {_failed.Count} failed:" + string.Concat(_failed.Select(name => "\n  " + name)));
        return _failed.Count == 0 ? 0 : 1;
    }
    private static int _passed;
    private static readonly List<string> _failed = [];
    private static void Check(string name, bool condition)
    {
        if (condition) { _passed++; Console.WriteLine("PASS: " + name); }
        else { _failed.Add(name); Console.WriteLine("FAIL: " + name); }
    }
}
