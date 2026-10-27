using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

// Native, opt-in and read-only. No Pi extension, subprocess, snapshot or refresh-token access.
public static class PiQuota
{
    internal const string AntigravityUserAgent = "antigravity/cli/1.4.0 (aidev_client; os_type=linux; arch=amd64; cl=982146307; auth_method=consumer)";

    public static async Task ReadAsync(ProviderSnapshot snapshot, string authFile, bool allowed, string provider,
        HttpClient http, DateTimeOffset now, CancellationToken cancellation = default)
    {
        snapshot.Source = "Pi";
        snapshot.StatusLabel = Loc.T("未授权读取登录", "Sign-in access disabled");
        snapshot.Status = Loc.T("在 Pi 配额配置中开启“读取 Pi 登录并查询额度”；本地用量不受影响", "Enable Read Pi sign-in and fetch quota in Pi quota settings; local usage is unaffected");
        if (!allowed) return; // Do not even inspect a credential file until consent is enabled.
        try
        {
            if (provider is not ("openai-codex" or "antigravity" or "google-antigravity"))
            { snapshot.StatusLabel = Loc.T("来源未支持", "Provider unsupported"); snapshot.Status = Loc.T("请选择 Codex 或 Antigravity", "Select Codex or Antigravity"); return; }
            if (!File.Exists(authFile)) { Missing(snapshot); return; }
            string access, account;
            using (var stream = SharedFile.Open(authFile))
            {
                if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException();
                using var auth = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
                // Select this provider only. Never resolve command-based API keys or inspect refresh tokens.
                var entry = auth.RootElement.Get(provider);
                if (entry.ValueKind != JsonValueKind.Object && provider == "antigravity")
                    entry = auth.RootElement.Get("google-antigravity");
                if (entry.Get("type").Text() != "oauth") { Missing(snapshot); return; }
                access = entry.Get("access").Text();
                account = entry.Get("accountId").Text();
                if (access.Length == 0) { Missing(snapshot); return; }
                if (entry.Get("expires").Number() is { } expires && expires <= now.ToUnixTimeMilliseconds())
                { Expired(snapshot); return; }
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            if (provider == "openai-codex")
            {
                if (account.Length == 0) account = AccountFromJwt(access);
                if (account.Length == 0) throw new InvalidDataException();
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);
                request.Headers.TryAddWithoutValidation("originator", "pi");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!Accept(snapshot, response.StatusCode)) return;
                using var data = await ReadResponse(response, deadline.Token);
                snapshot.Quotas = QuotaParser.Codex(data.RootElement, now, true);
                snapshot.Plan = data.RootElement.Get("plan_type").Text();
            }
            else
            {
                foreach (var host in new[] { "daily-cloudcode-pa.googleapis.com", "cloudcode-pa.googleapis.com" })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + host + "/v1internal:retrieveUserQuotaSummary");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                    request.Headers.TryAddWithoutValidation("User-Agent", AntigravityUserAgent);
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if (!response.IsSuccessStatusCode && host.StartsWith("daily-") && response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)) continue;
                    if (!Accept(snapshot, response.StatusCode)) return;
                    using var data = await ReadResponse(response, deadline.Token);
                    snapshot.Quotas = QuotaParser.Antigravity(data.RootElement);
                    break;
                }
            }
            if (snapshot.Quotas.Count == 0) throw new InvalidDataException();
            snapshot.Metrics = snapshot.Quotas.Select(q => UsageMetric.FromQuota(q) with { ShowRemaining = true }).ToList();
            snapshot.Source = provider == "openai-codex" ? "Pi · Codex" : "Pi · Antigravity";
            snapshot.QuotaTime = now; snapshot.LiveQuota = true;
            snapshot.StatusLabel = Loc.T("已连接", "Connected");
            snapshot.Status = Loc.T("使用 Pi 登录直接查询；不依赖插件", "Queried directly with Pi sign-in; no extension required");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { Fail(snapshot, Loc.T("查询超时，稍后自动重试", "Query timed out; retrying automatically")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or HttpRequestException or FormatException or ArgumentException)
        { Fail(snapshot, Loc.T("暂时无法查询 Pi 额度，请检查登录或网络", "Pi quota is unavailable; check sign-in or connectivity")); }
    }

    // Shared by the opt-in account-pool adapter; access never appears in snapshots or error text.
    internal static async Task QueryAntigravityAsync(ProviderSnapshot snapshot, string access, HttpClient http,
        DateTimeOffset now, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        foreach (var host in new[] { "daily-cloudcode-pa.googleapis.com", "cloudcode-pa.googleapis.com" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + host + "/v1internal:retrieveUserQuotaSummary");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            request.Headers.TryAddWithoutValidation("User-Agent", AntigravityUserAgent);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode && host.StartsWith("daily-") && response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)) continue;
            if (!Accept(snapshot, response.StatusCode)) return;
            using var data = await ReadResponse(response, deadline.Token);
            snapshot.Quotas = QuotaParser.Antigravity(data.RootElement);
            break;
        }
        if (snapshot.Quotas.Count == 0) throw new InvalidDataException();
        snapshot.Metrics = snapshot.Quotas.Select(q => UsageMetric.FromQuota(q) with { ShowRemaining = true }).ToList();
        snapshot.LiveQuota = true; snapshot.QuotaTime = now; snapshot.Source = "Pi · Antigravity";
        snapshot.StatusLabel = Loc.T("已连接", "Connected");
        snapshot.Status = Loc.T("账号独立额度；不切换 Pi 活动账号", "Account-specific quota; Pi's active account is unchanged");
    }

    internal static async Task<JsonDocument> ReadBoundedResponse(HttpResponseMessage response, CancellationToken ct) => await ReadResponse(response, ct);

    private static bool Accept(ProviderSnapshot snapshot, HttpStatusCode code)
    {
        if (code == HttpStatusCode.Unauthorized) { Expired(snapshot); return false; }
        if ((int)code is >= 200 and < 300) return true;
        Fail(snapshot, code == HttpStatusCode.TooManyRequests ? Loc.T("请求频繁，稍后自动重试", "Too many requests; retrying automatically")
            : Loc.T("额度接口拒绝访问或暂不可用", "Quota access was denied or is temporarily unavailable"));
        return false;
    }
    private static void Missing(ProviderSnapshot snapshot)
    { snapshot.StatusLabel = Loc.T("未找到 Pi 登录", "No Pi sign-in"); snapshot.Status = Loc.T("未找到所选来源的 Pi OAuth 登录，本地用量仍可统计", "No Pi OAuth sign-in for this provider; local usage is still available"); }
    private static void Expired(ProviderSnapshot snapshot)
    { snapshot.StatusLabel = Loc.T("Pi 登录需续期", "Pi sign-in needs renewal"); snapshot.Status = Loc.T("使用 Pi 更新登录后自动恢复；BrimDeck 不修改 Pi 登录或轮换令牌", "Resumes after Pi renews its sign-in; BrimDeck never changes Pi sign-in or rotates tokens"); }
    private static void Fail(ProviderSnapshot snapshot, string text)
    { snapshot.Quotas = []; snapshot.Metrics = []; snapshot.LiveQuota = false; snapshot.StatusLabel = Loc.T("额度暂不可用", "Quota unavailable"); snapshot.Status = text; }
    private static string AccountFromJwt(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return "";
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return doc.RootElement.Get("https://api.openai.com/auth").Get("chatgpt_account_id").Text();
    }
    private static async Task<JsonDocument> ReadResponse(HttpResponseMessage response, CancellationToken ct)
    {
        using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        { if (output.Length + read > 1024 * 1024) throw new InvalidDataException(); output.Write(buffer, 0, read); }
        return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }
}
