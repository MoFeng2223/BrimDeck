using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using UI = BrimDeck.ModuleUi;

namespace BrimDeck.Thrm;

// Shared marks and the compact reading: a dial in the built-in system readings' own shape (three quarters of a
// circle open at the bottom, the reading's letter inside, sized and weighted like the row's dials at the row's
// scale), with the figure beside it. The letter and the figure follow the palette; only the share's arc takes the
// reading's level color.
internal static class ThermVisuals
{
    // A four-blade fan about a hub, on the 24 grid the tab icons draw in.
    public const string FanGlyph =
        "M 12,9.5 C 10.6,8.2 10.6,5.6 12,3.5 C 13.4,5.6 13.4,8.2 12,9.5 Z " +
        "M 14.5,12 C 15.8,10.6 18.4,10.6 20.5,12 C 18.4,13.4 15.8,13.4 14.5,12 Z " +
        "M 12,14.5 C 13.4,15.8 13.4,18.4 12,20.5 C 10.6,18.4 10.6,15.8 12,14.5 Z " +
        "M 9.5,12 C 8.2,13.4 5.6,13.4 3.5,12 C 5.6,10.6 8.2,10.6 9.5,12 Z " +
        "M 13.2,12 A 1.2,1.2 0 1 1 13.19,12";
    public const string CpuGlyph =
        "M 7,7 H 17 V 17 H 7 Z M 10,10 H 14 V 14 H 10 Z " +
        "M 9,7 V 4 M 12,7 V 4 M 15,7 V 4 M 9,17 V 20 M 12,17 V 20 M 15,17 V 20 " +
        "M 7,9 H 4 M 7,12 H 4 M 7,15 H 4 M 17,9 H 20 M 17,12 H 20 M 17,15 H 20";
    public const string GpuGlyph = "M 3,6 H 21 V 15 H 3 Z M 6,9 H 18 M 6,12 H 12 M 7,15 V 18 M 17,15 V 18";

    // A stroked mark that follows the palette: the stroke resource goes on the path itself, so the theme updates it.
    public static FrameworkElement Icon(string data, double size, string strokeResource)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data), StrokeThickness = 1.3, Stretch = Stretch.Uniform,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
        };
        path.SetResourceReference(Shape.StrokeProperty, strokeResource);
        return new Viewbox { Width = size, Height = size, Child = path };
    }

    internal static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    // The built-in row's rounding: whole DIPs for sizes, half DIPs for strokes and type.
    internal static double Whole(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
    internal static double Half(double value) => Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2;

    // Three quarters of a circle, from lower left over the top to lower right (the gap tells it from a full ring),
    // filled clockwise to the share. The centre sits a thirty-second of the size below, so the open circle looks centred.
    public static Geometry DialArc(double size, double ring, double fraction)
    {
        double radius = size / 2 - ring / 2;
        var center = new Point(size / 2, size / 2 + size / 32);
        Point At(double angle) => new(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        const double start = Math.PI * 1.25, sweep = Math.PI * 1.5;
        double span = sweep * Math.Clamp(fraction, 0, 1);
        var figure = new PathFigure { StartPoint = At(start), IsClosed = false };
        figure.Segments.Add(new ArcSegment(At(start + span), new Size(radius, radius), 0, span > Math.PI, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}

// One collapsed reading: the dial with its letter, and the figure beside it. The figure's room is fixed at the
// widest figure the reading can show, so the row does not breathe with the numbers.
internal sealed class ThermReading(string letter, string figurePreview)
{
    private double _scale = -1;
    private Grid _dial = null!;
    private Path _track = null!, _fill = null!;
    private TextBlock _figure = null!;
    private double _size, _ring;
    private FrameworkElement? _root;

    public FrameworkElement Root => _root ??= Build(1);

    private FrameworkElement Build(double scale)
    {
        _scale = scale;
        _size = Math.Max(4, ThermVisuals.Whole(16 * scale));
        _ring = Math.Max(1.2, ThermVisuals.Half(2 * scale));
        double letterSize = Math.Max(1, 7 * scale), font = Math.Max(1, ThermVisuals.Half(11.5 * scale));

        _track = new Path { StrokeThickness = _ring, Stretch = Stretch.None };
        _track.SetResourceReference(Shape.StrokeProperty, "PanelRingTrack");
        _fill = new Path { StrokeThickness = _ring, Stretch = Stretch.None, Data = ThermVisuals.DialArc(_size, _ring, 0) };
        var mark = new TextBlock
        {
            Text = letter, FontSize = letterSize, FontWeight = FontWeights.SemiBold, FontFamily = UI.PanelFont,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, _size / 32, 0, 0)
        };
        mark.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary");
        _dial = new Grid { Width = _size, Height = _size, VerticalAlignment = VerticalAlignment.Center };
        _dial.Children.Add(_track);
        _dial.Children.Add(_fill);
        _dial.Children.Add(mark);

        _figure = new TextBlock
        {
            // The panel's own face: the width below is measured with it, so the held room matches the rendered digits.
            FontSize = font, FontWeight = FontWeights.SemiBold, FontFamily = UI.PanelFont, TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Math.Round(4 * scale), 0, 0, 0)
        };
        _figure.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary");
        // The Width holds the widest figure this reading can show, so the numbers never move each other; the extra
        // two DIPs keep a face that hints wider from losing its last digit at the edge.
        _figure.Text = figurePreview;
        _figure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _figure.Width = Math.Ceiling(_figure.DesiredSize.Width) + 2;
        _figure.Text = "";

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_dial);
        row.Children.Add(_figure);
        return _root = row;
    }

    // Rebuild for a new row scale; the elements are cheap and the reading owns no timers.
    public void Rescale(double scale)
    {
        if (Math.Abs(_scale - scale) <= .01) return;
        string figure = _figure.Text;
        Build(scale);
        _figure.Text = figure;
    }

    public void Show(string figure, double share, string levelColor)
    {
        _figure.Text = figure;
        _fill.Data = ThermVisuals.DialArc(_size, _ring, share);
        _fill.Stroke = ThermVisuals.Brush(levelColor);
    }
}
