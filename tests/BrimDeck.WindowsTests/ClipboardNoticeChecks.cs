using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

internal static partial class ClipboardChecks
{
    private static void CheckClipboardNotice(ClipboardView view, ListBox list, Window window, Action<string, bool> check)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var app = (App)Application.Current; bool animations = app.Settings.Animations; double width = window.Width;
        void Show(string message) => typeof(ClipboardView).GetMethod("ShowNotice", hidden)!.Invoke(view, [message]);
        var notice = Descendants<Border>(view).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-notice");
        var text = (TextBlock)notice.Child;
        var timer = ((QuietNotice)notice).Timer;
        var state = view.CapturePresentation(); var source = list.ItemsSource;
        try
        {
            app.Settings.Animations = false; Show("Synthetic move completed; entry kept in history"); Pump();
            check("clipboard notice: result appears in a non-interactive bottom-right capsule for three seconds", notice.IsVisible && !notice.IsHitTestVisible && timer.IsEnabled && timer.Interval == TimeSpan.FromSeconds(3)
                && notice.HorizontalAlignment == HorizontalAlignment.Right && notice.VerticalAlignment == VerticalAlignment.Bottom);
            double right = notice.TranslatePoint(new Point(), view).X + notice.ActualWidth;
            check("clipboard notice: result stays inside panel gutters without taking a list row", Math.Abs(right - (view.ActualWidth - 14)) <= 1 && Grid.GetRow(notice) == 3 && notice.ActualHeight < 60);
            WaitClipboardMotion(2100); view.Refresh(); Pump();
            check("clipboard notice: routine refresh does not dismiss a fresh message or restart its timeout", notice.IsVisible && text.Text.StartsWith("Synthetic move", StringComparison.Ordinal));
            WaitClipboardMotion(1050);
            check("clipboard notice: elapsed result disappears and clears its stored text", notice.Visibility == Visibility.Collapsed && text.Text.Length == 0 && !timer.IsEnabled);
            Show("First synthetic result"); WaitClipboardMotion(1600); Show("Latest synthetic result"); WaitClipboardMotion(1600);
            check("clipboard notice: newer result replaces the old one with a fresh three-second lifetime", notice.IsVisible && text.Text == "Latest synthetic result");
            WaitClipboardMotion(1550);
            check("clipboard notice: replaced result also expires instead of becoming permanent", notice.Visibility == Visibility.Collapsed && text.Text.Length == 0);
            window.Width = 440; Pump(); Show(new string('W', 160)); Pump();
            check("clipboard notice: narrow panel limits long result to its available width", notice.ActualWidth <= view.ActualWidth - 28 + 1 && notice.TranslatePoint(new Point(), view).X >= 13);
            view.Visibility = Visibility.Collapsed; Pump(); view.Visibility = Visibility.Visible; Pump();
            check("clipboard notice: hiding and reopening does not replay a stale result", notice.Visibility == Visibility.Collapsed && !timer.IsEnabled && text.Text.Length == 0);
            app.Settings.Animations = true; Show("Animated synthetic result");
            typeof(ClipboardView).GetMethod("HideNotice", hidden)!.Invoke(view, [true]); Show("Replacement during exit");
            WaitClipboardMotion(Soft.Quick + 80);
            check("clipboard notice: replaced notice survives an older exit callback", notice.IsVisible && text.Text == "Replacement during exit");
            check("clipboard notice: feedback preserves history source and browsing state", ReferenceEquals(source, list.ItemsSource) && view.CapturePresentation() == state);
        }
        finally { typeof(ClipboardView).GetMethod("HideNotice", hidden)!.Invoke(view, [false]); app.Settings.Animations = animations; window.Width = width; Pump(); }
    }
}
