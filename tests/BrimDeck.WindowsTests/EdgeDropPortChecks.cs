using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
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
using DropShelf.Models;

// Isolated synthetic data; never reads real history, writes the system clipboard, or starts native export.
internal static class EdgeDropPortChecks
{
    internal static void Run(Action<string, bool> check, string? renderDirectory = null)
    {
        Algorithms(check);
        var app = Application.Current ?? new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string token in new[] { "PanelFill", "PanelPopup", "PanelTrack", "PanelSwitchFill", "PanelHover", "PanelPressed", "PanelFillSelected" }) app.Resources[token] = Brushes.DimGray;
        foreach (string token in new[] { "PanelPrimary", "PanelSecondary", "PanelTertiary", "PanelPopupBorder" }) app.Resources[token] = Brushes.White;
        app.Resources["PanelAccent"] = Brushes.Cyan;
        app.Resources["PanelKeyboardFocus"] = new Style();
        for (int r = 1; r <= 16; r++) app.Resources["PanelR" + r] = new CornerRadius(r);
        var settings = new ClipboardSettings { Enabled = false, MaximumItems = 1000, RetentionDays = 0 };
        var history = new ClipboardHistory(); var now = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(0, 1000).Select(i => new ClipboardEntry { Id = "synthetic-" + i, Content = ClipboardContent.FromText("Synthetic item " + i), CreatedAt = now.AddMinutes(-i), CopiedAt = now.AddMinutes(-i), SourceName = "Fixture" }).ToArray();
        history.Restore(new ClipboardSnapshot { Items = entries.ToList() }, settings, now);
        using var view = new ClipboardView(history, () => settings, (_, _) => throw new InvalidOperationException("Unexpected clipboard activation"), () => { }, () => { }, () => { }, () => (false, false, ""), grid => settings.GridView = grid, () => { }, confirm: _ => true);
        var window = new Window { Content = view, Width = ClipboardDockLayout.Width, Height = ClipboardDockLayout.Height, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        string root = Path.Combine(Path.GetTempPath(), "BrimDeck-EdgeDrop-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            window.Show(); Pump();
            var list = All<ListBox>(view).Single();
            check("EdgeDrop: 1000-entry strip realizes only the viewport", view.ResultCount == 1000 && All<ClipboardCard>(view).Count() < 40);
            var source = list.ItemsSource; var card = All<ClipboardCard>(view).First(); var body = card.PreviewBody;
            list.SelectedItem = card.Entry; history.ToggleFavorite(card.Entry!.Id); Pump();
            check("EdgeDrop: metadata changes preserve list source, card and preview", ReferenceEquals(source, list.ItemsSource) && All<ClipboardCard>(view).Contains(card) && ReferenceEquals(body, card.PreviewBody) && (list.SelectedItem as ClipboardEntry)?.Id == card.Entry.Id);
            view.RefreshTimes(); Pump();
            check("EdgeDrop: clock updates do not recreate preview or collection", ReferenceEquals(source, list.ItemsSource) && ReferenceEquals(body, card.PreviewBody));
            settings.GridView = true; view.ApplyLayout(); Pump();
            check("EdgeDrop: 1000-entry grid realizes bounded rows", All<IslandCardPanel>(view).Any() && All<ClipboardCard>(view).Count() is > 0 and < 60);
            var scroll = All<ScrollViewer>(list).First(); scroll.ScrollToVerticalOffset(6000); Pump();
            check("EdgeDrop: virtual grid scrolls to later entries", scroll.VerticalOffset > 0 && All<ClipboardCard>(view).Any(value => int.Parse(value.Entry!.Id[10..]) > 30) && All<ClipboardCard>(view).Count() < 60);
            scroll.ScrollToTop(); Pump();
            list.SelectedItem = history.Items[2]; string selected = history.Items[2].Id; settings.GridView = false; view.ApplyLayout(); Pump();
            check("EdgeDrop: layout changes preserve selected id", (list.SelectedItem as ClipboardEntry)?.Id == selected);
            var syntheticImage = Image(); var photo = new ClipboardEntry { Id = "photo", Content = new ClipboardContent { Kind = ClipboardContentKind.Image, Image = syntheticImage, PixelWidth = 48, PixelHeight = 48 }, CreatedAt = now, CopiedAt = now };
            var standalone = new ClipboardCard { DataContext = photo, Width = 172, Height = 126 }; var preview = standalone.PreviewBody;
            standalone.DataContext = photo with { Title = "Edited metadata", Content = photo.Content with { Image = [.. syntheticImage] } };
            check("EdgeDrop: copied image buffers with unchanged visible content reuse preview", ReferenceEquals(preview, standalone.PreviewBody));
            var keyboardCard = All<ClipboardCard>(view).First(value => value.Entry?.Favorite == false);
            check("EdgeDrop: idle actions are hidden and cannot intercept input", !keyboardCard.ActionsVisible && All<Border>(keyboardCard).Where(border => border.Child is StackPanel).Any(border => border.Visibility == Visibility.Hidden && !border.IsHitTestVisible));
            var row = ItemsControl.ContainerFromElement(list, keyboardCard) as ListBoxItem;
            window.Activate(); row!.Focus(); Pump();
            check("EdgeDrop: row keyboard focus reveals actions", keyboardCard.ActionsVisible);
            string a = Path.Combine(root, "one.txt"), b = Path.Combine(root, "two.txt"); File.WriteAllText(a, "one"); File.WriteAllText(b, "two");
            var files = new ClipboardEntry { Id = "files", Content = new ClipboardContent { Kind = ClipboardContentKind.Files, Files = [a, b] }, CreatedAt = now, CopiedAt = now };
            typeof(ClipboardView).GetMethod("OpenPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [files]); Pump();
            check("EdgeDrop: file group preview holds island open", view.PreviewOpen && view.PopupOpen);
            var popup = (Popup)typeof(ClipboardView).GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            check("EdgeDrop: expanded group exposes each filename and whole-entry actions", All<TextBlock>(popup.Child).Any(text => text.Text == "one.txt") && All<TextBlock>(popup.Child).Any(text => text.Text == "two.txt") && All<Button>(popup.Child).Any(button => AutomationProperties.GetName(button) == Loc.T("复制整条记录", "Copy whole entry")));
            view.DismissFromKeyboard(); Pump(); check("EdgeDrop: Escape dismisses preview before collapse", !view.PreviewOpen);
            check("EdgeDrop: dismissing a preview never deletes originals", File.Exists(a) && File.Exists(b));
            var drag = new ClipboardDragDrop(history); var payload = drag.EntryData(entries[1].Id); string format = payload.GetFormats(false).Single();
            string marker = (string)payload.GetData(format, false)!;
            var nativeShape = new DataObject(); nativeShape.SetData(format, new MemoryStream(Encoding.Unicode.GetBytes(marker + "\0")), false);
            var target = All<Button>(view).First(button => AutomationProperties.GetAutomationId(button) == "clipboard-collection-assets");
            var args = Drag(target, nativeShape); target.RaiseEvent(args); Pump();
            check("EdgeDrop: native UTF-16 marker keeps internal collection organization", args.Effects == DragDropEffects.Copy && history.Items.First(item => item.Id == entries[1].Id).CollectionId == "assets");
            var shelf = new ShelfIslandCard(_ => { }, _ => { }, _ => { }); var item = new ShelfItem { Id = "shelf", Kind = ShelfItemKind.File, Value = a };
            shelf.Update(item, 172, 144); var shelfBody = shelf.PreviewBody; item.Pinned = true; shelf.Update(item, 180, 132);
            check("EdgeDrop: shelf card metadata and geometry retain its preview", ReferenceEquals(shelfBody, shelf.PreviewBody));
            var segment = new IslandSegmentedControl(["Folders", "Apps"]); int chosen = -1;
            segment.Buttons[1].Click += (_, _) => { chosen = 1; segment.Select(1); }; segment.Select(0);
            segment.Buttons[0].RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.End) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            check("EdgeDrop: segmented End commits last slot with roving tab focus", chosen == 1 && segment.Buttons[1].IsTabStop && !segment.Buttons[0].IsTabStop);
            foreach (bool grid in new[] { false, true })
            {
                settings.GridView = grid; view.ApplyLayout(); view.SearchText = "Synthetic item 9"; Pump();
                check($"EdgeDrop: filtered results retain virtualization in {(grid ? "grid" : "strip")} (results={view.ResultCount}, realized={All<ClipboardCard>(view).Count()})", view.ResultCount == history.Query(view.QueryState).Count() && view.ResultCount < 1000 && All<ClipboardCard>(view).Count() < 60);
            }
            ThemeChecks(app, view, list, shelf, files, window, check, renderDirectory);
            string feedback = ""; var transitions = new List<bool>();
            var export = new ClipboardDragDrop(history, transitions.Add, message => feedback = message);
            export.Export(window, files with { Content = files.Content with { Files = [a, Path.Combine(root, "absent.txt")] } });
            check("EdgeDrop: one missing original refuses the whole export before OLE", feedback.Length > 0 && transitions.SequenceEqual(new[] { true, false }) && !export.IsDragging && File.Exists(a) && File.Exists(b));
        }
        finally { view.ClosePopups(); window.Close(); Directory.Delete(root, true); }
    }
    private static void ThemeChecks(Application app, ClipboardView view, ListBox list, ShelfIslandCard shelf, ClipboardEntry files, Window window, Action<string, bool> check, string? renderDirectory)
    {
        view.SearchText = "Synthetic item 9"; Pump();
        var scroll = All<ScrollViewer>(list).First(); scroll.ScrollToVerticalOffset(180); Pump();
        var card = All<ClipboardCard>(view).First(); list.SelectedItem = card.Entry;
        var shelfWindow = new Window { Content = shelf, Width = shelf.Width, Height = shelf.Height, SizeToContent = SizeToContent.WidthAndHeight,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        shelfWindow.SetResourceReference(Control.BackgroundProperty, "PanelSurface");
        shelfWindow.Show(); Pump();
        try
        {
        typeof(ClipboardView).GetMethod("OpenPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [files]); Pump(); var state = view.CapturePresentation();
        var preview = card.PreviewBody; var shelfPreview = shelf.PreviewBody;
        var palettes = new[]
        {
            PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!),
            PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Light)!),
            PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.PixelBlocks)!),
            PanelThemes.Build("fixture-custom", new PanelThemeFile { Base = PanelThemes.Light, Colors = new() { ["fill"] = "#E7EFF7", ["accent"] = "#285DA8" } }),
            PanelThemes.HighContrast(new(255, 0, 0, 0), new(255, 255, 255, 255), new(255, 190, 190, 190), new(255, 255, 255, 0), new(255, 0, 0, 0)),
        };
        foreach (var palette in palettes)
        {
            foreach (var (resource, token) in new[] { ("PanelSurface", "surface"), ("PanelPrimary", "textPrimary"), ("PanelSecondary", "textSecondary"), ("PanelTertiary", "textTertiary"), ("PanelAccent", "accent"), ("PanelTrack", "track"), ("PanelFill", "fill"), ("PanelFillSelected", "fillSelected"), ("PanelHover", "hover"), ("PanelPressed", "pressed"), ("PanelSwitchFill", "switchFill"), ("PanelPopup", "popup"), ("PanelPopupBorder", "popupBorder") })
            { var color = palette[token]; app.Resources[resource] = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)); }
            for (int r = 1; r <= 16; r++) app.Resources["PanelR" + r] = new CornerRadius(palette.Effects.Pixel > 0 ? 0 : r);
            app.Resources["ClipboardDockCardRadius"] = new CornerRadius(palette.Effects.Pixel > 0 ? 0 : 18);
            Pump();
            check($"EdgeDrop themes: {palette.Id} clipboard fill follows the palette", Equals(card.Background, app.Resources["PanelFill"]));
            check($"EdgeDrop themes: {palette.Id} shelf fill follows the palette", Equals(shelf.Background, app.Resources["PanelFill"]));
            check($"EdgeDrop themes: {palette.Id} retains both preview bodies", ReferenceEquals(preview, card.PreviewBody) && ReferenceEquals(shelfPreview, shelf.PreviewBody));
            check($"EdgeDrop themes: {palette.Id} keeps query, selection, scroll and expanded preview", view.SearchText == "Synthetic item 9" && view.CapturePresentation() == state && view.PreviewOpen);
            check($"EdgeDrop themes: {palette.Id} updates both card clips with the theme", card.Clip is RectangleGeometry clip && clip.RadiusX == card.CornerRadius.TopLeft
                && shelf.Clip is RectangleGeometry shelfClip && shelfClip.RadiusX == shelf.CornerRadius.TopLeft);
            if (renderDirectory is not null)
            {
                Directory.CreateDirectory(renderDirectory);
                shelf.Measure(new Size(shelf.Width, shelf.Height)); shelf.Arrange(new Rect(0, 0, shelf.Width, shelf.Height)); shelf.UpdateLayout();
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                {
                    // Card fills may be translucent; composite them over the host surface, as the island does.
                    context.DrawRectangle((Brush)app.Resources["PanelSurface"], null, new Rect(0, 0, shelf.Width, shelf.Height));
                    context.DrawRectangle(new VisualBrush(shelf), null, new Rect(0, 0, shelf.Width, shelf.Height));
                }
                var bitmap = new RenderTargetBitmap((int)shelf.Width, (int)shelf.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string path = Path.Combine(renderDirectory, "edgedrop-shelf-card-" + palette.Id + ".png"); using var stream = File.Create(path); encoder.Save(stream);
                Console.WriteLine("RENDER: " + path);
            }
        }
        bool animations = ((App)app).Settings.Animations;
        try
        {
            ((App)app).Settings.Animations = false;
            Soft.Enter(shelf);
            check("EdgeDrop motion: disabled animation leaves shared card fully visible", shelf.Opacity == 1 && !shelf.HasAnimatedProperties);
        }
        finally { ((App)app).Settings.Animations = animations; view.ClosePopups(); }
        }
        finally { shelfWindow.Close(); }
    }
    private static void Algorithms(Action<string, bool> check)
    {
        var side = new PreviewImageBounds(440, 526, 560, 40, 1); var top = side with { MaxWidth = 760, MaxHeight = 380 };
        check("EdgeDrop upstream image-fit: screen screenshot", EdgeDropPresentation.FitImage(1920, 1080, side) == new PreviewImageFit(526, 486, 273, false));
        check("EdgeDrop upstream image-fit: wide preview", EdgeDropPresentation.FitImage(1920, 1080, top) == new PreviewImageFit(716, 676, 380, false));
        check("EdgeDrop upstream image-fit: long screenshot scrolls", EdgeDropPresentation.FitImage(1080, 2400, side) == new PreviewImageFit(526, 486, 1080, true));
        check("EdgeDrop upstream image-fit: portrait fits", EdgeDropPresentation.FitImage(600, 900, side) == new PreviewImageFit(440, 373, 560, false));
        check("EdgeDrop upstream image-fit: small icon cap", EdgeDropPresentation.FitImage(48, 48, side) == new PreviewImageFit(440, 96, 96, false) && EdgeDropPresentation.FitImage(48, 48, side with { Dpi = 2 }).Width == 48);
        check("EdgeDrop upstream image-fit: medium image", EdgeDropPresentation.FitImage(300, 200, side) == new PreviewImageFit(440, 400, 267, false));
        check("EdgeDrop upstream image-fit: bad dimensions", EdgeDropPresentation.FitImage(double.NaN, 100, side).FlyoutWidth == 440);
        foreach (var (w, h) in new[] { (5000, 5000), (10000, 200), (200, 10000), (1, 1), (4096, 2160), (2560, 1600) })
            foreach (var bounds in new[] { side, top, side with { Dpi = 1.5 } })
            { var fit = EdgeDropPresentation.FitImage(w, h, bounds); check($"EdgeDrop upstream image bounds: {w}x{h}, {bounds.Dpi}", fit.FlyoutWidth >= bounds.MinWidth && fit.FlyoutWidth <= bounds.MaxWidth && fit.Width <= bounds.MaxWidth - bounds.SidePadding && (fit.Scroll || fit.Height <= bounds.MaxHeight)); }
        var pr = EdgeDropPresentation.Link("https://github.com/facebook/react/pull/28000?utm_source=fixture");
        check("EdgeDrop upstream offline link: GitHub PR and tracking", pr.Service == "GitHub" && pr.Subtitle == "Pull request" && pr.Title == "facebook/react #28000" && !pr.DisplayUrl.Contains("utm_"));
        check("EdgeDrop upstream offline link: reserved site pages", EdgeDropPresentation.Link("https://github.com/features/copilot").Title == "Copilot" && EdgeDropPresentation.Link("https://github.com/features/copilot").Subtitle == "");
        check("EdgeDrop upstream offline link: exact domain boundary", EdgeDropPresentation.Link("https://github.com.attacker.example/foo/bar").Service != "GitHub" && EdgeDropPresentation.Link("https://www.max.com/movies/dune").Service != "X");
        check("EdgeDrop upstream offline link: redirects are decoded locally", EdgeDropPresentation.CleanLink("https://www.google.com/url?q=https%3A%2F%2Fexample.com%2Farticle%3Futm_source%3Dx%26keep%3D1") == "https://example.com/article?keep=1");
        check("EdgeDrop upstream offline link: query order, hash and original retained", EdgeDropPresentation.StripTracking("https://www.example.com/a/?x=1&utm_source=x&y=2#h") == "https://www.example.com/a/?x=1&y=2#h");
        check("EdgeDrop upstream offline link: short link is honest", EdgeDropPresentation.Link("https://bit.ly/abc123").Subtitle == "Short link" && EdgeDropPresentation.Link("https://bit.ly/abc123").Title == "");
        check("EdgeDrop upstream offline link: local addresses", EdgeDropPresentation.Link("http://localhost:3000/design").Service == "This computer" && EdgeDropPresentation.Link("http://192.168.1.1/a").Service == "Local network");
        check("EdgeDrop upstream offline link: broken encoding stays inert", EdgeDropPresentation.Link("https://example.com/foo/%E3%82").DisplayUrl.Length > 0);
        check("EdgeDrop upstream stack geometry: files and gallery", EdgeDropPresentation.StackOpenWidth(3, false) == 492 && EdgeDropPresentation.StackOpenWidth(3, true) == 426);
    }
    private static byte[] Image()
    {
        var pixels = Enumerable.Repeat((byte)200, 48 * 48 * 4).ToArray(); var bitmap = BitmapSource.Create(48, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 48 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static DragEventArgs Drag(Button button, IDataObject data)
    {
        var constructor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null)!;
        var args = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, button, new Point(1, 1)]); args.RoutedEvent = DragDrop.DropEvent; return args;
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> All<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is T found) yield return found; foreach (var nested in All<T>(child)) yield return nested; }
    }
}
