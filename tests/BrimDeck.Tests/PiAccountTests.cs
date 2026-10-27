using System.Net;
using System.Text.Json;
using BrimDeck.Core;

static class PiAccountTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "pi-account-pool");
        Directory.CreateDirectory(folder);
        var pool = Path.Combine(folder, "antigravity-accounts.json");
        var auth = Path.Combine(folder, "auth.json");
        var oauthDir = Path.Combine(folder, "npm", "node_modules", "pi-antigravity", "src", "auth");
        Directory.CreateDirectory(oauthDir);
        File.WriteAllText(Path.Combine(oauthDir, "oauth.ts"),
            "export const CLIENT_ID = atob(\"" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("synthetic-client-id")) + "\");\n" +
            "export const CLIENT_SECRET = atob(\"" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("synthetic-client-secret")) + "\");\n");
        var now = DateTimeOffset.Now;
        object Account(string access, string refresh, bool expired, int added) => new { access, refresh, expires = (expired ? now.AddHours(-1) : now.AddHours(1)).ToUnixTimeMilliseconds(), addedAt = added };
        var data = new { version = 1, activeAccountId = "four", accounts = new Dictionary<string, object> {
            ["one"] = Account("old1", "refresh1", true, 1), ["two"] = Account("old2", "refresh2", true, 2),
            ["three"] = Account("access3", "refresh3", false, 3), ["four"] = Account("old4", "revoked", true, 4)
        } };
        File.WriteAllText(pool, JsonSerializer.Serialize(data));
        File.WriteAllText(auth, "{\"unrelated\":\"must stay unchanged\"}");
        var original = File.ReadAllBytes(pool);
        var authOriginal = File.ReadAllBytes(auth);
        var listed = PiAntigravityAccounts.List(pool, now);
        check("Pi account pool lists four anonymous stable selectors", listed.Count == 4 && listed[3].Active && listed.Take(3).All(a => a.Key.Length == 64) && !listed[0].Label.Contains("one"));
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var reader = new PiAntigravityAccounts();
        async Task<ProviderSnapshot> Read(int index, bool allowed = true, bool renew = true)
        {
            var snapshot = new ProviderSnapshot(ProviderId.Pi);
            await reader.ReadAsync(snapshot, pool, listed[index].Key, allowed, renew, http, now);
            return snapshot;
        }
        await Read(0, false);
        check("Pi pool does not query or renew without sign-in consent", handler.Calls == 0);
        var blocked = await Read(0, true, false);
        check("Pi pool renewal has separate opt-in", !blocked.LiveQuota && handler.Calls == 0);
        var first = await Read(0);
        check("Pi expired account renews only with Google's official token endpoint", handler.Renewals == 1 && handler.ValidRenewalRequest && first.LiveQuota);
        check("Pi paidTier takes priority over currentTier free", first.Plan == "Google AI Pro");
        check("Pi account one quota is independent", first.Metrics.Single().ValueText == "60%");
        await Read(0);
        check("Pi renewed access is cached in memory, not renewed on each poll", handler.Renewals == 1 && handler.QuotaQueries == 2);
        var second = await Read(1);
        check("Pi different accounts have different quota values", second.Metrics.Single().ValueText == "70%" && first.Metrics.Single().ValueText == "60%");
        var third = await Read(2);
        check("Pi valid stored access requires no renewal", third.LiveQuota && handler.Renewals == 2);
        var fourth = await Read(3);
        check("Pi revoked account cannot break other accounts", !fourth.LiveQuota && (await Read(0)).LiveQuota);
        check("Pi account queries never write auth or account-pool files", File.ReadAllBytes(pool).SequenceEqual(original) && File.ReadAllBytes(auth).SequenceEqual(authOriginal));
        reader.Clear();
        await Read(0);
        check("Pi clearing memory drops renewed tokens", handler.Renewals == 4);
        var missing = new ProviderSnapshot(ProviderId.Pi);
        await reader.ReadAsync(missing, pool, new string('f', 64), true, true, http, now);
        check("Pi missing account never falls back to an unrelated active account", !missing.LiveQuota && missing.Quotas.Count == 0);

        var apps = listed.Select((a, i) => new AppEntry { QuotaSource = ProviderId.Pi, UsageSource = ProviderId.Pi, PiProvider = "antigravity", PiAccountKey = a.Key, DisplayName = "Account " + (i + 1) }).ToList();
        apps.Add(apps[0].Copy());
        var settings = new DeckSettings { Apps = apps, PiReadLogin = true, PiGoogleRefresh = true };
        using var serviceHandler = new Handler();
        using var serviceHttp = new HttpClient(serviceHandler);
        using var service = new UsageService(new Desktop(), new DataLocations("", "", "", "", "") { PiAuthFile = auth }, serviceHttp, () => now);
        var snapshots = await service.RefreshAsync(settings);
        var columns = DashboardUsage.Columns(settings, snapshots);
        check("Pi dashboard maps each account column to its own snapshot", columns[0].Quota.Metrics.Single().ValueText == "60%" && columns[1].Quota.Metrics.Single().ValueText == "70%" && !columns[3].Quota.LiveQuota);
        check("Pi duplicate columns do not duplicate account quota requests", serviceHandler.QuotaQueries == 3 && ReferenceEquals(columns[0].Quota, columns[4].Quota));
        check("Pi account columns do not fabricate per-account token usage", columns.All(c => !c.Usage.UsageAvailable) && DashboardUsage.Statistics(settings, snapshots).Count == 1);
        var serialized = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<DeckSettings>(serialized)!;
        check("Pi account selectors and renewal consent round trip", loaded.Apps[1].PiAccountKey == listed[1].Key && loaded.PiGoogleRefresh);
        check("Pi renewal permission defaults off", !new DeckSettings().PiGoogleRefresh);

        // Two-column compact mode: Pi Codex + single pi-antigravity column with instant account switching and provider-split token usage.
        var sessions = Path.Combine(folder, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "s.jsonl"),
            JsonSerializer.Serialize(new { type = "message", id = "c1", timestamp = now.ToString("O"), message = new { role = "assistant", provider = "openai-codex", model = "gpt-5", usage = new { input = 100, output = 50, cacheRead = 0, cacheWrite = 0 } } }) + "\n" +
            JsonSerializer.Serialize(new { type = "message", id = "g1", timestamp = now.ToString("O"), message = new { role = "assistant", provider = "antigravity", model = "gemini-2.5-pro", usage = new { input = 200, output = 80, cacheRead = 0, cacheWrite = 0 } } }) + "\n");
        var twoColSettings = new DeckSettings
        {
            PiReadLogin = true,
            PiGoogleRefresh = true,
            Apps =
            [
                new AppEntry { QuotaSource = ProviderId.Pi, UsageSource = ProviderId.Pi, PiProvider = "openai-codex", DisplayName = "Pi Codex" },
                new AppEntry { QuotaSource = ProviderId.Pi, UsageSource = ProviderId.Pi, PiProvider = "antigravity", PiAccountKey = listed[0].Key, DisplayName = "pi-antigravity" }
            ]
        };
        using var twoColHandler = new Handler();
        using var twoColHttp = new HttpClient(twoColHandler);
        using var twoColService = new UsageService(new Desktop(), new DataLocations("", "", "", "", "") { PiAuthFile = auth, PiSessions = sessions }, twoColHttp, () => now);
        var twoColSnapshots = await twoColService.RefreshAsync(twoColSettings);
        int queriesBeforeSwitch = twoColHandler.QuotaQueries;
        var cols1 = DashboardUsage.Columns(twoColSettings, twoColSnapshots);
        check("Single pi-antigravity column shows selected account 1 quota", cols1[1].Quota.Metrics.Single().ValueText == "60%");
        check("Pi Codex and pi-antigravity columns split token usage by provider",
            cols1[0].Usage.UsageAvailable && cols1[0].Usage.Entries.Sum(e => e.Total) == 150 &&
            cols1[1].Usage.UsageAvailable && cols1[1].Usage.Entries.Sum(e => e.Total) == 280);
        // Switching to account 2 in UI uses pre-fetched snapshot in 0ms without making new HTTP requests
        twoColSettings.Apps[1].PiAccountKey = listed[1].Key;
        var cols2 = DashboardUsage.Columns(twoColSettings, twoColSnapshots);
        check("Switching pi-antigravity account is instant without extra HTTP requests",
            cols2[1].Quota.Metrics.Single().ValueText == "70%" && twoColHandler.QuotaQueries == queriesBeforeSwitch);
    }

    sealed class Handler : HttpMessageHandler
    {
        public int Calls, Renewals, QuotaQueries;
        public bool ValidRenewalRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var uri = request.RequestUri!;
            if (uri.Host == "oauth2.googleapis.com")
            {
                Renewals++;
                var body = await request.Content!.ReadAsStringAsync(ct);
                ValidRenewalRequest = uri.AbsolutePath == "/token" && body.Contains("grant_type=refresh_token") && body.Contains("client_id=") && body.Contains("client_secret=");
                if (body.Contains("refresh_token=revoked")) return Json("{}", HttpStatusCode.BadRequest);
                var index = body.Contains("refresh_token=refresh1") ? 1 : 2;
                return Json(JsonSerializer.Serialize(new { access_token = "access" + index, expires_in = 3600 }));
            }
            if (!uri.Host.EndsWith(".googleapis.com", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected credential destination");
            if (uri.AbsolutePath.EndsWith("loadCodeAssist"))
                return Json("{\"currentTier\":{\"name\":\"Free\"},\"paidTier\":{\"id\":\"g1-pro-tier\",\"name\":\"Google AI Pro\"}}");
            QuotaQueries++;
            var token = request.Headers.Authorization?.Parameter ?? "";
            double fraction = token == "access1" ? .6 : token == "access2" ? .7 : .8;
            return Json(JsonSerializer.Serialize(new { groups = new[] { new { buckets = new[] { new { bucketId = "gemini-5h", remainingFraction = fraction } } } } }));
        }
        static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body) };
    }
    sealed class Desktop : IDesktopSources
    {
        public string? ReadCursorToken(string database) => throw new InvalidOperationException("Unexpected credential read");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => throw new InvalidOperationException("Unexpected desktop service lookup");
    }
}
