using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The one line every part of the collapsed row stands on. The parts are built apart (the system readings and the devices draw
// themselves, the notices and the file count are laid out from elements, the title is a marquee, the clock is filled outlines),
// and each used to find the middle of the row and the place of its text by itself: a figure drawn at the row's half plus .36 of
// its size, rounded to a whole DIP; a count centred by its line box, which holds the font's ascent and descent and so stood a
// pixel below the figures; an open dial centred by its outline, its letter a pixel and a half below the figure beside it. Each
// was right or wrong by its own amount at each display scale and each height of the row.
//
// Here the row has one midline, on a whole device pixel below the row's top (Middle), and its text one baseline for each size, on
// a whole device pixel too (Baseline): the one that puts the middle of the font's digits on the midline, taken from the font's own
// outlines, so it holds for a font the user chose. A mark is centred on the midline (rings, dials, icons); text of any kind,
// digits, Latin or Chinese, in any weight, shares the baseline of its size.
//
// A part keeps its own height. Inside it the midline is Middle of that height, and the row puts the part where the two midlines
// meet (Top), which is a whole device pixel: so a part standing alone (a line of the column on a side edge) is laid out the same.
internal static class CompactLine
{
    private static double Scale(double dpi) => double.IsFinite(dpi) && dpi > 0 ? dpi : 1;
    public static double Snap(double value, double dpi) => double.IsFinite(value) ? Math.Round(value * Scale(dpi), MidpointRounding.AwayFromZero) / Scale(dpi) : 0;

    // The midline of a box this tall, below its top: half its height, on a whole device pixel.
    public static double Middle(double height, double dpi) => Snap(Math.Max(0, double.IsFinite(height) ? height : 0) / 2, dpi);

    // Where a part this tall starts below the row's top, so that its midline is the row's.
    public static double Top(double row, double height, double dpi) => Middle(row, dpi) - Middle(height, dpi);

    // The same for a part that centres what it draws on the exact half of its own height, which need not be a whole device pixel
    // (the clock: its digits are fitted about the half of its face): the part starts on the whole pixel that brings that half
    // nearest the row's midline.
    public static double TopOfCentred(double row, double height, double dpi) => Snap(Middle(row, dpi) - Math.Max(0, double.IsFinite(height) ? height : 0) / 2, dpi);

    // The baseline of text of this size in a box this tall, below the box's top.
    public static double Baseline(double height, double size, double dpi) => Above(Middle(height, dpi), size, dpi);

    // The baseline that puts the digits' middle on a line (middle, below the same top), on a whole device pixel.
    public static double Above(double middle, double size, double dpi) => Snap(middle + Rise * size, dpi);

    // How tall the font's digits are, for each DIP of its size.
    public static double DigitHeight => 2 * Rise;

    // How far the middle of the panel font's digits lies above the baseline, for each DIP of its size: from the outlines of all
    // ten, so lining digits give half their height and old-style ones the middle of the band they fill. The regular weight
    // speaks for all weights (a font's digits keep their height across its weights), which keeps one baseline for one size.
    private static double Rise
    {
        get
        {
            if (_riseFont == Fonts.Version) return _rise;
            const double size = 100;
            var text = new FormattedText("0123456789", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Fonts.Face(UiFontRole.Panel), size, Brushes.Black, 1);
            var ink = text.BuildGeometry(new Point()).Bounds;
            // A font that draws no digits: about what Segoe UI's give.
            _rise = ink.IsEmpty || ink.Height <= 0 ? .35 : Math.Clamp((text.Baseline - (ink.Top + ink.Bottom) / 2) / size, .2, .5);
            _riseFont = Fonts.Version;
            return _rise;
        }
    }
    private static double _rise;
    private static int _riseFont = -1;

    // Puts a text block's baseline at the given height below the top of the panel it lies in (a Grid or a Canvas, which leave it
    // at its top): by its top margin, off the layout's rounding, which would move the block and its baseline with it by up to
    // half a pixel. The block's own baseline holds whatever it shows later (a running time), as long as its font stays.
    public static void Place(TextBlock text, double baseline)
    {
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double own = text.BaselineOffset;
        if (!double.IsFinite(own)) own = text.FontFamily.Baseline * text.FontSize;
        text.VerticalAlignment = VerticalAlignment.Top; text.UseLayoutRounding = false;
        var margin = text.Margin;
        text.Margin = new Thickness(margin.Left, baseline - own, margin.Right, 0);
    }
}
