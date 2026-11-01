namespace BrimDeck.Core;

// Stable ids are independent of display language and of the lifetime of a plugin package.
public static class DeckPageIds
{
    public const string Usage = "@builtin/usage", Music = "@builtin/music", Monitor = "@builtin/monitor", LegacyMiniApps = "@builtin/miniapps";
    public static string Of(DeckPage page) => page switch { DeckPage.Music => Music, DeckPage.Monitor => Monitor, DeckPage.MiniApps => LegacyMiniApps, _ => Usage };
    public static bool Valid(string? id) => id is Usage or Music or Monitor or LegacyMiniApps || id is { Length: > 2 and <= 256 } && id[0] != '@'
        && id.IndexOf(':') is > 0 && !id.Any(char.IsWhiteSpace) && !id.Any(char.IsControl);
}

public sealed record DeckPageDefinition(string Id, string Title, string TitleEn, DeckPage? BuiltIn = null)
{
    public string? Owner { get; init; }
    public string? IconPath { get; init; }
    public double Width { get; init; } = 480;
    public double Height { get; init; } = 266;
    public double MinWidth { get; init; } = DeckPages.MinWidth;
    public double MaxWidth { get; init; } = DeckPages.MaxWidth;
    public double MinHeight { get; init; } = DeckPages.MinHeight;
    public double MaxHeight { get; init; } = DeckPages.MaxHeight;
    public bool Available { get; init; } = true;
}

public sealed class DeckPagePreference
{
    public bool Visible { get; set; } = true;
    public double? Width { get; set; }
    public double? Height { get; set; }
    internal DeckPagePreference Copy() => new() { Visible = Visible, Width = Width, Height = Height };
}

public sealed class DeckPagePreferences
{
    public int Version { get; set; }
    public List<string> Order { get; set; } = [];
    public Dictionary<string, DeckPagePreference> Entries { get; set; } = new(StringComparer.Ordinal);
    public string? DefaultId { get; set; }
    public string? LastId { get; set; }
    public DeckPagePreferences Copy() => new() { Version = Version, Order = [.. Order], Entries = Entries.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal), DefaultId = DefaultId, LastId = LastId };
    public void Normalize(DeckSettings settings)
    {
        if (Version == 0)
        {
            Order = settings.PageOrder.Select(DeckPageIds.Of).ToList();
            DefaultId = DeckPages.Opening(settings.DefaultPage) is { } page ? DeckPageIds.Of(page) : null;
            LastId = DeckPageIds.Of(settings.LastPage);
            Version = 1;
        }
        Order = (Order ?? []).Where(DeckPageIds.Valid).Distinct(StringComparer.Ordinal).Take(2048).ToList();
        Entries = (Entries ?? []).Where(p => DeckPageIds.Valid(p.Key) && p.Value is not null).Take(2048).ToDictionary(p => p.Key, p =>
        {
            var value = p.Value;
            value.Width = Fit(value.Width, DeckPages.MinWidth, DeckPages.MaxWidth);
            value.Height = Fit(value.Height, DeckPages.MinHeight, DeckPages.MaxHeight);
            return value;
        }, StringComparer.Ordinal);
        if (DefaultId is not null && !DeckPageIds.Valid(DefaultId)) DefaultId = null;
        if (!DeckPageIds.Valid(LastId)) LastId = DeckPageIds.Usage;
    }
    private static double? Fit(double? value, double min, double max) => value is { } number && double.IsFinite(number) ? Math.Clamp(number, min, max) : null;
}

// Runtime fallback never writes preferences: disabling or removing a package is not a new user choice.
public sealed class DeckPageDirectory
{
    private readonly DeckSettings _settings;
    public IReadOnlyList<DeckPageDefinition> All { get; }
    public IReadOnlyList<DeckPageDefinition> Visible => All.Where(Shown).ToArray();
    public DeckPageDirectory(DeckSettings settings, IEnumerable<DeckPageDefinition> plugins)
    {
        _settings = settings;
        if (settings.Pages.Version == 0) settings.Pages.Normalize(settings);
        DeckPageDefinition[] builtIns = [new(DeckPageIds.Usage, "AI 用量", "AI usage", DeckPage.Usage), new(DeckPageIds.Music, "音乐", "Music", DeckPage.Music), new(DeckPageIds.Monitor, "系统监控", "System monitor", DeckPage.Monitor)];
        var entries = builtIns.Concat(plugins.Where(p => p.BuiltIn is null && DeckPageIds.Valid(p.Id) && !p.Id.StartsWith('@'))).DistinctBy(p => p.Id, StringComparer.Ordinal).ToArray();
        var saved = settings.Pages.Order.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index, StringComparer.Ordinal);
        All = entries.OrderBy(p => saved.GetValueOrDefault(p.Id, int.MaxValue)).ThenBy(p => Array.IndexOf(entries, p)).ToArray();
    }
    public DeckPageDefinition? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
    public bool Shown(DeckPageDefinition page) => page.Available && (page.BuiltIn is { } builtIn ? DeckPages.Shown(_settings, builtIn)
        : _settings.Modules.Enabled && _settings.MiniApps.Enabled && (!_settings.Pages.Entries.TryGetValue(page.Id, out var preference) || preference.Visible));
    public DeckPageDefinition Resolve(string? wanted) => Find(wanted) is { } page && Shown(page) ? page : Visible.FirstOrDefault() ?? All[0];
    public string Opening(string? requested) => _settings.Pages.DefaultId ?? requested ?? _settings.Pages.LastId ?? DeckPageIds.Usage;
    public (double MinWidth, double MaxWidth, double MinHeight, double MaxHeight) Limits(DeckPageDefinition page)
    {
        if (page.BuiltIn is { } builtIn) return DeckPages.Limits(_settings, builtIn);
        static double Fit(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        double minW = Fit(page.MinWidth, DeckPages.MinWidth, DeckPages.MaxWidth, DeckPages.MinWidth), minH = Fit(page.MinHeight, DeckPages.MinHeight, DeckPages.MaxHeight, DeckPages.MinHeight);
        return (minW, Fit(page.MaxWidth, minW, DeckPages.MaxWidth, DeckPages.MaxWidth), minH, Fit(page.MaxHeight, minH, DeckPages.MaxHeight, DeckPages.MaxHeight));
    }
    public (double Width, double Height) Size(DeckPageDefinition page)
    {
        if (page.BuiltIn is { } builtIn) return DeckPages.Size(_settings, builtIn);
        var limits = Limits(page); _settings.Pages.Entries.TryGetValue(page.Id, out var preference);
        // The former shared mini-app size seeds each legacy page independently, until the user gives it its own size.
        double width = preference?.Width ?? page.Width, height = preference?.Height ?? page.Height;
        if (!double.IsFinite(width)) width = page.Width;
        if (!double.IsFinite(height)) height = page.Height;
        return (Math.Clamp(width, limits.MinWidth, limits.MaxWidth), Math.Clamp(height, limits.MinHeight, limits.MaxHeight));
    }
    public void SetSize(string id, double width, double height)
    {
        if (Find(id) is not { } page) return;
        if (page.BuiltIn is { } builtIn) { DeckPages.SetSize(_settings, builtIn, width, height); return; }
        var limits = Limits(page);
        if (!double.IsFinite(width) || !double.IsFinite(height)) throw new ArgumentOutOfRangeException(nameof(width));
        var preference = Preference(id); preference.Width = Math.Clamp(width, limits.MinWidth, limits.MaxWidth); preference.Height = Math.Clamp(height, limits.MinHeight, limits.MaxHeight);
    }
    public void SetVisible(string id, bool visible)
    {
        switch (Find(id)?.BuiltIn)
        {
            case DeckPage.Usage: _settings.UsagePage = visible; break;
            case DeckPage.Music: _settings.MusicPage = visible; break;
            case DeckPage.Monitor: _settings.MonitorPage = visible; break;
            default: if (DeckPageIds.Valid(id)) Preference(id).Visible = visible; break;
        }
    }
    private DeckPagePreference Preference(string id)
    { if (!_settings.Pages.Entries.TryGetValue(id, out var value)) _settings.Pages.Entries[id] = value = new(); return value; }
}
