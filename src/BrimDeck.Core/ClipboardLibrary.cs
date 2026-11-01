using System.Globalization;
using System.Text;

namespace BrimDeck.Core;

public enum ClipboardArchiveScope { Active, Archived, All }
public enum ClipboardSort { Time, Count }
public sealed record ClipboardQuery
{
    public string Text { get; init; } = "";
    public string CollectionId { get; init; } = "";
    public bool FavoritesOnly { get; init; }
    public string Source { get; init; } = "";
    public bool UnknownSourceOnly { get; init; }
    public IReadOnlyCollection<ClipboardContentKind> Kinds { get; init; } = [];
    public ClipboardArchiveScope Archive { get; init; }
    public ClipboardSort Sort { get; init; }
}
public sealed record ClipboardLibraryStats(int Total, int Active, int Archived, int Favorites, int Today, long Bytes, int Uses);
public sealed record ClipboardApplicationGroup(string Source, int Count)
{
    public bool Unknown => Source.Length == 0;
    public string Label => Unknown ? Loc.T("未知应用", "Unknown app") : Source;
}
public sealed record ClipboardTimelineGroup(DateOnly Date, IReadOnlyList<ClipboardEntry> Items)
{
    public string Label(DateTime today) => Date == DateOnly.FromDateTime(today) ? Loc.T("今天", "Today")
        : Date == DateOnly.FromDateTime(today.AddDays(-1)) ? Loc.T("昨天", "Yesterday") : Date.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
}

public sealed partial class ClipboardHistory
{
    public IEnumerable<ClipboardEntry> Query(ClipboardQuery query)
    {
        string[] terms = Fold(query.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = _items.Where(item => (query.Archive == ClipboardArchiveScope.All || item.Archived == (query.Archive == ClipboardArchiveScope.Archived))
            && (!query.FavoritesOnly || item.Favorite) && InCollection(item, query.CollectionId)
            && (query.UnknownSourceOnly ? string.IsNullOrWhiteSpace(item.SourceName) : query.Source.Length == 0 || string.Equals(item.SourceName, query.Source, StringComparison.OrdinalIgnoreCase))
            && (query.Kinds.Count == 0 || query.Kinds.Contains(item.Content.Kind))
            && Matches(item, terms));
        // Count is a view projection; retention and canonical recapture order never depend on the active sort.
        return query.Sort == ClipboardSort.Count
            ? matches.OrderByDescending(item => item.CopyCount).ThenByDescending(item => item.CopiedAt).ThenBy(item => item.Id, StringComparer.Ordinal)
            : matches.OrderByDescending(item => item.CopiedAt);
    }
    private static bool Matches(ClipboardEntry item, string[] terms)
    {
        if (terms.Length == 0) return true;
        string text = Fold(string.Join('\n', item.Title, item.Notes, string.Join(' ', item.Tags), item.Content.Text,
            string.Join('\n', item.Content.Files), item.SourceName, item.Content.Language));
        return terms.All(term => text.Contains(term, StringComparison.Ordinal) || Subsequence(term, text));
    }
    private static bool Subsequence(string term, string text)
    {
        // A bounded gap admits abbreviations and missing letters without matching unrelated words across an entire log.
        if (term.Length < 3) return false;
        int at = 0, start = -1;
        foreach (char c in text)
        {
            if (at > 0 && c is '\n' or '\r') { at = 0; start = -1; }
            if (c == term[at]) { if (at == 0) start = 0; if (++at == term.Length) return true; }
            if (start >= 0 && ++start > term.Length * 3) { at = 0; start = -1; }
        }
        return false;
    }
    private static string Fold(string text)
    {
        string value = text.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(value.Length);
        foreach (char c in value) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) result.Append(char.ToLowerInvariant(c));
        return result.ToString();
    }
    public static IReadOnlyList<ClipboardTimelineGroup> Timeline(IEnumerable<ClipboardEntry> items)
        => [.. items.GroupBy(item => DateOnly.FromDateTime(item.CopiedAt.LocalDateTime)).OrderByDescending(group => group.Key)
            .Select(group => new ClipboardTimelineGroup(group.Key, group.OrderByDescending(item => item.CopiedAt).ToList()))];
    public ClipboardLibraryStats Statistics(DateTimeOffset now) => new(_items.Count, _items.Count(item => !item.Archived), _items.Count(item => item.Archived),
        _items.Count(item => item.Favorite), _items.Count(item => item.CreatedAt.LocalDateTime.Date == now.LocalDateTime.Date),
        _items.Sum(item => item.Content.ByteCount), _items.Sum(item => item.UseCount));
    public string[] Sources => [.. _items.Select(item => item.SourceName).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
    // Application groups are derived from captured provenance, never from assigned collections or installed-software scans.
    // Keep zero-count groups while searching so the selected application does not disappear under the user's pointer.
    public IReadOnlyList<ClipboardApplicationGroup> ApplicationGroups(ClipboardQuery query)
    {
        static string SourceOf(ClipboardEntry item) => string.IsNullOrWhiteSpace(item.SourceName) ? "" : item.SourceName;
        var counts = Query(query with { CollectionId = "", Source = "", UnknownSourceOnly = false })
            .GroupBy(SourceOf, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return _items.GroupBy(SourceOf, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ClipboardApplicationGroup(group.Key, counts.GetValueOrDefault(group.Key)))
            .OrderBy(group => group.Unknown).ThenBy(group => group.Source, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    public bool Archive(string id, bool archived) => Edit(id, item => item with { Archived = archived });
    // ItemStore.touch accounting, without altering capture time or extending the host's retention policy.
    public bool RecordUse(string id, DateTimeOffset now) => Edit(id, item => item with
    { UseCount = Math.Min(1_000_000, item.UseCount + 1), CopyCount = Math.Min(1_000_000, item.CopyCount + 1), LastUsedAt = now });

    // Management edits preserve origin and timeline identity; recall counts do not masquerade as a newly captured copy.
    public bool Update(string id, string title, string notes, IEnumerable<string> tags, ClipboardContent content, DateTimeOffset now, out string problem)
    {
        problem = "";
        var item = _items.FirstOrDefault(item => item.Id == id);
        if (item is null) { problem = Loc.T("记录已被删除，请刷新。", "This entry was removed. Refresh the library."); return false; }
        title = title.Trim(); notes = notes.Trim();
        var labels = tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (title.Length > 160 || notes.Length > 4096 || labels.Length > 20 || labels.Any(tag => tag.Length > 64) || !content.Valid)
        { problem = Loc.T("内容或元数据超出限制。标题最多 160 字、备注 4096 字、标签 20 个，内容须为有效格式。", "Invalid content or metadata. Use up to 160 title characters, 4096 note characters and 20 tags with valid content."); return false; }
        string fingerprint = content.Fingerprint();
        if (_fingerprints.TryGetValue(fingerprint, out var duplicateId) && duplicateId != id)
        { problem = Loc.T("已有相同内容，请整理现有条目，避免覆盖分类和备注。", "Identical content already exists. Organize that entry instead of overwriting its metadata."); return false; }
        if (_items.Sum(entry => Measure(entry).Bytes) - item.Content.ByteCount + content.ByteCount > MaxTotalBytes)
        { problem = Loc.T("历史内容已达到 32 MB 上限，请先删除部分记录。", "History has reached its 32 MB limit. Remove some entries first."); return false; }
        return Edit(id, previous => previous with { Title = title, Notes = notes, Tags = labels, Content = content, EditedAt = now });
    }
    // Validate before adding: a rejected draft must never become a partial entry or overwrite a deduplicated item.
    public ClipboardEntry? CreateSnippet(string title, string notes, IEnumerable<string> tags, ClipboardContent content, DateTimeOffset now, ClipboardSettings settings, out string problem)
    {
        title = title.Trim(); notes = notes.Trim();
        var labels = tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        problem = "";
        if (title.Length > 160 || notes.Length > 4096 || labels.Length > 20 || labels.Any(tag => tag.Length > 64) || !content.Valid
            || content.Kind is ClipboardContentKind.Image or ClipboardContentKind.Screenshot or ClipboardContentKind.Files)
        { problem = Loc.T("内容或元数据无效；标题最多 160 字、备注 4096 字、标签 20 个。", "Invalid content or metadata; use up to 160 title characters, 4096 note characters and 20 tags."); return null; }
        string fingerprint = content.Fingerprint();
        if (_fingerprints.ContainsKey(fingerprint))
        { problem = Loc.T("已有相同内容，请编辑现有记录。", "Identical content already exists. Edit that entry instead."); return null; }
        if (_items.Count(item => item.Favorite || item.Archived) >= settings.ItemLimit
            || _items.Where(item => item.Favorite || item.Archived).Sum(item => Measure(item).Bytes) + content.ByteCount > MaxTotalBytes)
        { problem = Loc.T("收藏或归档已占满容量，请先整理历史。", "Favorites or archived entries fill the capacity. Organize the library first."); return null; }
        var entry = new ClipboardEntry { Content = content, Title = title, Notes = notes, Tags = labels,
            SourceName = Loc.T("手动创建", "Manual entry"), CreatedAt = now, CopiedAt = now, EditedAt = now };
        _items.Insert(0, entry); CleanInternal(settings, now); IndexChanged(); return entry;
    }
    public static string DisplayTitle(ClipboardEntry entry)
    {
        if (entry.Title.Length > 0) return entry.Title;
        string text = entry.Content.Kind is ClipboardContentKind.Image or ClipboardContentKind.Screenshot ? ClipboardContentTypes.Label(entry.Content.Kind) + " · " + entry.Content.Preview
            : entry.Content.Text.Length > 0 ? entry.Content.Text : entry.Content.Preview;
        string line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ClipboardContentTypes.Label(entry.Content.Kind);
        return line[..Math.Min(line.Length, 100)];
    }
}
