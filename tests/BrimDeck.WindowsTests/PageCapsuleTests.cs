using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// Soft reads only this property. No real App startup, accounts or user settings are involved.
public sealed class App : Application
{
    public DeckSettings Settings { get; } = new();
}

public partial class MainWindow
{
    private DeckSettings Settings { get; } = new();
    private DeckPage CurrentPage { get; set; }
    private readonly Button AIUsageButton = new() { Content = "Usage", Width = 60, Height = 24 };
    private readonly Button MusicPageButton = new() { Content = "Music", Width = 60, Height = 24 };
    private readonly Button MonitorPageButton = new() { Content = "Monitor", Width = 60, Height = 24 };
    private readonly Button MiniAppsPageButton = new() { Content = "Mini apps", Width = 60, Height = 24 };
    private readonly Grid PageSwitch = new();
    private readonly Border PageCapsule = new() { Width = 60, Height = 24 };
    private readonly TranslateTransform PageCapsuleShift = new();
    private bool _expanded = true, _preview = false;

    // Links the actual production method, including its retry branch and button reorder, rather than testing the helper alone.
    internal static void CheckPageCapsule(Action<string, bool> check)
    {
        var window = new MainWindow { Width = 260, Height = 100, Left = -20000, Top = -20000,
            ShowActivated = false, WindowStyle = WindowStyle.None };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(window.AIUsageButton); buttons.Children.Add(window.MonitorPageButton); buttons.Children.Add(window.MusicPageButton); buttons.Children.Add(window.MiniAppsPageButton);
        window.PageCapsule.RenderTransform = window.PageCapsuleShift;
        window.PageSwitch.Children.Add(window.PageCapsule); window.PageSwitch.Children.Add(buttons);
        window.Content = window.PageSwitch;
        try
        {
            window.Show();
            PumpCapsule(() => window.PlacePageCapsule(), check);
            check("page capsule starts at the measured usage button", window.PageCapsule.Width == window.AIUsageButton.ActualWidth);

            // Usage details hide the whole switch; it retains the old ActualWidth after a button invalidates its measure.
            window.PageSwitch.Visibility = Visibility.Collapsed;
            window.AIUsageButton.Width = 80;
            PumpCapsule(() => { for (int i = 0; i < 20; i++) window.PlacePageCapsule(); }, check);
            check("production placement waits once while the switch is collapsed", window._capsulePending);

            // A page changed while the callback was waiting: it must use the latest page and its latest order, not the old button.
            window.CurrentPage = DeckPage.Monitor;
            window.Settings.PageOrder = [DeckPage.Music, DeckPage.Usage, DeckPage.Monitor];
            window.OrderPageButtons();
            window.MonitorPageButton.Width = 90;
            window.PageSwitch.Visibility = Visibility.Visible;
            PumpCapsule(() => window.PlacePageCapsule(), check);
            var at = window.MonitorPageButton.TranslatePoint(new Point(), window.PageSwitch).X;
            check("pending production placement uses the current page after reorder", !window._capsulePending &&
                (double)window.PageCapsule.GetAnimationBaseValue(WidthProperty) == window.MonitorPageButton.ActualWidth && Math.Abs((double)window.PageCapsuleShift.GetAnimationBaseValue(TranslateTransform.XProperty) - at) < .5);

            window.CurrentPage = DeckPage.MiniApps;
            window.Settings.PageOrder = [DeckPage.MiniApps, DeckPage.Monitor, DeckPage.Usage, DeckPage.Music];
            window.OrderPageButtons();
            PumpCapsule(() => window.PlacePageCapsule(), check);
            var miniAt = window.MiniAppsPageButton.TranslatePoint(new Point(), window.PageSwitch).X;
            check("mini apps use their own page capsule and reorder without duplicate buttons", buttons.Children.Count == 4 && ReferenceEquals(buttons.Children[0], window.MiniAppsPageButton)
                && Math.Abs((double)window.PageCapsuleShift.GetAnimationBaseValue(TranslateTransform.XProperty) - miniAt) < .5);
            var pluginButton = new Button { Content = "Plugin", Width = 45, Height = 24 };
            var moreButton = new Button { Content = "More", Width = 36, Height = 24 };
            window._navigationButtons = [pluginButton, window.AIUsageButton, moreButton]; window._navigationSelection = moreButton;
            window.OrderPageButtons(); PumpCapsule(() => window.PlacePageCapsule(), check);
            double moreAt = moreButton.TranslatePoint(new Point(), window.PageSwitch).X;
            check("an overflow page anchors its capsule to the more button", Math.Abs((double)window.PageCapsuleShift.GetAnimationBaseValue(TranslateTransform.XProperty) - moreAt) < .5 && buttons.Children.Count == 3);
            window._navigationButtons = null; window._navigationSelection = null;
            buttons.Children.Clear(); buttons.Children.Add(window.AIUsageButton); window.OrderPageButtons();
            window.CurrentPage = DeckPage.Monitor;

            // An invisible/retired panel must yield too, even though its controls keep their previous size.
            window.Hide(); window.MonitorPageButton.Width = 95;
            PumpCapsule(() => window.PlacePageCapsule(), check);
            check("hidden production window does not spin while waiting for layout", window._capsulePending);
        }
        finally { window.Close(); }
    }

    // The header changes size tier: the capsule follows the buttons' new drawn height at once (ApplyHeader), not at the next refresh.
    internal static void CheckPageCapsuleFit(Action<string, bool> check)
    {
        var window = new MainWindow { Width = 260, Height = 100, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(window.AIUsageButton); buttons.Children.Add(window.MonitorPageButton); buttons.Children.Add(window.MusicPageButton); buttons.Children.Add(window.MiniAppsPageButton);
        window.PageCapsule.RenderTransform = window.PageCapsuleShift;
        window.PageSwitch.Children.Add(window.PageCapsule); window.PageSwitch.Children.Add(buttons);
        window.Content = window.PageSwitch;
        try
        {
            window.Show();
            var tall = PanelHeader.For(36, 20); var short_ = PanelHeader.For(30, 20);
            window.FitPageButtons(tall); window.UpdateLayout(); window.PlacePageCapsule();
            check("page capsule is as tall as the buttons draw in the tall header", window.PageCapsule.Height == tall.ButtonHeight && tall.ButtonHeight == 24);

            // The header moves to the shorter tier: the capsule follows the buttons at once, and a placement before the next layout
            // pass (RefreshStatus runs PlacePageCapsule before ApplyHeader) keeps it there, though ActualHeight is still the old tier's.
            window.FitPageButtons(short_);
            check("the capsule takes the new tier's height as the buttons change", window.PageCapsule.Height == short_.ButtonHeight && short_.ButtonHeight != tall.ButtonHeight);
            check("mini apps follow the same header height as built-in pages", window.MiniAppsPageButton.Height == short_.ButtonHeight);
            check("a button's ActualHeight before the next layout pass is still the old tier's", window.AIUsageButton.ActualHeight == tall.ButtonHeight);
            window.PlacePageCapsule();
            check("a placement before the next layout pass keeps the new tier's height", window.PageCapsule.Height == short_.ButtonHeight);
            window.UpdateLayout(); window.PlacePageCapsule();
            check("after layout the capsule and the buttons agree", window.PageCapsule.Height == window.AIUsageButton.ActualHeight);
        }
        finally { window.Close(); }
    }

    private static void PumpCapsule(Action arrange, Action<string, bool> check)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        int loadedPosts = 0;
        bool inputRan = false, backgroundRan = false, stopped = false, timedOut = false;
        DispatcherHookEventHandler count = (_, e) =>
        {
            if (e.Operation.Priority != DispatcherPriority.Loaded || ++loadedPosts <= 32 || stopped) return;
            stopped = true;
            dispatcher.BeginInvoke(DispatcherPriority.Send, () => frame.Continue = false);
        };
        dispatcher.Hooks.OperationPosted += count;
        // Runs outside the UI thread. An old Loaded-priority retry can starve DispatcherTimer, Input and ApplicationIdle.
        using var watchdog = new System.Threading.Timer(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        { timedOut = true; frame.Continue = false; }), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        try
        {
            arrange();
            dispatcher.BeginInvoke(DispatcherPriority.Input, () => inputRan = true);
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => backgroundRan = true);
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
        finally { dispatcher.Hooks.OperationPosted -= count; }
        check("production page placement has a bounded Loaded queue", !stopped && loadedPosts <= 32);
        check("production page placement yields to input and settings/save-priority work", !timedOut && inputRan && backgroundRan);
    }
}