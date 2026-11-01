using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Xml;
using System.Xml.Linq;
using System.Runtime.CompilerServices;
using DropShelf;
using BrimDeck.Core;
using Path = System.Windows.Shapes.Path;

namespace BrimDeck;

// Both surfaces use these inert previews. No browser, rich-document loader, remote URL or file execution is involved.
internal static class ClipboardPreview
{
    private sealed class Identity { internal readonly string Key = Guid.NewGuid().ToString("N"); }
    private static readonly ConditionalWeakTable<ClipboardContent, Identity> Identities = new();
    internal static int DecodeCount { get; private set; }
    internal static FrameworkElement Create(ClipboardContent content, bool detailed = false, ModuleMode mode = ModuleMode.Full, bool deferred = false)
    {
        if (content.Kind is ClipboardContentKind.Image or ClipboardContentKind.Screenshot)
        {
            var policy = ModuleRuntimePolicy.For(mode);
            int cap = detailed ? policy.PreviewPixels : policy.CardPixels;
            var image = new Image { Stretch = detailed ? Stretch.Uniform : Stretch.UniformToFill };
            string key = "clipboard:" + Identities.GetValue(content, _ => new Identity()).Key + ":" + cap;
            if (!deferred) { image.Source = Decode(content, key, cap); return image; }
            CancellationTokenSource? pending = null;
            async void Load()
            {
                if (!image.IsVisible || pending is not null) return;
                var request = pending = new CancellationTokenSource();
                try
                {
                    double dpi = VisualTreeHelper.GetDpi(image).PixelsPerDip;
                    double extent = Math.Max(image.ActualWidth, image.ActualHeight) * dpi;
                    int pixels = extent > 0 ? Math.Clamp((int)Math.Ceiling(extent), 1, cap) : cap;
                    string sizedKey = key + ":" + pixels;
                    var bitmap = DecodedImageCache.Get(sizedKey) ?? await HostedResources.ImageWorkAsync(() => Decode(content, sizedKey, pixels), request.Token);
                    await image.Dispatcher.InvokeAsync(() =>
                    { if (!request.IsCancellationRequested && image.IsVisible) image.Source = bitmap; });
                }
                catch (OperationCanceledException) { }
            }
            void Release() { pending?.Cancel(); pending?.Dispose(); pending = null; image.Source = null; }
            image.Loaded += (_, _) => Load();
            image.IsVisibleChanged += (_, _) => { if (image.IsVisible) Load(); else Release(); };
            image.Unloaded += (_, _) => Release();
            return image;
        }
        if (content.Kind == ClipboardContentKind.Color)
        {
            var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var colors = ClipboardColors.Swatches(content.Text);
            if (colors.Count > 0)
            {
                Brush brush = colors.Count == 1 ? ColorBrush(colors[0]) : new LinearGradientBrush(new GradientStopCollection(colors.Select((color, i) => new GradientStop(((SolidColorBrush)ColorBrush(color)).Color, i / (double)(colors.Count - 1)))), 0);
                panel.Children.Add(new Border { Background = brush, MinHeight = 12, Margin = new Thickness(8, 8, 8, 4) });
            }
            var label = Text(content.Text); label.Margin = new Thickness(8, 0, 8, 5); Grid.SetRow(label, 1); panel.Children.Add(label); return panel;
        }
        if (content.Kind == ClipboardContentKind.Files) return IslandFilePreview.Deck(content.Files);
        if (content.Kind == ClipboardContentKind.Link) return Link(content.Text);
        if (mode != ModuleMode.Performance || detailed)
            if (content.Kind == ClipboardContentKind.Svg && Svg(content.Text) is { } svg) return svg;
        string preview = content.Text.Length > 0 ? content.Text : content.Preview;
        int limit = detailed ? 24000 : 1200;
        preview = preview[..Math.Min(preview.Length, limit)];
        var text = Text(preview); text.Margin = new Thickness(8); text.ClipToBounds = true;
        if (content.Kind == ClipboardContentKind.Code || content.Kind == ClipboardContentKind.Svg)
        {
            text.FontFamily = new FontFamily("Cascadia Mono, Consolas"); text.FontSize = detailed ? 12 : 10.5;
            if (mode != ModuleMode.Performance) Highlight(text, preview);
        }
        if (detailed) return new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        return text;
    }
    private static BitmapSource? Decode(ClipboardContent content, string key, int cap)
    {
        if (DecodedImageCache.Get(key) is { } cached) return cached;
        try
        {
            using var source = content.OpenImage();
            var frame = BitmapDecoder.Create(source, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            using var input = content.OpenImage();
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // Constrain the longest edge, including portrait sources, and never upscale.
            if (Math.Max(width, height) > cap) { if (width >= height) bitmap.DecodePixelWidth = cap; else bitmap.DecodePixelHeight = cap; }
            bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze(); DecodeCount++;
            return DecodedImageCache.Add(key, bitmap);
        }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException or InvalidOperationException) { return null; }
    }
    // LinkPreviewCard's three layers: site/kind, distinct title, then the readable URL.
    private static FrameworkElement Link(string raw)
    {
        var info = EdgeDropPresentation.Link(raw);
        string Service(string text) => text switch { "This computer" => Loc.T("本机", text), "Local network" => Loc.T("本地网络", text), _ => text };
        string subtitle = info.Subtitle switch { "Repository" => Loc.T("仓库", info.Subtitle), "Issue" => Loc.T("议题", info.Subtitle), "Pull request" => Loc.T("合并请求", info.Subtitle), "Discussion" => Loc.T("讨论", info.Subtitle), "File" => Loc.T("文件", info.Subtitle), "Folder" => Loc.T("文件夹", info.Subtitle), "Profile" => Loc.T("个人主页", info.Subtitle), "Search" => Loc.T("搜索", info.Subtitle), "Video" => Loc.T("视频", info.Subtitle), "Short" => Loc.T("短视频", info.Subtitle), "Short link" => Loc.T("短链接", info.Subtitle), _ => info.Subtitle.StartsWith("Port ", StringComparison.Ordinal) ? Loc.T("端口 " + info.Subtitle[5..], info.Subtitle) : info.Subtitle };
        var panel = new Grid { Margin = new Thickness(12, 12, 12, 6), ClipToBounds = true };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new()); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = Text("↗  " + Service(info.Service) + (subtitle.Length > 0 ? " · " + subtitle : ""), 10.5, "PanelTertiary");
        top.TextWrapping = TextWrapping.NoWrap; panel.Children.Add(top);
        bool title = info.Title.Length > 0 && !string.Equals(info.Title, info.DisplayUrl, StringComparison.OrdinalIgnoreCase) && !string.Equals(info.Title, info.Service, StringComparison.OrdinalIgnoreCase);
        var body = Text(title ? info.Title : info.DisplayUrl, 12.5); body.FontWeight = FontWeights.Medium; body.Margin = new Thickness(0, 5, 0, 0); Grid.SetRow(body, 1); panel.Children.Add(body);
        if (title) { var url = Text(info.DisplayUrl, 10.5, "PanelTertiary"); url.TextWrapping = TextWrapping.NoWrap; url.Margin = new Thickness(0, 4, 0, 0); Grid.SetRow(url, 2); panel.Children.Add(url); }
        return panel;
    }
    internal static TextBlock Text(string text, double size = 12, string token = "PanelPrimary")
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
        block.SetResourceReference(TextBlock.ForegroundProperty, token); return block;
    }
    private static readonly Regex Tokens = new("(?<comment>//[^\\r\\n]*|#[^\\r\\n]*)|(?<string>\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*')|(?<number>\\b\\d+(?:\\.\\d+)?\\b)|(?<keyword>\\b(?:true|false|null|const|let|var|return|if|else|class|public|private|async|await|def|import|from|SELECT|FROM|WHERE|ERROR|Exception)\\b)", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static void Highlight(TextBlock block, string text)
    {
        block.Inlines.Clear(); int at = 0;
        try
        {
            foreach (Match token in Tokens.Matches(text))
            {
                if (token.Index > at) block.Inlines.Add(new Run(text[at..token.Index]));
                var run = new Run(token.Value); run.SetResourceReference(TextElement.ForegroundProperty, token.Groups["comment"].Success ? "PanelTertiary" : token.Groups["string"].Success ? "PanelSecondary" : "PanelAccent");
                block.Inlines.Add(run); at = token.Index + token.Length;
            }
            if (at < text.Length) block.Inlines.Add(new Run(text[at..]));
        }
        catch (RegexMatchTimeoutException) { block.Inlines.Clear(); block.Inlines.Add(new Run(text)); }
    }
    private static Brush ColorBrush(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    private static FrameworkElement? Svg(string text)
    {
        try
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
            var root = XDocument.Load(reader).Root;
            if (root is null || root.Descendants().Take(2049).Count() > 2048) return null;
            // A deliberately small static vector subset. Scripts, linked images, use, filters, fonts and external references
            // are never loaded; unsupported drawings fall back to their source rather than a misleading partial image.
            var allowed = new HashSet<string> { "g", "path", "rect", "circle", "ellipse", "line", "polygon", "polyline", "title", "desc" };
            if (root.DescendantsAndSelf().Any(node => node != root && !allowed.Contains(node.Name.LocalName)
                || node.Name.NamespaceName is not ("" or "http://www.w3.org/2000/svg")
                || node.Attributes().Any(attribute => attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) || attribute.Name.LocalName is "href" or "style" or "transform"))) return null;
            double Number(XElement node, string key, double fallback = 0) => double.TryParse((string?)node.Attribute(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? Math.Clamp(n, -100000, 100000) : fallback;
            double width = Number(root, "width", 100), height = Number(root, "height", 100), x = 0, y = 0;
            if (root.Attribute("viewBox") is { } box)
            {
                var values = box.Value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                if (values.Length != 4 || values.Any(value => !double.IsFinite(value))) return null;
                (x, y, width, height) = (values[0], values[1], values[2], values[3]);
            }
            if (width <= 0 || height <= 0 || width > 100000 || height > 100000) return null;
            var canvas = new Canvas { Width = width, Height = height, ClipToBounds = true };
            foreach (var node in root.Descendants())
            {
                if (node.Name.LocalName is "title" or "desc" or "g") continue;
                Shape? shape = node.Name.LocalName switch
                {
                    "path" when ((string?)node.Attribute("d")) is { Length: <= 24000 } d => new Path { Data = Geometry.Parse(d) },
                    "rect" => new Rectangle { Width = Math.Max(0, Number(node, "width")), Height = Math.Max(0, Number(node, "height")), RadiusX = Number(node, "rx"), RadiusY = Number(node, "ry") },
                    "circle" => new Ellipse { Width = Math.Max(0, Number(node, "r") * 2), Height = Math.Max(0, Number(node, "r") * 2) },
                    "ellipse" => new Ellipse { Width = Math.Max(0, Number(node, "rx") * 2), Height = Math.Max(0, Number(node, "ry") * 2) },
                    "line" => new Line { X1 = Number(node, "x1"), X2 = Number(node, "x2"), Y1 = Number(node, "y1"), Y2 = Number(node, "y2") },
                    "polygon" => new Polygon { Points = PointCollection.Parse((string?)node.Attribute("points") ?? "") },
                    "polyline" => new Polyline { Points = PointCollection.Parse((string?)node.Attribute("points") ?? "") }, _ => null
                };
                if (shape is null) return null;
                string Attribute(string key, string fallback) => node.AncestorsAndSelf().Select(element => (string?)element.Attribute(key)).FirstOrDefault(value => value is not null) ?? fallback;
                void Paint(DependencyProperty property, string value)
                {
                    if (value == "none") shape.SetValue(property, null);
                    else if (value == "currentColor") shape.SetResourceReference(property, "PanelPrimary");
                    else if (ClipboardColors.Swatches(value) is { Count: 1 } colors) shape.SetValue(property, ColorBrush(colors[0]));
                    else shape.SetValue(property, new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)));
                }
                Paint(Shape.FillProperty, Attribute("fill", "currentColor")); Paint(Shape.StrokeProperty, Attribute("stroke", "none"));
                shape.StrokeThickness = Math.Max(0, Number(node, "stroke-width", 1));
                double left = node.Name.LocalName is "circle" or "ellipse" ? Number(node, "cx") - shape.Width / 2 : Number(node, "x");
                double top = node.Name.LocalName is "circle" or "ellipse" ? Number(node, "cy") - shape.Height / 2 : Number(node, "y");
                Canvas.SetLeft(shape, left - x); Canvas.SetTop(shape, top - y); canvas.Children.Add(shape);
            }
            return new Viewbox { Child = canvas, Stretch = Stretch.Uniform, Margin = new Thickness(10) };
        }
        catch (Exception error) when (error is XmlException or FormatException or ArgumentException or OverflowException) { return null; }
    }
}
