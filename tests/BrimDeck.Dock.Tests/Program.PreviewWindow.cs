using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using DropShelf;
using DropShelf.Views;

internal static partial class Program
{
    private static void WaitPreview(Func<bool> ready)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (!ready() && Environment.TickCount64 < deadline) { Pump(); Thread.Sleep(1); }
        Check("visible preview: deferred image work completes on its owning dispatcher", ready());
    }

    // Kept explicit: legacy placement/follow regressions cover automatic hover, not the detached manual window.
    private static void OpenAutomaticPreview(ClipboardView view, ClipboardEntry entry) => Call(view, "OpenPreview", entry);

    private static void CheckPreviewWindows(App app, BrimDeck.MainWindow host, DeckSettings settings, PanelPalette[] palettes, string scratch, string directory)
    {
        using var shelf = new AppController(Dispatcher.CurrentDispatcher, new EmbeddedHostOptions
        {
            DataDirectory = Path.Combine(scratch, "preview-shelf-data"), CacheDirectory = Path.Combine(scratch, "preview-attachments"),
            AnimationsEnabled = () => settings.Animations, Exit = () => { }, GetStartWithWindows = () => false, SetStartWithWindows = _ => true,
        });
        var oldDress = HostedResources.DressShelf; var oldSquare = HostedResources.SquareCorners; var oldMotion = HostedResources.MotionEase;
        HostedResources.DressShelf = (window, root) => typeof(App).GetMethod("DressShelf", Private)!.Invoke(app, [window, root]);
        HostedResources.SquareCorners = () => PanelTheme.Current.Effects.Pixel > 0; HostedResources.MotionEase = Soft.Out;
        var preferences = new ClipboardSettings { Enabled = false, MaximumItems = 0, RetentionDays = 0, PasteOnActivate = false };
        var history = new ClipboardHistory(); var now = DateTimeOffset.UtcNow;
        string file = Path.Combine(scratch, "preview-original.txt"); File.WriteAllText(file, "Synthetic preview original.");
        var text = history.Add(ClipboardContent.FromText(string.Join("\n", Enumerable.Range(0, 150).Select(i => "Synthetic preview line " + i))), "Fixture", "", now, preferences)!;
        var image = history.Add(new ClipboardContent { Kind = ClipboardContentKind.Image, Image = Image(), PixelWidth = 400, PixelHeight = 300 }, "Fixture", "", now.AddMinutes(-1), preferences)!;
        var files = history.Add(new ClipboardContent { Kind = ClipboardContentKind.Files, Files = [file] }, "Fixture", "", now.AddMinutes(-2), preferences)!;
        var calls = new List<(ClipboardEntry Entry, bool CopyOnly, IntPtr Target)>();
        using var previews = new ClipboardPreviewWindows(history, () => preferences, (entry, copyOnly, target) => { calls.Add((entry, copyOnly, target)); return true; }, () => new IntPtr(123));
        var view = new ClipboardView(history, () => preferences, (_, _) => throw new InvalidOperationException("Unexpected island activation"), () => { }, () => { }, () => { }, () => (true, false, ""), _ => { }, () => { }, openPreviewWindow: previews.Show);
        var owner = new Window { Content = view, Width = 1080, Height = 210, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        Button Button(ClipboardPreviewWindow window, string id) => All<Button>(window).Single(button => AutomationProperties.GetAutomationId(button) == "preview-window-" + id);
        ClipboardPreviewWindow Window(string id) => previews.Windows.Single(window => window.Entry.Id == id);
        try
        {
            owner.Show(); Pump();
            foreach (var palette in palettes)
            foreach (double scale in new[] { 1d, 1.5d })
            {
                Palette(palette); typeof(App).GetMethod("PublishShelfTheme", Private)!.Invoke(app, null);
                var card = new Rect(600, 80, 210, 146); var panel = new Rect(400, 34, 1080, 210);
                previews.Show(image, card, panel, scale); Pump(); var window = Window(image.Id);
                WaitPreview(() => window.PreviewBody is Image { Source: not null });
                var screen = BrimDeck.Native.WindowsHost.WorkAreaAt(new Point(700, 160));
                var body = window.PreviewBody!;
                Check("preview window " + palette.Id + "-" + scale + ": uses actual independent shelf chrome and title", window is ShelfToolWindow && window.Title == Loc.T("预览浮窗", "Preview window") && window.Owner is null && !window.ShowInTaskbar && window.Topmost && new WindowInteropHelper(window).Handle != new WindowInteropHelper(owner).Handle);
                Check("preview window " + palette.Id + "-" + scale + ": pin and close reuse original shelf styles", All<ToggleButton>(window).Single().Style == window.FindResource("IconToggleButtonStyle") && Button(window, "close").Style == window.FindResource("IconButtonStyle"));
                var chrome = All<IslandBorder>(window).Single();
                Check("preview window " + palette.Id + "-" + scale + ": palette effects and square theme match shelves", chrome.CornerRadius.TopLeft == (palette.Effects.Pixel > 0 ? 0 : 12) && chrome.Background is SolidColorBrush fill && fill.Color == PanelTheme.ColorOf("surface"));
                var topLeft = window.PointToScreen(new Point()); var size = new Size(window.ActualWidth * VisualTreeHelper.GetDpi(window).DpiScaleX, window.ActualHeight * VisualTreeHelper.GetDpi(window).DpiScaleY);
                Check("preview window " + palette.Id + "-" + scale + ": placement and content scale stay in work area", screen.Contains(new Rect(topLeft, size)) && Math.Abs(window.Width - Math.Min(440 * scale, screen.Width / VisualTreeHelper.GetDpi(window).DpiScaleX - 24)) < 2);
                Check("preview window " + palette.Id + "-" + scale + ": local image is full inert content, not a file card", body is Image { Source: not null, Stretch: Stretch.Uniform } && !window.AllowDrop && !view.PreviewOpen && !view.PopupOpen);
                Render((FrameworkElement)window.Content, directory, "preview-window-" + palette.Id + "-" + scale + ".png");
                window.CloseImmediately(); Pump();
            }
            Palette(palettes[0]); typeof(App).GetMethod("PublishShelfTheme", Private)!.Invoke(app, null); view.ShowPreview(text); Pump(); var first = Window(text.Id);
            Check("manual preview: explicit entry point opens a window without pinning hover Popup", first.IsVisible && !view.PreviewOpen && !view.PopupOpen && All<Button>(view).Any(button => AutomationProperties.GetName(button) == Loc.T("预览浮窗", "Preview window")));
            var content = first.PreviewBody; var scroll = (ScrollViewer)content!; scroll.ScrollToVerticalOffset(120); Pump(); double offset = scroll.VerticalOffset;
            first.Left = 220; first.Top = 260; view.ShowPreview(text); Pump();
            Check("manual preview: repeated request reuses same entry window and moved position", previews.Windows.Count == 1 && ReferenceEquals(first, Window(text.Id)) && first.Left == 220 && first.Top == 260 && ReferenceEquals(content, first.PreviewBody));
            view.ShowPreview(image); Pump();
            Check("manual preview: different entries have independent windows, not a shared stale flyout", previews.Windows.Count == 2 && Window(image.Id) != first && first.Entry.Id == text.Id);
            Button(first, "copy").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            Check("manual preview copy: explicit whole-entry copy preserves the floating window and its original payload", calls.Count == 1 && calls[0].CopyOnly && ReferenceEquals(calls[0].Entry.Content, text.Content) && first.IsVisible && calls[0].Target == new IntPtr(123));
            All<ToggleButton>(first).Single().IsChecked = false; Pump(); Check("manual preview pin: unpinning changes only topmost, never history favorite", !first.Topmost && !history.Items.Single(item => item.Id == text.Id).Favorite);
            view.Visibility = Visibility.Collapsed; Pump(); view.ClosePopups();
            Check("manual preview lifetime: hiding island and closing its popups leave floating windows open", first.IsVisible && Window(image.Id).IsVisible && !view.PreviewOpen);
            view.Dispose(); owner.Close(); Pump();
            Check("manual preview lifetime: view destruction does not own detached windows", first.IsVisible && Window(image.Id).IsVisible && previews.Windows.Count == 2);
            history.ToggleFavorite(text.Id); Pump();
            Check("manual preview metadata: shared history update preserves decoded content, scroll and moved geometry", first.Entry.Favorite && ReferenceEquals(content, first.PreviewBody) && Math.Abs(scroll.VerticalOffset - offset) < 1 && first.Left == 220 && first.Top == 260);
            var previousRegistry = Get(app, "_clipboardPreviews"); string previousLanguage = settings.Language;
            try
            {
                Set(app, "_clipboardPreviews", previews); settings.Language = Loc.English;
                typeof(App).GetMethod("UseLanguage", Private)!.Invoke(app, null);
            }
            finally { Set(app, "_clipboardPreviews", previousRegistry); settings.Language = previousLanguage; }
            Palette(palettes[1]); typeof(App).GetMethod("PublishShelfTheme", Private)!.Invoke(app, null); Pump();
            Check("manual preview theme/language: existing HWND, scroll and location survive refresh", first.Title == "Preview window" && ReferenceEquals(content, first.PreviewBody) && Math.Abs(scroll.VerticalOffset - offset) < 1 && first.Left == 220 && !first.Topmost);
            previews.Show(files, new Rect(300, 80, 210, 146), new Rect(200, 34, 1080, 210), 1); Pump(); var fileWindow = Window(files.Id);
            Check("manual preview files: per-file copy and drag reference the original without a second shelf model", All<TextBlock>(fileWindow).Any(block => block.Text == "preview-original.txt") && fileWindow.Entry.Content.Files.Single() == file && File.Exists(file) && shelf.Shelves.OpenShelves.Count == 0);
            var keyboard = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(first), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Button(first, "copy").RaiseEvent(keyboard);
            Check("manual preview keyboard: Enter on a focused action is left to that button without unintended paste", !keyboard.Handled && calls.Count == 1 && first.IsVisible);
            var failed = new ClipboardPreviewWindow(history, files, () => preferences, (_, _) => false);
            try
            {
                failed.ShowNear(new Rect(300, 80, 210, 146), new Rect(200, 34, 1080, 210), 1); Pump();
                Button(failed, "paste").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
                Check("manual preview failure: unsuccessful copy leaves original content open and announces the failure", failed.IsVisible && !failed.IsClosing && failed.Entry.Content.Files.Single() == file && All<TextBlock>(failed).Any(block => AutomationProperties.GetAutomationId(block) == "preview-window-notice" && block.IsVisible && block.Text.Length > 0) && File.Exists(file));
            }
            finally { failed.CloseImmediately(); }
            Button(fileWindow, "paste").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            Check("manual preview paste: explicit action closes only its own window and uses its saved receiver", fileWindow.IsClosing && calls.Count == 2 && !calls[^1].CopyOnly && calls[^1].Entry.Id == files.Id && calls[^1].Target == new IntPtr(123) && first.IsVisible);
            var imageWindow = Window(image.Id);
            WaitPreview(() => imageWindow.PreviewBody is Image { Source: not null });
            imageWindow.Hide(); Pump();
            Check("manual preview visibility: hidden windows release their decoded image reference", imageWindow.PreviewBody is Image { Source: null });
            imageWindow.Show(); WaitPreview(() => imageWindow.PreviewBody is Image { Source: not null });
            imageWindow.WindowState = WindowState.Minimized; Pump();
            Check("manual preview visibility: minimizing releases decoded pixels without dropping the history entry", imageWindow.PreviewBody is Image { Source: null } && history.Items.Any(item => item.Id == image.Id));
            imageWindow.WindowState = WindowState.Normal; WaitPreview(() => imageWindow.PreviewBody is Image { Source: not null });
            history.Remove(image.Id); Pump();
            Check("manual preview retention: deleting history closes its detached content without deleting originals", !previews.Windows.Any(window => window.Entry.Id == image.Id) && first.IsVisible && File.Exists(file));
            previews.Dispose(); Pump();
            Check("manual preview shutdown: app registry closes all windows without changing history or enabling capture", previews.Windows.Count == 0 && !first.IsVisible && history.Items.Count == 2 && !preferences.Enabled && calls.Count == 2 && File.ReadAllText(file) == "Synthetic preview original.");
        }
        finally
        {
            previews.Dispose(); view.Dispose(); if (owner.IsVisible) owner.Close(); settings.Animations = false; Loc.Use(Loc.Chinese);
            HostedResources.DressShelf = oldDress; HostedResources.SquareCorners = oldSquare; HostedResources.MotionEase = oldMotion;
        }
    }
}
