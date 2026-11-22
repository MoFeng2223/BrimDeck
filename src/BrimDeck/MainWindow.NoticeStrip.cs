using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// The collapsed count of unread notices (CompactPart.Notices): the icons of the applications the newest unread notices came from
// (NoticeStripIcons) or a bell, and the number. Nothing shows while every notice has been seen. A notice counts as seen once the
// notices page has shown it (NoticesPageShown), as the page switch's dot does; a click on the count opens that page.
public partial class MainWindow
{
    // At most this many applications' icons, newest first; the number counts every unread notice.
    private const int NoticeStripApps = 3;
    private FrameworkElement? _compactNotices;
    // What the count was last built from (NoticeStripKey): a change of it builds the collapsed row again.
    private string _noticeStripShown = "";
    // The icons the count shows, to build it again when one of them arrives.
    private readonly HashSet<string> _noticeStripIcons = new(StringComparer.Ordinal);

    // The unread notices' number and the newest of each application, newest first.
    private (int Count, List<Notice> Apps) NoticeStripContent()
    {
        if (_app.Notices is not { } center || !Settings.ShowsNotices(EffectiveStyle)) return (0, []);
        var hub = center.Hub;
        if (hub.UnviewedCount == 0) return (0, []);
        var apps = Settings.NoticeStripIcons
            ? hub.Notices.Where(notice => !notice.Viewed).DistinctBy(notice => notice.AppKey, StringComparer.Ordinal).Take(NoticeStripApps).ToList()
            : [];
        return (hub.UnviewedCount, apps);
    }

    private static string NoticeStripKey(int count, List<Notice> apps) => count + "|" + string.Join('|', apps.Select(notice => notice.AppKey + "/" + notice.IconRef));

    // A notice arrived, was seen or went: the collapsed row is built again when the count would show otherwise. While the panel is
    // open nothing is built; closing it builds the row.
    private void NoticeStripChanged()
    {
        if (_expanded) return;
        var (count, apps) = NoticeStripContent();
        if (NoticeStripKey(count, apps) != _noticeStripShown) RenderCompact();
    }

    // An icon arrived; the count shows it in place of the application's letter.
    private void NoticeStripIconLoaded(string iconRef)
    {
        if (!_expanded && _compactNotices is not null && _noticeStripIcons.Contains(iconRef)) RenderCompact();
    }

    // room: the width the count may take (a notch on a side edge, whose column is narrow); icons that do not fit are left out.
    private FrameworkElement? NoticeStrip(double scale, double room = double.PositiveInfinity)
    {
        var (count, apps) = NoticeStripContent();
        _noticeStripShown = NoticeStripKey(count, apps); _compactNotices = null; _noticeStripIcons.Clear();
        if (count == 0) return null;
        double s = CompactMusicLayout.SafeScale(scale), icon = CompactMusicLayout.Whole(14 * s), height = CompactMusicLayout.Whole(16 * s);
        double gap = CompactMusicLayout.Whole(3 * s), between = CompactMusicLayout.Whole(2 * s);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // Tabular figures, as the file transfer's count: the number keeps its width while it counts within the same digits.
        var number = new TextBlock
        {
            Text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture), FontFamily = UI.PanelFont, FontSize = CompactMusicLayout.Half(11.5 * s), FontWeight = FontWeights.SemiBold,
            Foreground = UI.Brush(Look.Primary), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap
        };
        Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        number.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double digits = number.DesiredSize.Width;
        // As many icons as fit beside the number, at least one: the newest application's.
        int shown = apps.Count;
        while (shown > 1 && DevicePixels.Up(shown * icon + (shown - 1) * between + gap, dpi) + digits > room) shown--;
        var marks = new List<FrameworkElement>();
        if (shown == 0) marks.Add(NoticeStripBell(icon));
        else foreach (var notice in apps.Take(shown)) marks.Add(NoticeStripIcon(notice, icon));
        double left = 0;
        var badge = new Grid { Height = height, Background = Brushes.Transparent };
        foreach (var mark in marks)
        {
            mark.HorizontalAlignment = HorizontalAlignment.Left; mark.VerticalAlignment = VerticalAlignment.Center; mark.IsHitTestVisible = false;
            mark.Margin = new Thickness(left, 0, 0, 0);
            badge.Children.Add(mark);
            left += icon + between;
        }
        // The number starts on a whole device pixel and the badge ends on one (see TransferBadge).
        double start = DevicePixels.Up(left - between + gap, dpi);
        number.Margin = new Thickness(start, 0, 0, 0);
        badge.Children.Add(number);
        badge.Width = DevicePixels.Up(start + digits, dpi);
        string tip = Loc.T($"{count} 条未读通知", Loc.Count(count, "unread notice", "unread notices")) + (apps.Count > 0 ? Loc.T("：", ": ") + string.Join(Loc.T("、", ", "), apps.Select(notice => notice.AppName)) : "");
        badge.ToolTip = tip; AutomationProperties.SetName(badge, tip);
        _compactNotices = badge;
        return badge;
    }

    // The application's icon at the mark's size; without one yet, its first letter on a plate tinted with its color.
    private FrameworkElement NoticeStripIcon(Notice notice, double size)
    {
        if (notice.IconRef is { } iconRef) _noticeStripIcons.Add(iconRef);
        if (_app.Notices.Icons.Get(notice.IconRef, out _) is { } image)
        {
            var picture = new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            return picture;
        }
        return NoticeLetter.Plate(notice.AppName, size, PanelTheme.Readable(_app.Notices.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey))));
    }

    // The bell of the page switch on the 16 unit grid of the row's other marks, scaled to the mark with their 1.35 DIP line
    // (as the file transfer's tray).
    private FrameworkElement NoticeStripBell(double size)
    {
        double grid = size / 16;
        return new Path
        {
            Data = Geometry.Parse("M 2,12.5 H 14 M 3.5,12.5 V 6 A 4.5,4.5 0 0 1 12.5,6 V 12.5 M 6.5,13 A 1.5,1.5 0 0 0 9.5,13"),
            Stroke = UI.Brush(Look.Secondary), StrokeThickness = 1.35 / grid, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 16, Height = 16, LayoutTransform = new ScaleTransform(grid, grid)
        };
    }

    // Opened while the pointer is on the count, the panel opens on the notices page.
    private bool PointerOnNotices() => PointerOnPart(_compactNotices);

    private static bool PointerOnPart(FrameworkElement? part)
    {
        if (part is not { IsVisible: true } || PresentationSource.FromVisual(part) is null) return false;
        var rect = new Rect(part.PointToScreen(new Point()), part.PointToScreen(new Point(part.ActualWidth, part.ActualHeight)));
        rect.Inflate(4, 6);
        return rect.Contains(WindowsHost.Cursor());
    }
}
