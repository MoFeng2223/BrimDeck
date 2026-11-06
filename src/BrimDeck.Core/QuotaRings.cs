namespace BrimDeck.Core;

// One collapsed ring per reported quota: Used is the share the ring fills (0 to 100); Mark is the short time-window label in
// its lower-right opening, empty when labelling is off or the source gave no window; Tip is what a screen reader and the
// tooltip show.
public sealed record QuotaRingData(double Used, string Tip, string Mark = "");

public static class CompactQuotaRings
{
    public const int FiveHours = 300, Weekly = 10080, Day = 1440;

    // Every allowance a source reports gets its own ring, side by side, in time-window order: five hours, seven days, then
    // longer or custom ones. A source with no 5-hour limit simply has no 5h ring; an extra plan window is matched like any
    // other, so no quota is dropped. A window the source did not state gets no time label but keeps its full name in the
    // tooltip. remaining fills the ring with what is left instead of what has been used.
    public static IReadOnlyList<QuotaRingData> Build(string name, ProviderSnapshot snapshot, bool remaining = false, bool marks = true)
    {
        var readings = snapshot.Metrics.Where(metric => metric.UsedPercent is not null)
            .OrderBy(metric => metric.Window ?? int.MaxValue)
            .ThenBy(metric => metric.Label, StringComparer.Ordinal)
            .ToArray();
        if (readings.Length == 0)
        {
            // A source with metrics but no percentage still owns a ring, so its state is visible rather than silently absent.
            string status = snapshot.Metrics.Count > 0
                ? string.Join(Loc.T("；", "; "), snapshot.Metrics.Select(metric =>
                    metric.ValueText.Length > 0 ? metric.ValueText + " " + metric.ValueSuffix : Loc.Label(metric.Label)))
                : snapshot.StatusLabel;
            return [new(Shown(100, remaining), name + " · " + status)];
        }
        return readings.Select(metric =>
        {
            double used = Math.Clamp(metric.UsedPercent!.Value, 0, 100);
            string tip = name + " · " + Loc.Label(metric.Label) + " " + Percent(Shown(used, remaining), remaining)
                + (metric.Unlimited ? Loc.T(" · 无上限", " · no limit") : "");
            return new QuotaRingData(Shown(used, remaining), tip, marks ? WindowMark(metric.Window) : "");
        }).ToArray();
    }

    private static double Shown(double used, bool remaining) => remaining ? 100 - used : used;

    // Short, language-independent labels for the ring's opening: 5h for five hours, 7D for the seven-day week, and the same
    // idea for any other window a source reports (30D, 12h, 45m). Longer labels draw smaller (QuotaCarousel.Ring). The tooltip
    // always carries the source's full label.
    private static string WindowMark(int? window)
    {
        if (window is not > 0) return "";
        int minutes = window.Value;
        if (minutes == FiveHours) return "5h";
        if (minutes == Weekly) return "7D";
        if (minutes >= Day) return Math.Max(1, (int)Math.Round(minutes / (double)Day)) + "D";
        if (minutes >= 60) return Math.Max(1, (int)Math.Round(minutes / 60.0)) + "h";
        return minutes + "m";
    }

    private static string Percent(double share, bool remaining) =>
        remaining ? Loc.T($"剩余 {share:0.#}%", $"{share:0.#}% left") : Loc.T($"已用 {share:0.#}%", $"{share:0.#}% used");
}
