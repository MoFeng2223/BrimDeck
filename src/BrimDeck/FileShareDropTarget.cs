using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// One preview handler on the island owns external file drops, including those over child pages. Internal drags stay
// with their existing owners. The share callback runs after the OLE drop has returned, never from hover or drag-out.
internal sealed class FileShareDropTarget : IDisposable
{
    private readonly UIElement _surface;
    private readonly Func<bool> _enabled, _exporting;
    private readonly Action<string[]> _share;
    private readonly Action<bool> _highlight;
    private readonly Action<string> _feedback;
    private readonly object _previousAllowDrop;
    private bool _queued, _disposed;
    internal bool IsDragging { get; private set; }

    internal FileShareDropTarget(UIElement surface, Func<bool> enabled, Func<bool> exporting, Action<string[]> share,
        Action<bool> highlight, Action<string> feedback)
    {
        _surface = surface; _enabled = enabled; _exporting = exporting; _share = share; _highlight = highlight; _feedback = feedback;
        _previousAllowDrop = surface.ReadLocalValue(UIElement.AllowDropProperty);
        surface.AllowDrop = true;
        surface.PreviewDragEnter += DragOver;
        surface.PreviewDragOver += DragOver;
        surface.PreviewDragLeave += DragLeave;
        surface.PreviewDrop += Drop;
    }

    internal static MenuItem SourceMenuItem(string path, Action<string[]> share)
    {
        var item = new MenuItem { Header = Loc.T("直接分享源文件…", "Share original file…"), IsEnabled = File.Exists(path) || Directory.Exists(path) };
        AutomationProperties.SetAutomationId(item, "file-share-original");
        item.Click += (_, e) =>
        {
            // Let the native menu close before asking Windows to open a different popup.
            item.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => share([path]));
            e.Handled = true;
        };
        return item;
    }

    private bool FileDrop(DragEventArgs e)
    {
        try { return e.Data.GetDataPresent(DataFormats.FileDrop, false); }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or NotSupportedException) { return false; }
    }

    private bool Accepts(DragEventArgs e) => !_disposed && !_queued && _enabled() && e.AllowedEffects.HasFlag(DragDropEffects.Copy)
        && FileShareService.IsExternalFileDrop(e.Data, _exporting());

    private void DragOver(object sender, DragEventArgs e)
    {
        if (!FileDrop(e)) return;
        bool accepted = Accepts(e);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        Highlight(accepted);
    }

    private void DragLeave(object sender, DragEventArgs e)
    {
        if (!IsDragging) return;
        Point point = e.GetPosition(_surface);
        // Child-to-child drag-leave events also tunnel through the island; only leaving the surface ends the cue.
        if (point.X < 0 || point.Y < 0 || point.X >= _surface.RenderSize.Width || point.Y >= _surface.RenderSize.Height) Highlight(false);
    }

    private void Drop(object sender, DragEventArgs e)
    {
        if (!FileDrop(e)) return;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        bool accepted = Accepts(e);
        Highlight(false);
        if (!accepted) return;
        string[] paths;
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop, false) is not string[] dropped) return;
            paths = FileShareService.NormalizePaths(dropped);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or NotSupportedException)
        {
            _feedback(Loc.T("无法读取拖入的文件，请重试。", "The dropped files could not be read. Try again."));
            return;
        }
        if (paths.Length == 0)
        {
            _feedback(Loc.T("源文件已不存在或无法访问，未发起分享。", "A source file is missing or inaccessible. Nothing was shared."));
            return;
        }
        e.Effects = DragDropEffects.Copy;
        _queued = true;
        _surface.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try { if (!_disposed && _enabled()) _share(paths); }
            finally { _queued = false; }
        });
    }

    private void Highlight(bool shown)
    {
        if (IsDragging == shown) return;
        IsDragging = shown;
        _highlight(shown);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Highlight(false);
        _surface.PreviewDragEnter -= DragOver;
        _surface.PreviewDragOver -= DragOver;
        _surface.PreviewDragLeave -= DragLeave;
        _surface.PreviewDrop -= Drop;
        if (_previousAllowDrop == DependencyProperty.UnsetValue) _surface.ClearValue(UIElement.AllowDropProperty);
        else _surface.SetValue(UIElement.AllowDropProperty, _previousAllowDrop);
    }
}
