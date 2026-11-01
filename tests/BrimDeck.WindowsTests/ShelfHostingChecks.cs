using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DropShelf;
using DropShelf.Interop;
using DropShelf.Models;
using DropShelf.Services;
using DropShelf.Views;

internal static class ShelfHostingChecks
{
    // No App startup, real clipboard, global hooks or registered shortcuts: every input and stored file belongs to this fixture.
    public static void Run(Action<string, bool> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "BrimDeck-ShelfHost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // 与正常 WPF 消息循环一致：异步拖入完成后仍由 UI 线程更新绑定的集合。
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var application = new BrimDeck.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var hostStyle = new Style(typeof(Button));
        application.Resources[typeof(Button)] = hostStyle;
        application.Resources["TextPrimaryBrush"] = Brushes.Magenta;
        bool startup = false, exited = false;
        using var controller = new AppController(Dispatcher.CurrentDispatcher, new EmbeddedHostOptions
        {
            DataDirectory = Path.Combine(root, "data"), CacheDirectory = Path.Combine(root, "attachments"),
            Exit = () => exited = true, GetStartWithWindows = () => startup,
            SetStartWithWindows = enabled => { startup = enabled; return true; }, AnimationsEnabled = () => false,
        });
        try
        {
            ThemeService.Initialize(AppTheme.Light, null); LanguageService.SetMode(AppLanguage.Chinese);
            check("shelf host: data and attachment roots are isolated", controller.IsEmbedded
                && controller.SettingsPath == Path.Combine(root, "data", "settings.json")
                && VirtualFileDropService.CacheRoot == Path.Combine(root, "attachments"));
            controller.SetStartWithWindows(true);
            check("shelf host: startup delegates to the host, not a second startup registration", startup && controller.Settings.StartWithWindows);
            controller.Exit();
            check("shelf host: exit delegates without shutting down the WPF application", exited && !application.Dispatcher.HasShutdownStarted);
            check("shelf host: original motion follows the host animation preference", !ShelfMotion.Enabled);
            CheckSharedModel(controller, root, check);
            CheckDropSurface(controller, root, check);
            CheckOriginalWindows(controller, application, hostStyle, check);
            ShelfSettingsChecks.Run(application, root, check);
        }
        finally
        {
            controller.Dispose();
            foreach (Window window in application.Windows.Cast<Window>().ToArray()) window.Close();
            WaitUntil(() => !Directory.Exists(Path.Combine(root, "attachments")) || !Directory.EnumerateDirectories(Path.Combine(root, "attachments")).Any(), 10000);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void CheckSharedModel(AppController controller, string root, Action<string, bool> check)
    {
        using var tray = new ShelfTrayAdapter(controller.Shelves);
        var first = controller.Shelves.ActiveWindow;
        string file = Path.Combine(root, "ordinary.txt"), moved = Path.Combine(root, "moved.txt");
        File.WriteAllText(file, "ordinary source");
        int changes = 0, persisted = 0;
        tray.Changed += () => changes++;
        tray.PersistedChanged += () => persisted++;
        first.ViewModel.AddFiles([file]);
        check("shelf host: island and original shelf share the exact collection and item", ReferenceEquals(tray.Items, first.ViewModel.Items)
            && ReferenceEquals(tray.Items[0], first.ViewModel.Items[0]) && changes == 1 && persisted == 1);
        var item = tray.Items[0];
        int metadataBaseline = changes, persistedBaseline = persisted;
        item.TypeName = "fixture type"; item.Thumbnail = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, 4);
        check("shelf host: same-count metadata and thumbnail changes refresh without disk saves", changes == metadataBaseline + 2 && persisted == persistedBaseline);
        metadataBaseline = changes; item.Pinned = true;
        check("shelf host: pins notify and persist without a count change", changes == metadataBaseline + 1 && persisted == persistedBaseline + 1);
        item.Pinned = false;
        item.AddedAt = DateTimeOffset.Now.AddHours(-1); tray.TogglePin(item.Id);
        File.Move(file, moved);
        first.ViewModel.ApplyMoves([(file, moved)]);
        check("shelf host: original actions preserve island pin and retention metadata", tray.Items[0].Value == moved
            && tray.Items[0].Pinned && tray.Items[0].AddedAt == item.AddedAt);
        first.ViewModel.AddEntries([new DroppedEntry(ShelfItemKind.Text, "second item")]);
        tray.Reorder(0, 1); tray.Announce();
        check("shelf host: island reordering updates the original shelf in place", first.ViewModel.Items[1].Value == moved);
        tray.Clear(keepPinned: true);
        check("shelf host: clearing the island keeps pins and does not delete source files", first.ViewModel.Count == 1 && File.ReadAllText(moved) == "ordinary source");
        check("shelf host: old item age does not trigger retired automatic cleanup", tray.Items.Count == 1 && item.AddedAt < DateTimeOffset.Now.AddMinutes(-5));
        tray.TogglePin(tray.Items[0].Id);
        check("shelf host: explicit removal keeps the source file", tray.Remove(item.Id) && File.Exists(moved));
        first.Close();
        int baseline = changes;
        var replacement = controller.Shelves.ActiveWindow;
        replacement.ViewModel.AddEntries([new DroppedEntry(ShelfItemKind.Text, "after closing the last shelf")]);
        check("shelf host: closing the last shelf switches the island once without duplicate subscriptions", !ReferenceEquals(first, replacement)
            && ReferenceEquals(tray.Model, replacement.ViewModel) && changes == baseline + 1);
        var second = controller.Shelves.OpenAt(new POINT(-20000, -20000), false, false);
        check("shelf host: opening another shelf never reuses a populated hidden island shelf", !ReferenceEquals(second, replacement)
            && replacement.ViewModel.Count == 1 && ReferenceEquals(tray.Model, second.ViewModel));
        second.Close(); replacement.Close();
    }

    private static void CheckDropSurface(AppController controller, string root, Action<string, bool> check)
    {
        var child = new Button { Content = "Existing island page" };
        var surface = new Border { Child = child, Width = 300, Height = 40, Background = Brushes.Black };
        var owner = new Window { Content = surface, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000, Width = 300, Height = 40 };
        surface.Measure(new Size(300, 40)); surface.Arrange(new Rect(0, 0, 300, 40));
        bool enabled = true, exporting = false, highlight = false;
        string problem = "";
        var shelf = controller.Shelves.ActiveWindow;
        using var target = new ShelfIslandDropTarget(owner, surface, () => controller.Shelves.ActiveWindow,
            () => enabled, () => exporting, shown => highlight = shown, message => problem = message);
        try
        {
            string file = Path.Combine(root, "temporary-folder-ordinary.txt");
            File.WriteAllText(file, "original path even under TEMP");
            var data = new DataObject(DataFormats.FileDrop, new[] { file });
            var over = Raise(child, data, UIElement.PreviewDragEnterEvent);
            check("shelf drop: collapsed island accepts external file hover", over.Handled && over.Effects == DragDropEffects.Copy && highlight && target.IsDragging);
            var drop = Raise(child, data, UIElement.PreviewDropEvent);
            WaitUntil(() => !target.IsReceiving);
            check("shelf drop: ordinary files under TEMP remain original paths with zero cache copies", drop.Effects == DragDropEffects.Copy
                && shelf.ViewModel.Items.Single().Value == file && !shelf.ViewModel.Items[0].IsTemporaryAttachment && shelf.ViewModel.CacheDirectories.Count == 0);
            check("shelf drop: completing a drop releases the highlight", !target.IsDragging && !target.IsReceiving && !highlight);
            var own = new DataObject(DataFormats.FileDrop, new[] { file }); own.SetData(DropDataReader.ShelfIdFormat, shelf.ViewModel.Id);
            check("shelf drop: returning a current-shelf drag is rejected", Raise(child, own, UIElement.PreviewDropEvent).Effects == DragDropEffects.None);
            var legacy = new DataObject(DataFormats.FileDrop, new[] { file }); legacy.SetData(ShelfIslandDropTarget.LegacyOriginFormat, "card");
            check("shelf drop: legacy island drags are rejected", Raise(child, legacy, UIElement.PreviewDropEvent).Effects == DragDropEffects.None);
            exporting = true;
            check("shelf drop: exporting from the island never triggers reimport", Raise(child, data, UIElement.PreviewDropEvent).Effects == DragDropEffects.None);
            exporting = false;
            var internalDrag = new DataObject(DataFormats.UnicodeText, "inert identifier");
            internalDrag.SetData("BrimDeck.ClipboardLibrary.Internal", "fixture:E:id");
            check("shelf drop: internal clipboard gestures retain their page owner", !Raise(child, internalDrag, UIElement.PreviewDropEvent).Handled);
            check("shelf drop: move-only external gestures cannot move sources into the shelf", Raise(child, data, UIElement.PreviewDropEvent, DragDropEffects.Move).Effects == DragDropEffects.None);
            enabled = false;
            check("shelf drop: a disabled island leaves the routed gesture alone", !Raise(child, data, UIElement.PreviewDropEvent).Handled);
            enabled = true;
            Raise(child, new DataObject(DataFormats.UnicodeText, "synthetic text"), UIElement.PreviewDropEvent);
            WaitUntil(() => !target.IsReceiving);
            check("shelf drop: text uses the original reader and model", shelf.ViewModel.Items.Any(item => item.Kind == ShelfItemKind.Text && item.Value == "synthetic text"));

            string image = Path.Combine(root, "volatile-image.png");
            WriteImage(image);
            Raise(child, new DataObject(DataFormats.FileDrop, new[] { image }), UIElement.PreviewDropEvent);
            File.Delete(image);
            WaitUntil(() => !target.IsReceiving);
            var snapshot = shelf.ViewModel.Items.Single(item => item.IsTemporaryAttachment);
            check("shelf drop: volatile images survive source deletion via controlled cache", snapshot.Value != image && File.Exists(snapshot.Value)
                && VirtualFileDropService.GetCacheDirectory(snapshot.Value) is not null);
            string snapshotDirectory = shelf.ViewModel.CacheDirectories.Single();

            var descriptor = new byte[4 + 592];
            BitConverter.GetBytes(1u).CopyTo(descriptor, 0);
            Encoding.Unicode.GetBytes("synthetic-attachment.txt\0").CopyTo(descriptor, 4 + 72);
            var attachment = new DataObject();
            attachment.SetData(VirtualFileDropService.FileDescriptorUnicodeFormat, new MemoryStream(descriptor));
            attachment.SetData(VirtualFileDropService.FileContentsFormat, new MemoryStream(Encoding.UTF8.GetBytes("virtual attachment")));
            Raise(child, attachment, UIElement.PreviewDropEvent);
            WaitUntil(() => !target.IsReceiving);
            var imported = shelf.ViewModel.Items.Single(item => item.Name == "synthetic-attachment.txt");
            check("shelf drop: virtual attachments retain upstream import and ownership", imported.IsTemporaryAttachment && File.ReadAllText(imported.Value) == "virtual attachment");
            string[] caches = shelf.ViewModel.CacheDirectories.ToArray();
            check("shelf drop: successful imports do not report failure", problem.Length == 0);
            shelf.Close();
            WaitUntil(() => caches.All(cache => !Directory.Exists(cache)));
            check("shelf drop: closing releases owned caches without touching ordinary files", caches.All(cache => !Directory.Exists(cache)) && File.Exists(file));
            Raise(child, data, UIElement.PreviewDragOverEvent);
            target.Dispose();
            check("shelf drop: disposal clears feedback and restores original AllowDrop", !highlight && !surface.AllowDrop
                && surface.ReadLocalValue(UIElement.AllowDropProperty) == DependencyProperty.UnsetValue);
        }
        finally { owner.Close(); }
    }

    private static void CheckOriginalWindows(AppController controller, Application application, Style hostStyle, Action<string, bool> check)
    {
        var shelf = controller.Shelves.ActiveWindow;
        shelf.Left = -20000; shelf.Top = -20000; shelf.ShowActivated = false; shelf.Show();
        using var content = new ShelfSettingsContent(controller);
        var manager = new Window { Content = content, Width = 760, Height = 650, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            manager.Show(); Pump();
            var actions = (System.Windows.Controls.Primitives.UniformGrid)shelf.FindName("ActionBar")!;
            check("shelf UI: floating shelf and embedded settings load original feature controls", actions.Children.Count == 4
                && content.FindName("ActionSlotsList") is not null && content.FindName("HistoryList") is not null && content.FindName("HotkeyEditor") is not null);
            var palette = new ResourceDictionary();
            foreach (var (key, color) in ThemeService.BuildPalette(true, AccentPalette.Default)) palette[key] = new SolidColorBrush(color);
            palette["SheetBrush"] = Brushes.Black;
            ThemeService.ApplyHostPalette(palette, true, Colors.Teal); Pump();
            check("shelf UI: host publishes the final accent without a second system derivation", ThemeService.AccentFill == Colors.Teal && ThemeService.IsDark);
            ThemeService.SetMode(AppTheme.Light, "#FF0000");
            check("shelf UI: independent theme settings cannot overwrite the host palette", ThemeService.IsDark && ThemeService.AccentFill == Colors.Teal);
            check("shelf UI: hosted controls use scoped styles and brushes", shelf.FindResource("TextPrimaryBrush") is SolidColorBrush brush
                && brush.Color == Colors.White && shelf.FindResource(typeof(Button)) is Style style && !ReferenceEquals(style, hostStyle));
            check("shelf UI: themes and implicit styles never replace the island's application resources", ReferenceEquals(application.Resources[typeof(Button)], hostStyle)
                && ReferenceEquals(application.Resources["TextPrimaryBrush"], Brushes.Magenta));
            var bitmap = new RenderTargetBitmap((int)manager.ActualWidth, (int)manager.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(manager);
            check("shelf UI: embedded settings render through the hosted resources", bitmap.PixelWidth > 400 && bitmap.PixelHeight > 300);
        }
        finally { manager.Close(); shelf.Close(); }
    }

    private static void WriteImage(string path)
    {
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static DragEventArgs Raise(UIElement target, IDataObject data, RoutedEvent routedEvent, DragDropEffects allowed = DragDropEffects.Copy)
    {
        var constructor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(IDataObject), typeof(System.Windows.DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null)!;
        var args = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.LeftMouseButton, allowed, target, new Point(1, 1)]);
        args.RoutedEvent = routedEvent; target.RaiseEvent(args); return args;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void WaitUntil(Func<bool> complete, int milliseconds = 10000)
    {
        long end = Environment.TickCount64 + milliseconds;
        while (!complete() && Environment.TickCount64 < end) { Pump(); Thread.Sleep(10); }
        if (!complete()) throw new TimeoutException("Isolated shelf operation exceeded its deadline");
    }
}
