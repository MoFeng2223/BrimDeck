using System.Net;
using System.Text.Json;
using BrimDeck.Core;

static class WorkBuddyUsageTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var home = Path.Combine(root, "workbuddy-home");
        var folder = Path.Combine(home, "projects", "c-Users-test-WorkBuddy-2026-11-13");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "session.jsonl");
        var now = DateTimeOffset.Now;
        var time = now.AddMinutes(-1).ToUnixTimeMilliseconds();
        // The shape WorkBuddy 5.9.3 writes: rawUsage in the OpenAI style, with the cache reads inside prompt_tokens.
        string Reply(string messageId, int step, string model = "hy4-free", long? at = null) => JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString(), timestamp = at ?? time, type = "message", role = "assistant",
            providerData = new
            {
                messageId, stepSeq = step, model, requestModelId = "hy4free", requestModelName = "Hy4",
                rawUsage = new
                {
                    prompt_tokens = 31505, completion_tokens = 1103, total_tokens = 32608, cached_tokens = 0,
                    prompt_tokens_details = new { cached_tokens = 13312 }, prompt_cache_hit_tokens = 13312, prompt_cache_miss_tokens = 18193,
                    cache_read_input_tokens = 0, cache_creation_input_tokens = 0, credit = 0, original_credit = 3.26,
                    quota = new { model = "hy4", percent = 0, reset_at = "1794657272" }
                },
                usage = new { requests = 1, inputTokens = 31505, outputTokens = 1103, inputTokensDetails = new[] { new { cached_tokens = 13312 } } }
            },
            status = "completed"
        });
        var user = JsonSerializer.Serialize(new { id = "u1", timestamp = time, type = "message", role = "user", content = new[] { new { type = "input_text", text = "usage" } } });
        var reasoning = JsonSerializer.Serialize(new { id = "r1", timestamp = time, type = "reasoning", providerData = new { messageId = "m1", stepSeq = 1, model = "hy4-free" } });
        File.WriteAllText(file, user + "\n" + reasoning + "\n" + Reply("m1", 1) + "\n" + Reply("m1", 2) + "\n");
        // A copy of the same reply in another session file is counted once.
        File.WriteAllText(Path.Combine(folder, "copy.jsonl"), Reply("m1", 1) + "\n");
        var reader = new LogReader();
        var data = reader.Read(ProviderId.WorkBuddy, home, now);
        check("WorkBuddy counts each reply step once across files", data.Entries.Count == 2);
        var first = data.Entries.First();
        check("WorkBuddy separates cache reads from prompt tokens", first.Input == 18193 && first.CacheRead == 13312 && first.CacheWrite == 0 && first.Output == 1103);
        check("WorkBuddy shows the channel name and prices by the model name", first.Model == "hy4-free" && first.PriceModel == "Hy4" && first.ReportedCostUsd is null);

        // Without rawUsage, WorkBuddy's own usage summary gives the same counts.
        var summaryOnly = JsonSerializer.Serialize(new
        {
            id = "s1", timestamp = time, type = "message", role = "assistant",
            providerData = new { messageId = "m2", stepSeq = 1, model = "glm-5.2", usage = new { inputTokens = 500, outputTokens = 40, inputTokensDetails = new[] { new { cached_tokens = 200 } } } }
        });
        File.AppendAllText(file, summaryOnly + "\n");
        data = reader.Read(ProviderId.WorkBuddy, home, now);
        var summary = data.Entries.Single(entry => entry.Model == "glm-5.2");
        check("WorkBuddy falls back to its usage summary", summary.Input == 300 && summary.CacheRead == 200 && summary.Output == 40);
        check("WorkBuddy respects the date cutoff", reader.Read(ProviderId.WorkBuddy, home, now, now.LocalDateTime.Date.AddDays(1)).Entries.Count == 0);
        check("WorkBuddy missing folder is unavailable", !reader.Read(ProviderId.WorkBuddy, Path.Combine(root, "no-workbuddy"), now).UsageAvailable);
        check("WorkBuddy is both a quota source and a usage app", ProviderCatalog.IsUsageSource(ProviderId.WorkBuddy) &&
            ProviderCatalog.Sources.Contains(ProviderId.WorkBuddy) && ProviderCatalog.IsBuiltIn(ProviderId.WorkBuddy));
        check("WorkBuddy data folder is .workbuddy in the user profile",
            DataLocations.Resolve(Path.Combine(root, "profile"), root, root, null, null).WorkBuddyHome == Path.Combine(root, "profile", ".workbuddy"));

        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.GlmChina, UsageSource = ProviderId.WorkBuddy }] };
        settings.Normalize();
        check("Normalizing settings keeps the WorkBuddy usage app", settings.Apps.Single() is { UsageSource: ProviderId.WorkBuddy } && settings.RequiredProviders.Contains(ProviderId.WorkBuddy));
        using var handler = new NoNetwork();
        using var http = new HttpClient(handler);
        using var service = new UsageService(new Desktop(), new DataLocations("", "", "", "", "") { WorkBuddyHome = home }, http, () => now);
        var snapshot = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.WorkBuddy);
        check("WorkBuddy without a sign-in reads usage only, with no HTTP", handler.Calls == 0 && snapshot.UsageAvailable && snapshot.Entries.Count == 3
            && snapshot.Metrics.Count == 0 && !snapshot.LiveQuota && snapshot.StatusLabel == Loc.T("未连接", "Not connected"));

        CheckPrices(root, check);
        CheckQuota(check);
        await CheckReadingAsync(check);
    }

    private static void CheckPrices(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "workbuddy-prices");
        Directory.CreateDirectory(folder);
        TokenPrices Price(int n) => new(n * .000001m, n * .000005m, n * .0000001m, null, null);
        File.WriteAllText(Path.Combine(folder, "model-prices.json"), JsonSerializer.Serialize(new
        {
            updatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            models = new[] { new ModelPrice("tencent/hy4", Price(1), []), new ModelPrice("z-ai/glm-5.2", Price(2), []) }
        }));
        using var pricing = new Pricing(folder);
        var entry = new TokenEntry("wb:test", DateTimeOffset.Now, "hy4-free", 1000, 2000, 0, 0, 100);
        decimal Cost(int n) { var p = Price(n); return 1000 * p.Input + 100 * p.Output + 2000 * p.CacheRead!.Value; }
        check("WorkBuddy records are priced by the model name they keep", pricing.Estimate(entry with { PriceModel = "Hy4" }) == Cost(1));
        check("Without that name the channel name stays unpriced", pricing.Estimate(entry) is null);
        check("A model name the catalog lacks falls back to the shown name",
            pricing.Estimate(entry with { Model = "glm-5.2", PriceModel = "GLM-5.2 (WorkBuddy)" }) == Cost(2));
    }

    // The summary WorkBuddy returned for a trial account (体验版), as the website answered it, with some credits spent and a credit
    // pack (TCACA_code_009) added.
    private const string TrialSummary = """
        {"code":0,"msg":"OK","data":{"Packages":[{"PackageCode":"TCACA_code_008_cfWoLwvjU4","CycleTotalCapacity":"500","CycleRemainCapacity":"420","CycleUsedCapacity":"80","CapacityUnit":"credits"},
           {"PackageCode":"TCACA_code_009_0XmEQc2xOf","CycleTotalCapacity":"200","CycleRemainCapacity":"200","CycleUsedCapacity":"0","CapacityUnit":"credits"}],
         "TotalCredits":"500","SubscriptionPackageName":"体验版","IsPaidUser":false,
         "CreditGroups":[{"Kind":"plan_base","Name":"套餐基础积分","CycleTotalCapacity":"500","CycleRemainCapacity":"420","CycleUsedCapacity":"80"},
           {"Kind":"plan_bonus","Name":"套餐赠送积分","CycleTotalCapacity":"0","CycleRemainCapacity":"0","CycleUsedCapacity":"0"},
           {"Kind":"addon","Name":"购买积分","CycleTotalCapacity":"200","CycleRemainCapacity":"200","CycleUsedCapacity":"0"},
           {"Kind":"benefit","Name":"平台奖励积分","CycleTotalCapacity":"0","CycleRemainCapacity":"0","CycleUsedCapacity":"0"}]}}
        """;

    private static void CheckQuota(Action<string, bool> check)
    {
        using var doc = JsonDocument.Parse(TrialSummary);
        var reset = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var expires = new DateTimeOffset(2027, 1, 15, 10, 0, 0, TimeSpan.FromHours(8));
        var reading = WorkBuddyUsage.Parse(doc.RootElement.Get("data"), new Dictionary<string, WorkBuddyUsage.KindTime>
            { ["plan_base"] = new(reset, null), ["addon"] = new(null, expires) });
        check("WorkBuddy shows each kind of credits the account has, each on its own", reading.Metrics.Select(m => m.Label).SequenceEqual(["套餐积分", "购买积分"]));
        var plan = reading.Metrics[0];
        check("WorkBuddy plan credits count used against the total and show their reset; bought credits show their expiry",
            plan is { Kind: MetricKind.Count, Total: 500, Used: 80, Unit: "积分", ExpiresAt: null } && plan.ResetAt == reset
            && reading.Metrics[1] is { ResetAt: null } bought && bought.ExpiresAt == expires && reading.Plan == "体验版");
        check("WorkBuddy cycle ends are China Standard Time", WorkBuddyUsage.CycleEnd("2026-11-30 23:59:59") == new DateTimeOffset(2026, 11, 30, 23, 59, 59, TimeSpan.FromHours(8))
            && WorkBuddyUsage.CycleEnd("") is null && WorkBuddyUsage.CycleEnd("1797094799000") is null);
        using var packages = JsonDocument.Parse("""{"Packages":[{"CycleTotalCapacity":"300","CycleRemainCapacity":"100"},{"CycleTotalCapacity":200,"CycleRemainCapacity":200}]}""");
        var summed = WorkBuddyUsage.Parse(packages.RootElement);
        check("WorkBuddy adds up the packages without credit groups", summed.Metrics.Single() is { Total: 500, Used: 200 });
        check("WorkBuddy accepts only its own login page for the state", WorkBuddyUsage.LoginUrlMatches("https://www.workbuddy.cn/login?platform=brimdeck&state=abc", "abc")
            && !WorkBuddyUsage.LoginUrlMatches("https://www.workbuddy.cn/login?state=other", "abc") && !WorkBuddyUsage.LoginUrlMatches("https://example.com/login?state=abc", "abc")
            && !WorkBuddyUsage.LoginUrlMatches("http://www.workbuddy.cn/login?state=abc", "abc"));
        check("WorkBuddy sign-in survives saving", WorkBuddyUsage.Session.Parse(new WorkBuddyUsage.Session("a", "r", "u", "www.workbuddy.cn").Serialize()) is { AccessToken: "a", RefreshToken: "r", Uid: "u" }
            && WorkBuddyUsage.Session.Parse("") is null && WorkBuddyUsage.Session.Parse("{broken") is null);
    }

    // A refused token is renewed, the renewed sign-in saved, and the credits read again with it. The plan's credits show when the
    // plan's cycle ends, and the credit packs when the first one still in use expires; their own cycle and an expired pack are
    // passed over.
    private static async Task CheckReadingAsync(Action<string, bool> check)
    {
        var asked = new List<string>();
        var bodies = new List<string>();
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
        string End(int days) => now.AddDays(days).ToString("yyyy-MM-dd") + " 23:59:59";
        long Valid(int days) => now.AddDays(days).ToUnixTimeMilliseconds();
        using var handler = new Site(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            asked.Add(path);
            if (request.Content is { } content) bodies.Add(content.ReadAsStringAsync().Result);
            var token = request.Headers.Authorization?.Parameter;
            if (path == "/v2/plugin/auth/token/refresh")
                return request.Headers.GetValues("X-Refresh-Token").Single() switch
                {
                    "old-refresh" => (HttpStatusCode.OK, """{"code":0,"data":{"accessToken":"new-token","refreshToken":"new-refresh","domain":"www.workbuddy.cn"}}"""),
                    "forbidden-refresh" => (HttpStatusCode.Forbidden, "{}"),
                    "bad-request-refresh" => (HttpStatusCode.BadRequest, """{"code":10001,"msg":"10001:refreshToken is empty"}"""),
                    _ => (HttpStatusCode.Unauthorized, """{"code":12153,"msg":"12153:refresh token failed"}""")
                };
            if (token != "new-token") return (HttpStatusCode.Unauthorized, "{}");
            if (request.Headers.GetValues("X-User-Id").Single() != "uid-1") return (HttpStatusCode.BadRequest, "{}");
            return path switch
            {
                "/billing/meter/get-user-resource-summary" => (HttpStatusCode.OK, TrialSummary),
                "/billing/meter/get-user-resource-free-packages" => (HttpStatusCode.OK, "{\"code\":0,\"data\":{\"Accounts\":[{\"PackageCode\":\"TCACA_code_008_cfWoLwvjU4\",\"CycleEndTime\":\"" + End(17)
                    + "\",\"DeductionEndTime\":" + Valid(3000) + "}],\"TotalCount\":1}}"),
                "/billing/meter/get-user-resource-paid-packages" => (HttpStatusCode.OK, "{\"code\":0,\"data\":{\"Accounts\":["
                    + "{\"PackageCode\":\"TCACA_code_009_0XmEQc2xOf\",\"CycleEndTime\":\"" + End(3) + "\",\"DeductionEndTime\":" + Valid(-2) + "},"
                    + "{\"PackageCode\":\"TCACA_code_009_0XmEQc2xOf\",\"CycleEndTime\":\"" + End(3) + "\",\"DeductionEndTime\":" + Valid(40) + "},"
                    + "{\"PackageCode\":\"TCACA_code_009_0XmEQc2xOf\",\"CycleEndTime\":\"" + End(3) + "\",\"DeductionEndTime\":" + Valid(90) + "}],\"TotalCount\":3}}"),
                _ => (HttpStatusCode.NotFound, "{}")
            };
        });
        using var http = new HttpClient(handler);
        WorkBuddyUsage.Session? saved = null;
        var reading = await WorkBuddyUsage.ReadAsync(http, new("old-token", "old-refresh", "uid-1", "www.workbuddy.cn"), session => saved = session, CancellationToken.None);
        check("WorkBuddy renews a refused sign-in and saves it before reading again", saved is { AccessToken: "new-token", RefreshToken: "new-refresh", Uid: "uid-1" } && reading.Metrics.Count == 2);
        var plan = reading.Metrics.Single(m => m.Label == "套餐积分");
        check("WorkBuddy plan credits reset one second after the plan's own cycle ends", plan.ResetAt is { } at && at.Offset == TimeSpan.FromHours(8)
            && at.ToString("yyyy-MM-dd HH:mm:ss") == now.AddDays(18).ToString("yyyy-MM-dd") + " 00:00:00");
        check("WorkBuddy plan credits show no expiry", plan.ExpiresAt is null);
        var packs = reading.Metrics.Single(m => m.Label == "购买积分");
        check("WorkBuddy credit packs show when the first pack still in use expires, and no reset", packs.ResetAt is null
            && packs.ExpiresAt == DateTimeOffset.FromUnixTimeMilliseconds(Valid(40)));
        check("WorkBuddy asks about the packages the summary lists", bodies.Where(body => body.Contains("PackageCodes")).All(body => body.Contains("TCACA_code_008") && body.Contains("TCACA_code_009")));
        async Task<string> Renewal(string refresh)
        {
            try { await WorkBuddyUsage.ReadAsync(http, new("old-token", refresh, "uid-1", ""), _ => { }, CancellationToken.None); return "read"; }
            catch (WorkBuddyUsage.RejectedException) { return "rejected"; }
            catch (HttpRequestException ex) { return "failed " + (int?)ex.StatusCode; }
        }
        check("WorkBuddy reports a sign-in that cannot be renewed, on 401 and 403 only",
            await Renewal("bad-refresh") == "rejected" && await Renewal("forbidden-refresh") == "rejected" && await Renewal("bad-request-refresh") == "failed 400");
    }

    private sealed class Site(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
    }
    private sealed class Desktop : IDesktopSources
    {
        public string? ReadCursorToken(string database) => throw new InvalidOperationException("Unexpected credential read");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => throw new InvalidOperationException("Unexpected service discovery");
    }
}
