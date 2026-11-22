namespace BrimDeck.Core;

// Moving the collapsed notch, line or capsule by dragging it (MainWindow.Move.cs). Places are on the screen, in DIPs relative to
// the monitor's top-left corner, not in an edge's frame; work is the monitor's work area in the same coordinates.
// - The notch and the line hang from the edge nearest the pointer (EdgeFrame): from the screen's edge while the pointer is over a
//   taskbar there, otherwise from the taskbar's inner edge (AnchorAt). They move along that edge and are held at its middle.
// - The capsule moves anywhere on the monitor, over a taskbar on any side (CollapsedGeometry.CapsuleTopRange, OffsetXRange). It
//   is held at the work area's middle across and down, at Gap inside each side of the work area, and at Gap inside each side of
//   the monitor where a taskbar lies between the two.
public static class CollapsedMove
{
    // How near a place the surface has to come to be held there, and the gap the capsule keeps from the edges it is held at (its
    // usual distance from the top).
    public const double SnapReach = 12, Gap = CollapsedLimits.CapsuleTopDefault;
    // How much nearer than the edge the notch hangs from another edge has to be before the notch moves there.
    public const double EdgeHysteresis = 32;

    // The line a surface hangs from on an edge, as EdgeFrame.For takes it.
    public static double Line(ScreenEdge edge, bool overTaskbar, double screenWidth, double screenHeight, DipRect work)
        => EdgeFrame.For(edge, screenWidth, screenHeight, work, overTaskbar).Line;

    // Whether a taskbar takes part of the screen along an edge: there the screen's edge and the work area's differ.
    public static bool HasTaskbar(ScreenEdge edge, double screenWidth, double screenHeight, DipRect work) => edge switch
    {
        ScreenEdge.Bottom => work.Bottom < Finite(screenHeight) - .5,
        ScreenEdge.Left => work.X > .5,
        ScreenEdge.Right => work.Right < Finite(screenWidth) - .5,
        _ => work.Y > .5
    };

    // What a surface dragged to an edge hangs from: the screen's edge while the pointer is over the taskbar there, the taskbar's
    // inner edge otherwise; null where no taskbar is on that edge, so the saved choice stays as it was.
    public static EdgeAnchor? AnchorAt(ScreenEdge edge, double x, double y, double screenWidth, double screenHeight, DipRect work)
    {
        if (!HasTaskbar(edge, screenWidth, screenHeight, work)) return null;
        bool over = edge switch
        {
            ScreenEdge.Bottom => y > work.Bottom,
            ScreenEdge.Left => x < work.X,
            ScreenEdge.Right => x > work.Right,
            _ => y < work.Y
        };
        return over ? EdgeAnchor.Screen : EdgeAnchor.Taskbar;
    }

    // Where along an edge a surface's centre may go: the whole edge for the notch (CollapsedGeometry.Place on the monitor), and for
    // the line the work area's part of it, or the whole edge when it hangs over the taskbar.
    public static (double From, double To) AlongRange(CompactStyle style, ScreenEdge edge, bool overTaskbar, double screenWidth, double screenHeight, DipRect work)
    {
        if (style != CompactStyle.Line || overTaskbar) return (0, Span(edge, screenWidth, screenHeight));
        return edge is ScreenEdge.Left or ScreenEdge.Right ? (work.Y, work.Bottom) : (work.X, work.Right);
    }

    // The length of an edge: the notch's along span there.
    public static double Span(ScreenEdge edge, double screenWidth, double screenHeight)
        => edge is ScreenEdge.Left or ScreenEdge.Right ? Finite(screenHeight) : Finite(screenWidth);

    // Where a point lies along an edge.
    public static double Along(ScreenEdge edge, double x, double y) => edge is ScreenEdge.Left or ScreenEdge.Right ? y : x;

    // The edge the notch hangs from for a pointer at (x, y): the nearest side of the screen, unless current is less than
    // EdgeHysteresis farther.
    public static ScreenEdge NearestEdge(double x, double y, double screenWidth, double screenHeight, ScreenEdge current)
    {
        double Distance(ScreenEdge edge) => edge switch
        {
            ScreenEdge.Bottom => Math.Abs(Finite(screenHeight) - y),
            ScreenEdge.Left => Math.Abs(x),
            ScreenEdge.Right => Math.Abs(Finite(screenWidth) - x),
            _ => Math.Abs(y)
        };
        var nearest = current;
        double best = Distance(current) - EdgeHysteresis;
        foreach (var edge in Enum.GetValues<ScreenEdge>())
        {
            double distance = Distance(edge);
            if (distance < best) { best = distance; nearest = edge; }
        }
        return nearest;
    }

    // The notch's centre along an edge of this span for the centre the pointer asks for: within the edge, and at the edge's middle
    // when it comes within SnapReach of it. Snapped says it is held there.
    public static (double Centre, bool Snapped) NotchAlong(double wanted, double length, double span) => AlongIn(wanted, length, 0, span);

    // The same within the part of an edge from `from` to `to`.
    public static (double Centre, bool Snapped) AlongIn(double wanted, double length, double from, double to)
    {
        double span = Math.Max(0, to - from), half = Math.Max(0, (span - Finite(length)) / 2), middle = from + span / 2;
        double centre = Math.Clamp(double.IsFinite(wanted) ? wanted : middle, middle - half, middle + half);
        return Math.Abs(centre - middle) <= SnapReach ? (middle, true) : (centre, false);
    }

    // The saved offset that puts a centre here in the part of an edge from `from` to `to` (CollapsedGeometry.Place centres a surface
    // there, then moves it by the offset).
    public static double Offset(double centre, double from, double to) => Math.Clamp(centre - (from + to) / 2, -CollapsedLimits.OffsetLimit, CollapsedLimits.OffsetLimit);

    // The capsule's top-left corner for the corner the pointer asks for, and what holds it: GuideX is the vertical line its centre
    // is held on, GuideY the horizontal line its middle is held on (the work area's middle) or one of its edges is held at (the work
    // area's top or bottom, or the monitor's top or bottom);
    // null where nothing holds it that way. Being held at a side of the work area or the monitor draws no guide: the side is the guide.
    public static (double X, double Y, double? GuideX, double? GuideY) Capsule(double x, double y, double width, double height, double screenWidth, double screenHeight, DipRect work)
    {
        double w = Finite(width), h = Finite(height), sw = Finite(screenWidth), sh = Finite(screenHeight);
        double right = Math.Max(0, sw - w), bottom = Math.Max(0, sh - h);
        x = Math.Clamp(double.IsFinite(x) ? x : work.X, 0, right);
        y = Math.Clamp(double.IsFinite(y) ? y : work.Y, 0, bottom);
        double? guideX = null, guideY = null;
        var across = new List<(double Place, double? Guide)> { (work.X + work.Width / 2 - w / 2, work.X + work.Width / 2), (work.X + Gap, null), (work.Right - w - Gap, null) };
        if (work.X > .5) across.Add((Gap, null));
        if (work.Right < sw - .5) across.Add((sw - w - Gap, null));
        var (sx, sxGuide) = Nearest(x, across);
        if (sx is { } snappedX) { x = Math.Clamp(snappedX, 0, right); guideX = sxGuide; }
        // By a taskbar the guide is its inner edge; at the monitor's top or bottom, beyond the taskbar or with none there, that edge.
        var down = new List<(double Place, double? Guide)> { (work.Y + Gap, work.Y), (sh - Gap - h, sh), (work.Y + work.Height / 2 - h / 2, work.Y + work.Height / 2) };
        if (work.Y > .5) down.Add((Gap, 0));
        if (work.Bottom < sh - .5) down.Add((work.Bottom - Gap - h, work.Bottom));
        var (sy, syGuide) = Nearest(y, down);
        if (sy is { } snappedY) { y = Math.Clamp(snappedY, 0, bottom); guideY = syGuide; }
        return (x, y, guideX, guideY);
    }

    // The saved offset that puts a notch's centre here on an edge of this span (CollapsedGeometry.Place centres it, then moves it).
    public static double NotchOffset(double centre, double span) => Math.Clamp(centre - Finite(span) / 2, -CollapsedLimits.OffsetLimit, CollapsedLimits.OffsetLimit);

    // The saved offset and distance from the work area's top that put a capsule of this width with its top-left corner here.
    public static (double OffsetX, double Top) CapsulePlace(double x, double y, double width, DipRect work)
        => (Math.Clamp(x + Finite(width) / 2 - (work.X + work.Width / 2), -CollapsedLimits.OffsetLimit, CollapsedLimits.OffsetLimit),
            Math.Clamp(y - work.Y, CollapsedLimits.CapsuleTopMin, CollapsedLimits.CapsuleTopMax));

    // The nearest place within SnapReach, with its guide; null when none is that near.
    private static (double? Place, double? Guide) Nearest(double value, IEnumerable<(double Place, double? Guide)> places)
    {
        (double? Place, double? Guide) best = (null, null);
        double distance = SnapReach;
        foreach (var (place, guide) in places)
        {
            double d = Math.Abs(value - place);
            if (d <= distance) { distance = d; best = (place, guide); }
        }
        return best;
    }

    private static double Finite(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
