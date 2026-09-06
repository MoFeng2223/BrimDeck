using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

public enum CompactStyle { Notch, Capsule, Line }
public enum WindowBehavior { Normal, Line, Hide }
public enum ScreenContext { Desktop, Maximized, Borderless, Exclusive }
public enum ProviderId { Claude, Codex, Antigravity, Cursor }

// One row of the application list. Colors are "#RRGGBB"; an empty string means the preset default.
public sealed class AppEntry
{
    public ProviderId Id { get; set; }
    public bool Enabled { get; set; } = true;
    public string ThemeColor { get; set; } = "";
    public string WarningColor { get; set; } = "";
    public string CriticalColor { get; set; } = "";
    [JsonIgnore] public string Theme => ThemeColor.Length > 0 ? ThemeColor : AppPresets.ThemeColor(Id);
    [JsonIgnore] public string Warning => WarningColor.Length > 0 ? WarningColor : AppPresets.WarningColor;
    [JsonIgnore] public string Critical => CriticalColor.Length > 0 ? CriticalColor : AppPresets.CriticalColor;
    public AppEntry Copy() => (AppEntry)MemberwiseClone();
    public void Normalize()
    {
        ThemeColor = AppPresets.NormalizeColor(ThemeColor);
        WarningColor = AppPresets.NormalizeColor(WarningColor);
        CriticalColor = AppPresets.NormalizeColor(CriticalColor);
    }
}

public static class AppPresets
{
    public const string WarningColor = "#E5C890", CriticalColor = "#E78284";
    public const double WarningPercent = 70, CriticalPercent = 90;
    // Each column needs this much width; the panel's minimum width follows the number of enabled applications.
    public const double MinimumColumnWidth = 150;
    public static readonly string[] Palette =
    [
        "#E5A385", "#F0B37E", "#E5C890", "#98DBB0", "#7FD1C4", "#9CB9FF",
        "#B9A8FF", "#E7A0D0", "#E78284", "#D4D5DF", "#8E8E93", "#F5F5F7"
    ];
    public static string Name(ProviderId id) => id == ProviderId.Claude ? "Claude" : id.ToString();
    public static string Description(ProviderId id) => id switch
    {
        ProviderId.Claude => "Claude Code 或桌面版的订阅配额与本机记录",
        ProviderId.Codex => "账户配额与本机记录",
        ProviderId.Antigravity => "本地服务中的 Gemini 与 Claude 配额",
        _ => "当前账期的模型额度"
    };
    // The original preset colors: coral for Claude, mint for Codex, periwinkle for Antigravity, light gray for Cursor.
    public static string ThemeColor(ProviderId id) => id switch
    { ProviderId.Claude => "#E5A385", ProviderId.Codex => "#98DBB0", ProviderId.Antigravity => "#9CB9FF", _ => "#D4D5DF" };
    public static string QuotaColor(AppEntry entry, double usedPercent)
        => usedPercent >= CriticalPercent ? entry.Critical : usedPercent >= WarningPercent ? entry.Warning : entry.Theme;
    public static int Level(double usedPercent) => usedPercent >= CriticalPercent ? 2 : usedPercent >= WarningPercent ? 1 : 0;
    public static bool IsColor(string value) => Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$");
    public static string NormalizeColor(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 6 && Regex.IsMatch(text, "^[0-9A-Fa-f]{6}$")) text = "#" + text;
        return IsColor(text) ? "#" + text[1..].ToUpperInvariant() : "";
    }
}

public sealed class DeckSettings
{
    private List<AppEntry>? _apps;
    public CompactStyle Style { get; set; } = CompactStyle.Notch;
    public bool CompactSummary { get; set; } = true;
    public double Width { get; set; } = 760;
    public double Height { get; set; } = 240;
    public int OpenDelay { get; set; } = 200;
    public int CloseDelay { get; set; } = 300;
    public int AnimationDuration { get; set; } = 240;
    public bool Animations { get; set; } = true;
    public WindowBehavior Maximized { get; set; } = WindowBehavior.Line;
    public WindowBehavior Borderless { get; set; } = WindowBehavior.Hide;
    public WindowBehavior Exclusive { get; set; } = WindowBehavior.Hide;
    public bool UsagePage { get; set; } = true;
    // The ordered application list; the order is the column order of the expanded panel.
    public List<AppEntry> Apps
    {
        get => _apps ??= Enum.GetValues<ProviderId>().Select(id => new AppEntry { Id = id, Enabled = LegacyEnabled(id) }).ToList();
        set => _apps = value;
    }
    // Switches saved by earlier versions; read once for migration and never written again.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public bool Claude { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public bool Codex { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public bool Antigravity { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public bool Cursor { get; set; } = true;
    private bool LegacyEnabled(ProviderId id) => id switch
    { ProviderId.Claude => Claude, ProviderId.Codex => Codex, ProviderId.Antigravity => Antigravity, _ => Cursor };

    [JsonIgnore] public IReadOnlyList<AppEntry> EnabledApps => UsagePage ? Apps.Where(app => app.Enabled).ToList() : [];
    [JsonIgnore] public double MinimumWidth => Math.Clamp(Math.Max(440, AppPresets.MinimumColumnWidth * Math.Max(1, EnabledApps.Count) + 40), 440, 1200);
    public AppEntry? Entry(ProviderId id) => Apps.FirstOrDefault(app => app.Id == id);
    public bool Enabled(ProviderId id) => UsagePage && Entry(id) is { Enabled: true };
    public void SetEnabled(ProviderId id, bool enabled)
    {
        var entry = Entry(id);
        if (entry is null) { if (enabled) Apps.Add(new AppEntry { Id = id }); }
        else entry.Enabled = enabled;
    }
    public WindowBehavior Behavior(ScreenContext context) => context switch
    {
        ScreenContext.Maximized => Maximized, ScreenContext.Borderless => Borderless,
        ScreenContext.Exclusive => Exclusive, _ => WindowBehavior.Normal
    };
    public DeckSettings Copy()
    {
        var copy = (DeckSettings)MemberwiseClone();
        copy._apps = _apps?.Select(app => app.Copy()).ToList();
        return copy;
    }
    public void Normalize()
    {
        var seen = new HashSet<ProviderId>();
        Apps = Apps.Where(app => app is not null && Enum.IsDefined(app.Id) && seen.Add(app.Id)).ToList();
        foreach (var app in Apps) app.Normalize();
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 440, 1200) : 760;
        Width = Math.Max(Width, MinimumWidth);
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 140, 400) : 240;
        OpenDelay = Math.Clamp(OpenDelay, 0, 2000);
        CloseDelay = Math.Clamp(CloseDelay, 0, 2000);
        AnimationDuration = Math.Clamp(AnimationDuration, 100, 600);
        if (!Enum.IsDefined(Style)) Style = CompactStyle.Notch;
        if (!Enum.IsDefined(Maximized)) Maximized = WindowBehavior.Line;
        if (!Enum.IsDefined(Borderless)) Borderless = WindowBehavior.Hide;
        if (!Enum.IsDefined(Exclusive)) Exclusive = WindowBehavior.Hide;
    }
}

public sealed class SettingsStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public string? LoadWarning { get; private set; }
    public DeckSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var settings = JsonSerializer.Deserialize<DeckSettings>(File.ReadAllText(FilePath)) ?? new();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LoadWarning = "无法读取已保存的设置，本次使用默认值。";
            return new();
        }
    }
    public void Save(DeckSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}
