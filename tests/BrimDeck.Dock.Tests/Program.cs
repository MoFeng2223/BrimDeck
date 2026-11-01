using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;

// References the built production App/MainWindow, including its compiled XAML. No real App startup or capture service runs.
internal static partial class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--paste-receiver", var receiverDirectory, var receiverNonce]) return RunPasteReceiver(receiverDirectory, receiverNonce);
        if (args.Length > 0 && args[0] == "--paste-e2e") return CheckPasteIntegration(args);
        string directory = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "BrimDeck-Dock-Renders");
        string scratch = args.Skip(1).Contains("--exit-lifecycle-child") ? directory : Path.Combine(Path.GetTempPath(), "BrimDeck-Dock-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        MainWindow? window = null; LyricsService? lyrics = null;
        try
        {
            if (args.Skip(1).Contains("--only-exit-lifecycle")) return CheckExitLifecycleProcess(scratch);
            Migration();
            var app = new LayoutApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("BrimDeck.Dock.App.xaml")!)
            {
                var xml = System.Xml.Linq.XDocument.Load(source); var resources = xml.Root!.Elements().Single().Elements().Single();
                resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "local", "clr-namespace:BrimDeck;assembly=BrimDeck");
                resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=System.Runtime");
                app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
            }
            var settings = new DeckSettings { Animations = false, UsagePage = false, MusicPage = false, MonitorPage = false, LastPage = DeckPage.Clipboard,
                ClipboardLayoutVersion = ClipboardDockLayout.Version, NotchMonitor = false, CapsuleMonitor = false, NotchMusic = false, CapsuleMusic = false, MusicIndicatorProgress = false };
            settings.Apps.Clear(); settings.Tray.Placement = FileTrayPlacement.Floating;
            Set(app, "<Settings>k__BackingField", settings);
            // The explicit offscreen host needs the move-loop state, not a live global WinEvent hook.
            Set(app, "<WindowDrags>k__BackingField", System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WindowDrags)));
            lyrics = new LyricsService(appleCacheRoots: () => []); Set(app, "<Lyrics>k__BackingField", lyrics);
            app.Updates = new AppUpdates(new NoUpdates(), scratch);
            window = new MainWindow(app) { ShowInTaskbar = false, ShowActivated = false, Topmost = false, Left = -20000, Top = -20000 };
            Set(app, "<Deck>k__BackingField", window);
            // Loaded normally starts live monitors, music and account refresh. Remove only that event in this isolated process.
            var store = typeof(UIElement).GetProperty("EventHandlersStore", Private)!.GetValue(window)!;
            var loaded = (RoutedEventHandlerInfo[])store.GetType().GetMethod("GetRoutedEventHandlers", Private | BindingFlags.Public)!.Invoke(store, [FrameworkElement.LoadedEvent])!;
            foreach (var handler in loaded) window.RemoveHandler(FrameworkElement.LoadedEvent, handler.Handler);
            ((IDisposable)Get(window, "_host")!).Dispose();
            Set(window, "_preview", true); Set(window, "_previewExpanded", true); Set(window, "_retiring", true);
            var history = new ClipboardHistory(); var now = DateTimeOffset.UtcNow;
            string file = Path.Combine(scratch, "design-notes.txt"); File.WriteAllText(file, "Synthetic layout fixture, not user data.");
            var fixture = new[]
            {
                new ClipboardEntry { Id = "text", Content = ClipboardContent.FromText("Design review\nA quiet place for copied ideas.\nKeep every detail within reach.\nOne horizontal row, one clear focus."), CopiedAt = now.AddMinutes(-2), SourceName = "Fixture", CopyCount = 4 },
                new ClipboardEntry { Id = "photo", Content = new ClipboardContent { Kind = ClipboardContentKind.Image, Image = Image(), PixelWidth = 400, PixelHeight = 300 }, CopiedAt = now.AddMinutes(-5) },
                new ClipboardEntry { Id = "link", Content = ClipboardContent.FromText("https://github.com/example/design/pull/42"), CopiedAt = now.AddMinutes(-8), CopyCount = 7 },
                new ClipboardEntry { Id = "file", Content = new ClipboardContent { Kind = ClipboardContentKind.Files, Files = [file] }, CopiedAt = now.AddMinutes(-11) },
                new ClipboardEntry { Id = "color", Content = ClipboardContent.FromText("#79A8E8"), CopiedAt = now.AddMinutes(-15) },
                new ClipboardEntry { Id = "next", Content = ClipboardContent.FromText("The next card stays in the same row."), CopiedAt = now.AddMinutes(-18) },
            };
            history.Restore(new ClipboardSnapshot { Items = fixture.ToList() }, settings.Clipboard, now);
            var palettes = new[] { PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!), PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Light)!),
                PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.PixelBlocks)!),
                PanelThemes.Build("fixture-custom", new PanelThemeFile { Base = PanelThemes.Light, Colors = new() { ["fill"] = "#E7EFF7", ["accent"] = "#285DA8" } }),
                PanelThemes.HighContrast(new(255, 0, 0, 0), new(255, 255, 255, 255), new(255, 190, 190, 190), new(255, 255, 255, 0), new(255, 0, 0, 0)) };
            Palette(palettes[0]); window.Show(); Pump();
            if (args.Skip(1).Contains("--exit-lifecycle-child"))
            {
                CheckExitLifecycleHost(app, window, scratch);
                Console.WriteLine($"EXIT CHILD RESULT: {_checks} checks passed.");
                return 0;
            }
            if (args.Skip(1).Contains("--only-feedback"))
            {
                CheckFeedbackHost(app, window, settings, history, palettes, scratch, directory);
                Console.WriteLine($"FEEDBACK HOST RESULT: {_checks} checks passed.");
                return 0;
            }
            if (args.Skip(1).Contains("--only-module-lifecycle"))
            {
                CheckModuleLifecycleHost(app, window, scratch);
                Console.WriteLine($"MODULE LIFECYCLE RESULT: {_checks} checks passed.");
                return 0;
            }
            if (args.Skip(1).Contains("--only-performance"))
            {
                CheckPerformanceHost(app, window, settings, palettes, scratch, directory);
                Console.WriteLine($"PERFORMANCE HOST RESULT: {_checks} checks passed; isolated production-host measurements in {directory}");
                return 0;
            }
            if (args.Skip(1).Contains("--only-shelf"))
            {
                CheckShelfDockHost(app, window, settings, palettes, scratch, directory);
                Console.WriteLine($"SHELF DOCK RESULT: {_checks} checks passed; actual MainWindow renders in {directory}");
                return 0;
            }
            if (args.Skip(1).Contains("--only-panel-layout"))
            {
                CheckPanelLayoutHost(app, window, settings, history, palettes, directory);
                Console.WriteLine($"PANEL LAYOUT RESULT: {_checks} checks passed; production panel renders in {directory}");
                return 0;
            }
            if (args.Skip(1).Contains("--only-collections"))
            {
                CheckCollectionEditorHost(window, settings, palettes, directory);
                Console.WriteLine($"COLLECTION HOST RESULT: {_checks} checks passed; production collection menu renders in {directory}");
                return 0;
            }
            if (args.Skip(1).Contains("--only-preview-window"))
            {
                CheckPreviewWindows(app, window, settings, palettes, scratch, directory);
                Console.WriteLine($"PREVIEW WINDOW RESULT: {_checks} checks passed; production window renders in {directory}");
                return 0;
            }
            ClipboardView? view = null;
            foreach (var palette in palettes)
            foreach (int percent in new[] { 100, 150 })
            foreach (double workWidth in new[] { 1920d, 720d })
            {
                Palette(palette); settings.PanelScale = percent; Call(window, "ApplyScale");
                double scale = percent / 100d, width = ClipboardDockLayout.ReferenceWidth(workWidth, scale);
                Set(window, "<MonitorBoundsDip>k__BackingField", new DipRect(0, 0, workWidth, 1080)); Set(window, "<WorkDip>k__BackingField", new DipRect(0, 0, workWidth, 1032));
                Set(window, "_pageWidth", width); Set(window, "_widestPage", width); window.Width = workWidth; window.Height = 600;
                Call(window, "ApplyTheme"); Call(window, "PrepareClipboardDock");
                Call(window, "RenderClipboardPage", history, (Action)(() => { }), (Action)(() => { }), (Func<(bool, bool, string)>)(() => (true, false, "")));
                view = (ClipboardView)Get(window, "_clipboardView")!; window.SetExpanded(true, instant: true); Pump();
                var island = (IslandBorder)window.FindName("Island");
                var header = All<Grid>(view).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-dock-header");
                var search = All<Border>(view).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-search-capsule");
                var cards = All<ClipboardCard>(view).OrderBy(card => card.TranslatePoint(new Point(), view).X).ToArray();
                string scenario = palette.Id + "-" + percent + "-" + (workWidth > 1000 ? "wide" : "narrow");
                Check(scenario + ": actual host panel geometry", Near(island.ActualWidth, width * scale) && Near(island.ActualHeight, 210 * scale));
                Check(scenario + ": one header and no host toolbar slot", header.ActualHeight == 40 && ((FrameworkElement)window.FindName("Toolbar")).Visibility == Visibility.Collapsed && ((RowDefinition)window.FindName("ToolbarRow")).ActualHeight == 0);
                var types = All<Button>(header).Where(button => AutomationProperties.GetAutomationId(button).StartsWith("clipboard-type-", StringComparison.Ordinal)).ToArray();
                var classification = All<DockPanel>(header).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-classification");
                var searchField = All<TextBox>(view).Single();
                Check(scenario + ": all seven type icons stay visible at every width", types.Length == 7 && types.All(button => button.IsVisible && button.TranslatePoint(new Point(), view).X >= 0 && button.TranslatePoint(new Point(), view).X + button.ActualWidth <= width));
                Check(scenario + ": collapsed search leaves inline classification visible", Near(search.ActualWidth, 30) && !searchField.IsVisible && classification.IsVisible
                    && All<Button>(classification).Count(button => AutomationProperties.GetAutomationId(button).StartsWith("clipboard-mode-", StringComparison.Ordinal)) == 2);
                var actions = ((StackPanel)Get(view, "_dockRight")!).Children.OfType<Button>().ToArray();
                Check(scenario + ": sort sits directly left of settings beside favorites", actions.Select(AutomationProperties.GetAutomationId).SequenceEqual(new[] { "clipboard-drop-favorites", "clipboard-sort", "clipboard-settings" }));
                Check(scenario + ": fixed cards and exact horizontal gap", cards.Length >= 2 && cards.All(card => Near(card.ActualWidth, 210) && Near(card.ActualHeight, 146)) && Near(cards[1].TranslatePoint(new Point(), view).X - cards[0].TranslatePoint(new Point(), view).X, 220));
                Check(scenario + ": list inset and no date or wrapping", Near(cards[0].TranslatePoint(new Point(), view).X, 14) && Near(cards[0].TranslatePoint(new Point(), view).Y, 46) && cards.All(card => Near(card.TranslatePoint(new Point(), view).Y, 46)) && view.TimelineHeaderHeight == 0);
                Check(scenario + ": partially visible next card", cards.Any(card => { double x = card.TranslatePoint(new Point(), view).X; return x < width - 14 && x + 210 > width - 14; }) || width - 28 >= cards.Length * 220);
                var meta = All<Grid>(cards[0]).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-card-meta");
                var time = All<TextBlock>(cards[0]).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-card-time");
                var copies = All<TextBlock>(cards[0]).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-card-copy-count");
                Check(scenario + ": footer has left label, time and a trailing repeat count", meta.TranslatePoint(new Point(), cards[0]).X < 20 && copies.IsVisible && copies.Text == "×4"
                    && time.TranslatePoint(new Point(), cards[0]).X + time.ActualWidth <= copies.TranslatePoint(new Point(), cards[0]).X && copies.TranslatePoint(new Point(), cards[0]).X + copies.ActualWidth > 190 && meta.ActualWidth > 0);
                Check(scenario + ": theme radius and clip remain coherent", cards[0].CornerRadius.TopLeft == (palette.Effects.Pixel > 0 ? 0 : 18) && cards[0].Clip is RectangleGeometry clip && clip.RadiusX == cards[0].CornerRadius.TopLeft);
                Render(island, directory, "clipboard-dock-" + scenario + ".png");
                var toggle = All<Button>(header).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-search-toggle");
                var beforeSearch = view.CapturePresentation();
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Check(scenario + ": search click expands without moving or hiding the fixed type track", searchField.IsVisible && searchField.ActualWidth > 80 && search.ActualWidth <= ClipboardDockLayout.SearchWidth
                    && types.All(button => button.IsVisible) && search.TranslatePoint(new Point(), view).X + search.ActualWidth <= actions[0].TranslatePoint(new Point(), view).X);
                Render(island, directory, "clipboard-dock-search-" + scenario + ".png");
                view.DismissFromKeyboard(); Pump();
                Check(scenario + ": empty-search Escape returns classification without changing panel geometry", !searchField.IsVisible && classification.IsVisible && Near(island.ActualHeight, 210 * scale) && view.CapturePresentation() == beforeSearch);
                view.ApplicationsMode = true; Pump();
                Render(island, directory, "clipboard-dock-applications-" + scenario + ".png");
                view.ApplicationsMode = false; Pump();
            }
            Check("real host references compiled production XAML", window.GetType().Assembly == typeof(App).Assembly && window.FindName("ContentCanvas") is Grid);
            Palette(palettes[0]); Call(window, "ApplyTheme");
            if (args.Skip(1).Contains("--only-applications"))
            {
                CheckApplicationActionsHost(app, window, view!, settings, history, palettes, scratch, directory);
                Console.WriteLine($"APPLICATION HOST RESULT: {_checks} checks passed; production menus, code and fragments in {directory}");
                return 0;
            }
            if (args.Skip(1).Contains("--only-toolbar"))
            {
                CheckClipboardToolbarHost(app, window, view!, settings, history, palettes, scratch, directory);
                Console.WriteLine($"TOOLBAR HOST RESULT: {_checks} checks passed; actual MainWindow renders in {directory}");
                return 0;
            }
            if (args.Skip(1).Contains("--only-storage"))
            {
                CheckPreviewSettings(app, settings, scratch, directory);
                Console.WriteLine($"STORAGE HOST RESULT: {_checks} checks passed; actual settings renders in {directory}");
                return 0;
            }
            CheckAnchoredPreviews(window, view!, settings, history, directory);
            CheckPreviewSettings(app, settings, scratch, directory);
            CheckAdaptiveGridHost(window, view!, settings, history, palettes, directory);
            CheckSharedClipboardHeader(window, view!, settings, history, palettes, directory);
            CheckClipboardToolbarHost(app, window, view!, settings, history, palettes, scratch, directory);
            CheckChatImageHost(window, view!, settings, history, palettes, scratch, directory);
            view!.FocusHeader(reset: true); Pump();
            Check("hotkey browsing focus leaves search collapsed and navigation visible", !All<TextBox>(view).Single().IsVisible && ((DockPanel)Get(view, "_categoryBar")!).IsVisible);
            Palette(palettes[0]); settings.PanelScale = 100; Call(window, "ApplyScale");
            Set(window, "<MonitorBoundsDip>k__BackingField", new DipRect(0, 0, 1920, 1080)); Set(window, "<WorkDip>k__BackingField", new DipRect(0, 0, 1920, 1032));
            Set(window, "_pageWidth", 1080d); Set(window, "_widestPage", 1080d); window.Width = 1920;
            Call(window, "ApplyTheme"); Call(window, "PrepareClipboardDock"); window.SetExpanded(true, instant: true); Pump();
            var list = All<ListBox>(view!).Single();
            // The live host callback writes real app preferences; redirect only that boundary to isolated settings.
            Set(view, "_setSort", (Action<ClipboardSort>)(order => settings.Clipboard.SortOrder = order));
            var sortButton = All<Button>(view).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-sort");
            sortButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            RenderMenu((IslandBorder)window.FindName("Island"), sortButton.ContextMenu!, directory, "clipboard-card-sort-menu.png");
            sortButton.ContextMenu!.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "clipboard-sort-count").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            Check("real host count sort updates order without changing shell size", ((ClipboardEntry)list.Items[0]).Id == "link" && view.QueryState.Sort == ClipboardSort.Count);
            Render((IslandBorder)window.FindName("Island"), directory, "clipboard-card-count-order.png");
            view.QueryState = new ClipboardQuery(); Pump();
            Call(view, "OpenItemMenu", history.Items.Single(item => item.Id == "link"), false); Pump();
            Check("real host card context menu has offline clean-link action", list.ContextMenu?.IsOpen == true && list.ContextMenu.Items.OfType<MenuItem>().Any(item => AutomationProperties.GetAutomationId(item) == "clipboard-card-menu-copy"));
            RenderMenu((IslandBorder)window.FindName("Island"), list.ContextMenu!, directory, "clipboard-card-context-menu.png"); view.ClosePopups(); Pump();
            list.SelectedItem = history.Items[1]; view!.SearchText = ""; Pump();
            OpenAutomaticPreview(view,history.Items[1]); var state = view.CapturePresentation(); Palette(palettes[1]); Call(window, "RenderClipboardPage", history, (Action)(() => { }), (Action)(() => { }), (Func<(bool, bool, string)>)(() => (true, false, ""))); Pump();
            Check("real host theme update preserves view, selection, offsets and preview", ReferenceEquals(view, Get(window, "_clipboardView")) && view.CapturePresentation() == state && view.PreviewOpen);
            view.ClosePopups();
            view.QueryState = new ClipboardQuery { Text = "design", Sort = ClipboardSort.Count }; Pump(); list.SelectedItem = history.Items.Single(item => item.Id == "file");
            All<ScrollViewer>(list).First().ScrollToHorizontalOffset(1); Pump();
            OpenAutomaticPreview(view,history.Items.Single(item => item.Id == "file")); var filteredState = view.CapturePresentation();
            Loc.Use(Loc.English); Call(window, "RenderClipboardPage", history, (Action)(() => { }), (Action)(() => { }), (Func<(bool, bool, string)>)(() => (true, false, ""))); Pump();
            view = (ClipboardView)Get(window, "_clipboardView")!; list = All<ListBox>(view).Single();
            Check("real host language rebuild preserves query, selected id, scroll and preview", view.SearchText == "design" && view.QueryState.Sort == ClipboardSort.Count && view.CapturePresentation() == filteredState && view.PreviewOpen);
            view.ClosePopups(); view.ShowDetails(history.Items.Single(item => item.Id == "file")); Pump();
            var inspector = (ClipboardEntryEditor)Get(view, "_inspector")!;
            var draft = All<TextBox>(inspector).Single(field => AutomationProperties.GetAutomationId(field) == "clipboard-edit-title"); draft.Text = "Unsaved synthetic title";
            var draftState = view.CapturePresentation(); Palette(palettes[0]); Loc.Use(Loc.Chinese);
            Call(window, "RenderClipboardPage", history, (Action)(() => { }), (Action)(() => { }), (Func<(bool, bool, string)>)(() => (true, false, ""))); Pump();
            Check("real host theme and language changes keep unsaved editor draft and browsing state", ReferenceEquals(view, Get(window, "_clipboardView")) && view.HasUnsavedDraft && draft.Text == "Unsaved synthetic title" && view.SearchText == "design" && view.CapturePresentation() == draftState);
            inspector.Load(history.Items.Single(item => item.Id == "file")); view.ClosePopups(); Loc.Use(Loc.English); view.QueryState = new(); Pump();
            var thousand = Enumerable.Range(0, 1000).Select(i => new ClipboardEntry { Id = "item-" + i, Content = ClipboardContent.FromText("Synthetic item " + i), CopiedAt = now.AddMinutes(-i) }).ToList();
            settings.Clipboard.MaximumItems = 1000; settings.Clipboard.RetentionDays = 0;
            history.Restore(new ClipboardSnapshot { Items = thousand }, settings.Clipboard, now); view.Refresh(); Pump();
            Check("real host thousand-entry strip realizes only viewport", view.ResultCount == 1000 && All<ClipboardCard>(view).Count() is > 0 and < 30);
            settings.Clipboard.GridView = true; view.ApplyLayout(); Pump();
            Check("real host thousand-entry grid realizes only bounded rows", All<IslandCardPanel>(view).Any() && All<ClipboardCard>(view).Count() is > 0 and < 40);
            settings.Clipboard.GridView = false; view.ApplyLayout(); Pump();
            list.ScrollIntoView(history.Items[42]); Pump();
            var anchor = VisibleEntries(list).First(); var selected = history.Items[42]; list.SelectedItem = selected;
            var oldPreviewBoundary = Get(view, "_openPreviewWindow");
            using (var floating = new ClipboardPreviewWindows(history, () => settings.Clipboard, (_, _, _) => throw new InvalidOperationException("Unexpected preview activation")))
            {
                Set(view, "_openPreviewWindow", (Action<ClipboardEntry, Rect, Rect, double>)floating.Show);
                try
                {
                    view.ShowPreview(selected); var previewWindow = floating.Windows.Single(); settings.Clipboard.GridView = true; view.ApplyLayout(); Pump();
                    Check("real host layout switch keeps selection, independent preview and first visible entry", ReferenceEquals(list.SelectedItem, selected) && previewWindow.IsVisible && !view.PreviewOpen && !view.PopupOpen && VisibleEntries(list).Any(entry => entry.Id == anchor.Id));
                }
                finally { Set(view, "_openPreviewWindow", oldPreviewBoundary); }
            }
            view.ClosePopups(); settings.Clipboard.GridView = false; view.ApplyLayout(); Pump();
            OpenEmoji(view);
            var picker = All<ListBox>(view).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-emoji-grid");
            Check("local emoji view has real Unicode cells and bounded row virtualization", picker.Items.Count > 10 && All<Button>(picker).Any(button => button.Content is string { Length: > 0 }) && All<Button>(picker).Count() < 300);
            view.DismissFromKeyboard(); Check("Escape returns from emoji before collapsing", picker.Visibility == Visibility.Collapsed);
            CheckEmojiCopies(window, history);
            Check("layout verification leaves isolated file and capture settings intact", File.Exists(file) && !settings.Clipboard.Enabled && !settings.Animations);
            Console.WriteLine($"DOCK RESULT: {_checks} checks passed; actual MainWindow renders in {directory}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (window is not null && !window.Dispatcher.HasShutdownStarted) { Set(window, "_retiring", true); window.Close(); } lyrics?.Dispose(); Directory.Delete(scratch, true); }
    }
    private static void Migration()
    {
        var settings = new DeckSettings { ClipboardWidth = 620, ClipboardHeight = 280, PanelScale = 150, TrayWidth = 567, TrayLayoutVersion = ShelfDockLayout.Version, MusicWidth = 543 };
        settings.Clipboard.GridView = true; settings.Clipboard.Enabled = false; settings.Clipboard.MaximumItems = 500; settings.Normalize();
        Check("migration changes current saved dimensions and backs up original layout", settings.ClipboardWidth == 1200 && settings.ClipboardHeight == 210 && !settings.Clipboard.GridView && settings.ClipboardPreviousLayout is { Width: 620, Height: 280, GridView: true });
        Check("migration leaves scale, other pages, capture and retention alone", settings.PanelScale == 150 && settings.TrayWidth == 567 && settings.MusicWidth == 543 && !settings.Clipboard.Enabled && settings.Clipboard.MaximumItems == 500);
        DeckPages.SetSize(settings, DeckPage.Clipboard, 900, 120); settings.Normalize();
        Check("migration runs once and clipboard-specific minimum protects full cards", settings.ClipboardWidth == 900 && settings.ClipboardHeight == 210 && !settings.ClipboardReferenceSize && settings.ClipboardPreviousLayout!.Width == 620);
        ClipboardDockLayout.RestorePrevious(settings); settings.Normalize();
        Check("previous geometry and grid are restorable without remigration", settings.ClipboardWidth == 620 && settings.ClipboardHeight == 280 && settings.Clipboard.GridView && !settings.ClipboardReferenceSize);
        ClipboardDockLayout.Reset(settings); Check("reference reset opts back into work-area fitting", settings.ClipboardWidth == 1200 && settings.ClipboardHeight == 210 && settings.ClipboardReferenceSize && !settings.Clipboard.GridView);
        Check("100 and 150 percent work-area fitting applies scale once", ClipboardDockLayout.ReferenceWidth(1920, 1.5) == 1200 && ClipboardDockLayout.ReferenceWidth(720, 1.5) == 440);
    }
    private static void Palette(PanelPalette palette)
    {
        typeof(PanelTheme).GetProperty(nameof(PanelTheme.Current))!.SetValue(null, palette);
        typeof(PanelTheme).GetField("_hex", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, typeof(PanelTheme).GetMethod("Hexes", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [palette]));
        typeof(PanelTheme).GetProperty(nameof(PanelTheme.Generation))!.SetValue(null, PanelTheme.Generation + 1);
        typeof(PanelTheme).GetMethod("Publish", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Application.Current.Resources]);
    }
    private static byte[] Image()
    {
        var visual = new DrawingVisual(); using (var context = visual.RenderOpen())
        { context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(35, 62, 92), Color.FromRgb(120, 171, 179), 45), null, new Rect(0, 0, 400, 300)); context.DrawEllipse(Brushes.BurlyWood, null, new Point(180, 150), 70, 80); }
        var bitmap = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static void Render(FrameworkElement island, string directory, string name)
    {
        Directory.CreateDirectory(directory); island.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Round(island.ActualWidth), (int)Math.Round(island.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(island);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); string path = Path.Combine(directory, name); using var stream = File.Create(path); encoder.Save(stream); Console.WriteLine("RENDER: " + path);
    }
    private static void CheckAnchoredPreviews(MainWindow window, ClipboardView view, DeckSettings settings, ClipboardHistory history, string directory)
    {
        double oldLeft = window.Left, oldTop = window.Top, oldWidth = window.Width, oldHeight = window.Height;
        var screen = WindowsHost.PrimaryScreen(); double dpi = screen.Scale;
        Rect Bounds(FrameworkElement element) => new(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
        try
        {
            foreach (int percent in new[] { 100, 150 })
            foreach (bool bottom in new[] { false, true })
            {
                settings.PanelScale = percent; Call(window, "ApplyScale");
                double width = ClipboardDockLayout.ReferenceWidth(screen.Work.Width / dpi, percent / 100d);
                Set(window, "_pageWidth", width); Set(window, "_widestPage", width); window.Width = screen.Work.Width / dpi; window.Height = 600;
                Call(window, "PrepareClipboardDock"); window.SetExpanded(true, instant: true); Pump();
                window.Left += (screen.Work.Left + (screen.Work.Width - Bounds(view).Width) / 2 - Bounds(view).Left) / dpi;
                double targetTop = bottom ? screen.Work.Bottom - Bounds(view).Height - 24 : screen.Work.Top + 24;
                window.Top += (targetTop - Bounds(view).Top) / dpi; Pump();
                var island = (IslandBorder)window.FindName("Island");
                var cards = All<ClipboardCard>(view).Where(card => card.Entry is not null).OrderBy(card => card.TranslatePoint(new Point(), view).X).ToArray();
                var previous = Rect.Empty;
                foreach (var card in new[] { cards[0], cards[Math.Min(3, cards.Length - 1)] })
                {
                    OpenAutomaticPreview(view,card.Entry!); Pump();
                    var popup = (Popup)Get(view, "_preview")!; var chrome = (FrameworkElement)popup.Child; var actual = Bounds(chrome); var panel = Bounds(view); var trigger = Bounds(card);
                    string scenario = percent + "-" + (bottom ? "bottom" : "top") + "-" + card.Entry!.Id;
                    Check("anchored preview " + scenario + ": uses actual card and stays on its monitor", screen.Work.Contains(actual) && popup.Placement == PlacementMode.Custom);
                    Check("anchored preview " + scenario + ": opens outside the island on the available vertical side", bottom ? actual.Bottom <= panel.Top : actual.Top >= panel.Bottom);
                    Check("anchored preview " + scenario + ": content scale is applied exactly once", Near(actual.Width / dpi, chrome.ActualWidth * percent / 100d));
                    if (!previous.IsEmpty) Check("anchored preview " + scenario + ": right card moves the flyout rather than reusing list-left", actual.Left > previous.Left);
                    previous = actual;
                    if (percent == 100) RenderPreview(island, chrome, directory, "clipboard-preview-" + scenario + ".png");
                    view.ClosePopups(); Pump();
                }
                if (percent == 100 && !bottom)
                {
                    settings.Animations = true;
                    Call(view, "OpenHoverPreview", cards[0]); Pump();
                    var currentPopup = (Popup)Get(view, "_preview")!; var currentChrome = currentPopup.Child;
                    var targets = new[] { cards[3], cards[1], cards[2], cards[0], cards[1], cards[3] };
                    foreach (var target in targets)
                    {
                        target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent }); Pump();
                        Check("real host rapid preview " + target.Entry!.Id + ": latest card content replaces stale preview in the same HWND", (string?)Get(view, "_previewId") == target.Entry.Id && ReferenceEquals(currentPopup, Get(view, "_preview")) && ReferenceEquals(currentChrome, currentPopup.Child));
                    }
                    if (Soft.Enabled) Check("real host rapid preview: follow animation retargets rather than reopening", (bool)Get(view, "_previewFollowing")! && currentPopup.IsOpen);
                    var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
                    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
                    var actual = Bounds((FrameworkElement)currentPopup.Child); var lastCard = Bounds(targets[^1]);
                    double inset = 12 * dpi;
                    var work = screen.Work; work.Inflate(-inset, -inset);
                    var expected = ClipboardView.PlacePreview(lastCard, Bounds(view), work, actual.Size, 6 * dpi);
                    Check("real host rapid preview: final position settles at latest card without queued old motions", Math.Abs(actual.Left - expected.Left) < 2 && Math.Abs(actual.Top - expected.Top) < 2 && !(bool)Get(view, "_previewFollowing")!);
                    RenderPreview(island, (FrameworkElement)currentPopup.Child, directory, "clipboard-preview-rapid-settled.png");
                    view.ClosePopups(); settings.Animations = false; Pump();
                }
            }
        }
        finally
        {
            view.ClosePopups(); settings.Animations = false; settings.PanelScale = 100; Call(window, "ApplyScale");
            window.Left = oldLeft; window.Top = oldTop; window.Width = oldWidth; window.Height = oldHeight;
            Set(window, "_pageWidth", 1080d); Set(window, "_widestPage", 1080d); Call(window, "PrepareClipboardDock"); window.SetExpanded(true, instant: true); Pump();
        }
    }
    private static void CheckPreviewSettings(App app, DeckSettings settings, string scratch, string directory)
    {
        using var prices = new Pricing(scratch); Set(app, "<Prices>k__BackingField", prices);
        Set(app, "<Store>k__BackingField", new SettingsStore(Path.Combine(scratch, "storage-host-settings")));
        var cacheType = typeof(DropShelf.Services.VirtualFileDropService);
        var configureCache = cacheType.GetMethod("ConfigureCacheRoot", BindingFlags.Static | BindingFlags.NonPublic)!;
        string previousCache = DropShelf.Services.VirtualFileDropService.CacheDirectoryPath;
        configureCache.Invoke(null, [Path.Combine(scratch, "storage-cache")]);
        using var clipboard = new ClipboardService(Path.Combine(scratch, "storage-settings"), () => settings.Clipboard, Dispatcher.CurrentDispatcher);
        Set(app, "_clipboard", clipboard);
        clipboard.History.Add(ClipboardContent.FromText("Synthetic storage settings fixture"), "Fixture", "", DateTimeOffset.UtcNow, settings.Clipboard);
        var preferences = new SettingsWindow(app) { ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        bool exiting = app.Exiting;
        try
        {
            preferences.ShowPage(SettingsWindow.ClipboardPage); preferences.Show(); Pump();
            var limit = All<FrameworkElement>(preferences).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-history-limit");
            Check("real storage settings: unlimited is an explicit count option", All<RadioButton>(limit).Any(option => AutomationProperties.GetName(option).Contains(Loc.T("无上限", "Unlimited"), StringComparison.Ordinal)));
            var persistence = All<FrameworkElement>(preferences).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-persistent-storage");
            Check("real storage settings: persistent storage replaces the old restart label", All<TextBlock>(persistence).Any(label => label.Text == Loc.T("持久化存储", "Persistent storage")) && !All<TextBlock>(preferences).Any(label => label.Text == "重启后保留历史"));
            var data = All<FrameworkElement>(preferences).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-data-storage");
            var path = All<TextBox>(data).Single();
            Check("real storage settings: selected path and apply/reset controls are accessible", path.Text == clipboard.StorageDirectory && All<Button>(data).Any(button => AutomationProperties.GetAutomationId(button) == "clipboard-storage-apply" && button.IsEnabled) && All<Button>(data).Any(button => AutomationProperties.GetAutomationId(button) == "clipboard-storage-reset"));
            var usage = All<FrameworkElement>(data).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-storage-usage");
            var until = DateTime.UtcNow.AddSeconds(5);
            while (All<TextBlock>(usage).All(label => label.Text.Length == 0) && DateTime.UtcNow < until) { Pump(); Thread.Sleep(5); }
            Check("real storage settings: encoded payload, encrypted history and shared cache have distinct values", All<TextBlock>(usage).Count(label => label.Text.Length > 0) == 3);
            string selectedPath = Path.Combine(scratch, "storage-selected");
            var migration = app.SetClipboardStorageDirectoryAsync(selectedPath);
            until = DateTime.UtcNow.AddSeconds(5);
            while (!migration.IsCompleted && DateTime.UtcNow < until) { Pump(); Thread.Sleep(5); }
            Check("real storage settings: application commits the path to isolated ordinary settings", migration.IsCompletedSuccessfully && migration.Result.Succeeded && app.Store.Load().Clipboard.StorageDirectory == selectedPath && settings.Clipboard.StorageDirectory == selectedPath);
            migration = app.SetClipboardStorageDirectoryAsync("");
            until = DateTime.UtcNow.AddSeconds(5);
            while (!migration.IsCompleted && DateTime.UtcNow < until) { Pump(); Thread.Sleep(5); }
            Check("real storage settings: default reset is durably committed before deleting the migrated file", migration.IsCompletedSuccessfully && migration.Result.Succeeded && app.Store.Load().Clipboard.StorageDirectory == "" && !File.Exists(new ClipboardHistoryStore(selectedPath).FilePath));
            string draftPath = Path.Combine(scratch, "unsaved-storage-draft"); path.Text = draftPath;
            var previousTheme = settings.Theme;
            foreach (bool dark in new[] { false, true })
            {
                settings.Theme = dark ? SettingsTheme.Dark : SettingsTheme.Light; preferences.RebuildTheme(); preferences.ShowPage(SettingsWindow.ClipboardPage); Pump();
                data = All<FrameworkElement>(preferences).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-data-storage");
                path = All<TextBox>(data).Single();
                Check("real storage settings: theme rebuild preserves unsubmitted path draft", path.Text == draftPath && settings.Clipboard.StorageDirectory == "");
                var currentUsage = All<FrameworkElement>(data).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-storage-usage");
                until = DateTime.UtcNow.AddSeconds(5);
                while (All<TextBlock>(currentUsage).All(label => label.Text.Length == 0) && DateTime.UtcNow < until) { Pump(); Thread.Sleep(5); }
                data.BringIntoView(); Pump();
                Check("real storage settings: path fits " + (dark ? "dark" : "light") + " settings layout", path.ActualWidth > 100 && path.TranslatePoint(new Point(), preferences).X + path.ActualWidth <= preferences.ActualWidth);
                Render(preferences.RootVisual, directory, "clipboard-data-storage-" + (dark ? "dark" : "light") + ".png");
            }
            settings.Theme = previousTheme;
            var headerMode = All<FrameworkElement>(preferences).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-shared-header-mode");
            var headerChoices = All<RadioButton>(headerMode).ToArray();
            Check("real clipboard settings: header exposes edge reveal and always-visible choices without changing capture", headerChoices.Length == 2 && headerChoices.Count(choice => choice.IsChecked == true) == 1 && !settings.Clipboard.Enabled);
            headerMode.BringIntoView(); Pump(); Render(preferences.RootVisual, directory, "clipboard-shared-header-settings.png");
            var slider = All<EdgeDropWakeSlider>(preferences).Single();
            Check("real clipboard settings: native EdgeDrop wave slider is registered on clipboard page", AutomationProperties.GetAutomationId(slider) == "clipboard-preview-sensitivity" && slider.Value == settings.Clipboard.PreviewSensitivity);
            var control = All<Grid>(preferences).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-preview-sensitivity-control");
            control.BringIntoView(); Pump();
            Check("real clipboard settings: sensitivity control fits the actual settings page", slider.ActualWidth > 200 && slider.ActualWidth < preferences.ActualWidth && All<Button>(preferences).Count(button => AutomationProperties.GetAutomationId(button).StartsWith("clipboard-preview-preset-", StringComparison.Ordinal)) == 3);
            // Render actual settings content without calling the live save boundary; slider input is covered in the isolated runner.
            Render(preferences.RootVisual, directory, "clipboard-preview-settings.png");
            foreach (bool dark in new[] { false, true })
            {
                var palette = new SettingsPalette(dark); palette.Apply(preferences.Resources); Pump();
                var scroll = (ScrollViewer)Get(preferences, "_scroll")!;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 40); Pump();
                var backdrop = new Border { Width = control.ActualWidth, Height = control.ActualHeight, Background = UI.Brush(palette.Surface) };
                backdrop.Measure(new Size(backdrop.Width, backdrop.Height)); backdrop.Arrange(new Rect(0, 0, backdrop.Width, backdrop.Height));
                var drawing = new DrawingVisual(); using (var context = drawing.RenderOpen())
                {
                    context.DrawRectangle(backdrop.Background, null, new Rect(0, 0, backdrop.Width, backdrop.Height));
                    context.DrawRectangle(new VisualBrush(control) { Stretch = Stretch.Fill }, null, new Rect(0, 0, backdrop.Width, backdrop.Height));
                }
                var picture = new RenderTargetBitmap((int)Math.Ceiling(backdrop.Width), (int)Math.Ceiling(backdrop.Height), 96, 96, PixelFormats.Pbgra32); picture.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(picture));
                using (var stream = File.Create(Path.Combine(directory, "clipboard-preview-slider-" + (dark ? "dark" : "light") + ".png"))) encoder.Save(stream);
                Check("real clipboard settings: " + (dark ? "dark" : "light") + " palette updates retained wave slider", slider.FindResource("SettingsTextPrimary") == preferences.FindResource("SettingsTextPrimary"));
            }
        }
        finally
        {
            // Closing an isolated settings window must not call live FlushSettings or save its synthetic size.
            Set(app, "<Exiting>k__BackingField", true); preferences.Close(); Set(app, "<Exiting>k__BackingField", exiting); Set(app, "_clipboard", null!);
            configureCache.Invoke(null, [previousCache]);
        }
    }
    // Both visuals retain their actual screen displacement; this is an isolated production host, not live user history.
    private static void RenderPreview(FrameworkElement island, FrameworkElement preview, string directory, string name)
    {
        Rect Bounds(FrameworkElement element) => new(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
        var panel = Bounds(island); var flyout = Bounds(preview); var union = Rect.Union(panel, flyout); double dpi = VisualTreeHelper.GetDpi(island).DpiScaleX;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)Application.Current.Resources["PanelSurface"], null, new Rect(0, 0, union.Width / dpi, union.Height / dpi));
            foreach (var (element, rect) in new[] { (island, panel), (preview, flyout) })
                drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, new Rect((rect.X - union.X) / dpi, (rect.Y - union.Y) / dpi, rect.Width / dpi, rect.Height / dpi));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(union.Width / dpi), (int)Math.Ceiling(union.Height / dpi), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
    // ContextMenu lives in a separate HWND: compose its actual WPF visual below the actual island, not a mocked menu.
    private static void RenderMenu(FrameworkElement island, ContextMenu menu, string directory, string name)
    {
        menu.UpdateLayout(); double width = island.ActualWidth, menuWidth = menu.ActualWidth, menuHeight = menu.ActualHeight;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)Application.Current.Resources["PanelSurface"], null, new Rect(0, 0, width, island.ActualHeight + menuHeight + 12));
            drawing.DrawRectangle(new VisualBrush(island) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, island.ActualHeight));
            drawing.DrawRectangle(new VisualBrush(menu) { Stretch = Stretch.Fill }, null, new Rect(Math.Max(0, width - menuWidth - 16), island.ActualHeight + 6, menuWidth, menuHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(island.ActualHeight + menuHeight + 12), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
    private static IEnumerable<ClipboardEntry> VisibleEntries(ListBox list) => list.Items.OfType<ClipboardEntry>().Where(entry =>
    {
        if (list.ItemContainerGenerator.ContainerFromItem(entry) is not FrameworkElement container) return false;
        var position = container.TranslatePoint(new Point(), list);
        return position.X < list.ActualWidth && position.X + container.ActualWidth > 0 && position.Y < list.ActualHeight && position.Y + container.ActualHeight > 0;
    });
    private static void OpenEmoji(ClipboardView view)
    {
        // The toolbar more-menu was removed; retain isolated coverage of the existing picker without recreating that UI.
        Call(view, "SetEmojiOpen", true); Pump();
    }
    private static void CheckEmojiCopies(MainWindow owner, ClipboardHistory history)
    {
        // Intercept activation before the App boundary: never read or write the system clipboard for picker checks.
        ClipboardEntry? copied = null; bool copyOnly = false; int before = history.Items.Count;
        var settings = new ClipboardSettings(); using var picker = new ClipboardView(history, () => settings,
            (entry, only) => { copied = entry; copyOnly = only; }, () => { }, () => { }, () => { }, () => (true, false, ""), value => settings.GridView = value, () => { });
        var host = new Window { Content = picker, Width = 1080, Height = 210, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000, Owner = owner };
        try
        {
            host.Show(); Pump(); OpenEmoji(picker);
            var grid = All<ListBox>(picker).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-emoji-grid");
            var simple = All<Button>(grid).First(button => ((Array)Get(button.Tag, "<Skins>k__BackingField")!).Length == 0);
            simple.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("emoji selection uses Unicode copy-only activation without inserting history", copied?.Content.Text == (string)simple.Content && copyOnly && history.Items.Count == before);
            var toned = All<Button>(grid).First(button => ((Array)Get(button.Tag, "<Skins>k__BackingField")!).Length > 0);
            copied = null; toned.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); var menu = (ContextMenu)Get(picker, "_activeMenu")!;
            var choices = menu.Items.OfType<MenuItem>().ToArray();
            Check("emoji skin-tone branch waits for an explicit native menu choice", copied is null && menu.IsOpen && choices.Length >= 6);
            choices[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            Check("emoji skin-tone choice preserves original Unicode and protected copy-only intent", copied?.Content.Text == choices[1].Header.ToString() && copyOnly && history.Items.Count == before);
            picker.ClosePopups();
            var recents = (List<string>)typeof(ClipboardView).GetField("EmojiRecents", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!; recents.Clear();
            for (int i = 0; i < 90; i++) Call(picker, "CopyEmoji", (0x1F600 + i).ToString("X"));
            string mostRecent = recents[0]; Call(picker, "CopyEmoji", mostRecent);
            Check("emoji recents are session-only, unique and bounded at seventy-seven", recents.Count == 77 && recents.Distinct().Count() == 77 && recents[0] == mostRecent && history.Items.Count == before);
            picker.DismissFromKeyboard(); Check("emoji Escape restores the existing history view without closing its host", grid.Visibility == Visibility.Collapsed && host.IsVisible);
        }
        finally { host.Close(); }
    }
    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1;
    private static void Check(string label, bool ok) { if (!ok) throw new InvalidOperationException("FAIL: " + label); _checks++; Console.WriteLine("PASS: " + label); }
    private static FieldInfo Field(object target, string field)
    {
        for (Type? type = target.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(field, Private) is { } found) return found;
        throw new MissingFieldException(target.GetType().FullName, field);
    }
    private static object? Get(object target, string field) => Field(target, field).GetValue(target);
    private static void Set(object target, string field, object? value) => Field(target, field).SetValue(target, value);
    private static void Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T found) yield return found; foreach (var nested in All<T>(child)) yield return nested; }
    }
    // Application queues startup before Run; dispatcher pumps must never enter App's live startup path.
    private sealed class LayoutApplication : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }
    private sealed class NoUpdates : IAppUpdateBackend
    {
        public string CurrentVersion => "0.0.0";
        public bool CanInstall => false;
        public AppRelease? PendingRelease => null;
        public Task<AppRelease?> CheckAsync(CancellationToken cancellation) => throw new InvalidOperationException("Network is forbidden in layout verification");
        public Task DownloadAsync(AppRelease release, Action<int> progress, CancellationToken cancellation) => throw new InvalidOperationException("Downloads are forbidden in layout verification");
        public void PrepareRestart() => throw new InvalidOperationException("Restart is forbidden in layout verification");
    }
}
