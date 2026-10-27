namespace BrimDeck.Modules;

public static class ModuleApi
{
    public const int Major = 1, Minor = 6;
    public const string HostInfo = "host.info", UsageRead = "usage.read", MediaRead = "media.read", SystemRead = "system.read", NotificationsShow = "notifications.show";
    public const string UiPresentation = "ui.presentation";
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
{ public double PlaybackRate { get; init; } = 1; }
public interface IModuleMedia : IModuleSnapshotReader<ModuleMediaSnapshot>;
public sealed record ModuleSystemSnapshot(long Revision, DateTimeOffset ObservedAt, ModuleDataStatus Status,
    double? CpuUsedPercent, double? MemoryUsedPercent, ulong? MemoryUsedBytes, ulong? MemoryTotalBytes,
    double? GpuUsedPercent, double? DownloadBytesPerSecond, double? UploadBytesPerSecond);
public interface IModuleSystem : IModuleSnapshotReader<ModuleSystemSnapshot>;
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
