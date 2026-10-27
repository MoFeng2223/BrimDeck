using System.Net;
using System.Text.Json;
using BrimDeck.Core;

static class PiQuotaTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var file = Path.Combine(root, "pi-native-auth.json");
        var now = DateTimeOffset.Now;
        var expires = now.AddHours(1).ToUnixTimeMilliseconds();
        string Auth(long expiry, string type = "oauth") => JsonSerializer.Serialize(new Dictionary<string, object> {
            ["openai-codex"] = new { type, access = "synthetic-access", refresh = "never-read-this", accountId = "synthetic-account", expires = expiry },
            ["antigravity"] = new { type, access = "synthetic-google", expires = expiry },
            ["unrelated"] = new { type = "api_key", key = "!never execute this" }
        });
        var response = JsonSerializer.Serialize(new { plan_type = "plus", rate_limit = new {
            primary_window = new { used_percent = 35, reset_at = now.AddHours(1).ToUnixTimeSeconds(), limit_window_seconds = 18000 },
            secondary_window = new { used_percent = 59, reset_at = now.AddDays(2).ToUnixTimeSeconds(), limit_window_seconds = 604800 }
        } });
        using var handler = new Handler { Body = response };
        using var http = new HttpClient(handler);
        async Task<ProviderSnapshot> Read(bool allowed = true, string provider = "openai-codex")
        {
            var snapshot = new ProviderSnapshot(ProviderId.Pi) { UsageAvailable = true, Entries = [new("local", now, "test", 1, 0, 0, 0, 2)] };
            await PiQuota.ReadAsync(snapshot, file, allowed, provider, http, now);
            return snapshot;
        }
        File.WriteAllText(file, "malformed auth must not be opened without consent");
        var off = await Read(false);
        check("Pi credential access defaults off and makes no HTTP request", !new DeckSettings().PiReadLogin && handler.Calls == 0 && off.Quotas.Count == 0 && off.Entries.Count == 1);
        File.WriteAllText(file, Auth(expires));
        var before = File.ReadAllBytes(file);
        var connected = await Read();
        check("Native Pi quota displays 65/41 remaining without extensions", connected.LiveQuota && connected.Metrics[0].ValueText == "65%" && connected.Metrics[1].ValueText == "41%" && connected.Source == "Pi · Codex");
        check("Native Pi sends authorization only to official Codex endpoint", handler.LastUri == "https://chatgpt.com/backend-api/wham/usage" && handler.SawExpectedCodexAuth);
        check("Native Pi leaves auth and refresh tokens unchanged", File.ReadAllBytes(file).SequenceEqual(before));
        check("Native Pi keeps accounting separate from subscription charges", connected.Entries.Count == 1 && connected.Plan == "plus");
        check("Normal percentage metrics still show used share", UsageMetric.FromQuota(connected.Quotas[0]).ValueText == "35%");
        handler.Code = HttpStatusCode.Unauthorized;
        check("Native Pi rejected login is not shown as a quota", !(await Read()).LiveQuota);
        handler.Code = HttpStatusCode.TooManyRequests;
        check("Native Pi rate limiting is handled without inventing a value", (await Read()).Quotas.Count == 0);
        handler.Code = HttpStatusCode.OK;
        File.WriteAllText(file, Auth(now.AddMinutes(-1).ToUnixTimeMilliseconds()));
        int calls = handler.Calls;
        check("Native Pi expired sign-in is not sent or refreshed", !(await Read()).LiveQuota && handler.Calls == calls);
        File.WriteAllText(file, Auth(expires, "api_key"));
        check("Native Pi never executes API-key commands or guesses OAuth", !(await Read()).LiveQuota && handler.Calls == calls);
        File.WriteAllText(file, "{broken");
        check("Native Pi malformed auth preserves token accounting", (await Read()).Entries.Count == 1);
        File.WriteAllText(file, Auth(expires));
        check("Native Pi unsupported providers make no credential request", !(await Read(provider: "unknown")).LiveQuota && handler.Calls == calls);
        handler.Body = "{\"groups\":[{\"buckets\":[{\"bucketId\":\"gemini-5h\",\"remainingFraction\":0.8},{\"bucketId\":\"gemini-argon-wtu-weekly\",\"remainingFraction\":0.75}]}]}";
        var google = await Read(provider: "antigravity");
        check("Native Pi supports Antigravity and Gemini 4 Argon without its desktop service", google.LiveQuota && google.Metrics[0].ValueText == "80%" && google.Metrics[1].Label == "Gemini 4 · 每周" && google.Metrics[1].ValueText == "75%" && handler.LastUri.StartsWith("https://daily-cloudcode-pa.googleapis.com/"));
        File.WriteAllText(file, JsonSerializer.Serialize(new Dictionary<string, object> { ["google-antigravity"] = new { type = "oauth", access = "synthetic-google", expires } }));
        var builtInGoogle = await Read(provider: "antigravity");
        check("Native Pi falls back to built-in google-antigravity auth entry", builtInGoogle.LiveQuota && builtInGoogle.Metrics[0].ValueText == "80%");
        File.WriteAllText(file, Auth(expires));
        handler.Body = "SYNTHETIC_SECRET_DO_NOT_DISPLAY";
        var invalid = await Read();
        check("Native Pi invalid responses never expose body or credentials", !invalid.Status.Contains("SYNTHETIC") && invalid.Quotas.Count == 0);
        check("Pi auth and session roots are independent", DataLocations.Resolve(root, root, root, null, null).PiAuthFile == Path.Combine(root, ".pi", "agent", "auth.json"));
    }

    sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public string Body = "", LastUri = "";
        public HttpStatusCode Code = HttpStatusCode.OK;
        public bool SawExpectedCodexAuth;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; LastUri = request.RequestUri!.ToString();
            SawExpectedCodexAuth |= request.Headers.Authorization?.Parameter == "synthetic-access" && request.Headers.Contains("ChatGPT-Account-Id");
            return Task.FromResult(new HttpResponseMessage(Code) { Content = new StringContent(Body) });
        }
    }
}
