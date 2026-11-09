namespace BrimDeck.Core;

// One application's collapsed quota ring. Outer is the share the ring fills; Inner, when not null, the share of a second ring
// inside it. Detail is what the tooltip says after the application's name.
public sealed record QuotaRing(double Outer, double? Inner, string Detail)
{
    // Every ring's diameter at scale 1, one ring or two: one ring is as large as the outer ring of a pair, and the inner ring of a
    // pair keeps the same stroke and an 8 DIP middle for the letter.
    public const double Diameter = 20;

    // The outer ring holds the quotas of the shortest window the source states (five hours for Claude, Codex and Antigravity),
    // the inner ring every other quota (the weekly ones, a monthly one beside a daily one); each shows its highest share. A source
    // with one window, or with none it states, keeps one ring with the highest share, as before. Without any share the ring is
    // full while the source reports something (a balance) and empty while it reports nothing.
    public static QuotaRing For(ProviderSnapshot snapshot)
    {
        var shares = snapshot.Metrics.Where(metric => metric.UsedPercent is { } used && double.IsFinite(used))
            .OrderBy(metric => metric.Window is > 0 ? metric.Window.Value : int.MaxValue).ToArray();
        if (shares.Length == 0)
            return new(snapshot.Metrics.Count > 0 ? 100 : 0, null, snapshot.Metrics.Count > 0
                ? string.Join(Loc.T("；", "; "), snapshot.Metrics.Select(metric => metric.ValueText.Length > 0 ? metric.ValueText + " " + metric.ValueSuffix : Loc.Label(metric.Label)))
                : snapshot.StatusLabel);
        static double Used(UsageMetric metric) => Math.Clamp(metric.UsedPercent!.Value, 0, 100);
        static string Share(double used) => Loc.T($"已用 {used:0.#}%", $"{used:0.#}% used");
        string detail = shares.Length == 1 ? Share(Used(shares[0]))
            : string.Join(Loc.T("；", "; "), shares.Select(metric => Loc.Label(metric.Label) + " " + Share(Used(metric))));
        int shortest = shares[0].Window is > 0 ? shares[0].Window!.Value : 0;
        var inner = shortest == 0 ? [] : shares.Where(metric => metric.Window != shortest).ToArray();
        if (inner.Length == 0) return new(shares.Max(Used), null, detail);
        return new(shares.Where(metric => metric.Window == shortest).Max(Used), inner.Max(Used), detail);
    }
}
