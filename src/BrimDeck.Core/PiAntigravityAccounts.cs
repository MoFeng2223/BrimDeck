using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

public sealed record PiAccountInfo(string Key, string Label, bool Active, bool AccessExpired);

// No credential record implements ToString/serialization. All refreshed access is process-memory only.
public sealed class PiAntigravityAccounts
{
    private sealed class CachedAccess(string fingerprint, string access, DateTimeOffset expires)
    {
        public readonly string Fingerprint = fingerprint, Access = access;
        public readonly DateTimeOffset Expires = expires;
    }
    private readonly Dictionary<string, CachedAccess> _cache = [];
    private readonly Dictionary<string, (string Fingerprint, string Name, DateTimeOffset Expires)> _plans = [];
    private readonly SemaphoreSlim _gate = new(1);
    public void Clear() { _cache.Clear(); _plans.Clear(); }
    public static string KeyFor(string accountId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId))).ToLowerInvariant();
    public static string ActiveKey(string file)
    {
        try
        {
            using var doc = OpenStore(file);
            var active = doc?.RootElement.Get("activeAccountId").Text() ?? "";
            return active.Length > 0 ? KeyFor(active) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return ""; }
    }

    public static IReadOnlyList<PiAccountInfo> List(string file, DateTimeOffset now)
    {
        var result = new List<PiAccountInfo>();
        try
        {
            using var doc = OpenStore(file);
            if (doc is null) return result;
            var root = doc.RootElement;
            var active = root.Get("activeAccountId").Text();
            int index = 0;
            foreach (var property in root.Get("accounts").EnumerateObject().OrderBy(p => p.Value.Get("addedAt").Number() ?? 0).ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                if (++index > 32) break;
                var account = property.Value;
                result.Add(new(KeyFor(property.Name), Loc.T($"Antigravity 账号 {index}", $"Antigravity account {index}"),
                    property.Name == active, account.Get("expires").Number() is not { } expires || expires <= now.ToUnixTimeMilliseconds()));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        return result;
    }

    public async Task ReadAsync(ProviderSnapshot snapshot, string file, string accountKey, bool allowed, bool allowRefresh,
        HttpClient http, DateTimeOffset now, CancellationToken ct = default)
    {
        snapshot.Source = "Pi · Antigravity";
        snapshot.StatusLabel = Loc.T("未授权读取登录", "Sign-in access disabled");
        snapshot.Status = Loc.T("请在 Pi 配额设置中允许读取登录", "Enable Pi sign-in access in quota settings");
        if (!allowed) { Clear(); return; }
        bool entered = false;
        try
        {
            await _gate.WaitAsync(ct); // Bounded requests and renewal single-flight across the account pool.
            entered = true;
            using var store = OpenStore(file);
            if (store is null) { Missing(snapshot); return; }
            JsonElement account = default;
            bool active = false;
            foreach (var property in store.RootElement.Get("accounts").EnumerateObject())
                if (KeyFor(property.Name) == accountKey) { account = property.Value; active = property.Name == store.RootElement.Get("activeAccountId").Text(); break; }
            if (account.ValueKind != JsonValueKind.Object) { _cache.Remove(accountKey); Missing(snapshot); return; }
            var access = account.Get("access").Text();
            var expires = account.Get("expires").Number() ?? 0;
            // Fingerprint only this account, so Pi-side removal/login/rotation invalidates the memory cache.
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.GetRawText())));
            if (_cache.TryGetValue(accountKey, out var cached) && cached.Fingerprint == fingerprint && cached.Expires > now.AddMinutes(1))
                access = cached.Access;
            else if (access.Length == 0 || expires <= now.AddMinutes(1).ToUnixTimeMilliseconds())
            {
                _cache.Remove(accountKey);
                if (!allowRefresh)
                { snapshot.StatusLabel = Loc.T("需允许 Google 续期", "Google renewal permission required"); snapshot.Status = Loc.T("访问授权已到期；可开启 Google 内存续期，不修改 Pi 文件", "Access has expired; allow in-memory Google renewal without changing Pi files"); return; }
                var refresh = account.Get("refresh").Text();
                if (refresh.Length == 0) { Missing(snapshot); return; }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                var client = PublicClient(file);
                if (client.Id.Length == 0 || client.Secret.Length == 0) { Missing(snapshot); return; }
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                        ["client_id"] = client.Id, ["client_secret"] = client.Secret,
                        ["refresh_token"] = refresh, ["grant_type"] = "refresh_token"
                    })
                };
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!response.IsSuccessStatusCode)
                { snapshot.StatusLabel = Loc.T("Google 续期失败", "Google renewal failed"); snapshot.Status = Loc.T("此账号可能需在 Pi 中重新授权，其他账号不受影响", "This account may need authorization in Pi; other accounts are unaffected"); return; }
                using var data = await PiQuota.ReadBoundedResponse(response, deadline.Token);
                access = data.RootElement.Get("access_token").Text();
                if (access.Length == 0) throw new InvalidDataException();
                var seconds = data.RootElement.Get("expires_in").Number() ?? 3600;
                _cache[accountKey] = new(fingerprint, access, now.AddSeconds(Math.Clamp(seconds, 60, 86400) - 30));
            }
            await PiQuota.QueryAntigravityAsync(snapshot, access, http, now, ct);
            if (!snapshot.LiveQuota) { _cache.Remove(accountKey); return; }
            if (active) snapshot.Status += Loc.T("（Pi 当前账号）", " (Pi active account)");
            if (_plans.TryGetValue(accountKey, out var planCache) && planCache.Fingerprint == fingerprint && planCache.Expires > now)
            { snapshot.Plan = planCache.Name; return; }
            // Subscription metadata is best effort. A failed plan lookup must not discard valid quotas.
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                request.Headers.TryAddWithoutValidation("User-Agent", PiQuota.AntigravityUserAgent);
                request.Content = new StringContent("{\"metadata\":{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"PLATFORM_UNSPECIFIED\",\"pluginType\":\"GEMINI\"}}", Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.IsSuccessStatusCode)
                {
                    using var data = await PiQuota.ReadBoundedResponse(response, deadline.Token);
                    var paid = data.RootElement.Get("paidTier");
                    // Never label an account Free merely because currentTier reports free-tier.
                    var plan = paid.Get("name").Text();
                    if (plan.Length == 0) plan = paid.Get("id").Text();
                    snapshot.Plan = plan.Length <= 100 ? plan : "";
                    if (snapshot.Plan.Length > 0) _plans[accountKey] = (fingerprint, snapshot.Plan, now.AddMinutes(15));
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or OperationCanceledException) { }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Failure(snapshot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or InvalidOperationException or FormatException or ArgumentException) { Failure(snapshot); }
        finally { if (entered) _gate.Release(); }
    }

    private static JsonDocument? OpenStore(string file)
    {
        if (!File.Exists(file)) return null;
        using var stream = SharedFile.Open(file);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException();
        var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        if (doc.RootElement.Get("version").Number() != 1 || doc.RootElement.Get("accounts").ValueKind != JsonValueKind.Object)
        { doc.Dispose(); return null; }
        return doc;
    }
    private static (string Id, string Secret) PublicClient(string accountsFile)
    {
        var envId = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLIENT_ID")?.Trim() ?? "";
        var envSecret = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLIENT_SECRET")?.Trim() ?? "";
        if (envId.Length > 0 && envSecret.Length > 0) return (envId, envSecret);
        // Read the installed pi-antigravity package's public desktop OAuth client metadata from the local Pi directory.
        var root = Path.GetDirectoryName(accountsFile) ?? "";
        var oauthFile = Path.Combine(root, "npm", "node_modules", "pi-antigravity", "src", "auth", "oauth.ts");
        if (!File.Exists(oauthFile)) return ("", "");
        using var stream = SharedFile.Open(oauthFile);
        if (stream.Length > 256 * 1024) return ("", "");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var source = reader.ReadToEnd();
        static string DecodeAtob(string text, string constantName)
        {
            var match = Regex.Match(text, $@"\b{constantName}\b[\s\S]*?atob\(\s*((?:""[A-Za-z0-9+/=]+""\s*\+?\s*)+),?\s*\)");
            if (!match.Success) return "";
            var b64 = string.Concat(Regex.Matches(match.Groups[1].Value, @"""([A-Za-z0-9+/=]+)""").Select(m => m.Groups[1].Value));
            return b64.Length > 0 ? Encoding.UTF8.GetString(Convert.FromBase64String(b64)) : "";
        }
        return (DecodeAtob(source, "CLIENT_ID"), DecodeAtob(source, "CLIENT_SECRET"));
    }
    private static void Missing(ProviderSnapshot s)
    { s.StatusLabel = Loc.T("账号不存在", "Account missing"); s.Status = Loc.T("账号已移除或没有有效授权，请检查 Pi 账号池", "The account was removed or lacks authorization; check the Pi account pool"); }
    private static void Failure(ProviderSnapshot s)
    { s.LiveQuota = false; s.Quotas = []; s.Metrics = []; s.StatusLabel = Loc.T("账号额度暂不可用", "Account quota unavailable"); s.Status = Loc.T("此账号查询失败，稍后自动重试；不影响其他账号", "This account's query failed; retrying automatically, without affecting other accounts"); }
}
