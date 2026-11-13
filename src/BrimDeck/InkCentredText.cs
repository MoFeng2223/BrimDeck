using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrimDeck;

// A one-line text placed so that its letters, as the screen draws them, are centred in the height this element is given. A
// TextBlock centres its line box, which holds the font's ascent and descent: with the panel font, Chinese drawn from Microsoft
// YaHei UI sat about 1 DIP below the middle of an 11.5 line, and other fonts the user may choose sit higher or lower by their
// own amounts. Where the letters land also depends on the display scale, since glyphs snap to device pixels, and fonts such
// as SimSun draw small sizes from bitmaps rather than from their outlines. So, as CapDot does, the text is drawn once at the
// scale in use and its ink measured: Chinese text by its own ink, other text by the centre of its capitals (an "H"), so
// that descenders do not lift it. The text is placed on whole device pixels, where the measurement was made.
internal sealed class InkCentredText : FrameworkElement
{
    private static readonly Dictionary<(string Text, string Font, double Size, int Weight, bool Italic, TextFormattingMode Mode, double Pixels), double> Middles = [];
    private readonly TextBlock _text;

    public InkCentredText(TextBlock text)
    {
        _text = text;
        AddVisualChild(text);
        AddLogicalChild(text);
    }

    public TextBlock Text => _text;

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _text;
    protected override IEnumerator LogicalChildren => new object[] { _text }.GetEnumerator();

    protected override Size MeasureOverride(Size available)
    {
        _text.Measure(available);
        return _text.DesiredSize;
    }

    protected override Size ArrangeOverride(Size size)
    {
        double pixels = UI.PixelsPerDip(this), height = _text.DesiredSize.Height;
        double top = Math.Round((size.Height / 2 - InkMiddle(_text, pixels)) * pixels) / pixels;
        _text.Arrange(new Rect(0, top, size.Width, height));
        return size;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => InvalidateArrange();

    // How far below the top of text's line its letters' middle is drawn at pixels device pixels per DIP, in DIP. Each edge
    // is placed within its pixel row by how much of the row the glyphs cover.
    internal static double InkMiddle(TextBlock text, double pixels)
    {
        string probeText = text.Text.Any(IsChinese) ? text.Text : "H";
        var mode = TextOptions.GetTextFormattingMode(text);
        var key = (probeText, text.FontFamily.Source, text.FontSize, text.FontWeight.ToOpenTypeWeight(), text.FontStyle != FontStyles.Normal, mode, pixels);
        if (Middles.TryGetValue(key, out var middle)) return middle;
        var probe = new TextBlock { Text = probeText, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight, FontStyle = text.FontStyle,
            Foreground = Brushes.White, UseLayoutRounding = true };
        TextOptions.SetTextFormattingMode(probe, mode);
        VisualTreeHelper.SetRootDpi(probe, new DpiScale(pixels, pixels));
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        probe.Arrange(new Rect(probe.DesiredSize));
        int width = Math.Max(1, (int)Math.Ceiling(probe.ActualWidth * pixels)), height = Math.Max(1, (int)Math.Ceiling(probe.ActualHeight * pixels));
        var bitmap = new RenderTargetBitmap(width, height, 96 * pixels, 96 * pixels, PixelFormats.Pbgra32);
        bitmap.Render(probe);
        var bytes = new byte[width * height * 4];
        bitmap.CopyPixels(bytes, width * 4, 0);
        double top = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            int cover = 0;
            for (int x = 0; x < width; x++) cover = Math.Max(cover, bytes[(y * width + x) * 4 + 3]);
            if (cover == 0) continue;
            if (top < 0) top = y + 1 - cover / 255.0;
            bottom = y + cover / 255.0;
        }
        middle = top < 0 ? probe.ActualHeight / 2 : (top + bottom) / 2 / pixels;
        if (Middles.Count > 64) Middles.Clear();
        Middles[key] = middle;
        return middle;
    }

    // CJK radicals, symbols and punctuation, kana, the unified ideographs, compatibility ideographs and full-width forms.
    private static bool IsChinese(char c) => c is >= '\u2E80' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' or >= '\uFF00' and <= '\uFFEF';
}
