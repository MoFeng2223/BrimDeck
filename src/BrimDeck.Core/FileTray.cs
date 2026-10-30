using System.IO;
using System.Text.Json;

namespace BrimDeck.Core;

// What the file tray holds: a reference to a file or a folder, never a copy of it. The tray is a waiting room for something
// the user is moving, so a copy would be a second thing to clean up and would surprise anyone who then moves the original.
public enum TrayItemKind { File, Folder }

public sealed class TrayItem
{
    // Stable across renames inside the tray: the path may change, the item does not.
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public TrayItemKind Kind { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    // Pinned items are kept by the retention clean-up and by "clear".
    public bool Pinned { get; set; }

    public string Name => Path.Length == 0 ? "" : System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : Path;

    // Whether the original is still there. Asking the file system costs a lookup, and a page of cards asks for every one of
    // them several times over, so the answer FileTray read last is kept here and refreshed by FileTray.RefreshExistence.
    [System.Text.Json.Serialization.JsonIgnore] public bool? KnownExists { get; internal set; }
    public bool Exists => KnownExists ?? Check();
    internal bool Check()
    {
        bool exists = Kind == TrayItemKind.Folder ? Directory.Exists(Path) : File.Exists(Path);
        KnownExists = exists;
        return exists;
    }

    public TrayItem Copy() => new() { Id = Id, Path = Path, Kind = Kind, AddedAt = AddedAt, Pinned = Pinned, KnownExists = KnownExists };
}

// The tray's state and the rules that keep it honest: no missing paths, no duplicates, pinned items outliving both the
// retention time and a clear. Nothing here touches the screen; the page and the floating tray both read this one list.
public sealed class FileTray
{
    // A tray is a waiting room, not a library: past this many items the oldest unpinned one goes.
    public const int Capacity = 200;
    private readonly List<TrayItem> _items = [];

    // Raised after any change, on the thread that made it.
    public event Action? Changed;

    // How long an answer about the file system is kept. A page of cards asks whether every item is still there several times
    // over, and at one lookup per item per question that is hundreds of reads for one rebuild: enough to be felt. One sweep
    // every few seconds is plenty for a tray the user is watching.
    public static readonly TimeSpan ExistenceInterval = TimeSpan.FromSeconds(2);
    private DateTimeOffset _existenceAt;
    // Reads whether every item is still where it was, no sooner than the interval allows. Returns whether it read.
    public bool RefreshExistence(DateTimeOffset now)
    {
        if (_existenceAt != default && now - _existenceAt < ExistenceInterval) return false;
        _existenceAt = now;
        foreach (var item in _items) item.KnownExists = item.Check();
        return true;
    }

    public IReadOnlyList<TrayItem> Items => _items;
    public int Count => _items.Count;
    public int PinnedCount => _items.Count(item => item.Pinned);

    // Takes the paths that exist and are not in the tray yet; returns how many were added. The newest item is last.
    public int Add(IEnumerable<string> paths, DateTimeOffset now)
    {
        int added = 0;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string full;
            try { full = System.IO.Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            TrayItemKind? kind = Directory.Exists(full) ? TrayItemKind.Folder : File.Exists(full) ? TrayItemKind.File : null;
            if (kind is null) continue;
            if (_items.Any(item => string.Equals(item.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
            if (_items.Count >= Capacity && OldestUnpinned() is { } oldest) _items.Remove(oldest);
            _items.Add(new TrayItem { Id = Guid.NewGuid().ToString("N"), Path = full, Kind = kind.Value, AddedAt = now, KnownExists = true });
            added++;
        }
        if (added > 0) Changed?.Invoke();
        return added;
    }

    private TrayItem? OldestUnpinned() => _items.Where(item => !item.Pinned).OrderBy(item => item.AddedAt).FirstOrDefault();

    public bool Remove(string id)
    {
        int at = _items.FindIndex(item => item.Id == id);
        if (at < 0) return false;
        _items.RemoveAt(at);
        Changed?.Invoke();
        return true;
    }

    public int RemoveWhere(Func<TrayItem, bool> match)
    {
        int removed = _items.RemoveAll(item => match(item));
        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    public int IndexOf(string id) => _items.FindIndex(item => item.Id == id);

    // Reordering inside the tray: the item takes the place it was dragged to. The move is applied at once but is not
    // announced, so a drag writes the file once at the end and the page is not rebuilt on every step of it.
    public bool Reorder(int from, int to)
    {
        if (from < 0 || from >= _items.Count || to < 0 || to >= _items.Count || from == to) return false;
        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, item);
        return true;
    }

    public void Announce() => Changed?.Invoke();

    // Pins or unpins everything at once, for the capsule's bulk action. One announcement, not one per item.
    public void SetAllPinned(bool pinned)
    {
        bool changed = false;
        foreach (var item in _items)
            if (item.Pinned != pinned) { item.Pinned = pinned; changed = true; }
        if (changed) Changed?.Invoke();
    }

    public bool TogglePin(string id)
    {
        if (_items.FirstOrDefault(item => item.Id == id) is not { } item) return false;
        item.Pinned = !item.Pinned;
        Changed?.Invoke();
        return true;
    }

    // Clears what the user asked to clear; pinned items stay unless asked for too.
    public int Clear(bool keepPinned)
    {
        int removed = keepPinned ? RemoveWhere(item => !item.Pinned) : RemoveWhere(_ => true);
        return removed;
    }

    // Drops unpinned items older than the retention time; returns how many went. A retention of zero keeps everything.
    public int Expire(TimeSpan retention, DateTimeOffset now)
        => retention <= TimeSpan.Zero ? 0 : RemoveWhere(item => !item.Pinned && now - item.AddedAt >= retention);

    // Items whose path no longer exists are shown as missing rather than dropped: the user may have moved the file back,
    // and a tray that silently forgets things is worse than one that admits the original is gone.
    public int MissingCount => _items.Count(item => !item.Exists);

    public int PruneMissing()
    {
        int removed = RemoveWhere(item => !item.Exists);
        return removed;
    }

    internal void Replace(IEnumerable<TrayItem> items)
    {
        _items.Clear();
        foreach (var item in items)
            if (!string.IsNullOrEmpty(item.Id) && !string.IsNullOrEmpty(item.Path) && _items.Count < Capacity) _items.Add(item);
        Changed?.Invoke();
    }
}

// The tray's file, beside settings.json. Written whole on every change: a tray changes a few times a minute at most, and a
// half-written file is worse than a slow one, so the write goes through a temporary file and a replace.
public sealed class FileTrayStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public string FilePath { get; } = Path.Combine(directory, "file-tray.json");

    public FileTray Load()
    {
        var tray = new FileTray();
        try
        {
            if (!File.Exists(FilePath)) return tray;
            if (JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath), Json) is { } stored)
                tray.Replace(stored.Items ?? []);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // A file that cannot be read leaves an empty tray rather than stopping the app.
        }
        return tray;
    }

    public void Save(FileTray tray)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Stored { Items = [.. tray.Items] }, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private sealed class Stored { public List<TrayItem>? Items { get; set; } }
}
