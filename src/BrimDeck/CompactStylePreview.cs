using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

internal static class CompactStylePreview
{
    private const double Width = 240, Height = 136;
    private static readonly DrawingBrush Desktop = CreateDesktop();

    public static FrameworkElement Create(CompactStyle style)
    {
        // Every option uses the same scene and scale so only the panel's shape changes.
        var screen = new Grid
        {
            Width = Width, Height = Height, Background = Desktop, IsHitTestVisible = false,
            Clip = new RectangleGeometry(new Rect(0, 0, Width, Height), 9, 9)
        };
        if (style == CompactStyle.Line)
        {
            screen.Children.Add(new Border { Background = UI.Brush("#18212B"), Width = 29, Height = 2.5,
                CornerRadius = new CornerRadius(1.25), Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top });
        }
        else
        {
            bool notch = style == CompactStyle.Notch;
            var island = new IslandBorder
            {
                Background = Brushes.Black, MaterialOpacity = 1,
                Width = notch ? 100 : 82, Height = 14, ShoulderRadius = notch ? 6.7 : 0,
                CornerRadius = notch ? new CornerRadius(0, 0, 5, 5) : new CornerRadius(7),
                Margin = new Thickness(0, notch ? 0 : 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top
            };
            // The miniature shows the summary rings the real island carries when it is collapsed.
            var detail = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (color, percent) in new[] { ("#E5A385", 47d), ("#98DBB0", 23), ("#9CB9FF", 16), ("#D4D5DF", 37) })
            {
                var ring = UI.Ring(percent, color, 4.6, .9); ring.Margin = new Thickness(0, 0, 3.4, 0); detail.Children.Add(ring);
            }
            detail.Margin = new Thickness(3.4, 0, 0, 0);
            island.Child = detail; screen.Children.Add(island);
        }
        return new Viewbox { Child = screen, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private static DrawingBrush CreateDesktop()
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Gradient("#8495A5", "#344658", new Point(0, 0), new Point(1, 1)), null, new Rect(0, 0, Width, Height));
            context.DrawGeometry(Gradient("#A8B5BF", "#52687C", new Point(0, 0), new Point(.8, 1)), null,
                Geometry.Parse("M -20,124 C 28,66 68,113 112,63 C 155,13 184,38 258,-18 L 260,142 L -20,142 Z"));
            context.DrawGeometry(Gradient("#718798", "#354B60", new Point(0, 0), new Point(1, 1)), null,
                Geometry.Parse("M -20,148 C 42,89 74,118 115,102 C 162,84 186,68 220,72 C 240,74 253,60 265,48 L 265,148 Z"));

            // A quiet, cropped window gives the miniature a desktop context.
            context.DrawRoundedRectangle(UI.Brush("#1A000000"), null, new Rect(85, 71, 149, 92), 9, 9);
            context.DrawRoundedRectangle(Gradient("#E52E3741", "#ED1B232C", new Point(0, 0), new Point(0, 1)),
                new Pen(UI.Brush("#36FFFFFF"), 1), new Rect(88, 67, 149, 92), 8, 8);
            context.DrawLine(new Pen(UI.Brush("#16FFFFFF"), 1), new Point(88, 84), new Point(237, 84));
            context.DrawLine(new Pen(UI.Brush("#689AA7B5"), 1), new Point(201, 75), new Point(207, 75));
            context.DrawRectangle(null, new Pen(UI.Brush("#689AA7B5"), 1), new Rect(216, 73, 4, 4));
            context.DrawRoundedRectangle(UI.Brush("#33495C6D"), null, new Rect(94, 90, 32, 9), 2, 2);
            context.DrawRoundedRectangle(UI.Brush("#44C4CED6"), null, new Rect(100, 94, 19, 1.5), .75, .75);
            foreach (double y in new[] { 107d, 118, 129 })
                context.DrawRoundedRectangle(UI.Brush("#339CA9B5"), null, new Rect(100, y, 18, 1.5), .75, .75);
            context.DrawRoundedRectangle(UI.Brush("#88C5D0D9"), null, new Rect(140, 95, 39, 3), 1.5, 1.5);
            context.DrawRoundedRectangle(UI.Brush("#30BECBD6"), null, new Rect(140, 106, 70, 2), 1, 1);
            context.DrawRoundedRectangle(UI.Brush("#30BECBD6"), null, new Rect(140, 113, 51, 2), 1, 1);
            context.DrawRoundedRectangle(UI.Brush("#706D94B4"), null, new Rect(140, 124, 30, 9), 3, 3);
            context.DrawLine(new Pen(UI.Brush("#40FFFFFF"), 1), new Point(0, .5), new Point(Width, .5));
        }
        var brush = new DrawingBrush(drawing) { Viewbox = new Rect(0, 0, Width, Height), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        brush.Freeze(); return brush;
    }

    private static LinearGradientBrush Gradient(string first, string last, Point start, Point end) => new()
    {
        StartPoint = start, EndPoint = end,
        GradientStops = [new GradientStop((Color)ColorConverter.ConvertFromString(first), 0), new GradientStop((Color)ColorConverter.ConvertFromString(last), 1)]
    };
}
