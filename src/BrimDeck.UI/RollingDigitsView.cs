using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using BrimDeck.Core;

namespace BrimDeck;

// A clipped window per digit, sharing the app's on-demand rendering loop. A wheel
// carries an unwrapped count, so 9 -> 0 and 59 -> 00 never spin in the wrong direction.
public sealed class RollingDigitsView : Grid
{
    private sealed class Cell : FrameworkElement
    {
        public RollingWheel Wheel = new();
        public double Size;
        public Brush Ink = Brushes.White;
        private readonly BlurEffect _blur = new() { RenderingBias = RenderingBias.Performance };
        private FormattedText[]? _glyphs;
        private Brush? _lastInk;
        private double _lastDpi;
        public Cell(double size)
        { Size = size; Height = Math.Ceiling(size * 1.45); Width = 0; ClipToBounds = true; Effect = _blur; IsHitTestVisible = false; }
        public void Paint()
        { _blur.Radius = Math.Min(2, Math.Abs(Wheel.Velocity) * 1.2); InvalidateVisual(); }
        protected override void OnRender(DrawingContext drawing)
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (_glyphs is null || !ReferenceEquals(Ink, _lastInk) || _lastDpi != dpi)
            {
                _lastInk = Ink; _lastDpi = dpi;
                _glyphs = Enumerable.Range(0, 10).Select(d => new FormattedText(d.ToString(), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface(UI.DisplayFont, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal), Size, Ink, dpi)).ToArray();
            }
            double floor = Math.Floor(Wheel.Position), fraction = Wheel.Position - floor;
            for (int offset = -1; offset <= 1; offset++)
            {
                int digit = (int)(((long)floor + offset) % Wheel.Radix + Wheel.Radix) % Wheel.Radix;
                var glyph = _glyphs[digit];
                drawing.DrawText(glyph, new Point((Math.Ceiling(Size * .62) - glyph.Width) / 2,
                    (Height - glyph.Height) / 2 + (offset - fraction) * Height));
            }
        }
    }
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _symbol;
    private readonly Cell[] _cells = new Cell[6];
    private readonly TextBlock[] _separators = new TextBlock[6];
    private bool _initialized, _clock, _numeric;
    private double _last;
    private double _size;
    private string _text = "";
    private int _lastMinimumDigits;
    private Brush _ink = Brushes.White;
    public int MinimumTimeDigits { get; set; } = 3;
    public double FontSize
    {
        get => _size;
        set
        {
            if (_size == value) return;
            _size = value; Build();
            // New cells must inherit the current figure when a responsive card changes type size.
            if (_text.Length > 0) Text = _text;
        }
    }
    public Brush Foreground
    {
        get => _ink;
        set
        {
            if (ReferenceEquals(_ink, value)) return;
            _ink = value; _symbol.Foreground = value;
            foreach (var cell in _cells) { cell.Ink = value; cell.InvalidateVisual(); }
            foreach (var separator in _separators) separator.Foreground = value;
        }
    }
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value && _initialized && _lastMinimumDigits == MinimumTimeDigits && Soft.Enabled) return;
            _lastMinimumDigits = MinimumTimeDigits;
            bool clock = value.Contains(':');
            long number = 0;
            bool numeric = clock ? value.Split(':').All(part => long.TryParse(part, out _)) : long.TryParse(value, out number);
            if (clock && numeric) foreach (string part in value.Split(':')) number = Math.Clamp(number * 60 + long.Parse(part), 0, 999999);
            _text = value; AutomationProperties.SetName(this, value);
            if (clock && numeric && value.Count(char.IsDigit) > _cells.Length)
            {
                // Long stopwatches keep their full elapsed hours instead of wrapping the six-digit wheels.
                _numeric = _initialized = false; _strip.Visibility = Visibility.Collapsed; _symbol.Visibility = Visibility.Visible;
                _symbol.FontSize = Math.Min(_size, 120d / Math.Max(1, value.Length * .6)); _symbol.Text = value; return;
            }
            if (!numeric)
            { _numeric = _initialized = false; _strip.Visibility = Visibility.Collapsed; _symbol.Visibility = Visibility.Visible; _symbol.FontSize = _size; _symbol.Text = value; return; }
            _numeric = true; _strip.Visibility = Visibility.Visible; _symbol.Visibility = Visibility.Collapsed;
            bool animate = _initialized && _clock == clock && Soft.Enabled && IsVisible;
            var places = RollingNumbers.Places(number, clock, clock ? Math.Max(MinimumTimeDigits, value.Count(char.IsDigit)) : 1);
            double now = Eased.Now;
            for (int p = 0; p < _cells.Length; p++)
            {
                bool shown = p < places.Count;
                var place = shown ? places[places.Count - 1 - p] : new RollingPlace(0, 10, false);
                var cell = _cells[p];
                if (cell.Wheel.Radix != place.Radix) cell.Wheel = new(place.Count, place.Radix);
                cell.Wheel.Set(place.Count, now, p * .035, animate && shown);
                WidthTo(cell, shown ? Math.Ceiling(_size * .62) : 0, animate);
                WidthTo(_separators[p], shown && place.SeparatorAfter ? Math.Ceiling(_size * .26) : 0, animate);
                cell.Paint();
            }
            _clock = clock; _initialized = true;
            if (animate) { _last = now; Motion.Start(this, Step); }
        }
    }
    public RollingDigitsView(double size = 30)
    {
        HorizontalAlignment = HorizontalAlignment.Right; VerticalAlignment = VerticalAlignment.Center;
        _symbol = UI.Centered("", size, UI.Primary, FontWeights.Medium, UI.DisplayFont);
        _symbol.Visibility = Visibility.Collapsed;
        var mask = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        mask.GradientStops.Add(new(Colors.Transparent, 0)); mask.GradientStops.Add(new(Colors.Black, .2));
        mask.GradientStops.Add(new(Colors.Black, .8)); mask.GradientStops.Add(new(Colors.Transparent, 1)); mask.Freeze();
        _strip.OpacityMask = mask; Children.Add(_strip); Children.Add(_symbol);
        FontSize = size;
    }
    private void Build()
    {
        _strip.Children.Clear(); _initialized = false;
        for (int p = 5; p >= 0; p--)
        {
            var cell = new Cell(_size) { Ink = _ink }; _cells[p] = cell; _strip.Children.Add(cell);
            var separator = UI.Centered(":", _size, UI.Primary, FontWeights.Medium, UI.DisplayFont);
            separator.Foreground = _ink; separator.Width = 0; separator.ClipToBounds = true;
            _separators[p] = separator; _strip.Children.Add(separator);
        }
        _symbol.FontSize = _size;
    }
    private bool Step(double now)
    {
        bool animate = Soft.Enabled && IsVisible && IsLoaded && _numeric;
        double dt = Math.Clamp(now - _last, 0, .05); _last = now; bool moving = false;
        foreach (var cell in _cells)
        {
            if (!animate) cell.Wheel.Snap(cell.Wheel.Count);
            else moving |= cell.Wheel.Step(dt, now);
            cell.Paint();
        }
        return animate && moving;
    }
    private static void WidthTo(FrameworkElement element, double width, bool animate)
    {
        double from = element.Width;
        if (animate && element.ReadLocalValue(WidthProperty) is double target && Math.Abs(target - width) < .01) return;
        if (!animate) { element.BeginAnimation(WidthProperty, null); element.Width = width; return; }
        if (Math.Abs(from - width) < .01) return;
        element.BeginAnimation(WidthProperty, null); element.Width = width;
        if (animate) element.BeginAnimation(WidthProperty, new DoubleAnimation(from, width, TimeSpan.FromMilliseconds(260))
        { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
    }
}
