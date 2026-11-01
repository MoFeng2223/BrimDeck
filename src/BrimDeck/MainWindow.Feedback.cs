using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BrimDeck;

public partial class MainWindow
{
    private QuietNotice? _moduleNotice;
    private string _pendingModuleFeedback = "";
    internal void ShowModuleFeedback(string key, string message)
    {
        if (!IsVisible || IsSuppressed) { _pendingModuleFeedback = message; return; }
        if (_expanded && IsClipboardPage && _clipboardView is not null) { _clipboardView.ShowFeedback(message); return; }
        if (_expanded && IsTrayPage && _trayNotice is not null) { ShowTrayNotice(message); return; }
        _moduleNotice ??= CreateModuleNotice();
        _moduleNotice.SetValue(FrameworkElement.ToolTipProperty, null);
        _moduleNotice.Show(message);
    }
    private QuietNotice CreateModuleNotice()
    {
        var notice = new QuietNotice("module-feedback") { Margin = new Thickness(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxHeight = 24 };
        notice.Padding = new Thickness(7, 2, 7, 2);
        // A compact status replaces content temporarily without changing the island's geometry or activating a window.
        Panel.SetZIndex(notice, 80); ((Panel)Island.Child).Children.Add(notice);
        notice.MaxWidth = Math.Max(0, Island.ActualWidth - 16);
        Island.SizeChanged += (_, _) => notice.MaxWidth = Math.Max(0, Island.ActualWidth - 16);
        Closed += (_, _) => notice.Hide();
        return notice;
    }
    private void ReplayModuleFeedback()
    {
        _moduleNotice?.Hide();
        if (_pendingModuleFeedback.Length == 0) return;
        string message = _pendingModuleFeedback; _pendingModuleFeedback = "";
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (_expanded) ShowModuleFeedback("pending", message); });
    }
}
