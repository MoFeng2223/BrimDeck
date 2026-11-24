namespace BrimDeck.Core;

// One application's collapsed quota ring. Shares: what each ring fills, outside in, 0 to 100 (one share for one ring); a null
// share is a quota chosen for the ring that has no share now (the source does not report it, or a balance or an amount spent has
// no target yet), drawn as the bare track; NaN is a place left out on purpose (AppEntry.RingQuotas), drawn as nothing, so the
// rings inside it keep their places. Detail is what the tooltip says after the application's name.
public sealed record QuotaRing(IReadOnlyList<double?> Shares, string Detail)
{
    // The nested style shows at most this many rings, one inside the other.
    public const int MaximumRings = 3;
    // Nested is the drawing reference diameter: nested quotas match the 16 DIP system dials as height grows, with a 16 DIP floor.
    // One, two or three shares keep the same size so data never changes the row width. Single keeps its 12 DIP base diameter.
    public const double Nested = 20, Single = 12;
    public const double NestedMinimum = 16;
    public static double Diameter(QuotaRingStyle style) => style == QuotaRingStyle.Single ? Single : Nested;

    // The default nested rings, drawn as used shares.
    public static QuotaRing For(ProviderSnapshot snapshot) => For(snapshot, null, QuotaRingStyle.Nested, false);

    // entry: the application's own choice of quotas (AppEntry.SingleRingQuota, AppEntry.RingQuotas) and targets (AppEntry.QuotaTargets),
    // null for the defaults. remaining: each ring fills with what is left (100 less the used share) instead of what is used.
    //
    // A ring's share (Used): the share the source gives, or for a balance or an amount spent with a target of its own, the part of
    // the target used (a balance of 20 against 100 is 80% used; 30 spent against 100 is 30% used). The account balance takes the
    // row's target balance (AppEntry.BalanceTarget), which the snapshot already carries as its total (DashboardUsage.Quota).
    //
    // Single: the chosen quota while the source reports it; else the account balance where the source has one, else the highest
    // used share, as the one ring was drawn before there were nested rings.
    // Nested, chosen quotas: one ring per chosen quota, outside in; a chosen quota the source does not report now keeps its ring,
    // empty, so the rings do not move. While the source reports none of them (another source, a quota renamed), the default rings
    // show instead.
    // Nested, by default: the first three quotas in their default order (Ordered), one per ring, outside in: for GLM five hours,
    // the week and the tool calls; for Claude five hours, the week and the first model's week (Fable); for Kimi and OpenCode five
    // hours, the week and the month; for New API the account balance, the key's balance and today's spending. Each ring keeps its
    // own quota, whichever is used more. A quota the source does not report lets the ones after it move out (GLM without a weekly
    // quota: five hours, then the tool calls).
    //
    // While no ring has a share, one ring shows: full while the source reports something (a balance with no target) and empty
    // while it reports nothing.
    public static QuotaRing For(ProviderSnapshot snapshot, AppEntry? entry, QuotaRingStyle style, bool remaining)
    {
        var choices = Choices(snapshot);
        double? Shown(UsageMetric metric) => Used(metric, entry) is { } used ? remaining ? 100 - used : used : null;
        string Share(double shown) => remaining ? Loc.T($"剩余 {shown:0.#}%", $"{shown:0.#}% left") : Loc.T($"已用 {shown:0.#}%", $"{shown:0.#}% used");
        string Text(UsageMetric metric) => metric.ValueText.Length > 0 && metric.ValueText != "—" ? metric.ValueText + " " + metric.ValueSuffix : Loc.Label(metric.Label);
        // The tooltip: every quota with a share, and every balance or amount spent, with its figure; a lone share needs no name.
        var listed = snapshot.Metrics.Where(metric => Shown(metric) is not null || metric.Kind is MetricKind.Balance or MetricKind.Spend && metric.Amount is not null).ToArray();
        string detail = snapshot.Metrics.Count == 0 ? snapshot.StatusLabel
            : listed.Length == 0 ? string.Join(Loc.T("；", "; "), snapshot.Metrics.Select(Text))
            : listed.Length == 1 && Shown(listed[0]) is { } lone ? Share(lone)
            : string.Join(Loc.T("；", "; "), listed.Select(metric => Shown(metric) is { } shown ? Loc.Label(metric.Label) + " " + Share(shown) : Text(metric)));
        QuotaRing Rings(IEnumerable<UsageMetric?> metrics)
        {
            var shares = metrics.Take(MaximumRings).Select(metric => metric is null ? null : Shown(metric)).ToList();
            return shares.Any(share => share is { } value && !double.IsNaN(value)) ? new(shares, detail) : new([snapshot.Metrics.Count > 0 ? 100 : 0], detail);
        }
        UsageMetric? Find(string id) => choices.FirstOrDefault(metric => metric.Id == id);

        if (style == QuotaRingStyle.Single)
            return Rings([PrimaryMetric(choices, entry)]);

        if (entry?.RingQuotas is { Count: > 0 } picked && picked.Any(id => Find(id) is not null))
        {
            // A place left out draws nothing, while a chosen quota without a share keeps its track (Shares).
            var shares = picked.Take(MaximumRings).Select(id => id.Length == 0 ? double.NaN : Find(id) is { } metric ? Shown(metric) : null).ToList();
            return new(shares, detail);
        }
        return Rings(choices);
    }

    // The nested rings' choice by place (AppEntry.RingQuotas): at most three, an empty string for a place left out, a quota that
    // shows twice left out the second time, places left out at the end dropped; nothing chosen at all is the default rings ([]).
    public static List<string> Slots(IEnumerable<string>? quotas)
    {
        var slots = new List<string>();
        foreach (var quota in (quotas ?? []).Take(MaximumRings))
        {
            var id = quota?.Trim() ?? "";
            slots.Add(id.Length > 500 || slots.Contains(id) ? "" : id);
        }
        while (slots.Count > 0 && slots[^1].Length == 0) slots.RemoveAt(slots.Count - 1);
        return slots;
    }

    // A quota's used share for the rings, 0 to 100, or null without one: a balance or an amount spent with a target of its own
    // (AppEntry.QuotaTargets) is measured against it; anything else has the share its source gives.
    public static double? Used(UsageMetric metric, AppEntry? entry)
    {
        if (TakesTarget(metric) && metric.Amount is { } amount && entry?.QuotaTargets is { } targets && targets.TryGetValue(metric.Id, out var target) && target > 0)
            return Math.Clamp((double)((metric.Kind == MetricKind.Balance ? target - amount : amount) / target * 100), 0, 100);
        return metric.UsedPercent is { } used && double.IsFinite(used) ? Math.Clamp(used, 0, 100) : null;
    }

    // A balance or an amount spent can be given a target of its own for the rings (AppEntry.QuotaTargets). The account balance is
    // not: its target is the row's target balance (AppEntry.BalanceTarget), which the panel and the notices use too.
    public static bool TakesTarget(UsageMetric metric) => metric.Kind is MetricKind.Balance or MetricKind.Spend && metric.Amount is not null && !IsAccountBalance(metric);

    // Picks the primary metric for an application's single ring and compact detail line.
    public static UsageMetric? PrimaryMetric(ProviderSnapshot snapshot, AppEntry? entry) => PrimaryMetric(Choices(snapshot), entry);

    private static UsageMetric? PrimaryMetric(IReadOnlyList<UsageMetric> choices, AppEntry? entry)
    {
        if (entry?.SingleRingQuota is { Length: > 0 } single && choices.FirstOrDefault(metric => metric.Id == single) is { } chosen) return chosen;
        if (entry?.CompactQuotas is { Count: 1 } compact && choices.FirstOrDefault(metric => metric.Id == compact[0]) is { } onlyCompact) return onlyCompact;
        if (choices.FirstOrDefault(IsAccountBalance) is { } account) return account;
        return choices.Where(metric => Used(metric, entry) is not null).MaxBy(metric => Used(metric, entry));
    }

    // Formats the primary metric's percentage for the collapsed row (used or remaining, matching QuotaRingRemaining).
    public static string PrimaryFigure(ProviderSnapshot snapshot, AppEntry? entry, bool remaining)
        => MetricFigure(PrimaryMetric(snapshot, entry), entry, remaining);

    // Formats a specific metric's percentage for the collapsed row (used or remaining, matching QuotaRingRemaining).
    public static string MetricFigure(UsageMetric? metric, AppEntry? entry, bool remaining)
    {
        if (metric is null || Used(metric, entry) is not { } used) return "";
        double shown = remaining ? 100 - used : used;
        return $"{shown:0}%";
    }

    // Selects which metrics to render in the collapsed detail strip: all available, per-app selected, or the primary metric.
    public static IReadOnlyList<UsageMetric> DetailMetrics(ProviderSnapshot snapshot, AppEntry? entry, bool showAll)
    {
        var choices = DetailChoices(snapshot);
        if (choices.Count == 0) return [];
        if (showAll) return choices;
        if (entry?.CompactQuotas is { Count: > 0 } picked)
        {
            var matched = choices.Where(metric => picked.Contains(metric.Id, StringComparer.Ordinal)).ToList();
            if (matched.Count > 0) return matched;
        }
        return PrimaryMetric(Choices(snapshot), entry) is { } primary ? [primary] : [choices[0]];
    }

    // Formats a compact reset countdown for the collapsed row; returns empty when there is no reset time.
    public static string CompactReset(DateTimeOffset? date, DateTimeOffset now)
    {
        if (date is null) return "";
        var remaining = date.Value - now;
        if (remaining <= TimeSpan.Zero) return Loc.T("待重置", "Reset due");
        if (remaining.TotalDays >= 1) return Loc.T($"{(int)remaining.TotalDays}天{remaining.Hours}时", $"{(int)remaining.TotalDays}d {remaining.Hours}h");
        if (remaining.TotalHours >= 1) return Loc.T($"{remaining.Hours}时{remaining.Minutes}分", $"{remaining.Hours}h {remaining.Minutes}m");
        int minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return Loc.T($"{minutes}分", $"{minutes}m");
    }

    // Formats a compact label for a metric (e.g. 5h, 周/1w, Gemini 5h) for the collapsed row.
    // Set omitGroup to drop the group prefix when consecutive metrics belong to the same group.
    public static string CompactMetricLabel(UsageMetric? metric, bool omitGroup = false)
    {
        if (metric is null) return "";
        string group = omitGroup ? "" : Loc.Label(GroupName(metric.Label));
        string rawSuffix = (metric.Label.Contains('·') ? metric.Label.Split('·')[^1] : metric.Label).Trim();
        string normalizedPeriod = rawSuffix.Replace("额度", "", StringComparison.Ordinal).Trim();
        static bool IsGenericPeriod(string text) => text.ToLowerInvariant() switch
        {
            "" or "5 小时" or "5小时" or "5h" or "每日" or "每天" or "daily" or "1d" or "每周" or "weekly" or "1w" or "7d" or "双周" or "每月" or "monthly" or "1m" or "30d" => true,
            _ => false
        };
        if (metric.Window is > 0 and var minutes && IsGenericPeriod(normalizedPeriod))
        {
            const int week = 7 * 24 * 60;
            string span = minutes == week ? Loc.T("周", "1w")
                : minutes % week == 0 ? Loc.T($"{minutes / week}周", $"{minutes / week}w")
                : minutes % (24 * 60) == 0 ? $"{minutes / (24 * 60)}d"
                : minutes % 60 == 0 ? $"{minutes / 60}h"
                : $"{minutes}m";
            return group.Length > 0 ? $"{group} {span}" : span;
        }
        string localized = Loc.Label(rawSuffix);
        return group.Length > 0 && localized.Length > 0 ? $"{group} {localized}" : localized;
    }

    // Joins the enabled detail parts for one application's collapsed quota item.
    public static string DetailText(string? name, string? label, string? figure, string? reset, bool showName, bool showLabel, bool showFigure, bool showReset)
    {
        var parts = new List<string>(4);
        if (showName && !string.IsNullOrWhiteSpace(name)) parts.Add(name.Trim());
        if (showLabel && !string.IsNullOrWhiteSpace(label)) parts.Add(label.Trim());
        if (showFigure && !string.IsNullOrWhiteSpace(figure)) parts.Add(figure.Trim());
        if (showReset && !string.IsNullOrWhiteSpace(reset)) parts.Add(reset.Trim());
        return string.Join(" ", parts);
    }

    public static string DetailText(string? name, string? figure, string? reset, bool showName, bool showFigure, bool showReset)
        => DetailText(name, null, figure, reset, showName, false, showFigure, showReset);

    // Extracts the group prefix from a metric label ("Gemini · 5 小时" -> "Gemini"), or empty when ungrouped.
    public static string GroupName(string? label) => label is not null && label.Contains('·') ? label.Split('·')[0].Trim() : "";

    // Groups and orders quota items according to per-app priority, falling back to the provider's default order.
    // For Antigravity, groups of equal default rank are ordered by shortest window within the group (matching legacy paging).
    public static List<List<T>> OrderGroups<T>(IReadOnlyList<T> items, Func<T, string> labelOf, IReadOnlyList<string>? preferredOrder, ProviderId id, Func<T, int?>? windowOf = null)
    {
        var originalOrder = items.Select(item => GroupName(labelOf(item))).Distinct(StringComparer.Ordinal).ToList();
        int DefaultRank(string group) => id == ProviderId.Antigravity
            ? group switch { "Gemini" => 0, "Argon" => 1, { Length: > 0 } => 2, _ => 3 }
            : originalOrder.IndexOf(group);
        int PriorityRank(string group)
        {
            if (preferredOrder is { Count: > 0 } && group.Length > 0)
            {
                for (int i = 0; i < preferredOrder.Count; i++)
                    if (string.Equals(preferredOrder[i], group, StringComparison.Ordinal)) return i;
            }
            return int.MaxValue;
        }
        int ShortestWindow(IGrouping<string, T> group) => id == ProviderId.Antigravity && windowOf is not null
            ? group.Min(item => windowOf(item) is > 0 and var w ? w : int.MaxValue)
            : 0;
        return items.GroupBy(item => GroupName(labelOf(item)), StringComparer.Ordinal)
            .OrderBy(g => PriorityRank(g.Key))
            .ThenBy(g => DefaultRank(g.Key))
            .ThenBy(ShortestWindow)
            .ThenBy(g => originalOrder.IndexOf(g.Key))
            .Select(g => g.ToList())
            .ToList();
    }

    // Swaps two visible groups in the persisted group order while preserving any groups not currently present.
    public static List<string> SwapGroupOrder(IReadOnlyList<string>? existingOrder, IReadOnlyList<string> visibleGroups, int indexA, int indexB)
    {
        var merged = new List<string>();
        if (existingOrder is not null)
        {
            foreach (var group in existingOrder)
                if (!string.IsNullOrWhiteSpace(group) && !merged.Contains(group, StringComparer.Ordinal)) merged.Add(group);
        }
        foreach (var group in visibleGroups)
            if (!string.IsNullOrWhiteSpace(group) && !merged.Contains(group, StringComparer.Ordinal)) merged.Add(group);
        if (indexA < 0 || indexB < 0 || indexA >= visibleGroups.Count || indexB >= visibleGroups.Count || indexA == indexB)
            return merged;
        int posA = merged.IndexOf(visibleGroups[indexA]);
        int posB = merged.IndexOf(visibleGroups[indexB]);
        if (posA >= 0 && posB >= 0) (merged[posA], merged[posB]) = (merged[posB], merged[posA]);
        return merged;
    }

    // The account balance, with or without the row's target balance as its total (DashboardUsage.Quota).
    public static bool IsAccountBalance(UsageMetric metric) => metric is { Kind: MetricKind.Balance, Label: "账户余额" } && (metric.Total is null || metric.TotalIsTarget);

    // The quotas the nested rings show by default, outside in (UsageMetric.Id), for the settings to start from.
    public static IReadOnlyList<string> Automatic(ProviderSnapshot snapshot) => [.. Choices(snapshot).Take(MaximumRings).Select(metric => metric.Id)];

    // The quotas an application's rings can be given (AppEntry.SingleRingQuota, AppEntry.RingQuotas), in their default order
    // (Ordered): every quota with a share, and every balance and amount spent, which a target gives a share.
    public static IReadOnlyList<UsageMetric> Choices(ProviderSnapshot snapshot) => Ordered(snapshot.Metrics.Where(metric =>
        metric.UsedPercent is { } used && double.IsFinite(used) || metric.Kind is MetricKind.Balance or MetricKind.Spend && metric.Amount is not null));

    // The quotas an application's compact detail strip and multi-select can offer, in default order:
    // keeps ring choices as well as label-bearing metrics without a percentage share (e.g. custom text metrics).
    public static IReadOnlyList<UsageMetric> DetailChoices(ProviderSnapshot snapshot) => Ordered(snapshot.Metrics.Where(metric =>
        metric.UsedPercent is { } used && double.IsFinite(used)
        || metric.Kind is MetricKind.Balance or MetricKind.Spend && metric.Amount is not null
        || !string.IsNullOrWhiteSpace(metric.Label)));

    // The default order of a source's quotas: the account balance first; then a source that groups its quotas ("Gemini · 5 小时",
    // "Claude · 每周", or one Claude organization's before another's) keeps its groups in its own order, and within a group, as for a
    // source without groups, the shortest window comes first (five hours, a day, a week, a month) and quotas without a window (tool
    // calls, a key's balance, an amount spent) come last. Quotas that tie keep the source's order, so Claude's week comes before its
    // models' weeks and New API's key balance before today's spending. Antigravity: Gemini's five hours and week, then Claude's five hours.
    private static IReadOnlyList<UsageMetric> Ordered(IEnumerable<UsageMetric> metrics)
    {
        var list = metrics.Where(metric => metric.Id.Length > 0).DistinctBy(metric => metric.Id).ToList();
        static string Group(UsageMetric metric) => metric.Label.Contains('·') ? metric.Label.Split('·')[0].Trim() : "";
        var groups = list.Select(Group).Distinct().ToList();
        // OrderBy is stable, so quotas that tie keep the source's order.
        return [.. list.OrderBy(metric => IsAccountBalance(metric) ? 0 : 1).ThenBy(metric => groups.IndexOf(Group(metric)))
            .ThenBy(metric => metric.Window is > 0 ? metric.Window.Value : int.MaxValue)];
    }
}
