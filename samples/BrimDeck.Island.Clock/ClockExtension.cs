using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Island;

namespace BrimDeck.Island.Clock;

// A minimal extension, meant as the starting point for a new page: a large clock with the date under it. It reads
// nothing outside itself and uses only the contract assembly and WPF. Build it, put the DLL in the extensions folder,
// and reload from Settings > Plugins.
public sealed class ClockExtension : IIslandExtension
{
    // The views the panels hold, and the demand as last told: CreateView may come after SetDemand(true), and a rebuild
    // makes a new view while the old one is on its way out.
    private readonly List<WeakReference> _views = new();
    private bool _shown;

    public string Id => "sample-clock";
    public string TitleZh => "时钟";
    public string TitleEn => "Clock";
    public string Glyph => "M 12,3 A 9,9 0 1 1 11.99,3 M 12,7 V 12.5 L 15.5,14.5";

    public FrameworkElement CreateView(IslandColors colors)
    {
        // One view per panel showing the page; sharing one is fine too, but a view belongs to a single parent.
        var view = new ClockView(colors);
        _views.RemoveAll(reference => reference.Target is not ClockView);
        _views.Add(new WeakReference(view));
        view.Tick(_shown);
        return view;
    }

    // The page is on screen somewhere: keep the clocks ticking. Off screen: stop every timer, so an unseen page costs
    // nothing.
    public void SetDemand(bool shown)
    {
        _shown = shown;
        foreach (var reference in _views.ToList())
            if (reference.Target is ClockView view) view.Tick(shown);
    }
}

// The page's content: the time large in the middle, the date under it. It draws itself and updates once a second while
// the page is shown.
internal sealed class ClockView : FrameworkElement
{
    private static readonly Typeface Bold = new(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Regular = new(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly IslandColors _colors;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ClockView(IslandColors colors)
    {
        _colors = colors;
        _timer.Tick += (_, _) => InvalidateVisual();
    }

    public void Tick(bool shown)
    {
        if (shown) { _timer.Start(); InvalidateVisual(); }
        else _timer.Stop();
    }

    protected override Size MeasureOverride(Size availableSize) => availableSize;

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var now = DateTime.Now;
        double size = Math.Clamp(Math.Min(h * .38, w * .22), 22, 68);
        var time = Text(now.ToString("HH:mm", CultureInfo.CurrentCulture), size, _colors.Primary, Bold);
        var date = Text(now.ToString("yyyy-MM-dd dddd", CultureInfo.CurrentCulture), Math.Max(11, size * .28), _colors.Tertiary, Regular);
        double total = time.Height + 6 + date.Height;
        double top = (h - total) / 2;
        drawing.DrawText(time, new Point((w - time.WidthIncludingTrailingWhitespace) / 2, top));
        drawing.DrawText(date, new Point((w - date.WidthIncludingTrailingWhitespace) / 2, top + time.Height + 6));
    }

    private static FormattedText Text(string text, double size, string color, Typeface face)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, Brush(color), 1);
    private static Brush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
}
