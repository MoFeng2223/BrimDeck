using System.Windows;
using System.Windows.Controls;

namespace BrimDeck;

internal sealed partial class ClipboardView
{
    private readonly QuietNotice _notice = new("clipboard-notice");
    private string _lastNoticeStatus = "";
    private void BuildNotice()
    {
        Grid.SetRow(_notice, 3); Panel.SetZIndex(_notice, 4); Children.Add(_notice);
        SizeChanged += (_, _) => FitNotice(); FitNotice();
    }
    private void FitNotice() => _notice.Fit(ActualWidth);
    internal void ShowFeedback(string text) => ShowNotice(text);
    private void ShowNotice(string text)
    {
        if (_disposed || !IsVisible || string.IsNullOrWhiteSpace(text)) return;
        FitNotice(); _notice.Show(text);
    }
    private void HideNotice(bool animate = false) => _notice.Hide(animate);
}
