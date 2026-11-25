using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public sealed class IslandBorder : Border
{
    // A faint inner rim keeps the outline readable over wallpapers of the surface's own tone; the pen is twice the rim width
    // because the layout clip removes the outer half of the stroke.
    private static readonly Brush DefaultRim = CreateRim();
    public static readonly DependencyProperty ShoulderRadiusProperty = DependencyProperty.Register(
        nameof(ShoulderRadius), typeof(double), typeof(IslandBorder),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty MaterialOpacityProperty = DependencyProperty.Register(
        nameof(MaterialOpacity), typeof(double), typeof(IslandBorder),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    // The surface's own opacity (DeckSettings.SurfaceTransparency): the base color and the theme's layers are drawn this opaque,
    // the rim and the content as they are.
    public static readonly DependencyProperty SurfaceOpacityProperty = DependencyProperty.Register(
        nameof(SurfaceOpacity), typeof(double), typeof(IslandBorder),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    // Opacity of the rim along the top edge (y = 0), from 0 (omitted for a surface docked to a screen edge) to 1 (full rim for
    // a floating surface). NaN (the default) derives 0 or 1 from the shape itself (IsTopDocked), so static previews and tests
    // need not set it while animated transitions in MainWindow can hold it at 0 or fade it cleanly before touching the edge.
    public static readonly DependencyProperty TopRimOpacityProperty = DependencyProperty.Register(
        nameof(TopRimOpacity), typeof(double), typeof(IslandBorder),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    // The theme's layers over the base color: a gradient and a pattern (scan lines, or a pixel theme's texture). Like the rim
    // they fade with MaterialOpacity, so the indicator, which has neither, keeps its plain color.
    public static readonly DependencyProperty GradientProperty = DependencyProperty.Register(
        nameof(Gradient), typeof(Brush), typeof(IslandBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PatternProperty = DependencyProperty.Register(
        nameof(Pattern), typeof(Brush), typeof(IslandBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    // The theme's motifs (IslandDecor), between the gradient and the pattern, fading with the material like them.
    public static readonly DependencyProperty DecorProperty = DependencyProperty.Register(
        nameof(Decor), typeof(IslandDecor), typeof(IslandBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RimBrushProperty = DependencyProperty.Register(
        nameof(RimBrush), typeof(Brush), typeof(IslandBorder), new FrameworkPropertyMetadata(DefaultRim, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((IslandBorder)d)._rim = null));
    // A new width also rebuilds a pixel theme's bevel, which is made with the outline.
    public static readonly DependencyProperty RimWidthProperty = DependencyProperty.Register(
        nameof(RimWidth), typeof(double), typeof(IslandBorder), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => { var island = (IslandBorder)d; island._rim = null; island._outlineKey = null; }));
    // Draws the outline this far inside the element's bounds, for the copy that casts the island's shadow. The shadow effect
    // also draws the copy itself; lying exactly under the island, its edge would double the coverage of the island's
    // antialiased edge pixels and leave a stepped edge. A shape with shoulders reaches the screen edge at its top, where the
    // shoulders flare out, so it keeps its top edge and moves in at the sides and the bottom only.
    public static readonly DependencyProperty InsetProperty = DependencyProperty.Register(
        nameof(Inset), typeof(double), typeof(IslandBorder), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    // Pixel art (a theme's "pixel" effect): the outline's curves become steps of this many DIP, rounded to whole device pixels,
    // and with a BevelBrush the rim becomes a bevel: RimBrush lights the edges that face up and left, BevelBrush shades the
    // ones that face down and right. 0 keeps the smooth outline.
    public static readonly DependencyProperty PixelProperty = DependencyProperty.Register(
        nameof(Pixel), typeof(double), typeof(IslandBorder), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty BevelBrushProperty = DependencyProperty.Register(
        nameof(BevelBrush), typeof(Brush), typeof(IslandBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private Pen? _rim;
    private (Size Size, double Shoulder, CornerRadius Corners, double Inset, double Step, double Rim)? _outlineKey;
    private Geometry _outline = Geometry.Empty;
    private Geometry _rimOutline = Geometry.Empty;
    private Geometry _topEdge = Geometry.Empty;
    private (Geometry Light, Geometry TopLight, Geometry Shade)? _bevel;

    static IslandBorder() => CornerRadiusProperty.OverrideMetadata(typeof(IslandBorder),
        new FrameworkPropertyMetadata(default(CornerRadius), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double ShoulderRadius
    {
        get => (double)GetValue(ShoulderRadiusProperty);
        set => SetValue(ShoulderRadiusProperty, value);
    }

    public double MaterialOpacity
    {
        get => (double)GetValue(MaterialOpacityProperty);
        set => SetValue(MaterialOpacityProperty, value);
    }
    public double SurfaceOpacity { get => (double)GetValue(SurfaceOpacityProperty); set => SetValue(SurfaceOpacityProperty, value); }
    public double TopRimOpacity { get => (double)GetValue(TopRimOpacityProperty); set => SetValue(TopRimOpacityProperty, value); }
    public Brush? Gradient { get => (Brush?)GetValue(GradientProperty); set => SetValue(GradientProperty, value); }
    public Brush? Pattern { get => (Brush?)GetValue(PatternProperty); set => SetValue(PatternProperty, value); }
    internal IslandDecor? Decor { get => (IslandDecor?)GetValue(DecorProperty); set => SetValue(DecorProperty, value); }
    public Brush RimBrush { get => (Brush)GetValue(RimBrushProperty); set => SetValue(RimBrushProperty, value); }
    public double RimWidth { get => (double)GetValue(RimWidthProperty); set => SetValue(RimWidthProperty, value); }
    public double Inset { get => (double)GetValue(InsetProperty); set => SetValue(InsetProperty, value); }
    public double Pixel { get => (double)GetValue(PixelProperty); set => SetValue(PixelProperty, value); }
    public Brush? BevelBrush { get => (Brush?)GetValue(BevelBrushProperty); set => SetValue(BevelBrushProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var outline = Outline();
        double material = Math.Clamp(MaterialOpacity, 0, 1), surface = Math.Clamp(SurfaceOpacity, 0, 1);
        // Only a fading material needs the opacity layer; fully shown, the layers draw straight onto the surface.
        bool fading = material < 1;
        if (surface >= 1)
        {
            drawingContext.DrawGeometry(Background, null, outline);
            if (material <= 0) return;
            if (fading) drawingContext.PushOpacity(material);
            Layers(drawingContext, outline);
            Rim(drawingContext, outline);
            if (fading) drawingContext.Pop();
            return;
        }
        // See-through: the base color and the layers together take the surface's opacity, and the rim keeps its own strength so
        // the outline stays readable.
        drawingContext.PushOpacity(surface);
        drawingContext.DrawGeometry(Background, null, outline);
        if (material > 0)
        {
            if (fading) drawingContext.PushOpacity(material);
            Layers(drawingContext, outline);
            if (fading) drawingContext.Pop();
        }
        drawingContext.Pop();
        if (material <= 0) return;
        if (fading) drawingContext.PushOpacity(material);
        Rim(drawingContext, outline);
        if (fading) drawingContext.Pop();
    }

    private void Layers(DrawingContext drawingContext, Geometry outline)
    {
        if (Gradient is { } gradient) drawingContext.DrawGeometry(gradient, null, outline);
        Decor?.Draw(drawingContext, RenderSize, outline);
        if (Pattern is { } pattern) drawingContext.DrawGeometry(pattern, null, outline);
    }

    private double EffectiveTopRim()
    {
        double value = TopRimOpacity;
        return double.IsNaN(value) ? (IsTopDocked(ShoulderRadius, CornerRadius) ? 0d : 1d) : Math.Clamp(value, 0, 1);
    }

    private void Rim(DrawingContext drawingContext, Geometry outline)
    {
        if (RimWidth <= 0) return;
        double topRim = EffectiveTopRim();
        if (_bevel is { } bevel && BevelBrush is { } shade)
        {
            drawingContext.DrawGeometry(RimBrush, null, bevel.Light);
            if (topRim > 0 && !bevel.TopLight.IsEmpty())
            {
                if (topRim < 1) drawingContext.PushOpacity(topRim);
                drawingContext.DrawGeometry(RimBrush, null, bevel.TopLight);
                if (topRim < 1) drawingContext.Pop();
            }
            drawingContext.DrawGeometry(shade, null, bevel.Shade);
        }
        else if (_settledClip)
        {
            if (topRim >= 1) drawingContext.DrawGeometry(RimBrush, null, Ring(outline, outline, openTop: false));
            else
            {
                drawingContext.DrawGeometry(RimBrush, null, Ring(outline, _rimOutline, openTop: true));
                if (topRim > 0 && !_topEdge.IsEmpty())
                {
                    drawingContext.PushOpacity(topRim);
                    drawingContext.DrawGeometry(RimBrush, null, TopRing(outline, _topEdge));
                    drawingContext.Pop();
                }
            }
        }
        else
        {
            if (_rim is null) { _rim = new Pen(RimBrush, RimWidth * 2); _rim.Freeze(); }
            if (topRim >= 1) drawingContext.DrawGeometry(null, _rim, outline);
            else
            {
                drawingContext.DrawGeometry(null, _rim, _rimOutline);
                if (topRim > 0 && !_topEdge.IsEmpty())
                {
                    drawingContext.PushOpacity(topRim);
                    drawingContext.DrawGeometry(null, _rim, _topEdge);
                    drawingContext.Pop();
                }
            }
        }
    }

    // The visible surface owns input; its decorative shadow is a separate, non-interactive sibling.
    // At rest collapsed (SettledClip, set by the window), the content keeps clear of the corners and shoulders and is clipped to
    // the bounds instead: a rounded clip made each frame of the scrolling title and the bars cost about a quarter more. The surface
    // itself is still drawn as its outline (OnRender).
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => _settledClip ? Bounds() : Outline();
    private bool _settledClip;
    public bool SettledClip
    {
        get => _settledClip;
        set { if (_settledClip == value) return; _settledClip = value; InvalidateArrange(); InvalidateVisual(); }
    }
    // The rim's inner half as a filled ring, for the outline it was made from; made only at rest, so never on an animation's frames.
    private Geometry? _ring, _ringOutline;
    private double _ringWidth;
    private bool _ringOpenTop;
    private Geometry Ring(Geometry outline, Geometry rimOutline, bool openTop)
    {
        if (_ring is not null && ReferenceEquals(_ringOutline, outline) && _ringWidth == RimWidth && _ringOpenTop == openTop) return _ring;
        var widened = rimOutline.GetWidenedPathGeometry(new Pen(Brushes.Black, RimWidth * 2));
        var ring = Geometry.Combine(outline, widened, GeometryCombineMode.Intersect, null);
        ring.Freeze();
        _ring = ring; _ringOutline = outline; _ringWidth = RimWidth; _ringOpenTop = openTop;
        return ring;
    }
    private Geometry TopRing(Geometry outline, Geometry topEdge)
    {
        var widened = topEdge.GetWidenedPathGeometry(new Pen(Brushes.Black, RimWidth * 2));
        var ring = Geometry.Combine(outline, widened, GeometryCombineMode.Intersect, null);
        ring.Freeze();
        return ring;
    }
    private RectangleGeometry? _bounds;
    private Geometry Bounds()
    {
        var rect = new Rect(RenderSize);
        if (_bounds?.Rect != rect) { _bounds = new RectangleGeometry(rect); _bounds.Freeze(); }
        return _bounds;
    }

    // A pixel theme's steps are whole device pixels, so a new scale needs a new outline.
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (Pixel > 0) { _outlineKey = null; InvalidateArrange(); InvalidateVisual(); }
    }

    private static Brush CreateRim()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        brush.Freeze();
        return brush;
    }

    private Geometry Outline()
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip, step = PixelStairs.Step(Pixel, dpi);
        var key = (RenderSize, ShoulderRadius, CornerRadius, Inset, step, RimWidth);
        if (_outlineKey == key) return _outline;
        _outlineKey = key;
        // Stepped, the inset (the shadow caster's) is whole device pixels too, so its steps stay on them at 125 % or 175 %.
        double inset = step > 0 ? Math.Round(Inset * dpi, MidpointRounding.AwayFromZero) / dpi : Inset;
        (_outline, _rimOutline, _topEdge, _bevel) = Shape(RenderSize.Width, RenderSize.Height, ShoulderRadius, CornerRadius, inset, step, RimWidth, dpi);
        return _outline;
    }

    // A shape with shoulders (the notch) or square top corners (an expanded panel docked to the edge) hangs flush from the
    // screen edge at y = 0 in the edge's frame (EdgeFrame maps every docked edge there), so the rim omits that edge.
    private static bool IsTopDocked(double shoulder, CornerRadius corners)
        => shoulder > 0 || (corners.TopLeft <= 0 && corners.TopRight <= 0);

    // The outline, stepped for a pixel theme, its open-top rim path and separate top edge (so TopRimOpacity can fade or omit the
    // edge at y = 0 cleanly without rebuilding geometry), and its bevel split into sides/interior light, top light and shade.
    private static (Geometry Outline, Geometry RimOutline, Geometry TopEdge, (Geometry Light, Geometry TopLight, Geometry Shade)? Bevel) Shape(double w, double h, double shoulder, CornerRadius corners, double inset, double step, double rimWidth, double dpi)
    {
        if (step <= 0)
        {
            var outline = BuildOutline(w, h, shoulder, corners, inset);
            var (rim, top) = BuildOpenTopRim(w, h, shoulder, corners, inset);
            return (outline, rim, top, null);
        }
        var stairs = BuildStairs(w, h, shoulder, corners, inset, step);
        var polygon = Polygon(stairs);
        var (rimPoly, topPoly) = OpenTopPolygon(stairs);
        return (polygon, rimPoly, topPoly, rimWidth > 0 && stairs.Count >= 3 ? Bevel(stairs, PixelStairs.Step(rimWidth, dpi)) : null);
    }

    // The smooth outline flattened into straight edges, turned into steps (PixelStairs.Build).
    private static IReadOnlyList<Point> BuildStairs(double w, double h, double shoulderRadius, CornerRadius corners, double insetBy, double step)
    {
        var smooth = BuildOutline(w, h, shoulderRadius, corners, insetBy);
        if (smooth.IsEmpty()) return [];
        // The flattened copy keeps the inset as its own transform (or has it applied already, leaving none): only that transform
        // is applied, as applying the smooth outline's as well would move the shadow's copy in twice.
        var flat = smooth.GetFlattenedPathGeometry(.02, ToleranceType.Absolute);
        var matrix = flat.Transform?.Value ?? Matrix.Identity;
        var edges = new List<((double X, double Y) A, (double X, double Y) B)>();
        foreach (var figure in flat.Figures)
        {
            var points = new List<Point> { figure.StartPoint };
            foreach (var segment in figure.Segments)
                if (segment is PolyLineSegment poly) points.AddRange(poly.Points); else if (segment is LineSegment line) points.Add(line.Point);
            for (int i = 0; i < points.Count; i++)
            {
                Point a = matrix.Transform(points[i]), b = matrix.Transform(points[(i + 1) % points.Count]);
                edges.Add(((a.X, a.Y), (b.X, b.Y)));
            }
        }
        var bounds = smooth.Bounds;
        double curve = Math.Max(Math.Max(Math.Max(corners.TopLeft, corners.TopRight), Math.Max(corners.BottomLeft, corners.BottomRight)), shoulderRadius);
        return [.. PixelStairs.Build(edges, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, curve, step).Select(p => new Point(p.X, p.Y))];
    }

    private static Geometry Polygon(IReadOnlyList<Point> points)
    {
        if (points.Count < 3) return Geometry.Empty;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], true, true);
            path.PolyLineTo(points.Skip(1).ToList(), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    // The stepped polygon split into an open stroke path (without the flat edge along y = top) and that top segment on its own,
    // for pixel themes without a bevel brush.
    private static (Geometry Rim, Geometry TopEdge) OpenTopPolygon(IReadOnlyList<Point> points)
    {
        if (points.Count < 3) return (Geometry.Empty, Geometry.Empty);
        double top = points.Min(p => p.Y);
        int topEdge = -1;
        for (int i = 0; i < points.Count; i++)
        {
            Point a = points[i], b = points[(i + 1) % points.Count];
            if (Math.Abs(a.Y - b.Y) < 1e-9 && Math.Abs(a.Y - top) < 1e-6 && a.X < b.X) { topEdge = i; break; }
        }
        if (topEdge < 0) return (Polygon(points), Geometry.Empty);
        int start = (topEdge + 1) % points.Count;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[start], false, false);
            for (int k = 1; k < points.Count; k++)
                path.LineTo(points[(start + k) % points.Count], true, false);
        }
        geometry.Freeze();
        var topLine = new LineGeometry(points[topEdge], points[start]);
        topLine.Freeze();
        return (geometry, topLine);
    }

    // The bevel of a stepped outline: a band `thickness` DIP wide inside every edge, those facing up or left in one geometry and
    // those facing down or right in the other. The outline runs clockwise, so the inside lies to the right of each edge: an edge
    // running right faces up, one running down faces right, and so on. The top-most horizontal segment (y = top) is kept apart
    // as TopLight so TopRimOpacity can fade or omit it cleanly when docked to a screen edge.
    private static (Geometry Light, Geometry TopLight, Geometry Shade) Bevel(IReadOnlyList<Point> points, double thickness)
    {
        double top = points.Count > 0 ? points.Min(p => p.Y) : double.NaN;
        var light = new GeometryGroup(); var topLight = new GeometryGroup(); var shade = new GeometryGroup();
        for (int i = 0; i < points.Count; i++)
        {
            Point a = points[i], b = points[(i + 1) % points.Count];
            if (Math.Abs(a.Y - b.Y) < 1e-9)
            {
                if (a.X < b.X)
                    (Math.Abs(a.Y - top) < 1e-6 ? topLight : light).Children.Add(new RectangleGeometry(new Rect(a.X, a.Y, b.X - a.X, thickness)));
                else shade.Children.Add(new RectangleGeometry(new Rect(b.X, a.Y - thickness, a.X - b.X, thickness)));
            }
            else if (a.Y < b.Y) shade.Children.Add(new RectangleGeometry(new Rect(a.X - thickness, a.Y, thickness, b.Y - a.Y)));
            else light.Children.Add(new RectangleGeometry(new Rect(a.X, b.Y, thickness, a.Y - b.Y)));
        }
        light.Freeze(); topLight.Freeze(); shade.Freeze();
        return (light, topLight, shade);
    }

    // The island's smooth outline for a size, shoulder and corners; insetBy draws it that far inside the bounds (Inset). Frozen.
    internal static Geometry BuildOutline(double w, double h, double shoulderRadius, CornerRadius corners, double insetBy)
    {
        if (!(w > 0) || !(h > 0)) return Geometry.Empty;
        // The shoulders are sized on the full bounds, so an inset outline has the same shoulders moved in by the inset: its
        // curves then lie inside the full outline's. The corners keep their centres and lose the inset from their radii.
        double shoulder = Math.Clamp(shoulderRadius, 0, Math.Min(w / 4, h / 2));
        double shoulderDepth = shoulder * (7d / 16);
        double inset = Math.Clamp(insetBy, 0, Math.Min(w, h) / 4), top = shoulder > 0 ? 0 : inset;
        w -= 2 * inset; h -= top + inset;
        double left = shoulder, right = w - shoulder;
        // With shoulders the bottom corners also stay within half the height, so the limit does not jump when a shoulder
        // shrinking to nothing on a low surface (a notch becoming the line) ends; a notch at rest never reaches either bound.
        double limit = Math.Max(0, Math.Min(shoulder > 0 ? Math.Min(h - shoulderDepth, h / 2) : h / 2, (right - left) / 2));
        double bl = Math.Clamp(corners.BottomLeft - inset, 0, limit);
        double br = Math.Clamp(corners.BottomRight - inset, 0, limit);
        double tl = Math.Clamp(corners.TopLeft - inset, 0, Math.Min(w, h) / 2);
        double tr = Math.Clamp(corners.TopRight - inset, 0, Math.Min(w, h) / 2);
        const double k = .5522847498307936; // Cubic approximation of a circular quadrant.
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(shoulder > 0 ? 0 : tl, 0), true, true);
            path.LineTo(new Point(shoulder > 0 ? w : w - tr, 0), true, false);
            if (shoulder > 0)
            {
                // Two C2-joined cubics: curvature starts and ends at zero, matching
                // the straight edges. Independent width/depth avoids a circular cutout.
                path.BezierTo(new Point(w - shoulder * .28, 0), new Point(w - shoulder * .64, 0),
                    new Point(w - shoulder * .82, shoulderDepth * .18), true, true);
                path.BezierTo(new Point(right, shoulderDepth * .36), new Point(right, shoulderDepth * .72),
                    new Point(right, shoulderDepth), true, true);
            }
            else
                path.BezierTo(new Point(w - tr * (1 - k), 0), new Point(w, tr * (1 - k)),
                    new Point(w, tr), true, true);
            path.LineTo(new Point(right, h - br), true, false);
            path.BezierTo(new Point(right, h - br * (1 - k)), new Point(right - br * (1 - k), h),
                new Point(right - br, h), true, true);
            path.LineTo(new Point(left + bl, h), true, false);
            path.BezierTo(new Point(left + bl * (1 - k), h), new Point(left, h - bl * (1 - k)),
                new Point(left, h - bl), true, true);
            path.LineTo(new Point(left, shoulder > 0 ? shoulderDepth : tl), true, false);
            if (shoulder > 0)
            {
                path.BezierTo(new Point(left, shoulderDepth * .72), new Point(left, shoulderDepth * .36),
                    new Point(shoulder * .82, shoulderDepth * .18), true, true);
                path.BezierTo(new Point(shoulder * .64, 0), new Point(shoulder * .28, 0),
                    new Point(0, 0), true, true);
            }
            else
                path.BezierTo(new Point(0, tl * (1 - k)), new Point(tl * (1 - k), 0),
                    new Point(tl, 0), true, true);
        }
        if (inset > 0) geometry.Transform = new TranslateTransform(inset, top);
        geometry.Freeze();
        return geometry;
    }

    // The smooth outline split into an open rim path (without the flat top edge along y = 0) and that top segment on its own.
    // Handles both square top corners (docked notch/panel) and rounded top corners (mid-transition from a capsule or line) so
    // the flat top span can stay hidden or fade independently while the shoulders/corners/sides meet y = 0 cleanly butt-to-butt.
    private static (Geometry Rim, Geometry TopEdge) BuildOpenTopRim(double w, double h, double shoulderRadius, CornerRadius corners, double insetBy)
    {
        if (!(w > 0) || !(h > 0)) return (Geometry.Empty, Geometry.Empty);
        double shoulder = Math.Clamp(shoulderRadius, 0, Math.Min(w / 4, h / 2));
        double shoulderDepth = shoulder * (7d / 16);
        double inset = Math.Clamp(insetBy, 0, Math.Min(w, h) / 4), top = shoulder > 0 ? 0 : inset;
        w -= 2 * inset; h -= top + inset;
        if (!(w > 0) || !(h > 0)) return (Geometry.Empty, Geometry.Empty);
        double left = shoulder, right = w - shoulder;
        double limit = Math.Max(0, Math.Min(shoulder > 0 ? Math.Min(h - shoulderDepth, h / 2) : h / 2, (right - left) / 2));
        double bl = Math.Clamp(corners.BottomLeft - inset, 0, limit);
        double br = Math.Clamp(corners.BottomRight - inset, 0, limit);
        double tl = Math.Clamp(corners.TopLeft - inset, 0, Math.Min(w, h) / 2);
        double tr = Math.Clamp(corners.TopRight - inset, 0, Math.Min(w, h) / 2);
        const double k = .5522847498307936;
        double x0 = shoulder > 0 ? 0 : tl, x1 = shoulder > 0 ? w : w - tr;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(x1, 0), false, false);
            if (shoulder > 0)
            {
                path.BezierTo(new Point(w - shoulder * .28, 0), new Point(w - shoulder * .64, 0),
                    new Point(w - shoulder * .82, shoulderDepth * .18), true, true);
                path.BezierTo(new Point(right, shoulderDepth * .36), new Point(right, shoulderDepth * .72),
                    new Point(right, shoulderDepth), true, true);
            }
            else if (tr > 0)
                path.BezierTo(new Point(w - tr * (1 - k), 0), new Point(w, tr * (1 - k)),
                    new Point(w, tr), true, true);
            path.LineTo(new Point(right, h - br), true, false);
            path.BezierTo(new Point(right, h - br * (1 - k)), new Point(right - br * (1 - k), h),
                new Point(right - br, h), true, true);
            path.LineTo(new Point(left + bl, h), true, false);
            path.BezierTo(new Point(left + bl * (1 - k), h), new Point(left, h - bl * (1 - k)),
                new Point(left, h - bl), true, true);
            path.LineTo(new Point(left, shoulder > 0 ? shoulderDepth : tl), true, false);
            if (shoulder > 0)
            {
                path.BezierTo(new Point(left, shoulderDepth * .72), new Point(left, shoulderDepth * .36),
                    new Point(shoulder * .82, shoulderDepth * .18), true, true);
                path.BezierTo(new Point(shoulder * .64, 0), new Point(shoulder * .28, 0),
                    new Point(0, 0), true, true);
            }
            else if (tl > 0)
                path.BezierTo(new Point(0, tl * (1 - k)), new Point(tl * (1 - k), 0),
                    new Point(tl, 0), true, true);
        }
        Geometry topEdge = Geometry.Empty;
        if (x1 > x0)
        {
            var line = new LineGeometry(new Point(x0, 0), new Point(x1, 0));
            if (inset > 0) line.Transform = new TranslateTransform(inset, top);
            line.Freeze();
            topEdge = line;
        }
        if (inset > 0) geometry.Transform = new TranslateTransform(inset, top);
        geometry.Freeze();
        return (geometry, topEdge);
    }
}
