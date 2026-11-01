using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BrimDeck;

public partial class MainWindow
{
    private Border? _trayNotice;
    private TextBlock? _trayNoticeText;
    private readonly DispatcherTimer _trayNoticeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int _trayNoticeGeneration;
    private void BuildTrayNotice(Grid host)
    {
        _trayNoticeText = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 36 };
        _trayNoticeText.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary");
        _trayNotice = new Border { Child = _trayNoticeText, Margin = new Thickness(14, 0, 14, 12), Padding = new Thickness(12, 6, 12, 6), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _trayNotice.SetResourceReference(Border.BackgroundProperty, "PanelPopup"); _trayNotice.SetResourceReference(Border.BorderBrushProperty, "PanelPopupBorder"); _trayNotice.SetResourceReference(Border.CornerRadiusProperty, "PanelR16");
        AutomationProperties.SetAutomationId(_trayNotice, "shelf-notice"); AutomationProperties.SetLiveSetting(_trayNoticeText, AutomationLiveSetting.Polite);
        Panel.SetZIndex(_trayNotice, 4); host.Children.Add(_trayNotice);
        _trayNoticeTimer.Tick += (_, _) => HideTrayNotice(animate: true);
        Closed += (_, _) => HideTrayNotice(); FitTrayNotice();
    }
    private void FitTrayNotice()
    {
        if (_trayNotice is not null) _trayNotice.MaxWidth = Math.Max(0, Math.Min(520, TrayContent.ActualWidth - 28));
    }
    private void ShowTrayNotice(string text)
    {
        if (_trayNotice is null || !IsTrayPage || !_expanded || string.IsNullOrWhiteSpace(text)) return;
        ++_trayNoticeGeneration; _trayNoticeTimer.Stop(); _trayNotice.BeginAnimation(OpacityProperty, null); _trayNotice.Opacity = 1; _trayNotice.RenderTransform = Transform.Identity;
        _trayNoticeText!.Text = text; FitTrayNotice(); _trayNotice.Visibility = Visibility.Visible; Soft.Enter(_trayNotice, 4); _trayNoticeTimer.Start();
    }
    private void HideTrayNotice(bool animate = false)
    {
        _trayNoticeTimer.Stop(); int generation = ++_trayNoticeGeneration;
        if (_trayNotice is null) return;
        void Clear()
        {
            if (generation != _trayNoticeGeneration) return;
            _trayNotice.Visibility = Visibility.Collapsed; _trayNoticeText!.Text = "";
            _trayNotice.BeginAnimation(OpacityProperty, null); _trayNotice.Opacity = 1; _trayNotice.RenderTransform = Transform.Identity;
        }
        if (animate && Soft.For(TrayContent) && _trayNotice.Visibility == Visibility.Visible) Soft.Leave(_trayNotice, Clear); else Clear();
    }
}
