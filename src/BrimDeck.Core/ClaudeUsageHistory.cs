using System.Text.Json;

namespace BrimDeck.Core;

public sealed record ClaudeUsageSample(DateTimeOffset Time, double? FiveHour, double SevenDay, string Organization = "");

// The Claude desktop application keeps "plan-usage-history.json" beside its configuration: periodic
// samples of the signed-in account's five-hour ("fh") and seven-day ("sd") utilisation percentages.
// It carries no reset times and no credentials, and is written by the application itself. Each sample names the organization
// ("org") the application was using; organization, when given, keeps only that organization's samples (and those naming none).
// A plan without a five-hour limit has no "fh", and its samples carry "sd" alone. Whether a sample without "fh" says that or only
// skipped a reading is told from the samples around it: the newest sample counts as weekly-only (FiveHour null) when no sample
// within FiveHourGap before it has "fh". Otherwise the account does have a five-hour window and the newest sample that carries
// "fh" is used, as it was before weekly-only samples were read: a gap in "fh" never replaces the five-hour quota.
public static class ClaudeUsageHistory
{
    public static readonly TimeSpan FiveHourGap = TimeSpan.FromHours(6);

    public static ClaudeUsageSample? Parse(JsonElement root, string organization = "")
    {
        ClaudeUsageSample? latest = null, latestWithFiveHour = null;
        foreach (var sample in root.Get("samples").Items())
        {
            var usage = sample.Get("u");
            var org = sample.Get("org").Text();
            if (organization.Length > 0 && org.Length > 0 && !org.Equals(organization, StringComparison.OrdinalIgnoreCase)) continue;
            if (sample.Get("t").Date() is not { } time || usage.Get("sd").Number() is not { } sevenDay) continue;
            double? fiveHour = usage.Get("fh").Number() is { } fh ? Math.Clamp(fh, 0, 100) : null;
            var parsed = new ClaudeUsageSample(time, fiveHour, Math.Clamp(sevenDay, 0, 100), org);
            if (latest is null || time > latest.Time) latest = parsed;
            if (fiveHour is not null && (latestWithFiveHour is null || time > latestWithFiveHour.Time)) latestWithFiveHour = parsed;
        }
        if (latest?.FiveHour is not null || latest is null) return latest;
        return latestWithFiveHour is not null && latest.Time - latestWithFiveHour.Time <= FiveHourGap ? latestWithFiveHour : latest;
    }

    // The quotas a desktop usage sample gives: the five-hour one only when the sample has a five-hour share (a plan without that
    // limit has none, and none is made up), then the weekly one.
    public static List<Quota> Quotas(ClaudeUsageSample sample, DateTimeOffset? fiveHourReset, DateTimeOffset? weekReset)
    {
        var quotas = new List<Quota>(2);
        if (sample.FiveHour is { } fiveHour) quotas.Add(new Quota("5 小时额度", fiveHour, fiveHourReset, 300));
        quotas.Add(new Quota("每周额度", sample.SevenDay, weekReset, 10080));
        return quotas;
    }

    public static ClaudeUsageSample? Latest(DataLocations locations, string organization = "")
    {
        ClaudeUsageSample? latest = null;
        foreach (var profile in locations.ClaudeDesktopProfiles())
        {
            try
            {
                var path = Path.Combine(profile, "plan-usage-history.json");
                if (!File.Exists(path)) continue;
                using var doc = SharedFile.Parse(path);
                if (Parse(doc.RootElement, organization) is { } sample && (latest is null || sample.Time > latest.Time)) latest = sample;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return latest;
    }
}
