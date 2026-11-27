using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;

// The collapsed row's one line (CompactLine), measured on the row's own parts drawn offscreen at 100 to 300 % and at several
// heights of the row: the system readings, the devices' batteries, a quota ring, the notices' icons with their number, the file
// count, the title, the clock and the equalizer at rest. Each part is put in the row as the window puts it (CompactLine.Top) and
// rendered alone; its ink is split into the marks and words it shows (runs of columns with ink), and each one's rows are read.
//   - Every figure, whatever draws it (a drawn text, a text block, the marquee), ends on one row: the row's baseline.
//   - The figures' middle and every ring's and mark's middle lie within half a device pixel of the row's midline.
// Nothing is shown and no settings are read. With a folder given, each row is also saved enlarged with the midline drawn in.
//   dotnet run -c Release --project tests/BrimDeck.IslandChecks -- line [folder]
internal static class CompactLineChecks
{
    private sealed record Ink(string Name, double Top, double Bottom, bool Figure, bool Mark)
    {
        public double Centre => (Top + Bottom) / 2;
    }
    // How far a figure's or a mark's middle may lie from the midline, in device pixels: half a pixel where its ink is an odd
    // number of pixels tall, and as much again for a first or last row that is half covered and may or may not count.
    private const double Allowed = 1.01;

    public static int Run(string? folder)
    {
        // The clock's own fonts are read through pack URIs, which an Application registers.
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var main = new Window();
        app.MainWindow = main;
        Motion.Enabled = false;
        if (folder is not null) Directory.CreateDirectory(folder);
        int failures = 0;
        double worstMark = 0, worstFigure = 0;
        string worstMarkAt = "", worstFigureAt = "";
        foreach (double dpi in new[] { 1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 3 })
            foreach (double scale in new[] { 1, .875, 1.125, 1.25, 1.5 })
            {
                // The rings and the letters in them read the scale from the main window.
                VisualTreeHelper.SetRootDpi(main, new DpiScale(dpi, dpi));
                var layout = CompactMusicLayout.Calculate(2000, scale, true, 1, true, 0, 0, true, ring: QuotaRing.Single);
                double row = layout.RowHeight, middle = CompactLine.Middle(row, dpi) * dpi;
                var inks = new List<Ink>();
                var pictures = new List<(byte[] Pixels, int Width, int Height)>();
                foreach (var (name, part, figures, marks) in Parts(scale, dpi, layout))
                {
                    var (pixels, width, height) = Render(part, row, dpi);
                    pictures.Add((pixels, width, height));
                    var runs = Runs(pixels, width, height, (int)Math.Floor(2.6 * dpi * scale));
                    for (int i = 0; i < runs.Count; i++)
                    {
                        bool figure = figures.Contains(i) || figures.Contains(i - runs.Count), mark = marks.Contains(i) || marks.Contains(i - runs.Count);
                        inks.Add(new Ink($"{name}[{i}]", runs[i].Top, runs[i].Bottom, figure, mark));
                    }
                    if (runs.Count == 0) { failures++; Console.WriteLine($"FAIL {dpi * 100:0}% x{scale}: {name} drew nothing"); }
                }
                string at = $"{dpi * 100:0}% x{scale} (row {row * dpi:0.##} px, midline {middle:0})";
                var figureInks = inks.Where(ink => ink.Figure).ToList();
                var bottoms = figureInks.Select(ink => ink.Bottom).Distinct().ToList();
                if (bottoms.Count > 1)
                {
                    failures++;
                    Console.WriteLine($"FAIL {at}: the figures end on different rows: " + string.Join(", ", figureInks.Select(ink => $"{ink.Name} {ink.Bottom:0}")));
                }
                // The dial's circle and the device's ring, both sixteen DIPs at the row's scale, start on the same row.
                // Where the circle is an odd number of pixels across, its first row is half covered and may or may not count.
                double dialTop = inks.First(ink => ink.Name == "memory[0]").Top, ringTop = inks.First(ink => ink.Name == "device[0]").Top, across = CompactMusicLayout.Whole(16 * scale) * dpi;
                bool whole = Math.Abs(across - Math.Round(across)) < 1e-6 && (int)Math.Round(across) % 2 == 0;
                if (Math.Abs(dialTop - ringTop) > (whole ? 0 : 1))
                {
                    failures++;
                    Console.WriteLine($"FAIL {at}: the dial starts on row {inks.First(ink => ink.Name == "memory[0]").Top:0}, the device's ring on row {inks.First(ink => ink.Name == "device[0]").Top:0}");
                }
                foreach (var ink in inks)
                {
                    double off = ink.Centre - middle;
                    if (ink.Figure && Math.Abs(off) > worstFigure) { worstFigure = Math.Abs(off); worstFigureAt = $"{ink.Name} at {at}: {off:+0.0;-0.0}"; }
                    if (ink.Mark && Math.Abs(off) > worstMark) { worstMark = Math.Abs(off); worstMarkAt = $"{ink.Name} at {at}: {off:+0.0;-0.0}"; }
                    if ((ink.Figure || ink.Mark) && Math.Abs(off) > Allowed)
                    {
                        failures++;
                        Console.WriteLine($"FAIL {at}: {ink.Name} rows {ink.Top:0}..{ink.Bottom - 1:0} lie {off:+0.0;-0.0} px off the midline");
                    }
                }
                if (dpi is 1 or 1.5 && scale == 1 || folder is not null && scale == 1)
                    Console.WriteLine($"{at}: " + string.Join("  ", inks.Where(ink => ink.Figure || ink.Mark).Select(ink => $"{ink.Name} {ink.Top:0}..{ink.Bottom - 1:0} ({ink.Centre - middle:+0.0;-0.0})")));
                if (folder is not null) Save(pictures, (int)Math.Round(middle), Path.Combine(folder, $"line-{dpi * 100:0}-x{scale}.png"));
            }
        Console.WriteLine($"worst figure: {worstFigureAt}");
        Console.WriteLine($"worst mark: {worstMarkAt}");
        Console.WriteLine(failures == 0 ? "PASS: at 100 to 300 % and five heights, every figure of the collapsed row ends on one row, the dial starts level with the rings, and the figures, rings and marks lie within a device pixel of the row's midline"
            : $"{failures} collapsed row line checks failed");
        main.Close();
        return failures == 0 ? 0 : 1;
    }

    // The row's parts, each with which of its runs of ink are figures (digits ending on the baseline) and which are marks centred
    // on the midline; an index below zero counts from the last run.
    private static IEnumerable<(string Name, FrameworkElement Part, int[] Figures, int[] Marks)> Parts(double scale, double dpi, CompactMusicLayout layout)
    {
        var look = PanelTheme.Main;
        var settings = new DeckSettings();
        var colors = MonitorThemeColors.Strip(settings, look);
        // The cores at full load, so the columns show their whole height.
        var core = new CoreSample(0, 0, CoreTier.Performance, 1, 1, 2000d, 4000);
        var snapshot = new MonitorSnapshot(DateTimeOffset.Now, 2, new CpuSample([core, core with { Id = 1 }], 3000d), new MemorySample(100, 81, 10, 100),
            new NetworkSample(118_000, 181_000), new GpuSample(.5, 0UL), Reading<ProcessList>.Unavailable("check"));
        MonitorStrip Strip(CompactMonitorItems item)
        {
            var strip = new MonitorStrip([new CompactReading { Item = item }], colors, scale, false, new StripPresence(), dpi);
            strip.Update(snapshot, false, 30);
            strip.Width = strip.PreferredWidth;
            return strip;
        }
        // A dial and its figure; an arrow and its figure; the core columns. The dial is open at the bottom, so its ink is not centred:
        // its top is compared with the device's ring, a whole circle of the same size (Run).
        yield return ("memory", Strip(CompactMonitorItems.Memory), [-1], []);
        yield return ("download", Strip(CompactMonitorItems.Download), [-1], [0]);
        yield return ("upload", Strip(CompactMonitorItems.Upload), [-1], [0]);
        yield return ("columns", Strip(CompactMonitorItems.Columns), [], [0]);
        // A device's ring and its figure.
        var devices = new DeviceStrip(new StripDevices([new StripDevice("Mouse", DeviceKind.Mouse, 81, false, MemoryLevel.Normal, "#FFFFFF", "#FFFFFF")], true), scale);
        devices.Width = devices.PreferredWidth;
        yield return ("device", devices, [-1], [0]);
        // A quota ring, without a letter.
        var rings = new QuotaCarousel([(new double?[] { 60 }, "#E08A4A", "check", "", false)], layout.RingSize, layout.RingThickness, layout.RingScale, layout.RingGap, 1, 0, animate: false, rowHeight: layout.RowHeight);
        yield return ("quota", rings, [], [0]);
        // The notices' icon and their number; the file count.
        double s = CompactMusicLayout.SafeScale(scale);
        var icon = CompactStacks.Icon(null, CompactStacks.Diameter(s), s, look, "Q", Colors.Orange);
        var number = CompactStacks.Words("18", s, look.Primary);
        yield return ("notices", CompactStacks.Row(CompactStacks.Stack([(icon, 0.0)], s), number, s, dpi), [-1], [0]);
        yield return ("files", CompactStacks.Tray(18, scale, dpi, look), [-1], [0]);
        // The title, set on the row's baseline as the window sets it.
        var title = new MusicMarquee { Text = "18 81", Width = 60 * scale, Height = layout.TextHeight, FontSize = layout.TextFont, Baseline = CompactLine.Baseline(layout.TextHeight, layout.TextFont, dpi) };
        yield return ("title", title, [0, 1], []);
        // The quota notice's words, a text block with a line height of its own, placed by its baseline in the row.
        var words = UI.Fixed("81", layout.TextFont, Math.Max(1, CompactMusicLayout.Whole(16 * scale)), look.Primary, FontWeights.SemiBold);
        words.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var notice = new Canvas { Width = 40 * scale, Height = layout.RowHeight };
        words.UseLayoutRounding = false; Canvas.SetTop(words, CompactLine.Baseline(layout.RowHeight, layout.TextFont, dpi) - words.BaselineOffset);
        notice.Children.Add(words);
        yield return ("notice", notice, [0], []);
        // The clock and the equalizer at rest.
        var clock = new ClockFace(ClockStyle.Minimal, true, false, [Colors.White], new DateTime(2026, 1, 1, 18, 11, 0), scale, look);
        clock.Width = clock.ReservedWidth;
        yield return ("clock", clock, [], [0, 1, 2]);
        yield return ("equalizer", new MusicEqualizer { Bars = 4, Width = layout.EqualizerSize, Height = layout.EqualizerHeight, BarWidth = layout.EqualizerBar, OnRowLine = true }, [], [0]);
    }

    // The part in a row of its own, where the window puts it, as device pixels (BGRA).
    private static (byte[] Pixels, int Width, int Height) Render(FrameworkElement part, double row, double dpi)
    {
        double width = Math.Ceiling(double.IsFinite(part.Width) ? part.Width : 200) + 4;
        // No layout rounding: outside a window an element rounds to the system's scale, not to the one rendered at. In the window
        // the parts' tops are whole device pixels (CompactLine.Top) and what stands inside them is kept off the rounding.
        var host = new Canvas { Width = width, Height = row, UseLayoutRounding = false };
        part.UseLayoutRounding = false;
        Canvas.SetLeft(part, Math.Round(2 * dpi) / dpi); Canvas.SetTop(part, part is ClockFace ? CompactLine.TopOfCentred(row, part.Height, dpi) : CompactLine.Top(row, part.Height, dpi));
        host.Children.Add(part);
        VisualTreeHelper.SetRootDpi(host, new DpiScale(dpi, dpi));
        host.Measure(new Size(width, row)); host.Arrange(new Rect(0, 0, width, row)); host.UpdateLayout();
        int w = (int)Math.Ceiling(width * dpi), h = (int)Math.Ceiling(row * dpi) + 2;
        var bitmap = new RenderTargetBitmap(w, h, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);
        return (pixels, w, h);
    }

    // The runs of columns with ink, columns no further apart than join joined into one, each with its first row and the row after
    // its last. A row counts once a pixel in it is at least three eighths covered, so the faint edge of a curve does not.
    private static List<(double Top, double Bottom)> Runs(byte[] pixels, int width, int height, int join)
    {
        const int Seen = 96;
        var inked = new bool[width];
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                if (pixels[(y * width + x) * 4 + 3] >= Seen) { inked[x] = true; break; }
        var runs = new List<(double, double)>();
        for (int x = 0; x < width;)
        {
            if (!inked[x]) { x++; continue; }
            int from = x, to = x, gap = 0;
            for (; x < width && gap <= join; x++) { if (inked[x]) { to = x; gap = 0; } else gap++; }
            int top = height, bottom = -1;
            for (int y = 0; y < height; y++)
                for (int column = from; column <= to; column++)
                    if (pixels[(y * width + column) * 4 + 3] >= Seen) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); break; }
            runs.Add((top, bottom + 1));
        }
        return runs;
    }

    // The parts side by side on the surface's color, four times enlarged, with the midline in red.
    private static void Save(List<(byte[] Pixels, int Width, int Height)> pictures, int middle, string path)
    {
        const int Zoom = 4;
        int width = pictures.Sum(picture => picture.Width), height = pictures.Max(picture => picture.Height);
        var sheet = new byte[width * Zoom * height * Zoom * 4];
        int left = 0;
        foreach (var (pixels, w, h) in pictures)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < w; x++)
                {
                    byte b = 0x12, g = 0x12, r = 0x12;
                    if (y < h)
                    {
                        int at = (y * w + x) * 4, alpha = pixels[at + 3];
                        b = (byte)(pixels[at] + b * (255 - alpha) / 255); g = (byte)(pixels[at + 1] + g * (255 - alpha) / 255); r = (byte)(pixels[at + 2] + r * (255 - alpha) / 255);
                    }
                    for (int dy = 0; dy < Zoom; dy++)
                        for (int dx = 0; dx < Zoom; dx++)
                        {
                            int to = ((y * Zoom + dy) * width * Zoom + (left + x) * Zoom + dx) * 4;
                            bool line = y == middle && dy == 0;
                            sheet[to] = line ? (byte)0x30 : b; sheet[to + 1] = line ? (byte)0x30 : g; sheet[to + 2] = line ? (byte)0xFF : r; sheet[to + 3] = 255;
                        }
                }
            left += w;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width * Zoom, height * Zoom, 96, 96, PixelFormats.Bgra32, null, sheet, width * Zoom * 4)));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
