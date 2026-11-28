using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck;

// 请求闸门、请求结果与统一 HTTP 入口；真正的缓存实现在 BrimDeck.Core/WeatherCache.cs。
// 本文件只关心「能不能发、怎么发、结果如何」，不负责数据缓存与快照落盘。

// 请求闸门：退避 + 每日预算。
// 和风文档明确「不要在被限流时硬重试，会被判定为攻击并冻结账号」，因此 401/402/403 直接停 30 分钟，
// 429 指数退避，网络失败短退避；成功即清零。预算只是防跑飞的安全网，正常用量远达不到。
internal static class RequestGate
{
    private sealed class State
    {
        public int Failures;
        public DateTimeOffset Until;
        public DateOnly Day;
        public int Count;
        public int MonthCount;
        public int Generation;
    }

    private static readonly ConcurrentDictionary<string, State> States = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> DailyLimit = new(StringComparer.Ordinal)
    {
        [ChinaDailySupplement.Id] = 600,
        ["openmeteo"] = 1200,
        // 地名搜索与天气取数分通道：冷却互不牵连，额度单独配置，不落到默认 500 次且无月上限的策略。
        [HeFengSource.GeoId] = 500
    };
    private static readonly Dictionary<string, int> MonthlyLimit = new(StringComparer.Ordinal)
    {
        [HeFengSource.GeoId] = 10000
    };
    private static readonly object Sync = new();
    private static bool _dirty;
    private static bool _restored;
    private static Timer? _flushTimer;
    private static int _flushFailures;

    public static bool TryEnter(string source, WeatherSettings? limits)
    {
        lock (Sync)
        {
            RestoreCore();
            var state = States.GetOrAdd(source, _ => new State());
            var clock = DateTimeOffset.Now;
            var today = DateOnly.FromDateTime(clock.LocalDateTime);
            if (state.Day.Year != today.Year || state.Day.Month != today.Month) state.MonthCount = 0;
            if (state.Day != today) { state.Day = today; state.Count = 0; }
            if (state.Until > clock) return false;
            int daily = source == "hefeng" ? limits?.HeFengDailyLimit ?? 1500 : DailyLimit.GetValueOrDefault(source, 500);
            int monthly = source == "hefeng" ? limits?.HeFengMonthlyLimit ?? 50000 : MonthlyLimit.GetValueOrDefault(source, 0);
            if ((daily > 0 && state.Count >= daily) || (monthly > 0 && state.MonthCount >= monthly)) return false;
            state.Count++;
            state.MonthCount++;
            MarkDirty();   // 所有来源统一标记，交给定时器合并落盘，不再只给和风单开一条路径
            return true;
        }
    }

    // 请求代次：429 时递增。响应回来只对「发起时看到的代次仍等于当前代次」的自己生效，
    // 否则后发请求的 429 刚设好退避，早发请求的 200 又把它清掉，限流形同虚设。
    public static int Generation(string source)
    {
        lock (Sync) return States.TryGetValue(source, out var state) ? state.Generation : 0;
    }

    public static void Report(string source, int status, int generation)
    {
        var state = States.GetOrAdd(source, _ => new State());
        lock (Sync)
        {
            if (state.Generation != generation) return;
            var now = DateTimeOffset.Now;
            if (status is >= 200 and < 300) { state.Failures = 0; state.Until = default; return; }
            MarkDirty();
            // 4xx 里只有认证/额度/限流代表「这个来源整体不可用」：
            // 401/402/403 不会靠重试恢复，停半小时；429 按指数退避并递增代次作废在途响应。
            if (status is 401 or 402 or 403) { state.Until = now + TimeSpan.FromMinutes(30); return; }
            if (status is 429)
            {
                state.Generation++;
                state.Failures++;
                state.Until = now + TimeSpan.FromMinutes(Math.Min(60, 5 * Math.Pow(2, Math.Max(0, state.Failures - 1))));
                return;
            }
            // 400/404/422 只是「这一个请求」的语义错误（参数、地点、坐标范围），不代表来源不可用，
            // 更不参与失败计数：和风分钟降水对国外坐标固定 400，一旦计数就会把后续请求全部冷却。
            if (status is 400 or 404 or 422) return;
            // 超时和网络异常只计数：单个慢响应不该让整个来源冷却（中国天气网实况常年 4 秒级）。
            if (status is 408 or -1)
            {
                state.Failures++;
                if (state.Failures >= 3) state.Until = now + TimeSpan.FromMinutes(1);
                return;
            }
            // 5xx 才是服务端故障：短冷却，等下一轮刷新再试。
            state.Failures++;
            state.Until = now + TimeSpan.FromMinutes(2);
        }
    }

    public static int Today(string source)
    {
        if (!States.TryGetValue(source, out var state)) return 0;
        lock (Sync) return state.Day == DateOnly.FromDateTime(DateTime.Now) ? state.Count : 0;
    }

    public static TimeSpan Cooldown(string source)
    {
        if (!States.TryGetValue(source, out var state)) return TimeSpan.Zero;
        lock (Sync) { var left = state.Until - DateTimeOffset.Now; return left > TimeSpan.Zero ? left : TimeSpan.Zero; }
    }

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record Usage(DateOnly Day, int Count, int MonthCount);
    private static string UsagePath => Path.Combine(WeatherFiles.CacheRoot, "usage.json");

    public static int ThisMonth(string source)
    {
        lock (Sync)
        {
            var now = DateTime.Now;
            return States.TryGetValue(source, out var state) && state.Day.Year == now.Year && state.Day.Month == now.Month
                ? state.MonthCount : 0;
        }
    }

    public static void Restore()
    {
        lock (Sync) RestoreCore();
    }

    private static void RestoreCore()
    {
        if (_restored) return;
        _restored = true;
        try
        {
            if (!File.Exists(UsagePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(UsagePath));
            var today = DateOnly.FromDateTime(DateTime.Now);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var usage = entry.Value.Deserialize<Usage>(Json);
                if (usage is null) continue;
                var state = States.GetOrAdd(entry.Name, _ => new State());
                state.Day = usage.Day;
                // 只恢复属于今天的当日计数：昨天落盘的计数若被标成今天，会把今天还没花的请求误拦。
                if (usage.Day == today) state.Count += usage.Count;
                // 月度累计跨天有效，同月内照常恢复（跨月由 TryEnter 的月度重置兜底）。
                if (usage.Day.Year == today.Year && usage.Day.Month == today.Month) state.MonthCount += usage.MonthCount;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }

    public static void Flush()
    {
        lock (Sync)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            FlushCore();
        }
    }

    private static void FlushCore()
    {
        if (!_dirty) return;
        var counts = States.ToDictionary(p => p.Key, p => new Usage(p.Value.Day, p.Value.Count, p.Value.MonthCount));
        if (WeatherFiles.WriteAtomic(UsagePath, JsonSerializer.Serialize(counts, Json))) { _dirty = false; _flushFailures = 0; }
        else _flushFailures++;
    }

    // 计数不再逐请求写盘：30 秒合并一次，进程被终止最多丢半分钟的用量。
    // Called under Sync. One burst schedules one write and leaves no idle polling timer.
    private static void MarkDirty()
    {
        _dirty = true;
        if (_flushTimer is null) ScheduleFlush(TimeSpan.FromSeconds(30));
    }

    // 写盘失败时脏数据还在，旧实现直接丢掉定时器导致再也不会重试；这里按 30→60→120…秒退避，
    // 成功后 FlushCore 清掉脏标记，不再安排下一次，空闲时没有轮询。
    private static void ScheduleFlush(TimeSpan delay)
    {
        _flushTimer = new Timer(_ =>
        {
            lock (Sync)
            {
                _flushTimer?.Dispose();
                _flushTimer = null;
                FlushCore();
                if (_dirty && _flushTimer is null)
                    ScheduleFlush(TimeSpan.FromSeconds(Math.Min(30 * (1 << Math.Min(_flushFailures, 4)), 300)));
            }
        }, null, delay, Timeout.InfiniteTimeSpan);
    }
}

// 一次请求的结果：Body 为空时 Status 说明原因（0 = 被闸门拦下；非 2xx 时为 HTTP 状态码；408 = 超时；-1 = 网络异常）。
internal readonly record struct HttpResult(string? Body, int Status);

// 统一的网络入口：显式解压、固定 UA、超时、闸门与计数都在这里，来源只关心 URL 和解析。
internal static class WeatherHttp
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
    // 带和风 Key 的请求专用：禁用自动重定向，改由 SendAsync 手动跟随。
    private static readonly HttpClient NoRedirect = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };

    static WeatherHttp()
    {
        // 浏览器 UA 加应用标识：国内站点对非浏览器 UA 偶尔会长时间不响应，MET Norway 则要求 UA 可识别。
        foreach (var client in new[] { Client, NoRedirect })
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 BrimDeck/1.4");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        }
    }

    public static async Task<HttpResult> GetAsync(string source, string url, CancellationToken token, Action<HttpRequestMessage>? decorate = null, int seconds = 20, WeatherSettings? limits = null)
    {
        token.ThrowIfCancellationRequested();
        if (!RequestGate.TryEnter(source, limits)) return new(null, 0);
        int generation = RequestGate.Generation(source);   // 发起时记下代次，响应回来据此决定能否更新退避
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            decorate?.Invoke(request);
            using var response = await SendAsync(request, cts.Token);
            int status = (int)response.StatusCode;
            RequestGate.Report(source, status, generation);
            if (!response.IsSuccessStatusCode) return new(null, status);
            return new(await response.Content.ReadAsStringAsync(cts.Token), status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { RequestGate.Report(source, 408, generation); return new(null, 408); }
        catch { RequestGate.Report(source, -1, generation); return new(null, -1); }
    }

    // .NET 自动跟随重定向时会把原请求的全部头（包括和风 Key）原样发给跳转目标主机，Key 就泄露了。
    // 带 Key 的请求改走手动跟随：跳转前核对主机，不同主机一律剥掉自定义头再走；同主机才保留。
    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (!request.Headers.Contains("X-QW-Api-Key")) return await Client.SendAsync(request, token);
        for (int hops = 0; ; hops++)
        {
            var response = await NoRedirect.SendAsync(request, token);
            if (hops >= 5 || (int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)
                || request.RequestUri is not { } current || response.Headers.Location is not { } location)
            { request.Dispose(); return response; }
            using var stale = response;
            using var previous = request;
            var next = new HttpRequestMessage(HttpMethod.Get, new Uri(current, location));
            var target = next.RequestUri!;
            if (target.Scheme == current.Scheme && target.Host == current.Host && target.Port == current.Port)
                foreach (var header in request.Headers) next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request = next;
        }
    }
}
