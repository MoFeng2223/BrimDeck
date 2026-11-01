using System.Collections;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using DropShelf;
using DropShelf.Models;
using DropShelf.Services;

internal static partial class Program
{
    // No AppController.Start, clipboard calls, hooks, real history, or external receivers run in this production-host fixture.
    private static void CheckShelfDockHost(App app, MainWindow window, DeckSettings settings, PanelPalette[] palettes, string scratch, string directory)
    {
        CheckShelfMigration();
        using var controller = new AppController(Dispatcher.CurrentDispatcher, new EmbeddedHostOptions
        {
            DataDirectory = Path.Combine(scratch, "shelves"), CacheDirectory = Path.Combine(scratch, "attachments"), AnimationsEnabled = () => settings.Animations,
            Exit = () => { }, GetStartWithWindows = () => false, SetStartWithWindows = _ => true,
        });
        using var tray = new ShelfTrayAdapter(controller.Shelves);
        Set(app, "<Shelves>k__BackingField", controller); Set(app, "<ShelfTray>k__BackingField", tray);
        tray.Changed += window.TrayChanged;
        var model = tray.Model;
        string ordinary = Path.Combine(scratch, "original-report.txt"), imagePath = Path.Combine(scratch, "synthetic-image.png"), folder = Path.Combine(scratch, "Assets");
        File.WriteAllText(ordinary, "Synthetic original retained at its actual path."); File.WriteAllBytes(imagePath, Image()); Directory.CreateDirectory(folder);
        var bitmap = BitmapFrame.Create(new MemoryStream(File.ReadAllBytes(imagePath)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); bitmap.Freeze();
        var image = new ShelfItem { Id = "shelf-image", Kind = ShelfItemKind.File, Value = imagePath, Thumbnail = bitmap, SizeBytes = new FileInfo(imagePath).Length };
        model.Items.Add(new ShelfItem { Id = "shelf-file", Kind = ShelfItemKind.File, Value = ordinary, SizeBytes = 128 }); model.Items.Add(image);
        model.Items.Add(new ShelfItem { Id = "shelf-folder", Kind = ShelfItemKind.Folder, Value = folder });
        model.Items.Add(new ShelfItem { Id = "shelf-link", Kind = ShelfItemKind.Link, Value = "https://example.invalid/design/review", Title = "Synthetic design review" });
        model.Items.Add(new ShelfItem { Id = "shelf-missing", Kind = ShelfItemKind.File, Value = Path.Combine(scratch, "missing.txt"), IsAvailable = false });
        for (int i = 0; i < 160; i++) model.Items.Add(new ShelfItem { Id = "shelf-note-" + i, Kind = ShelfItemKind.Text, Value = "Synthetic shelf note " + i + "\nA shared model with one horizontal row and quiet metadata." });
        int writes = 0; tray.PersistedChanged += () => writes++;
        settings.Tray.Placement = FileTrayPlacement.Both; settings.Tray.HeaderMode = ClipboardHeaderMode.Edge; settings.Clipboard.HeaderMode = ClipboardHeaderMode.Always;
        settings.TrayHeight = 210; Set(window, "_page", DeckPage.FileTray);
        Set(window, "<MonitorBoundsDip>k__BackingField", new DipRect(0, 0, 2400, 1400)); Set(window, "<WorkDip>k__BackingField", new DipRect(0, 0, 2400, 1352));
        window.Width = 2400; window.Height = 1000;
        var island = (IslandBorder)window.FindName("Island"); var content = (Grid)window.FindName("TrayContent");
        void Show(double width, double height = 210, bool grid = false)
        {
            settings.Tray.GridView = grid; settings.TrayHeight = height;
            Set(window, "_pageWidth", width); Set(window, "_widestPage", width);
            window.SetExpanded(true, instant: true); Call(window, "RenderFileTray"); Pump(); window.UpdateLayout(); Pump();
        }
        Button Button(string id) => All<Button>(content).Single(button => AutomationProperties.GetAutomationId(button) == id);
        List<ShelfItem> Results() => (List<ShelfItem>)Get(window, "_trayResults")!;
        bool Shortcut(Key key, ModifierKeys modifiers = ModifierKeys.None) => (bool)window.GetType().GetMethod("HandleTrayShortcut", Private)!.Invoke(window, [key, modifiers, null])!;
        try
        {
            foreach (var palette in palettes)
            foreach (int percent in new[] { 100, 150 })
            foreach (double width in new[] { 1080d, 440d })
            {
                Palette(palette); settings.PanelScale = percent; Call(window, "ApplyScale"); Call(window, "ApplyTheme"); Show(width);
                string scenario = palette.Id + "-" + percent + "-" + width;
                var header = (Grid)Get(window, "_trayBar")!; var scroll = (ScrollViewer)Get(window, "_trayScroll")!;
                scroll.ScrollToHorizontalOffset(0); Pump();
                var cards = All<ShelfIslandCard>(content).OrderBy(card => card.TranslatePoint(new Point(), content).X).ToArray();
                var types = All<Button>(header).Where(button => AutomationProperties.GetAutomationId(button).StartsWith("shelf-type-", StringComparison.Ordinal)).ToArray();
                Check("shelf " + scenario + ": production island shares clipboard reference geometry", Near(island.ActualWidth, width * percent / 100d) && Near(island.ActualHeight, 210 * percent / 100d) && Near(header.ActualHeight, 40) && Near(content.ActualHeight, 210));
                Check("shelf " + scenario + ": all six filters and essential actions fit one header", types.Length == 6 && types.All(button => button.IsVisible && button.TranslatePoint(new Point(), content).X >= 0)
                    && new[] { "shelf-pinned", "shelf-more", "shelf-settings", "shelf-search-toggle" }.All(id => { var button = Button(id); double x = button.TranslatePoint(new Point(), content).X; return button.IsVisible && x >= 0 && x + button.ActualWidth <= width + 1; }));
                Check("shelf " + scenario + ": hidden shared header leaves a single forty-DIP toolbar", ((RowDefinition)window.FindName("ToolbarRow")).ActualHeight == 0 && !((FrameworkElement)window.FindName("Toolbar")).IsVisible);
                Check("shelf " + scenario + ": keyed cards use exact strip sizes, spacing and insets", cards.Length is > 1 and < 15 && cards.All(card => Near(card.ActualWidth, 210) && Near(card.ActualHeight, 146) && card.ToolTip is null)
                    && Near(cards[0].TranslatePoint(new Point(), content).X, 14) && Near(cards[0].TranslatePoint(new Point(), content).Y, 46) && Near(cards[1].TranslatePoint(new Point(), content).X - cards[0].TranslatePoint(new Point(), content).X, 220));
                Check("shelf " + scenario + ": native palette radius also clips image cards", cards.All(card => card.CornerRadius.TopLeft == (palette.Effects.Pixel > 0 ? 0 : 18) && card.Clip is RectangleGeometry clip && clip.RadiusX == card.CornerRadius.TopLeft));
                Check("shelf " + scenario + ": shared image uses original model thumbnail without filename-only preview", cards.Single(card => card.Item == image).PreviewBody is Image preview && ReferenceEquals(preview.Source, image.Thumbnail));
                Render(island, directory, "shelf-dock-" + scenario + ".png");
                Button("shelf-search-toggle").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump();
                var search = (TextBox)Get(window, "_traySearch")!;
                Check("shelf " + scenario + ": search expands without hiding filters or settings", search.IsVisible && search.ActualWidth >= 45 && ((Border)Get(window, "_traySearchCapsule")!).ActualWidth <= 300 && types.All(button => button.IsVisible) && Button("shelf-settings").IsVisible);
                Render(island, directory, "shelf-search-" + scenario + ".png");
                Check("shelf " + scenario + ": empty search Escape collapses only the search", Shortcut(Key.Escape) && !(bool)Get(window, "_traySearchOpen")!); Pump();
            }
            Palette(palettes[0]); settings.PanelScale = 100; Call(window, "ApplyScale"); Call(window, "ApplyTheme"); Show(1080);
            Check("shelf ownership: island references original current collection and paths", ReferenceEquals(tray.Items, controller.Shelves.ActiveWindow.ViewModel.Items) && model.Items[0].Value == ordinary && File.Exists(ordinary));
            var initialCard = All<ShelfIslandCard>(content).First(card => card.Item!.Id == "shelf-file"); var initialBody = initialCard.PreviewBody;
            model.Items[0].SizeBytes = 256; image.Thumbnail = bitmap.Clone(); Call(window, "RenderFileTray"); Pump();
            Check("shelf refresh: metadata and thumbnails preserve card and preview identity without persistence", ReferenceEquals(initialCard, All<ShelfIslandCard>(content).First(card => card.Item!.Id == "shelf-file")) && ReferenceEquals(initialBody, initialCard.PreviewBody) && writes == 0);
            model.SetWindowVisible(true); model.SetRowVisible(image, true); Call(window, "ReleaseTrayViewport");
            Check("shelf thumbnails: releasing island keeps a visible floating row alive", ((IDictionary)Get(model, "_thumbnails")!).Contains(image) && image.Thumbnail is not null);
            model.SetWindowVisible(false);
            Check("shelf thumbnails: a hidden floating row cannot retain decoded pixels", !((IDictionary)Get(model, "_thumbnails")!).Contains(image) && image.Thumbnail is null);
            model.SetWindowVisible(true); model.SetRowVisible(image, false); model.SetWindowVisible(false);
            Check("shelf thumbnails: offscreen images release only after every owner leaves", !((IDictionary)Get(model, "_thumbnails")!).Contains(image));
            image.Thumbnail = bitmap; Call(window, "RenderFileTray"); Pump();
            Shortcut(Key.F, ModifierKeys.Control); Pump(); var field = (TextBox)Get(window, "_traySearch")!; field.Text = "SYNTHETIC 12"; Pump();
            Check($"shelf filters: case-insensitive multi-term search is only a model projection (results={string.Join(",", Results().Select(item => item.Id))}; model={model.Count}; writes={writes})", Results().Count == 12 && Results().All(item => item.Value.Contains("12")) && model.Count == 165 && writes == 0);
            Call(window, "OpenTrayMore", Button("shelf-more")); Pump();
            Check("shelf Escape: menu closes before clearing search", Shortcut(Key.Escape) && field.Text == "SYNTHETIC 12"); Pump();
            Check("shelf Escape: query clears before search collapses", Shortcut(Key.Escape) && field.Text.Length == 0 && (bool)Get(window, "_traySearchOpen")!); Pump();
            Shortcut(Key.Escape); Pump();
            Button("shelf-type-images").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump();
            Check("shelf filters: image type uses existing flat image file record", Results().Count == 1 && ReferenceEquals(Results()[0], image));
            Button("shelf-type-all").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); model.Items[0].Pinned = true; Pump(); int afterPin = writes;
            Button("shelf-pinned").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump();
            Check("shelf filters: pinned-only changes navigation, not pin state or persistence", Results().Count == 1 && Results()[0].Pinned && writes == afterPin);
            Button("shelf-pinned").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump();
            foreach (double width in new[] { 440d, 660d, 880d, 1080d, 1120d, 1320d })
            {
                Show(width, 356, true); var scroll = (ScrollViewer)Get(window, "_trayScroll")!; scroll.ScrollToTop(); Pump();
                var cards = All<ShelfIslandCard>(content).ToArray(); double top = cards.Min(card => card.TranslatePoint(new Point(), scroll).Y);
                var row = cards.Where(card => Near(card.TranslatePoint(new Point(), scroll).Y, top)).OrderBy(card => card.TranslatePoint(new Point(), scroll).X).ToArray();
                int columns = Math.Max(1, (int)Math.Floor((scroll.ViewportWidth + 10) / 220));
                Check($"shelf grid {width}: full row fills actual viewport with equal cards (viewport={scroll.ViewportWidth:0.##}; columns={columns}; row={row.Length}; widths={string.Join(",", row.Select(card => card.ActualWidth.ToString("0.##")))}; right={row[^1].TranslatePoint(new Point(), scroll).X + row[^1].ActualWidth:0.##}; offset={scroll.VerticalOffset:0.##})", row.Length == columns && row.All(card => Near(card.ActualWidth, row[0].ActualWidth)) && Near(row[^1].TranslatePoint(new Point(), scroll).X + row[^1].ActualWidth, scroll.ViewportWidth));
                Check("shelf grid " + width + ": bounded card realization and fixed row pitch", cards.Length < 45 && row.Zip(row.Skip(1)).All(pair => Near(pair.Second.TranslatePoint(new Point(), scroll).X - pair.First.TranslatePoint(new Point(), scroll).X - pair.First.ActualWidth, 10)) && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled);
                Render(island, directory, "shelf-grid-" + width + ".png");
            }
            Show(1080, 356, true); var scroller = (ScrollViewer)Get(window, "_trayScroll")!; scroller.ScrollToVerticalOffset(300); Pump();
            var anchor = All<ShelfIslandCard>(content).Where(card => card.TranslatePoint(new Point(), scroller).Y + card.ActualHeight > 0).OrderBy(card => Grid.GetRow(card)).ThenBy(card => Grid.GetColumn(card)).First();
            string anchorId = anchor.Item!.Id; double anchorTop = anchor.TranslatePoint(new Point(), scroller).Y;
            Show(880, 356, true);
            Check("shelf reflow: changing columns preserves visible stable anchor and inset", All<ShelfIslandCard>(content).Any(card => card.Item!.Id == anchorId && Near(card.TranslatePoint(new Point(), scroller).Y, anchorTop)));
            scroller.ScrollToTop(); Pump(); settings.Animations = true; Show(1120, 356, true);
            if (Soft.Enabled)
            {
                Check("shelf reflow: native keyed moves animate instead of fading the whole list", All<ShelfIslandCard>(content).Any(card => card.RenderTransform is TranslateTransform shift && shift.HasAnimatedProperties) && !((Grid)Get(window, "_trayGrid")!).HasAnimatedProperties);
                Show(660, 356, true); Show(1080, 356, true); WaitGridMotion();
                Check("shelf reflow: rapid resizes settle at latest slots", All<ShelfIslandCard>(content).All(card => card.RenderTransform.Value.IsIdentity));
            }
            settings.Animations = false; Show(1080); scroller.ScrollToHorizontalOffset(0); Pump();
            string[] order = model.Items.Select(item => item.Id).ToArray(); writes = 0;
            void Begin()
            {
                // Stage only the post-capture state: this offscreen fixture never presses the user's physical mouse.
                var first = All<ShelfIslandCard>(content).Single(card => card.Item!.Id == order[0]);
                Set(window, "_trayOriginalOrder", model.Items.Select(item => item.Id).ToArray());
                Set(window, "_trayDragCard", first); Set(window, "_trayDragItem", model.Items[0]); Set(window, "_trayDragging", true);
            }
            Begin(); tray.Reorder(0, 3); Call(window, "LayoutTrayCards"); Call(window, "EndTrayReorder", false); Pump();
            Check($"shelf drag: cancelling a flat reorder restores order without a disk notification (restored={model.Items.Select(item => item.Id).SequenceEqual(order)}; writes={writes}; dragging={Get(window, "_trayDragging")})", model.Items.Select(item => item.Id).SequenceEqual(order) && writes == 0 && !(bool)Get(window, "_trayDragging")!);
            Begin(); tray.Reorder(0, 3); Call(window, "LayoutTrayCards"); Call(window, "EndTrayReorder", true); Pump();
            Check("shelf drag: completed reorder shares original items and persists exactly once", model.Items[3].Id == order[0] && writes == 1 && File.Exists(ordinary));
            var toolbar = (FrameworkElement)window.FindName("Toolbar"); var stage = (FrameworkElement)window.FindName("Stage");
            Point Edge() { var p = island.PointToScreen(new Point()); return new Point(p.X + island.ActualWidth / 2, stage.PointToScreen(new Point()).Y + 1); }
            Point Body() { var p = island.PointToScreen(new Point()); return new Point(p.X + 100, p.Y + 100); }
            window.SetExpanded(false, instant: true); window.SetExpanded(true, instant: true); Pump(); Call(window, "TrackClipboardHeader", Edge());
            Check("shelf shared header: initial hover cannot reveal a second level", !toolbar.IsVisible);
            Call(window, "TrackClipboardHeader", Body()); Call(window, "TrackClipboardHeader", Edge()); Pump();
            Check("shelf shared header: reentering top edge reveals actual native navigation", toolbar.IsVisible && Near(((RowDefinition)window.FindName("ToolbarRow")).ActualHeight, 36) && Near(island.ActualHeight, 246) && Near(content.ActualHeight, 210));
            Render(island, directory, "shelf-shared-header.png");
            Call(window, "TrackClipboardHeader", Body()); WaitHeader(520);
            Check("shelf shared header: returning to content retracts after existing grace", !toolbar.IsVisible && Near(island.ActualHeight, 210));
            settings.Tray.HeaderMode = ClipboardHeaderMode.Always; window.SetExpanded(true, instant: true); Pump();
            Check("shelf shared header: island-only always mode persists independently of clipboard", toolbar.IsVisible && Near(island.ActualHeight, 246) && settings.Clipboard.HeaderMode == ClipboardHeaderMode.Always);
            Call(window, "ShowTrayNotice", "Synthetic action completed; original files remain untouched"); Pump(); var notice = (Border)Get(window, "_trayNotice")!;
            Check("shelf notice: one noninteractive capsule is constrained to bottom-right", notice.IsVisible && !notice.IsHitTestVisible && Near(notice.TranslatePoint(new Point(), content).X + notice.ActualWidth, content.ActualWidth - 14));
            Render(island, directory, "shelf-result-notice.png"); WaitHeader(1700); Call(window, "ShowTrayNotice", "Replacement result"); WaitHeader(1500);
            Check("shelf notice: replacement restarts its three-second lifetime", notice.IsVisible && ((TextBlock)notice.Child).Text == "Replacement result"); WaitHeader(1700);
            Check("shelf notice: expiry clears text instead of leaving a bottom caption", notice.Visibility == Visibility.Collapsed && ((TextBlock)notice.Child).Text.Length == 0);
            Shortcut(Key.F, ModifierKeys.Control); field = (TextBox)Get(window, "_traySearch")!; field.Text = "synthetic"; field.Select(2, 3); Pump(); var frame = Get(window, "_trayFrame");
            Palette(palettes[1]); Loc.Use(Loc.English); Call(window, "ApplyTheme"); Call(window, "RenderFileTray"); Pump();
            Check("shelf state: theme and language retain the actual page, query and active filter", ReferenceEquals(frame, Get(window, "_trayFrame")) && ((TextBox)Get(window, "_traySearch")!).Text == "synthetic" && (bool)Get(window, "_traySearchOpen")! && !(bool)Get(window, "_trayPinnedOnly")!);
            field = (TextBox)Get(window, "_traySearch")!; field.Clear(); Shortcut(Key.Escape); Call(window, "ShowTrayNotice", "Hidden result");
            Set(window, "_page", DeckPage.Clipboard); Call(window, "PrepareClipboardDock"); Pump();
            Check("shelf page boundary: hiding clears feedback and releases only its viewport", notice.Visibility == Visibility.Collapsed && Get(window, "_trayViewportModel") is null && File.Exists(ordinary) && model.Items.Any(item => item.Value == imagePath));
            Check("shelf fixture: no live input, capture, sharing or duplicate attachment ownership", !settings.Clipboard.Enabled && !(bool)Get(window, "_trayExporting")! && !controller.IsGestureRunning && !controller.IsHotkeyRegistered);
        }
        finally
        {
            ((DispatcherTimer)Get(app, "_save")!).Stop(); settings.Animations = false; settings.PanelScale = 100; settings.Tray.Placement = FileTrayPlacement.Floating; settings.Clipboard.HeaderMode = ClipboardHeaderMode.Edge;
            Call(window, "EndTrayReorder", false); Call(window, "ReleaseTrayViewport"); Call(window, "HideTrayNotice", false); tray.Changed -= window.TrayChanged;
            Set(window, "_page", DeckPage.Clipboard); Loc.Use(Loc.Chinese); controller.Dispose(); Pump();
        }
    }
    private static void CheckShelfMigration()
    {
        var settings = new DeckSettings { TrayWidth = 620, TrayHeight = 280, ClipboardLayoutVersion = ClipboardDockLayout.Version, ClipboardWidth = 880, ClipboardHeight = 340 };
        settings.Tray.GridView = true; settings.Tray.Placement = FileTrayPlacement.Both; settings.Normalize();
        Check("shelf migration: reference Dock replaces old dimensions with recoverable backup", settings.TrayWidth == 1080 && settings.TrayHeight == 210 && !settings.Tray.GridView && settings.TrayReferenceSize && settings.TrayPreviousLayout is { Width: 620, Height: 280, GridView: true });
        Check("shelf migration: clipboard, floating placement and capture preferences stay unchanged", settings.ClipboardWidth == 880 && settings.ClipboardHeight == 340 && settings.Tray.Placement == FileTrayPlacement.Both && !settings.Clipboard.Enabled);
        DeckPages.SetSize(settings, DeckPage.FileTray, 900, 300); settings.Normalize();
        Check("shelf migration: custom resize survives subsequent normalization", settings.TrayWidth == 900 && settings.TrayHeight == 300 && !settings.TrayReferenceSize && settings.TrayPreviousLayout!.Width == 620);
        ShelfDockLayout.RestorePrevious(settings); settings.Normalize();
        Check("shelf migration: previous size and grid can be restored without remigration", settings.TrayWidth == 620 && settings.TrayHeight == 280 && settings.Tray.GridView && !settings.TrayReferenceSize);
        var copy = settings.Copy(); copy.TrayPreviousLayout!.Width = 730;
        Check("shelf migration: layout backup is deep copied", settings.TrayPreviousLayout!.Width == 620);
        ShelfDockLayout.Reset(settings); settings.Tray.HeaderMode = ClipboardHeaderMode.Always;
        var stored = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(settings))!; stored.Normalize();
        Check("shelf preferences: geometry, view and header mode round-trip without enabling capture", stored.TrayLayoutVersion == ShelfDockLayout.Version && stored.TrayReferenceSize && !stored.Tray.GridView && stored.Tray.HeaderMode == ClipboardHeaderMode.Always && !stored.Clipboard.Enabled);
    }
}
