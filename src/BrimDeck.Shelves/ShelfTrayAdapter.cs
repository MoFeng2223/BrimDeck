using System.ComponentModel;
using System.Collections.Specialized;
using DropShelf.Models;
using DropShelf.Shelves;
using DropShelf.ViewModels;

namespace DropShelf;

/// <summary>岛内沿用 BrimDeck 的卡片布局，所有增删与排序直接作用于原版当前置物架，不维护第二份内容。</summary>
public sealed class ShelfTrayAdapter : IDisposable
{
    private readonly ShelfManager _manager;
    private ShelfViewModel _model;
    private DateTimeOffset _existenceAt;
    private bool _reordering;
    private readonly HashSet<ShelfItem> _observed = [];
    public ShelfTrayAdapter(ShelfManager manager)
    {
        _manager = manager; _model = manager.ActiveWindow.ViewModel;
        AttachModel();
        manager.ActiveChanged += ActiveChanged;
    }

    public event Action? Changed;
    /// <summary>内容、顺序及固定元数据改变；缩略图和可用性刷新不触发磁盘保存。</summary>
    public event Action? PersistedChanged;

    public ShelfViewModel Model => _model;
    public IReadOnlyList<ShelfItem> Items => _model.Items;
    public int Count => _model.Count;
    public int PinnedCount => Items.Count(item => item.Pinned);
    public int MissingCount => Items.Count(item => !item.Exists);
    public bool RefreshExistence(DateTimeOffset now)
    {
        if (_existenceAt != default && now - _existenceAt < TimeSpan.FromSeconds(2)) return false;
        _existenceAt = now; _model.RefreshAvailability(); return true;
    }
    private void ChangedModel(object? sender, PropertyChangedEventArgs e)
    {
        if (_reordering) return;
        if (e.PropertyName == nameof(ShelfViewModel.Count)) Announce();
        // WindowTitle follows Count; host language changes already invalidate the island.
        else if (e.PropertyName == nameof(ShelfViewModel.IsBusy)) Changed?.Invoke();
    }
    private void ActiveChanged(object? sender, EventArgs e)
    {
        // 关闭最后一个置物架时，取得替代窗口会再次通知；同一模型只订阅一次。
        var model = _manager.ActiveWindow.ViewModel;
        if (ReferenceEquals(_model, model)) return;
        DetachModel();
        _model = model;
        AttachModel();
        _existenceAt = default; Announce();
    }
    public bool Remove(string id)
    {
        var item = Items.FirstOrDefault(item => item.Id == id);
        if (item is null) return false;
        _model.Remove([item]); return true;
    }
    public int Clear(bool keepPinned)
    {
        var removing = Items.Where(item => !keepPinned || !item.Pinned).ToArray();
        _model.Remove(removing); return removing.Length;
    }
    public void SetAllPinned(bool pinned)
    {
        _reordering = true;
        try { foreach (var item in Items) item.Pinned = pinned; }
        finally { _reordering = false; }
        Announce();
    }
    public bool TogglePin(string id)
    {
        var item = Items.FirstOrDefault(item => item.Id == id);
        if (item is null) return false;
        item.Pinned = !item.Pinned; return true;
    }
    public int IndexOf(string id) => _model.Items.ToList().FindIndex(item => item.Id == id);
    public bool Reorder(int from, int to)
    {
        if (from < 0 || to < 0 || from >= Count || to >= Count || from == to) return false;
        _reordering = true;
        try { _model.Items.Move(from, to); } finally { _reordering = false; }
        return true;
    }
    private void AttachModel()
    {
        _model.PropertyChanged += ChangedModel; _model.Items.CollectionChanged += ItemsChanged; ObserveItems();
    }
    private void DetachModel()
    {
        _model.PropertyChanged -= ChangedModel; _model.Items.CollectionChanged -= ItemsChanged;
        foreach (var item in _observed) item.PropertyChanged -= ItemChanged;
        _observed.Clear();
    }
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ObserveItems();
    private void ObserveItems()
    {
        var current = _model.Items.ToHashSet();
        foreach (var removed in _observed.Where(item => !current.Contains(item)).ToArray()) { removed.PropertyChanged -= ItemChanged; _observed.Remove(removed); }
        foreach (var added in current.Where(item => !_observed.Contains(item))) { added.PropertyChanged += ItemChanged; _observed.Add(added); }
    }
    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Count belongs to collection changes; visible metadata and asynchronous thumbnails have their own notifications.
        if (_reordering) return;
        if (e.PropertyName == nameof(ShelfItem.Pinned)) Announce();
        else if (e.PropertyName is nameof(ShelfItem.IsAvailable) or nameof(ShelfItem.Thumbnail) or nameof(ShelfItem.SizeBytes) or nameof(ShelfItem.TypeName)) Changed?.Invoke();
    }
    public void Announce() { PersistedChanged?.Invoke(); Changed?.Invoke(); }
    public void Dispose()
    {
        _manager.ActiveChanged -= ActiveChanged;
        DetachModel();
    }
}
