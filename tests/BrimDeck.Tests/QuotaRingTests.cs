using BrimDeck.Core;

// One collapsed ring per application (QuotaRing.For). Nested by default: the first three quotas in their default order, one per
// ring (shortest window first within each group, quotas without a window last). Nested and chosen: the application's quotas
// outside in, a missing one kept as an empty ring. Single: the chosen quota, else the highest share.
internal static class QuotaRingTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        static ProviderSnapshot Snapshot(params Quota[] quotas) => new(ProviderId.Claude) { Quotas = [.. quotas] };
        static QuotaRing Of(params Quota[] quotas) => QuotaRing.For(Snapshot(quotas));
        static bool Is(QuotaRing ring, params double?[] shares) => ring.Shares.SequenceEqual(shares);

        var codex = Of(new("5 小时额度", 35, null, 300), new("每周额度", 88, null, 10080));
        check("Quota ring: five hours outside, the week inside", Is(codex, 35, 88) && codex.Detail.Contains("35") && codex.Detail.Contains("88"));

        var claude = Of(new("每周额度", 41, null, 10080), new("Sonnet 每周", 55, null, 10080), new("5 小时额度", 72, null, 300));
        check("Quota ring: Claude's week goes before its models' weeks, each in a ring of its own", Is(claude, 72, 41, 55));

        var antigravity = Of(new("Gemini · 5 小时", 60, null, 300), new("Gemini · 每周", 20, null, 10080), new("Claude · 5 小时", 12, null, 300), new("Claude · 每周", 8, null, 10080));
        check("Quota ring: Antigravity shows Gemini's five hours and week, then Claude's five hours, whichever is used more", Is(antigravity, 60, 20, 12));

        var monthly = Of(new("每月", 10, null, 43200), new("每日", 40, null, 1440));
        check("Quota ring: a daily quota outside a monthly one", Is(monthly, 40, 10));

        check("Quota ring: a quota without a window comes after the windows",
            Is(Of(new("Gemini Pro", 64, null), new("5 小时额度", 30, null, 300)), 30, 64));
        check("Quota ring: at most three rings, the rest in the tooltip",
            Is(Of(new("每月", 5, null, 43200), new("每周额度", 15, null, 10080), new("5 小时额度", 25, null, 300), new("每日", 35, null, 1440)), 25, 35, 15));
        // GLM: five hours and a week by time, and the monthly tool calls counted without a window.
        var glm = QuotaRing.For(new ProviderSnapshot(ProviderId.GlmChina) { Metrics = [
            new UsageMetric(MetricKind.Percent, "5 小时") { Id = "TOKENS_LIMIT:3:5:0", Percent = 20, Window = 300 },
            new UsageMetric(MetricKind.Percent, "每周") { Id = "TOKENS_LIMIT:6:1:1", Percent = 12, Window = 10080 },
            new UsageMetric(MetricKind.Count, "工具调用") { Id = "tools", Used = 80, Total = 100 }] });
        check("Quota ring: GLM shows five hours, the week, then the tool calls", Is(glm, 20, 12, 80) && glm.Detail.Contains("80"));
        var noWeek = QuotaRing.For(new ProviderSnapshot(ProviderId.GlmChina) { Metrics = [
            new UsageMetric(MetricKind.Percent, "5 小时") { Id = "TOKENS_LIMIT:3:5:0", Percent = 20, Window = 300 },
            new UsageMetric(MetricKind.Count, "工具调用") { Id = "tools", Used = 80, Total = 100 }] });
        check("Quota ring: without a weekly quota the tool calls move out", Is(noWeek, 20, 80));

        check("Quota ring: one window keeps one ring with its highest share",
            Is(Of(new Quota("每周额度", 70, null, 10080)), 70) && Is(Of(new("A 每周", 20, null, 10080), new("B 每周", 45, null, 10080)), 20, 45));
        check("Quota ring: quotas without windows keep the source's order", Is(Of(new("Gemini Pro", 30, null), new("Claude", 50, null)), 30, 50));
        check("Quota ring: a share that is not a number is left out", Is(Of(new("5 小时额度", double.NaN, null, 300), new("每周额度", 25, null, 10080)), 25));

        var balance = QuotaRing.For(new ProviderSnapshot(ProviderId.Claude) { Metrics = [new UsageMetric(MetricKind.Balance, "账户余额") { Amount = 5m, Currency = "USD" }] });
        var nothing = QuotaRing.For(new ProviderSnapshot(ProviderId.Claude) { StatusLabel = "未登录" });
        check("Quota ring: a balance without a share fills one ring, a source with nothing leaves it empty",
            Is(balance, 100) && balance.Detail.Contains("5.00") && Is(nothing, 0) && nothing.Detail == "未登录");

        // What is left: each ring fills with 100 less its used share, and the tooltip says "left".
        var left = QuotaRing.For(Snapshot(new("5 小时额度", 35, null, 300), new("每周额度", 88, null, 10080)), null, QuotaRingStyle.Nested, true);
        check("Quota ring: the arcs can show what is left", Is(left, 65, 12) && left.Detail.Contains("65") && !left.Detail.Contains("88"));

        // Chosen quotas: outside in as chosen, up to three; a quota the source does not report keeps its ring empty; while it
        // reports none of them, the automatic rings show.
        var fable = Snapshot(new("5 小时额度", 42, null, 300), new("每周额度", 68, null, 10080), new("Fable 每周", 30, null, 10080));
        var three = new AppEntry { RingQuotas = ["5 小时额度", "每周额度", "Fable 每周"] };
        var turned = new AppEntry { RingQuotas = ["Fable 每周", "5 小时额度"] };
        var away = new AppEntry { RingQuotas = ["5 小时额度", "Opus 每周", "每周额度"] };
        var gone = new AppEntry { RingQuotas = ["Opus 每周"] };
        check("Quota ring: chosen quotas show outside in, up to three",
            Is(QuotaRing.For(fable, three, QuotaRingStyle.Nested, false), 42, 68, 30) && Is(QuotaRing.For(fable, turned, QuotaRingStyle.Nested, false), 30, 42));
        check("Quota ring: a chosen quota without data keeps an empty ring",
            Is(QuotaRing.For(fable, away, QuotaRingStyle.Nested, false), 42, null, 68));
        check("Quota ring: with none of the chosen quotas reported, the default rings show",
            Is(QuotaRing.For(fable, gone, QuotaRingStyle.Nested, false), 42, 68, 30) && Is(QuotaRing.For(fable), 42, 68, 30));
        check("Quota ring: a ring left out draws nothing and the others keep their places",
            Is(QuotaRing.For(fable, new AppEntry { RingQuotas = ["", "", "Fable 每周"] }, QuotaRingStyle.Nested, false), double.NaN, double.NaN, 30)
            && Is(QuotaRing.For(fable, new AppEntry { RingQuotas = ["5 小时额度", "", "Fable 每周"] }, QuotaRingStyle.Nested, false), 42, double.NaN, 30));
        check("Quota ring: the choice by place drops repeats and empty places at the end, and nothing chosen is the default",
            QuotaRing.Slots(["a", "a", "b", "c"]).SequenceEqual(["a", "", "b"]) && QuotaRing.Slots(["", "b", ""]).SequenceEqual(["", "b"]) && QuotaRing.Slots(["", "", ""]).Count == 0);
        check("Quota ring: the chosen rings can show what is left", Is(QuotaRing.For(fable, three, QuotaRingStyle.Nested, true), 58, 32, 70));

        // Single: the highest share, as before the nested rings, or the chosen quota while the source reports it.
        // As before, the single ring counts every quota, the tool calls too.
        var tools = new ProviderSnapshot(ProviderId.GlmChina) { Metrics = [
            new UsageMetric(MetricKind.Percent, "5 小时") { Id = "a", Percent = 20, Window = 300 }, new UsageMetric(MetricKind.Count, "工具调用") { Id = "tools", Used = 80, Total = 100 }] };
        check("Quota ring: one ring shows the highest share by default", Is(QuotaRing.For(fable, null, QuotaRingStyle.Single, false), 68)
            && Is(QuotaRing.For(fable, three, QuotaRingStyle.Single, false), 68) && Is(QuotaRing.For(tools, null, QuotaRingStyle.Single, false), 80));
        check("Quota ring: one ring shows the chosen quota, or the highest while it is away",
            Is(QuotaRing.For(fable, new AppEntry { SingleRingQuota = "5 小时额度" }, QuotaRingStyle.Single, true), 58)
            && Is(QuotaRing.For(fable, new AppEntry { SingleRingQuota = "Opus 每周" }, QuotaRingStyle.Single, false), 68));

        // The settings start from the automatic rings' quotas and offer every quota with a share, shortest window first.
        check("Quota ring: the default quotas and the choices", QuotaRing.Automatic(fable).SequenceEqual(["5 小时额度", "每周额度", "Fable 每周"])
            && QuotaRing.Choices(fable).Select(metric => metric.Id).SequenceEqual(["5 小时额度", "每周额度", "Fable 每周"])
            && QuotaRing.Automatic(new ProviderSnapshot(ProviderId.Claude)).Count == 0);

        // Balances and amounts spent (New API, Sub2API): the account balance first, against the row's target balance, then the
        // source's order; a key's balance with its own total has a share, today's spending one once it has a target of its own.
        static ProviderSnapshot Station() => new(ProviderId.NewApi) { Metrics = [
            new UsageMetric(MetricKind.Balance, "账户余额") { Id = "account", Amount = 20m, Currency = "USD", Total = 100m, TotalIsTarget = true },
            new UsageMetric(MetricKind.Balance, "密钥余额") { Id = "key", Amount = 30m, Total = 50m, Currency = "USD" },
            new UsageMetric(MetricKind.Spend, "今日扣费") { Id = "today", Amount = 3m, Currency = "USD" },
            new UsageMetric(MetricKind.Spend, "密钥累计") { Id = "total", Amount = 12m, Currency = "USD" }] };
        var targeted = new AppEntry { QuotaSource = ProviderId.NewApi, QuotaTargets = new() { ["today"] = 10m, ["key"] = 200m } };
        check("Quota ring: a station's rings are the account balance, the key's balance and today's spending",
            Is(QuotaRing.For(Station()), 80, 40, null) && QuotaRing.Automatic(Station()).SequenceEqual(["account", "key", "today"]));
        check("Quota ring: a target of the ring's own gives spending a share and replaces a balance's total",
            Is(QuotaRing.For(Station(), targeted, QuotaRingStyle.Nested, false), 80, 85, 30)
            && Is(QuotaRing.For(Station(), new AppEntry { RingQuotas = ["total", "today"], QuotaTargets = new() { ["today"] = 10m } }, QuotaRingStyle.Nested, true), null, 70));
        check("Quota ring: the single ring shows the account balance by default, or the chosen quota with its target",
            Is(QuotaRing.For(Station(), null, QuotaRingStyle.Single, false), 80)
            && Is(QuotaRing.For(Station(), new AppEntry { SingleRingQuota = "today", QuotaTargets = new() { ["today"] = 6m } }, QuotaRingStyle.Single, false), 50));
        check("Quota ring: the account balance takes no target of the ring's own",
            !QuotaRing.TakesTarget(Station().Metrics[0]) && QuotaRing.TakesTarget(Station().Metrics[1]) && QuotaRing.TakesTarget(Station().Metrics[2]));
        var subscription = new ProviderSnapshot(ProviderId.Sub2Api) { Metrics = [
            new UsageMetric(MetricKind.Percent, "每日") { Id = "daily", Percent = 10, Window = 1440 },
            new UsageMetric(MetricKind.Percent, "每周") { Id = "weekly", Percent = 20, Window = 10080 },
            new UsageMetric(MetricKind.Balance, "账户余额") { Id = "wallet", Amount = 5m, Currency = "USD" }] };
        check("Quota ring: the account balance goes before the windows", QuotaRing.Automatic(subscription).SequenceEqual(["wallet", "daily", "weekly"]));

        // Nested rings keep a 16 DIP floor whatever they hold; the single ring keeps its 12 DIP reference.
        var one = CompactMusicLayout.Calculate(1000, 1, false, 3, false, 0);
        var two = CompactMusicLayout.Calculate(1000, 2, false, 3, false, 0);
        var small = CompactMusicLayout.Calculate(1000, 1, false, 3, false, 0, ring: QuotaRing.Diameter(QuotaRingStyle.Single));
        check("Quota ring: nested rings keep a 16 DIP floor and match taller system dials", one.RingSize == 16 && two.RingSize == 32 && two.RingWidth == 3 * 32 + 2 * 16);
        foreach (var (height, diameter) in new[] { (24d, 16d), (32d, 16d), (40d, 20d), (48d, 24d), (64d, 32d) })
        {
            var layout = CompactMusicLayout.Calculate(1000, height / 32, false, 3, false, 0);
            check($"Quota ring: height {height} preserves readable concentric geometry and its full layout budget",
                layout.RingSize == diameter && layout.RowHeight >= diameter && layout.RingScale == diameter / 20
                && diameter - 2 * (3 * CompactMusicLayout.Half(1.5 * layout.RingScale) + 2 * CompactMusicLayout.Half(layout.RingScale)) >= 6
                && CompactMusicLayout.NaturalWidth(height / 32, false, 3, false, 0, 0) == layout.RingWidth);
        }
        check("Quota ring: the single ring is 12 DIP and narrows the row",
            small.RingSize == 12 && small.RingWidth == 3 * 12 + 2 * 8 && CompactMusicLayout.RingSizeFor(QuotaRing.Single, 2) == 24
            && CompactMusicLayout.NaturalWidth(1, false, 3, false, 0, 0, ring: QuotaRing.Single) == 3 * 12 + 2 * 8);

        // The settings: the style and the arcs' meaning default to single and used, are saved and read back, and a file without
        // them reads as the defaults; an application's choices are cleaned when read.
        var store = new SettingsStore(Path.Combine(root, "quota-rings"), "0.1.0");
        var fresh = store.Load();
        bool defaults = fresh.QuotaRingStyle == QuotaRingStyle.Single && !fresh.QuotaRingRemaining;
        fresh.QuotaRingStyle = QuotaRingStyle.Nested; fresh.QuotaRingRemaining = true;
        fresh.Apps[0].SingleRingQuota = " 每周额度 "; fresh.Apps[0].RingQuotas = ["", "b", "b", "c"]; fresh.Apps[0].QuotaTargets = new() { ["today"] = 12.345m, ["key"] = -1m };
        store.Save(fresh);
        var read = new SettingsStore(store.DirectoryPath, "0.1.0").Load();
        check("Quota ring settings: saved and read back, with each application's choices cleaned",
            defaults && read.QuotaRingStyle == QuotaRingStyle.Nested && read.QuotaRingRemaining
            && read.Apps[0].SingleRingQuota == "每周额度" && read.Apps[0].RingQuotas.SequenceEqual(["", "b"])
            && read.Apps[0].QuotaTargets.Count == 1 && read.Apps[0].QuotaTargets["today"] == 12.35m);
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","QuotaRingStyle":7}""");
        var odd = new SettingsStore(store.DirectoryPath, "0.1.0").Load();
        check("Quota ring settings: an unknown style, or a file without one, reads as single", odd.QuotaRingStyle == QuotaRingStyle.Single && !odd.QuotaRingRemaining);
        var copy = read.Apps[0].Copy(); copy.RingQuotas.Add("z");
        check("Quota ring settings: a copied application keeps its own list", read.Apps[0].RingQuotas.Count == 2);

        // Collapsed quota details: primary figure, compact reset countdown, detail text composition, layout width and settings persistence.
        var detailSnap = new ProviderSnapshot(ProviderId.Claude)
        {
            Quotas =
            [
                new("5 小时额度", 35, new DateTimeOffset(2026, 11, 17, 12, 30, 0, TimeSpan.Zero)),
                new("每周额度", 70, new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero))
            ]
        };
        check("Quota ring detail: primary figure follows used/remaining setting and single-ring choice",
            QuotaRing.PrimaryFigure(detailSnap, null, false) == "70%"
            && QuotaRing.PrimaryFigure(detailSnap, null, true) == "30%"
            && QuotaRing.PrimaryFigure(detailSnap, new AppEntry { SingleRingQuota = "5 小时额度" }, false) == "35%"
            && QuotaRing.PrimaryFigure(new ProviderSnapshot(ProviderId.Claude), null, false) == "");
        var now = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        check("Quota ring detail: compact reset countdown formats minutes, hours, days and due",
            QuotaRing.CompactReset(null, now) == ""
            && QuotaRing.CompactReset(now.AddMinutes(-1), now) == Loc.T("待重置", "Reset due")
            && QuotaRing.CompactReset(now.AddMinutes(45), now) == Loc.T("45分", "45m")
            && QuotaRing.CompactReset(now.AddHours(2).AddMinutes(30), now) == Loc.T("2时30分", "2h 30m")
            && QuotaRing.CompactReset(now.AddDays(1).AddHours(3), now) == Loc.T("1天3时", "1d 3h"));
        check("Quota ring detail: compact metric label formats window and group",
            QuotaRing.CompactMetricLabel(new UsageMetric(MetricKind.Percent, "5 小时额度") { Window = 300 }) == "5h"
            && QuotaRing.CompactMetricLabel(new UsageMetric(MetricKind.Percent, "每周额度") { Window = 10080 }) == Loc.T("周", "1w")
            && QuotaRing.CompactMetricLabel(new UsageMetric(MetricKind.Percent, "双周额度") { Window = 20160 }) == Loc.T("2周", "2w")
            && QuotaRing.CompactMetricLabel(new UsageMetric(MetricKind.Percent, "Gemini · 5 小时") { Window = 300 }) == "Gemini 5h"
            && QuotaRing.CompactMetricLabel(new UsageMetric(MetricKind.Percent, "Gemini · 每周") { Window = 10080 }, omitGroup: true) == Loc.T("周", "1w")
            && QuotaRing.CompactMetricLabel(new UsageMetric(MetricKind.Percent, "Pro · 每周额度") { Window = 10080 }) != QuotaRing.CompactMetricLabel(new UsageMetric(MetricKind.Percent, "Pro · Fable 每周") { Window = 10080 })
            && QuotaRing.CompactMetricLabel(new UsageMetric(MetricKind.Balance, "账户余额")) == Loc.Label("账户余额")
            && QuotaRing.CompactMetricLabel(null) == "");
        check("Quota ring detail: detail text joins only enabled non-empty parts",
            QuotaRing.DetailText("Claude", "5h", "35%", "2h 30m", true, true, true, true) == "Claude 5h 35% 2h 30m"
            && QuotaRing.DetailText("Claude", "5h", "35%", "", true, false, true, true) == "Claude 35%"
            && QuotaRing.DetailText("Claude", "5h", "35%", "2h 30m", false, true, true, false) == "5h 35%"
            && QuotaRing.DetailText("Claude", "5h", "35%", "2h 30m", false, false, false, false) == "");
        check("Quota ring detail: NaturalWidth accepts custom ringsWidth for detail labels",
            CompactMusicLayout.NaturalWidth(1, false, 2, false, 0, 0, ringsWidth: 180) == 180);
        // A wide detail strip beside a short lyric and monitor readings must fit all three at their measured widths.
        foreach (double measured in new[] { 9.5, 127.2, 840.5 })
        {
            double lyric = CompactMusicLayout.MeasuredTextSlot(measured);
            double width = CompactMusicLayout.NaturalWidth(1, false, 2, true, 0, 160, textSlot: lyric, ringsWidth: 500);
            var (quotas, readings) = CompactSpacing.Share([230, 258], 12, [160], 0, width - lyric - 16);
            var row = CompactSpacing.Arrange([new(quotas.Window), new(0), new(readings.Window)], 1, lyric, 18, width, 8, 1, 1, lyric);
            check($"Quota details and a {measured}-DIP lyric fit with one gap on each side",
                lyric == Math.Ceiling(measured) + 1 && width == 500 + 8 + lyric + 8 + 160
                && quotas.Travel == 0 && row.Room.SequenceEqual([500d, lyric, 160d]) && row.Gap == 8 && row.Left[0] == 0);
        }
        check("Quota ring detail settings: default off and persist",
            !fresh.QuotaRingNames && !fresh.QuotaRingLabels && !fresh.QuotaRingFigures && !fresh.QuotaRingResets && !fresh.ShowsQuotaDetails);
        fresh.QuotaRingNames = true; fresh.QuotaRingLabels = true; fresh.QuotaRingFigures = true; fresh.QuotaRingResets = true;
        store.Save(fresh);
        var readDetails = new SettingsStore(store.DirectoryPath, "0.1.0").Load();
        check("Quota ring detail settings: read back enabled flags",
            readDetails.QuotaRingNames && readDetails.QuotaRingLabels && readDetails.QuotaRingFigures && readDetails.QuotaRingResets && readDetails.ShowsQuotaDetails);

        var pickedEntry = new AppEntry { CompactQuotas = ["5 小时额度", "missing-id"] };
        check("Quota ring detail metrics: primary, selected and all modes",
            QuotaRing.DetailMetrics(detailSnap, null, false).Select(m => m.Id).SequenceEqual(["每周额度"])
            && QuotaRing.DetailMetrics(detailSnap, pickedEntry, false).Select(m => m.Id).SequenceEqual(["5 小时额度"])
            && QuotaRing.DetailMetrics(detailSnap, null, true).Select(m => m.Id).SequenceEqual(["5 小时额度", "每周额度"]));
        var copiedEntry = pickedEntry.Copy(); copiedEntry.CompactQuotas.Add("每周额度");
        check("Quota ring detail settings: copied app keeps its own CompactQuotas", pickedEntry.CompactQuotas.Count == 2 && copiedEntry.CompactQuotas.Count == 3);
        check("Quota ring detail: single ring follows lone CompactQuotas choice when SingleRingQuota is empty and respects explicit override",
            QuotaRing.PrimaryFigure(detailSnap, new AppEntry { CompactQuotas = ["5 小时额度"] }, false) == "35%"
            && QuotaRing.For(detailSnap, new AppEntry { CompactQuotas = ["5 小时额度"] }, QuotaRingStyle.Single, false).Shares.SequenceEqual([35d])
            && QuotaRing.For(detailSnap, new AppEntry { SingleRingQuota = "每周额度", CompactQuotas = ["5 小时额度"] }, QuotaRingStyle.Single, false).Shares.SequenceEqual([70d]));

        // Explicit account balance selection overrides lone CompactQuotas choice.
        var accountSnap = new ProviderSnapshot(ProviderId.NewApi)
        {
            Metrics =
            [
                new(MetricKind.Balance, "账户余额") { Id = "账户余额", Amount = 20m, Total = 100m, TotalIsTarget = true },
                new(MetricKind.Percent, "5 小时额度") { Id = "5 小时额度", Percent = 35, Window = 300 }
            ]
        };
        check("Quota ring detail: explicit account balance overrides automatic single-ring selection",
            QuotaRing.For(accountSnap, new AppEntry { CompactQuotas = ["5 小时额度"] }, QuotaRingStyle.Single, false).Shares.SequenceEqual([35d])
            && QuotaRing.For(accountSnap, new AppEntry { SingleRingQuota = "账户余额", CompactQuotas = ["5 小时额度"] }, QuotaRingStyle.Single, false).Shares.SequenceEqual([80d]));

        // Custom text metrics without percent appear in detail choices/metrics without altering ring choices.
        var textOnlySnap = new ProviderSnapshot(ProviderId.Custom)
        {
            Metrics = [new(MetricKind.Text, "Monthly") { Id = "monthly", Value = "$12.50" }]
        };
        check("Quota ring detail: label-bearing text metrics without percent stay in detail choices while ring choices stay unchanged",
            QuotaRing.Choices(textOnlySnap).Count == 0
            && QuotaRing.DetailChoices(textOnlySnap).Select(m => m.Id).SequenceEqual(["monthly"])
            && QuotaRing.DetailMetrics(textOnlySnap, null, true).Select(m => m.Id).SequenceEqual(["monthly"]));

        // Non-generic labels and group prefixes localize in English while keeping the raw GroupName matching key.
        string savedLang = Loc.Language;
        try
        {
            Loc.Use(Loc.English);
            var busyMetric = new UsageMetric(MetricKind.Percent, "外部模型 · 忙时每日额度") { Id = "busy", Percent = 40, Window = 1440 };
            check("Quota ring detail: non-generic labels translate original term and group prefix in English",
                QuotaRing.GroupName(busyMetric.Label) == "外部模型"
                && QuotaRing.CompactMetricLabel(busyMetric) == "Outside models Daily busy-hour limit"
                && QuotaRing.CompactMetricLabel(busyMetric, omitGroup: true) == "Daily busy-hour limit");
        }
        finally { Loc.Use(savedLang); }

        // CompactQuotas supports up to 100 metrics (matching parser cap) and QuotaRingAll enables ShowsQuotaDetails.
        var manyEntry = new AppEntry { CompactQuotas = [.. Enumerable.Range(1, 105).Select(i => $"m{i}")] };
        manyEntry.Normalize();
        var allOnlySettings = new DeckSettings { QuotaRingAll = true };
        check("Quota ring detail settings: CompactQuotas keeps up to 100 items and QuotaRingAll enables details",
            manyEntry.CompactQuotas.Count == 100 && manyEntry.CompactQuotas[39] == "m40"
            && allOnlySettings.ShowsQuotaDetails && !new DeckSettings().ShowsQuotaDetails);

        // Discrete GlideCycle with Step covers middle stops when animations are disabled.
        var discreteMulti = new GlideCycle(200, Discrete: true, Step: 100);
        check("GlideCycle discrete multi-stop: visits start, middle and end stops",
            discreteMulti.DiscreteStops == 3
            && discreteMulti.Shift(0) == 0
            && discreteMulti.Shift(GlideCycle.StartHold + .1) == -100
            && discreteMulti.Shift(GlideCycle.StartHold + GlideCycle.EndHold + .1) == -200);

        var groupedMetrics = new[]
        {
            new UsageMetric(MetricKind.Percent, "Claude · 每周") { Window = 10080 },
            new UsageMetric(MetricKind.Percent, "GPT OSS · 5 小时") { Window = 300 },
            new UsageMetric(MetricKind.Percent, "Gemini · 5 小时") { Window = 300 },
            new UsageMetric(MetricKind.Percent, "Argon · 每周") { Window = 10080 }
        };
        check("Quota group order: defaults to Gemini, Argon, then shortest-window named groups for Antigravity and follows custom priority",
            QuotaRing.OrderGroups(groupedMetrics, m => m.Label, null, ProviderId.Antigravity, m => m.Window).Select(g => QuotaRing.GroupName(g[0].Label)).SequenceEqual(["Gemini", "Argon", "GPT OSS", "Claude"])
            && QuotaRing.OrderGroups(groupedMetrics, m => m.Label, ["Claude", "Argon"], ProviderId.Antigravity, m => m.Window).Select(g => QuotaRing.GroupName(g[0].Label)).SequenceEqual(["Claude", "Argon", "Gemini", "GPT OSS"]));

        // Swapping visible groups preserves missing groups in the saved order.
        check("Quota group order: SwapGroupOrder preserves absent groups when swapping visible neighbors",
            QuotaRing.SwapGroupOrder(["A", "B", "C", "D"], ["A", "C", "D"], 1, 2).SequenceEqual(["A", "B", "D", "C"])
            && QuotaRing.SwapGroupOrder([], ["A", "B", "C"], 0, 1).SequenceEqual(["B", "A", "C"]));
    }
}
