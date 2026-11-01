using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The letter inside a collapsed quota ring, drawn as the glyph's outline and centred on that outline's own ink box. A TextBlock
// centres its line box, which holds the font's ascent and descent: a capital then sits above or below the ring's centre by a
// fraction of the font size (about a device pixel at 7 DIP), and the line is placed on whole device pixels besides. The outline
// is unhinted and placed by CompactMusicLayout.RingLetterOffset, so the ink's centre is the ring's to within half a device pixel
// at any scale. The stroke gives back the weight the unhinted outline lacks, evenly on every side (as MonitorDraw.Dial does),
// so it does not move the centre.
internal static class RingLetter
{
    private static readonly Dictionary<(string Letter, double Size, double Cell, double Ppd, string Font), (Geometry Glyph, double Stroke)> Made = [];

    // The letter's path, to put at the ring's top left corner (cell: the ring's size); null when the font draws nothing for it.
    public static Path? Create(string letter, double fontSize, double cell, double pixelsPerDip, FontFamily font, Brush ink)
    {
        if (letter.Length == 0) return null;
        double ppd = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        var key = (letter, fontSize, cell, ppd, font.Source ?? "");
        if (!Made.TryGetValue(key, out var made))
        {
            var text = new FormattedText(letter, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), fontSize, ink, ppd);
            var outline = text.BuildGeometry(new Point());
            var bounds = outline.Bounds;
            if (bounds.IsEmpty) return null;
            var (x, y) = CompactMusicLayout.RingLetterOffset(bounds.X, bounds.Y, bounds.Width, bounds.Height, cell, ppd);
            outline.Transform = new TranslateTransform(x, y);
            outline.Freeze();
            double pixels = fontSize * ppd;
            if (Made.Count > 64) Made.Clear();
            Made[key] = made = (outline, Math.Min(.06 * pixels, .17 * Math.Sqrt(pixels)) / ppd);
        }
        return new Path { Data = made.Glyph, Fill = ink, Stroke = ink, StrokeThickness = made.Stroke, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    }
}
