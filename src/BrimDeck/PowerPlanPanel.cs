using System.Windows;
using BrimDeck.Core;

namespace BrimDeck;

// The power plan switcher page (DeckPage.PowerPlans): shows Windows power schemes and switches the active one on click.
public partial class MainWindow
{
    private PowerPlanView? _powerPlanView;
    private string _powerPlanKey = "";

    private void InitializePowerPlans() { }

    private string PowerPlanKey() => string.Join("|", Loc.Language, Look.Generation, SystemParameters.HighContrast);

    private void RenderPowerPlans()
    {
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed;
        MusicContent.Visibility = Visibility.Collapsed;
        MonitorContent.Visibility = Visibility.Collapsed;
        ShelfContent.Visibility = Visibility.Collapsed;
        DevicesContent.Visibility = Visibility.Collapsed;
        PowerPlanContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed;
        ApplicationPages.Visibility = Visibility.Collapsed;
        ClockLabel.Visibility = Visibility.Visible;

        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        ApplyPageHeader(PanelHeader.ForPanel(height));
        PowerPlanContent.Margin = new Thickness(_header.ContentSide, 0, _header.ContentSide, MonitorBottom(height));

        var key = PowerPlanKey();
        if (_powerPlanView is not null && key == _powerPlanKey && PowerPlanContent.Children.Contains(_powerPlanView))
        {
            _powerPlanView.RefreshPlans();
            return;
        }
        _powerPlanKey = key;
        _powerPlanView = new PowerPlanView();
        PowerPlanContent.Children.Clear();
        PowerPlanContent.Children.Add(_powerPlanView);
        _powerPlanView.RefreshPlans();
    }
}
