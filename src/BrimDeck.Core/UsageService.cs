using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

public sealed record LocalEndpoint(int Port, string Scheme, string Token);
public interface IDesktopSources
{
    ClaudeDesktopState ReadClaudeDesktop(DataLocations locations) => new(false, []);
    string? ReadCursorToken(string database);
    Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation);
}

public sealed class UsageService(IDesktopSources desktop, DataLocations? locations = null, HttpClient? http = null, Func<DateTimeOffset>? clock = null) : IDisposable
{
    // Every provider uses the panel's shared refresh cycle. Claude may display local history
    // after an unsuccessful request, but it does not delay the next scheduled refresh.
    private const string ClaudeUsageNote = " Token 统计来自本机 Claude Code 会话，不包含桌面版普通聊天。";
    private readonly HttpClient _http = http ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    private readonly DataLocations _locations = locations ?? DataLocations.Detect();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);
    private readonly LogReader _logs = new();
    private readonly SemaphoreSlim _gate = new(1);
    private ClaudeQuotaSnapshot? _claudeRetained;

    public async Task<List<ProviderSnapshot>> RefreshAsync(DeckSettings settings, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var tasks = Enum.GetValues<ProviderId>().Where(settings.Enabled).Select(id => FetchAsync(id, cancellation));
            return (await Task.WhenAll(tasks)).ToList();
        }
        finally { _gate.Release(); }
    }

    private async Task<ProviderSnapshot> FetchAsync(ProviderId id, CancellationToken ct)
    {
        var now = _clock();
        // The reader owns a cache and is accessed serially before the first asynchronous request.
        var snapshot = id is ProviderId.Claude or ProviderId.Codex
            ? _logs.Read(id, id == ProviderId.Codex ? _locations.CodexHome : _locations.ClaudeHome, now) : new ProviderSnapshot(id);
        if (id == ProviderId.Claude) ReadClaudeProfile(snapshot);
        try
        {
            switch (id)
            {
                case ProviderId.Codex:
                {
                    using var auth = ReadJson(Path.Combine(_locations.CodexHome, "auth.json"));
                    var tokens = auth?.RootElement.Get("tokens") ?? default;
                    var token = tokens.Get("access_token").Text();
                    if (token.Length == 0) { snapshot.StatusLabel = "未连接"; snapshot.Status = "请先登录 Codex"; break; }
                    using var request = Bearer(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage", token);
                    var account = tokens.Get("account_id").Text();
                    if (account.Length > 0) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);
                    using var doc = await SendAsync(_http, request, ct);
                    var quotas = QuotaParser.Codex(doc.RootElement, now, true);
                    if (quotas.Count == 0) throw new InvalidDataException();
                    snapshot.Quotas = quotas;
                    snapshot.Plan = doc.RootElement.Get("plan_type").Text();
                    snapshot.PlanSource = "Codex 账户";
                    Connected(snapshot, "Codex 账户", now);
                    break;
                }
                case ProviderId.Claude:
                    await ReadClaudeAsync(snapshot, now, ct);
                    break;
                case ProviderId.Cursor:
                    await ReadCursorAsync(snapshot, now, ct);
                    break;
                case ProviderId.Antigravity:
                    await ReadAntigravityAsync(snapshot, now, ct);
                    break;
            }
        }
        catch (HttpRequestException ex)
        {
            snapshot.StatusLabel = ex.StatusCode switch { HttpStatusCode.Unauthorized => "登录已失效", HttpStatusCode.Forbidden => "访问受限", _ => "暂不可用" };
            snapshot.Status = ex.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "登录已失效或账户接口拒绝访问，请在原应用重新登录",
                HttpStatusCode.TooManyRequests => "请求频繁，稍后自动重试",
                _ => "账户接口暂时不可用"
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { snapshot.StatusLabel = "读取超时"; snapshot.Status = "读取超时，稍后重试"; }
        catch (InvalidDataException) { snapshot.StatusLabel = "数据不可用"; snapshot.Status = "数据格式发生变化，暂时无法读取配额"; }
        catch (System.Runtime.InteropServices.COMException)
        { snapshot.StatusLabel = "检测失败"; snapshot.Status = "本地服务检测失败，稍后重试"; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or FormatException)
        { snapshot.StatusLabel = "读取失败"; snapshot.Status = "暂时无法读取数据，稍后重试"; }
        if (!snapshot.LiveQuota && snapshot.Quotas.Count > 0) snapshot.Source = "本地配额快照 · 可能已过时";
        return snapshot;
    }

    private void ReadClaudeProfile(ProviderSnapshot snapshot)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var profile = Path.Combine(_locations.ClaudeHome, ".claude.json");
            if (!File.Exists(profile) && Path.GetFullPath(_locations.ClaudeHome).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.Combine(home, ".claude"), StringComparison.OrdinalIgnoreCase)) profile = Path.Combine(home, ".claude.json");
            using var doc = ReadJson(profile);
            snapshot.Plan = PlanParser.ClaudeProfile(doc?.RootElement ?? default);
            if (snapshot.Plan.Length > 0) snapshot.PlanSource = "Claude 本地账户记录";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    private async Task ReadClaudeAsync(ProviderSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        Exception? failure = null;
        Exception? lastFailure = null;
        ClaudeCredential? login = null;
        async Task<bool> TryCredential(ClaudeCredential credential)
        {
            ct.ThrowIfCancellationRequested();
            lastFailure = null;
            if (string.IsNullOrWhiteSpace(credential.Token) || !attempted.Add(credential.Token)) return false;
            login = credential;
            try
            {
                using var request = Bearer(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage", credential.Token);
                request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                using var doc = await SendAsync(_http, request, ct);
                var quotas = QuotaParser.Claude(doc.RootElement);
                if (quotas.Count == 0) throw new InvalidDataException();
                // Publish only a complete successful result, all from the same login.
                snapshot.Quotas = quotas;
                snapshot.Plan = credential.Plan;
                snapshot.PlanSource = credential.Plan.Length > 0 ? credential.Source + " 登录记录" : "";
                Connected(snapshot, credential.Source + " · 账户配额", now);
                snapshot.UsageNote += ClaudeUsageNote;
                _claudeRetained = new(now, snapshot.Plan, snapshot.PlanSource, snapshot.Source, quotas.ToList());
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is
                HttpRequestException or OperationCanceledException or IOException or InvalidDataException or JsonException or FormatException)
            {
                lastFailure = ex;
                // Keep an operational error instead of masking it with a later stale login.
                if (failure is null || ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } ||
                    failure is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }) failure = ex;
                return false;
            }
        }

        bool codeReadFailed = false;
        ClaudeCredential? codeCredential = null;
        try
        {
            using var auth = ReadJson(Path.Combine(_locations.ClaudeHome, ".credentials.json"));
            var oauth = auth?.RootElement.Get("claudeAiOauth") ?? default;
            var token = oauth.Get("accessToken").Text();
            var plan = PlanParser.ClaudeTier(oauth.Get("rateLimitTier").Text(), oauth.Get("subscriptionType").Text());
            if (plan.Equals("max", StringComparison.OrdinalIgnoreCase) && snapshot.Plan.StartsWith("Max ", StringComparison.Ordinal)) plan = snapshot.Plan;
            if (token.Length > 0) codeCredential = new(token, plan, "Claude Code");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { codeReadFailed = true; }
        if (codeCredential is not null && await TryCredential(codeCredential)) return;

        ct.ThrowIfCancellationRequested();
        ClaudeDesktopState desktopState;
        try { desktopState = desktop.ReadClaudeDesktop(_locations); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or FormatException or
            ArgumentException or System.Security.Cryptography.CryptographicException or System.Runtime.InteropServices.COMException)
        { desktopState = new(true, [], true); }
        foreach (var credential in desktopState.Credentials)
        {
            if (await TryCredential(credential)) return;
            // Only stale or insufficiently scoped tokens warrant trying another
            // Desktop credential. Network errors and rate limits affect this source.
            if (lastFailure is not null && lastFailure is not HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }) break;
        }
        if (failure is not null)
        {
            if (failure is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }) _claudeRetained = null;
            else
            {
                if (HasLocalQuota)
                {
                    // The plan comes from the login record and stays known while the interface is unavailable.
                    var local = Freshest(now);
                    if (local.Plan.Length == 0 && login is { Plan.Length: > 0 }) _claudeRetained = local = local with { Plan = login.Plan, PlanSource = login.Source + " 登录记录" };
                    Retain(snapshot, local, failure, now); return;
                }
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        _claudeRetained = null;
        snapshot.StatusLabel = desktopState.ReadFailed || codeReadFailed ? "读取失败" : desktopState.Detected ? "等待登录" : "未连接";
        snapshot.Status = desktopState.ReadFailed ? "已发现 Claude 桌面版，暂时无法读取登录信息" : codeReadFailed
            ? "暂时无法读取 Claude Code 登录信息" : desktopState.Detected
            ? "请打开 Claude 桌面版并确认已登录，随后刷新配额" : "请先登录 Claude 桌面版或 Claude Code";
    }

    private bool HasLocalQuota => _claudeRetained is not null || ClaudeUsageHistory.Latest(_locations) is not null;

    // The desktop application samples the same account about every fifteen minutes. A newer local
    // sample replaces the last interface reading; reset times are carried over while they still lie ahead.
    private ClaudeQuotaSnapshot Freshest(DateTimeOffset now)
    {
        var kept = _claudeRetained;
        var sample = ClaudeUsageHistory.Latest(_locations);
        if (sample is not null && sample.Time <= now.AddMinutes(5) && (kept is null || sample.Time > kept.Time))
        {
            DateTimeOffset? Reset(int minutes) => kept?.Quotas.FirstOrDefault(quota => quota.Minutes == minutes)?.ResetAt is { } reset && reset > sample.Time ? reset : null;
            kept = new(sample.Time, kept?.Plan ?? "", kept?.PlanSource ?? "", "Claude 桌面版 · 本地用量记录",
                [new Quota("5 小时额度", sample.FiveHour, Reset(300), 300), new Quota("每周额度", sample.SevenDay, Reset(10080), 10080)], true);
            _claudeRetained = kept;
        }
        return kept ?? throw new InvalidOperationException("No local quota available");
    }

    private static void Retain(ProviderSnapshot snapshot, ClaudeQuotaSnapshot kept, Exception? failure, DateTimeOffset now)
    {
        snapshot.Quotas = kept.Quotas.ToList();
        if (kept.Plan.Length > 0) { snapshot.Plan = kept.Plan; snapshot.PlanSource = kept.PlanSource; }
        snapshot.LiveQuota = true; snapshot.QuotaTime = kept.Time; snapshot.Source = kept.Source;
        var time = kept.Time.LocalDateTime.ToString(kept.Time.LocalDateTime.Date == now.LocalDateTime.Date ? "HH:mm" : "MM-dd HH:mm");
        var shown = kept.Local ? $"显示 Claude 桌面版 {time} 记录的配额" : $"显示 {time} 读取的配额";
        snapshot.Status = failure switch
        {
            null => kept.Local ? $"已连接，{shown}" : "已连接",
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => $"请求频繁，{shown}，稍后自动重试",
            OperationCanceledException => $"读取超时，{shown}，稍后自动重试",
            _ => $"账户接口暂时不可用，{shown}，稍后自动重试"
        };
        snapshot.StatusLabel = failure is null ? "已连接" : "稍后重试";
        snapshot.UsageNote += ClaudeUsageNote;
    }

    private async Task ReadCursorAsync(ProviderSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        var token = desktop.ReadCursorToken(_locations.CursorDatabase);
        snapshot.UsageNote = "Cursor Token 明细来自账户用量接口，包含其他设备上的用量。";
        if (string.IsNullOrWhiteSpace(token)) { snapshot.StatusLabel = "未连接"; snapshot.Status = "请打开 Cursor 并确认已登录"; return; }
        try
        {
            using var request = Bearer(HttpMethod.Post, "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage", token);
            request.Content = JsonBody(new { });
            using var doc = await SendAsync(_http, request, ct);
            snapshot.Quotas = QuotaParser.Cursor(doc.RootElement);
            snapshot.Plan = PlanParser.Cursor(doc.RootElement);
        }
        catch (HttpRequestException) { /* Older client sessions may use the dashboard endpoint. */ }
        if (snapshot.Quotas.Count == 0 || snapshot.Plan.Length == 0)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length < 2) throw new InvalidDataException();
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                using var jwt = JsonDocument.Parse(Convert.FromBase64String(payload));
                var subject = jwt.RootElement.Get("sub").Text();
                if (subject.Length == 0) throw new InvalidDataException();
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://cursor.com/api/usage-summary");
                request.Headers.TryAddWithoutValidation("Cookie", "WorkosCursorSessionToken=" + Uri.EscapeDataString(subject + "::" + token));
                using var doc = await SendAsync(_http, request, ct);
                if (snapshot.Quotas.Count == 0) snapshot.Quotas = QuotaParser.Cursor(doc.RootElement);
                snapshot.Plan = PlanParser.Cursor(doc.RootElement);
            }
            catch (Exception ex) when (snapshot.Quotas.Count > 0 && !ct.IsCancellationRequested &&
                ex is HttpRequestException or JsonException or InvalidDataException or FormatException or TaskCanceledException) { }
        }
        if (snapshot.Plan.Length > 0) snapshot.PlanSource = "Cursor 账户";
        if (snapshot.Quotas.Count == 0) throw new InvalidDataException();
        Connected(snapshot, "Cursor 当前账期", now);
        try { await ReadCursorEvents(snapshot, token, now, ct); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidDataException or TaskCanceledException)
        { snapshot.UsageAvailable = false; snapshot.Entries.Clear(); snapshot.UsageNote = "配额已读取，账户 Token 明细暂时不可用或尚未读取完整。"; }
    }

    private async Task ReadCursorEvents(ProviderSnapshot snapshot, string token, DateTimeOffset now, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        var start = new DateTimeOffset(now.LocalDateTime.Date.AddDays(-29));
        var entries = new Dictionary<string, TokenEntry>();
        long rowsRead = 0;
        for (int page = 1; page <= 100; page++)
        {
            using var request = Bearer(HttpMethod.Post, "https://api2.cursor.sh/aiserver.v1.DashboardService/GetFilteredUsageEvents", token);
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            request.Content = JsonBody(new { startDate = start.ToUnixTimeMilliseconds().ToString(), endDate = now.ToUnixTimeMilliseconds().ToString(), page, pageSize = 1000 });
            using var response = await SendAsync(_http, request, budget.Token);
            var events = response.RootElement.Get("usageEventsDisplay");
            if (events.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
            foreach (var item in events.EnumerateArray())
            {
                var entry = ParseCursorEvent(item);
                if (entry is not null && entry.Time >= start && entry.Time <= now) entries.TryAdd(entry.Key, entry);
            }
            rowsRead += events.GetArrayLength();
            var total = response.RootElement.Get("totalUsageEventsCount").Number();
            if ((total is { } expected && rowsRead >= expected) || (total is null && events.GetArrayLength() < 1000))
            { snapshot.Entries = entries.Values.ToList(); snapshot.UsageAvailable = true; return; }
            if (events.GetArrayLength() == 0) throw new InvalidDataException();
        }
        throw new InvalidDataException();
    }
    public static TokenEntry? ParseCursorEvent(JsonElement item)
    {
        var usage = item.Get("tokenUsage");
        if (usage.ValueKind != JsonValueKind.Object || item.Get("timestamp").Date() is not { } time) return null;
        var model = item.Get("model").Text();
        var identity = item.Get("id").Text();
        if (identity.Length == 0) identity = time.ToString("O") + ":" + model + ":" + item.Get("conversationId").Text() + ":" + usage.GetRawText();
        decimal? cost = usage.Get("totalCents").Number() is { } cents && cents >= 0 ? (decimal)cents / 100m : null;
        return new("cursor:" + identity, time, model, usage.Get("inputTokens").Count(), usage.Get("cacheReadTokens").Count(),
            usage.Get("cacheWriteTokens").Count(), 0, usage.Get("outputTokens").Count(), cost);
    }

    private async Task ReadAntigravityAsync(ProviderSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        snapshot.UsageNote = "Token 明细需要 Antigravity 本地会话服务。";
        var endpoints = await desktop.FindAntigravityAsync(ct);
        if (endpoints.Count == 0) { snapshot.StatusLabel = "服务未就绪"; snapshot.Status = "未检测到 Antigravity 本地用量服务"; return; }
        foreach (var endpoint in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            // Certificate exception is restricted to a loopback port owned by the detected Antigravity process.
            using var local = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false, UseProxy = false,
                ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                    request.RequestUri?.Host == "127.0.0.1" && request.RequestUri.Port == endpoint.Port
            }) { Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                using var status = await LocalCall(local, endpoint, "GetUserStatus", new { }, ct);
                snapshot.Plan = PlanParser.Antigravity(status.RootElement);
                if (snapshot.Plan.Length > 0) snapshot.PlanSource = "Antigravity 本地账户服务";
                snapshot.Quotas = QuotaParser.Antigravity(status.RootElement);
                try
                {
                    using var summary = await LocalCall(local, endpoint, "RetrieveUserQuotaSummary", new { }, ct);
                    var authoritative = QuotaParser.Antigravity(summary.RootElement);
                    if (authoritative.Count > 0) snapshot.Quotas = authoritative;
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { }
                if (snapshot.Quotas.Count == 0) continue;
                Connected(snapshot, "Antigravity 本地服务", now);
                try { await ReadTrajectories(local, endpoint, status.RootElement, snapshot, now, ct); }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
                { snapshot.UsageNote = "配额已读取；部分本机会话明细暂时不可用。"; }
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { }
        }
        snapshot.StatusLabel = "服务暂不可用"; snapshot.Status = "检测到 Antigravity，但本地用量服务暂不可用";
    }

    private static async Task ReadTrajectories(HttpClient client, LocalEndpoint endpoint, JsonElement status, ProviderSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        using var timeBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeBudget.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeBudget.Token;
        var aliases = new Dictionary<string, string>();
        foreach (var item in status.Get("userStatus").Get("cascadeModelConfigData").Get("clientModelConfigs").Items())
        {
            var alias = item.Get("modelOrAlias").Get("model").Text();
            var model = item.Get("modelId").Text();
            if (alias.Length > 0 && model.Length > 0) aliases[alias] = model;
        }
        using var index = await LocalCall(client, endpoint, "GetAllCascadeTrajectories", new { }, token);
        var summaries = index.RootElement.Get("trajectorySummaries");
        if (summaries.ValueKind != JsonValueKind.Object) return;
        snapshot.UsageAvailable = true;
        var cutoff = now.LocalDateTime.Date.AddDays(-29);
        var seen = new HashSet<string>();
        foreach (var summary in summaries.EnumerateObject())
        {
            if (summary.Value.Get("lastModifiedTime").Date() is { } modified && modified.LocalDateTime < cutoff) continue;
            var steps = summary.Value.Get("stepCount").Count();
            for (int offset = 0; offset < steps; offset += 50)
            {
                using var batch = await LocalCall(client, endpoint, "GetCascadeTrajectorySteps", new { cascadeId = summary.Name, startIndex = offset, endIndex = Math.Min(offset + 50, steps) }, token);
                foreach (var step in batch.RootElement.Get("steps").Items())
                {
                    var metadata = step.Get("metadata");
                    var usage = metadata.Get("modelUsage");
                    if (metadata.Get("createdAt").Date() is not { } time || time.LocalDateTime < cutoff || time > now) continue;
                    var key = usage.Get("messageId").Text();
                    if (key.Length == 0) key = summary.Name + ":" + time.ToString("O") + ":" + usage.ToString();
                    if (!seen.Add(key)) continue;
                    var input = usage.Get("inputTokens").Count();
                    var cached = Math.Min(input, usage.Get("cacheReadTokens").Count());
                    var model = usage.Get("model").Text();
                    snapshot.Entries.Add(new("ag:" + key, time, aliases.GetValueOrDefault(model, model), input - cached, cached, 0, 0, usage.Get("outputTokens").Count()));
                }
            }
        }
        snapshot.UsageNote = "本地服务 · 近 30 天会话记录";
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("BrimDeck/0.1");
        return request;
    }
    private static StringContent JsonBody(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    private static async Task<JsonDocument> LocalCall(HttpClient client, LocalEndpoint endpoint, string method, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.Scheme}://127.0.0.1:{endpoint.Port}/exa.language_server_pb.LanguageServerService/{method}") { Content = JsonBody(body) };
        request.Headers.TryAddWithoutValidation("x-codeium-csrf-token", endpoint.Token);
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        return await SendAsync(client, request, ct);
    }
    private static async Task<JsonDocument> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
    private static JsonDocument? ReadJson(string path) => File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null;
    private static void Connected(ProviderSnapshot snapshot, string source, DateTimeOffset now)
    { snapshot.LiveQuota = true; snapshot.QuotaTime = now; snapshot.Source = source; snapshot.Status = "已连接"; snapshot.StatusLabel = "已连接"; }
    public void Dispose() { if (http is null) _http.Dispose(); _gate.Dispose(); }
}
