using System.Windows;
using DropShelf.Localization;
using DropShelf.Services;
using DropShelf.Views;

namespace DropShelf;

/// <summary>岛内只是原版拖入链路的入口：在 OLE 回调中取出数据，再由同一置物架接管附件缓存。</summary>
public sealed class ShelfIslandDropTarget : IDisposable
{
    public const string LegacyOriginFormat = "BrimDeck.FileTray.Origin";
    private readonly UIElement _surface;
    private readonly Window _owner;
    private readonly Func<ShelfWindow> _target;
    private readonly Func<bool> _enabled, _exporting;
    private readonly OperationTracker? _operations;
    public event Action? ActivityChanged;
    private readonly Action<bool> _highlight;
    private readonly Action<string> _feedback;
    private readonly object _oldAllowDrop;
    private readonly DropTargetHelper _helper = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy, _disposed;
    public bool IsDragging { get; private set; }
    public bool IsReceiving => _busy;

    public ShelfIslandDropTarget(Window owner, UIElement surface, Func<ShelfWindow> target, Func<bool> enabled,
        Func<bool> exporting, Action<bool> highlight, Action<string> feedback, OperationTracker? operations = null)
    {
        _operations = operations;
        _owner = owner; _surface = surface; _target = target; _enabled = enabled; _exporting = exporting;
        _highlight = highlight; _feedback = feedback; _oldAllowDrop = surface.ReadLocalValue(UIElement.AllowDropProperty);
        surface.AllowDrop = true;
        surface.PreviewDragEnter += Over; surface.PreviewDragOver += Over;
        surface.PreviewDragLeave += Leave; surface.PreviewDrop += Drop;
    }

    private static bool Owns(IDataObject data)
    {
        try
        {
            // 剪贴板库的条目和收藏夹拖动仍交给原页面，不把其文本外形当成外部拖入。
            return !data.GetDataPresent("BrimDeck.ClipboardLibrary.Internal", false)
                && DropDataReader.Classify(data) != DropContentKind.None;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException or NotSupportedException) { return false; }
    }
    private bool Accepts(DragEventArgs e, ShelfWindow shelf)
    {
        if (_disposed || _busy || !_enabled() || _exporting() || shelf.IsClosing || shelf.ViewModel.IsBusy
            || !e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return false;
        try { return !e.Data.GetDataPresent(LegacyOriginFormat, false) && DropDataReader.GetSourceShelfId(e.Data) != shelf.ViewModel.Id; }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException or NotSupportedException) { return false; }
    }
    private void Over(object sender, DragEventArgs e)
    {
        if (!Owns(e.Data) || !_enabled()) return;
        bool accepted = Accepts(e, _target());
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
        if (accepted && !IsDragging) _helper.Enter(_owner, e.Data, e.Effects);
        Highlight(accepted); _helper.Over(e.Effects);
        DropTargetHelper.SetDescription(e.Data, accepted ? DropImageType.Label : DropImageType.Warning,
            accepted ? Strings.PutOnShelf : Strings.PleaseWait);
    }
    private void Leave(object sender, DragEventArgs e)
    {
        if (!IsDragging) return;
        var point = e.GetPosition(_surface);
        if (point.X >= 0 && point.Y >= 0 && point.X < _surface.RenderSize.Width && point.Y < _surface.RenderSize.Height) return;
        Highlight(false); _helper.Leave();
    }
    private async void Drop(object sender, DragEventArgs e)
    {
        if (!Owns(e.Data) || !_enabled()) return;
        e.Handled = true; e.Effects = DragDropEffects.None;
        using var operation = _operations?.Enter();
        var shelf = _target();
        bool accepted = Accepts(e, shelf);
        _busy = accepted;
        Highlight(false);
        if (!accepted) { _helper.Drop(e.Data, e.Effects); return; }
        e.Effects = DragDropEffects.Copy; _helper.Drop(e.Data, e.Effects);
        try
        {
            // 不得延后读取 IDataObject：虚拟附件流和来源程序临时图片可能在 Drop 返回后立刻失效。
            var read = await DropDataReader.ReadAsync(e.Data, cancellationToken: _lifetime.Token);
            if (_disposed || shelf.IsClosing)
            {
                foreach (var cache in read.CacheDirectories) await VirtualFileDropService.ReleaseCacheAsync(cache);
                return;
            }
            var summary = shelf.ViewModel.Add(read);
            if (summary.Added == 0 || summary.Duplicates > 0 || summary.Unavailable > 0 || read.FailedCount > 0)
                _feedback(read.ErrorMessage ?? summary.Message);
        }
        catch (Exception exception)
        {
            if (!_disposed) { LogService.Error("从灵动岛接收内容失败", exception); _feedback(Strings.DropFailed); }
        }
        finally
        {
            _busy = false;
            if (!_disposed) _highlight(false);
            ActivityChanged?.Invoke();
        }
    }
    private void Highlight(bool value)
    {
        if (IsDragging == value) return;
        IsDragging = value; _highlight(value);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); Highlight(false); _helper.Leave();
        _surface.PreviewDragEnter -= Over; _surface.PreviewDragOver -= Over;
        _surface.PreviewDragLeave -= Leave; _surface.PreviewDrop -= Drop;
        if (_oldAllowDrop == DependencyProperty.UnsetValue) _surface.ClearValue(UIElement.AllowDropProperty);
        else _surface.SetValue(UIElement.AllowDropProperty, _oldAllowDrop);
    }
}
