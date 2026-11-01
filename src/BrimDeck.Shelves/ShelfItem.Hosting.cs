namespace DropShelf.Models;

// BrimDeck 的固定和保留时间是条目元数据；内容与附件所有权仍只在原版 ShelfItem / ShelfViewModel 中。
public sealed partial class ShelfItem
{
    private bool _pinned;
    public bool Pinned
    {
        get => _pinned;
        set { if (_pinned == value) return; _pinned = value; OnPropertyChanged(); }
    }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public string Path => Value;
    public string Name => DisplayName;
    public bool Exists => !IsFileSystem || IsAvailable;
}
