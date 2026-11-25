using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.Agents;

namespace BrimDeck;

// The collapsed row's stacks of round icons (the unread notices' applications, the coding agents' sessions): each icon round,
// in an outline of the surface's color so that the one on top stands apart from the one it covers, each next one further right
// and on top. The front icon of the agents carries the state of its session: a ring around it (turning while it works, slowly
// brightening and dimming while it waits for the user, still once it is done) and a badge at its upper right.
internal static class CompactStacks
{
    // An icon's diameter with its outline, at the row's scale.
    public static double Diameter(double s) => CompactMusicLayout.Whole(19 * s);
    private static double Outline(double s) => Math.Max(1, Math.Round(1.5 * s * 2) / 2);
    // How far each next icon stands from the one before it: they overlap by about 40%.
    private static double Step(double s) => Diameter(s) - CompactMusicLayout.Whole(7.5 * s);
    // The room the state's ring takes around the front icon.
    public static double RingPad(double s) => Math.Max(1.5, Math.Round(1.5 * s * 2) / 2);

    // The application's icon in a round outline. The picture is cut round: one with a plate of its own (most icons) fills the
    // circle, its transparent margin left out; a mark on a transparent ground (a logo with no plate) sits smaller on a plate of
    // the panel's fill, so that it reads on any theme. Without a picture, the name's first letter in the accent color.
    public static FrameworkElement Icon(BitmapSource? image, double size, double s, PanelLook look, string? name, Color accent)
    {
        double edge = Outline(s), inner = size - 2 * edge;
        var grid = Round(size, look);
        FrameworkElement face;
        if (image is null)
        {
            face = NoticeLetter.Plate(name, inner, accent);
            face.Clip = new EllipseGeometry(new Point(inner / 2, inner / 2), inner / 2, inner / 2);
        }
        else
        {
            var (box, plated, dark) = Shape(image);
            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            if (plated)
            {
                // A plate's rounded corners fall outside the circle: the middle of the plate shows, a little larger than the box.
                double cut = Math.Min(box.Width, box.Height) * .06;
                brush.Viewbox = new Rect(box.X + cut, box.Y + cut, Math.Max(.01, box.Width - 2 * cut), Math.Max(.01, box.Height - 2 * cut));
                face = new Ellipse { Width = inner, Height = inner, Fill = brush };
            }
            else
            {
                brush.Stretch = Stretch.Uniform; brush.Viewbox = box;
                // A dark mark (a logo drawn for a light taskbar) on a light plate, a light one on the panel's fill.
                var mark = new Grid { Width = inner, Height = inner };
                mark.Children.Add(new Ellipse { Fill = dark ? UI.Brush("#ECECF0") : UI.Brush(look.FillSelected) });
                double side = Math.Round(inner * .64);
                mark.Children.Add(new Rectangle { Width = side, Height = side, Fill = brush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                face = mark;
            }
        }
        face.HorizontalAlignment = HorizontalAlignment.Center; face.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(face);
        return grid;
    }

    // A round plate of the panel's fill in the same outline, holding a glyph (the bell, a phone).
    public static FrameworkElement Plate(double size, double s, PanelLook look, FrameworkElement glyph)
    {
        double inner = size - 2 * Outline(s);
        var grid = Round(size, look);
        grid.Children.Add(new Ellipse { Width = inner, Height = inner, Fill = UI.Brush(look.FillSelected), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(glyph);
        return grid;
    }

    // A call's mark: while it rings, a phone on a disc of the success color with a ring that keeps spreading out from it (animate
    // false: still); once it has stopped, a phone in the danger color on the panel's fill.
    public static FrameworkElement Call(double size, double s, PanelLook look, bool ringing, bool animate)
    {
        if (!ringing) return Plate(size, s, look, NoticeBadges.Glyph(Core.Notices.NoticeGlyph.Call, Math.Round(size * .5), look.ColorOf("danger")));
        var green = look.ColorOf("success");
        var grid = new Grid { Width = size, Height = size, IsHitTestVisible = false };
        var wave = new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(150, green.R, green.G, green.B)), StrokeThickness = Math.Max(1, s), Opacity = 0 };
        var spread = new ScaleTransform(1, 1, size / 2, size / 2);
        wave.RenderTransform = spread;
        grid.Children.Add(wave);
        grid.Children.Add(new Ellipse { Fill = new SolidColorBrush(green) });
        var phone = NoticeBadges.Glyph(Core.Notices.NoticeGlyph.Call, Math.Round(size * .55), Color.FromRgb(0x10, 0x10, 0x12));
        phone.HorizontalAlignment = HorizontalAlignment.Center; phone.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(phone);
        if (animate)
            new Storyboarded(grid, () =>
            {
                var time = TimeSpan.FromSeconds(1.6);
                var grow = Paced(new DoubleAnimation(.9, 1.45, time) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
                spread.BeginAnimation(ScaleTransform.ScaleXProperty, grow); spread.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());
                wave.BeginAnimation(UIElement.OpacityProperty, Paced(new DoubleAnimation(1, 0, time) { RepeatBehavior = RepeatBehavior.Forever }));
            }, () =>
            {
                spread.BeginAnimation(ScaleTransform.ScaleXProperty, null); spread.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                wave.BeginAnimation(UIElement.OpacityProperty, null);
            }).Attach();
        return grid;
    }

    private static Grid Round(double size, PanelLook look)
    {
        var grid = new Grid { Width = size, Height = size, IsHitTestVisible = false };
        grid.Children.Add(new Ellipse { Fill = UI.Brush(look.Hex("surface")) });
        return grid;
    }

    // The icons from left to right, each next one on top. The last may stand in a state's ring (pad: the ring's room around it),
    // which leaves the icons' spacing as it is.
    public static FrameworkElement Stack(IReadOnlyList<FrameworkElement> icons, double s, double frontPad = 0)
    {
        double size = Diameter(s), step = Step(s);
        double pad = icons.Count == 1 ? frontPad : 0;
        double width = pad + (icons.Count - 1) * step + size + frontPad, height = size + 2 * frontPad;
        var canvas = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
        for (int i = 0; i < icons.Count; i++)
        {
            var icon = icons[i];
            bool front = i == icons.Count - 1;
            double left = pad + i * step - (front ? frontPad : 0);
            Canvas.SetLeft(icon, left); Canvas.SetTop(icon, front ? 0 : frontPad);
            canvas.Children.Add(icon);
        }
        return canvas;
    }

    // The row's words beside a stack, as the other parts' (the file transfer's count): 11.5 at scale 1, semibold.
    public static TextBlock Words(string text, double s, string color) => new()
    {
        Text = text, FontFamily = UI.PanelFont, FontSize = CompactMusicLayout.Half(11.5 * s), FontWeight = FontWeights.SemiBold,
        Foreground = UI.Brush(color), TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center
    };

    // A stack and the words after it, as one fixed part: it starts and ends on whole device pixels (see TransferBadge). reserve:
    // the words' widest form, whose width the part keeps while the words change within it (a running time).
    public static Grid Row(FrameworkElement stack, TextBlock? words, double s, double dpi, string? reserve = null)
    {
        stack.HorizontalAlignment = HorizontalAlignment.Left; stack.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { Height = Math.Max(stack.Height, CompactMusicLayout.Whole(16 * s)), Background = Brushes.Transparent };
        row.Children.Add(stack);
        double width = stack.Width;
        if (words is not null)
        {
            double wordsWidth = Measure(words);
            if (reserve is not null) { var widest = Words(reserve, s, "#000000"); Typography.SetNumeralAlignment(widest, FontNumeralAlignment.Tabular); wordsWidth = Math.Max(wordsWidth, Measure(widest)); }
            double start = DevicePixels.Up(stack.Width + CompactMusicLayout.Whole(5 * s), dpi);
            words.HorizontalAlignment = HorizontalAlignment.Left; words.Margin = new Thickness(start, 0, 0, 0);
            row.Children.Add(words);
            width = start + wordsWidth;
        }
        row.Width = DevicePixels.Up(width, dpi);
        return row;
    }

    private static double Measure(TextBlock words)
    {
        words.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return words.DesiredSize.Width;
    }

    // The front icon of the agents in its state's ring, with its badge. animate false leaves everything still.
    public static FrameworkElement State(FrameworkElement icon, AgentState state, double s, PanelLook look, bool animate)
    {
        double size = Diameter(s), pad = RingPad(s), whole = size + 2 * pad, stroke = Math.Max(1, Math.Round(1.25 * s * 2) / 2);
        var grid = new Grid { Width = whole, Height = whole, IsHitTestVisible = false };
        bool waits = state is AgentState.Approval or AgentState.Question;
        var color = look.ColorOf(waits ? "caution" : state == AgentState.Working ? "accent" : "success");
        var brush = new SolidColorBrush(color);
        var ring = new Ellipse { Width = whole, Height = whole, StrokeThickness = stroke };
        Storyboarded? motion = null;
        if (state == AgentState.Working)
        {
            // A quarter and a little more of the ring in the accent, over the whole ring faint, turning once a second.
            ring.Stroke = new SolidColorBrush(Color.FromArgb(56, color.R, color.G, color.B));
            grid.Children.Add(ring);
            double r = (whole - stroke) / 2, c = whole / 2;
            var arc = new PathGeometry([new PathFigure(new Point(c, c - r), [new ArcSegment(new Point(c + r, c), new Size(r, r), 0, false, SweepDirection.Clockwise, true)], false)]);
            var turn = new RotateTransform(0, c, c);
            var spinner = new Path { Data = arc, Stroke = brush, StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = turn };
            grid.Children.Add(spinner);
            if (animate) motion = new(spinner, () => turn.BeginAnimation(RotateTransform.AngleProperty, Paced(new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever })),
                () => turn.BeginAnimation(RotateTransform.AngleProperty, null));
        }
        else
        {
            ring.Stroke = brush;
            grid.Children.Add(ring);
        }
        icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        if (state != AgentState.Working)
        {
            var badge = Badge(state, s, look, color);
            grid.Children.Add(badge);
            if (waits && animate)
            {
                // The ring brightens and dims; every 2.4 s the icon gives a short shake and the badge a small pop.
                var nudge = new RotateTransform(0, size / 2, size / 2);
                icon.RenderTransform = nudge;
                var pop = new ScaleTransform(1, 1, badge.Width / 2, badge.Height / 2);
                badge.RenderTransform = pop;
                motion = new(grid, () =>
                {
                    ring.BeginAnimation(UIElement.OpacityProperty, Paced(new DoubleAnimation(1, .4, TimeSpan.FromSeconds(.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() }));
                    var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.4), RepeatBehavior = RepeatBehavior.Forever };
                    foreach (var (at, angle) in new[] { (0.0, 0.0), (1.68, 0.0), (1.82, -10.0), (1.97, 9.0), (2.11, -5.0), (2.26, 0.0) })
                        shake.KeyFrames.Add(new EasingDoubleKeyFrame(angle, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at)), new SineEase()));
                    nudge.BeginAnimation(RotateTransform.AngleProperty, Paced(shake));
                    var grow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.4), RepeatBehavior = RepeatBehavior.Forever };
                    foreach (var (at, scale) in new[] { (0.0, 1.0), (1.68, 1.0), (1.87, 1.25), (2.06, 1.0) })
                        grow.KeyFrames.Add(new EasingDoubleKeyFrame(scale, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at)), new SineEase()));
                    pop.BeginAnimation(ScaleTransform.ScaleXProperty, Paced(grow));
                    pop.BeginAnimation(ScaleTransform.ScaleYProperty, Paced(grow.Clone()));
                }, () =>
                {
                    ring.BeginAnimation(UIElement.OpacityProperty, null);
                    nudge.BeginAnimation(RotateTransform.AngleProperty, null);
                    pop.BeginAnimation(ScaleTransform.ScaleXProperty, null); pop.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                });
            }
        }
        motion?.Attach();
        return grid;
    }

    // The badge at the icon's upper right: "!" for an approval, "?" for a question, a tick once done; dark on the state's color,
    // in an outline of the surface's color.
    private static FrameworkElement Badge(AgentState state, double s, PanelLook look, Color color)
    {
        double size = CompactMusicLayout.Whole(9.5 * s), edge = Math.Max(1, Math.Round(s * 2) / 2), inner = size - 2 * edge;
        var badge = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -edge, -edge, 0) };
        badge.Children.Add(new Ellipse { Fill = UI.Brush(look.Hex("surface")) });
        badge.Children.Add(new Ellipse { Width = inner, Height = inner, Fill = new SolidColorBrush(color) });
        var ink = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12));
        // Drawn on a 10-unit square, so each mark sits in the middle of the badge whatever the font.
        string data = state switch
        {
            AgentState.Approval => "M 5,2.2 V 5.6 M 5,7.6 V 7.65",
            AgentState.Question => "M 3.4,3.6 Q 3.5,2 5,2 Q 6.6,2 6.6,3.5 Q 6.6,4.5 5.6,5 Q 5,5.3 5,6 M 5,7.7 V 7.75",
            _ => "M 3,5.2 L 4.4,6.6 L 7.1,3.7"
        };
        badge.Children.Add(new Path
        {
            Data = Geometry.Parse(data), Stroke = ink, StrokeThickness = 1.55, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Width = 10, Height = 10, Stretch = Stretch.None, LayoutTransform = new ScaleTransform(inner / 10, inner / 10),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        return badge;
    }

    // The ring's and the badge's motion at 30 frames a second: it is small, and it runs as long as a session works.
    private static T Paced<T>(T timeline) where T : Timeline { Timeline.SetDesiredFrameRate(timeline, 30); return timeline; }

    // A motion that runs only while its element is on screen: shown, it starts; hidden or taken out of the window, it stops, so
    // that a row built again (or hidden under the open panel) leaves no clock running.
    private sealed class Storyboarded(FrameworkElement element, Action start, Action stop)
    {
        private bool _running;
        public void Attach()
        {
            element.IsVisibleChanged += (_, _) => Update();
            element.Loaded += (_, _) => Update();
            element.Unloaded += (_, _) => Update();
        }
        private void Update()
        {
            bool run = element.IsVisible && element.IsLoaded;
            if (run == _running) return;
            _running = run;
            if (run) start(); else stop();
        }
    }

    // Where the picture's opaque part lies (relative to its size), whether it is a plate or a mark on a transparent ground, and
    // whether such a mark is dark. A plate covers most of its box (a rounded square 90% and more, a circle 79%); a mark, drawn
    // in lines, much less. Read once per picture.
    private static readonly ConditionalWeakTable<BitmapSource, Tuple<Rect, bool, bool>> s_shapes = new();
    private static (Rect Box, bool Plated, bool Dark) Shape(BitmapSource image)
    {
        if (s_shapes.TryGetValue(image, out var known)) return (known.Item1, known.Item2, known.Item3);
        var result = Tuple.Create(new Rect(0, 0, 1, 1), true, false);
        try
        {
            var source = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
            if (w > 0 && h > 0 && w <= 1024 && h <= 1024)
            {
                var pixels = new byte[stride * h];
                source.CopyPixels(pixels, stride, 0);
                int left = w, top = h, right = -1, bottom = -1, opaque = 0;
                double light = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int at = y * stride + x * 4, alpha = pixels[at + 3];
                        if (alpha <= 24) continue;
                        left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                        if (alpha < 160) continue;
                        // Premultiplied: the color is the stored value over alpha.
                        opaque++;
                        light += (.2126 * pixels[at + 2] + .7152 * pixels[at + 1] + .0722 * pixels[at]) / alpha;
                    }
                if (right >= left && bottom >= top)
                {
                    int bw = right - left + 1, bh = bottom - top + 1;
                    bool plated = opaque >= .7 * bw * bh;
                    bool dark = opaque > 0 && light / opaque < .4;
                    result = Tuple.Create(new Rect((double)left / w, (double)top / h, (double)bw / w, (double)bh / h), plated, dark);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException) { }
        s_shapes.AddOrUpdate(image, result);
        return (result.Item1, result.Item2, result.Item3);
    }
}
