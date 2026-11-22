using BrimDeck.Core;

internal static class UsageTrendTests
{
    internal static void Run(Action<string, bool> check)
    {
        var today = new DateTime(2026, 11, 17);
        var range = new UsageDateRange(today.AddDays(-2), today);
        TokenEntry Entry(DateTime day, long tokens, decimal? cost = null) =>
            new("k", new DateTimeOffset(day.AddHours(12)), "model", tokens, 0, 0, 0, 0, cost);

        var entries = new[]
        {
            Entry(today.AddDays(-2), 100, 1.5m),
            Entry(today.AddDays(-2), 50, 0.5m),
            Entry(today, 200, 2m),
            // Outside the range — ignored.
            Entry(today.AddDays(-5), 999, 9m),
        };
        var days = UsageTrend.Days(entries, range, list => list.Sum(e => e.ReportedCostUsd ?? 0));
        check("Usage trend keeps every local day in the range, zeros included",
            days.Count == 3
            && days[0] is { Start: var d0, Tokens: 150, Cost: 2.0m } && d0 == today.AddDays(-2)
            && days[1] is { Start: var d1, Tokens: 0, Cost: 0m } && d1 == today.AddDays(-1)
            && days[2] is { Start: var d2, Tokens: 200, Cost: 2.0m } && d2 == today);

        check("Usage trend token totals match summing the filtered entries",
            days.Sum(d => d.Tokens) == entries.Where(e => range.Contains(e.Time)).Sum(e => e.Total));

        var week = UsageDateRange.Recent(7, today);
        var weekDays = UsageTrend.Days([], week);
        check("Usage trend fills a seven-day range with empty days when there are no records",
            weekDays.Count == 7 && weekDays.All(d => d.Tokens == 0 && d.Cost == 0) && weekDays[0].Start == today.AddDays(-6) && weekDays[^1].Start == today);

        var single = UsageDateRange.Recent(1, today);
        check("Usage trend for today alone is one day",
            UsageTrend.Days([Entry(today, 10)], single).Single() is { Tokens: 10 } &&
            UsageTrend.Days([Entry(today.AddDays(-1), 10)], single).Single() is { Tokens: 0 });

        var priced = new CostSummary(1.25m, 1, 0, 0);
        var unpriced = new CostSummary(0, 0, 0, 2);
        var mixed = new CostSummary(0.80m, 1, 0, 1);
        var marked = UsageTrend.Days(
            [Entry(today.AddDays(-2), 10), Entry(today.AddDays(-1), 20), Entry(today, 30)],
            range,
            list => list.Count == 0 ? new CostSummary(0, 0, 0, 0)
                : list[0].Time.Date == today.AddDays(-2) ? priced
                : list[0].Time.Date == today.AddDays(-1) ? unpriced
                : mixed);
        check("Usage trend keeps unpriced counts so a day is not drawn as a confirmed zero",
            marked[0] is { Cost: 1.25m, Unpriced: 0 } && marked[1] is { Cost: 0, Unpriced: 2 } && marked[2] is { Cost: 0.80m, Unpriced: 1 }
            && marked[1].Tokens == 20);

        var peakPriced = UsageTrend.Peak([new(today, 10, 1.25m), new(today.AddDays(1), 0, 0m)]);
        check("Usage trend peak is the highest known cost when every record is priced",
            peakPriced is { Tokens: 10, Cost: 1.25m, Unpriced: 0 });

        var peakUnknown = UsageTrend.Peak([new(today, 20, 0m, 3), new(today.AddDays(1), 5, 0m, 1)]);
        check("Usage trend peak is unknown when every record is unpriced",
            peakUnknown is { Tokens: 20, Cost: 0m, Unpriced: 4 });

        var peakKnown = UsageTrend.Peak([new(today, 10, 1.25m), new(today.AddDays(1), 30, 0.80m, 2)]);
        check("Usage trend peak of mixed days is the highest known cost and still counts the unpriced records",
            peakKnown is { Tokens: 30, Cost: 1.25m, Unpriced: 2 });

        bool Fits(int days, double width)
        {
            var (gap, bar) = UsageTrend.Layout(days, width);
            if (days == 0) return bar == 0;
            double last = UsageTrend.GraphPad + (days - 1) * (bar + gap);
            return bar > 0 && last + bar <= width - UsageTrend.GraphPad + 1e-9;
        }
        check("Usage trend layout keeps a year of days in view at 600 DIP", Fits(365, 600));
        check("Usage trend layout keeps a year of days in view at 300 DIP", Fits(365, 300) && UsageTrend.Layout(365, 300).Bar < 1);
        var month = UsageTrend.Layout(30, 600);
        check("Usage trend layout keeps a 30-day range spaced at a typical width",
            Fits(30, 600) && month.Bar >= 1 && month.Gap > 0);

        // A label 26 DIP wide needs 50 DIP from one to the next.
        check("Usage trend labels every day when each day has room for one", UsageTrend.LabelStep(60, 26) == 1);
        check("Usage trend labels every other day when two days have room", UsageTrend.LabelStep(30, 26) == 2);
        check("Usage trend labels a 30-day range weekly at a typical width", UsageTrend.LabelStep(18.6, 26) == 7);
        check("Usage trend labels a year in whole four-week steps far enough apart",
            UsageTrend.LabelStep(1.5, 26) is var year && year % 28 == 0 && year * 1.5 >= 50 && (year - 28) * 1.5 < 50);

        var claude = UsageTrend.Days([Entry(today.AddDays(-2), 100, 1m), Entry(today, 30, 0.5m)], range, list => list.Sum(e => e.ReportedCostUsd ?? 0));
        var codex = UsageTrend.Days([Entry(today, 70, 0.25m)], range, list => list.Sum(e => e.ReportedCostUsd ?? 0));
        var stacked = UsageTrend.Sum([claude, codex]);
        check("Usage trend adds the applications' days together day by day",
            stacked.Count == 3 && stacked[0] is { Tokens: 100, Cost: 1m } && stacked[1] is { Tokens: 0, Cost: 0m } && stacked[2] is { Tokens: 100, Cost: 0.75m }
            && stacked[2].Start == today && UsageTrend.Sum([]).Count == 0);

        CostSummary Zero(IReadOnlyList<TokenEntry> _) => new(0, 0, 0, 0);
        var earlier = UsageTrend.Points([Entry(today.AddDays(-2), 40), Entry(today.AddDays(-1).AddHours(-12).AddMinutes(30), 5)], range, true, Zero, today.AddDays(5));
        check("Usage trend by hour covers every hour of a past range and puts a record in its hour",
            earlier.Count == 72 && earlier[0].Start == today.AddDays(-2) && earlier[^1].Start == today.AddHours(23)
            && earlier[12] is { Tokens: 40 } && earlier[24] is { Tokens: 5 } && earlier.Sum(p => p.Tokens) == 45);
        var now = today.AddHours(14).AddMinutes(20);
        var hours = UsageTrend.Points([Entry(today, 7)], single, true, Zero, now);
        check("Usage trend by hour stops at the current hour of today",
            hours.Count == 15 && hours[^1].Start == today.AddHours(14) && hours[12] is { Tokens: 7 });
        check("Usage trend labels hours at steps that divide a day, and a long range in whole days",
            UsageTrend.LabelStep(30, 26, true) == 2 && UsageTrend.LabelStep(12, 26, true) == 6 && UsageTrend.LabelStep(1, 26, true) == 168
            && UsageTrend.LabelStep(0.05, 26, true) % 672 == 0);
    }
}
