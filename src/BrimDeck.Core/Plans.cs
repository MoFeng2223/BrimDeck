using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

public static class PlanParser
{
    public static string Cursor(JsonElement root) => root.Get("membershipType").Text().Trim();

    public static string Antigravity(JsonElement root)
    {
        var status = root.Get("userStatus");
        var tier = status.Get("userTier").Get("name").Text().Trim();
        return tier.Length > 0 ? tier : status.Get("planStatus").Get("planInfo").Get("planName").Text().Trim();
    }

    public static string ClaudeProfile(JsonElement root)
    {
        var account = root.Get("oauthAccount");
        var tier = account.Get("organizationRateLimitTier").Text();
        return ClaudeTier(tier);
    }

    public static string ClaudeTier(string tier, string subscription = "")
    {
        var max = Regex.Match(tier, @"^default_claude_max_(\d+x)$", RegexOptions.CultureInvariant);
        return max.Success ? "Max " + max.Groups[1].Value : subscription.Trim();
    }
}
