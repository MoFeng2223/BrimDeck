using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace BrimDeck.Core;

// Stored as numbers: keep the original five values when adding formats.
public enum ClipboardContentKind { Text, Link, Color, Image, Files, RichText, Code, Svg, Screenshot }

public enum ClipboardHeaderMode { Edge, Always }

public sealed class ClipboardSettings
{
    public ModuleMode Mode { get; set; } = ModuleMode.Full;
    // Recording is opt-in: an update must not start retaining a user's passwords and private text by itself.
    public bool Enabled { get; set; }
    public bool PersistHistory { get; set; } = true;
    // Empty retains the host's existing data directory, including portable installations.
    public string StorageDirectory { get; set; } = "";
    // Zero removes the entry-count limit; payload and retention safeguards still apply.
    public int MaximumItems { get; set; } = 200;
    [JsonIgnore] public int ItemLimit => MaximumItems == 0 ? int.MaxValue : Math.Clamp(MaximumItems, 1, ClipboardHistory.HardLimit);
    public int RetentionDays { get; set; } = 30;
    public bool CaptureImages { get; set; } = true;
    public bool CaptureFiles { get; set; } = true;
    public bool PasteOnActivate { get; set; } = true;
    public bool CloseAfterAction { get; set; } = true;
    public bool HideOnDeactivate { get; set; } = true;
    public bool GridView { get; set; }
    public ClipboardHeaderMode HeaderMode { get; set; }
    public ClipboardSort SortOrder { get; set; }
    // Navigation preferences never change captured provenance; an empty source key represents unknown provenance.
    public string[] PinnedApplications { get; set; } = [];
    public string[] HiddenApplications { get; set; } = [];
    // Higher sensitivity shortens only the first hover dwell; an already-open preview always follows the latest card.
    public int PreviewSensitivity { get; set; } = 70;
    [JsonIgnore] public int PreviewDelayMilliseconds => (100 - Math.Clamp(PreviewSensitivity, 0, 100)) * 10;
    public string Hotkey { get; set; } = "Ctrl+Alt+V";
    // Process names, not window titles or copied content. Password-manager opt-out markers are always respected as well.
    public string ExcludedApplications { get; set; } = "KeePass,KeePassXC,1Password,Bitwarden";

    public ClipboardSettings Copy()
    {
        var copy = (ClipboardSettings)MemberwiseClone();
        copy.PinnedApplications = [.. PinnedApplications ?? []]; copy.HiddenApplications = [.. HiddenApplications ?? []]; return copy;
    }
    public bool IsApplicationPinned(string source) => PinnedApplications.Contains(source, StringComparer.OrdinalIgnoreCase);
    public bool SetApplicationPinned(string source, bool pinned)
    {
        if (IsApplicationPinned(source) == pinned) return false;
        PinnedApplications = pinned ? [source, .. PinnedApplications] : [.. PinnedApplications.Where(name => !string.Equals(name, source, StringComparison.OrdinalIgnoreCase))];
        return true;
    }
    public bool IsApplicationHidden(string source) => HiddenApplications.Contains(source, StringComparer.OrdinalIgnoreCase);
    public bool SetApplicationHidden(string source, bool hidden)
    {
        source = string.IsNullOrWhiteSpace(source) ? "" : source;
        if (IsApplicationHidden(source) == hidden) return false;
        HiddenApplications = hidden ? [.. HiddenApplications, source] : [.. HiddenApplications.Where(name => !string.Equals(name, source, StringComparison.OrdinalIgnoreCase))];
        return true;
    }
    public void Normalize()
    {
        if (!Enum.IsDefined(Mode)) Mode = ModuleMode.Full;
        if (MaximumItems != 0) MaximumItems = Math.Clamp(MaximumItems, 1, ClipboardHistory.HardLimit);
        StorageDirectory = (StorageDirectory ?? "").Trim();
        RetentionDays = Math.Clamp(RetentionDays, 0, 365);
        PreviewSensitivity = Math.Clamp(PreviewSensitivity, 0, 100);
        Hotkey = HotkeyText.Normalize(Hotkey);
        if (!Enum.IsDefined(SortOrder)) SortOrder = ClipboardSort.Time;
        if (!Enum.IsDefined(HeaderMode)) HeaderMode = ClipboardHeaderMode.Edge;
        PinnedApplications = [.. (PinnedApplications ?? []).Where(name => name is not null).Select(name => string.IsNullOrWhiteSpace(name) ? "" : name).Distinct(StringComparer.OrdinalIgnoreCase)];
        HiddenApplications = [.. (HiddenApplications ?? []).Where(name => name is not null).Select(name => string.IsNullOrWhiteSpace(name) ? "" : name).Distinct(StringComparer.OrdinalIgnoreCase)];
        ExcludedApplications = (ExcludedApplications ?? "")[..Math.Min((ExcludedApplications ?? "").Length, 4096)];
    }
    public bool Excludes(string processName) => ExcludedApplications.Split([',', ';', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Any(name => string.Equals(System.IO.Path.GetFileNameWithoutExtension(name), System.IO.Path.GetFileNameWithoutExtension(processName), StringComparison.OrdinalIgnoreCase));
}

// Only local payloads are kept. Links are not fetched, and file entries reference the originals rather than copying them.
public sealed record ClipboardContent
{
    public ClipboardContentKind Kind { get; init; }
    public string Text { get; init; } = "";
    public string Html { get; init; } = "";
    public string Rtf { get; init; } = "";
    public string Language { get; init; } = "";
    private string[] _files = [];
    private byte[] _image = [];
    // Do not expose owned mutable buffers: callers cannot invalidate the owner's fingerprint index.
    public string[] Files { get => _files is null ? null! : [.. _files]; init => _files = value is null ? null! : [.. value]; }
    public byte[] Image { get => _image is null ? null! : [.. _image]; init => _image = value is null ? null! : [.. value]; }
    [JsonIgnore] public int ImageLength => _image?.Length ?? 0;
    public Stream OpenImage() => new MemoryStream(_image, 0, _image.Length, writable: false, publiclyVisible: false);
    public bool ImageEquals(ClipboardContent other) => _image.AsSpan().SequenceEqual(other._image);
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }

    [JsonIgnore] public long ByteCount => Encoding.UTF8.GetByteCount(Text ?? "") + Encoding.UTF8.GetByteCount(Html ?? "") + Encoding.UTF8.GetByteCount(Rtf ?? "") + (_image?.LongLength ?? 0) + (_files ?? []).Sum(path => (long)Encoding.UTF8.GetByteCount(path ?? ""));
    [JsonIgnore] public string Preview => Kind switch
    {
        ClipboardContentKind.Image or ClipboardContentKind.Screenshot => $"{PixelWidth} × {PixelHeight}",
        ClipboardContentKind.Files => string.Join("\n", (Files ?? []).Select(System.IO.Path.GetFileName)),
        _ => (Text ?? "")[..Math.Min((Text ?? "").Length, 2000)]
    };

    public static ClipboardContent FromText(string text) => new()
    {
        Text = text,
        Kind = ClipboardContentTypes.Detect(text), Language = ClipboardContentTypes.Language(text)
    };

    public static string? ColorHex(string text)
    {
        var value = text.Trim();
        if (value.Length is not (4 or 7 or 9) || value[0] != '#' || !value.AsSpan(1).ContainsOnlyHex()) return null;
        return value.Length == 4 ? "#" + string.Concat(value.Skip(1).Select(c => new string(c, 2))).ToUpperInvariant() : value.ToUpperInvariant();
    }

    internal bool Valid => Enum.IsDefined(Kind) && Text is not null && Html is not null && Rtf is not null && Language is not null && _files is not null && _image is not null
        && Html.Length <= 1_000_000 && Rtf.Length <= 1_000_000 && Language.Length <= 64
        && Text.Length <= 1_000_000 && _files.Length <= 200 && _files.All(path => !string.IsNullOrWhiteSpace(path) && path.Length <= 32768)
        && ByteCount is > 0 and <= ClipboardHistory.MaxItemBytes
        && (Kind is not (ClipboardContentKind.Image or ClipboardContentKind.Screenshot) || (_image.Length > 0 && PixelWidth is > 0 and <= 8192 && PixelHeight is > 0 and <= 8192 && (long)PixelWidth * PixelHeight <= 16_777_216))
        && (Kind != ClipboardContentKind.Files || _files.Length > 0)
        && (Kind is ClipboardContentKind.Image or ClipboardContentKind.Screenshot or ClipboardContentKind.Files || !string.IsNullOrWhiteSpace(Text))
        && (Kind != ClipboardContentKind.Color || ClipboardColors.Swatches(Text).Count > 0)
        && (Kind != ClipboardContentKind.Svg || ClipboardContentTypes.IsSvg(Text))
        && (Kind != ClipboardContentKind.Link || Uri.TryCreate(Text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");

    internal string Fingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([(byte)Kind]);
        if (Kind is ClipboardContentKind.Image or ClipboardContentKind.Screenshot) hash.AppendData(_image);
        else if (Kind == ClipboardContentKind.Files)
            foreach (var path in _files) hash.AppendData(Encoding.UTF8.GetBytes(path.ToUpperInvariant() + "\0"));
        else hash.AppendData(Encoding.UTF8.GetBytes(Kind == ClipboardContentKind.Color ? ColorHex(Text) ?? Text : Text));
        if (Html.Length > 0) hash.AppendData(Encoding.UTF8.GetBytes("\0html\0" + Html));
        if (Rtf.Length > 0) hash.AppendData(Encoding.UTF8.GetBytes("\0rtf\0" + Rtf));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

internal static class ClipboardHex
{
    internal static bool ContainsOnlyHex(this ReadOnlySpan<char> value)
    {
        foreach (char c in value) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}

public sealed record ClipboardEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ClipboardContent Content { get; init; } = new();
    public string SourceName { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset CopiedAt { get; init; }
    public string Title { get; init; } = "";
    public string Notes { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public bool Archived { get; init; }
    public DateTimeOffset? EditedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public int UseCount { get; init; }
    // Edge-Drop ItemStore.hitCount: first capture is one; recapture and successful reuse each add one.
    public int CopyCount { get; init; } = 1;
    public bool Favorite { get; init; }
    public string CollectionId { get; init; } = "";
    [JsonIgnore] public string Fingerprint => Content.Fingerprint();
}

public sealed record ClipboardCollection(string Id, string Name)
{
    // Empty values retain the native, theme-driven label with no icon, including snapshots saved before appearance support.
    public string BackgroundColor { get; init; } = "";
    public string Icon { get; init; } = "";
    public static string NormalizeColor(string? value)
    {
        value = value?.Trim();
        return value is { Length: 4 or 7 } && ClipboardContent.ColorHex(value) is { } color ? color : "";
    }
    public static bool ValidIcon(string? value) => value is "" or "folder" or "star" or "heart" or "bookmark" or "sparkles" or "code"
        or "image" or "link" or "palette" or "briefcase" or "note" or "bolt";
}

public sealed record ClipboardSnapshot
{
    public int Version { get; init; } = 4;
    public List<ClipboardCollection> Collections { get; init; } = ClipboardHistory.DefaultCollections();
    public List<ClipboardEntry> Items { get; init; } = [];
}

// This is the only owner of ordering, deduplication and retention. Views and the native listener never keep parallel lists.
public sealed partial class ClipboardHistory
{
    public const int HardLimit = 1000, MaxItemBytes = 8 * 1024 * 1024, MaxTotalBytes = 32 * 1024 * 1024;
    private readonly List<ClipboardEntry> _items = [];
    private readonly Dictionary<string, string> _fingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ClipboardContent Content, string Fingerprint, long Bytes)> _metrics = new(StringComparer.Ordinal);
    private readonly List<ClipboardCollection> _collections = DefaultCollections();
    private (string Fingerprint, long Bytes) Measure(ClipboardEntry item)
    {
        if (!_metrics.TryGetValue(item.Id, out var found) || !ReferenceEquals(found.Content, item.Content))
            _metrics[item.Id] = found = (item.Content, item.Content.Fingerprint(), item.Content.ByteCount);
        return (found.Fingerprint, found.Bytes);
    }
    private void IndexChanged()
    {
        var ids = _items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _metrics.Keys.Where(id => !ids.Contains(id)).ToArray()) _metrics.Remove(id);
        _fingerprints.Clear();
        foreach (var item in _items) _fingerprints[Measure(item).Fingerprint] = item.Id;
        Changed?.Invoke();
    }
    public DateTimeOffset? NextExpiration(ClipboardSettings settings) => settings.RetentionDays <= 0 ? null
        : _items.Where(item => !item.Favorite && !item.Archived).Select(item => (DateTimeOffset?)item.CopiedAt.AddDays(settings.RetentionDays)).Min();
    public IReadOnlyList<ClipboardEntry> Items => _items;
    public IReadOnlyList<ClipboardCollection> Collections => _collections;
    public event Action? Changed;
    // Explicit privacy deletion is a persistence barrier, unlike ordinary burst-coalesced captures.
    public event Action? CommitRequested;
    public static List<ClipboardCollection> DefaultCollections() => [new("prompts", "Prompts"), new("colors", "Colors"), new("assets", "Assets"), new("inspirations", "Inspirations")];

    public ClipboardEntry? Add(ClipboardContent content, string sourceName, string sourcePath, DateTimeOffset now, ClipboardSettings settings)
    {
        if (!content.Valid || (content.Kind is ClipboardContentKind.Image or ClipboardContentKind.Screenshot && !settings.CaptureImages) || (content.Kind == ClipboardContentKind.Files && !settings.CaptureFiles)) return null;
        string fingerprint = content.Fingerprint();
        int duplicate = _fingerprints.TryGetValue(fingerprint, out var duplicateId) ? _items.FindIndex(item => item.Id == duplicateId) : -1;
        ClipboardEntry entry;
        if (duplicate >= 0)
        {
            var previous = _items[duplicate]; _items.RemoveAt(duplicate);
            entry = previous with { CopiedAt = now, SourceName = sourceName, SourcePath = sourcePath, CopyCount = Math.Min(1_000_000, previous.CopyCount + 1) };
        }
        else
        {
            if (_items.Count(item => item.Favorite || item.Archived) >= settings.ItemLimit
                || _items.Where(item => item.Favorite || item.Archived).Sum(item => Measure(item).Bytes) + content.ByteCount > MaxTotalBytes) return null;
            entry = new ClipboardEntry { Content = content, SourceName = sourceName, SourcePath = sourcePath, CreatedAt = now, CopiedAt = now };
            _metrics[entry.Id] = (content, fingerprint, content.ByteCount);
        }
        _items.Insert(0, entry);
        CleanInternal(settings, now);
        IndexChanged();
        return _items.FirstOrDefault(item => item.Id == entry.Id);
    }

    public IEnumerable<ClipboardEntry> Query(string query = "", string collectionId = "", bool favoritesOnly = false)
        => Query(new ClipboardQuery { Text = query, CollectionId = collectionId, FavoritesOnly = favoritesOnly });

    private bool InCollection(ClipboardEntry item, string id) => id.Length == 0 || _collections.Any(group => group.Id == id) && (item.CollectionId == id
        || item.CollectionId.Length == 0 && ((id == "colors" && item.Content.Kind == ClipboardContentKind.Color)
        || (id == "assets" && item.Content.Kind is ClipboardContentKind.Image or ClipboardContentKind.Screenshot or ClipboardContentKind.Files or ClipboardContentKind.Svg)));

    public bool ToggleFavorite(string id) => Edit(id, item => item with { Favorite = !item.Favorite });
    public bool Move(string id, string collectionId) => (collectionId.Length == 0 || _collections.Any(group => group.Id == collectionId))
        && Edit(id, item => item with { CollectionId = collectionId });
    public bool Remove(string id)
    {
        if (_items.RemoveAll(item => item.Id == id) == 0) return false;
        IndexChanged(); return true;
    }
    private static bool FromApplication(ClipboardEntry item, string source) => string.IsNullOrWhiteSpace(source)
        ? string.IsNullOrWhiteSpace(item.SourceName) : string.Equals(item.SourceName, source, StringComparison.OrdinalIgnoreCase);
    public int ApplicationCount(string source) => _items.Count(item => FromApplication(item, source));
    // An empty key means unknown provenance, never all history. Explicit app clearing includes favorites and archives.
    public int ClearApplication(string source)
    {
        int removed = _items.RemoveAll(item => FromApplication(item, source));
        if (removed > 0) { IndexChanged(); CommitRequested?.Invoke(); }
        return removed;
    }
    public int Clear(bool keepFavorites = true)
    {
        int removed = _items.RemoveAll(item => !keepFavorites || !item.Favorite && !item.Archived);
        if (removed > 0) { IndexChanged(); CommitRequested?.Invoke(); }
        return removed;
    }
    public int Clean(ClipboardSettings settings, DateTimeOffset now)
    {
        int removed = CleanInternal(settings, now);
        if (removed > 0) IndexChanged();
        return removed;
    }
    private int CleanInternal(ClipboardSettings settings, DateTimeOffset now)
    {
        int removed = settings.RetentionDays <= 0 ? 0 : _items.RemoveAll(item => !item.Favorite && !item.Archived && now - item.CopiedAt >= TimeSpan.FromDays(Math.Clamp(settings.RetentionDays, 1, 365)));
        long bytes = _items.Sum(item => Measure(item).Bytes);
        int limit = settings.ItemLimit;
        while (_items.Count > limit || bytes > MaxTotalBytes)
        {
            int at = _items.FindLastIndex(item => !item.Favorite && !item.Archived);
            if (at < 0) break;
            bytes -= Measure(_items[at]).Bytes; _items.RemoveAt(at); removed++;
        }
        return removed;
    }
    private bool Edit(string id, Func<ClipboardEntry, ClipboardEntry> edit)
    {
        int at = _items.FindIndex(item => item.Id == id);
        if (at < 0) return false;
        var next = edit(_items[at]);
        if (next == _items[at]) return false;
        _items[at] = next; IndexChanged(); return true;
    }
    public ClipboardCollection? AddCollection(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 32 || _collections.Count >= 20 || _collections.Any(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase))) return null;
        var group = new ClipboardCollection(Guid.NewGuid().ToString("N"), name);
        _collections.Add(group); IndexChanged(); return group;
    }
    public bool RenameCollection(string id, string name)
        => _collections.FirstOrDefault(group => group.Id == id) is { } group && UpdateCollection(id, name, group.BackgroundColor, group.Icon, out _);
    public bool SetCollectionAppearance(string id, string backgroundColor, string icon)
        => _collections.FirstOrDefault(group => group.Id == id) is { } group && UpdateCollection(id, group.Name, backgroundColor, icon, out _);
    // Name and appearance are one local draft: validate everything before publishing a single history change.
    public bool UpdateCollection(string id, string name, string backgroundColor, string icon, out string problem)
    {
        problem = ""; int at = _collections.FindIndex(group => group.Id == id); name = name.Trim();
        if (at < 0) { problem = Loc.T("此收藏夹已被删除，请取消。", "This collection was removed. Cancel this editor."); return false; }
        if (name.Length is 0 or > 32 || _collections.Any(group => group.Id != id && string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)))
        { problem = Loc.T("名称不能为空、重复或超过 32 字。", "Use a unique name of up to 32 characters."); return false; }
        string color = ClipboardCollection.NormalizeColor(backgroundColor);
        if (backgroundColor is null || backgroundColor.Trim().Length > 0 && color.Length == 0 || !ClipboardCollection.ValidIcon(icon))
        { problem = Loc.T("背景色须为 #RGB 或 #RRGGBB；留空使用默认样式。", "Use #RGB or #RRGGBB, or leave empty for the default style."); return false; }
        var group = _collections[at];
        var next = group with { Name = name, BackgroundColor = color, Icon = icon };
        if (next == group) return true;
        _collections[at] = next; IndexChanged(); return true;
    }
    // Reorder the canonical list, never a filtered view, so both surfaces and persisted snapshots agree.
    public bool ReorderCollection(string id, string targetId, bool after = false)
    {
        int from = _collections.FindIndex(group => group.Id == id), target = _collections.FindIndex(group => group.Id == targetId);
        if (from < 0 || target < 0 || from == target) return false;
        int to = target + (after ? 1 : 0);
        if (from < to) to--;
        if (from == to) return false;
        var group = _collections[from]; _collections.RemoveAt(from); _collections.Insert(to, group);
        IndexChanged(); return true;
    }
    public bool RemoveCollection(string id)
    {
        if (_collections.RemoveAll(group => group.Id == id) == 0) return false;
        for (int i = 0; i < _items.Count; i++) if (_items[i].CollectionId == id) _items[i] = _items[i] with { CollectionId = "" };
        IndexChanged(); return true;
    }
    public ClipboardSnapshot Snapshot() => new() { Items = [.. _items.Select(item => item with { Tags = [.. item.Tags] })], Collections = [.. _collections] };
    public void Restore(ClipboardSnapshot snapshot, ClipboardSettings settings, DateTimeOffset now)
    {
        _items.Clear(); _collections.Clear(); _metrics.Clear(); _fingerprints.Clear();
        // Before v3 the four starter collections could not be deleted; newer lists are authoritative, including an empty list.
        if (snapshot.Version < 3) _collections.AddRange(DefaultCollections());
        var collectionIds = new HashSet<string>();
        foreach (var group in snapshot.Collections ?? [])
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Id) || string.IsNullOrWhiteSpace(group.Name) || group.Name.Trim().Length > 32) continue;
            string name = group.Name.Trim();
            if (collectionIds.Contains(group.Id) || _collections.Any(candidate => candidate.Id != group.Id && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            int at = _collections.FindIndex(candidate => candidate.Id == group.Id);
            if (at < 0 && _collections.Count >= 20) continue;
            collectionIds.Add(group.Id);
            var restored = group with { Name = name, BackgroundColor = ClipboardCollection.NormalizeColor(group.BackgroundColor), Icon = ClipboardCollection.ValidIcon(group.Icon) ? group.Icon : "" };
            if (at >= 0) _collections[at] = restored; else _collections.Add(restored);
        }
        var ids = new HashSet<string>(); var fingerprints = new HashSet<string>(); long bytes = 0;
        foreach (var item in (snapshot.Items ?? []).Where(item => item is not null).OrderByDescending(item => item.CopiedAt))
        {
            if (item.Content is null || !item.Content.Valid || string.IsNullOrWhiteSpace(item.Id)
                || item.SourceName is null || item.SourcePath is null || item.CollectionId is null || item.Title is null || item.Notes is null || item.Tags is null
                || item.Title.Length > 160 || item.Notes.Length > 4096 || item.Tags.Length > 20 || item.Tags.Any(tag => tag is null || tag.Length > 64) || !ids.Add(item.Id) || !fingerprints.Add(item.Fingerprint)) continue;
            if (bytes + item.Content.ByteCount > MaxTotalBytes) continue;
            bytes += item.Content.ByteCount;
            // Older snapshots did not retain recapture totals; only their first capture and known reuses are recoverable.
            int copies = snapshot.Version < 4 ? Math.Clamp(item.UseCount, 0, 999_999) + 1 : Math.Clamp(item.CopyCount, 1, 1_000_000);
            _items.Add(item with { CollectionId = _collections.Any(group => group.Id == item.CollectionId) ? item.CollectionId : "", UseCount = Math.Clamp(item.UseCount, 0, 1_000_000), CopyCount = copies });
        }
        CleanInternal(settings, now); IndexChanged();
    }
}
