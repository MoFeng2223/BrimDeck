using BrimDeck.Core;

static class ZCodeUsageTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var start = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.FromHours(8));
        long At(int hours) => start.AddHours(hours).ToUnixTimeMilliseconds();
        // input_tokens includes the cached part, as in ZCode's own records.
        var entries = ZCodeUsage.Parse([
            ["a", "GLM-5.3", At(1).ToString(), "25000", "200", "20000", "0"],
            ["b", "GLM-5.3", At(2).ToString(), "100", "5", "80", "50"],
            ["c", "GLM-5.3", "not a time", "1", "1", "0", "0"]]).ToList();
        check("ZCode input excludes cached reads", entries.Count == 2 && entries[0] is { Input: 5000, CacheRead: 20000, CacheWrite: 0, Output: 200, Total: 25200 });
        check("ZCode cache writes never make input negative", entries[1] is { Input: 0, CacheRead: 80, CacheWrite: 20, Output: 5 });
        check("ZCode keys are unique per request", entries[0].Key == "zcode:a" && entries[1].Key == "zcode:b");

        var home = Path.Combine(root, "zcode-home");
        Directory.CreateDirectory(Path.Combine(home, "cli", "db"));
        File.WriteAllText(ZCodeUsage.Database(home), "");
        var locations = DataLocations.Resolve(Path.Combine(root, "home"), root, root, null, null, home);
        check("ZCODE_HOME overrides the ZCode directory", locations.ZCodeHome == home &&
            DataLocations.Resolve(Path.Combine(root, "home"), root, root, null, null).ZCodeHome == Path.Combine(root, "home", ".zcode"));
        var desktop = new Rows([["a", "GLM-5.3", At(1).ToString(), "25000", "200", "20000", "0"]]);
        using var service = new UsageService(desktop, locations, clock: () => start.AddDays(1));
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Claude, UsageSource = ProviderId.ZCode }] };
        var snapshot = (await service.RefreshAsync(settings, usageStart: start.LocalDateTime)).Single(s => s.Id == ProviderId.ZCode);
        check("ZCode statistics come from its request table", snapshot.UsageAvailable && snapshot.UsageComplete && snapshot.Entries.Count == 1 &&
            desktop.Sql.Contains("FROM model_usage") && desktop.Sql.Contains(start.ToUnixTimeMilliseconds().ToString()));
        check("ZCode without a signed-in Coding Plan asks the user to sign in", snapshot.StatusLabel == "未连接");

        // Credentials written in ZCode's format decrypt with the same key derivation.
        var key = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("fixture-secret"));
        string Encrypt(string value)
        {
            var iv = new byte[12]; var tag = new byte[16]; var plain = System.Text.Encoding.UTF8.GetBytes(value); var data = new byte[plain.Length];
            using var aes = new System.Security.Cryptography.AesGcm(key, 16); aes.Encrypt(iv, plain, data, tag);
            string B(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "enc:v1:" + B(iv) + "." + B(tag) + "." + B(data);
        }
        Directory.CreateDirectory(Path.Combine(home, "v2"));
        void Store(Dictionary<string, string> values) => File.WriteAllText(ZCodeUsage.Credentials(home), System.Text.Json.JsonSerializer.Serialize(values));
        // A plan key left over from the previously signed-in region must not be used for the active one.
        const string old = "account-provider:coding-plan:account:bigmodel-individual-coding-plan:account:2:api-key";
        Store(new()
        {
            ["oauth:active_provider"] = Encrypt("zai"), ["zcodejwttoken"] = Encrypt("jwt"), ["oauth:zai:access_token"] = Encrypt("access"),
            [old] = Encrypt("old-key"), ["account-provider:coding-plan:account:zai-individual-coding-plan:account:1:api-key"] = Encrypt("plan-key")
        });
        check("ZCode credentials decrypt to the active region, its plan key and MCP tokens",
            ZCodeUsage.ReadAccount(home, "fixture-secret") == new ZCodeAccount("zai", "plan-key", "jwt", "access"));
        Store(new() { ["oauth:active_provider"] = Encrypt("zai"), [old] = Encrypt("old-key") });
        check("ZCode never queries the active region with another region's leftover key", ZCodeUsage.ReadAccount(home, "fixture-secret") is { Family: "zai", ApiKey: "" });
        Store(new() { [old] = Encrypt("old-key") });
        check("Without an active region the stored key decides the region", ZCodeUsage.ReadAccount(home, "fixture-secret") is { Family: "bigmodel", ApiKey: "old-key" });
        var mcp = ZCodeUsage.McpMetric(System.Text.Json.JsonDocument.Parse("""{"code":0,"data":{"server_time":1790200000,"next_refresh_at":1790208000,"level":"lite","total_usage":{"used":12,"limit":1000,"remaining":988}}}""").RootElement);
        check("ZCode MCP is a daily count with its refresh time", mcp is { Kind: MetricKind.Count, Label: "ZCode MCP", Used: 12, Total: 1000 } && mcp.ResetAt == DateTimeOffset.FromUnixTimeSeconds(1790208000));
        // A Start Plan with a one-time grant, a daily allowance and an ended plan.
        var startNow = DateTimeOffset.FromUnixTimeSeconds(1790200000);
        var startPlan = ZCodeUsage.StartPlan(System.Text.Json.JsonDocument.Parse("""
            {"code":0,"msg":"","data":{"server_time":1790200000,
              "plans":[
                {"user_plan_id":"upl_a","plan_id":"start-plan-example","name":"Example Plan","status":"active","starts_at":1790100000,"ends_at":1790500000,
                 "entitlements":[{"entitlement_id":"ent-a","show_name":"GLM-5.3-Flash","unit_type":"token","grant_units":1000000,"period":"one_time"}]},
                {"user_plan_id":"upl_b","name":"Daily","status":"active","ends_at":1791000000,"entitlements":[{"entitlement_id":"ent-b","period":"daily"}]},
                {"user_plan_id":"upl_c","name":"Old","status":"expired","ends_at":1790000000,"entitlements":[{"entitlement_id":"ent-c","period":"one_time"}]}],
              "balances":[
                {"user_plan_id":"upl_a","entitlement_id":"ent-a","show_name":"GLM-5.3-Flash","unit_type":"token","total_units":1000000,"used_units":25000,"remaining_units":975000,"period_end":1790500000,"expires_at":1790500000},
                {"user_plan_id":"upl_b","entitlement_id":"ent-b","show_name":"GLM-5.3","unit_type":"token","total_units":1000,"remaining_units":400,"period_end":1790280000,"expires_at":1791000000},
                {"user_plan_id":"upl_c","entitlement_id":"ent-c","show_name":"Old","unit_type":"token","total_units":10,"used_units":1,"expires_at":1790000000}]}}
            """).RootElement, startNow);
        check("ZCode Start Plan shows each active grant with its expiry and skips ended plans", startPlan.Plan == "Example Plan" && startPlan.Metrics.Count == 2 &&
            startPlan.Metrics[0] is { Kind: MetricKind.Count, Label: "GLM-5.3-Flash", Used: 25000, Total: 1000000, Unit: "令牌", ResetAt: null } &&
            startPlan.Metrics[0].ExpiresAt == DateTimeOffset.FromUnixTimeSeconds(1790500000));
        check("Recurring Start Plan allowances reset at the end of their period", startPlan.Metrics[1] is { Used: 600, Total: 1000 } &&
            startPlan.Metrics[1].ResetAt == DateTimeOffset.FromUnixTimeSeconds(1790280000));
        // A slow ZCode response is not awaited past the refresh, finishes in the background and is used afterwards.
        var latest = new LatestResponse(); int loads = 0;
        var slow = new TaskCompletionSource();
        bool Accept(System.Text.Json.JsonElement root) => root.GetProperty("code").GetInt32() == 0;
        System.Text.Json.JsonElement Json(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        Func<CancellationToken, Task<System.Text.Json.JsonElement>> load = async _ => { loads++; await slow.Task; return Json("""{"code":0,"n":1}"""); };
        using (var shortWait = new CancellationTokenSource(50))
            check("A slow ZCode response does not hold up the first refresh", await latest.GetAsync("a", load, Accept, shortWait.Token) is null);
        slow.SetResult(); await Task.Delay(20);
        check("The finished response is used by the next refresh", await latest.GetAsync("a", load, Accept, new CancellationToken(true)) is not null && loads == 2);
        check("A failed ZCode read keeps showing the last good result",
            await latest.GetAsync("a", _ => throw new HttpRequestException(), Accept, default) is { } failed && failed.GetProperty("n").GetInt32() == 1);
        check("An error response never replaces the last good result",
            await latest.GetAsync("a", _ => Task.FromResult(Json("""{"code":3001}""")), Accept, default) is { } error && error.GetProperty("code").GetInt32() == 0);
        var pending = new TaskCompletionSource<System.Text.Json.JsonElement>();
        check("A refresh with a kept result does not wait for a slow response",
            await latest.GetAsync("a", _ => pending.Task, Accept, default) is { } kept && kept.GetProperty("n").GetInt32() == 1);
        pending.SetResult(Json("""{"code":0,"n":2}""")); await Task.Delay(20);
        check("The newer response replaces the kept one once it arrives", await latest.GetAsync("a", _ => throw new HttpRequestException(), Accept, default) is { } newer && newer.GetProperty("n").GetInt32() == 2);
        check("Another ZCode account never sees the kept result", await latest.GetAsync("b", _ => throw new HttpRequestException(), Accept, default) is null);
        desktop.Result = null;
        snapshot = (await service.RefreshAsync(settings, usageStart: start.LocalDateTime)).Single(s => s.Id == ProviderId.ZCode);
        check("An unreadable ZCode database is reported as incomplete", !snapshot.UsageComplete && snapshot.Entries.Count == 0);
    }

    private sealed class Rows(IReadOnlyList<string?[]> rows) : IDesktopSources
    {
        public IReadOnlyList<string?[]>? Result { get; set; } = rows;
        public string Sql { get; private set; } = "";
        public IReadOnlyList<string?[]>? QueryDatabase(string database, string sql) { Sql = sql; return Result; }
        public string? ReadCursorToken(string database) => null;
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
    }
}
