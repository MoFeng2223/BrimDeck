namespace DropShelf;

/// <summary>宿主只提供生命周期与存储边界；拖放、手势、置物架和快速操作仍由上游实现。</summary>
public sealed class EmbeddedHostOptions
{
    public required string DataDirectory { get; init; }
    public required string CacheDirectory { get; init; }
    public required Action Exit { get; init; }
    public required Func<bool, bool> SetStartWithWindows { get; init; }
    public required Func<bool> GetStartWithWindows { get; init; }
    public Func<bool>? AnimationsEnabled { get; init; }
    public Func<bool>? AutomaticEnabled { get; init; }
    public Func<int>? ThumbnailLimit { get; init; }
    public string InstanceName { get; init; } = "BrimDeck.DropShelf";
    public bool Enabled { get; init; } = true;
    public DropShelf.Models.TransferMode? LegacyDragOutMode { get; init; }
    public bool? LegacyCloseWhenEmpty { get; init; }
    public Func<DropShelf.Models.AppLanguage>? Language { get; init; }
    public Action? ShowSettings { get; init; }
    public Action? ShowHelp { get; init; }
    public Func<bool>? FloatingEnabled { get; init; }
    public Action? ShowIsland { get; init; }
    public Func<bool>? IsIslandBusy { get; init; }
    public Func<DropShelf.Models.HotkeyGesture, bool>? HotkeyConflicts { get; init; }
    public Func<System.Windows.Window?>? SettingsWindow { get; init; }
}
