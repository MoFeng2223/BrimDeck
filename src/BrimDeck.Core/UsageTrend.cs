namespace BrimDeck.Core;

// One bar of a usage trend: a local calendar day, or one hour of it, from Start. Tokens and priced cost in it.
// Unpriced is how many records in it could not be matched to a price; Cost is the known amount only.
public sealed record UsageTrendPoint(DateTime Start, long Tokens, decimal Cost, int Unpriced = 0);

// Buckets TokenEntry records into one point per day, or per hour, of a UsageDateRange (zeros where there are no records).
public static class UsageTrend
{
    public static IReadOnlyList<UsageTrendPoint> Days(IEnumerable<TokenEntry> entries, UsageDateRange range,
        Func<IReadOnlyList<TokenEntry>, decimal>? costOf = null)
        => Points(entries, range, false, list => new CostSummary(costOf is null ? 0 : costOf(list), 0, 0, 0));

    public static IReadOnlyList<UsageTrendPoint> Days(IEnumerable<TokenEntry> entries, UsageDateRange range,
        Func<IReadOnlyList<TokenEntry>, CostSummary> costOf) => Points(entries, range, false, costOf);

    // By hour the points run from midnight of the first day to the last hour of the last day, or to the current hour when the last
    // day is today (now), so the hours still to come are not drawn as hours without use.
    public static IReadOnlyList<UsageTrendPoint> Points(IEnumerable<TokenEntry> entries, UsageDateRange range, bool hourly,
        Func<IReadOnlyList<TokenEntry>, CostSummary> costOf, DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        DateTime Bucket(DateTime time) => hourly ? time.Date.AddHours(time.Hour) : time.Date;
        var last = !hourly ? range.End : range.End == current.Date ? Bucket(current) : range.End.AddHours(23);
        var byStart = new Dictionary<DateTime, List<TokenEntry>>();
        foreach (var entry in entries)
        {
            var local = entry.Time.LocalDateTime;
            if (local.Date < range.Start || local.Date > range.End) continue;
            var start = Bucket(local);
            if (start > last) continue;
            if (!byStart.TryGetValue(start, out var list)) byStart[start] = list = [];
            list.Add(entry);
        }
        var result = new List<UsageTrendPoint>();
        for (var start = range.Start; start <= last; start = hourly ? start.AddHours(1) : start.AddDays(1))
        {
            if (!byStart.TryGetValue(start, out var list)) list = [];
            long tokens = 0;
            foreach (var entry in list) tokens += entry.Total;
            // Most hours of a long range are empty; they cost nothing and need no pricing.
            var cost = list.Count == 0 ? new CostSummary(0, 0, 0, 0) : costOf(list);
            result.Add(new(start, tokens, cost.Amount, cost.Unpriced));
        }
        return result;
    }

    // Several series of the same points (one per application) added together point by point.
    public static IReadOnlyList<UsageTrendPoint> Sum(IReadOnlyList<IReadOnlyList<UsageTrendPoint>> series)
    {
        if (series.Count == 0) return [];
        var result = new List<UsageTrendPoint>(series[0].Count);
        for (int i = 0; i < series[0].Count; i++)
        {
            long tokens = 0;
            decimal cost = 0;
            int unpriced = 0;
            foreach (var days in series) { tokens += days[i].Tokens; cost += days[i].Cost; unpriced += days[i].Unpriced; }
            result.Add(new(series[0][i].Start, tokens, cost, unpriced));
        }
        return result;
    }

    // Tokens is the highest total of a point; Cost is the highest known amount only. Unpriced is how many
    // records in the range could not be priced, so a cost peak may be incomplete or unknown.
    public static (long Tokens, decimal Cost, int Unpriced) Peak(IReadOnlyList<UsageTrendPoint> days)
    {
        long tokens = 0;
        decimal cost = 0;
        int unpriced = 0;
        foreach (var day in days)
        {
            if (day.Tokens > tokens) tokens = day.Tokens;
            if (day.Cost > cost) cost = day.Cost;
            unpriced += day.Unpriced;
        }
        return (tokens, cost, unpriced);
    }

    public const double GraphPad = 2;

    // Gap shrinks first so a 1 DIP bar still fits; if even that overflows, bars go below 1 DIP so the last day stays in view.
    public static (double Gap, double Bar) Layout(int days, double width)
    {
        if (days <= 0 || width <= 0) return (0, 0);
        double available = Math.Max(0, width - GraphPad * 2);
        if (days == 1) return (0, available);
        double gap = Math.Min(6, available / days * .18);
        double bar = (available - gap * (days - 1)) / days;
        if (bar < 1)
        {
            gap = Math.Max(0, (available - days) / (days - 1));
            bar = (available - gap * (days - 1)) / days;
        }
        return (gap, bar);
    }

    // The points from one label to the next, the fewest that leave each label clear of the next. By day: every day, every other day,
    // weekly, fortnightly, or a whole number of four weeks. By hour: 1, 2, 3, 6 or 12 hours, a day, two days, a week, a fortnight,
    // or a whole number of four weeks. pitch is a bar and its gap; spacing is kept between labels.
    public static int LabelStep(double pitch, double labelWidth, bool hourly = false, double spacing = 24)
    {
        if (pitch <= 0) return int.MaxValue;
        double need = (labelWidth + spacing) / pitch;
        int[] steps = hourly ? [1, 2, 3, 6, 12, 24, 48, 168, 336] : [1, 2, 7, 14];
        foreach (int step in steps) if (step >= need) return step;
        int month = hourly ? 672 : 28;
        return (int)Math.Ceiling(need / month) * month;
    }
}
