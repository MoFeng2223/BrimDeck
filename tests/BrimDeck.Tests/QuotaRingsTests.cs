using BrimDeck.Core;

// One ring per reported quota, side by side: the five-hour and weekly windows, any extra plan window, a source with only one
// window, a window the source did not state, and a source with no percentage at all. What is left, the time labels and the
// letters are independent switches.
internal static class QuotaRingsTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        var store = new SettingsStore(Path.Combine(root, "quota-rings"), "0.1.0");
        var fresh = store.Load();
        bool defaults = fresh.QuotaRingsRemaining && fresh.QuotaRingWindowMarks && !fresh.QuotaRingLetters;
        fresh.QuotaRingsRemaining = false; fresh.QuotaRingWindowMarks = false; fresh.QuotaRingLetters = true; store.Save(fresh);
        var reread = new SettingsStore(store.DirectoryPath, "0.1.0").Load();
        bool saved = !reread.QuotaRingsRemaining && !reread.QuotaRingWindowMarks && reread.QuotaRingLetters;
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","QuotaRingLetters":true}""");
        var oldFile = new SettingsStore(store.DirectoryPath, "0.1.0").Load();
        bool oldFileDefaults = oldFile.QuotaRingsRemaining && oldFile.QuotaRingWindowMarks && oldFile.QuotaRingLetters;
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","QuotaRingDisplay":99,"QuotaRingsRemaining":false}""");
        var retired = new SettingsStore(store.DirectoryPath, "0.1.0").Load();
        bool retiredModeIgnored = retired.QuotaRingWindowMarks && !retired.QuotaRingsRemaining;
        check("Quota rings settings: what is left and time labels on by default, letters off; all saved and read back; a retired mode key is ignored",
            defaults && saved && oldFileDefaults && retiredModeIgnored);

        var both = Snapshot(Five(42), Week(16));
        var pair = CompactQuotaRings.Build("Codex", both, remaining: true);
        check("One ring per window, 5h then 7D, each with its own tooltip and what is left",
            pair.Count == 2 && pair[0] is { Used: 58, Mark: "5h" } && pair[1] is { Used: 84, Mark: "7D" }
            && pair[0].Tip.Contains("5 小时额度", StringComparison.Ordinal) && pair[0].Tip.Contains("剩余 58", StringComparison.Ordinal)
            && pair[1].Tip.Contains("每周额度", StringComparison.Ordinal) && pair[1].Tip.Contains("剩余 84", StringComparison.Ordinal));

        var used = CompactQuotaRings.Build("Codex", both);
        check("With remaining off, the rings and tooltips read in used terms",
            used.Count == 2 && used[0].Used == 42 && used[0].Tip.Contains("已用 42", StringComparison.Ordinal)
            && !used[0].Tip.Contains("剩余", StringComparison.Ordinal));

        var noMarks = CompactQuotaRings.Build("Codex", both, remaining: true, marks: false);
        check("Turning the time labels off keeps every ring and empties every mark", noMarks.Count == 2 && noMarks.All(ring => ring.Mark.Length == 0));

        var weekOnly = CompactQuotaRings.Build("Codex", Snapshot(Week(16)), remaining: true);
        var fiveOnly = CompactQuotaRings.Build("Codex", Snapshot(Five(42)), remaining: true);
        check("A source without a 5-hour limit gets one labelled ring, and vice versa",
            weekOnly.Count == 1 && weekOnly[0].Mark == "7D" && weekOnly[0].Tip.Contains("每周额度", StringComparison.Ordinal)
            && fiveOnly.Count == 1 && fiveOnly[0].Mark == "5h");

        var extra = CompactQuotaRings.Build("Claude", Snapshot(Five(12), Week(30),
            new UsageMetric(MetricKind.Percent, "每月额度") { Percent = 70, Window = 43200 }), remaining: true);
        check("An extra plan window is matched as a third ring labelled 30D",
            extra.Count == 3 && extra[2] is { Used: 30, Mark: "30D" } && extra[2].Tip.Contains("每月额度", StringComparison.Ordinal));

        var custom = CompactQuotaRings.Build("Custom", Snapshot(
            new UsageMetric(MetricKind.Percent, "十二小时") { Percent = 20, Window = 720 },
            new UsageMetric(MetricKind.Percent, "四十五分钟") { Percent = 50, Window = 45 }), remaining: true);
        check("Custom windows get compact h / m labels, shortest window first", custom.Count == 2 && custom[0].Mark == "45m" && custom[1].Mark == "12h");

        var noWindow = CompactQuotaRings.Build("Script", Snapshot(new UsageMetric(MetricKind.Percent, "自定义额度") { Percent = 37 }), remaining: true);
        check("A window the source did not state keeps its ring and full name in the tooltip, without a time label",
            noWindow.Count == 1 && noWindow[0].Mark.Length == 0 && noWindow[0].Used == 63
            && noWindow[0].Tip.Contains("自定义额度", StringComparison.Ordinal));

        var reordered = CompactQuotaRings.Build("Codex", Snapshot(Week(16), Five(42)), remaining: true);
        check("The source's metric order does not move the rings", reordered[0].Mark == "5h" && reordered[1].Mark == "7D");

        var clamped = CompactQuotaRings.Build("Codex", Snapshot(Five(140), Week(-5)), remaining: true);
        check("Shares stay within 0 to 100", clamped[0].Used == 0 && clamped[1].Used == 100);

        var unlimited = CompactQuotaRings.Build("Codex", Snapshot(
            new UsageMetric(MetricKind.Percent, "无限额度") { Percent = 10, Window = 300, Unlimited = true }), remaining: true);
        check("An unlimited quota is named in its tooltip", unlimited.Count == 1 && unlimited[0].Tip.Contains("无上限", StringComparison.Ordinal));

        var noPercent = CompactQuotaRings.Build("Script", Snapshot(
            new UsageMetric(MetricKind.Text, "剩余次数") { Value = "3/10", Suffix = "次" }), remaining: true);
        var empty = CompactQuotaRings.Build("Script", Snapshot(), remaining: true);
        check("A source with no percentage still owns one ring, with its state or status in the tooltip",
            noPercent.Count == 1 && noPercent[0].Tip.Contains("3/10", StringComparison.Ordinal)
            && empty.Count == 1 && empty[0].Tip.Contains("未连接", StringComparison.Ordinal));

        var plainPair = CompactMusicLayout.Calculate(10000, 1, false, 2, false, 0, 0);
        var labelledPair = CompactMusicLayout.Calculate(10000, 1, false, 2, false, 0, 0, ringSize: 18);
        check("Labelled rings and the auto-width surface grow by the same amount",
            plainPair.RingSize == 12 && labelledPair.RingSize == 18 && labelledPair.VisibleRings == 2
            && CompactMusicLayout.NaturalWidth(1, false, 2, false, 0, 0, ringSize: 18)
                - CompactMusicLayout.NaturalWidth(1, false, 2, false, 0, 0) == 12);
    }

    private static ProviderSnapshot Snapshot(params UsageMetric[] metrics) => new(ProviderId.Codex) { Metrics = [.. metrics], StatusLabel = "未连接" };

    private static UsageMetric Five(double percent) => new(MetricKind.Percent, "5 小时额度") { Percent = percent, Window = CompactQuotaRings.FiveHours };
    private static UsageMetric Week(double percent) => new(MetricKind.Percent, "每周额度") { Percent = percent, Window = CompactQuotaRings.Weekly };
}
