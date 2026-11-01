using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BrimDeck;

internal sealed partial class ClipboardView
{
    private readonly TextBlock _noticeText = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 36 };
    private readonly Border _notice = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(14, 0, 14, 12), Padding = new Thickness(12, 6, 12, 6), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int _noticeGeneration;
    private string _lastNoticeStatus = "";

    private void BuildNotice()
    {
        _notice.SetResourceReference(Border.BackgroundProperty, "PanelPopup");
        _notice.SetResourceReference(Border.BorderBrushProperty, "PanelPopupBorder");
        _notice.SetResourceReference(Border.CornerRadiusProperty, "PanelR16");
        _noticeText.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary");
        _notice.Child = _noticeText;
        AutomationProperties.SetAutomationId(_notice, "clipboard-notice");
        AutomationProperties.SetLiveSetting(_noticeText, AutomationLiveSetting.Polite);
        Grid.SetRow(_notice, 3); Panel.SetZIndex(_notice, 4); Children.Add(_notice);
        _noticeTimer.Tick += (_, _) => HideNotice(animate: true);
        SizeChanged += (_, _) => FitNotice(); FitNotice();
    }
    private void FitNotice() => _notice.MaxWidth = Math.Max(0, Math.Min(520, ActualWidth - _notice.Margin.Left - _notice.Margin.Right));

    private void ShowNotice(string text)
    {
        if (_disposed || !IsVisible || string.IsNullOrWhiteSpace(text)) return;
        ++_noticeGeneration; _noticeTimer.Stop();
        _notice.BeginAnimation(OpacityProperty, null); _notice.Opacity = 1;
        _notice.RenderTransform = Transform.Identity;
        _noticeText.Text = text; FitNotice(); _notice.Visibility = Visibility.Visible;
        Soft.Enter(_notice, 4); _noticeTimer.Start();
    }
    private void HideNotice(bool animate = false)
    {
        _noticeTimer.Stop(); int generation = ++_noticeGeneration;
        void Clear()
        {
            // A new result can replace this notice while its exit is still playing.
            if (generation != _noticeGeneration) return;
            _notice.Visibility = Visibility.Collapsed; _noticeText.Text = "";
            _notice.BeginAnimation(OpacityProperty, null); _notice.Opacity = 1; _notice.RenderTransform = Transform.Identity;
        }
        if (animate && Soft.Enabled && _notice.Visibility == Visibility.Visible) Soft.Leave(_notice, Clear);
        else Clear();
    }
}
