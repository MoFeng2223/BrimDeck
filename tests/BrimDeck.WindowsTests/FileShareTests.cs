using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Native;
using Windows.ApplicationModel.DataTransfer;

internal static class FileShareTests
{
    // Synthetic files and unshown controls only: never launch Share Sheet, read the clipboard or load the user's tray.
    public static void Run(Action<string, bool> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "BrimDeck-FileShare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "source.txt"), folder = Path.Combine(root, "source-folder");
            string missing = Path.Combine(root, "missing.txt");
            File.WriteAllText(file, "source remains in place");
            Directory.CreateDirectory(folder);
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, file);
            var normalized = FileShareService.NormalizePaths([relative, file, file, folder]);
            check("file sharing: original files and folders normalize to unique existing absolute paths", normalized.Length == 2
                && normalized[0] == Path.GetFullPath(file) && normalized[1] == Path.GetFullPath(folder));
            check("file sharing: a missing member rejects the whole selection", FileShareService.NormalizePaths([file, missing]).Length == 0);
            check("file sharing: blank or invalid members never produce a partial share", FileShareService.NormalizePaths([file, " "]).Length == 0
                && FileShareService.NormalizePaths([null!, "", missing]).Length == 0
                && FileShareService.NormalizePaths([file, "\0"]).Length == 0);
            var external = new DataObject(DataFormats.FileDrop, new[] { file });
            var own = new DataObject(DataFormats.FileDrop, new[] { file }); own.SetData(FileShareService.TrayOriginFormat, "card");
            check("file sharing: external drops are accepted while island-originated drags are rejected", FileShareService.IsExternalFileDrop(external, false)
                && !FileShareService.IsExternalFileDrop(own, false) && !FileShareService.IsExternalFileDrop(external, true));

            using var unavailable = new FileShareService(() => IntPtr.Zero);
            check("file sharing: an unavailable island handle fails without opening a share UI", !unavailable.TryShare([file], "Direct share", out var problem)
                && problem.Length > 0 && File.Exists(file));
            unavailable.Dispose();
            check("file sharing: disposing the service rejects later requests without touching the source", !unavailable.TryShare([file], "Direct share", out var disposedProblem)
                && disposedProblem.Length > 0 && File.Exists(file));

            CheckDropSurface(file, missing, external, own, check);
            CheckSourceMenu(file, folder, missing, check);
            var package = new DataPackage();
            Complete(FileShareService.PopulateAsync(package, normalized, "Original selection"));
            var items = Complete(package.GetView().GetStorageItemsAsync().AsTask());
            check("file sharing: native payload references the original file and folder, not copies", items.Select(item => item.Path).SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase));
            check("file sharing: native payload requests copy and preserves its title", package.RequestedOperation == DataPackageOperation.Copy
                && package.Properties.Title == "Original selection");
            var interrupted = new DataPackage(); bool failed = false;
            try { Complete(FileShareService.PopulateAsync(interrupted, [file, missing], "Disappeared source")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { failed = true; }
            check("file sharing: a disappearing source cannot publish a partial native payload", failed && !interrupted.GetView().Contains(StandardDataFormats.StorageItems));
            check("file sharing: all flows leave original contents intact without staging files", File.ReadAllText(file) == "source remains in place"
                && Directory.Exists(folder) && Directory.GetFileSystemEntries(root).Length == 2 && Directory.GetFileSystemEntries(folder).Length == 0);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CheckDropSurface(string file, string missing, IDataObject external, IDataObject own, Action<string, bool> check)
    {
        var child = new Button { Content = "Page content" };
        var content = new Grid(); content.Children.Add(child);
        var island = new IslandBorder { Width = 238, Height = 32, Background = Brushes.Black, Child = content };
        bool enabled = true, exporting = false, highlighted = false;
        int shares = 0, childDrops = 0; string[]? shared = null; string feedback = "";
        child.PreviewDrop += (_, _) => childDrops++;
        using var target = new FileShareDropTarget(island, () => enabled, () => exporting,
            paths => { shares++; shared = paths; }, shown => highlighted = shown, message => feedback = message);
        Layout();
        var hover = Raise(child, external, UIElement.PreviewDragEnterEvent);
        check("file sharing: collapsed island accepts external hover without starting a share", hover.Handled && hover.Effects == DragDropEffects.Copy
            && target.IsDragging && highlighted && shares == 0);
        Raise(child, external, UIElement.PreviewDragLeaveEvent, point: new Point(-2, -2));
        check("file sharing: leaving the island clears the cue without sharing", !target.IsDragging && !highlighted && shares == 0);
        Raise(child, external, UIElement.PreviewDragOverEvent);
        var drop = Raise(child, external, UIElement.PreviewDropEvent);
        var duplicate = Raise(child, external, UIElement.PreviewDropEvent);
        check("file sharing: drop is deferred beyond the OLE event and never reaches a child page", drop.Handled && drop.Effects == DragDropEffects.Copy
            && !target.IsDragging && !highlighted && shares == 0 && childDrops == 0 && duplicate.Effects == DragDropEffects.None);
        Flush();
        check("file sharing: repeated routed drops schedule exactly one original-path share", shares == 1 && shared is [var path] && path == file);
        exporting = true;
        var exportingDrop = Raise(child, external, UIElement.PreviewDropEvent);
        exporting = false;
        var returnedDrop = Raise(child, own, UIElement.PreviewDropEvent);
        Flush();
        check("file sharing: dragging out and dropping an island-originated file back never opens sharing", exportingDrop.Effects == DragDropEffects.None
            && returnedDrop.Effects == DragDropEffects.None && shares == 1 && childDrops == 0);
        var move = Raise(child, external, UIElement.PreviewDropEvent, DragDropEffects.Move);
        enabled = false;
        var disabled = Raise(child, external, UIElement.PreviewDropEvent);
        enabled = true;
        Flush();
        check("file sharing: move-only gestures and disabled island entry never share", move.Effects == DragDropEffects.None
            && disabled.Effects == DragDropEffects.None && shares == 1);
        var internalData = new DataObject(DataFormats.UnicodeText, "internal gesture");
        var untouched = Raise(child, internalData, UIElement.PreviewDropEvent);
        check("file sharing: non-file drags retain their child-page owner", !untouched.Handled && childDrops == 1 && shares == 1);
        var absent = Raise(child, new DataObject(DataFormats.FileDrop, new[] { file, missing }), UIElement.PreviewDropEvent);
        Flush();
        check("file sharing: missing dropped sources show feedback without a partial share", absent.Effects == DragDropEffects.None && feedback.Length > 0 && shares == 1);
        island.Width = 760; island.Height = 240; Layout();
        Raise(child, external, UIElement.PreviewDragOverEvent);
        Raise(child, external, UIElement.PreviewDropEvent); Flush();
        check("file sharing: the same root handler serves expanded pages too", shares == 2 && !highlighted);
        Raise(child, external, UIElement.PreviewDropEvent);
        target.Dispose(); Flush();
        check("file sharing: closing cancels queued shares and restores the surface's drop setting", shares == 2 && !island.AllowDrop
            && island.ReadLocalValue(UIElement.AllowDropProperty) == DependencyProperty.UnsetValue && !highlighted);

        void Layout() { island.Measure(new Size(island.Width, island.Height)); island.Arrange(new Rect(0, 0, island.Width, island.Height)); island.UpdateLayout(); }
    }

    private static void CheckSourceMenu(string file, string folder, string missing, Action<string, bool> check)
    {
        string[]? shared = null;
        var menu = FileShareDropTarget.SourceMenuItem(file, paths => shared = paths);
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check("file sharing: source-menu sharing waits for the menu to close", menu.IsEnabled && shared is null);
        Flush();
        check("file sharing: source-menu action passes the original path directly", shared is [var path] && path == file);
        var directory = FileShareDropTarget.SourceMenuItem(folder, paths => shared = paths);
        directory.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Flush();
        check("file sharing: source-menu supports folders and disables missing originals", shared is [var directoryPath] && directoryPath == folder
            && !FileShareDropTarget.SourceMenuItem(missing, _ => { }).IsEnabled);
    }

    private static DragEventArgs Raise(UIElement target, IDataObject data, RoutedEvent routedEvent, DragDropEffects allowed = DragDropEffects.Copy, Point? point = null)
    {
        var constructor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null)!;
        var args = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.LeftMouseButton, allowed, target, point ?? new Point(1, 1)]);
        args.RoutedEvent = routedEvent; target.RaiseEvent(args); return args;
    }

    private static void Flush()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Complete(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            long deadline = Environment.TickCount64 + 10000;
            timer.Tick += (_, _) => { if (task.IsCompleted || Environment.TickCount64 >= deadline) frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            if (!task.IsCompleted) throw new TimeoutException("Synthetic file-share payload did not complete within ten seconds");
        }
        task.GetAwaiter().GetResult();
    }

    private static T Complete<T>(Task<T> task) { Complete((Task)task); return task.GetAwaiter().GetResult(); }
}
