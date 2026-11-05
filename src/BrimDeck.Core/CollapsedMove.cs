namespace BrimDeck.Core;

// Moving the collapsed notch, line or capsule by dragging it (MainWindow.Move.cs). Places are on the screen, in DIPs relative to
// the monitor's top-left corner, not in an edge's frame; work is the monitor's work area in the same coordinates.
// - The notch and the line hang from the nearest screen edge (EdgeFrame), including over a taskbar.
// - The capsule moves within the entire monitor. It snaps to the screen's middle and edges, and just above a bottom taskbar.
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

    // A drag uses the physical screen edge; null where there is no taskbar and the two anchors coincide.
    public static EdgeAnchor? AnchorAt(ScreenEdge edge, double screenWidth, double screenHeight, DipRect work)
        => HasTaskbar(edge, screenWidth, screenHeight, work) ? EdgeAnchor.Screen : null;

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
    // is held on, GuideY the screen's horizontal centre or an edge it is held at; null where nothing holds it that way.
    // Being held at a side of the screen draws no guide: the side is the guide.
    public static (double X, double Y, double? GuideX, double? GuideY) Capsule(double x, double y, double width, double height, double screenWidth, double screenHeight, DipRect work)
    {
        double w = Finite(width), h = Finite(height);
        double left = 0, right = Math.Max(0, Finite(screenWidth) - w);
        double top = 0, bottom = Math.Max(0, Finite(screenHeight) - h);
        x = Math.Clamp(double.IsFinite(x) ? x : left, left, right);
        y = Math.Clamp(double.IsFinite(y) ? y : top, top, bottom);
        double? guideX = null, guideY = null;
        double middle = Finite(screenWidth) / 2 - w / 2;
        var (sx, sxGuide) = Nearest(x, [(middle, Finite(screenWidth) / 2), (left + Gap, null), (right - Gap, null)]);
        if (sx is { } snappedX) { x = Math.Clamp(snappedX, left, right); guideX = sxGuide; }
        // Above a bottom taskbar the guide is the taskbar's top; at the monitor's bottom (or with no taskbar there) its bottom.
        var places = new List<(double Place, double? Guide)> { (top + Gap, top), (Finite(screenHeight) - Gap - h, Finite(screenHeight)), (Finite(screenHeight) / 2 - h / 2, Finite(screenHeight) / 2) };
        if (work.Bottom < Finite(screenHeight) - .5) places.Add((work.Bottom - Gap - h, work.Bottom));
        var (sy, syGuide) = Nearest(y, places);
        if (sy is { } snappedY) { y = Math.Clamp(snappedY, top, bottom); guideY = syGuide; }
        return (x, y, guideX, guideY);
    }

    // The saved offset that puts a notch's centre here on an edge of this span (CollapsedGeometry.Place centres it, then moves it).
    public static double NotchOffset(double centre, double span) => Math.Clamp(centre - Finite(span) / 2, -CollapsedLimits.OffsetLimit, CollapsedLimits.OffsetLimit);

    // The saved offset from the screen's centre and distance from its top for this drop.
    public static (double OffsetX, double Top) CapsulePlace(double x, double y, double width, double screenWidth)
        => (Math.Clamp(x + Finite(width) / 2 - Finite(screenWidth) / 2, -CollapsedLimits.OffsetLimit, CollapsedLimits.OffsetLimit),
            Math.Clamp(y, CollapsedLimits.CapsuleTopMin, CollapsedLimits.CapsuleTopMax));

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
