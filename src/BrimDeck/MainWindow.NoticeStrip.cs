using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// The collapsed count of unread notices (CompactPart.Notices): the round icons of the applications the newest unread notices
// came from, stacked from left to right with the newest at the right on top (at most NoticeStripApps, however many applications
// there are; NoticeStripIcons off, a bell in a round plate), and one number, every unread notice. An unread call takes its
// application's place as a phone in the call's color. Nothing shows while every notice has been seen. A notice counts as seen
// once the notices page has shown it (NoticesPageShown), as the page switch's dot does; a pointer on the count opens that page.
public partial class MainWindow
{
    // At most this many applications' icons; the number counts every unread notice.
    private const int NoticeStripApps = 3;
    private FrameworkElement? _compactNotices;
    // What the count was last built from (NoticeStripKey): a change of it builds the collapsed row again.
    private string _noticeStripShown = "";
    // The icons the collapsed row shows (the notices' and the coding agents'), to build it again when one of them arrives.
    private readonly HashSet<string> _noticeStripIcons = new(StringComparer.Ordinal);

    // The unread notices' number and the newest of each application, newest first.
    private (int Count, List<Notice> Apps) NoticeStripContent()
    {
        if (_app.Notices is not { } center || !Settings.ShowsNotices(EffectiveStyle)) return (0, []);
        var hub = center.Hub;
        if (hub.UnviewedCount == 0) return (0, []);
        var apps = Settings.NoticeStripIcons
            // An unread call comes first, in front of the applications.
            ? hub.Notices.Where(notice => !notice.Viewed).DistinctBy(notice => notice.Call ? "call:" + notice.Id : notice.AppKey, StringComparer.Ordinal)
                .OrderByDescending(notice => notice.Call).Take(NoticeStripApps).ToList()
            : [];
        return (hub.UnviewedCount, apps);
    }

    private static string NoticeStripKey(int count, List<Notice> apps)
        => count + "|" + string.Join('|', apps.Select(notice => notice.AppKey + "/" + notice.IconRef + (notice.Call ? "/call" + notice.SourcePresent : "")));

    // A notice arrived, was seen or went: the collapsed row is built again when the count would show otherwise. While the panel is
    // open nothing is built; closing it builds the row.
    private void NoticeStripChanged()
    {
        if (_expanded) return;
        var (count, apps) = NoticeStripContent();
        if (NoticeStripKey(count, apps) != _noticeStripShown) RenderCompact();
    }

    // An icon arrived; the row shows it in place of the application's letter.
    private void NoticeStripIconLoaded(string iconRef)
    {
        if (!_expanded && (_compactNotices is not null || _compactAgents is not null) && _noticeStripIcons.Contains(iconRef)) RenderCompact();
    }

    // single: only the newest application's icon (CompactAttention's last step for a short surface).
    private FrameworkElement? NoticeStrip(double scale, bool single = false)
    {
        var (count, apps) = NoticeStripContent();
        _noticeStripShown = NoticeStripKey(count, apps); _compactNotices = null;
        if (count == 0) return null;
        double s = CompactMusicLayout.SafeScale(scale), dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var marks = new List<FrameworkElement>();
        if (apps.Count == 0) marks.Add(CompactStacks.Plate(CompactStacks.Diameter(s), s, Look, NoticeBadges.Glyph(NoticeGlyph.Bell, Math.Round(CompactStacks.Diameter(s) * .5), Look.ColorOf("textPrimary"))));
        // Oldest of those shown at the left, the newest at the right, on top.
        else foreach (var notice in (single ? apps.Take(1) : apps).Reverse()) marks.Add(NoticeStripIcon(notice, s));
        var stack = CompactStacks.Stack(marks, s);
        // Tabular figures, as the file transfer's count: the number keeps its width while it counts within the same digits.
        var number = CompactStacks.Words(count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture), s, Look.Primary);
        Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        var badge = CompactStacks.Row(stack, number, s, dpi);
        string tip = Loc.T($"{count} 条未读通知", Loc.Count(count, "unread notice", "unread notices")) + (apps.Count > 0 ? Loc.T("：", ": ") + string.Join(Loc.T("、", ", "), apps.Select(notice => notice.AppName)) : "");
        badge.ToolTip = tip; AutomationProperties.SetName(badge, tip);
        _compactNotices = badge;
        return badge;
    }

    // The application's icon, round; without one yet, its first letter on a round plate in its color. A call shows a phone: green
    // while it rings, in the danger color once it has stopped unanswered here.
    private FrameworkElement NoticeStripIcon(Notice notice, double s)
    {
        double size = CompactStacks.Diameter(s);
        if (notice.Call)
        {
            return CompactStacks.Plate(size, s, Look, NoticeBadges.Glyph(NoticeGlyph.Call, Math.Round(size * .5), Look.ColorOf(notice.SourcePresent ? "success" : "danger")));
        }
        if (notice.IconRef is { } iconRef) _noticeStripIcons.Add(iconRef);
        var image = _app.Notices.Icons.Get(notice.IconRef, out _);
        var accent = PanelTheme.Readable(_app.Notices.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey)));
        return CompactStacks.Icon(image, size, s, Look, notice.AppName, accent);
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
