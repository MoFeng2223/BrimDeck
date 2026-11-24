using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    // Places the chosen page's fill under its button. While the open panel shows it, it glides there as the monitor page's sort pill
    // does; otherwise it is set in place. A switch whose buttons are not laid out yet places it once they are.
    private void PlacePageCapsule()
    {
        var button = SwitchButton(CurrentPage);
        _capsuleOn = button;
        if (!CapsuleReady(button))
        {
            // Retried after a layout pass, never by posting itself again: the switch is collapsed while the usage details show, its
            // buttons then keep an invalid measure and their old width, and a retry that posted itself spun the UI thread at 90%.
            if (!_capsulePending) { _capsulePending = true; LayoutRetry.WhenReady(this, () => CapsuleReady(SwitchButton(CurrentPage)), () => { _capsulePending = false; PlacePageCapsule(); }); }
            return;
        }
        // The button's set height, not ActualHeight: a height ApplyHeader just set shows in ActualHeight only after the next layout pass.
        double x = button.TranslatePoint(new Point(), (UIElement)PageCapsule.Parent).X, width = button.ActualWidth, height = button.Height;
        bool glide = _expanded && !_preview && PageSwitch.IsVisible && Animates && PageCapsule.Width > 0 && Math.Abs(PageCapsuleShift.X - x) > .5;
        PageCapsule.Height = height;
        // motion-review F8: the width is set at once and the change is drawn by stretching the capsule from its left edge, so the
        // glide lays nothing out again. A stretch still under way goes on from the width it draws now, not from its laid-out width.
        double was = PageCapsule.Width * PageCapsuleStretch.ScaleX;
        PageCapsule.Width = width;
        PageCapsuleStretch.BeginAnimation(ScaleTransform.ScaleXProperty, null); PageCapsuleStretch.ScaleX = 1;
        if (glide && StretchGlide.From(was, width) is { } stretchFrom)
            PageCapsuleStretch.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(stretchFrom, 1, TimeSpan.FromMilliseconds(Soft.Choose))
            { EasingFunction = Soft.Follow, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
        double from = PageCapsuleShift.X;
        PageCapsuleShift.BeginAnimation(TranslateTransform.XProperty, null);
        PageCapsuleShift.X = x;
        if (glide) PageCapsuleShift.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(from, x, TimeSpan.FromMilliseconds(Soft.Choose))
        { EasingFunction = Soft.Follow, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
    }
    private bool _capsulePending;
    // The button is laid out, and so is the row it stands in: a button shown, hidden or moved before it leaves the button's own
    // measure valid while its place in the row is still the old one until the next layout pass.
    private static bool CapsuleReady(Button button)
        => LayoutRetry.Laid(button) && (VisualTreeHelper.GetParent(button) is not UIElement row || row.IsMeasureValid && row.IsArrangeValid);
    // The button the capsule was last placed under.
    private Button? _capsuleOn;
    // The page buttons take the header's button height, and the capsule under the chosen one takes it in the same step: PlacePageCapsule,
    // which RefreshStatus runs before ApplyHeader, would otherwise leave it at the size tier left behind until the next refresh.
    private void FitPageButtons(PanelHeader header)
    {
        foreach (var button in new[] { AIUsageButton, MonitorPageButton, MusicPageButton, ShelfPageButton, DevicesPageButton, NoticesPageButton, AgendaPageButton, PageMoreButton }) button.Height = header.ButtonHeight;
        PageCapsule.Height = header.ButtonHeight;
    }
    // The buttons stand in the order chosen in the settings, the more button after them; FitPageSwitch shows those that fit.
    private void OrderPageButtons()
    {
        if (AIUsageButton.Parent is not Panel buttons) return;
        var order = Settings.PageOrder.Select(PageButton).Append(PageMoreButton).ToList();
        if (!buttons.Children.Cast<UIElement>().SequenceEqual(order))
        {
            buttons.Children.Clear();
            foreach (var button in order) buttons.Children.Add(button);
        }
    }

    // The shown pages that do not fit in the switch, in switch order; the more button lists them (MainWindow.PageMore.cs).
    private List<DeckPage> _pagesInMenu = [];

    // The button that stands for a page: its own button, or the more button while the page is in the more button's list.
    private Button SwitchButton(DeckPage page) => _pagesInMenu.Contains(page) ? PageMoreButton : PageButton(page);

    private Button PageButton(DeckPage page) => page switch { DeckPage.Music => MusicPageButton, DeckPage.Monitor => MonitorPageButton, DeckPage.Clipboard => ShelfPageButton, DeckPage.Devices => DevicesPageButton, DeckPage.Notices => NoticesPageButton, DeckPage.Agenda => AgendaPageButton, _ => AIUsageButton };
}
