using System.Text.Json;
using BrimDeck.Core;

var passed = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
DeckSettings Only(params ProviderId[] ids) => new() { Apps = ids.Select(id => new AppEntry { Id = id }).ToList() };
var root = Path.Combine(Path.GetTempPath(), "BrimDeck-Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var priceTime = DateTimeOffset.UtcNow;
    const string catalogJson = """
        {"data":[
          {"id":"openai/gpt-6-astra","pricing":{"prompt":"0.00001","completion":"0.00005","input_cache_read":"0.000001","input_cache_write":"0.0000125","overrides":[{"min_prompt_tokens":272000,"prompt":"0.00002","completion":"0.000075","input_cache_read":"0.000002","input_cache_write":"0.000025"}]}},
          {"id":"anthropic/claude-fable-5.1","pricing":{"prompt":"0.00001","completion":"0.00005","input_cache_read":"0.00000025","input_cache_write":"0.0000125","input_cache_write_1h":"0.00002"}},
          {"id":"anthropic/claude-sonnet-4.5","pricing":{"prompt":"0.000003","completion":"0.000015"}},
          {"id":"vendor/free-model:free","pricing":{"prompt":"0","completion":"0"}},
          {"id":"vendor/dynamic-price","pricing":{"prompt":"-1","completion":"0.00001"}}
        ]}
        """;
    using var handler = new CatalogHandler(catalogJson);
    using var priceClient = new HttpClient(handler);
    var priceDirectory = Path.Combine(root, "prices");
    using var pricing = new Pricing(priceDirectory, priceClient, () => priceTime);
    Check("No built-in price table", pricing.ModelCount == 0 && pricing.Find("gpt-6-astra") is null);
    await pricing.RefreshAsync();
    Check("Live catalog loaded without credentials", pricing.ModelCount == 4 && handler.Calls == 1 && !handler.SawAuthorization);
    await pricing.RefreshAsync();
    Check("Catalog refresh is throttled within 24 hours", handler.Calls == 1);
    Check("Provider-qualified model and Claude version aliases match", pricing.Find("claude-fable-5-1")?.Model == "anthropic/claude-fable-5.1" && pricing.Find("openai/gpt-6-astra") is not null);
    using (var cached = new Pricing(priceDirectory, priceClient, () => priceTime))
    {
        Check("Downloaded catalog survives restart", cached.ModelCount == 4 && cached.UpdatedAt == pricing.UpdatedAt);
        await cached.RefreshAsync();
        Check("Restart refreshes online even with fresh cache", handler.Calls == 2);
    }

    var defaults = new DeckSettings();
    Check("Default notch", defaults.Style == CompactStyle.Notch);
    Check("Default compact summary rings", defaults.CompactSummary);
    Check("Maximized uses line", defaults.Behavior(ScreenContext.Maximized) == WindowBehavior.Line);
    Check("Both fullscreen modes hide", defaults.Behavior(ScreenContext.Borderless) == WindowBehavior.Hide && defaults.Behavior(ScreenContext.Exclusive) == WindowBehavior.Hide);
    Check("Borderless fullscreen classified independently", ScreenPolicy.Classify(false, true, false, true) == ScreenContext.Borderless);
    Check("Normal maximized browser classified", ScreenPolicy.Classify(false, false, true, true) == ScreenContext.Maximized);
    Check("Auto-hidden taskbar does not turn captioned maximization into fullscreen", ScreenPolicy.Classify(false, true, true, true) == ScreenContext.Maximized);
    Check("Exclusive mode wins over window shape", ScreenPolicy.Classify(true, false, true, true) == ScreenContext.Exclusive);
    defaults.Borderless = WindowBehavior.Normal;
    Check("Fullscreen override independent", defaults.Behavior(ScreenContext.Borderless) == WindowBehavior.Normal && defaults.Behavior(ScreenContext.Exclusive) == WindowBehavior.Hide);
    Check("All four presets are listed in order by default", defaults.Apps.Select(app => app.Id).SequenceEqual([ProviderId.Claude, ProviderId.Codex, ProviderId.Antigravity, ProviderId.Cursor]) && defaults.EnabledApps.Count == 4);
    Check("Four applications raise the minimum width to 640", defaults.MinimumWidth == 640);
    defaults.UsagePage = false;
    Check("Page off retains provider choice", !defaults.Enabled(ProviderId.Claude) && defaults.Entry(ProviderId.Claude)!.Enabled);
    defaults.UsagePage = true; defaults.SetEnabled(ProviderId.Codex, false);
    Check("Provider toggles independent", defaults.Enabled(ProviderId.Claude) && !defaults.Enabled(ProviderId.Codex) && defaults.EnabledApps.Count == 3);
    Check("Two applications keep the 440 minimum", Only(ProviderId.Claude, ProviderId.Codex).MinimumWidth == 440);
    defaults.Width = double.NaN; defaults.Height = 99999; defaults.OpenDelay = -10; defaults.Style = (CompactStyle)99; defaults.Normalize();
    Check("Invalid settings clamped", defaults.Width == 760 && defaults.Height == 400 && defaults.OpenDelay == 0 && defaults.Style == CompactStyle.Notch);
    var narrow = new DeckSettings { Width = 500 }; narrow.Normalize();
    Check("Width below the per-column minimum is raised", narrow.Width == 640);
    var moved = new DeckSettings(); var first = moved.Apps[0]; moved.Apps.RemoveAt(0); moved.Apps.Add(first);
    moved.Apps[0].ThemeColor = "ff8800"; moved.Apps[1].WarningColor = "#zzzzzz"; moved.Apps.Add(new AppEntry { Id = ProviderId.Codex, ThemeColor = "#000000" });
    moved.Normalize();
    Check("Order is kept, colors are normalized and duplicates are dropped", moved.Apps.Select(app => app.Id).SequenceEqual([ProviderId.Codex, ProviderId.Antigravity, ProviderId.Cursor, ProviderId.Claude]) &&
        moved.Apps[0].ThemeColor == "#FF8800" && moved.Apps[1].WarningColor == "" && moved.Apps[1].Warning == AppPresets.WarningColor);
    Check("Quota color follows the thresholds", AppPresets.QuotaColor(moved.Apps[0], 69.9) == "#FF8800" && AppPresets.QuotaColor(moved.Apps[0], 70) == AppPresets.WarningColor && AppPresets.QuotaColor(moved.Apps[0], 90) == AppPresets.CriticalColor);
    var copied = moved.Copy(); copied.Apps[0].ThemeColor = "#123456";
    Check("Copies do not share application entries", moved.Apps[0].ThemeColor == "#FF8800");
    var store = new SettingsStore(Path.Combine(root, "settings")); store.Save(moved);
    var loaded = store.Load();
    Check("Settings roundtrip keeps order and colors", loaded.Apps.Select(app => app.Id).SequenceEqual(moved.Apps.Select(app => app.Id)) && loaded.Apps[0].ThemeColor == "#FF8800" && loaded.Height == 240);
    File.WriteAllText(store.FilePath, "broken json");
    Check("Corrupt settings recover visibly", store.Load().Style == CompactStyle.Notch && store.LoadWarning is not null);

    var now = DateTimeOffset.Now;
    var locations = DataLocations.Resolve(Path.Combine(root, "home"), Path.Combine(root, "roaming"), Path.Combine(root, "local"), null, "");
    Check("Provider paths resolve without saved configuration", locations.ClaudeHome == Path.Combine(root, "home", ".claude") &&
        locations.CodexHome == Path.Combine(root, "home", ".codex") && locations.CursorDatabase == Path.Combine(root, "roaming", "Cursor", "User", "globalStorage", "state.vscdb"));
    var relocated = DataLocations.Resolve(root, root, root, Path.Combine(root, "custom-claude"), Path.Combine(root, "custom-codex"));
    Check("Existing provider environment overrides are respected", relocated.ClaudeHome.EndsWith("custom-claude") && relocated.CodexHome.EndsWith("custom-codex"));
    var classicProfile = Path.Combine(locations.Roaming, "Claude");
    var storeProfile = Path.Combine(locations.Local, "Packages", "Claude_testfamily", "LocalCache", "Roaming", "Claude");
    Directory.CreateDirectory(classicProfile); Directory.CreateDirectory(storeProfile);
    Directory.CreateDirectory(Path.Combine(locations.Local, "Packages", "Unrelated_testfamily", "LocalCache", "Roaming", "Claude"));
    Check("Classic and Store Claude profiles discovered automatically", locations.ClaudeDesktopProfiles().Count == 2 && locations.ClaudeDesktopProfiles().Contains(storeProfile));
    var oldSettings = JsonSerializer.Deserialize<DeckSettings>("""{"Width":890,"Claude":false,"CodexHome":"obsolete","ClaudeHome":"obsolete","CursorDatabase":"obsolete","OnlineQuota":false}""")!;
    oldSettings.Normalize();
    var migrated = JsonSerializer.Serialize(oldSettings);
    Check("Old switches migrate into the application list and are not written back", oldSettings.Width == 890 && !oldSettings.Enabled(ProviderId.Claude) && oldSettings.Enabled(ProviderId.Codex) &&
        migrated.Contains("\"Apps\"") && !migrated.Contains("\"Claude\"") && !migrated.Contains("Home") && !migrated.Contains("CursorDatabase") && !migrated.Contains("OnlineQuota"));
    object CachedToken(string token, long expires) => new { token, expiresAt = expires, subscriptionType = "max", rateLimitTier = "default_claude_max_5x" };
    string CacheKey(string account, string scope, string host = "https://api.anthropic.com") => $"acct:{account}|client:org:{host}:{scope}";
    var future = now.AddHours(1).ToUnixTimeMilliseconds();
    var cacheFixture = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
    {
        [CacheKey("current", "user:inference user:profile")] = CachedToken("test-inference", future),
        [CacheKey("current", "user:profile")] = CachedToken("test-profile", future),
        [CacheKey("previous", "user:profile")] = CachedToken("test-other-account", future),
        [CacheKey("current", "user:profile", "https://example.invalid")] = CachedToken("test-other-host", future),
        [CacheKey("current", "user:inference")] = CachedToken("test-no-profile", future),
        [CacheKey("current", "user:profile expired")] = CachedToken("test-expired", now.AddSeconds(-1).ToUnixTimeMilliseconds()),
        [CacheKey("current", "user:profile cleared")] = null
    });
    var desktopCredentials = ClaudeDesktopCache.Parse(cacheFixture, "current", now);
    Check("Desktop selects active account, provider host, valid expiry and profile scope", desktopCredentials.Count == 2 && desktopCredentials[0].Token == "test-profile");
    Check("Desktop tier provides exact Max multiplier", desktopCredentials.All(item => item.Plan == "Max 5x"));
    Check("Scoped desktop tokens require a known current account", ClaudeDesktopCache.Parse(cacheFixture, "", now).Count == 0);
    Check("Credential formatting does not expose secrets", !desktopCredentials[0].ToString()!.Contains("test-profile"));
    var desktopFixture = new ClaudeFixtureSources { State = new(true, desktopCredentials) };
    var usageClock = now;
    using (var quotaHandler = new ClaudeQuotaHandler())
    using (var quotaClient = new HttpClient(quotaHandler))
    using (var service = new UsageService(desktopFixture, locations, quotaClient, () => usageClock))
    {
        // Match the panel's shared 60-second refresh cycle without waiting on the wall clock.
        Task<List<ProviderSnapshot>> Refresh(DeckSettings settings, CancellationToken ct = default) { usageClock = usageClock.AddSeconds(60); return service.RefreshAsync(settings, ct); }
        var onlyClaude = Only(ProviderId.Claude);
        var result = (await Refresh(onlyClaude)).Single();
        Check("Desktop-only login connects without Claude Code credentials", result.LiveQuota && result.Quotas.Count == 2 && result.Plan == "Max 5x" && result.Source.Contains("桌面版"));
        Check("Desktop quota resets come from server", result.Quotas.All(quota => quota.ResetAt is not null) && result.Quotas.Single(quota => quota.Minutes == 300).UsedPercent == 9);
        Directory.CreateDirectory(locations.ClaudeHome);
        var credentialPath = Path.Combine(locations.ClaudeHome, ".credentials.json");
        File.WriteAllText(credentialPath, """{"claudeAiOauth":{"accessToken":"test-rejected","subscriptionType":"pro"}}""");
        result = (await Refresh(onlyClaude)).Single();
        Check("Rejected Code login falls back to Desktop and replaces its plan", quotaHandler.Rejected == 1 && result.LiveQuota && result.Plan == "Max 5x");
        File.WriteAllText(credentialPath, "broken");
        result = (await Refresh(onlyClaude)).Single();
        Check("Damaged Code login does not prevent Desktop connection", result.LiveQuota);

        File.WriteAllText(credentialPath, """{"claudeAiOauth":{"accessToken":"test-code","subscriptionType":"pro"}}""");
        desktopFixture.State = new(false, []);
        quotaHandler.Requests.Clear();
        result = (await Refresh(onlyClaude)).Single();
        Check("CLI-only installation connects and displays its own plan", result.LiveQuota && result.Source.StartsWith("Claude Code") && result.Plan == "pro" && quotaHandler.Requests.Count == 1);
        var readCalls = desktopFixture.ReadCalls;
        desktopFixture.ReadFailure = new IOException("Test desktop cache unavailable");
        result = (await Refresh(onlyClaude)).Single();
        Check("A successful CLI result does not depend on Desktop discovery", result.LiveQuota && desktopFixture.ReadCalls == readCalls);
        desktopFixture.ReadFailure = null;
        desktopFixture.State = new(true, desktopCredentials);

        foreach (var fault in new[] { "server", "timeout", "network", "malformed", "empty", "limited" })
        {
            quotaHandler.Requests.Clear();
            quotaHandler.Override = (token, _) => token != "test-code" ? null : fault switch
            {
                "timeout" => throw new TaskCanceledException("Test timeout"),
                "network" => throw new HttpRequestException("Test connection failure"),
                "malformed" => new(System.Net.HttpStatusCode.OK) { Content = new StringContent("invalid json") },
                "empty" => new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") },
                "limited" => new(System.Net.HttpStatusCode.TooManyRequests),
                _ => new(System.Net.HttpStatusCode.ServiceUnavailable)
            };
            result = (await Refresh(onlyClaude)).Single();
            Check($"CLI {fault} failure falls back to one successful Desktop result", result.LiveQuota && result.Quotas.Count == 2 &&
                result.Plan == "Max 5x" && result.Source.Contains("桌面版") && quotaHandler.Requests.SequenceEqual(["test-code", "test-profile"]));
        }

        quotaHandler.Override = null; quotaHandler.Requests.Clear();
        result = (await Refresh(onlyClaude)).Single();
        var requestsSoFar = quotaHandler.Requests.Count;
        result = (await Refresh(onlyClaude)).Single();
        Check("Claude requests fresh account quotas on the next 60-second cycle", result.LiveQuota && result.Quotas.Count == 2 && result.QuotaTime == usageClock && result.Status == "已连接" && quotaHandler.Requests.Count == requestsSoFar + 1);
        result = (await service.RefreshAsync(onlyClaude)).Single();
        Check("Manual refresh immediately requests account quotas", result.LiveQuota && quotaHandler.Requests.Count == requestsSoFar + 2);
        var previousTime = result.QuotaTime;
        quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.ServiceUnavailable);
        quotaHandler.Requests.Clear();
        result = (await Refresh(onlyClaude)).Single();
        Check("Transient failure keeps the last account quotas with their time", result.LiveQuota && result.Quotas.Count == 2 && result.QuotaTime == previousTime && result.Status.Contains("显示") && result.StatusLabel == "稍后重试" && quotaHandler.Requests.Count == 2);
        for (int cycle = 1; cycle <= 4; cycle++)
        {
            quotaHandler.Requests.Clear();
            result = (await Refresh(onlyClaude)).Single();
            Check($"Failure cycle {cycle} still requests quotas after 60 seconds", result.Quotas.Count == 2 && quotaHandler.Requests.Count == 2);
        }
        quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.TooManyRequests);
        quotaHandler.Requests.Clear();
        result = (await Refresh(onlyClaude)).Single();
        Check("Rate limiting does not cycle every Desktop scope", result.Status.Contains("请求频繁") && result.Quotas.Count == 2 && quotaHandler.Requests.Count == 2);
        quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.Unauthorized);
        quotaHandler.Requests.Clear();
        result = (await Refresh(onlyClaude)).Single();
        Check("Rejected logins do not keep showing earlier account quotas", !result.LiveQuota && result.Quotas.Count == 0 && result.StatusLabel == "登录已失效");
        using (var fresh = new UsageService(desktopFixture, locations, quotaClient, () => usageClock))
        {
            quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.ServiceUnavailable);
            quotaHandler.Requests.Clear();
            var unavailable = (await fresh.RefreshAsync(onlyClaude)).Single();
            Check("Both sources failing leaves quota unavailable instead of inventing a value", !unavailable.LiveQuota && unavailable.Quotas.Count == 0 && unavailable.StatusLabel == "暂不可用" && quotaHandler.Requests.Count == 2);
        }

        desktopFixture.State = new(true, [new("test-code", "pro", "Claude 桌面版"), desktopCredentials[0]]);
        quotaHandler.Override = (token, _) => token == "test-code" ? new(System.Net.HttpStatusCode.Unauthorized) : null;
        quotaHandler.Requests.Clear();
        result = (await Refresh(onlyClaude)).Single();
        Check("The same credential shared by CLI and Desktop is queried once", result.LiveQuota && quotaHandler.Requests.SequenceEqual(["test-code", "test-profile"]));
        desktopFixture.State = new(true, desktopCredentials);

        using (var cancelled = new CancellationTokenSource())
        {
            readCalls = desktopFixture.ReadCalls;
            quotaHandler.Override = (_, ct) => { cancelled.Cancel(); ct.ThrowIfCancellationRequested(); return null; };
            bool propagated = false;
            try { await Refresh(onlyClaude, cancelled.Token); } catch (OperationCanceledException) { propagated = true; }
            Check("User cancellation stops reading instead of starting the other source", propagated && desktopFixture.ReadCalls == readCalls);
        }
        quotaHandler.Override = null;
        File.Delete(credentialPath);
        desktopFixture.ReadFailure = new UnauthorizedAccessException("Test unavailable profile");
        result = (await Refresh(onlyClaude)).Single();
        Check("Desktop discovery exceptions produce a readable status", !result.LiveQuota && result.StatusLabel == "读取失败");
        desktopFixture.ReadFailure = null;
        desktopFixture.State = new(true, [], true);
        result = (await Refresh(onlyClaude)).Single();
        Check("Unreadable Desktop login is not reported as missing installation", !result.LiveQuota && result.StatusLabel == "读取失败" && result.Status.Contains("桌面版"));
        desktopFixture.State = new(true, []);
        result = (await Refresh(onlyClaude)).Single();
        Check("Installed Desktop without valid cache prompts original app login", result.StatusLabel == "等待登录" && !result.LiveQuota);

        desktopFixture.State = new(true, desktopCredentials);
        var historyPath = Path.Combine(classicProfile, "plan-usage-history.json");
        string History(DateTimeOffset time, int fiveHour, int sevenDay) => JsonSerializer.Serialize(new { version = 2, samples = new[] {
            new { t = time.AddMinutes(-30).ToUnixTimeMilliseconds(), org = "test", u = new { fh = 60, sd = 50 } },
            new { t = time.ToUnixTimeMilliseconds(), org = "test", u = new { fh = fiveHour, sd = sevenDay } } } });
        Check("Desktop usage history yields its newest sample", ClaudeUsageHistory.Parse(Json(History(now, 37, 7))) is { FiveHour: 37, SevenDay: 7 } parsedSample && parsedSample.Time == DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()));
        quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.TooManyRequests);
        File.WriteAllText(historyPath, History(usageClock.AddMinutes(2), 37, 7));
        result = (await Refresh(onlyClaude)).Single();
        Check("Rate limiting without any earlier reading falls back to the desktop application's local history", result.LiveQuota && result.Source.Contains("本地用量记录") &&
            result.Quotas.Single(q => q.Minutes == 300).UsedPercent == 37 && result.Quotas.Single(q => q.Minutes == 10080).UsedPercent == 7 && result.Status.Contains("桌面版") && result.Plan == "Max 5x");
        quotaHandler.Override = null;
        result = (await Refresh(onlyClaude)).Single();
        Check("A successful account reading replaces the local history sample", result.LiveQuota && result.Source.Contains("账户配额") && result.Quotas.Single(q => q.Minutes == 300).UsedPercent == 9);
        var readingTime = result.QuotaTime;
        quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.TooManyRequests);
        File.WriteAllText(historyPath, History(usageClock.AddMinutes(2), 41, 8));
        result = (await Refresh(onlyClaude)).Single();
        Check("A newer local sample wins over the stored reading and keeps the reset time still ahead", result.Quotas.Single(q => q.Minutes == 300).UsedPercent == 41 &&
            result.Quotas.Single(q => q.Minutes == 10080).ResetAt is not null && result.QuotaTime > readingTime);
        File.Delete(historyPath);

    }
    var failingDesktop = new FailingDesktopSources();
    using (var service = new UsageService(failingDesktop))
    {
        var onlyAntigravity = Only(ProviderId.Antigravity);
        var failed = (await service.RefreshAsync(onlyAntigravity)).Single();
        Check("Process discovery failure is not mislabeled as signed out", failed.StatusLabel == "检测失败" && !failed.Status.Contains("登录", StringComparison.Ordinal));
        failingDesktop.Fail = false;
        var recovered = (await service.RefreshAsync(onlyAntigravity)).Single();
        Check("Discovery errors do not prevent subsequent refresh", recovered.StatusLabel == "服务未就绪");
    }
    var quotas = QuotaParser.Codex(Json("""
        {"rate_limit":{"primary_window":{"used_percent":16,"limit_window_seconds":604800,"reset_after_seconds":3600}}}
        """), now, true);
    Check("Codex primary can be weekly", quotas.Single().Label == "每周额度");
    Check("Codex reset relative", Math.Abs((quotas[0].ResetAt!.Value - now).TotalSeconds - 3600) < .1);
    var plusQuotas = QuotaParser.Codex(Json("""
        {"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":42,"limit_window_seconds":18000,"reset_after_seconds":1800},"secondary_window":{"used_percent":16,"limit_window_seconds":604800,"reset_after_seconds":3600}}}
        """), now, true);
    Check("Codex Plus retains both five-hour and weekly windows", plusQuotas.Count == 2 &&
        plusQuotas.Single(x => x.Minutes == 300) is { UsedPercent: 42, Label: "5 小时额度" } &&
        plusQuotas.Single(x => x.Minutes == 10080) is { UsedPercent: 16, Label: "每周额度" });
    var localQuotas = QuotaParser.Codex(Json("""
        {"primary":{"used_percent":16,"window_minutes":10080},"secondary":{"used_percent":42,"window_minutes":300}}
        """), now, false);
    Check("Codex local windows use duration rather than primary/secondary position", localQuotas.Count == 2 &&
        localQuotas.Single(x => x.Minutes == 300).UsedPercent == 42 && localQuotas.Single(x => x.Minutes == 10080).UsedPercent == 16);
    var weeklyOnly = QuotaParser.Codex(Json("""
        {"plan_type":"pro","rate_limit":{"primary_window":null,"secondary_window":{"used_percent":26,"limit_window_seconds":604800}}}
        """), now, true);
    Check("Codex weekly-only account does not invent a five-hour window", weeklyOnly.Count == 1 && weeklyOnly[0].Minutes == 10080);
    Check("Missing quota stays unknown", QuotaParser.Codex(Json("{}"), now, true).Count == 0);
    Check("Zero quota is known", QuotaParser.Codex(Json("""{"primary":{"used_percent":0,"window_minutes":300}}"""), now, false).Single().Remaining == 100);
    Check("Claude fractional reset", QuotaParser.Claude(Json("""{"five_hour":{"utilization":25,"resets_at":"2026-09-06T12:33:44.123456Z"}}""")).Single().ResetAt is not null);
    var cursor = QuotaParser.Cursor(Json("""{"individualUsage":{"plan":{"autoPercentUsed":"12.5","apiPercentUsed":20}},"billingCycleEnd":"1789000000000"}"""));
    Check("Cursor fractional percent preserved", cursor.Count == 2 && cursor[0].UsedPercent == 12.5 && cursor[0].ResetAt is not null);
    Check("Antigravity missing fraction is unknown", QuotaParser.Antigravity(Json("""{"userStatus":{"cascadeModelConfigData":{"clientModelConfigs":[{"label":"A","quotaInfo":{}}]}}}""")).Count == 0);
    Check("Antigravity zero fraction means depleted", QuotaParser.Antigravity(Json("""{"groups":[{"buckets":[{"bucketId":"gemini-5h","remainingFraction":0}]}]}""")).Single().Remaining == 0);
    Check("Cursor free membership is displayed as a real plan", PlanParser.Cursor(Json("""{"membershipType":"free"}""")) == "free");
    Check("Cursor quota alone does not imply a paid plan", PlanParser.Cursor(Json("""{"planUsage":{"totalPercentUsed":0}}""")) == "");
    Check("Antigravity uses the named user tier before its service plan", PlanParser.Antigravity(Json("""{"userStatus":{"userTier":{"name":"Google AI Pro"},"planStatus":{"planInfo":{"planName":"Pro"}}}}""")) == "Google AI Pro");
    Check("Antigravity falls back to its explicit service plan", PlanParser.Antigravity(Json("""{"userStatus":{"planStatus":{"planInfo":{"planName":"Pro"}}}}""")) == "Pro");
    Check("Missing Antigravity subscription is not inferred from quota", PlanParser.Antigravity(Json("""{"groups":[{"buckets":[{"bucketId":"gemini-weekly","remainingFraction":1}]}]}""")) == "");
    Check("Claude local account tier preserves Max multiplier", PlanParser.ClaudeProfile(Json("""{"oauthAccount":{"organizationRateLimitTier":"default_claude_max_5x"}}""")) == "Max 5x");
    Check("Unknown Claude profile does not invent a plan", PlanParser.ClaudeProfile(Json("""{"oauthAccount":{"organizationRateLimitTier":"unknown"}}""")) == "");
    var eventItem = UsageService.ParseCursorEvent(Json("""{"timestamp":"1789000000000","model":"composer-2.5","tokenUsage":{"inputTokens":100,"cacheReadTokens":900,"cacheWriteTokens":20,"outputTokens":30,"totalCents":3.45131}}"""));
    Check("Cursor token categories are disjoint", eventItem!.Total == 1050);
    Check("Cursor API cost cents converted", pricing.Estimate(eventItem!) == .0345131m);
    Check("Reported cost is not marked as an estimate", pricing.Summarize([eventItem!]) is { Reported: 1, Estimated: 0, IsEstimate: false });

    var entry = new TokenEntry("test", now, "claude-fable-5-1", 1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000);
    Check("Cache read and both cache durations priced", pricing.Estimate(entry) == 92.75m);
    Check("Unknown model unpriced", pricing.Estimate(entry with { Model = "unreleased-unknown" }) is null);
    Check("Model variant not assigned base price", pricing.Find("gpt-6-astra-pro") is null);
    Check("Dated model matches exact base", pricing.Find("claude-sonnet-4-5-20250929")?.Prices.Input == .000003m);
    Check("Unknown cache price is not invented", pricing.Estimate(entry with { Model = "claude-sonnet-4-5" }) is null);
    Check("Token calculation is marked as an estimate", pricing.Summarize([entry]) is { Estimated: 1, IsEstimate: true });
    Check("Mixed known and unknown costs retain incompleteness", pricing.Summarize([entry, entry with { Model = "unknown" }]) is { Estimated: 1, Unpriced: 1 });
    var longContext = new TokenEntry("long", now, "gpt-6-astra", 300_000, 0, 0, 0, 100);
    Check("Remote long-context tier used", pricing.Estimate(longContext) == 6.0075m);
    Check("Missing one-hour cache price stays unknown", pricing.Estimate(longContext with { CacheWriteHour = 10 }) is null);
    Check("Zero-cost models remain valid", pricing.Estimate(longContext with { Model = "free-model:free" }) == 0);
    Check("Free suffix never substituted for a paid model", pricing.Find("free-model") is null);

    priceTime = priceTime.AddHours(23);
    handler.Json = catalogJson.Replace("0.000003", "0.000004", StringComparison.Ordinal).Replace("vendor/dynamic-price", "vendor/new-catalog-model", StringComparison.Ordinal).Replace("\"-1\"", "\"0.000001\"", StringComparison.Ordinal);
    await pricing.RefreshAsync();
    Check("No hourly catalog requests before the daily interval", handler.Calls == 2 && !pricing.IsStale);
    priceTime = priceTime.AddMinutes(59).AddSeconds(59);
    await pricing.RefreshAsync();
    Check("Daily catalog request waits for the full 24 hours", handler.Calls == 2 && !pricing.IsStale);
    priceTime = priceTime.AddSeconds(1);
    await pricing.RefreshAsync();
    Check("Price changes update without a new build", handler.Calls == 3 && pricing.Find("claude-sonnet-4-5")?.Prices.Input == .000004m);
    Check("New remote models require no local list update", pricing.ModelCount == 5 && pricing.Find("new-catalog-model") is not null);
    handler.Fail = true;
    await pricing.RefreshAsync(true);
    Check("Network failure keeps last successful catalog", pricing.ModelCount == 5 && pricing.LastError is not null && pricing.Find("claude-sonnet-4-5")?.Prices.Input == .000004m);
    await pricing.RefreshAsync();
    Check("Network failure does not trigger an immediate catalog retry", handler.Calls == 4);
    priceTime = priceTime.AddMinutes(6); handler.Fail = false;
    await pricing.RefreshAsync();
    Check("Network failure does not add a five-minute catalog retry", handler.Calls == 4 && pricing.LastError is not null);
    priceTime = priceTime.AddHours(23).AddMinutes(54);
    await pricing.RefreshAsync();
    Check("Next daily catalog refresh recovers", handler.Calls == 5 && pricing.LastError is null);
    var cacheBefore = File.ReadAllText(Path.Combine(priceDirectory, "model-prices.json"));
    handler.Json = "{\"data\":[]}";
    await pricing.RefreshAsync(true);
    Check("Empty remote response cannot erase valid prices", pricing.ModelCount == 5 && File.ReadAllText(Path.Combine(priceDirectory, "model-prices.json")) == cacheBefore);
    using (var missing = new Pricing(Path.Combine(root, "no-cache"), priceClient, () => priceTime))
    {
        await missing.RefreshAsync();
        Check("Offline first start has no invented prices", missing.ModelCount == 0 && missing.Estimate(entry) is null && missing.LastError is not null);
        Check("Reported costs do not depend on catalog availability", missing.Estimate(eventItem!) == .0345131m);
    }

    var codexRoot = Path.Combine(root, "codex"); var sessions = Path.Combine(codexRoot, "sessions"); Directory.CreateDirectory(sessions);
    string Event(DateTimeOffset timestamp, long total, long input, long cache, long output) => JsonSerializer.Serialize(new
    {
        timestamp, type = "event_msg", payload = new { type = "token_count", info = new
        { total_token_usage = new { total_tokens = total }, last_token_usage = new { input_tokens = input, cached_input_tokens = cache, cache_write_input_tokens = 10, output_tokens = output, reasoning_output_tokens = 5 } },
        rate_limits = new { limit_id = "codex", primary = new { used_percent = 10, window_minutes = 10080, resets_at = now.AddDays(1).ToUnixTimeSeconds() } } }
    });
    var first2 = Event(now.AddMinutes(-5), 120, 100, 80, 20);
    var duplicate = Event(now.AddMinutes(-4), 120, 100, 80, 20);
    var second = Event(now.AddMinutes(-3), 250, 110, 90, 20);
    string context = """{"type":"turn_context","payload":{"model":"gpt-6-astra"}}""";
    var quotaOnly = JsonSerializer.Serialize(new { timestamp = now.AddMinutes(-4), type = "event_msg", payload = new { type = "token_count", info = (object?)null } });
    File.WriteAllLines(Path.Combine(sessions, "session.jsonl"), [context, first2, duplicate, quotaOnly, duplicate, second, "{\"type\":\"token_count\",broken"]);
    File.WriteAllLines(Path.Combine(sessions, "fork.jsonl"), [context, first2]);
    var logs = new LogReader();
    var parsed = logs.Read(ProviderId.Codex, codexRoot, now);
    Check("Codex duplicate snapshots and forks deduplicated", parsed.Entries.Count == 2);
    Check("Quota-only event does not reset deduplication", parsed.Entries.Sum(x => x.Output) == 40);
    Check("Codex cache input not double counted; reasoning not added", parsed.Entries.Sum(x => x.Total) == 270);
    Check("Codex model context retained", parsed.Entries.All(x => x.Model == "gpt-6-astra"));
    Check("Malformed JSONL reported, valid rows retained", parsed.UsageNote.Contains("未能读取") && parsed.Entries.Count == 2);
    Check("Cached reads stable", logs.Read(ProviderId.Codex, codexRoot, now).Entries.Count == 2);
    File.AppendAllText(Path.Combine(sessions, "session.jsonl"), Environment.NewLine + Event(now.AddMinutes(-1), 370, 100, 80, 20));
    Check("Appended logs invalidate cache", logs.Read(ProviderId.Codex, codexRoot, now).Entries.Count == 3);
    var claudeRoot = Path.Combine(root, "claude"); var projects = Path.Combine(claudeRoot, "projects"); Directory.CreateDirectory(projects);
    string ClaudeEvent(long output) => JsonSerializer.Serialize(new { timestamp = now.AddMinutes(-1), type = "assistant", message = new { id = "same-message", model = "claude-sonnet-4-6", usage = new { input_tokens = 100, cache_read_input_tokens = 200, cache_creation_input_tokens = 30, cache_creation = new { ephemeral_1h_input_tokens = 10 }, output_tokens = output } } });
    File.WriteAllLines(Path.Combine(projects, "session.jsonl"), [ClaudeEvent(10), ClaudeEvent(40)]);
    var claude = logs.Read(ProviderId.Claude, claudeRoot, now);
    Check("Claude streamed messages use complete counters once", claude.Entries.Count == 1 && claude.Entries.Single().Total == 370);
    Check("Claude 1-hour writes separated", claude.Entries.Single().CacheWriteHour == 10 && claude.Entries.Single().CacheWrite == 20);
    Check("Missing source is unavailable, not zero", !logs.Read(ProviderId.Claude, Path.Combine(root, "missing"), now).UsageAvailable);
    Console.WriteLine($"\n{passed} checks passed.");
}

finally
{
    // A unique test-owned directory, verified before recursive cleanup.
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("BrimDeck-Tests-")) Directory.Delete(root, true);
}

sealed class CatalogHandler(string json) : HttpMessageHandler
{
    public string Json { get; set; } = json;
    public bool Fail { get; set; }
    public int Calls { get; private set; }
    public bool SawAuthorization { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++; SawAuthorization |= request.Headers.Authorization is not null;
        return Task.FromResult(new HttpResponseMessage(Fail ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK) { Content = new StringContent(Json) });
    }
}

sealed class FailingDesktopSources : IDesktopSources
{
    public bool Fail { get; set; } = true;
    public string? ReadCursorToken(string database) => null;
    public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation)
        => Fail ? Task.FromException<IReadOnlyList<LocalEndpoint>>(new System.Runtime.InteropServices.COMException()) : Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
}

sealed class ClaudeFixtureSources : IDesktopSources
{
    public ClaudeDesktopState State { get; set; } = new(false, []);
    public Exception? ReadFailure { get; set; }
    public int ReadCalls { get; private set; }
    public ClaudeDesktopState ReadClaudeDesktop(DataLocations locations)
    { ReadCalls++; if (ReadFailure is not null) throw ReadFailure; return State; }
    public string? ReadCursorToken(string database) => null;
    public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
}

sealed class ClaudeQuotaHandler : HttpMessageHandler
{
    public int Rejected { get; private set; }
    public List<string> Requests { get; } = [];
    public Func<string, CancellationToken, HttpResponseMessage?>? Override { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsoluteUri != "https://api.anthropic.com/api/oauth/usage" || !request.Headers.Contains("anthropic-beta"))
            throw new InvalidOperationException("Unexpected quota destination or headers");
        var token = request.Headers.Authorization?.Parameter ?? "";
        Requests.Add(token);
        if (Override?.Invoke(token, cancellationToken) is { } response) return Task.FromResult(response);
        if (token == "test-rejected")
        { Rejected++; return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)); }
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""
            {"five_hour":{"utilization":9,"resets_at":"2026-09-06T12:20:00Z"},"seven_day":{"utilization":4,"resets_at":"2026-09-10T18:00:00Z"}}
            """) });
    }
}
