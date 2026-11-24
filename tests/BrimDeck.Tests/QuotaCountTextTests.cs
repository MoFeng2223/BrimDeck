using BrimDeck.Core;

// The "used or left" choice reaches a credit count, not only the rings and the bars (DeckSettings.QuotaRingRemaining).
internal static class QuotaCountTextTests
{
    public static void Run(Action<string, bool> check)
    {
        var credits = new UsageMetric(MetricKind.Count, "套餐积分") { Id = "plan", Unit = "积分", Total = 3000, Used = 1200 };
        check("Counts show what is used by default", credits.CountText == "1,200 / 3,000" && credits.CountLeft == 1800);
        check("Counts show what is left when that is chosen", credits.CountFigure(true) == "1,800 / 3,000");

        var overdrawn = new UsageMetric(MetricKind.Count, "套餐积分") { Id = "plan", Unit = "积分", Total = 300, Used = 420 };
        check("An overdrawn count keeps zero left instead of a negative figure", overdrawn.CountLeft == 0 && overdrawn.CountFigure(true) == "0 / 300");

        var share = new UsageMetric(MetricKind.Percent, "5 小时") { Id = "five", Percent = 40 };
        check("Shares keep their own text", share.CountText.Length == 0 && share.CountFigure(true).Length == 0 && share.CountLeft == 0);
    }
}
