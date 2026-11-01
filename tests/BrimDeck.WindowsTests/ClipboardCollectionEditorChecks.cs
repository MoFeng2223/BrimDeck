using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Core;

internal static partial class ClipboardChecks
{
    internal static void RunCollections(Action<string, bool> check, string? renderDirectory = null)
    {
        PrepareClipboardResources(); ClipboardHistoryTests.Run(check);
        var settings = new ClipboardSettings { RetentionDays = 0, Enabled = false };
        var history = new ClipboardHistory(); var now = DateTimeOffset.UtcNow;
        history.Add(ClipboardContent.FromText("Synthetic collection editor fixture"), "Fixture", "", now, settings);
        history.Add(ClipboardContent.FromText("https://example.invalid/collection"), "Fixture", "", now.AddMinutes(-1), settings);
        history.Add(ClipboardContent.FromText("Synthetic reply fixture"), "Fixture", "", now.AddMinutes(-2), settings);
        history.Add(ClipboardContent.FromText("#0080FF"), "Fixture", "", now.AddMinutes(-3), settings);
        history.Add(new ClipboardContent { Kind = ClipboardContentKind.Image, Image = Image(), PixelWidth = 400, PixelHeight = 300 }, "Fixture", "", now.AddMinutes(-4), settings);
        int activations = 0;
        using var view = new ClipboardView(history, () => settings, (_, _) => activations++, () => { }, () => { }, () => { }, () => (false, false, ""), grid => settings.GridView = grid, () => { }, confirm: _ => true);
        var host = new Grid { LayoutTransform = new ScaleTransform(1.5, 1.5) }; host.Children.Add(view);
        var window = new Window { Content = host, Width = 1620, Height = 315, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        bool confirm = true;
        var library = new ClipboardLibraryWindow(history, (_, _) => activations++, () => (false, false, ""), () => { }, () => { }, _ => confirm, () => settings)
        { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        void Theme(string id)
        {
            var palette = PanelThemes.Build(id, PanelThemes.Find(id, true, []).File);
            foreach (var (resource, token) in new[] { ("PanelSurface", "surface"), ("PanelPrimary", "textPrimary"), ("PanelSecondary", "textSecondary"),
                ("PanelTertiary", "textTertiary"), ("PanelAccent", "accent"), ("PanelFill", "fill"), ("PanelFillSelected", "fillSelected"), ("PanelTrack", "track"),
                ("PanelHover", "hover"), ("PanelPressed", "pressed"), ("PanelSwitchFill", "switchFill"), ("PanelPopup", "popup"), ("PanelPopupBorder", "popupBorder") })
            { var color = palette[token]; Application.Current.Resources[resource] = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)); }
            for (int radius = 1; radius <= 16; radius++) Application.Current.Resources["PanelR" + radius] = new CornerRadius(radius);
        }
        try
        {
            Theme(PanelThemes.Dark); window.Show(); library.Show(); Pump();
            CheckClipboardOrganization(history, view, library, check, value => confirm = value, renderDirectory);
            CheckClipboardAppearance(history, view, library, check, Theme, renderDirectory);
            CheckCollectionRibbon(history, view, check);
            check("collection editor: isolated operations never activate content, enable capture or change record count", activations == 0 && !settings.Enabled && history.Items.Count == 5);
            Console.WriteLine("COLLECTION EDITOR RESULT: isolated collection names, appearance and ribbon checks completed");
        }
        finally { view.ClosePopups(); library.ForceClose(); window.Close(); }
    }

    private static void CheckCollectionRibbon(ClipboardHistory history, ClipboardView view, Action<string, bool> check)
    {
        var snapshot = history.Snapshot(); int changes = 0;
        void Changed() => changes++;
        history.Changed += Changed;
        Popup? popup = null;
        try
        {
            var anchor = Descendants<Button>(view).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-collection-prompts");
            popup = ClipboardCollectionAppearance.Open(anchor, history, "prompts", 1.5)!; Pump();
            var root = popup.Child;
            Button Button(string id) => Descendants<Button>(root).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-appearance-" + id);
            TextBox Field(string id) => Descendants<TextBox>(root).Single(field => AutomationProperties.GetAutomationId(field) == "clipboard-appearance-" + id);
            void Click(string id) { Button(id).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); }
            var editor = Descendants<ClipboardCollectionAppearance>(root).Single();
            var ribbon = Descendants<ClipboardCollectionColorRibbon>(root).Single();
            var scroll = Descendants<ScrollViewer>(ribbon).Single();
            void Press(Key key) { ribbon.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(ribbon)!, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent }); Pump(); }
            check("collection ribbon: compact panel starts with only name, swatches and actions", editor.ActualHeight <= 160 && Field("name").IsVisible && !Field("color").IsVisible && Button("more").IsVisible
                && ribbon.ActualWidth >= 260 && scroll.ScrollableWidth > ribbon.ActualWidth * 5 && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Hidden);
            Press(Key.End); double end = scroll.HorizontalOffset; Press(Key.Home);
            check("collection ribbon: keyboard browses both ends without selecting or committing", end > 2500 && scroll.HorizontalOffset == 0 && Field("color").Text == "" && changes == 0);
            var before = scroll.HorizontalOffset;
            ribbon.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent }); Pump();
            check("collection ribbon: wheel reveals a new color interval but leaves the current draft alone", scroll.HorizontalOffset > before && changes == 0 && ribbon.SelectedColor == "");
            Press(Key.PageDown); Press(Key.Enter);
            string color = Field("color").Text;
            check("collection ribbon: Enter explicitly chooses the visible center swatch into the local draft", color.Length == 7 && ClipboardCollection.NormalizeColor(color) == color && ribbon.SelectedColor == color
                && history.Collections.Single(group => group.Id == "prompts").BackgroundColor == "" && changes == 0);
            Press(Key.End); Press(Key.Left); Press(Key.PageUp);
            check("collection ribbon: browsing after selection retains the chosen color", Field("color").Text == color && ribbon.SelectedColor == color && changes == 0);
            var swatch = Descendants<Button>(ribbon).First(button => AutomationProperties.GetName(button) == "#FF8000");
            swatch.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            check("collection ribbon: swatch invocation selects exact color without writing history", Field("color").Text == "#FF8000" && ribbon.SelectedColor == "#FF8000" && changes == 0);
            Field("name").Text = "Updated synthetic collection"; Click("more"); Field("color").Text = "#123456"; Click("icon-star");
            check("collection ribbon: custom HEX remains exact even between palette swatches", ribbon.SelectedColor == "#123456" && ClipboardCollectionVisuals.GetColor(Button("preview")) == "#123456" && changes == 0);
            Field("name").Text = "Assets"; Click("save");
            check("collection ribbon: duplicate name cannot partially apply appearance", popup.IsOpen && changes == 0 && history.Collections.Single(group => group.Id == "prompts") is { Name: "Prompts", BackgroundColor: "", Icon: "" });
            Field("name").Text = "Updated synthetic collection"; Field("color").Text = "invalid"; Click("save");
            check("collection ribbon: invalid color cannot partially rename", popup.IsOpen && changes == 0 && history.Collections.Single(group => group.Id == "prompts").Name == "Prompts");
            Field("color").Text = "#123456"; Click("save");
            check("collection ribbon: one Save commits name, custom color and icon in one notification", !popup.IsOpen && changes == 1
                && history.Collections.Single(group => group.Id == "prompts") is { Name: "Updated synthetic collection", BackgroundColor: "#123456", Icon: "star" });
        }
        finally { if (popup is not null) popup.IsOpen = false; history.Changed -= Changed; history.Restore(snapshot, new ClipboardSettings { RetentionDays = 0 }, DateTimeOffset.UtcNow); view.QueryState = new(); Pump(); }
    }
}
