using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;
using System.IO;
using DropShelf.Models;
using DropShelf.Services;

namespace BrimDeck;

// Internal organization carries scoped identifiers; external exports use native file/text formats without clipboard writes.
internal sealed class ClipboardDragDrop(ClipboardHistory history, Action<bool>? interaction = null, Action<string>? feedback = null)
{
    private const string Format = "BrimDeck.ClipboardLibrary.Internal";
    private sealed record Payload(string Id, bool Collection);
    private sealed class DragScope
    {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly DropShelf.OperationTracker Operations = new();
        internal bool InternalDrop, Suspended;
    }
    internal static Task SuspendAsync(ClipboardHistory history)
    {
        var scope = Scopes.GetValue(history, _ => new()); scope.Suspended = true; return scope.Operations.WhenIdle;
    }
    internal static void Resume(ClipboardHistory history) => Scopes.GetValue(history, _ => new()).Suspended = false;
    private static readonly ConditionalWeakTable<ClipboardHistory, DragScope> Scopes = new();
    private readonly DragScope _dragScope = Scopes.GetValue(history, _ => new());
    private string ScopeId => _dragScope.Id;
    internal static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached("Hint", typeof(string), typeof(ClipboardDragDrop), new PropertyMetadata(""));
    public static string GetHint(DependencyObject element) => (string)element.GetValue(HintProperty);
    public static void SetHint(DependencyObject element, string value) => element.SetValue(HintProperty, value);
    private Payload? _pressed;
    private Point _pressAt;
    private Button? _highlight;
    private long _lastScroll;
    internal bool IsDragging { get; private set; }

    internal DataObject EntryData(string id) => Data(new(id, false));
    internal DataObject CollectionData(string id) => Data(new(id, true));
    private DataObject Data(Payload payload)
    {
        // OLE may request data outside this process; an inert scoped identifier never serializes history or copied content.
        var data = new DataObject(); data.SetData(Format, ScopeId + (payload.Collection ? ":C:" : ":E:") + payload.Id, false); return data;
    }
    private Payload? Read(IDataObject data)
    {
        // A foreign DataObject is not an import route: ownership is checked before any identifier can mutate the library.
        if (!data.GetDataPresent(Format, false)) return null;
        object? encoded = data.GetData(Format, false);
        string? value = encoded as string;
        // Shell's custom HGLOBAL formats surface as streams in WPF; the bounded UTF-16 marker is still just an identifier.
        if (encoded is MemoryStream stream && stream.Length <= 4096) value = System.Text.Encoding.Unicode.GetString(stream.ToArray()).TrimEnd('\0');
        if (value is null || value.Length > 2048 || !value.StartsWith(ScopeId + ":", StringComparison.Ordinal)) return null;
        string[] fields = value.Split(':', 3);
        if (fields.Length != 3 || fields[1] is not ("C" or "E")) return null;
        var payload = new Payload(fields[2], fields[1] == "C");
        return payload.Collection ? history.Collections.Any(group => group.Id == payload.Id) ? payload : null
            : history.Items.Any(entry => entry.Id == payload.Id) ? payload : null;
    }
    internal void AttachEntries(ListBox list, Action<ClipboardEntry>? click = null)
    {
        AttachSource(list, source => Find<ButtonBase>(source) is null && Find<TextBoxBase>(source) is null
            && Find<ListBoxItem>(source)?.DataContext is ClipboardEntry entry ? new(entry.Id, false) : null,
            click is null ? null : payload => { if (history.Items.FirstOrDefault(entry => entry.Id == payload.Id) is { } entry) click(entry); });
        if (click is null) AutomationProperties.SetHelpText(list, Loc.T("拖到收藏夹整理，或拖出到其他应用；拖动不会写入系统剪贴板。", "Drag to a collection to organize, or out to another application. Dragging does not write to the system clipboard."));
    }
    internal void AttachCollection(Button button, string id, Orientation orientation)
    {
        AutomationProperties.SetAutomationId(button, "clipboard-collection-" + id);
        AttachSource(button, _ => new(id, true));
        AttachTarget(button, id, false, orientation);
        AutomationProperties.SetHelpText(button, Loc.T("右键编辑名称与标签外观或删除；拖动调整顺序；拖入条目移至此分类。", "Right-click to edit name and label appearance or delete; drag to reorder; drop entries to move them here."));
    }
    internal void AttachHistory(Button button)
    {
        AutomationProperties.SetAutomationId(button, "clipboard-drop-history"); AttachTarget(button, "", false, Orientation.Horizontal);
        AutomationProperties.SetHelpText(button, Loc.T("拖入条目以移出指定分类并恢复自动分类；记录始终保留在历史中。", "Drop an entry to remove its assigned collection and restore automatic classification. Entries always remain in history."));
    }
    internal void AttachFavorites(Button button)
    {
        AutomationProperties.SetAutomationId(button, "clipboard-drop-favorites"); AttachTarget(button, "", true, Orientation.Vertical);
        AutomationProperties.SetHelpText(button, Loc.T("拖入条目即可收藏，不改变其分类。", "Drop an entry to favorite it without changing its collection."));
    }
    private void AttachSource(UIElement source, Func<DependencyObject?, Payload?> pick, Action<Payload>? click = null)
    {
        source.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            var picked = pick(e.OriginalSource as DependencyObject);
            _pressed = e.ClickCount == 1 ? picked : null;
            _pressAt = e.GetPosition(source);
            if (click is not null && picked is not null && source is ListBox list)
            {
                // Edge-Drop's non-focusable shelf: WPF's default item press calls Focus() even behind MA_NOACTIVATE.
                // Select without that handler so the external caret survives until the release requests paste.
                list.SelectedItem = Find<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext;
                e.Handled = true;
            }
        };
        // ClipboardItem.handleCardClick: a drag, an attempted drag, or a release over another card is never a paste.
        source.PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            var pressed = _pressed; _pressed = null;
            if (pressed is null || IsDragging || pick(e.OriginalSource as DependencyObject) != pressed) return;
            var distance = e.GetPosition(source) - _pressAt;
            if (Math.Abs(distance.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(distance.Y) >= SystemParameters.MinimumVerticalDragDistance) return;
            click?.Invoke(pressed);
        };
        source.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) { _pressed = null; return; }
            if (_pressed is not { } payload || IsDragging || _dragScope.Suspended) return;
            var distance = e.GetPosition(source) - _pressAt;
            if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _pressed = null;
            var data = Data(payload); if (Read(data) is null) return;
            // Release a tab's click capture before OLE takes over, so a drag never also changes the selected filter.
            if (source.IsMouseCaptureWithin) Mouse.Capture(null);
            using var operation = _dragScope.Operations.Enter();
            IsDragging = true; interaction?.Invoke(true); e.Handled = true;
            try
            {
                if (!payload.Collection && history.Items.FirstOrDefault(item => item.Id == payload.Id) is { } entry && Window.GetWindow(source) is { } owner) ExportCore(owner, entry);
                else DragDrop.DoDragDrop(source, data, DragDropEffects.Move | DragDropEffects.Link);
            }
            catch (ExternalException) { feedback?.Invoke(Loc.T("拖动未完成，可重试或使用右键菜单整理。", "Drag did not complete. Retry or organize using the context menu.")); }
            finally { ClearHighlight(); IsDragging = false; interaction?.Invoke(false); }
        };
    }
    internal void Export(Window owner, ClipboardEntry entry)
    {
        if (IsDragging || _dragScope.Suspended) return;
        using var operation = _dragScope.Operations.Enter();
        IsDragging = true; interaction?.Invoke(true);
        try { ExportCore(owner, entry); }
        finally { ClearHighlight(); IsDragging = false; interaction?.Invoke(false); }
    }
    private void ExportCore(Window owner, ClipboardEntry entry)
    {
        string? cache = null;
        try
        {
            var content = entry.Content;
            string[] paths = content.Files;
            if (content.Kind == ClipboardContentKind.Files && paths.Any(path => !File.Exists(path) && !Directory.Exists(path)))
            { feedback?.Invoke(Loc.T("原文件已移动或删除，未导出任何文件。", "An original file was moved or deleted; no files were exported.")); return; }
            // A chat image can carry captured pixels and its original local path. Reuse that path while available;
            // only pixel-only images (including a vanished source) need an owned export attachment.
            if (content.Kind is ClipboardContentKind.Image or ClipboardContentKind.Screenshot && (paths.Length == 0 || paths.Any(path => !File.Exists(path))))
            {
                cache = VirtualFileDropService.CreateCacheDirectory();
                string file = Path.Combine(cache, "Clipboard.png");
                File.WriteAllBytes(file, content.Image); paths = [file];
            }
            var items = paths.Length > 0 ? paths.Select(path => new ShelfItem { Id = entry.Id + ":" + path, Kind = Directory.Exists(path) ? ShelfItemKind.Folder : ShelfItemKind.File, Value = path, IsTemporaryAttachment = cache is not null }).ToArray()
                : [new ShelfItem { Id = entry.Id, Kind = content.Kind == ClipboardContentKind.Link ? ShelfItemKind.Link : ShelfItemKind.Text, Value = content.Text }];
            // Copy-only export cannot move/delete source files. Its scoped marker keeps collection organization in-process.
            _dragScope.InternalDrop = false;
            var result = ShelfDragService.DoDragDrop(owner, items, "clipboard-" + ScopeId, out _, TransferMode.Copy,
                sourceFormats: new Dictionary<string, string> { [Format] = ScopeId + ":E:" + entry.Id });
            // Edge-Drop item:drag accounting: cancellation and internal collection organization are not reuses.
            if (result != DragOutResult.Cancelled && !_dragScope.InternalDrop) history.RecordUse(entry.Id, DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExternalException or InvalidOperationException)
        { feedback?.Invoke(Loc.T("无法拖出此记录，请检查源文件或使用复制。", "Cannot drag this entry. Check the original files or use Copy.")); }
        finally { if (cache is not null) _ = ReleaseExportCache(cache); }
    }
    private static async Task ReleaseExportCache(string cache)
    {
        // Shell targets may finish reading after OLE returns, as in the original shelf's temporary-file grace period.
        await Task.Delay(TimeSpan.FromMinutes(2)); await VirtualFileDropService.ReleaseCacheAsync(cache);
    }
    private void AttachTarget(Button button, string id, bool favorite, Orientation orientation)
    {
        button.AllowDrop = true;
        DragDropEffects Effect(DragEventArgs e, out Payload? payload)
        {
            payload = Read(e.Data);
            if (_dragScope.Suspended || payload is null || (id.Length > 0 && !history.Collections.Any(group => group.Id == id))) return DragDropEffects.None;
            if (payload.Collection) return !favorite && id.Length > 0 && id != payload.Id && e.AllowedEffects.HasFlag(DragDropEffects.Move) ? DragDropEffects.Move : DragDropEffects.None;
            string entryId = payload.Id;
            var entry = history.Items.First(item => item.Id == entryId);
            var effect = favorite ? !entry.Favorite ? DragDropEffects.Link : DragDropEffects.None
                : entry.CollectionId != id ? DragDropEffects.Move : DragDropEffects.None;
            // Native exports advertise Copy; an internal collection target still owns the logical move of its identifier.
            return effect != DragDropEffects.None && e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
                : e.AllowedEffects.HasFlag(effect) ? effect : DragDropEffects.None;
        }
        bool After(DragEventArgs e)
        {
            Point at = e.GetPosition(button);
            return orientation == Orientation.Horizontal ? at.X >= button.ActualWidth / 2 : at.Y >= button.ActualHeight / 2;
        }
        void Over(object sender, DragEventArgs e)
        {
            e.Effects = Effect(e, out var payload); e.Handled = true; ClearHighlight();
            if (e.Effects == DragDropEffects.None) return;
            _highlight = button; SetHint(button, payload!.Collection ? After(e) ? "After" : "Before" : "Into");
        }
        button.DragEnter += Over; button.DragOver += Over;
        button.DragLeave += (_, _) => ClearHighlight();
        button.Drop += (_, e) =>
        {
            e.Effects = Effect(e, out var payload); e.Handled = true; ClearHighlight();
            if (payload is not null) _dragScope.InternalDrop = true;
            if (e.Effects == DragDropEffects.None || payload is null) return;
            string name = history.Collections.FirstOrDefault(group => group.Id == id)?.Name ?? "";
            bool changed = payload.Collection ? history.ReorderCollection(payload.Id, id, After(e))
                : favorite ? history.ToggleFavorite(payload.Id) : history.Move(payload.Id, id);
            if (!changed) { e.Effects = DragDropEffects.None; return; }
            feedback?.Invoke(payload.Collection ? Loc.T("分类顺序已保存", "Collection order saved") : favorite ? Loc.T("已收藏，分类保持不变", "Favorited; collection unchanged")
                : id.Length == 0 ? Loc.T("已恢复自动分类，记录仍保留在历史中", "Automatic classification restored; entry kept in history")
                : Loc.T($"已移至「{name}」，记录仍保留在历史中", $"Moved to {name}; entry kept in history"));
        };
    }
    internal void AttachScroller(ScrollViewer scroll, Orientation orientation)
    {
        scroll.AllowDrop = true;
        scroll.AddHandler(UIElement.DragOverEvent, new DragEventHandler((_, e) =>
        {
            if (Read(e.Data) is null || Environment.TickCount64 - _lastScroll < 80) return;
            Point at = e.GetPosition(scroll);
            if (orientation == Orientation.Horizontal)
            {
                if (at.X < 22) scroll.LineLeft(); else if (at.X > scroll.ActualWidth - 22) scroll.LineRight(); else return;
            }
            else
            {
                if (at.Y < 22) scroll.LineUp(); else if (at.Y > scroll.ActualHeight - 22) scroll.LineDown(); else return;
            }
            _lastScroll = Environment.TickCount64;
        }), true);
        scroll.DragLeave += (_, _) => ClearHighlight();
    }
    private void ClearHighlight()
    {
        if (_highlight is not null) { SetHint(_highlight, ""); _highlight = null; }
    }
    private static T? Find<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null) { if (node is T found) return found; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node); }
        return null;
    }
}
