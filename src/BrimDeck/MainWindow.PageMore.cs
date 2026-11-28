using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The clock in the middle of the header, and the page switch fitted to the room left of it: the pages that do not fit go into the
// more button's list. The capsule under the chosen button is placed in MainWindow.PageSwitch.cs.
public partial class MainWindow
{
    // The clock always stands in the middle of the panel. The page switch keeps to the room left of it; FitPageSwitch moves the
    // pages that do not fit there into the more button's list.
    private void PlaceClock()
    {
        ClockLabel.HorizontalAlignment = HorizontalAlignment.Center;
        ClockLabel.Margin = new Thickness(0);
        FitPageSwitch();
    }

    // Each page button is 28 DIP wide with 1 DIP between buttons, inside the switch's 2 DIP padding; the more button is 40 DIP.
    private const double SwitchButtonWidth = 28, SwitchMoreWidth = 40, ClockGap = 8, PagerGap = 11;

    // Shows as many page buttons as fit between the switch's left inset and the clock (less the usage page's application pager,
    // which stands right after the switch), and the more button for the rest.
    private void FitPageSwitch()
    {
        var shown = DeckPages.Visible(Settings).ToList();
        int direct = shown.Count;
        double width = ContentCanvas.Width;
        if (double.IsFinite(width) && direct > 0)
        {
            double clock = ClockLabel.Text.Length > 0 ? TextWidth(ClockLabel.Text, 12, FontWeights.Normal) : 34;
            double pager = PagerWidth();
            double room = (width - clock) / 2 - ClockGap - _header.Side - (pager > 0 ? pager + PagerGap : 0);
            direct = PageSwitchFit.Direct(shown.Count, room, SwitchButtonWidth, SwitchMoreWidth, 2 * PanelHeader.SwitchPadding);
        }
        _pagesInMenu = shown.Skip(direct).ToList();
        bool first = true;
        foreach (var page in DeckPages.Order)
        {
            var button = PageButton(page);
            int at = shown.IndexOf(page);
            bool visible = at >= 0 && at < direct;
            button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) { button.Margin = new Thickness(first ? 0 : 1, 0, 0, 0); first = false; }
        }
        PageMoreButton.Visibility = _pagesInMenu.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PageMoreButton.Margin = new Thickness(first ? 0 : 1, 0, 0, 0);
        UpdateMoreButton();
        double switchWidth = PageSwitchFit.Width(direct, _pagesInMenu.Count > 0, SwitchButtonWidth, SwitchMoreWidth, 2 * PanelHeader.SwitchPadding);
        ApplicationPages.Margin = new Thickness(_header.Side + switchWidth + (shown.Count <= 1 ? 4 : PagerGap), 0, 0, 0);
        // The capsule moves when the chosen page's button changes, and also when the same button moves: the more button stands one
        // place further left or right when a page leaves or joins the switch (the clock's text or the pager can change the room).
        var fit = (direct, _pagesInMenu.Count > 0);
        bool moved = fit != _switchFit;
        _switchFit = fit;
        if (_capsuleOn is { } on && (moved || !ReferenceEquals(on, SwitchButton(CurrentPage)))) PlacePageCapsule();
    }
    // The number of page buttons in the switch, and whether the more button shows, as FitPageSwitch last set them.
    private (int Direct, bool More) _switchFit;
    // The usage page's application pager (‹ 1/2 ›) while the usage page shows it; 0 otherwise.
    private double PagerWidth()
    {
        if (!IsUsagePage || _details || ApplicationPages.Children.Count == 0) return 0;
        double width = 0;
        foreach (UIElement child in ApplicationPages.Children) { child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); width += child.DesiredSize.Width; }
        return width;
    }

    // What the more button's icon was last made for.
    private object? _moreGlyphKey;

    // Three dots, or the chosen page's icon while the chosen page is in the list; a dot while the notices page is in the list and
    // has notices not seen yet, unless the button already shows the bell with its own dot.
    private void UpdateMoreButton()
    {
        bool chosenInside = _pagesInMenu.Contains(CurrentPage);
        PageMoreButton.Foreground = UI.Brush(chosenInside ? Look.Primary : Look.Secondary);
        // Made again only when what it shows changed: this runs several times on every step of a resize.
        object glyph = chosenInside ? (CurrentPage, ShelfPageIcon.Data, NoticesBadge.Visibility, NoticesBadge.Fill) : "dots";
        if (!Equals(glyph, _moreGlyphKey))
        {
            _moreGlyphKey = glyph;
            PageMoreGlyph.Content = chosenInside ? PageGlyph(CurrentPage, PageMoreButton) : MoreDots(PageMoreButton);
        }
        PageMoreBadge.Visibility = _pagesInMenu.Contains(DeckPage.Notices) && CurrentPage != DeckPage.Notices && NoticesBadge.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(PageMoreButton, chosenInside ? Loc.T("更多页面：", "More pages: ") + PageTitle(CurrentPage) : Loc.T("更多页面", "More pages"));
    }

    // One row per page in the list, each with its icon and name; the chosen page's row is checked.
    private void PageMore_Click(object sender, RoutedEventArgs e)
    {
        if (_shelfMenu is { IsOpen: true } open && ReferenceEquals(open.PlacementTarget, PageMoreButton)) { CloseMenu(); return; }
        if (_pagesInMenu.Count == 0) return;
        var menu = NewMenu();
        foreach (var page in _pagesInMenu)
        {
            var glyph = new Viewbox { Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Child = PageGlyph(page, null) };
            menu.Items.Add(Item(PageTitle(page), () => SelectPage(page), check: page == CurrentPage, glyph: glyph));
        }
        OpenMenu(menu, PageMoreButton);
    }

    // The page's names, as the settings' list of pages gives them.
    private string PageTitle(DeckPage page) => page switch
    {
        DeckPage.Music => Loc.T("音乐", "Music"),
        DeckPage.Monitor => Loc.T("系统监控", "System monitor"),
        DeckPage.Clipboard => Settings.ShelfShown switch
        {
            ShelfShown.Clipboard => Loc.T("剪贴板", "Clipboard"),
            ShelfShown.Files => Loc.T("文件中转", "File transfer"),
            _ => Loc.T("剪贴板与文件中转", "Clipboard and file transfer")
        },
        DeckPage.Devices => Loc.T("设备电量", "Device batteries"),
        DeckPage.Notices => Loc.T("通知", "Notices"),
        DeckPage.Agenda => Loc.T("计划", "Planner"),
        _ => Loc.T("AI 用量", "AI usage")
    };

    // A copy of the page button's icon as it is drawn now (the clipboard page's icon and the bell's dot change). Its lines take
    // the foreground of the given button, or the menu's secondary color without one.
    private FrameworkElement PageGlyph(DeckPage page, Button? owner)
        => PageButton(page).Content is UIElement icon && CopyGlyph(icon, owner) is FrameworkElement copy ? copy : MoreDots(owner);
    private UIElement? CopyGlyph(UIElement part, Button? owner)
    {
        switch (part)
        {
            case Path path:
                var copy = new Path
                {
                    Data = path.Data?.CloneCurrentValue(), Fill = path.Fill, StrokeThickness = path.StrokeThickness, Stretch = path.Stretch,
                    StrokeStartLineCap = path.StrokeStartLineCap, StrokeEndLineCap = path.StrokeEndLineCap, StrokeLineJoin = path.StrokeLineJoin,
                    Clip = path.Clip?.CloneCurrentValue(), Visibility = path.Visibility, IsHitTestVisible = false
                };
                if (path.Stroke is not null) Paint(copy, owner);
                return copy;
            case Viewbox box:
                return new Viewbox { Width = box.Width, Height = box.Height, Child = box.Child is { } inner ? CopyGlyph(inner, owner) : null };
            case Panel panel:
                Panel copied = panel is Canvas ? new Canvas() : new Grid();
                copied.Width = panel.Width; copied.Height = panel.Height;
                foreach (UIElement child in panel.Children) if (CopyGlyph(child, owner) is { } c) copied.Children.Add(c);
                return copied;
            default:
                return null;
        }
    }
    // Three dots across a 16 DIP box, drawn like the page icons: a 24 unit drawing at 16.
    private FrameworkElement MoreDots(Button? owner)
    {
        var dots = new Path { Data = Geometry.Parse("M 5,12 L 5,12 M 12,12 L 12,12 M 19,12 L 19,12"), StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        Paint(dots, owner);
        return new Viewbox { Width = 16, Height = 16, Child = new Canvas { Width = 24, Height = 24, Children = { dots } } };
    }
    private void Paint(Shape shape, Button? owner)
    {
        if (owner is null) shape.Stroke = UI.Brush(Look.Secondary);
        else shape.SetBinding(Shape.StrokeProperty, new Binding(nameof(Button.Foreground)) { Source = owner });
    }
}
