using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BrimDeck;
using BrimDeck.Core;

// The letter inside a collapsed quota ring, rendered offscreen at 100 to 200 %: where its ink lands against the ring's centre, drawn
// as it is now (RingLetter) and as it was (a TextBlock centred in the ring). Nothing is shown. With a folder given, the enlarged
// pictures are saved there too.
internal static class RingLetterChecks
{
    private static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    private static readonly Brush Ink = Frozen(Color.FromRgb(0xC8, 0xC8, 0xC8));

    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    // The ring's cell: the ring (a track, and an arc when drawn) with the letter or without it, on whole device pixels.
    private static FrameworkElement Ring(double size, double thickness, string letter, double fontSize, double ppd, bool now, bool drawn)
    {
        var ring = new Grid { Width = size, Height = size };
        if (drawn)
        {
            ring.Children.Add(new Ellipse { Stroke = Frozen(Color.FromRgb(0x44, 0x44, 0x44)), StrokeThickness = thickness });
            double radius = size / 2 - thickness / 2, c = size / 2, angle = .65 * Math.PI * 2;
            var figure = new PathFigure { StartPoint = new Point(c, c - radius), IsClosed = false };
            figure.Segments.Add(new ArcSegment(new Point(c - radius * Math.Sin(angle), c - radius * Math.Cos(angle)), new Size(radius, radius), 0, true, SweepDirection.Counterclockwise, true));
            ring.Children.Add(new System.Windows.Shapes.Path { Data = new PathGeometry([figure]), Stroke = Frozen(Color.FromRgb(0xE0, 0x8A, 0x4A)), StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        if (letter.Length == 0) return ring;
        if (now) { if (RingLetter.Create(letter, fontSize, size, ppd, Font, Ink) is { } path) ring.Children.Add(path); }
        else ring.Children.Add(new TextBlock { Text = letter, FontFamily = Font, FontWeight = FontWeights.SemiBold, FontSize = fontSize, Foreground = Ink,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center });
        return ring;
    }

    private const double Pad = 4;
    // The picture (BGRA, device pixels) of the ring in a cell Pad DIP in from the corner, whole pixels as the carousel places it.
    private static (byte[] Pixels, int Width, int Height, double CenterX, double CenterY) Render(double size, double thickness, string letter, double fontSize, double ppd, bool now, bool drawn, bool dark)
    {
        double pad = Math.Round(Pad * ppd) / ppd, side = size + 2 * pad;
        var host = new Grid { Width = side, Height = side, UseLayoutRounding = true, Background = dark ? Frozen(Color.FromRgb(0x20, 0x20, 0x20)) : null };
        var ring = Ring(size, thickness, letter, fontSize, ppd, now, drawn);
        ring.HorizontalAlignment = HorizontalAlignment.Left; ring.VerticalAlignment = VerticalAlignment.Top; ring.Margin = new Thickness(pad);
        host.Children.Add(ring);
        host.Measure(new Size(side, side)); host.Arrange(new Rect(0, 0, side, side)); host.UpdateLayout();
        int w = (int)Math.Ceiling(side * ppd), h = w;
        var bitmap = new RenderTargetBitmap(w, h, 96 * ppd, 96 * ppd, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var pixels = new byte[w * h * 4]; bitmap.CopyPixels(pixels, w * 4, 0);
        return (pixels, w, h, (pad + size / 2) * ppd, (pad + size / 2) * ppd);
    }

    // The ink's box (pixels with at least a thirty-second of full alpha) against the ring's centre, in device pixels.
    private static (double Dx, double Dy) Deviation(double size, double thickness, string letter, double fontSize, double ppd, bool now)
    {
        var (pixels, w, h, cx, cy) = Render(size, thickness, letter, fontSize, ppd, now, drawn: false, dark: false);
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (pixels[(y * w + x) * 4 + 3] >= 8) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        if (x1 < 0) return (double.NaN, double.NaN);
        return ((x0 + x1 + 1) / 2.0 - cx, (y0 + y1 + 1) / 2.0 - cy);
    }

    public static void Run(Action<string, bool> check, string? folder = null)
    {
        var results = new List<(double Ppd, string Letter, (double Dx, double Dy) Old, (double Dx, double Dy) New)>();
        double worstNow = 0, worstThen = 0;
        foreach (double ppd in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
            foreach (string letter in new[] { "C", "G", "K", "O", "1", "文" })
            {
                // The collapsed row at its scale 1: the ring 12 DIP across with a 2 DIP track, the letter 7 DIP (MusicVisuals.Ring).
                double size = 12, thickness = 2, font = 7;
                var then = Deviation(size, thickness, letter, font, ppd, now: false);
                var now = Deviation(size, thickness, letter, font, ppd, now: true);
                results.Add((ppd, letter, then, now));
                worstNow = Math.Max(worstNow, Math.Max(Math.Abs(now.Dx), Math.Abs(now.Dy)));
                worstThen = Math.Max(worstThen, Math.Max(Math.Abs(then.Dx), Math.Abs(then.Dy)));
            }
        foreach (var r in results.Where(r => r.Letter is "C" or "G"))
            Console.WriteLine($"ring letter {r.Letter} at {r.Ppd * 100:0}%: ink centre minus ring centre (device px) before dx={r.Old.Dx:+0.00;-0.00} dy={r.Old.Dy:+0.00;-0.00}, now dx={r.New.Dx:+0.00;-0.00} dy={r.New.Dy:+0.00;-0.00}");
        foreach (var r in results.Where(r => Math.Max(Math.Abs(r.New.Dx), Math.Abs(r.New.Dy)) > .5))
            Console.WriteLine($"  over half a pixel: {r.Letter} at {r.Ppd * 100:0}%: now dx={r.New.Dx:+0.00;-0.00} dy={r.New.Dy:+0.00;-0.00}");
        Console.WriteLine($"ring letter worst offset over {results.Count} cases (device px): before {worstThen:0.00}, now {worstNow:0.00}");
        check("Ring letter, rendered: the ink box's centre is within 0.75 device pixel of the ring's centre at 100 to 200 %, for letters, a digit and a CJK character", worstNow <= .75);
        check("Ring letter, rendered: a TextBlock centred in the ring (before) was further off than the outline placed by its ink box", worstThen > worstNow);
        if (folder is null) return;

        Directory.CreateDirectory(folder);
        const int Zoom = 14, Gap = 12;
        var cells = new List<(byte[] Pixels, int Width, int Height, double Cx, double Cy)>();
        foreach (double ppd in new[] { 1.0, 1.5 })
            foreach (bool now in new[] { false, true })
            {
                var (p, w, h, cx, cy) = Render(12, 2, "C", 7, ppd, now, drawn: true, dark: true);
                cells.Add((p, w, h, cx, cy));
            }
        int cell = cells.Max(c => c.Width) * Zoom, total = cell * 2 + Gap * 3;
        var sheet = new byte[total * total * 4];
        for (int i = 0; i < sheet.Length; i += 4) { sheet[i] = 0x10; sheet[i + 1] = 0x10; sheet[i + 2] = 0x10; sheet[i + 3] = 0xFF; }
        for (int n = 0; n < cells.Count; n++)
        {
            var (p, w, h, cx, cy) = cells[n];
            int left = Gap + (n % 2) * (cell + Gap), top = Gap + (n / 2) * (cell + Gap);
            for (int y = 0; y < h * Zoom; y++)
                for (int x = 0; x < w * Zoom; x++)
                {
                    int src = ((y / Zoom) * w + x / Zoom) * 4, dst = ((top + y) * total + left + x) * 4;
                    Array.Copy(p, src, sheet, dst, 4);
                    // The ring's centre, as thin lines across the enlarged picture.
                    if (Math.Abs(y - cy * Zoom) < 1 || Math.Abs(x - cx * Zoom) < 1) { sheet[dst] = 0xFF; sheet[dst + 1] = 0x40; sheet[dst + 2] = 0xFF; }
                }
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(total, total, 96, 96, PixelFormats.Pbgra32, null, sheet, total * 4)));
        using var file = File.Create(System.IO.Path.Combine(folder, "ring-letter-compare.png"));
        encoder.Save(file);
    }
}
