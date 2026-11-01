using System.Globalization;
using BrimDeck.Modules;

namespace BrimDeck.UsageAlerts;

public sealed record AlertMark(string Period, bool Fired);
public sealed class AlertState
{
    public Dictionary<string, AlertMark> Marks { get; init; } = [];
}

// No desktop types, filesystem access, account credentials or provider-specific parsers.
public sealed class ThresholdGate(AlertState state)
{
    public bool Evaluate(ModuleUsageSnapshot snapshot, double threshold, DateTimeOffset now, Func<ModuleNotice, ModuleNoticeResult> show)
    {
        if (!double.IsFinite(threshold) || threshold is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(threshold));
        bool changed = false;
        foreach (var entry in snapshot.Entries.Where(e => e.ProviderId == "codex" && e.Status == ModuleDataStatus.Available))
        foreach (var metric in entry.Metrics)
        {
            if (metric.Unlimited || metric.UsedPercent is not { } percent || !double.IsFinite(percent) || percent < 0
                || metric.ResetsAt <= now || metric.ExpiresAt <= now) continue;
            string key = entry.ProviderId + "/" + metric.Id + "/" + threshold.ToString(CultureInfo.InvariantCulture);
            string period = metric.ResetsAt?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "unbounded";
            var mark = state.Marks.GetValueOrDefault(key);
            bool fired = mark?.Period == period && mark.Fired;
            // A known quota period alerts once. Without a reset timestamp, a 2-point recovery rearms it.
            if (fired && metric.ResetsAt is null && percent <= threshold - 2) { state.Marks[key] = new(period, false); changed = true; continue; }
            if (percent < threshold || fired) continue;
            var result = show(new("codex-threshold", "Codex 用量提醒", $"{metric.Label} 已用 {percent:0.#}%")
                { Tone = ModuleNoticeTone.Warning, ProgressPercent = percent, DurationMilliseconds = 3000 });
            if (result is not (ModuleNoticeResult.Shown or ModuleNoticeResult.Queued)) continue;
            state.Marks.Remove(key); state.Marks[key] = new(period, true); changed = true;
            while (state.Marks.Count > 64) state.Marks.Remove(state.Marks.Keys.First());
        }
        return changed;
    }
}
