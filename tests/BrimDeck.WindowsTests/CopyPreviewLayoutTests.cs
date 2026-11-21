using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Core;

internal static class CopyPreviewLayoutTests
{
    public static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var parent = new Grid();
        var window = new Window { Left = -20000, Top = -20000, Width = 1400, Height = 200,
            ShowActivated = false, ShowInTaskbar = false, Content = parent, UseLayoutRounding = true };
        try
        {
            window.Show();
            foreach (string font in new[] { "", "Microsoft YaHei", "Arial" })
            foreach (double scale in new[] { .75, 1, 1.25, 1.5, 2 })
            {
                BrimDeck.Fonts.Use(font);
                double previous = 0;
                foreach (string body in new[] { "已做好新版：", new string('字', 10), new string('字', 20), new string('字', 30) })
                {
                    string text = "✓ 已复制：" + body;
                    double wanted = CopyPreviewLayout.Measure(parent, text, scale, "#FFFFFF");
                    check($"copy width grows with text ({font}/{scale}/{body.Length})", wanted > previous);
                    previous = wanted;
                    foreach (var style in new[] { CompactStyle.Capsule, CompactStyle.Notch })
                    {
                        var settings = new DeckSettings { CapsuleHeight = 32 * scale, NotchHeight = 32 * scale };
                        var screen = new DipRect(0, 0, 3000, 1000);
                        var shape = CollapsedGeometry.Resolve(settings, style, screen, screen, contentWanted: wanted);
                        double available = shape.ContentWidth;
                        if (style == CompactStyle.Notch) available = Math.Min(available, shape.Surface.Width - 2 * (shape.Shoulder + shape.BottomRadius));
                        double dpi = VisualTreeHelper.GetDpi(parent).DpiScaleX;
                        available = Math.Floor(available * dpi) / dpi;
                        var label = CopyPreviewLayout.Label(text, scale, "#FFFFFF");
                        parent.Children.Add(label);
                        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        double natural = label.DesiredSize.Width;
                        label.MaxWidth = available;
                        label.Measure(new Size(available, 100));
                        label.Arrange(new Rect(0, 0, available, 100));
                        check($"copy label fits without secondary ellipsis ({style}/{font}/{scale}/{body.Length})",
                            available >= natural + 1 && label.ActualWidth >= natural);
                        parent.Children.Remove(label);
                    }
                }
                check("measurement leaves no probe in the island", parent.Children.Count == 0);
            }
        }
        finally { window.Close(); BrimDeck.Fonts.Use(""); }
    }
}
