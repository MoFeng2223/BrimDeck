using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;

namespace ReadmeMedia;

// The windows that ask before DingTalk's and WeChat's messages are read (ChatReadWindow), beside NetEase Cloud Music's full
// control that they follow, in the light and the dark theme: each window's content drawn at its width. For checking, not for the
// README.
internal sealed partial class Renderer
{
    private async Task ChatReadScene()
    {
        var chat = Type.GetType("BrimDeck.ChatReadWindow, BrimDeck")!;
        var netease = Type.GetType("BrimDeck.NeteaseFileControlWindow, BrimDeck")!;
        foreach (var (theme, name) in new[] { (SettingsTheme.Light, "light"), (SettingsTheme.Dark, "dark") })
        {
            await Shot("chat-read-dingtalk-" + name, (Window)chat.GetMethod("DingTalk")!.Invoke(null, [theme])!);
            await Shot("chat-read-wechat-" + name, (Window)chat.GetMethod("WeChat")!.Invoke(null, [theme])!);
            await Shot("chat-read-netease-" + name, (Window)Activator.CreateInstance(netease, Program.Any, null, new object[] { theme }, null)!);
        }

        // QQ's steps under its opened row, at the row's width in a wide and in the narrowest settings window.
        foreach (var (theme, name) in new[] { (SettingsTheme.Light, "light"), (SettingsTheme.Dark, "dark") })
        {
            _settings.Theme = theme;
            var settings = new SettingsWindow(app);
            var (heading, steps) = ((string, string[]))typeof(SettingsWindow).GetMethod("CommonSteps", Program.Any)!.Invoke(null, ["windows:QQ"])!;
            foreach (var width in new[] { 600.0, 420.0 })
            {
                var block = (FrameworkElement)typeof(SettingsWindow).GetMethod("Steps", Program.Any)!.Invoke(settings, [heading, steps])!;
                var host = new Border { Child = block, Resources = settings.Resources, Width = width, Padding = new Thickness(16, 0, 16, 0) };
                host.SetResourceReference(Border.BackgroundProperty, "SettingsSurface");
                const double scale = 1.5;
                host.Measure(new Size(width, double.PositiveInfinity)); host.Arrange(new Rect(host.DesiredSize));
                await Settle();
                host.Measure(new Size(width, double.PositiveInfinity)); host.Arrange(new Rect(host.DesiredSize)); host.UpdateLayout();
                var picture = new RenderTargetBitmap((int)(width * scale), (int)Math.Ceiling(host.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                picture.Render(host); picture.Freeze();
                Write(picture, Path.Combine(output, $"qq-steps-{width:0}-{name}.png"));
                host.Child = null;
            }
            try { settings.Close(); } catch (ArgumentOutOfRangeException) { }
        }

        // The status dot and a numbered step's mark beside their text (StepLine), at the scales of usual screens in the settings
        // window's text mode: how far the mark's middle is from the middle of the text's ink, in device pixels.
        var stepType = Type.GetType("BrimDeck.StepLine, BrimDeck")!;
        var panelFont = (FontFamily)Type.GetType("BrimDeck.UI, BrimDeck")!.GetProperty("PanelFont", Program.Any)!.GetValue(null)!;
        // Every scale Windows offers, and the text's colors in the light and the dark settings window on their surfaces.
        foreach (var pixels in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 3.0, 3.5 })
            foreach (var (mode, ink, surface) in new[] { (TextFormattingMode.Display, "#5C5F6B", "#FAFAFB"), (TextFormattingMode.Display, "#A9A9B2", "#1B1B1F"), (TextFormattingMode.Ideal, "#5C5F6B", "#FAFAFB") })
            {
                var inkBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(ink)!;
                var surfaceBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(surface)!;
                bool dark = surface == "#1B1B1F";
                var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = Brushes.Red };
                var status = new TextBlock { Text = "读取消息内容 · 正在接收 · 5 分钟前", FontSize = 11.5, FontFamily = panelFont, Foreground = inkBrush };
                var line = (FrameworkElement)Activator.CreateInstance(stepType, Program.Any, null, new object?[] { dot, status, 6.0, null }, null)!;
                var circle = new Border { Width = 18, Height = 18, Background = Brushes.Red };
                var number = new TextBlock { Text = "1", FontSize = 11, FontFamily = panelFont, Foreground = inkBrush };
                var stepText = new TextBlock { Text = "点击钉钉左上角的头像和名字，在弹出的菜单中选择“设置”。", FontSize = 12.5, FontFamily = panelFont, Foreground = inkBrush,
                    LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
                var step = (FrameworkElement)Activator.CreateInstance(stepType, Program.Any, null, new object?[] { circle, stepText, 10.0, number }, null)!;
                foreach (var (label, element) in new[] { ("dot", line), ("step", step) })
                {
                    var host = new Border { Background = surfaceBrush, Child = element, Width = 600, UseLayoutRounding = true };
                    TextOptions.SetTextFormattingMode(host, mode);
                    VisualTreeHelper.SetRootDpi(host, new DpiScale(pixels, pixels));
                    host.Measure(new Size(600, double.PositiveInfinity)); host.Arrange(new Rect(host.DesiredSize)); host.UpdateLayout();
                    int w = (int)Math.Ceiling(600 * pixels), h = (int)Math.Ceiling(host.ActualHeight * pixels);
                    var bitmap = new RenderTargetBitmap(w, h, 96 * pixels, 96 * pixels, PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    var bytes = new byte[w * h * 4]; bitmap.CopyPixels(bytes, w * 4, 0);
                    // Red is the mark, dark is the text (and the number over the mark): the middle of each, weighed by coverage.
                    double Middle(Func<byte, byte, byte, double> weight, int from, int to)
                    {
                        double sum = 0, total = 0;
                        for (int y = 0; y < h; y++) for (int x = from; x < Math.Min(w, to); x++)
                        { int i = (y * w + x) * 4; double v = weight(bytes[i], bytes[i + 1], bytes[i + 2]); sum += v * (y + .5); total += v; }
                        return total > 0 ? sum / total : double.NaN;
                    }
                    int markRight = (int)Math.Ceiling((label == "dot" ? 6 : 18) * pixels);
                    double mark = Middle((b, g, r) => r > 200 && g < 120 && b < 120 ? 1 : 0, 0, markRight);
                    // The text's ink: rows by their first and last dark pixel, as the eye reads a line's height.
                    int first = -1, last = -1;
                    double L(int i) => (0.2126 * bytes[i + 2] + 0.7152 * bytes[i + 1] + 0.0722 * bytes[i]) / 255;
                    double back = (0.2126 * surfaceBrush.Color.R + 0.7152 * surfaceBrush.Color.G + 0.0722 * surfaceBrush.Color.B) / 255;
                    double fore = (0.2126 * inkBrush.Color.R + 0.7152 * inkBrush.Color.G + 0.0722 * inkBrush.Color.B) / 255;
                    bool Letter(int i) => Math.Abs(L(i) - back) >= Math.Abs(fore - back) / 2 && !(bytes[i + 2] > 200 && bytes[i + 1] < 160);
                    for (int y = 0; y < h; y++) for (int x = markRight + 2; x < w; x++)
                    { int i = (y * w + x) * 4; if (Letter(i)) { if (first < 0) first = y; last = y; break; } }
                    double text = (first + last + 1) / 2.0;
                    int markTop = -1, markBottom = -1;
                    for (int y = 0; y < h; y++) for (int x = 0; x < markRight; x++)
                    { int i = (y * w + x) * 4; if (bytes[i + 2] > 200 && bytes[i + 1] < 160) { if (markTop < 0) markTop = y; markBottom = y; break; } }
                    string extra = "";
                    if (label == "step")
                    {
                        int top = -1, bottom = -1;
                        // The number is drawn over the red mark: its pixels are those at least halfway from the red to its color.
                        for (int y = markTop + 1; y < markBottom; y++) for (int x = markRight / 4; x < markRight * 3 / 4; x++)
                        {
                            int i = (y * w + x) * 4;
                            double Far(double r, double g, double b) => Math.Sqrt((r - 255) * (r - 255) + g * g + b * b);
                            if (Far(bytes[i + 2], bytes[i + 1], bytes[i]) >= Far(inkBrush.Color.R, inkBrush.Color.G, inkBrush.Color.B) / 2) { if (top < 0) top = y; bottom = y; break; }
                        }
                        extra = " number " + ((top + bottom + 1) / 2.0 - (markTop + markBottom + 1) / 2.0).ToString("0.00", CultureInfo.InvariantCulture);
                    }
                    Console.WriteLine($"{label} {pixels * 100:0}% {mode}{(dark ? " dark" : "")}: mark {mark:0.00} ({markTop}-{markBottom}) text {text:0.00} ({first}-{last}) off {(mark - text):0.00} px{extra}");
                    Write(bitmap, Path.Combine(output, $"steps-{label}-{pixels * 100:0}-{mode}{(dark ? "-dark" : "")}.png"));
                }
            }

        async Task Shot(string file, Window window)
        {
            const double scale = 1.5;
            var content = (FrameworkElement)window.Content; window.Content = null;
            double width = window.Width;
            var host = new Border { Child = content, Resources = window.Resources, Width = width };
            System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
            TextOptions.SetTextFormattingMode(host, TextFormattingMode.Ideal);
            host.Measure(new Size(width, double.PositiveInfinity)); host.Arrange(new Rect(host.DesiredSize));
            await Settle();
            host.Measure(new Size(width, double.PositiveInfinity)); host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)(width * scale), (int)Math.Ceiling(host.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(host); bitmap.Freeze();
            Write(bitmap, Path.Combine(output, file + ".png"));
            Console.WriteLine(file + " " + host.ActualHeight.ToString("0", CultureInfo.InvariantCulture));
            host.Child = null; window.Close();
        }
    }
}
