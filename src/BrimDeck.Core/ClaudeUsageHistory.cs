using System.Text.Json;

namespace BrimDeck.Core;

public sealed record ClaudeUsageSample(DateTimeOffset Time, double FiveHour, double SevenDay);

// The Claude desktop application keeps "plan-usage-history.json" beside its configuration: periodic
// samples of the signed-in account's five-hour ("fh") and seven-day ("sd") utilisation percentages.
// It carries no reset times and no credentials, and is written by the application itself.
public static class ClaudeUsageHistory
{
    public static ClaudeUsageSample? Parse(JsonElement root)
    {
        ClaudeUsageSample? latest = null;
        foreach (var sample in root.Get("samples").Items())
        {
            var usage = sample.Get("u");
            if (sample.Get("t").Date() is not { } time || usage.Get("fh").Number() is not { } fiveHour || usage.Get("sd").Number() is not { } sevenDay) continue;
            if (latest is null || time > latest.Time) latest = new(time, Math.Clamp(fiveHour, 0, 100), Math.Clamp(sevenDay, 0, 100));
        }
        return latest;
    }

    public static ClaudeUsageSample? Latest(DataLocations locations)
    {
        ClaudeUsageSample? latest = null;
        foreach (var profile in locations.ClaudeDesktopProfiles())
        {
            try
            {
                var path = Path.Combine(profile, "plan-usage-history.json");
                if (!File.Exists(path)) continue;
                using var doc = SharedFile.Parse(path);
                if (Parse(doc.RootElement) is { } sample && (latest is null || sample.Time > latest.Time)) latest = sample;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return latest;
    }
}
