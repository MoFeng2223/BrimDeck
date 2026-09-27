namespace BrimDeck.Core;

// Keep the two original snapshots separate: quota failures must not replace valid usage data.
public sealed record DashboardColumn(AppEntry Entry, ProviderSnapshot Quota, ProviderSnapshot Usage);

public static class DashboardUsage
{
    public static IReadOnlyList<DashboardColumn> Columns(DeckSettings settings, IReadOnlyList<ProviderSnapshot> snapshots)
        => settings.EnabledApps.Select(entry => new DashboardColumn(entry,
            Quota(entry, snapshots), Find(snapshots, entry.UsageSource))).ToList();

    // Reusing an application on several dashboard columns does not multiply its model totals.
    public static IReadOnlyList<ProviderSnapshot> Statistics(DeckSettings settings, IReadOnlyList<ProviderSnapshot> snapshots)
        => settings.StatisticsProviders.Select(id => Find(snapshots, id)).ToList();

    private static ProviderSnapshot Find(IReadOnlyList<ProviderSnapshot> snapshots, ProviderId id)
        => snapshots.FirstOrDefault(snapshot => snapshot.Id == id) ?? new ProviderSnapshot(id);

    public static ProviderSnapshot Quota(AppEntry entry, IReadOnlyList<ProviderSnapshot> snapshots)
        => ProviderCatalog.IsBuiltIn(entry.QuotaSource) ? Find(snapshots, entry.QuotaSource)
            : snapshots.FirstOrDefault(snapshot => snapshot.Id == entry.QuotaSource && snapshot.Configurations.Contains(entry.ConfigurationKey))
                ?? new ProviderSnapshot(entry.QuotaSource);
}
