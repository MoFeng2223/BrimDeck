using BrimDeck.Core;

internal static class ModelUsageTests
{
    internal static void Run(Action<string, bool> check)
    {
        var today = DateTime.Today;
        TokenEntry Entry(DateTime date, long input, decimal? cost = null) => new("same-key", new DateTimeOffset(date), "same-model", input, 20, 3, 7, 40, cost);
        var first = new ProviderSnapshot(ProviderId.Claude) { UsageAvailable = true, Entries = [Entry(today, 100, 1m), Entry(today.AddDays(1).AddTicks(-1), 200, 2m), Entry(today.AddDays(-1), 300), Entry(today.AddDays(1), 400)] };
        var second = new ProviderSnapshot(ProviderId.Cursor) { UsageAvailable = true, Entries = [Entry(today.AddHours(8), 500, 9m)] };
        var range = new UsageDateRange(today, today);
        var result = ModelUsage.Filter([first, second], new HashSet<ProviderId> { ProviderId.Claude, ProviderId.Cursor }, range);
        check("Matching model names remain separate by application", result.Rows.Count == 2 && result.Rows.Single(row => row.Provider == ProviderId.Claude).Entries.Count == 2);
        check("Date range includes both midnight and the end of the last day", result.Entries.Count == 3 && result.Entries.Sum(t => t.Input) == 800);
        check("Totals include every disjoint token category exactly once", result.Entries.Sum(t => t.Total) == 1010 && result.Entries.Sum(t => t.CacheWrite + t.CacheWriteHour) == 30);
        check("Matching message IDs across apps retain their own reported costs", result.Entries.Sum(t => t.ReportedCostUsd) == 12m);
        var single = ModelUsage.Filter([first, second], new HashSet<ProviderId> { ProviderId.Cursor }, range);
        check("Application filtering also filters totals", single.Rows.Count == 1 && single.Entries.Sum(t => t.Total) == 570);
        check("Seven days use local calendar dates", UsageDateRange.Recent(7, today).Start == today.AddDays(-6));
        bool invalid = false;
        try { _ = new UsageDateRange(today, today.AddDays(-1)); } catch (ArgumentException) { invalid = true; }
        check("An inverted date range is rejected", invalid);
        var missing = new ProviderSnapshot(ProviderId.Codex);
        result = ModelUsage.Filter([missing], new HashSet<ProviderId> { ProviderId.Codex }, range);
        check("Unavailable history is distinct from an empty day", result.Entries.Count == 0 && result.IncompleteProviders.Contains(ProviderId.Codex));
        first.UsageStart = today;
        result = ModelUsage.Filter([first], new HashSet<ProviderId> { ProviderId.Claude }, UsageDateRange.Recent(30, today));
        check("Not-yet-loaded dates mark the total as incomplete", result.IncompleteProviders.Contains(ProviderId.Claude));
        first.UsageStart = today.AddDays(-30); first.UsageComplete = false;
        check("A partially read source is marked incomplete", ModelUsage.Filter([first], new HashSet<ProviderId> { ProviderId.Claude }, range).IncompleteProviders.Count == 1);
    }
}
