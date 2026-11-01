using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Core;

internal static partial class Program
{
    private static void CheckPanelLayoutHost(App app, MainWindow window, DeckSettings settings, ClipboardHistory history, PanelPalette[] palettes, string directory)
    {
        settings.ClipboardLayoutVersion = ClipboardDockLayout.Version; settings.TrayLayoutVersion = ShelfDockLayout.Version;
        ClipboardDockLayout.Reset(settings); ShelfDockLayout.Reset(settings);
        settings.Clipboard.HeaderMode = ClipboardHeaderMode.Edge;
        settings.Tray.Placement = FileTrayPlacement.Floating;
        var now = DateTimeOffset.UtcNow;
        history.Restore(new ClipboardSnapshot { Items = Enumerable.Range(0, 24).Select(i => new ClipboardEntry
        {
            Id = "two-row-" + i, Content = ClipboardContent.FromText("Synthetic card " + i + "\nTwo complete rows without altering the saved strip size."), CopiedAt = now.AddMinutes(-i)
        }).ToList() }, settings.Clipboard, now);
        Set(window, "_page", DeckPage.Clipboard);
        Call(window, "RenderClipboardPage", history, (Action)(() => { }), (Action)(() => { }), (Func<(bool, bool, string)>)(() => (false, false, "")));
        var view = (ClipboardView)Get(window, "_clipboardView")!;
        // The fixture changes only presentation preferences; it never registers hotkeys, capture or settings watchers.
        Set(view, "_setGrid", (Action<bool>)(grid => { settings.Clipboard.GridView = grid; window.ApplySettings(); Call(window, "PrepareClipboardDock"); }));
        var toggle = (Button)Get(view, "_layout")!;
        var island = (IslandBorder)window.FindName("Island");
        foreach (int percent in new[] { 100, 150 })
        {
            settings.PanelScale = percent; settings.Clipboard.GridView = false; settings.Clipboard.HeaderMode = ClipboardHeaderMode.Edge;
            Palette(palettes[1]); window.ApplySettings(); Call(window, "PrepareClipboardDock"); window.Left = -20000; window.Top = -20000; window.SetExpanded(true, instant: true); Pump();
            double scale = percent / 100d;
            Check($"panel default {percent}: reference strip is 1200 by 210 before monitor fitting (saved={settings.ClipboardWidth}x{settings.ClipboardHeight}; view={view.ActualWidth}x{view.ActualHeight}; island={island.ActualWidth}x{island.ActualHeight}; page={Get(window, "_page")})", settings.ClipboardWidth == 1200 && settings.ClipboardHeight == 210
                && Near(view.ActualHeight, 210) && island.ActualWidth <= 1200 * scale + 1);
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            var list = All<ListBox>(view).Single(); var scroll = All<ScrollViewer>(list).First(); scroll.ScrollToTop(); Pump();
            var cards = All<ClipboardCard>(view).ToArray();
            var fullRows = cards.Where(card =>
            {
                double y = card.TranslatePoint(new Point(), scroll).Y;
                return y >= -1 && y + card.ActualHeight <= scroll.ActualHeight + 1;
            }).Select(card => Math.Round(card.TranslatePoint(new Point(), scroll).Y)).Distinct().ToArray();
            Check("panel grid " + percent + ": layout button expands the production host to two complete rows", settings.Clipboard.GridView && Near(view.ActualHeight, 366)
                && fullRows.Length == 2 && settings.ClipboardHeight == 210 && Near(island.ActualHeight, 366 * scale));
            Render(island, directory, "clipboard-two-rows-" + percent + ".png");
            settings.Clipboard.HeaderMode = ClipboardHeaderMode.Always; window.SetExpanded(true, instant: true); Pump();
            Check("panel grid " + percent + ": shared header adds room instead of clipping the second row", Near(island.ActualHeight, 402 * scale) && Near(view.ActualHeight, 366));
            settings.Clipboard.HeaderMode = ClipboardHeaderMode.Edge;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            Check("panel strip " + percent + ": leaving grid restores the saved single-row height", !settings.Clipboard.GridView && settings.ClipboardHeight == 210 && Near(view.ActualHeight, 210));
        }
        settings.PanelScale = 100; settings.Tray.Placement = FileTrayPlacement.Notch;
        Set(window, "_page", DeckPage.FileTray); window.ApplySettings(); window.Left = -20000; window.Top = -20000; window.SetExpanded(true, instant: true); Pump();
        Check("panel shelf: ApplySettings uses its own 240 by 120 reference, not the clipboard width", Near(island.ActualWidth, 240) && Near(((Grid)window.FindName("ContentCanvas")).ActualHeight, 120));
        Set(window, "_page", DeckPage.Clipboard); window.ApplySettings(); Call(window, "PrepareClipboardDock"); window.Left = -20000; window.Top = -20000; window.SetExpanded(true, instant: true); Pump();
        foreach (var kind in new[] { ClipboardContentKind.Text, ClipboardContentKind.Link, ClipboardContentKind.Color, ClipboardContentKind.Image, ClipboardContentKind.Files })
        {
            var entry = new ClipboardEntry { Id = "menu-" + kind, Content = new ClipboardContent { Kind = kind, Text = kind == ClipboardContentKind.Link ? "https://example.invalid/" : kind == ClipboardContentKind.Color ? "#123456" : "Synthetic text", Files = kind == ClipboardContentKind.Files ? ["synthetic.txt"] : [] } };
            var menu = (ContextMenu)typeof(ClipboardView).GetMethod("ItemMenu", Private)!.Invoke(view, [entry])!;
            Check("panel menu " + kind + ": every separator divides nonempty groups and uses the host style", menu.Items.Count > 0
                && menu.Items[0] is not Separator && menu.Items[^1] is not Separator
                && menu.Items.Cast<object>().Zip(menu.Items.Cast<object>().Skip(1)).All(pair => pair.First is not Separator || pair.Second is not Separator)
                && menu.Items.OfType<Separator>().All(separator => ReferenceEquals(separator.Style, view.FindResource("TrayMenuSeparator"))));
            if (kind == ClipboardContentKind.Text)
            {
                menu.PlacementTarget = view; menu.IsOpen = true; Pump();
                RenderMenu(island, menu, directory, "clipboard-text-menu-single-separators.png"); menu.IsOpen = false;
            }
        }
        Check("panel fixture: layout and menu navigation never opt into recording", !settings.Clipboard.Enabled);
    }
}
