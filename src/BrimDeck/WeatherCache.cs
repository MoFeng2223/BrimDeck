using BrimDeck.Core;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck;

// 贴盘策略的唯一实现：缓存目录、快照目录、原子写入。
// 任何一次写入都先写临时文件再覆盖移动，崩溃或断电最多丢掉最新一份，不会留下半个 JSON 让下次启动解析失败。
internal static class WeatherFiles
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck");
    public static string CacheRoot { get; } = Path.Combine(Root, "weather-cache");
    public static string Snapshots { get; } = Path.Combine(Root, "weather", "snapshot");

    public static void Ensure(string folder) { try { Directory.CreateDirectory(folder); } catch { } }

    public static bool WriteAtomic(string path, string text)
    {
        // 临时名带 Guid：并发写入共用同一个 .tmp 会互相踩踏，唯一名 + 原子替换才各自安全。
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Ensure(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, text);
            File.Move(temp, path, true);
            return true;
        }
        catch { try { File.Delete(temp); } catch { } return false; }
    }

    // 可读前缀 + 短哈希：同一份缓存永远落到同一个文件，不同键几乎不可能同名。
    public static string Name(string key, string extension)
    {
        var builder = new System.Text.StringBuilder(key.Length);
        foreach (char ch in key) builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        string readable = builder.ToString().Trim('-');
        if (readable.Length == 0) readable = "entry";
        if (readable.Length > 48) readable = readable[..48];
        uint hash = 2166136261u;
        foreach (char ch in key) { hash ^= ch; hash *= 16777619u; }
        return $"{readable}-{hash:x8}{extension}";
    }
}

// 两级缓存：内存优先，命中即返回；未命中读盘；都没有才发请求，结果写回内存与磁盘。
// - 每条独立过期，过期时间由调用方按数据种类决定（实况 10 分钟、逐天 2 小时、历史 7 天）。
// - 同一键并发只放一个请求出去，避免「刷新时连点两下」把同一份数据拉两遍。
// - 请求失败时退回过期副本：断网启动仍然能画满界面，新鲜度由快照的 FetchedAt 标注。
internal static class WeatherCache
{
    private const int Schema = 1;
    private const int MemoryLimit = 400;

    private sealed class Entry
    {
        public object Value = null!;
        public DateTimeOffset At;
    }

    // 磁盘格式：版本 + 键 + 写入时刻 + 序列化后的值。K 用来复核文件名哈希没有撞车。
    private sealed class Envelope
    {
        public int V { get; set; }
        public string K { get; set; } = "";
        public DateTimeOffset At { get; set; }
        public string Value { get; set; } = "";
    }

    private static readonly ConcurrentDictionary<string, Entry> Memory = new(StringComparer.Ordinal);
    // 固定 32 个分片闸门代替每键一把锁：键只增不减会让锁字典无限膨胀；按哈希取模分片，同键依旧互斥。
    private static readonly SemaphoreSlim[] Shards = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static SemaphoreSlim Gate(string id) => Shards[(uint)id.GetHashCode() % (uint)Shards.Length];
    // 手动刷新要绕过的只是「还没过期就直用缓存」这一层：AsyncLocal 随执行流进入并行的每个来源请求，
    // 自动刷新与其它调用看不到它；读取仍会写回缓存，失败照旧退回过期副本。
    private static readonly AsyncLocal<int> Bypass = new();
    public static IDisposable Fresh()
    {
        Bypass.Value++;
        return new FreshScope();
    }
    private sealed class FreshScope : IDisposable
    {
        public void Dispose() { if (Bypass.Value > 0) Bypass.Value--; }
    }
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<T?> GetAsync<T>(string ns, string key, TimeSpan ttl, Func<CancellationToken, Task<T?>> fetch, CancellationToken token, bool allowStale = true) where T : class
    {
        string id = ns + "/" + key;
        bool freshOnly = Bypass.Value > 0;
        if (!freshOnly && TryLookup<T>(id, ns, key, out var value, out var at) && DateTimeOffset.Now - at < ttl) return value;
        var gate = Gate(id);
        await gate.WaitAsync(token);
        try
        {
            // 排队期间前一个请求可能已经写好，命中就不必再发一次。
            if (!freshOnly && TryLookup<T>(id, ns, key, out value, out at) && DateTimeOffset.Now - at < ttl) return value;
            // 委托解析响应抛 JsonException 时同样回退过期缓存，而不是把异常直接抛给调用方。
            T? fresh;
            try { fresh = await fetch(token); }
            catch (JsonException) { fresh = null; }
            if (fresh is not null) { Store(id, ns, key, fresh); return fresh; }
            return allowStale && TryLookup<T>(id, ns, key, out var stale, out _) ? stale : null;
        }
        finally { gate.Release(); }
    }

    private static bool TryLookup<T>(string id, string ns, string key, out T value, out DateTimeOffset at) where T : class
    {
        value = null!; at = default;
        if (Memory.TryGetValue(id, out var entry) && entry.Value is T hit) { Touch(ns, key); value = hit; at = entry.At; return true; }
        string path = Path.Combine(WeatherFiles.CacheRoot, ns, WeatherFiles.Name(key, ".json"));
        try
        {
            if (!File.Exists(path)) return false;
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), Json);
            if (envelope is null || envelope.V != Schema || envelope.K != key || envelope.Value.Length == 0) { File.Delete(path); return false; }
            if (JsonSerializer.Deserialize<T>(envelope.Value, Json) is not { } parsed) return false;
            // 读盘期间内存可能已被并发请求写入更新鲜的值，别拿磁盘旧值盖掉。
            if (Memory.TryGetValue(id, out var kept) && kept.Value is T newer && kept.At >= envelope.At) { value = newer; at = kept.At; return true; }
            Touch(ns, key);
            Remember(id, parsed, envelope.At);
            value = parsed; at = envelope.At;
            return true;
        }
        catch { return false; }
    }

    // 「14 天未用自动清理」按最后访问时间算：命中即刷新对应缓存文件的时间戳（文件不存在就算了）。
    private static void Touch(string ns, string key)
    {
        try { File.SetLastWriteTime(Path.Combine(WeatherFiles.CacheRoot, ns, WeatherFiles.Name(key, ".json")), DateTime.Now); } catch { }
    }

    private static void Store(string id, string ns, string key, object value)
    {
        var now = DateTimeOffset.Now;
        Remember(id, value, now);
        try
        {
            var envelope = new Envelope { V = Schema, K = key, At = now, Value = JsonSerializer.Serialize(value, value.GetType(), Json) };
            WeatherFiles.WriteAtomic(Path.Combine(WeatherFiles.CacheRoot, ns, WeatherFiles.Name(key, ".json")), JsonSerializer.Serialize(envelope, Json));
        }
        catch { }
    }

    private static void Remember(string id, object value, DateTimeOffset at)
    {
        Memory[id] = new Entry { Value = value, At = at };
        if (Memory.Count <= MemoryLimit) return;
        foreach (var pair in Memory.OrderBy(pair => pair.Value.At).Take(Memory.Count - MemoryLimit).ToArray())
            Memory.TryRemove(pair.Key, out _);
    }

    // 启动时清掉长期不用的条目（按最后访问时间：命中缓存会刷新文件时间戳）：历史按日期缓存，城市换得勤会留下不少一次性文件。
    private static int _pruned;
    public static void Prune(TimeSpan age)
    {
        if (Interlocked.Exchange(ref _pruned, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(WeatherFiles.CacheRoot)) return;
                var limit = DateTime.Now - age;
                foreach (var file in Directory.EnumerateFiles(WeatherFiles.CacheRoot, "*.json", SearchOption.AllDirectories))
                {
                    if (File.GetLastWriteTime(file) >= limit) continue;
                    // 删除前复核时间戳：检查与删除之间刷新任务可能刚写过该文件，别误删新副本。
                    if (File.GetLastWriteTime(file) >= limit) continue;
                    File.Delete(file);
                }
            }
            catch { }
        });
    }

    public static (int Count, long Bytes) Usage()
    {
        int count = 0; long bytes = 0;
        try
        {
            if (Directory.Exists(WeatherFiles.CacheRoot))
                foreach (var file in Directory.EnumerateFiles(WeatherFiles.CacheRoot, "*.json", SearchOption.AllDirectories))
                {
                    count++;
                    bytes += new FileInfo(file).Length;
                }
        }
        catch { }
        return (count, bytes);
    }
}

// 请求闸门：退避 + 每日预算。
// 和风文档明确「不要在被限流时硬重试，会被判定为攻击并冻结账号」，因此 401/403 直接停 30 分钟，
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
        ["weathercomcn"] = 600,
        ["openmeteo"] = 1200
    };
    private static readonly object Sync = new();
    private static bool _dirty;
    private static bool _restored;
    private static Timer? _flushTimer;

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
            int monthly = source == "hefeng" ? limits?.HeFengMonthlyLimit ?? 50000 : 0;
            if ((daily > 0 && state.Count >= daily) || (monthly > 0 && state.MonthCount >= monthly)) return false;
            state.Count++;
            state.MonthCount++;
            _dirty = true;   // 所有来源统一标记，交给定时器合并落盘，不再只给和风单开一条路径
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
            _dirty = true;
            // 4xx 里只有认证与限流代表「这个来源整体不可用」：
            // 401/403 认证失败不会自己好，停半小时，别把 key 打到被冻结；
            // 429 按指数退避（和风限流按分钟窗口恢复），并递增代次作废所有在途请求的响应。
            if (status is 401 or 403) { state.Until = now + TimeSpan.FromMinutes(30); return; }
            if (status is 429)
            {
                state.Generation++;
                state.Failures++;
                state.Until = now + TimeSpan.FromMinutes(Math.Min(60, 5 * Math.Pow(2, Math.Max(0, state.Failures - 1))));
                return;
            }
            // 400/404/422 是「这一个请求」的语义错误：参数不支持、地点不存在、坐标超出接口范围。
            // 它们不代表来源不可用，也绝不能参与失败计数——否则一次 400 会把后续 429 的
            // 指数退避直接推到最长，把来源冷却一小时。实际踩过的坑：
            // 和风分钟降水对国外坐标固定返回 400，按失败处理后闸门停 2 分钟，
            // 紧随其后的时光机/预警/指数请求全部被拒，国外城市的「时光机」于是永远空白；
            // 城市搜索遇到无结果或特殊字符同样会收到 400，熔断后几分钟内所有搜索都落空。
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
        Schedule();
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
        lock (Sync) FlushCore();
    }

    private static void FlushCore()
    {
        if (!_dirty) return;
        var counts = States.ToDictionary(p => p.Key, p => new Usage(p.Value.Day, p.Value.Count, p.Value.MonthCount));
        if (WeatherFiles.WriteAtomic(UsagePath, JsonSerializer.Serialize(counts, Json))) _dirty = false;
    }

    // 计数不再逐请求写盘：30 秒合并一次，进程被终止最多丢半分钟的用量。
    private static void Schedule()
    {
        if (_flushTimer is not null) return;
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }
}

// 一次请求的结果：Body 为空时 Status 说明原因（0 = 被闸门拦下；非 2xx 时为 HTTP 状态码；408 = 超时；-1 = 网络异常）。
internal readonly record struct HttpResult(string? Body, int Status);

// 统一的网络入口：显式解压、固定 UA、超时、闸门与计数都在这里，来源只关心 URL 和解析。
internal static class WeatherHttp
{
    public static readonly HttpClient Client = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
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