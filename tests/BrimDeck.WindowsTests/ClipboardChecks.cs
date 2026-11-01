using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;

// Isolated fixtures only: never starts the real App or reads/writes the user's clipboard, settings or history.
internal static partial class ClipboardChecks
{
    internal static void Run(Action<string, bool> check, string? renderDirectory = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "BrimDeck-Clipboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ClipboardHistoryTests.Run(check);
            var settings = new ClipboardSettings { RetentionDays = 0, HideOnDeactivate = false };
            var history = new ClipboardHistory(); var now = DateTimeOffset.UtcNow;
            history.Add(ClipboardContent.FromText("https://example.org/design"), "Browser fixture", "", now.AddMinutes(-23), settings);
            var photo = history.Add(new ClipboardContent { Kind = ClipboardContentKind.Image, Image = Image(), PixelWidth = 400, PixelHeight = 300 }, "Image fixture", "", now.AddMinutes(-5), settings)!;
            history.Add(ClipboardContent.FromText("A curated shelf of\nbeautifully designed\nmacOS apps."), "Browser fixture", "", now.AddMinutes(-23), settings);
            history.Add(ClipboardContent.FromText("Minneapolis 55410,\n2941 Rocket Drive\nUnited States"), "Browser fixture", "", now.AddMinutes(-19), settings);
            history.Add(ClipboardContent.FromText("#0080FF"), "Mail fixture", "", now.AddMinutes(-35), settings);
            history.ToggleFavorite(photo.Id);
            var store = new ClipboardHistoryStore(directory); store.Save(history.Snapshot());
            var stored = File.ReadAllBytes(store.FilePath);
            check("clipboard store: DPAPI payload is not plaintext", !Encoding.UTF8.GetString(stored).Contains("Rocket Drive", StringComparison.Ordinal));
            var restored = new ClipboardHistory(); restored.Restore(store.Load(), settings, now);
            check("clipboard store: text, image and favorites round-trip locally", restored.Items.Count == 5 && restored.Items.Any(item => item.Favorite && item.Content.Image.SequenceEqual(photo.Content.Image)));
            check("clipboard formats: text is classified without touching the system clipboard", ClipboardListener.ReadContent(Text("#0080FF"), settings)?.Kind == ClipboardContentKind.Color);
            var imageData = ClipboardListener.ToDataObject(photo.Content);
            check("clipboard formats: image recall yields a bitmap and PNG", imageData.GetDataPresent(DataFormats.Bitmap) && imageData.GetDataPresent("PNG"));
            check("clipboard formats: recalls opt out of cloud and Windows history", imageData.GetDataPresent("CanIncludeInClipboardHistory") && imageData.GetDataPresent("CanUploadToCloudClipboard"));
            var file = Path.Combine(directory, "fixture.txt"); File.WriteAllText(file, "test fixture");
            var files = new ClipboardContent { Kind = ClipboardContentKind.Files, Files = [file] };
            check("clipboard formats: file recall uses original paths", ClipboardListener.ToDataObject(files).GetFileDropList().Cast<string>().Single() == file);
            File.Delete(file);
            bool refused = false;
            try { ClipboardListener.ToDataObject(files); } catch (FileNotFoundException) { refused = true; }
            check("clipboard formats: missing originals refuse the whole recall", refused);
            using (var listener = new ClipboardListener(() => settings, (_, _, _) => throw new InvalidOperationException("Unexpected capture"), () => { }))
            {
                listener.Paused = true;
                check("clipboard native: message-only listener can register and dispose without reading a payload", true);
                listener.Paused = false;
                // Schedule only the notification, never a payload read or system clipboard write. No dispatcher turn runs
                // before the pause assertion, so this checks the privacy boundary without touching clipboard contents.
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var notification = typeof(ClipboardListener).GetMethod("Hook", flags)!;
                var timer = (DispatcherTimer)typeof(ClipboardListener).GetField("_read", flags)!.GetValue(listener)!;
                notification.Invoke(listener, [IntPtr.Zero, 0x031D, IntPtr.Zero, IntPtr.Zero, false]);
                check("clipboard pause: a copy notification schedules a deferred read", timer.IsEnabled);
                listener.Paused = true;
                check("clipboard pause: pausing cancels an already scheduled read", !timer.IsEnabled);
                listener.Paused = false;
                check("clipboard pause: resuming does not replay a read from before the pause", !timer.IsEnabled);
            }
            using (var registrar = new GlobalHotkeys())
            using (var other = new GlobalHotkeys())
            {
                var status = registrar.Register(HotkeyKind.Clipboard, 0x4000 | 0x2 | 0x4, 0x87); // Ctrl+Shift+F24, no user shortcut.
                if (status == HotkeyStatus.Active)
                {
                    check("clipboard native: duplicate global registration is reported", other.Register(HotkeyKind.Clipboard, 0x4000 | 0x2 | 0x4, 0x87) == HotkeyStatus.InUse);
                    registrar.Unregister(HotkeyKind.Clipboard);
                    check("clipboard native: unregister releases the global shortcut", other.Register(HotkeyKind.Clipboard, 0x4000 | 0x2 | 0x4, 0x87) == HotkeyStatus.Active);
                }
                else Console.WriteLine("SKIP: the isolated hotkey combination is already held by another app.");
            }
            CheckLibraryFormats(settings, check);
            MainWindow.CheckPageCapsule(check);
            CheckWindow(history, settings, check, renderDirectory);
            using (var service = new ClipboardService(directory, () => settings, Dispatcher.CurrentDispatcher))
            {
                service.History.Add(ClipboardContent.FromText("isolated memory fixture"), "Test", "", now, settings);
                settings.PersistHistory = false; service.ApplySettings();
            }
            check("clipboard persistence: opting out removes the prior file after pending saves", !File.Exists(store.FilePath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void CheckWindow(ClipboardHistory history, ClipboardSettings settings, Action<string, bool> check, string? renderDirectory)
    {
        var application = Application.Current ?? new App();
        application.Resources["PanelKeyboardFocus"] = new Style();
        bool enabled = true, paused = false; string problem = ""; int activations = 0, collapses = 0, inputRequests = 0;
        (string Id, bool CopyOnly) lastActivation = ("", false);
        using var view = new ClipboardView(history, () => settings, (item, copyOnly) => { activations++; lastActivation = (item.Id, copyOnly); },
            () => { }, () => paused = !paused, () => enabled = true, () => (enabled, paused, problem), grid => settings.GridView = grid, () => collapses++, confirm: _ => true);
        view.InputRequested += () => inputRequests++;
        var content = new Grid(); content.Children.Add(view);
        // Scale the host, as ExpandedContent does, so the page's header and margins scale with its body.
        var scaledHost = new Grid(); scaledHost.Children.Add(content);
        var island = new IslandBorder { Child = scaledHost, CornerRadius = new CornerRadius(0, 0, 24, 24), ShoulderRadius = 16, MaterialOpacity = 1 };
        island.SetResourceReference(Border.BackgroundProperty, "PanelSurface");
        var window = new Window { Content = island, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Width = ClipboardDockLayout.Width, Height = ClipboardDockLayout.Height,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI") };
        void Theme(string id)
        {
            var palette = PanelThemes.Build(id, PanelThemes.Find(id, true, []).File);
            foreach (var (resource, token) in new[] { ("PanelSurface", "surface"), ("PanelPrimary", "textPrimary"), ("PanelSecondary", "textSecondary"),
                ("PanelTertiary", "textTertiary"), ("PanelAccent", "accent"), ("PanelFill", "fill"), ("PanelFillSelected", "fillSelected"),
                ("PanelHover", "hover"), ("PanelPressed", "pressed"), ("PanelSwitchFill", "switchFill"), ("PanelPopup", "popup"), ("PanelPopupBorder", "popupBorder") })
            { var color = palette[token]; application.Resources[resource] = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)); }
            for (int r = 1; r <= 16; r++) application.Resources["PanelR" + r] = new CornerRadius(r);
            application.Resources["ClipboardDockCardRadius"] = new CornerRadius(18);
        }
        try
        {
            Theme(PanelThemes.Dark); window.Show(); Pump();
            var handle = new WindowInteropHelper(window).Handle;
            WindowsHost.ConfigureOverlay(window);
            check("clipboard focus: native hover overlay does not activate", (GetWindowLongPtr(handle, -20).ToInt64() & 0x08000000) != 0 && !window.IsActive);
            WindowsHost.SetOverlayInput(window, true);
            check("clipboard focus: explicit input removes the non-activation flag", (GetWindowLongPtr(handle, -20).ToInt64() & 0x08000000) == 0);
            WindowsHost.SetOverlayInput(window, false);
            check("clipboard focus: leaving input restores the hover flag", (GetWindowLongPtr(handle, -20).ToInt64() & 0x08000000) != 0);
            check("clipboard paste: own windows can never receive injected paste", !ClipboardPaste.IsExternalWindow(handle) && !ClipboardPaste.Restore(handle) && !ClipboardPaste.Send(handle));
            check("clipboard paste: absent focus targets are refused", !ClipboardPaste.RestoreFocus(IntPtr.Zero) && !ClipboardPaste.Restore(IntPtr.Zero));
            check("clipboard UI: page content is not a second Window or island shell", !typeof(Window).IsAssignableFrom(typeof(ClipboardView)) && !Descendants<IslandBorder>(view).Any());
            check("clipboard UI: the native-sized strip lists real entries", view.ResultCount == 5);
            check("clipboard UI: browsing does not request keyboard activation", inputRequests == 0);
            CheckAdaptiveHeader(view, history, check, () => activations, () => inputRequests);
            var cards = Descendants<ClipboardCard>(view).ToList();
            check("clipboard UI: cards fit the native page dimensions", cards.Count > 0 && cards.All(card => Math.Abs(card.ActualWidth - view.CardWidth) < 1 && Math.Abs(card.ActualHeight - view.CardHeight) < 1 && Math.Abs(card.ActualWidth - ClipboardDockLayout.CardWidth) < 1 && Math.Abs(card.ActualHeight - ClipboardDockLayout.CardHeight) < 1));
            var first = cards[0].TranslatePoint(new Point(), window);
            check("clipboard UI: one dock header precedes full-height cards", first.Y >= ClipboardDockLayout.HeaderHeight && first.Y + cards[0].ActualHeight <= window.Height - 8);
            check("clipboard UI: card background uses the native fill token", cards.All(card => Equals(card.Background, application.Resources["PanelFill"])));
            if (renderDirectory is not null) Render(island, renderDirectory, "clipboard-island-dark.png");
            view.SearchText = "rocket"; Pump();
            check("clipboard UI: typing filters displayed cards", view.ResultCount == 1);
            view.SearchText = "no matching fixture"; Pump();
            check("clipboard UI: unmatched search reaches the empty state", view.ResultCount == 0);
            view.SearchText = ""; view.FavoritesOnly = true; Pump();
            check("clipboard UI: favorites filter keeps only favorites", view.ResultCount == 1);
            view.FavoritesOnly = false; view.CollectionId = "colors"; Pump();
            check("clipboard UI: Colors is a working collection", view.ResultCount == 1);
            view.CollectionId = ""; Pump();
            check("clipboard UI: browsing never writes or pastes content", activations == 0);
            var favoriteButton = Descendants<Button>(view).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-drop-favorites");
            favoriteButton.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            check("clipboard keyboard: Enter on a toolbar button never pastes", activations == 0);
            var searchField = Descendants<TextBox>(view).Single();
            check("clipboard keyboard: search exposes the actual shortcut mapping without adding a toolbar row", System.Windows.Automation.AutomationProperties.GetHelpText(searchField).Contains("Ctrl+Enter", StringComparison.Ordinal)
                && System.Windows.Automation.AutomationProperties.GetHelpText(searchField).Contains("Alt+1", StringComparison.Ordinal));
            var keyboardCard = Descendants<ClipboardCard>(view).First(card => card.Entry?.Favorite == false);
            var keyboardStar = ((Grid)keyboardCard.Child).Children.OfType<Button>().Single();
            var keyboardRow = ItemsControl.ContainerFromElement(Descendants<ListBox>(view).Single(), keyboardCard) as ListBoxItem;
            var foreground = ClipboardPaste.Foreground;
            try
            {
                // Keyboard focus needs an active input-capable HWND, just as the real search gesture enables.
                WindowsHost.SetOverlayInput(window, true); window.Activate(); keyboardRow!.Focus(); Pump();
                check("clipboard keyboard: focused rows reveal an unfavorited card's star", keyboardRow.IsKeyboardFocusWithin && keyboardStar.Opacity == 1);
                view.FocusSearch(); Pump();
                check("clipboard keyboard: leaving an unfavorited row hides the idle star", !keyboardRow.IsKeyboardFocusWithin && keyboardStar.Opacity == 0);
            }
            finally { WindowsHost.SetOverlayInput(window, false); ClipboardPaste.RestoreFocus(foreground); }
            var timelineEntries = history.Query(view.QueryState).ToList();
            check("clipboard keyboard: Ctrl+Enter copies the newest timeline entry", view.HandleShortcut(Key.Enter, ModifierKeys.Control, searchField) && activations == 1 && lastActivation == (timelineEntries[0].Id, true));
            check("clipboard keyboard: Alt+number recalls the matching visible result", view.HandleShortcut(Key.D2, ModifierKeys.Alt, searchField) && activations == 2 && lastActivation == (timelineEntries[1].Id, false));
            view.HandleShortcut(Key.D9, ModifierKeys.Alt, searchField);
            check("clipboard keyboard: out-of-range numbers never recall another entry", activations == 2);
            int beforeDelete = history.Items.Count;
            check("clipboard keyboard: Delete on toolbar buttons never deletes history", !view.HandleShortcut(Key.Delete, ModifierKeys.None, favoriteButton) && history.Items.Count == beforeDelete);
            check("clipboard keyboard: Down from search selects the first result", view.HandleShortcut(Key.Down, ModifierKeys.None, searchField) && Descendants<ListBox>(view).Single().SelectedIndex == 0);
            var favoriteCardButton = Descendants<ClipboardCard>(view).First().Child is Grid cardGrid ? cardGrid.Children.OfType<Button>().Single() : null;
            check("clipboard keyboard: Enter on a card favorite stays with the button", !view.HandleShortcut(Key.Enter, ModifierKeys.None, favoriteCardButton) && activations == 2);
            view.SearchText = "rocket"; Pump();
            check("clipboard keyboard: Enter after filtering recalls the match", view.HandleShortcut(Key.Enter, ModifierKeys.None, searchField) && lastActivation == (history.Items.Single(item => item.Content.Text.Contains("Rocket")).Id, false));
            view.HandleShortcut(Key.Escape, ModifierKeys.None, searchField); Pump();
            check("clipboard keyboard: Escape clears search before collapsing", view.SearchText == "" && collapses == 0);
            view.HandleShortcut(Key.Escape, ModifierKeys.None, searchField); Pump();
            check("clipboard keyboard: empty-search Escape folds search before the island", !searchField.IsVisible && collapses == 0);
            view.HandleShortcut(Key.Escape, ModifierKeys.None, searchField);
            check("clipboard keyboard: folded-search Escape asks the native owner to collapse", collapses == 1 && window.IsVisible);
            settings.GridView = true; view.ApplyLayout(); view.Refresh(); WaitClipboardMotion(Soft.Move + 80); Pump();
            cards = Descendants<ClipboardCard>(view).ToList();
            check("clipboard UI: grid mode wraps within the existing island size", view.ResultCount == 5 && window.Width == ClipboardDockLayout.Width && window.Height == ClipboardDockLayout.Height && cards.Count(card => Math.Abs(card.TranslatePoint(new Point(), view).Y - cards[0].TranslatePoint(new Point(), view).Y) < 1) == 4);
            Theme(PanelThemes.Light); Pump();
            check("clipboard UI: theme changes repaint the same cards", cards.All(card => Equals(card.Background, application.Resources["PanelFill"])) && Equals(searchField.Foreground, application.Resources["PanelPrimary"]));
            if (renderDirectory is not null) Render(island, renderDirectory, "clipboard-island-light-grid.png");
            settings.GridView = false; view.ApplyLayout(); window.Width = 440; window.Height = ClipboardDockLayout.Height; view.Refresh(); Pump();
            view.FocusSearch(); WaitClipboardMotion(Soft.Choose + 60); Pump();
            var fixedTypes = Descendants<Button>(view).Where(button => AutomationProperties.GetAutomationId(button).StartsWith("clipboard-type-", StringComparison.Ordinal)).ToArray();
            var headerActions = Descendants<Button>(view).Where(button => AutomationProperties.GetAutomationId(button) is "clipboard-settings" or "clipboard-sort" or "clipboard-drop-favorites" or "clipboard-search-toggle").ToArray();
            check($"clipboard UI: minimum page keeps every type, expanded search and fixed actions inside (search={searchField.ActualWidth:0.#}; page={view.ActualWidth:0.#}; types={fixedTypes.Length}; bounds={string.Join(",", fixedTypes.Concat(headerActions).Select(button => AutomationProperties.GetAutomationId(button) + ":" + button.IsVisible + ":" + button.TranslatePoint(new Point(), window).X.ToString("0.#") + ":" + button.ActualWidth.ToString("0.#")))})", searchField.ActualWidth > 80 && fixedTypes.Length == 7
                && fixedTypes.Concat(headerActions).All(button => button.IsVisible && button.TranslatePoint(new Point(), window).X >= 0 && button.TranslatePoint(new Point(), window).X + button.ActualWidth <= window.Width + 1));
            view.DismissFromKeyboard(); Pump();
            cards = Descendants<ClipboardCard>(view).ToList();
            check("clipboard UI: compact native page keeps the whole card above its bottom edge", cards.All(card => card.TranslatePoint(new Point(), window).Y + card.ActualHeight <= window.Height - 8));
            if (renderDirectory is not null) Render(island, renderDirectory, "clipboard-island-compact.png");
            window.Width = 1620; window.Height = 315; scaledHost.LayoutTransform = new ScaleTransform(1.5, 1.5); view.Refresh(); Pump();
            check("clipboard UI: native 150 percent scale preserves the page's logical width", Math.Abs(view.ActualWidth - ClipboardDockLayout.Width) < 2 && view.ResultCount == 5);
            CheckLibrary(history, settings, view, check, Theme, renderDirectory);
            var actionsDirectory = Path.Combine(Path.GetTempPath(), "BrimDeck-CardActions-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(actionsDirectory);
            try { CheckCardActions(actionsDirectory, check, renderDirectory); }
            finally { Directory.Delete(actionsDirectory, true); }
            enabled = false; history.Clear(false); view.Refresh(); Pump();
            check("clipboard UI: recording-off empty page stays available", view.ResultCount == 0 && window.IsVisible);
        }
        finally { window.Close(); }
    }
    [System.Runtime.InteropServices.DllImport("user32", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    private static DataObject Text(string value) { var data = new DataObject(); data.SetText(value); return data; }
    private static byte[] Image()
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x03, 0x75, 0x69), Color.FromRgb(0x15, 0x3D, 0x30), 35), null, new Rect(0, 0, 400, 300));
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE5, 0xAD, 0x84)), null, new Point(200, 130), 85, 100);
        }
        var bitmap = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static void Render(FrameworkElement island, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        // Capture the settled layout one to one, not the changing drawn bounds or a partly faded entrance frame.
        WaitClipboardMotion(Soft.Short + 60); island.UpdateLayout();
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xD8)), null, new Rect(0, 0, island.ActualWidth, island.ActualHeight + 22));
            context.DrawRectangle(PageHandover.Exactly(island, island.ActualWidth, island.ActualHeight), null, new Rect(0, 22, island.ActualWidth, island.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)island.ActualWidth, (int)island.ActualHeight + 22, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
        Console.WriteLine("RENDER: " + Path.Combine(directory, name));
    }
    private static void CheckAdaptiveHeader(ClipboardView view, ClipboardHistory history, Action<string, bool> check, Func<int> activations, Func<int> inputRequests)
    {
        Button Button(string id) => Descendants<Button>(view).Single(button => AutomationProperties.GetAutomationId(button) == id);
        void Click(string id) { Button(id).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); WaitClipboardMotion(Soft.Choose + 40); Pump(); }
        var header = Descendants<Grid>(view).Single(grid => AutomationProperties.GetAutomationId(grid) == "clipboard-dock-header");
        var field = Descendants<TextBox>(view).Single();
        int beforeActivation = activations(), beforeInput = inputRequests();
        check("adaptive header: initial search is an icon, not a permanent field", !field.IsVisible && Button("clipboard-search-toggle").IsVisible);
        check("adaptive header: no count, clear, emoji or more toolbar buttons remain", !Descendants<Button>(header).Any(button => AutomationProperties.GetAutomationId(button) is "clipboard-more" or "clipboard-clear-history" or "clipboard-emoji"));
        Click("clipboard-type-palette");
        check("adaptive header: fixed color icon filters the same history owner", view.ResultCount == 1 && view.QueryState.Kinds.SequenceEqual(new[] { ClipboardContentKind.Color }));
        Click("clipboard-mode-applications");
        check("adaptive header: classification pill changes source navigation without losing type", view.ApplicationsMode && view.QueryState.CollectionId == "" && view.ResultCount == 1 && view.QueryState.Kinds.SequenceEqual(new[] { ClipboardContentKind.Color }));
        Click("clipboard-mode-collections"); Click("clipboard-type-clockCounterClockwise");
        Click("clipboard-drop-favorites");
        check("adaptive header: visible favorite action filters without moving entries", view.FavoritesOnly && view.ResultCount == history.Items.Count(entry => entry.Favorite) && Button("clipboard-drop-favorites").Tag as string == "Selected");
        Click("clipboard-drop-favorites"); Click("clipboard-search-toggle");
        check("adaptive header: search click explicitly borrows input and expands", field.IsVisible && field.ActualWidth > 80 && inputRequests() > beforeInput);
        view.SearchText = "rocket"; Pump(); var filtered = view.QueryState; int count = view.ResultCount;
        Click("clipboard-search-toggle");
        check("adaptive header: folding search preserves its query and filter", !field.IsVisible && view.SearchText == "rocket" && view.ResultCount == count && Button("clipboard-search-toggle").Tag as string == "Selected");
        check("adaptive header: Ctrl+F reopens the same query", view.HandleShortcut(Key.F, ModifierKeys.Control, Button("clipboard-search-toggle"))); WaitClipboardMotion(Soft.Choose + 40); Pump();
        check("adaptive header: reopened search retains text and classification", field.IsVisible && view.SearchText == filtered.Text && !view.ApplicationsMode);
        view.DismissFromKeyboard(); view.DismissFromKeyboard(); WaitClipboardMotion(Soft.Choose + 40); Pump();
        var settings = Button("clipboard-settings"); settings.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent }); Pump();
        check("adaptive header: right-click settings no longer opens the retired toolbar menu", settings.ContextMenu is null && !view.PopupOpen);
        view.ClosePopups();
        check("adaptive header: toolbar browsing never copies or pastes entries", activations() == beforeActivation && !field.IsVisible && view.ResultCount == history.Query().Count());
        check("adaptive header: Down enters history directly from collapsed search", view.HandleShortcut(Key.Down, ModifierKeys.None, Button("clipboard-search-toggle")) && Descendants<ListBox>(view).Single().SelectedIndex == 0);
    }
    private static DependencyObject Navigation(DependencyObject root) => root;
    private static Button OpenFilters(ClipboardView view)
    {
        var settings = Descendants<Button>(view).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-settings");
        // Exercise the filter surface directly; the retired toolbar menu is no longer a user entry point.
        typeof(ClipboardView).GetMethod("ShowFilters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(view, null); Pump();
        return settings;
    }
    private static bool FindCard(DependencyObject node)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current)) if (current is ClipboardCard) return true;
        return false;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
