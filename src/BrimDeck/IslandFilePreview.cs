// Translated from Edge-Drop ClipboardItem.FileFolderCard / StackItems.fileStackEntries,
// commit 767b6d63, Apache-2.0. Local, inert files only; no Shell execution or network thumbnails.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;

namespace BrimDeck;

internal static class IslandFilePreview
{
    private static readonly HashSet<string> ImageExtensions = new([".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp"], StringComparer.OrdinalIgnoreCase);
    internal static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
    internal static BitmapSource? Thumbnail(string path, int pixels = 256)
    {
        // Remote paths are displayed as files without opening a connection for their preview.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || !IsImage(path)) return null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length > 64 * 1024 * 1024) return null;
            string key = $"clipboard:file:{path.ToUpperInvariant()}|{file.LastWriteTimeUtc.Ticks}|{file.Length}|{pixels}";
            if (DropShelf.DecodedImageCache.Get(key) is { } cached) return cached;
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frame = BitmapDecoder.Create(source, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (Math.Max(frame.PixelWidth, frame.PixelHeight) > pixels)
            { if (frame.PixelWidth >= frame.PixelHeight) bitmap.DecodePixelWidth = pixels; else bitmap.DecodePixelHeight = pixels; }
            bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze();
            return DropShelf.DecodedImageCache.Add(key, bitmap);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException) { return null; }
    }
    internal static FrameworkElement Art(string path, double size, ImageSource? thumbnail = null, bool available = true, bool? directory = null, bool loadThumbnail = true)
    {
        var frame = new Border { Width = size, Height = size, BorderThickness = new Thickness(1), ClipToBounds = true };
        frame.SetResourceReference(Border.BackgroundProperty, "PanelTrack"); frame.SetResourceReference(Border.BorderBrushProperty, "PanelPopupBorder"); frame.SetResourceReference(Border.CornerRadiusProperty, "PanelR8");
        if (thumbnail is not null) frame.Child = new Image { Source = thumbnail, Stretch = Stretch.UniformToFill };
        else
        {
            var icon = ClipboardPreview.Text(directory == true ? "\uE8B7" : "\uE8A5", Math.Clamp(size * .4, 16, 32), available ? "PanelSecondary" : "PanelTertiary");
            icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"); icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Center; frame.Child = icon;
        }
        if (thumbnail is null && available && loadThumbnail && IsImage(path))
        {
            CancellationTokenSource? pending = null;
            async void Load()
            {
                if (!frame.IsVisible || pending is not null) return;
                var request = pending = new CancellationTokenSource();
                try
                {
                    int pixels = Math.Clamp((int)Math.Ceiling(size * VisualTreeHelper.GetDpi(frame).PixelsPerDip), 1, 256);
                    var image = await DropShelf.HostedResources.ImageWorkAsync(() => Thumbnail(path, pixels), request.Token);
                    await frame.Dispatcher.InvokeAsync(() =>
                    {
                        if (!request.IsCancellationRequested && frame.IsVisible && image is not null)
                            frame.Child = new Image { Source = image, Stretch = Stretch.UniformToFill };
                    });
                }
                catch (OperationCanceledException) { }
            }
            void Release()
            {
                pending?.Cancel(); pending?.Dispose(); pending = null;
                if (frame.Child is Image image) image.Source = null;
            }
            frame.Loaded += (_, _) => Load(); frame.Unloaded += (_, _) => Release();
            frame.IsVisibleChanged += (_, _) => { if (frame.IsVisible) Load(); else Release(); };
        }
        frame.Opacity = available ? 1 : .45; return frame;
    }
    internal static FrameworkElement Deck(string[] paths)
    {
        var root = new Grid { Margin = new Thickness(12, 10, 12, 4), ClipToBounds = true };
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var deck = new Grid { MinHeight = 20 }; root.Children.Add(deck);
        int count = Math.Min(paths.Length, 4);
        for (int i = count - 1; i >= 0; i--)
        {
            var art = Art(paths[i], 54, directory: Directory.Exists(paths[i]));
            art.HorizontalAlignment = HorizontalAlignment.Center; art.VerticalAlignment = VerticalAlignment.Center;
            art.RenderTransformOrigin = new Point(.5, .8);
            art.RenderTransform = new TransformGroup { Children = { new RotateTransform((i - (count - 1) / 2d) * 9), new TranslateTransform((i - (count - 1) / 2d) * 12, i * -2) } };
            deck.Children.Add(art);
        }
        string title = paths.Length == 1 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0])) : Loc.T($"{paths.Length} 个文件", $"{paths.Length} files");
        var name = ClipboardPreview.Text(title, 11.5); name.TextWrapping = TextWrapping.NoWrap; name.Margin = new Thickness(0, 4, 0, 0); name.TextAlignment = TextAlignment.Center;
        Grid.SetRow(name, 1); root.Children.Add(name); return root;
    }
    private static bool ButtonWithin(DependencyObject? node, Button button)
    {
        while (node is not null) { if (ReferenceEquals(node, button)) return true; node = node is Visual ? VisualTreeHelper.GetParent(node) : null; }
        return false;
    }
    internal static FrameworkElement Row(string path, Action copy, Action drag)
    {
        var row = new Grid { Height = 58, Margin = new Thickness(0, 0, 0, 6), Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(44) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        bool directory = Directory.Exists(path), available = directory || File.Exists(path);
        row.Children.Add(Art(path, 38, available: available, directory: directory));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
        var name = ClipboardPreview.Text(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), 12); name.MaxHeight = 32; text.Children.Add(name);
        text.Children.Add(ClipboardPreview.Text(!available ? Loc.T("源文件已失效", "Original unavailable") : directory ? Loc.T("文件夹", "Folder") : Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), 10.5, "PanelTertiary"));
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var copyButton = IslandContentCard.ActionButton("⧉", Loc.T("复制此文件", "Copy this file"), copy);
        actions.Children.Add(copyButton);
        var dragHandle = IslandContentCard.ActionButton("↗", Loc.T("拖动此处以导出文件", "Drag here to export this file"), () => { });
        actions.Children.Add(dragHandle);
        Grid.SetColumn(actions, 2); row.Children.Add(actions); row.ToolTip = path;
        Point press = default; bool pending = false;
        row.PreviewMouseLeftButtonDown += (_, e) => { pending = !ButtonWithin(e.OriginalSource as DependencyObject, copyButton); press = e.GetPosition(row); };
        row.PreviewMouseLeftButtonUp += (_, _) => pending = false;
        row.PreviewMouseMove += (_, e) =>
        {
            if (!pending || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { pending = false; return; }
            var delta = e.GetPosition(row) - press;
            if (Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance) { pending = false; e.Handled = true; drag(); }
        };
        return row;
    }
}
