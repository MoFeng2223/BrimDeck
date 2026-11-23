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
        try
        {
            Ensure(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, true);
            return true;
        }
        catch { return false; }
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
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
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
        if (TryLookup<T>(id, ns, key, out var value, out var at) && DateTimeOffset.Now - at < ttl) return value;
        var gate = Gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            // 排队期间前一个请求可能已经写好，命中就不必再发一次。
            if (TryLookup<T>(id, ns, key, out value, out at) && DateTimeOffset.Now - at < ttl) return value;
            var fresh = await fetch(token);
            if (fresh is not null) { Store(id, ns, key, fresh); return fresh; }
            return allowStale && TryLookup<T>(id, ns, key, out var stale, out _) ? stale : null;
        }
        finally { gate.Release(); }
    }

    private static bool TryLookup<T>(string id, string ns, string key, out T value, out DateTimeOffset at) where T : class
    {
        value = null!; at = default;
        if (Memory.TryGetValue(id, out var entry) && entry.Value is T hit) { value = hit; at = entry.At; return true; }
        string path = Path.Combine(WeatherFiles.CacheRoot, ns, WeatherFiles.Name(key, ".json"));
        try
        {
            if (!File.Exists(path)) return false;
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), Json);
            if (envelope is null || envelope.V != Schema || envelope.K != key || envelope.Value.Length == 0) { File.Delete(path); return false; }
            if (JsonSerializer.Deserialize<T>(envelope.Value, Json) is not { } parsed) return false;
            Remember(id, parsed, envelope.At);
            value = parsed; at = envelope.At;
            return true;
        }
        catch { return false; }
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
        foreach (var pair in Memory.OrderBy(pair => pair.Value.At).Take(Memory.Count - MemoryLimit).ToArray()) Memory.TryRemove(pair.Key, out _);
    }

    // 启动时清掉长期不用的条目：历史按日期缓存，城市换得勤会留下不少一次性文件。
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
                    if (File.GetLastWriteTime(file) < limit) File.Delete(file);
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
    }

    private static readonly ConcurrentDictionary<string, State> States = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> DailyLimit = new(StringComparer.Ordinal)
    {
        ["hefeng"] = 800,        // 免费订阅按 key 计日额度，留出余量
        ["hefeng-search"] = 500, // 城市搜索有 1 天磁盘缓存，实际请求很少，单独计数即可
        ["weathercomcn"] = 600,
        ["openmeteo"] = 1200
    };
    private static readonly object Sync = new();
    private static bool _dirty;
    private static bool _restored;

    public static bool TryEnter(string source)
    {
        var now = DateTimeOffset.Now;
        var state = States.GetOrAdd(source, _ => new State());
        lock (Sync)
        {
            var today = DateOnly.FromDateTime(now.LocalDateTime);
            if (state.Day != today) { state.Day = today; state.Count = 0; }
            if (state.Until > now) return false;
            if (state.Count >= DailyLimit.GetValueOrDefault(source, 500)) return false;
            state.Count++;
            _dirty = true;
            return true;
        }
    }

    public static void Report(string source, int status)
    {
        var state = States.GetOrAdd(source, _ => new State());
        lock (Sync)
        {
            var now = DateTimeOffset.Now;
            if (status is >= 200 and < 300) { state.Failures = 0; state.Until = default; return; }
            _dirty = true;
            // 4xx 里只有认证与限流代表「这个来源整体不可用」：
            // 401/403 认证失败不会自己好，停半小时，别把 key 打到被冻结；
            // 429 按指数退避（和风限流按分钟窗口恢复）。
            if (status is 401 or 403) { state.Until = now + TimeSpan.FromMinutes(30); return; }
            if (status is 429)
            {
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

    // 计数落盘只是为了重启后不会把当天额度重新算满；每 15 分钟随快照保存一次即可，不必逐次写。
    public static void Restore()
    {
        if (_restored) return;
        _restored = true;
        try
        {
            string path = Path.Combine(WeatherFiles.CacheRoot, "usage.json");
            if (!File.Exists(path)) return;
            if (JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path), Json) is not { } saved) return;
            var today = DateOnly.FromDateTime(DateTime.Now);
            foreach (var (source, count) in saved) States[source] = new State { Day = today, Count = count };
        }
        catch { }
    }

    public static void Flush()
    {
        Dictionary<string, int> counts;
        lock (Sync)
        {
            if (!_dirty) return;
            _dirty = false;
            var today = DateOnly.FromDateTime(DateTime.Now);
            counts = States.Where(pair => pair.Value.Day == today).ToDictionary(pair => pair.Key, pair => pair.Value.Count);
        }
        try { WeatherFiles.WriteAtomic(Path.Combine(WeatherFiles.CacheRoot, "usage.json"), JsonSerializer.Serialize(counts, Json)); } catch { }
    }
}

// 一次请求的结果：Body 为空时 Status 说明原因（0 = 被闸门拦下；非 2xx 时为 HTTP 状态码；408 = 超时；-1 = 网络异常）。
internal readonly record struct HttpResult(string? Body, int Status);

// 统一的网络入口：显式解压、固定 UA、超时、闸门与计数都在这里，来源只关心 URL 和解析。
internal static class WeatherHttp
{
    public static readonly HttpClient Client = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };

    static WeatherHttp()
    {
        // 浏览器 UA 加应用标识：国内站点对非浏览器 UA 偶尔会长时间不响应，MET Norway 则要求 UA 可识别。
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 BrimDeck/1.4");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
    }

    public static async Task<HttpResult> GetAsync(string source, string url, CancellationToken token, Action<HttpRequestMessage>? decorate = null, int seconds = 20)
    {
        if (!RequestGate.TryEnter(source)) return new(null, 0);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            decorate?.Invoke(request);
            using var response = await Client.SendAsync(request, cts.Token);
            int status = (int)response.StatusCode;
            RequestGate.Report(source, status);
            if (!response.IsSuccessStatusCode) return new(null, status);
            return new(await response.Content.ReadAsStringAsync(cts.Token), status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { RequestGate.Report(source, 408); return new(null, 408); }
        catch { RequestGate.Report(source, -1); return new(null, -1); }
    }
}
