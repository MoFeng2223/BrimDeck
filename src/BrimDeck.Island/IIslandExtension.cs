using System.Windows;

namespace BrimDeck.Island;

/// <summary>Theme colors supplied by BrimDeck. Extensions should create and own their brushes.</summary>
public sealed record IslandColors(string Normal, string Warning, string Critical, string Primary, string Secondary, string Tertiary, string Track, string Fill);

/// <summary>The stable managed plugin contract. Plugin DLLs reference BrimDeck.Island.dll from the SDK package.</summary>
public interface IIslandExtension : IDisposable
{
    string Id { get; }
    string TitleZh { get; }
    string TitleEn { get; }
    string Glyph { get; }
    FrameworkElement CreateView(IslandColors colors);
    FrameworkElement CreateView(IslandPluginContext context, IslandColors colors) => CreateView(colors);
    IReadOnlyList<IslandPage> Pages => [new IslandPage("main", TitleZh, TitleEn, Glyph, CreateView)];
    void SetDemand(bool shown);

    string Version => "1.0.0";
    string Author => "";
    string Description => "";
    IReadOnlyList<string> RequestedPermissions => Array.Empty<string>();
    IReadOnlyList<IslandCommand> Commands => Array.Empty<IslandCommand>();
    FrameworkElement? CreateSettingsView(IslandPluginContext context) => null;
    FrameworkElement? CreateCompactView(IslandPluginContext context) => null;
    void OnThemeChanged(IslandColors colors) { }
    void OnHostEvent(IslandHostEvent hostEvent) { }
    void OnDataChanged(IslandDataSnapshot snapshot) { }
    void IDisposable.Dispose() { }
}

/// <summary>A page contributed by an extension. Stable page IDs are combined with the plugin ID for saved settings.</summary>
public sealed record IslandPage(string Id, string TitleZh, string TitleEn, string Glyph,
    Func<IslandPluginContext, IslandColors, FrameworkElement> CreateView);

public sealed record IslandCommand(string Id, string TitleZh, string TitleEn, Action Execute);
public sealed record IslandHostEvent(string Name, IReadOnlyDictionary<string, string> Values);
public sealed record IslandDataSnapshot(DateTimeOffset CapturedAt, IReadOnlyDictionary<string, double> Numbers, IReadOnlyDictionary<string, string> Text);

/// <summary>Per-plugin services. This API is a convenience boundary, not a security sandbox: DLL code runs in-process.</summary>
public sealed class IslandPluginContext
{
    private readonly Func<string, string, string?> _read;
    private readonly Action<string, string, string> _write;
    private readonly Action<string> _control;
    private readonly Action<string> _subscribe;

    public IslandPluginContext(string pluginId, string dataDirectory, IslandColors colors,
        Func<string, string, string?> read, Action<string, string, string> write,
        Action<string> control, Action<string> subscribe)
    {
        PluginId = pluginId; DataDirectory = dataDirectory; Colors = colors;
        _read = read; _write = write; _control = control; _subscribe = subscribe;
    }

    public string PluginId { get; }
    public string DataDirectory { get; }
    public IslandColors Colors { get; }
    public string? ReadValue(string key) => _read(PluginId, key);
    public void WriteValue(string key, string value) => _write(PluginId, key, value);
    public void RequestControl(string action) => _control(action);
    public void Subscribe(string dataKind) => _subscribe(dataKind);
}
