using BrimDeck.Core;
using BrimDeck.Modules;

namespace BrimDeck;

internal static class ModuleHostProjection
{
    internal static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    internal static ModuleUsageSnapshot Usage(long revision, DateTimeOffset observed, IReadOnlyList<ProviderSnapshot> sources)
        => new(revision, observed, Freeze(sources.Select(source =>
        {
            var metrics = Freeze(source.Metrics.Select((metric, index) => new ModuleUsageMetric(
                string.IsNullOrEmpty(metric.Id) ? metric.Window is { } window ? "window-" + window : "metric-" + index : metric.Id,
                metric.Kind.ToString().ToLowerInvariant(), metric.Label,
                UsedPercent(metric),
                metric.Amount, metric.Used, metric.Total, metric.Unit, metric.Currency, metric.Unlimited, metric.ResetAt, metric.Window)
                { ExpiresAt = metric.ExpiresAt, AtLeast = metric.AtLeast }));
            bool available = metrics.Count > 0 || source.UsageAvailable;
            long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
            foreach (var entry in source.Entries)
            { input = Add(input, entry.Input); output = Add(output, entry.Output); cacheRead = Add(cacheRead, entry.CacheRead); cacheWrite = Add(Add(cacheWrite, entry.CacheWrite), entry.CacheWriteHour); }
            return new ModuleUsageEntry(source.Id.ToString().ToLowerInvariant(), source.Name,
                source.IsStale ? ModuleDataStatus.Stale : available ? ModuleDataStatus.Available : ModuleDataStatus.Unavailable,
                source.QuotaTime, metrics, source.UsageAvailable, input, output, Add(Add(input, output), Add(cacheRead, cacheWrite)))
            { TokenUsageComplete = source.UsageComplete, CacheReadTokens = cacheRead, CacheWriteTokens = cacheWrite };
        })));
    internal static ModuleMediaSnapshot Media(long revision, DateTimeOffset now, MediaTrack? track)
        => track is null ? new(revision, now, ModuleDataStatus.Unavailable, "unknown", "", "", "", "", null, null)
            : new(revision, now, ModuleDataStatus.Available, track.State.ToString().ToLowerInvariant(), track.Source, track.Title, track.Artist, track.Album,
                track.HasTimeline ? track.Position(now).TotalSeconds : null, track.HasTimeline ? track.Duration.TotalSeconds : null)
                {
                    PlaybackRate = double.IsFinite(track.Rate) && track.Rate >= 0 ? track.Rate : 1,
                    // When the player gave the position it is counted from; null where the timeline is the player's own.
                    PositionObservedAt = track.HasTimeline ? track.PositionAt : null
                };
    internal static ModuleSystemSnapshot System(long revision, MonitorSnapshot snapshot)
    {
        var observed = snapshot.Time.ToUniversalTime();
        bool hasCpu = snapshot.Cpu.TryGet(out var processor) && processor.Cores.Count > 0;
        bool memory = snapshot.Memory.TryGet(out var ram) && ram is not null, network = snapshot.Network.TryGet(out var traffic) && traffic is not null;
        bool graphics = snapshot.Gpu.TryGet(out var gpu) && gpu is not null;
        double? cpu = hasCpu ? Percent(processor.Usage * 100) : null;
        double? mem = memory && ram!.Total > 0 ? Percent(ram.UsedFraction * 100) : null;
        double? gpuPercent = graphics ? Percent(gpu!.Usage * 100) : null;
        double? down = network ? Rate(traffic!.DownBytesPerSecond) : null, up = network ? Rate(traffic!.UpBytesPerSecond) : null;
        var disk = snapshot.Disk.TryGet(out var rates) ? rates : null;
        var power = snapshot.Power.TryGet(out var drawn) ? drawn : null;
        var cpuPower = PartWatts(snapshot.CpuPower);
        var gpuPower = PartWatts(snapshot.GpuPower);
        var storage = snapshot.Storage.Count > 0 ? ModuleHostProjection.Storage(snapshot.Storage) : Array.AsReadOnly(Array.Empty<ModuleStorageDrive>());
        bool available = cpu is not null || mem is not null || down is not null || up is not null || gpuPercent is not null || disk is not null;
        return new(revision, observed, available ? ModuleDataStatus.Available : ModuleDataStatus.Unavailable,
            cpu, mem, mem is not null ? ram!.Used : null, mem is not null ? ram!.Total : null, gpuPercent, down, up)
        {
            CpuUsage = Reading<double, Core.CpuSample>(snapshot.Cpu, _ => cpu, observed),
            MemoryUsage = Reading<double, Core.MemorySample>(snapshot.Memory, _ => mem, observed),
            MemoryUsed = Reading<ulong, Core.MemorySample>(snapshot.Memory, _ => mem is not null ? ram!.Used : null, observed),
            MemoryTotal = Reading<ulong, Core.MemorySample>(snapshot.Memory, _ => mem is not null ? ram!.Total : null, observed),
            GpuUsage = Reading<double, Core.GpuSample>(snapshot.Gpu, _ => gpuPercent, observed),
            DownloadRate = Reading<double, Core.NetworkSample>(snapshot.Network, _ => down, observed),
            UploadRate = Reading<double, Core.NetworkSample>(snapshot.Network, _ => up, observed),
            CpuTemperature = Optional<Core.CpuSample>(snapshot.Cpu, sample => Celsius(sample.Temperature), observed, "no-sensor"),
            CpuTemperatureSource = !hasCpu ? null : processor!.TemperatureSource switch
            {
                Core.CpuTemperatureSource.Processor => "processor",
                Core.CpuTemperatureSource.HwInfo => "hwinfo",
                Core.CpuTemperatureSource.LibreHardwareMonitor => "libre-hardware-monitor",
                _ when processor.TemperatureZone is { Length: > 0 } => "acpi-zone",
                _ => null
            },
            GpuTemperature = Optional<Core.GpuSample>(snapshot.Gpu, sample => Celsius(sample.Temperature), observed, "no-sensor"),
            GpuTemperatureFrom = graphics ? gpu!.TemperatureFrom : null,
            GpuAdapterKey = graphics && gpu!.Key is { Length: > 0 } key ? key : null,
            GpuAdapterName = graphics ? gpu!.Name : null,
            GpuDedicatedBytesUsed = Optional<ulong, Core.GpuSample>(snapshot.Gpu, sample => sample.DedicatedBytes.TryGet(out var used) ? used : null, observed, "no-sensor"),
            GpuDedicatedBytesTotal = Optional<ulong, Core.GpuSample>(snapshot.Gpu, _ => graphics ? gpu!.DedicatedTotal : null, observed, "no-sensor"),
            DiskReadBytesPerSecond = disk is { } d ? ModuleReading<double>.Available(Rate(d.ReadBytesPerSecond) ?? 0, observed) : ModuleReading<double>.Missing(Reason(snapshot.Disk), observed),
            DiskWriteBytesPerSecond = disk is { } w ? ModuleReading<double>.Available(Rate(w.WriteBytesPerSecond) ?? 0, observed) : ModuleReading<double>.Missing(Reason(snapshot.Disk), observed),
            PowerWatts = power is { } p ? ModuleReading<double>.Available(p.Watts, observed) : ModuleReading<double>.Missing(Reason(snapshot.Power), observed),
            PowerEstimated = power?.Estimated == true,
            PowerSource = power?.Source switch
            {
                Core.PowerSource.SystemMeter => "system-meter",
                Core.PowerSource.Battery => "battery",
                Core.PowerSource.Components => "components",
                _ => null
            },
            CpuPowerWatts = cpuPower is { } cp ? ModuleReading<double>.Available(cp, observed) : ModuleReading<double>.Missing(null, observed),
            GpuPowerWatts = gpuPower is { } gp ? ModuleReading<double>.Available(gp, observed) : ModuleReading<double>.Missing(null, observed),
            Storage = storage,
            StorageRead = snapshot.Storage.Count > 0
        };
    }
    // Each drive and volume frozen, with the drive's temperature as its own reading. A drive Windows described without a
    // model keeps an empty name; a volume's label is what the file system gave.
    private static IReadOnlyList<ModuleStorageDrive> Storage(IReadOnlyList<Core.StorageDrive> drives) => Array.AsReadOnly(drives.Select(drive =>
        new ModuleStorageDrive(drive.Number, drive.Model is { Length: > 0 } model ? model : null, drive.SolidState,
            Array.AsReadOnly(drive.Volumes.Select(volume => new ModuleStorageVolume(volume.Letter, volume.Label, volume.Total, volume.Free)).ToArray()))
        {
            Temperature = drive.Temperature is { } heat && Celsius(heat.Celsius) is { } celsius ? ModuleReading<double>.Available(celsius) : ModuleReading<double>.Missing("no-sensor")
        }).ToArray());
    private static ModuleReading<T> Reading<T, TSample>(Core.Reading<TSample> source, Func<TSample, T?> value, DateTimeOffset observed) where T : struct
        => source.TryGet(out var sample) && value(sample) is { } read ? ModuleReading<T>.Available(read, observed) : ModuleReading<T>.Missing(Reason(source), observed);
    private static ModuleReading<double> Optional<TSample>(Core.Reading<TSample> source, Func<TSample, double?> value, DateTimeOffset observed, string missing)
        => source.TryGet(out var sample) && value(sample) is { } read ? ModuleReading<double>.Available(read, observed) : ModuleReading<double>.Missing(source.HasValue ? missing : Reason(source), observed);
    private static ModuleReading<T> Optional<T, TSample>(Core.Reading<TSample> source, Func<TSample, T?> value, DateTimeOffset observed, string missing) where T : struct
        => source.TryGet(out var sample) && value(sample) is { } read ? ModuleReading<T>.Available(read, observed) : ModuleReading<T>.Missing(source.HasValue ? missing : Reason(source), observed);
    private static string? Reason<T>(Core.Reading<T> source) => source.HasValue ? null : source.Reason is { Length: > 0 } reason ? reason : null;
    private static double? Celsius(double? value) => value is { } celsius && double.IsFinite(celsius) && celsius is > -100 and < 200 ? celsius : null;
    private static double? PartWatts(Core.PowerTrack track) => track.History.Count > 0 && double.IsFinite(track.History[^1]) && track.History[^1] >= 0 ? track.History[^1] : null;
    private static double? Percent(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : null;
    private static double? UsedPercent(UsageMetric metric)
    { try { return metric.UsedPercent is { } value && double.IsFinite(value) ? value : null; } catch (OverflowException) { return null; } }
    private static double? Rate(double value) => double.IsFinite(value) && value >= 0 ? value : null;
    private static long Add(long result, long value) => value <= 0 ? result : result > long.MaxValue - value ? long.MaxValue : result + value;
}
