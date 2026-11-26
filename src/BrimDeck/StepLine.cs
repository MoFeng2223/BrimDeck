using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrimDeck;

// A mark before a text that may wrap: a numbered step's round mark with its number, a status line's colored dot. The mark's
// middle goes on the middle of the first line's letters as the screen draws them, and a number's on the mark's middle. A
// TextBlock's box holds the font's ascent and descent, and Chinese drawn from Microsoft YaHei UI sits about 1 DIP below its
// middle, by an amount that changes with the display scale; so the letters are drawn once at the scale in use and the mark
// placed on the middle of their ink. The ink's edges are the first and last rows of pixels at least half covered, which is
// where the eye puts them: weighing the paler rows in as parts of a row (InkCentredText.InkSpan) left the dot 1 pixel low at
// 150 % and a step's number 1 pixel high.
//
// A dot is placed on whole device pixels, and made one pixel larger where its height and the letters' differ by an odd
// number of pixels: no place centers a 6-pixel dot on 11 rows of letters, and lines whose letters measured a hair apart were
// rounded half a pixel up and half a pixel down, so that dots next to each other sat at different heights.
internal sealed class StepLine : Panel
{
    private readonly FrameworkElement _mark;
    private readonly TextBlock? _number;
    private readonly TextBlock _text;
    private readonly double _gap;
    // The dot's size as asked for, which is fitted to the letters (a mark with a number keeps its size).
    private readonly double _size;
    private double _top;

    public StepLine(FrameworkElement mark, TextBlock text, double gap, TextBlock? number = null)
    {
        _mark = mark; _number = number; _text = text; _gap = gap; _size = mark.Height;
        if (mark is Border border && number is not null)
        {
            border.Child = number;
            number.VerticalAlignment = VerticalAlignment.Top; number.HorizontalAlignment = HorizontalAlignment.Center;
        }
        mark.VerticalAlignment = VerticalAlignment.Top; mark.UseLayoutRounding = false;
        Children.Add(mark); Children.Add(text);
    }

    protected override Size MeasureOverride(Size available)
    {
        Place();
        _mark.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = MarkWidth();
        _text.Measure(new Size(Math.Max(0, available.Width - left), double.PositiveInfinity));
        return new Size(left + _text.DesiredSize.Width, Math.Max(_top + _mark.DesiredSize.Height, _text.DesiredSize.Height));
    }

    // Where the mark goes below the text's top, and for a dot its size, from the letters' ink at the scale in use.
    private void Place()
    {
        double pixels = UI.PixelsPerDip(this);
        if (_text.Text.Length == 0 || double.IsNaN(_size)) { _top = 0; return; }
        var (first, last) = Ink(_text, pixels);
        double padding = _text.Padding.Top;
        if (_number is not null)
        {
            _top = Math.Max(0, padding + (first + last) / 2.0 / pixels - _size / 2);
            return;
        }
        int ink = last - first, size = (int)Math.Round(_size * pixels);
        if ((ink - size) % 2 != 0) size += _size * pixels >= size ? 1 : -1;
        size = Math.Max(1, size);
        if (Math.Abs(_mark.Width - size / pixels) > 1e-6) { _mark.Width = size / pixels; _mark.Height = size / pixels; }
        _top = Math.Max(0, padding + (first + (ink - size) / 2) / pixels);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double left = MarkWidth();
        _text.Arrange(new Rect(left, 0, Math.Max(0, final.Width - left), _text.DesiredSize.Height));
        if (_number is not null)
        {
            double pixels = UI.PixelsPerDip(this);
            var (top, bottom) = Ink(_number, pixels);
            var margin = new Thickness(0, Math.Max(0, _mark.DesiredSize.Height / 2 - (top + bottom) / 2.0 / pixels), 0, 0);
            if (_number.Margin != margin) _number.Margin = margin;
        }
        _mark.Arrange(new Rect(0, _top, _mark.DesiredSize.Width, _mark.DesiredSize.Height));
        return final;
    }

    // Moved to a screen at another scale, the letters fall on other pixels: they are measured again there. The text's own
    // size in DIPs may stay the same, which would leave this panel's layout as it was.
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => InvalidateMeasure();

    // A collapsed mark takes no room.
    private double MarkWidth() => _mark.Visibility == Visibility.Collapsed ? 0 : _mark.DesiredSize.Width + _gap;

    private static readonly Dictionary<(string Text, string Font, double Size, int Weight, TextFormattingMode Mode, double Line, double Pixels, Color Color), (int First, int Last)> Spans = [];

    // The rows of device pixels where text's letters start and end (the row after the last) below the top of its first line
    // when drawn at pixels device pixels per DIP. The letters are drawn in their own color on a background as dark or light as
    // the window's opposite: WPF draws light text on dark thinner than dark text on light, and drawn white on nothing the
    // letters measured a row shorter than the same gray letters on the light settings window.
    private static (int First, int Last) Ink(TextBlock text, double pixels)
    {
        var mode = TextOptions.GetTextFormattingMode(text);
        double line = text.LineStackingStrategy == LineStackingStrategy.BlockLineHeight && double.IsFinite(text.LineHeight) ? text.LineHeight : double.NaN;
        var color = (text.Foreground as SolidColorBrush)?.Color ?? Colors.Black;
        var key = (text.Text, text.FontFamily.Source, text.FontSize, text.FontWeight.ToOpenTypeWeight(), mode, line, pixels, color);
        if (Spans.TryGetValue(key, out var span)) return span;
        static double Light(byte r, byte g, byte b) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
        double ink = Light(color.R, color.G, color.B), back = ink < 0.5 ? 1 : 0;
        var probe = new TextBlock { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight, FontStyle = text.FontStyle,
            Foreground = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)) };
        if (double.IsFinite(line)) { probe.LineHeight = line; probe.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; }
        var host = new Border { Child = probe, Background = back > 0 ? Brushes.White : Brushes.Black, UseLayoutRounding = true };
        TextOptions.SetTextFormattingMode(host, mode);
        VisualTreeHelper.SetRootDpi(host, new DpiScale(pixels, pixels));
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        int width = Math.Max(1, (int)Math.Ceiling(host.ActualWidth * pixels)), height = Math.Max(1, (int)Math.Ceiling(host.ActualHeight * pixels));
        var bitmap = new RenderTargetBitmap(width, height, 96 * pixels, 96 * pixels, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var bytes = new byte[width * height * 4];
        bitmap.CopyPixels(bytes, width * 4, 0);
        // A pixel is the letters' where it is at least halfway from the background to their color.
        double reach = Math.Abs(back - ink) / 2;
        int first = -1, last = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (Math.Abs(Light(bytes[i + 2], bytes[i + 1], bytes[i]) - back) >= reach) { if (first < 0) first = y; last = y; break; }
            }
        span = first < 0 ? (height / 2, height / 2) : (first, last + 1);
        if (Spans.Count > 64) Spans.Clear();
        Spans[key] = span;
        return span;
    }
}
