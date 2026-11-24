using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrimDeck;

// The notices page's two lines that must keep their words at any width of the panel: the list's header and a row's text.
internal static class NoticeLines
{
    // A line of the page that is cut by its own size rather than by the page's: the header drops its count under the title when the
    // three do not fit side by side, and a row moves its time under the title when the title would be left too short to read.
    public const double HeadLine = 20, HeadGap = 2, CountLine = 15, CountLines = 2;
    public const double RowTitleLine = 18, RowPreviewLine = 17, RowGap = 1, RowTitleMinimum = 88, RowTimeReserve = 52;
    public const double Gap = 6;
    // The opacity (0 to 255) of the glow in the application's color behind the focus column. Text on it (the status line, the
    // category, the letter plate) keeps its contrast there, so it stays faint.
    public const byte GlowAlpha = 36;

    // Whether a row's time sits at the right of its title: when the title keeps at least RowTitleMinimum beside a time of the usual
    // width. It depends on the row's width alone, not on its words, so every row of the list is laid out the same way.
    public static bool TimeBesideTitle(double width) => width - Gap - RowTimeReserve >= RowTitleMinimum;

    // Whether the header's title, count and tools fit on one line.
    public static bool HeadFits(double width, double titleWidth, double countWidth, double toolsWidth)
        => titleWidth + Gap + countWidth + Gap + toolsWidth <= width;
}

// The list's header: the title with its count and, at the right, the tools (pause, all / unread). On one line while they fit; when
// they do not, the tools stay beside the title and the count, which carries the pause time, wraps under them instead of being cut.
// Children in order: title, count, tools.
internal sealed class NoticeHead : Panel
{
    private bool _stacked;

    protected override Size MeasureOverride(Size available)
    {
        var (title, count, tools) = (Children[0], Children[1], Children[2]);
        var open = new Size(double.PositiveInfinity, double.PositiveInfinity);
        title.Measure(open); count.Measure(open); tools.Measure(open);
        double width = double.IsInfinity(available.Width) ? title.DesiredSize.Width + NoticeLines.Gap + count.DesiredSize.Width + NoticeLines.Gap + tools.DesiredSize.Width : available.Width;
        _stacked = !NoticeLines.HeadFits(width, title.DesiredSize.Width, count.DesiredSize.Width, tools.DesiredSize.Width);
        if (!_stacked) return new Size(width, NoticeLines.HeadLine);
        // The title gives way to the tools; the count has the whole width and at most two lines.
        title.Measure(new Size(Math.Max(0, width - tools.DesiredSize.Width - NoticeLines.Gap), NoticeLines.HeadLine));
        count.Measure(new Size(width, NoticeLines.CountLine * NoticeLines.CountLines));
        return new Size(width, NoticeLines.HeadLine + NoticeLines.HeadGap + count.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (title, count, tools) = (Children[0], Children[1], Children[2]);
        double toolsX = Math.Max(0, final.Width - tools.DesiredSize.Width);
        tools.Arrange(new Rect(toolsX, 0, tools.DesiredSize.Width, NoticeLines.HeadLine));
        if (!_stacked)
        {
            title.Arrange(new Rect(0, 0, title.DesiredSize.Width, NoticeLines.HeadLine));
            // The count sits a hair low, on the title's baseline as it did beside it.
            count.Arrange(new Rect(title.DesiredSize.Width + NoticeLines.Gap, 1, count.DesiredSize.Width, NoticeLines.HeadLine - 1));
            return final;
        }
        title.Arrange(new Rect(0, 0, Math.Min(title.DesiredSize.Width, Math.Max(0, toolsX - NoticeLines.Gap)), NoticeLines.HeadLine));
        count.Arrange(new Rect(0, NoticeLines.HeadLine + NoticeLines.HeadGap, final.Width, count.DesiredSize.Height));
        return final;
    }
}

// One line of text that ends in "…" where it is cut, measured for a panel whose width keeps changing. WPF lays out a TextBlock
// that trims its text again for every new width it is given, which costs 50 to 550 µs a line, against about 2 µs for one measured
// at a width it was measured at before (ReadmeMedia notice-resize, 2026-11-24). Dragging the panel's edges gave each of the notices
// page's lines a new width every frame, and those lines took most of the frame. A line that fits is measured once at its own width
// and keeps that measurement whatever width it is given; only a line that is cut is measured at the width it has. A line whose
// words, font or size changed (WPF marks its measurement out of date) is measured at its own width again.
internal sealed class FitLine(UIElement line)
{
    private static readonly Size Open = new(double.PositiveInfinity, double.PositiveInfinity);
    private double _natural = double.NaN;
    private bool _open;

    // The line's whole width.
    public double Natural { get { Refresh(); return _natural; } }

    public void Measure(double width, double height)
    {
        Refresh();
        if (_natural <= width) { if (!_open) { line.Measure(Open); _open = true; } }
        else { line.Measure(new Size(width, height)); _open = false; }
    }

    // A line that fits takes its own width, at the start of the room it has: at the right for one that runs right to left. Drawn
    // where WPF would draw it had it been measured at the whole width.
    public void Arrange(Rect room)
    {
        if (!_open || _natural >= room.Width) { line.Arrange(room); return; }
        bool turned = line is FrameworkElement element && element.Parent is FrameworkElement parent && element.FlowDirection != parent.FlowDirection;
        line.Arrange(new Rect(turned ? room.Right - _natural : room.X, room.Y, _natural, room.Height));
    }

    private void Refresh()
    {
        if (!double.IsNaN(_natural) && line.IsMeasureValid) return;
        line.Measure(Open);
        _natural = line.DesiredSize.Width; _open = true;
    }
}

// A FitLine as an element, for a line inside an ordinary panel.
internal sealed class FitLineBox : Decorator
{
    private FitLine? _fit;
    private UIElement? _for;

    private FitLine? Fit()
    {
        if (Child is null) return null;
        if (!ReferenceEquals(_for, Child)) { _fit = new FitLine(Child); _for = Child; }
        return _fit;
    }

    protected override Size MeasureOverride(Size available)
    {
        if (Fit() is not { } fit) return default;
        if (double.IsInfinity(available.Width)) return new Size(fit.Natural, Math.Min(available.Height, Child!.DesiredSize.Height));
        fit.Measure(available.Width, available.Height);
        return new Size(Math.Min(available.Width, fit.Natural), Child!.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        Fit()?.Arrange(new Rect(final));
        return final;
    }

    // The line's whole width, and whether it is cut at the width it has now.
    public double Natural => Fit()?.Natural ?? 0;
    public bool Cut => Natural > ActualWidth + .5;
}

// A vertical stack in a scroll viewer that lays out again only the children in view. The notices page lists every notice of the
// day, and while the panel's edges were dragged each of them was laid out again for each frame, scrolled out of view or not: with
// thirty rows that was about sixty lines cut with an ellipsis a frame (ReadmeMedia notice-resize, 2026-11-24). A child out of view
// keeps the width it was last laid out at, and is laid out at the stack's width as it comes into view (the viewer's scrolling asks
// for that). The children's heights must not depend on their width, as the notices' rows do not: out of view, a child keeps the
// height it had.
internal sealed class ViewportStack : Panel
{
    // How far beyond the viewer's edges a child still counts as in view.
    private const double Slack = 48;
    // The width each child was last measured at, and the children left at that width by the last measuring.
    private readonly Dictionary<UIElement, double> _widths = [];
    private readonly HashSet<UIElement> _held = [];
    private ScrollViewer? _viewer;

    public ViewportStack()
    {
        Loaded += (_, _) => Watch(FindViewer());
        Unloaded += (_, _) => Watch(null);
    }

    private ScrollViewer? FindViewer()
    {
        for (var node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer viewer) return viewer;
        return null;
    }

    private void Watch(ScrollViewer? viewer)
    {
        if (ReferenceEquals(viewer, _viewer)) return;
        if (_viewer is not null) _viewer.ScrollChanged -= Scrolled;
        _viewer = viewer;
        if (_viewer is not null) _viewer.ScrollChanged += Scrolled;
    }

    private void Scrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_held.Count > 0 && ReferenceEquals(e.OriginalSource, _viewer) && (e.VerticalChange != 0 || e.ViewportHeightChange != 0)) InvalidateMeasure();
    }

    // The part of the stack the viewer shows, in the stack's own coordinates, from the last layout; null when that is not known.
    private Rect? View()
    {
        if (_viewer is not { ViewportHeight: > 0 } viewer || RenderSize.Height <= 0 || !viewer.IsAncestorOf(this)) return null;
        try
        {
            var shown = TransformToAncestor(viewer).Inverse?.TransformBounds(new Rect(0, 0, viewer.ViewportWidth, viewer.ViewportHeight));
            return shown is { } rect ? new Rect(rect.X, rect.Y - Slack, rect.Width, rect.Height + 2 * Slack) : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    protected override Size MeasureOverride(Size available)
    {
        var view = double.IsInfinity(available.Width) ? null : View();
        _held.Clear();
        double y = 0, widest = 0;
        foreach (UIElement child in InternalChildren)
        {
            double height = child.DesiredSize.Height;
            bool known = _widths.TryGetValue(child, out double width);
            if (known && width != available.Width && child.IsMeasureValid && view is { } shown && (y + height < shown.Top || y > shown.Bottom))
                _held.Add(child);
            else { child.Measure(new Size(available.Width, double.PositiveInfinity)); _widths[child] = available.Width; }
            y += child.DesiredSize.Height;
            widest = Math.Max(widest, child.DesiredSize.Width);
        }
        return new Size(double.IsInfinity(available.Width) ? widest : Math.Min(widest, available.Width), y);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double y = 0;
        foreach (UIElement child in InternalChildren)
        {
            double height = child.DesiredSize.Height;
            child.Arrange(new Rect(0, y, _held.Contains(child) ? _widths[child] : final.Width, height));
            y += height;
        }
        return final;
    }

    protected override void OnVisualChildrenChanged(DependencyObject? added, DependencyObject? removed)
    {
        base.OnVisualChildrenChanged(added, removed);
        if (removed is UIElement gone) { _widths.Remove(gone); _held.Remove(gone); }
    }
}

// What a row says: the title and when, and a line of preview. The time sits at the right of the title; when that would leave the
// title under RowTitleMinimum it moves to the front of the preview line, so the title is read whole. Children in order: title,
// time, preview.
internal sealed class NoticeRowTexts : Panel
{
    private bool _beside;
    private FitLine? _title, _preview;

    protected override Size MeasureOverride(Size available)
    {
        var (title, time, preview) = (Children[0], Children[1], Children[2]);
        _title ??= new FitLine(title); _preview ??= new FitLine(preview);
        time.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = double.IsInfinity(available.Width) ? _title.Natural + NoticeLines.Gap + time.DesiredSize.Width : available.Width;
        _beside = NoticeLines.TimeBesideTitle(width);
        double timeWidth = time.DesiredSize.Width + NoticeLines.Gap;
        _title.Measure(_beside ? Math.Max(0, width - timeWidth) : width, NoticeLines.RowTitleLine);
        _preview.Measure(_beside ? width : Math.Max(0, width - timeWidth), NoticeLines.RowPreviewLine);
        return new Size(width, NoticeLines.RowTitleLine + NoticeLines.RowGap + NoticeLines.RowPreviewLine);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (time, title, preview) = (Children[1], _title!, _preview!);
        double timeWidth = time.DesiredSize.Width;
        double secondY = NoticeLines.RowTitleLine + NoticeLines.RowGap;
        if (_beside)
        {
            time.Arrange(new Rect(Math.Max(0, final.Width - timeWidth), 0, timeWidth, NoticeLines.RowTitleLine));
            title.Arrange(new Rect(0, 0, Math.Max(0, final.Width - timeWidth - NoticeLines.Gap), NoticeLines.RowTitleLine));
            preview.Arrange(new Rect(0, secondY, final.Width, NoticeLines.RowPreviewLine));
        }
        else
        {
            title.Arrange(new Rect(0, 0, final.Width, NoticeLines.RowTitleLine));
            time.Arrange(new Rect(0, secondY, timeWidth, NoticeLines.RowPreviewLine));
            preview.Arrange(new Rect(timeWidth + NoticeLines.Gap, secondY, Math.Max(0, final.Width - timeWidth - NoticeLines.Gap), NoticeLines.RowPreviewLine));
        }
        return final;
    }
}

// The focus's buttons: delete and the actions flow from the left and wrap to a new line where one does not fit; with MainLast, the
// last child (the main button) sits at the right end of the last line it lands on. On one line this is what a dock panel gave; at
// the narrowest page, where the buttons did not fit, they are all reached instead of the later ones being cut off.
internal sealed class NoticeButtons : Panel
{
    public const double LineGap = 8;
    public bool MainLast { get; set; }
    // Turns the main button, last, into a round icon (true) or back into its words (false). Where the row does not fit one line
    // with the words, the main button shows the icon, so the row stays on one line; it wraps only where even that does not fit.
    public Action<bool>? CompactMain { get; set; }
    private bool _compact;
    private double _fullMain;
    private readonly List<Point> _at = [];

    protected override Size MeasureOverride(Size available)
    {
        _at.Clear();
        double x = 0, y = 0, line = 0, widest = 0;
        var open = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (UIElement child in Children) child.Measure(open);
        if (MainLast && CompactMain is { } compactMain && Children.Count > 1 && !double.IsInfinity(available.Width))
        {
            var main = Children[^1];
            // The width with the words, kept from when they showed: the icon's would always fit.
            if (!_compact) _fullMain = main.DesiredSize.Width;
            double others = 0;
            for (int i = 0; i < Children.Count - 1; i++) others += Children[i].DesiredSize.Width;
            bool compact = others + _fullMain > available.Width;
            if (compact != _compact) { _compact = compact; compactMain(compact); main.Measure(open); }
        }
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var (w, h) = (child.DesiredSize.Width, child.DesiredSize.Height);
            if (x > 0 && x + w > available.Width) { y += line + LineGap; x = 0; line = 0; }
            // The main button, last, goes to the right end of its line (or, on a line of its own, of the row).
            double at = MainLast && i == Children.Count - 1 && !double.IsInfinity(available.Width) ? Math.Max(x, available.Width - w) : x;
            _at.Add(new Point(at, y));
            x += w; line = Math.Max(line, h); widest = Math.Max(widest, at + w);
        }
        return new Size(double.IsInfinity(available.Width) ? widest : available.Width, y + line);
    }

    protected override Size ArrangeOverride(Size final)
    {
        for (int i = 0; i < Children.Count && i < _at.Count; i++)
            Children[i].Arrange(new Rect(_at[i], Children[i].DesiredSize));
        return final;
    }
}

// A scrolling column that fades out at its foot into the panel, whatever the panel is made of: the fade is a mask on the viewer
// (the content shows through it less and less), not a color laid over it that would have to match the surface, its gradient, its
// texture or the glass. The content leaves the same height free below it, so scrolled to its end the last line is not in the fade.
// The mask is there only while more of the content lies below: under a mask WPF draws the text in a layer of its own, without
// ClearType, and it looks blurred next to the text around it. Content that fits, or a column scrolled to its end, has no mask.
internal static class NoticeFade
{
    public static void Foot(ScrollViewer viewer, double height)
    {
        // Only the alpha of the mask's colors counts: opaque down to the fade, transparent at the foot.
        var shown = new GradientStop(Color.FromArgb(255, 0, 0, 0), 1);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0));
        mask.GradientStops.Add(shown);
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        void Fit()
        {
            shown.Offset = viewer.ActualHeight > height ? 1 - height / viewer.ActualHeight : 0;
            bool more = viewer.ScrollableHeight > 1 && viewer.VerticalOffset < viewer.ScrollableHeight - 1;
            if (!ReferenceEquals(viewer.OpacityMask, more ? mask : null)) viewer.OpacityMask = more ? mask : null;
        }
        viewer.SizeChanged += (_, _) => Fit();
        viewer.ScrollChanged += (_, _) => Fit();
        Fit();
    }
}
