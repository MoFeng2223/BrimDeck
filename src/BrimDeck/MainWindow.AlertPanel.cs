using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private bool _alertOpenedPanel;
    private GridLength _toolbarRowRest = new(36);

    // Opt-in expanded warning panel (DeckSettings.ExpandedQuotaAlerts); line style always keeps the collapsed notice.
    private bool AlertOpensPanel => Settings.ExpandedQuotaAlerts && EffectiveStyle != CompactStyle.Line && !IsSuppressed;

    private void ShowAlertPanel(CompactAlertInfo alert)
    {
        AlertContent.Children.Clear();
        FrameworkElement card;
        if (alert.Device is { } device)
        {
            var inks = new DeviceInks(MonitorThemeColors.For(Settings, Look), Settings.DeviceChoices, Settings.DeviceWarningPercent, Settings.DeviceLowPercent, Look);
            card = AlertPanel.ForDevice(Look, device, inks, alert.Detail ?? alert.Label.Trim());
        }
        else
        {
            string detail = alert.Detail ?? (Settings.QuotaRingRemaining
                ? Loc.T($"{alert.Label}剩余 {100 - alert.Used:0}%", $"{alert.Label}{100 - alert.Used:0}% left")
                : Loc.T($"{alert.Label}已用 {alert.Used:0}%", $"{alert.Label}{alert.Used:0}% used"));
            card = AlertPanel.ForQuota(Look, alert.Name, detail.Trim(), alert.Used, Look.QuotaTheme(alert.Entry), Settings.QuotaRingRemaining);
        }
        AlertContent.Children.Add(card);
        ApplyAlertChrome(true);
    }

    private void HideAlertPanel()
    {
        if (AlertContent.Visibility != Visibility.Visible && AlertContent.Children.Count == 0) return;
        AlertContent.Children.Clear();
        ApplyAlertChrome(false);
    }

    private void ApplyAlertChrome(bool alerting)
    {
        if (alerting)
        {
            if (ToolbarRow.Height.Value > 0) _toolbarRowRest = ToolbarRow.Height;
            ToolbarRow.Height = new GridLength(0);
            Toolbar.Visibility = Visibility.Collapsed;
            AlertContent.Visibility = Visibility.Visible;
            UsageContent.Visibility = Visibility.Collapsed;
            MusicContent.Visibility = Visibility.Collapsed;
            MonitorContent.Visibility = Visibility.Collapsed;
            ShelfContent.Visibility = Visibility.Collapsed;
            DevicesContent.Visibility = Visibility.Collapsed;
        }
        else
        {
            ToolbarRow.Height = _toolbarRowRest.Value > 0 ? _toolbarRowRest : new GridLength(36);
            Toolbar.Visibility = Visibility.Visible;
            AlertContent.Visibility = Visibility.Collapsed;
            if (_expanded) ShowPage(CurrentPage, animatePage: false);
            else
            {
                // Do not call ShowPage while the panel is collapsed; only restore which page will show on the next open.
                UsageContent.Visibility = Visibility.Collapsed;
                MusicContent.Visibility = Visibility.Collapsed;
                MonitorContent.Visibility = Visibility.Collapsed;
                ShelfContent.Visibility = Visibility.Collapsed;
                DevicesContent.Visibility = Visibility.Collapsed;
                PageContent(CurrentPage).Visibility = Visibility.Visible;
            }
        }
    }

    // Page size while a warning panel is open (no toolbar row).
    private (double Width, double Height) AlertPageSize => (AlertPanel.PanelWidth, AlertPanel.PanelHeight);
}
