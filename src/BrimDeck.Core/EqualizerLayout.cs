namespace BrimDeck.Core;

// Where the level bars stand (BrimDeck.MusicEqualizer), on half device pixels: a level that moves the bar by less than that
// draws the same, so the window draws no new frame for it. Each frame the bars drew cost the layered island most of a
// millisecond, about one percent of a core for every ten frames a second.
public static class EqualizerLayout
{
    // Bar index of count, at level 0..1, in a box width by height DIPs with bars barWidth wide, at pixelsPerDip; the bar's
    // left, top, width and height in DIPs, centred on the box's height and never shorter than it is wide. middle: the line the bars
    // stand about, below the box's top, when it is not the exact half of the height (the collapsed row's midline, a whole device
    // pixel: half of a box an odd number of pixels tall is not).
    public static (double Left, double Top, double Width, double Height) Bar(int index, int count, double level, double width, double height, double barWidth, double pixelsPerDip, double middle = double.NaN)
    {
        double step = pixelsPerDip > 0 && double.IsFinite(pixelsPerDip) ? 2 * pixelsPerDip : 2;
        double Snap(double value) => double.IsFinite(value) ? Math.Round(value * step, MidpointRounding.AwayFromZero) / step : 0;
        double bar = Math.Max(0, barWidth), box = Math.Max(0, height);
        level = double.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;
        double tall = Snap(Math.Clamp(bar + level * Math.Max(0, box - bar), bar, Math.Max(bar, box)));
        double left = Snap(index * Math.Max(0, width - bar) / Math.Max(1, count - 1));
        return (left, Snap((double.IsFinite(middle) ? middle : box / 2) - tall / 2), bar, tall);
    }
}
