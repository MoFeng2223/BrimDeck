using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BrimDeck;

// Phosphor Bold paths and their MIT notice are embedded unmodified from Edge-Drop's fixed source.
internal static class EdgeDropGlyph
{
    private static readonly Lazy<IReadOnlyDictionary<string, Geometry>> Paths = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BrimDeck.EdgeDrop.Phosphor")!;
        using var reader = new System.IO.StreamReader(stream);
        var paths = new Dictionary<string, Geometry>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(reader.ReadToEnd(), @"(?m)^\s*(\w+)\s*:\s*['""]([^'""]+)['""]"))
        { var geometry = Geometry.Parse(match.Groups[2].Value); geometry.Freeze(); paths.TryAdd(match.Groups[1].Value, geometry); }
        return paths;
    });
    internal static FrameworkElement Create(string name, double size = 14)
    {
        var path = new Path { Data = Paths.Value[name], Stretch = Stretch.None, RenderTransform = new TranslateTransform(-10, -10) };
        path.SetResourceReference(Shape.FillProperty, "PanelPrimary");
        var canvas = new Canvas { Width = 236, Height = 236 }; canvas.Children.Add(path);
        return new Viewbox { Child = canvas, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    }
}
