using System.Collections.Concurrent;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Core;

// 贴盘策略的唯一实现：缓存目录、快照目录、原子写入。
// 任何一次写入都先写临时文件再覆盖移动，崩溃或断电最多丢掉最新一份，不会留下半个 JSON 让下次启动解析失败。
public static class WeatherFiles
{
    public static string Root { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck");
    public static string CacheRoot => Path.Combine(Root, "weather-cache");
    public static string Snapshots => Path.Combine(Root, "weather", "snapshot");
    // 仅供自动化测试：把落盘目录改到独立临时目录，测试结束再改回来。
    public static void UseRoot(string root) => Root = root;

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
// - 每条独立过期，过期时间由调用方按数据种类决定（实况约 10–15 分钟、逐天 2 小时、历史昨天 24 小时 / 更早 7 天）。
// - 同一键并发只放一个请求出去，避免「刷新时连点两下」把同一份数据拉两遍。
// - 请求失败时退回过期副本：断网启动仍然能画满界面，新鲜度由快照的 FetchedAt 标注。
public static class WeatherCache
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
    private sealed class PendingKey
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
        public int Writes;
        public object? Latest;
    }
    private static readonly Dictionary<string, PendingKey> Pending = new(StringComparer.Ordinal);
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
    // 历史是定稿数据，手动刷新没有理由把它整段重拉：Cached 在当前执行流里把绕过层临时归零，
    // 退出时恢复原值，于是同一轮刷新里的实况/预报照旧强制，历史仍按各自的 24 小时/7 天过期。
    public static IDisposable Cached()
    {
        int saved = Bypass.Value;
        Bypass.Value = 0;
        return new CachedScope(saved);
    }
    private sealed class CachedScope(int saved) : IDisposable
    {
        public void Dispose() => Bypass.Value = saved;
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
        PendingKey pending;
        int observed;
        lock (Pending)
        {
            if (!Pending.TryGetValue(id, out var existing)) Pending[id] = existing = new();
            pending = existing;
            pending.Users++;
            observed = pending.Writes;
        }
        bool entered = false;
        try
        {
            await pending.Gate.WaitAsync(token);
            entered = true;
            if (freshOnly && observed != pending.Writes && pending.Latest is T shared) return shared;
            // 排队期间前一个请求可能已经写好，命中就不必再发一次。
            if (!freshOnly && TryLookup<T>(id, ns, key, out value, out at) && DateTimeOffset.Now - at < ttl) return value;
            // 委托解析响应抛 JsonException 时同样回退过期缓存，而不是把异常直接抛给调用方。
            T? fresh;
            try { fresh = await fetch(token); }
            catch (JsonException) { fresh = null; }
            if (fresh is not null)
            {
                Store(id, ns, key, fresh);
                lock (Pending) { pending.Latest = fresh; pending.Writes++; }
                return fresh;
            }
            return allowStale && TryLookup<T>(id, ns, key, out var stale, out _) ? stale : null;
        }
        finally
        {
            if (entered) pending.Gate.Release();
            lock (Pending)
                if (--pending.Users == 0) { Pending.Remove(id); pending.Gate.Dispose(); }
        }
    }

    private static bool TryLookup<T>(string id, string ns, string key, out T value, out DateTimeOffset at) where T : class
    {
        value = null!; at = default;
        if (Memory.TryGetValue(id, out var entry) && entry.Value is T hit) { Touch(ns, key); value = hit; at = entry.At; return true; }
        string path = Path.Combine(WeatherFiles.CacheRoot, ns, WeatherFiles.Name(key, ".json"));
        try
        {
            if (!File.Exists(path)) return false;
            Envelope? envelope;
            using (var stream = File.OpenRead(path))
                envelope = JsonSerializer.Deserialize<Envelope>(stream, Json);
            if (envelope is null || envelope.V != Schema || envelope.K != key || envelope.Value.Length == 0) { File.Delete(path); return false; }
            if (JsonSerializer.Deserialize<T>(envelope.Value, Json) is not { } parsed) return false;
            // 读盘期间内存可能已被并发请求写入更新鲜的值，别拿磁盘旧值盖掉。
            var disk = new Entry { Value = parsed, At = envelope.At };
            while (true)
            {
                if (!Memory.TryGetValue(id, out var kept))
                {
                    if (Memory.TryAdd(id, disk)) break;
                }
                else if (kept.At >= disk.At)
                {
                    if (kept.Value is not T newer) return false;
                    value = newer; at = kept.At; return true;
                }
                else if (Memory.TryUpdate(id, disk, kept)) break;
            }
            Touch(ns, key);
            TrimMemory();
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
        TrimMemory();
    }

    private static void TrimMemory()
    {
        if (Memory.Count <= MemoryLimit) return;
        foreach (var pair in Memory.OrderBy(pair => pair.Value.At).Take(Memory.Count - MemoryLimit).ToArray())
            Memory.TryRemove(pair);
    }

    // 启动时清掉长期不用的条目（按最后访问时间：命中缓存会刷新文件时间戳）：历史按日期缓存，城市换得勤会留下不少一次性文件。
    private static int _pruned;
    public static void ReleaseMemory() => Memory.Clear();

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
