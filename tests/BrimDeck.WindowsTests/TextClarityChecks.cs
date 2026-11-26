using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;

// A pop-up's text takes the ClearType hint and the grayscale text of what it opens from, down to text inside a scroll view, where a
// hint on the pop-up's frame alone would not reach: TextClarity relies on WPF passing a pop-up what its target inherits. The
// windows lie off the screen and are not activated.
internal static class TextClarityChecks
{
    private static void Settle() { for (int i = 0; i < 3; i++) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }

    // Opens a pop-up from target with text inside a scroll view, as a menu's list holds it, and returns that text once it is shown.
    private static (Popup Popup, TextBlock Text) Open(FrameworkElement target)
    {
        var text = new TextBlock { Text = "清晰 Clear" };
        var frame = new Border { Background = Brushes.Black, Child = new ScrollViewer { Content = text, Height = 40 } };
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = true, AllowsTransparency = true, Child = frame, IsOpen = true };
        Settle();
        return (popup, text);
    }

    private static Window Show(UIElement content, bool layered)
    {
        var window = new Window { Left = -10000, Top = -10000, Width = 300, Height = 200, ShowInTaskbar = false, ShowActivated = false, Content = content };
        if (layered) { window.WindowStyle = WindowStyle.None; window.AllowsTransparency = true; window.Background = Brushes.Transparent; }
        window.Show();
        Settle();
        return window;
    }

    public static void Run(Action<string, bool> check)
    {
        var windows = new List<Window>();
        var popups = new List<Popup>();
        try
        {
            // An ordinary window, framed as the settings and the dialogs are.
            var settingsButton = new Button { Content = "s", Width = 60, Height = 24 };
            var settings = new Window { Left = -10000, Top = -10000, Width = 300, Height = 200, ShowInTaskbar = false, ShowActivated = false, Content = settingsButton };
            BrimDeck.Native.SettingsChrome.Apply(settings);
            settings.Show(); Settle();
            windows.Add(settings);
            var (fromSettings, settingsText) = Open(settingsButton);
            popups.Add(fromSettings);
            check("text clarity: a pop-up from an ordinary window gives the text in its scroll view ClearType",
                RenderOptions.GetClearTypeHint(settingsText) == ClearTypeHint.Enabled);

            var panelButton = new Button { Content = "p", Width = 60, Height = 24 };
            var island = new Grid { Background = Brushes.Black, Children = { panelButton } };
            TextClarity.SetClearType(island, true);
            windows.Add(Show(island, layered: true));
            var (fromPanel, panelText) = Open(panelButton);
            popups.Add(fromPanel);
            check("text clarity: a pop-up from the opaque panel gives the text in its scroll view ClearType",
                RenderOptions.GetClearTypeHint(panelText) == ClearTypeHint.Enabled && TextOptions.GetTextRenderingMode(panelText) == TextRenderingMode.Auto);

            TextOptions.SetTextRenderingMode(island, TextRenderingMode.Grayscale);
            Settle();
            check("text clarity: over a see-through panel the pop-up's text turns grayscale",
                TextOptions.GetTextRenderingMode(panelText) == TextRenderingMode.Grayscale);

            var bareButton = new Button { Content = "b", Width = 60, Height = 24 };
            windows.Add(Show(new Grid { Children = { bareButton } }, layered: true));
            var (fromBare, bareText) = Open(bareButton);
            popups.Add(fromBare);
            check("text clarity: a pop-up from a window without TextClarity keeps WPF's own text",
                RenderOptions.GetClearTypeHint(bareText) == ClearTypeHint.Auto);
        }
        finally
        {
            foreach (var popup in popups) popup.IsOpen = false;
            foreach (var window in windows) window.Close();
        }
    }
}
