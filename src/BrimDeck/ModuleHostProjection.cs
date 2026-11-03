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
                { PlaybackRate = double.IsFinite(track.Rate) && track.Rate >= 0 ? track.Rate : 1 };
    internal static ModuleSystemSnapshot System(long revision, MonitorSnapshot snapshot)
    {
        double? cpu = snapshot.Cpu.TryGet(out var processor) && processor.Cores.Count > 0 ? Percent(processor.Usage * 100) : null;
        bool memory = snapshot.Memory.TryGet(out var ram), network = snapshot.Network.TryGet(out var traffic), graphics = snapshot.Gpu.TryGet(out var gpu);
        double? mem = memory && ram.Total > 0 ? Percent(ram.UsedFraction * 100) : null;
        double? gpuPercent = graphics ? Percent(gpu.Usage * 100) : null;
        double? down = network ? Rate(traffic.DownBytesPerSecond) : null, up = network ? Rate(traffic.UpBytesPerSecond) : null;
        return new(revision, snapshot.Time.ToUniversalTime(), cpu is not null || mem is not null || down is not null || up is not null || gpuPercent is not null ? ModuleDataStatus.Available : ModuleDataStatus.Unavailable,
            cpu, mem, mem is not null ? ram.Used : null, mem is not null ? ram.Total : null, gpuPercent, down, up);
    }
    private static double? Percent(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : null;
    private static double? UsedPercent(UsageMetric metric)
    { try { return metric.UsedPercent is { } value && double.IsFinite(value) ? value : null; } catch (OverflowException) { return null; } }
    private static double? Rate(double value) => double.IsFinite(value) && value >= 0 ? value : null;
    private static long Add(long result, long value) => value <= 0 ? result : result > long.MaxValue - value ? long.MaxValue : result + value;
}
