using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// BrimDeck application icon: a near-black rounded square filling the canvas, a high-contrast "B" (thick stems, thin bars,
// bowls that thin toward their joins), a thin outline that follows the letter's lower half and fades in from the waist,
// and a counter-clockwise quota ring docked into the upper bowl's shoulder (the letter is cut around it).
// Composition on a 256-unit canvas: square 0–256 with corner radius 42; the mark is scaled 1.12 about (129, 128) so the letter
// matches the neighbouring task bar icons, moved up 0.5 so the letter itself sits at the centre, then grown 256/240 with the square.
// Sizes of 24 and below thicken the bars (keeping stems about twice as thick) and the ring, and widen the outline's gap;
// 20 and below drop the outline; 40 and below draw the ring as a full coral circle.
// Usage: dotnet run --project scripts/icon -c Release -- <output.ico> <preview.png>
internal static class Program
{
    // One frame for every size Windows asks for at the common display scales (100 %–300 %), so no size is resampled from another:
    // task bar 24·scale (24, 30, 36, 42, 48, 54, 60, 72), notification area 16·scale (16, 20, 24, 28, 32, 36, 40, 48),
    // Explorer and desktop 32·scale (32, 40, 48, 56, 64, 72, 80, 96), plus 128 and 256 for large views.
    private static readonly int[] Sizes = [16, 20, 24, 28, 30, 32, 36, 40, 42, 48, 54, 56, 60, 64, 72, 80, 96, 128, 256];
    private static readonly Color Ink = Color.FromRgb(0xE8, 0xE9, 0xEF), Dot = Color.FromRgb(0xE5, 0xA3, 0x85);

    [STAThread]
    private static void Main(string[] args)
    {
        string icoPath = args.Length > 0 ? args[0] : "BrimDeck.ico";
        string previewPath = args.Length > 1 ? args[1] : "icon-preview.png";
        var frames = Sizes.Select(size => (Size: size, Png: Encode(Render(size)))).ToList();
        WriteIco(icoPath, frames);
        File.WriteAllBytes(previewPath, Encode(Preview(frames.Select(f => Decode(f.Png)).ToList())));
        Console.WriteLine($"icon: {icoPath} ({new FileInfo(icoPath).Length} bytes); preview: {previewPath}");
    }

    private sealed record Glyph(double Stem, double Bar, double UpperSide, double LowerSide);
    private const double Left = 80, Top = 60, Bottom = 196, UpperX = 132, UpperRx = 36, LowerX = 136, LowerRx = 42;

    // Outer bowls clockwise, counters counter-clockwise, non-zero fill. Each bowl's outer and inner edges are half-ellipses
    // with different x radii, so the bowl is thickest at its right side and has the bar's thickness where it meets the stem.
    private static Geometry Letter(Glyph g)
    {
        double mid = Top + (Bottom - Top) * .47, upperBottom = mid + g.Bar / 2, lowerTop = mid - g.Bar / 2, stem = Left + g.Stem;
        double upperRy = (upperBottom - Top) / 2, lowerRy = (Bottom - lowerTop) / 2;
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var c = geometry.Open())
        {
            Bowl(c, Top, upperBottom, UpperX, UpperRx, upperRy);
            Bowl(c, lowerTop, Bottom, LowerX, LowerRx, lowerRy);
            Counter(c, Top + g.Bar, upperBottom - g.Bar, UpperX, UpperRx - g.UpperSide, upperRy - g.Bar);
            Counter(c, lowerTop + g.Bar, Bottom - g.Bar, LowerX, LowerRx - g.LowerSide, lowerRy - g.Bar);
        }
        geometry.Freeze();
        return geometry;

        static void Bowl(StreamGeometryContext c, double y0, double y1, double x, double rx, double ry)
        {
            c.BeginFigure(new Point(Left, y0), true, true);
            c.LineTo(new Point(x, y0), false, false);
            c.ArcTo(new Point(x, y1), new Size(rx, ry), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(Left, y1), false, false);
        }
        void Counter(StreamGeometryContext c, double y0, double y1, double x, double rx, double ry)
        {
            c.BeginFigure(new Point(stem, y0), true, true);
            c.LineTo(new Point(stem, y1), false, false);
            c.LineTo(new Point(x, y1), false, false);
            c.ArcTo(new Point(x, y0), new Size(rx, ry), 0, false, SweepDirection.Counterclockwise, false, false);
        }
    }

    // The letter's outer contour: the upper bowl down to where it enters the lower bowl, then the lower bowl.
    private static Geometry Silhouette(Glyph g)
    {
        double mid = Top + (Bottom - Top) * .47, upperBottom = mid + g.Bar / 2, lowerTop = mid - g.Bar / 2;
        double upperRy = (upperBottom - Top) / 2, upperCy = Top + upperRy, lowerRy = (Bottom - lowerTop) / 2, lowerCy = lowerTop + lowerRy;
        var join = new Point(LowerX + LowerRx, lowerCy);
        for (int i = 1; i < 2000; i++)
        {
            double t = -Math.PI / 2 + Math.PI * i / 2000;
            var p = new Point(UpperX + UpperRx * Math.Cos(t), upperCy + upperRy * Math.Sin(t));
            if (Math.Pow((p.X - LowerX) / LowerRx, 2) + Math.Pow((p.Y - lowerCy) / lowerRy, 2) <= 1) { join = p; break; }
        }
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(new Point(Left, Top), true, true);
            c.LineTo(new Point(UpperX, Top), true, false);
            c.ArcTo(join, new Size(UpperRx, upperRy), 0, false, SweepDirection.Clockwise, true, false);
            c.ArcTo(new Point(LowerX, Bottom), new Size(LowerRx, lowerRy), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(Left, Bottom), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    // A band `width` wide that sits `gap` outside the contour, with mitred corners.
    private static Geometry Outline(Geometry contour, double gap, double width)
    {
        Geometry Grown(double by) => new CombinedGeometry(GeometryCombineMode.Union, contour,
            contour.GetWidenedPathGeometry(new Pen(Brushes.Black, by * 2) { LineJoin = PenLineJoin.Miter, MiterLimit = 8 }));
        return new CombinedGeometry(GeometryCombineMode.Exclude, Grown(gap + width), Grown(gap));
    }

    private static RenderTargetBitmap Render(int size)
    {
        bool small = size <= 24;
        // Stems stay about twice the bars at every size, so the letter keeps its contrast; small sizes only thicken the bars
        // enough to stay about 1.3 px wide (10 units is 1.3 px from 28 px up).
        var glyph = size switch
        {
            <= 16 => new Glyph(32, 16, 28, 30),
            <= 20 => new Glyph(28, 14, 25, 27),
            <= 24 => new Glyph(24, 12, 21, 23),
            _ => new Glyph(22, 10, 19, 21),
        };
        // The outline stays a hairline at every size: 7 units is about 0.8 px at 24, the same as 6 units at 28.
        double gap = small ? 12 : 10, lineWidth = small ? 7 : 6;
        double ringRadius = small ? 15 : 13, ringWidth = small ? 12 : 8, ringGap = small ? 12 : 5;
        // The ring sits on the upper bowl's 45° diagonal and bites 7 into it (6 at small sizes). Deeper bites leave the shoulder
        // thinner than the bars at 36 px (bite 10 leaves about 0.8 px); this leaves about 1.3 px.
        double mid = Top + (Bottom - Top) * .47, upperRy = (mid + glyph.Bar / 2 - Top) / 2, upperCy = Top + upperRy;
        double cut = ringRadius + ringWidth / 2 + ringGap, reach = cut - (small ? 6 : 7);
        var ring = new Point(UpperX + (UpperRx + reach) * .7071, upperCy - (upperRy + reach) * .7071);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256d, size / 256d));
            var fill = new LinearGradientBrush(Color.FromRgb(0x1C, 0x1E, 0x25), Color.FromRgb(0x0B, 0x0C, 0x10), 90);
            dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, 256, 256), 42, 42);
            if (size >= 48)
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)), 2), new Rect(1, 1, 254, 254), 41, 41);
            // Outer to inner: grow with the square, centre the letter vertically, enlarge the mark.
            dc.PushTransform(new ScaleTransform(256 / 240d, 256 / 240d, 128, 128));
            dc.PushTransform(new TranslateTransform(0, -.5));
            dc.PushTransform(new ScaleTransform(1.12, 1.12, 129, 128));

            // The outline (55 % of the ink) appears from the waist down: transparent at y 112, full at y 176.
            var fade = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xFF, 0, 0, 0), new Point(0, 112), new Point(0, 176)) { MappingMode = BrushMappingMode.Absolute };
            // At 16 and 20 pixels (the notification area at 100 % and 125 %) the outline crowds the letter into a blur, so only the letter and ring remain.
            if (size > 20)
            {
                dc.PushOpacityMask(fade);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x8C, Ink.R, Ink.G, Ink.B)), null, Outline(Silhouette(glyph), gap, lineWidth));
                dc.Pop();
            }
            dc.DrawGeometry(new SolidColorBrush(Ink), null, new CombinedGeometry(GeometryCombineMode.Exclude, Letter(glyph), new EllipseGeometry(ring, cut, cut)));
            DrawRing(dc, ring, ringRadius, ringWidth, progress: size > 40);

            dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // Matches UI.Ring: a 16 % white track and a coral arc (72 %) from the top running counter-clockwise. At 40 px and below
    // the track turns muddy, so the ring is drawn as a full coral circle.
    private static void DrawRing(DrawingContext dc, Point c, double r, double width, bool progress)
    {
        if (!progress) { dc.DrawEllipse(null, new Pen(new SolidColorBrush(Dot), width), c, r, r); return; }
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF)), width), c, r, r);
        double angle = .72 * Math.PI * 2;
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(new Point(c.X, c.Y - r), false, false);
            g.ArcTo(new Point(c.X - r * Math.Sin(angle), c.Y - r * Math.Cos(angle)), new Size(r, r), 0, true, SweepDirection.Counterclockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Dot), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
    }

    // Three bands: a dark task bar, a light task bar, and the tray overflow panel at 150 % (24 px).
    private static RenderTargetBitmap Preview(IReadOnlyList<BitmapSource> frames)
    {
        const int band = 300, panelHeight = 120;
        int width = 48 + frames.Sum(f => f.PixelWidth + 22);
        int height = band * 2 + panelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x28)), null, new Rect(0, 0, width, band));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF3)), null, new Rect(0, band, width, band));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x46, 0x56, 0x6C)), null, new Rect(0, band * 2, width, panelHeight));
            double x = 24;
            foreach (var frame in frames)
            {
                foreach (int row in new[] { 0, 1 })
                {
                    double y = row * band + (band - frame.PixelHeight) / 2;
                    dc.DrawImage(frame, new Rect(Math.Round(x), Math.Round(y), frame.PixelWidth, frame.PixelHeight));
                }
                x += frame.PixelWidth + 22;
            }
            var tray = frames.First(f => f.PixelWidth == 24);
            for (int i = 0; i < 5; i++)
                dc.DrawImage(tray, new Rect(24 + i * 40 + 8, band * 2 + 48, 24, 24));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource Decode(byte[] png)
    {
        var decoder = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    private static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Png)> frames)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(png.Length); writer.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames) writer.Write(png);
    }
}
