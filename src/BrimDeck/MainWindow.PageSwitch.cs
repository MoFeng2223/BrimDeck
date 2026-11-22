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
        var button = PageButton(CurrentPage);
        if (!LayoutRetry.Laid(button))
        {
            // Retried after a layout pass, never by posting itself again: the switch is collapsed while the usage details show, its
            // buttons then keep an invalid measure and their old width, and a retry that posted itself spun the UI thread at 90%.
            if (!_capsulePending) { _capsulePending = true; LayoutRetry.WhenReady(this, () => LayoutRetry.Laid(PageButton(CurrentPage)), () => { _capsulePending = false; PlacePageCapsule(); }); }
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
    // The page buttons take the header's button height, and the capsule under the chosen one takes it in the same step: PlacePageCapsule,
    // which RefreshStatus runs before ApplyHeader, would otherwise leave it at the size tier left behind until the next refresh.
    private void FitPageButtons(PanelHeader header)
    {
        foreach (var button in new[] { AIUsageButton, MonitorPageButton, MusicPageButton, ShelfPageButton, DevicesPageButton, NoticesPageButton, AgendaPageButton }) button.Height = header.ButtonHeight;
        PageCapsule.Height = header.ButtonHeight;
    }
    // The buttons stand in the order chosen in the settings; the first has no gap before it.
    private void OrderPageButtons()
    {
        if (AIUsageButton.Parent is not Panel buttons) return;
        var order = Settings.PageOrder.Select(PageButton).ToList();
        if (!buttons.Children.Cast<UIElement>().SequenceEqual(order))
        {
            buttons.Children.Clear();
            foreach (var button in order) buttons.Children.Add(button);
        }
        for (int i = 0; i < order.Count; i++) order[i].Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0);
    }
    // The clock stands in the middle of the panel. Where the page switch would reach it (seven pages on the narrowest panels), it
    // stands in the middle of the room between the switch and the settings button instead.
    private void PlaceClock()
    {
        double width = ContentCanvas.Width;
        if (!double.IsFinite(width)) return;
        double clock = ClockLabel.Text.Length > 0 ? TextWidth(ClockLabel.Text, 12, FontWeights.Normal) : 34;
        double? left = ClockLeft(width, clock);
        ClockLabel.HorizontalAlignment = left is null ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        ClockLabel.Margin = new Thickness(left ?? 0, 0, 0, 0);
    }
    // The clock's left edge when it cannot stand in the middle; null when it can.
    private double? ClockLeft(double width, double clock)
    {
        int pages = DeckPages.Visible(Settings).Count;
        if (pages == 0 || PageSwitch.Visibility != Visibility.Visible) return null;
        const double gap = 8;
        double switchRight = _header.Side + 2 * PanelHeader.SwitchPadding + pages * 28 + (pages - 1);
        if ((width - clock) / 2 >= switchRight + gap) return null;
        double gear = width - _header.RightMargin - PanelHeader.SettingsWidth;
        return Math.Round(switchRight + Math.Max(0, (gear - switchRight - clock) / 2));
    }
    // Where the clock ends on the right, wherever it stands.
    private double ClockRight(double width, double clock) => ClockLeft(width, clock) is { } left ? left + clock : (width + clock) / 2;
    private Button PageButton(DeckPage page) => page switch { DeckPage.Music => MusicPageButton, DeckPage.Monitor => MonitorPageButton, DeckPage.Clipboard => ShelfPageButton, DeckPage.Devices => DevicesPageButton, DeckPage.Notices => NoticesPageButton, DeckPage.Agenda => AgendaPageButton, _ => AIUsageButton };
}
