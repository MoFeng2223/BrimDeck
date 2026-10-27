namespace BrimDeck.Core;

// Keep the two original snapshots separate: quota failures must not replace valid usage data.
public sealed record DashboardColumn(AppEntry Entry, ProviderSnapshot Quota, ProviderSnapshot Usage);

public static class DashboardUsage
{
    public static IReadOnlyList<DashboardColumn> Columns(DeckSettings settings, IReadOnlyList<ProviderSnapshot> snapshots)
    {
        int agyUsageCols = settings.EnabledApps.Count(a => a.UsageSource == ProviderId.Pi && TargetPiProvider(a) == "antigravity");
        return settings.EnabledApps.Select(entry => new DashboardColumn(entry,
            Quota(entry, snapshots), ResolveUsage(entry, snapshots, agyUsageCols))).ToList();
    }

    // Reusing an application on several dashboard columns does not multiply its model totals.
    public static IReadOnlyList<ProviderSnapshot> Statistics(DeckSettings settings, IReadOnlyList<ProviderSnapshot> snapshots)
        => settings.StatisticsProviders.Select(id => Find(snapshots, id)).ToList();

    // The local Claude snapshot serves only the rows that chose it; statistics and online rows use the main snapshot.
    private static ProviderSnapshot Find(IReadOnlyList<ProviderSnapshot> snapshots, ProviderId id)
        => snapshots.FirstOrDefault(snapshot => snapshot.Id == id && !snapshot.Configurations.Contains(ProviderCatalog.LocalQuota)) ?? new ProviderSnapshot(id);

    private static ProviderSnapshot ResolveUsage(AppEntry entry, IReadOnlyList<ProviderSnapshot> snapshots, int agyUsageCols)
    {
        var baseUsage = Find(snapshots, entry.UsageSource);
        if (entry.UsageSource != ProviderId.Pi) return baseUsage;
        string target = TargetPiProvider(entry);
        if (agyUsageCols > 1 && target == "antigravity" && entry.PiAccountKey.Length > 0)
            return new ProviderSnapshot(ProviderId.Pi) { UsageNote = Loc.T("Pi 日志无法按账号拆分用量；总用量见 Pi/Codex 列或模型明细", "Pi logs cannot attribute usage per account; see the Pi/Codex column or model details for totals") };
        if (target.Length == 0) return baseUsage;
        bool Match(TokenEntry t) => target == "openai-codex"
            ? t.Provider.Equals("openai-codex", StringComparison.OrdinalIgnoreCase) || t.Model.StartsWith("openai-codex/", StringComparison.OrdinalIgnoreCase)
            : t.Provider.Equals("antigravity", StringComparison.OrdinalIgnoreCase)
                || t.Provider.Equals("google-antigravity", StringComparison.OrdinalIgnoreCase)
                || t.Model.StartsWith("antigravity/", StringComparison.OrdinalIgnoreCase)
                || t.Model.StartsWith("google-antigravity/", StringComparison.OrdinalIgnoreCase);
        var label = target == "openai-codex" ? "Codex" : "Antigravity";
        return new ProviderSnapshot(ProviderId.Pi)
        {
            UsageAvailable = baseUsage.UsageAvailable,
            UsageComplete = baseUsage.UsageComplete,
            UsageStart = baseUsage.UsageStart,
            Source = baseUsage.Source,
            StatusLabel = baseUsage.StatusLabel,
            Status = baseUsage.Status,
            UsageNote = baseUsage.UsageAvailable ? Loc.T($"Pi 会话中的 {label} 用量 · ", $"{label} usage in Pi sessions · ") + baseUsage.UsageNote : baseUsage.UsageNote,
            Entries = baseUsage.Entries.Where(Match).ToList()
        };
    }

    private static string TargetPiProvider(AppEntry entry)
    {
        if (entry.PiProvider.Length > 0) return entry.PiProvider;
        if (entry.Name.Contains("antigravity", StringComparison.OrdinalIgnoreCase) || entry.Name.Contains("agy", StringComparison.OrdinalIgnoreCase)) return "antigravity";
        if (entry.Name.Contains("codex", StringComparison.OrdinalIgnoreCase)) return "openai-codex";
        return "";
    }

    public static ProviderSnapshot Quota(AppEntry entry, IReadOnlyList<ProviderSnapshot> snapshots)
        => entry.QuotaSource == ProviderId.Pi && entry.PiProvider.Length > 0
            ? snapshots.FirstOrDefault(s => s.Id == ProviderId.Pi && (s.Configurations.Contains(ProviderCatalog.PiAccountKey(entry.PiProvider, entry.PiAccountKey)) || s.Configurations.Contains(entry.ConfigurationKey)))
                ?? snapshots.FirstOrDefault(s => s.Id == ProviderId.Pi && s.Configurations.Contains(ProviderCatalog.PiAccountKey(entry.PiProvider, "")))
                ?? new ProviderSnapshot(ProviderId.Pi)
            : ProviderCatalog.ReadsLocalQuota(entry)
            ? snapshots.FirstOrDefault(snapshot => snapshot.Id == entry.QuotaSource && snapshot.Configurations.Contains(ProviderCatalog.LocalQuota)) ?? new ProviderSnapshot(entry.QuotaSource)
            : ProviderCatalog.IsBuiltIn(entry.QuotaSource) ? Find(snapshots, entry.QuotaSource)
            : snapshots.FirstOrDefault(snapshot => snapshot.Id == entry.QuotaSource && snapshot.Configurations.Contains(entry.ConfigurationKey))
                ?? new ProviderSnapshot(entry.QuotaSource);
}
