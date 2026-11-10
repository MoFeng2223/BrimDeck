namespace BrimDeck.Modules;

public static class ModuleApi
{
    public const int Major = 1, Minor = 10;
    public const string HostInfo = "host.info", UsageRead = "usage.read", MediaRead = "media.read", SystemRead = "system.read", NotificationsShow = "notifications.show";
    // The panel's own state: expanded, page, theme, scale, full screen and displays, as one dated snapshot.
    public const string HostState = "host.state";
    public const string UiPresentation = "ui.presentation";
    public const string UiCompactParts = "ui.compact-parts";
    public const string CommandsInvoke = "commands.invoke";
    public const string UiCommands = "ui.commands";
    public const string UiPages = "ui.pages", UiActivities = "ui.activities", UiWidgets = "ui.widgets", UiSettings = "ui.settings", StorageData = "storage.data";
    public static IReadOnlyList<ModuleCapability> Catalog { get; } = ModuleCapabilities.Create();
    public static IReadOnlyList<string> Capabilities { get; } = Array.AsReadOnly(Catalog.Select(c => c.Id).ToArray());
    public static IReadOnlyList<string> Permissions { get; } = Array.AsReadOnly(Catalog.Select(c => c.Permission).OfType<string>().Distinct(StringComparer.Ordinal).ToArray());
    public static ModuleCapability? Describe(string id) => Catalog.FirstOrDefault(c => c.Id == id);
}
public sealed class ModuleApiException(string code, string message) : InvalidOperationException(message)
{ public string Code { get; } = code; }
public enum ModuleDataStatus { Unavailable, Available, Stale, Disabled }
public sealed record ModuleHostInfo(string ApplicationVersion, int ContractMajor, int ContractMinor, IReadOnlyList<string> Capabilities)
{ public IReadOnlyList<ModuleCapability> CapabilityDetails { get; init; } = Array.AsReadOnly(Array.Empty<ModuleCapability>()); }
public interface IModuleHostInfo { ModuleHostInfo GetInfo(); }
public interface IModuleSnapshotReader<T> where T : class
{
    T GetSnapshot();
    IDisposable Subscribe(Action<T> listener);
}
public sealed record ModuleUsageMetric(string Id, string Kind, string Label, double? UsedPercent, decimal? Amount,
    decimal? Used, decimal? Total, string Unit, string Currency, bool Unlimited, DateTimeOffset? ResetsAt, int? WindowMinutes)
{
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool AtLeast { get; init; }
}
public sealed record ModuleUsageEntry(string ProviderId, string Name, ModuleDataStatus Status, DateTimeOffset? UpdatedAt,
    IReadOnlyList<ModuleUsageMetric> Metrics, bool TokenUsageAvailable, long InputTokens, long OutputTokens, long TotalTokens)
{
    public bool TokenUsageComplete { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
}
public sealed record ModuleUsageSnapshot(long Revision, DateTimeOffset ObservedAt, IReadOnlyList<ModuleUsageEntry> Entries);
public interface IModuleUsage : IModuleSnapshotReader<ModuleUsageSnapshot>;
public sealed record ModuleMediaSnapshot(long Revision, DateTimeOffset ObservedAt, ModuleDataStatus Status, string PlaybackState,
    string Source, string Title, string Artist, string Album, double? PositionSeconds, double? DurationSeconds)
{
    public double PlaybackRate { get; init; } = 1;
    // When the position was read from the player, before any time since then was added (MediaTrack.PositionAt).
    public DateTimeOffset? PositionObservedAt { get; init; }
}
public interface IModuleMedia : IModuleSnapshotReader<ModuleMediaSnapshot>;

// One reading of the system monitor, with its own state: a value of a missing sensor is null and never a made-up zero.
// Available may hold a legitimate zero; Unavailable, Disabled and Stale say why there is no value (Reason, a stable code).
// ObservedAt is when the hardware was read, null where the host only knows when it projected the reading.
public sealed record ModuleReading<T> where T : struct
{
    public ModuleDataStatus Status { get; init; } = ModuleDataStatus.Unavailable;
    public T? Value { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public static ModuleReading<T> Available(T value, DateTimeOffset? observed = null) => new() { Status = ModuleDataStatus.Available, Value = value, ObservedAt = observed };
    public static ModuleReading<T> Missing(string? reason = null, DateTimeOffset? observed = null) => new() { Status = ModuleDataStatus.Unavailable, Reason = reason, ObservedAt = observed };
    public static ModuleReading<T> Disabled(string? reason = null) => new() { Status = ModuleDataStatus.Disabled, Reason = reason };
    public static ModuleReading<T> Stale(T? value, string? reason = null, DateTimeOffset? observed = null) => new() { Status = ModuleDataStatus.Stale, Value = value, Reason = reason, ObservedAt = observed };
}

// The readings of the system monitor as a module receives them. The first fields keep the shape 1.9 had, so a module built
// against an older contract reads the same properties; the readings below carry the per-field state.
public sealed record ModuleSystemSnapshot(long Revision, DateTimeOffset ObservedAt, ModuleDataStatus Status,
    double? CpuUsedPercent, double? MemoryUsedPercent, ulong? MemoryUsedBytes, ulong? MemoryTotalBytes,
    double? GpuUsedPercent, double? DownloadBytesPerSecond, double? UploadBytesPerSecond)
{
    public ModuleReading<double> CpuUsage { get; init; } = ModuleReading<double>.Missing();
    public ModuleReading<double> MemoryUsage { get; init; } = ModuleReading<double>.Missing();
    public ModuleReading<ulong> MemoryUsed { get; init; } = ModuleReading<ulong>.Missing();
    public ModuleReading<ulong> MemoryTotal { get; init; } = ModuleReading<ulong>.Missing();
    public ModuleReading<double> GpuUsage { get; init; } = ModuleReading<double>.Missing();
    public ModuleReading<double> DownloadRate { get; init; } = ModuleReading<double>.Missing();
    public ModuleReading<double> UploadRate { get; init; } = ModuleReading<double>.Missing();
    // Degrees Celsius. Source names where the reading came from ("processor", "acpi-zone", "hwinfo", "libre-hardware-monitor",
    // "driver"); Adapter names the graphics adapter a GPU temperature belongs to.
    public ModuleReading<double> CpuTemperature { get; init; } = ModuleReading<double>.Missing();
    public string? CpuTemperatureSource { get; init; }
    public ModuleReading<double> GpuTemperature { get; init; } = ModuleReading<double>.Missing();
    // Set when the temperature came from another adapter than the one used for GPU usage (that one has no sensor).
    public string? GpuTemperatureFrom { get; init; }
    // The graphics adapter the GPU readings belong to, by its stable key, and its name.
    public string? GpuAdapterKey { get; init; }
    public string? GpuAdapterName { get; init; }
    public ModuleReading<ulong> GpuDedicatedBytesUsed { get; init; } = ModuleReading<ulong>.Missing();
    public ModuleReading<ulong> GpuDedicatedBytesTotal { get; init; } = ModuleReading<ulong>.Missing();
    // Bytes per second over the whole machine's disks, in either direction.
    public ModuleReading<double> DiskReadBytesPerSecond { get; init; } = ModuleReading<double>.Missing();
    public ModuleReading<double> DiskWriteBytesPerSecond { get; init; } = ModuleReading<double>.Missing();
    // Watts. Estimated says the whole-machine figure is a sum of component readings (PowerOrigin), not a measurement;
    // Source names where it was read ("system-meter", "battery", "components").
    public ModuleReading<double> PowerWatts { get; init; } = ModuleReading<double>.Missing();
    public bool PowerEstimated { get; init; }
    public string? PowerSource { get; init; }
    public ModuleReading<double> CpuPowerWatts { get; init; } = ModuleReading<double>.Missing();
    public ModuleReading<double> GpuPowerWatts { get; init; } = ModuleReading<double>.Missing();
    // The internal drives, each with its lettered volumes; empty when storage was not read.
    public IReadOnlyList<ModuleStorageDrive> Storage { get; init; } = Array.AsReadOnly(Array.Empty<ModuleStorageDrive>());
    // Whether storage was read at all, and whether drive temperatures were read (ModuleReading states say the rest).
    public bool StorageRead { get; init; }
}
// One internal drive and the volumes Windows gives a letter on it. Bytes; Temperature is null where the drive was not asked
// (a hard disk, a drive behind a RAID controller) or gave none.
public sealed record ModuleStorageDrive(int Number, string? Model, bool SolidState, IReadOnlyList<ModuleStorageVolume> Volumes)
{
    public ModuleReading<double> Temperature { get; init; } = ModuleReading<double>.Missing();
}
public sealed record ModuleStorageVolume(string Letter, string Label, ulong TotalBytes, ulong FreeBytes)
{
    public ulong UsedBytes => TotalBytes - Math.Min(FreeBytes, TotalBytes);
}
public interface IModuleSystem : IModuleSnapshotReader<ModuleSystemSnapshot>;

// The panel the user sees, as of a revision. The host has one panel per chosen display; this snapshot is the main one,
// the panel on the primary or first chosen display, so a change on another display does not overwrite it.
public sealed record ModuleHostStateSnapshot(long Revision, DateTimeOffset ObservedAt)
{
    public bool Expanded { get; init; }
    // The page shown, as the module may see it: a built-in page id, or the generic "mini-apps" page while another
    // module's page is open. A module's own page is named in full only to that module.
    public string PageId { get; init; } = "@builtin/usage";
    public bool PageIsOwn { get; init; }
    // The panel theme in use: "dark", "light", "system", a built-in theme or a theme file's id.
    public string ThemeId { get; init; } = "";
    public bool ThemeIsDark { get; init; }
    public bool ThemeNightLook { get; init; }
    public bool HighContrast { get; init; }
    // Counts theme changes, so a module can tell a repaint from a rebuild.
    public int ThemeGeneration { get; init; }
    public double DeviceScale { get; init; } = 1;
    public double ContentScale { get; init; } = 1;
    // What fills the screen behind the panel: Desktop, Maximized, Borderless, Exclusive or BackgroundFullscreen.
    public string ScreenContext { get; init; } = "desktop";
    // The panel's display (Displays.Current id), and a count raised whenever the display layout changes.
    public string? DisplayId { get; init; }
    public int DisplayRevision { get; init; }
}
public interface IModuleHostState : IModuleSnapshotReader<ModuleHostStateSnapshot>;
public enum ModuleNoticeTone { Info, Success, Warning }
public sealed record ModuleNotice(string Id, string Title, string Message)
{
    public ModuleNoticeTone Tone { get; init; } = ModuleNoticeTone.Info;
    public int DurationMilliseconds { get; init; } = 2600;
    public double? ProgressPercent { get; init; }
}
public enum ModuleNoticeResult { Shown, Queued, RateLimited, Unavailable, NotReady }
public interface IModuleNotifications { ModuleNoticeResult Show(ModuleNotice notice); }
public interface IModuleHostEvent;
public sealed class ModuleRequirements
{
    public int ContractMinor { get; init; }
    public string[] Capabilities { get; init; } = [];
}
public enum ModuleReadStatus { Missing, Value, Corrupt, Unsupported }
public sealed record ModuleReadResult<T>(ModuleReadStatus Status, T? Value, string? Error = null);
public interface IModuleStructuredStorage { ModuleReadResult<T> ReadResult<T>(string key); }
public static class ModuleStorageExtensions
{
    public static ModuleReadResult<T> ReadResult<T>(this IModuleStorage storage, string key) => storage is IModuleStructuredStorage structured
        ? structured.ReadResult<T>(key) : new(ModuleReadStatus.Unsupported, default, "structured-read-unavailable");
}
