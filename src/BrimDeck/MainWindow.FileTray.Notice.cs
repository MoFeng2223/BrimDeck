using System.Windows;
using System.Windows.Controls;

namespace BrimDeck;

public partial class MainWindow
{
    private QuietNotice? _trayNotice;
    private void BuildTrayNotice(Grid host)
    {
        _trayNotice = new QuietNotice("shelf-notice");
        Panel.SetZIndex(_trayNotice, 4); host.Children.Add(_trayNotice);
        Closed += (_, _) => HideTrayNotice(); FitTrayNotice();
    }
    private void FitTrayNotice() => _trayNotice?.Fit(TrayContent.ActualWidth);
    private void ShowTrayNotice(string text)
    {
        if (_trayNotice is null || !IsTrayPage || !_expanded || string.IsNullOrWhiteSpace(text)) return;
        FitTrayNotice(); _trayNotice.Show(text);
    }
    private void HideTrayNotice(bool animate = false) => _trayNotice?.Hide(animate);
}
