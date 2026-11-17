using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrimDeck;

internal static class CopyPreviewLayout
{
    internal static TextBlock Label(string text, double scale, string color)
        => UI.Fixed(text, 11.5 * scale, 16 * scale, color, FontWeights.SemiBold);

    internal static double Measure(Panel parent, string text, double scale, string color)
    {
        // Measure inside the destination visual tree: use the same font, inherited text options and DPI
        // as the visible label, then leave room for layout rounding at both ends. No fixed width cap.
        var probe = Label(text, scale, color);
        probe.Opacity = 0;
        probe.IsHitTestVisible = false;
        parent.Children.Add(probe);
        try
        {
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double dpi = VisualTreeHelper.GetDpi(parent).DpiScaleX;
            return Math.Ceiling(probe.DesiredSize.Width * dpi) / dpi + 4;
        }
        finally { parent.Children.Remove(probe); }
    }
}
