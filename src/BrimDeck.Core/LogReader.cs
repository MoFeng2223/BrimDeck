using System.Text.Json;

namespace BrimDeck.Core;

public sealed class LogReader
{
    private sealed record CacheItem(long Length, DateTime Modified, List<TokenEntry> Entries, JsonElement Quota, DateTimeOffset? QuotaTime, int Errors);
    private readonly Dictionary<string, CacheItem> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ProviderSnapshot Read(ProviderId id, string root, DateTimeOffset now)
    {
        var result = new ProviderSnapshot(id);
        var cutoff = new DateTimeOffset(now.LocalDateTime.Date.AddDays(-29), now.ToLocalTime().Offset);
        var folders = id == ProviderId.Codex ? new[] { "sessions", "archived_sessions" } : new[] { "projects" };
        var entries = new Dictionary<string, TokenEntry>();
        int count = 0, errors = 0;
        JsonElement latestQuota = default;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var folder in folders)
        {
            var path = Path.Combine(root, folder);
            if (!Directory.Exists(path)) continue;
            result.UsageAvailable = true;
            foreach (var file in Directory.EnumerateFiles(path, "*.jsonl", options))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoff.UtcDateTime) continue;
                    if (!_cache.TryGetValue(file, out var cached) || cached.Length != info.Length || cached.Modified != info.LastWriteTimeUtc)
                        _cache[file] = cached = ParseFile(file, id, cutoff, info);
                    count++; errors += cached.Errors;
                    foreach (var entry in cached.Entries.Where(e => e.Time >= cutoff && e.Time <= now && e.Total > 0))
                    {
                        // Streaming Claude records can repeat an id; keep its most complete usage.
                        if (!entries.TryGetValue(entry.Key, out var old) || entry.Total >= old.Total) entries[entry.Key] = entry;
                    }
                    if (cached.QuotaTime is { } time && (result.QuotaTime is null || time > result.QuotaTime))
                    { result.QuotaTime = time; latestQuota = cached.Quota; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
            }
        }
        result.Entries = entries.Values.OrderBy(x => x.Time).ToList();
        result.UsageNote = result.UsageAvailable ? $"本机近 30 天 · {count} 个记录文件" : "未找到本机会话记录。";
        if (errors > 0) result.UsageNote += $" · {errors} 处记录未能读取";
        if (latestQuota.ValueKind == JsonValueKind.Object)
        {
            result.Quotas = QuotaParser.Codex(latestQuota, result.QuotaTime ?? now, false);
            result.Plan = latestQuota.Get("plan_type").Text();
            result.Source = "本地配额快照";
            result.Status = "本地记录";
        }
        return result;
    }

    private static CacheItem ParseFile(string file, ProviderId id, DateTimeOffset cutoff, FileInfo info)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var entries = new List<TokenEntry>();
        string model = "unknown";
        string lastTotal = "";
        JsonElement quota = default;
        DateTimeOffset? quotaTime = null;
        int errors = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.Contains("\"token_count\"", StringComparison.Ordinal) && !line.Contains("\"turn_context\"", StringComparison.Ordinal) &&
                !line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var obj = doc.RootElement;
                var time = obj.Get("timestamp").Date();
                var type = obj.Get("type").Text();
                if (id == ProviderId.Codex)
                {
                    var payload = obj.Get("payload");
                    if (type == "turn_context") { model = payload.Get("model").Text(); continue; }
                    if (type != "event_msg" || payload.Get("type").Text() != "token_count" || time is null) continue;
                    var limits = payload.Get("rate_limits");
                    if (limits.ValueKind == JsonValueKind.Object && (limits.Get("limit_id").Text() is "" or "codex") && (quotaTime is null || time >= quotaTime))
                    { quota = limits.Clone(); quotaTime = time; }
                    var usageInfo = payload.Get("info");
                    var cumulative = usageInfo.Get("total_token_usage");
                    var identity = cumulative.ValueKind == JsonValueKind.Object ? cumulative.GetRawText() : "";
                    if (identity.Length > 0)
                    {
                        if (identity == lastTotal) continue;
                        lastTotal = identity;
                    }
                    var usage = usageInfo.Get("last_token_usage");
                    if (usage.ValueKind != JsonValueKind.Object || time < cutoff) continue;
                    long input = usage.Get("input_tokens").Count(), cached = Math.Min(input, usage.Get("cached_input_tokens").Count());
                    var write = usage.Get("cache_write_input_tokens").Count();
                    var output = usage.Get("output_tokens").Count();
                    // Timestamp + cumulative counters deduplicate history copied into a fork.
                    entries.Add(new($"codex:{time:O}:{identity}:{usage.GetRawText()}", time.Value, model, input - cached, cached, write, 0, output));
                }
                else
                {
                    if (type != "assistant" || time is null || time < cutoff) continue;
                    var message = obj.Get("message");
                    var usage = message.Get("usage");
                    if (usage.ValueKind != JsonValueKind.Object) continue;
                    var creation = usage.Get("cache_creation_input_tokens").Count();
                    var hour = Math.Min(creation, usage.Get("cache_creation").Get("ephemeral_1h_input_tokens").Count());
                    var key = message.Get("id").Text();
                    if (key.Length == 0) key = obj.Get("uuid").Text();
                    if (key.Length == 0) key = $"{time:O}:{usage.GetRawText()}";
                    entries.Add(new("claude:" + key, time.Value, message.Get("model").Text(), usage.Get("input_tokens").Count(),
                        usage.Get("cache_read_input_tokens").Count(), creation - hour, hour, usage.Get("output_tokens").Count()));
                }
            }
            catch (JsonException) { errors++; }
        }
        return new(info.Length, info.LastWriteTimeUtc, entries, quota, quotaTime, errors);
    }
}

public static class QuotaParser
{
    public static List<Quota> Codex(JsonElement root, DateTimeOffset now, bool online)
    {
        var limits = online ? root.Get("rate_limit") : root;
        var result = new List<Quota>();
        foreach (var field in online ? new[] { "primary_window", "secondary_window" } : new[] { "primary", "secondary" })
        {
            var window = limits.Get(field);
            if (window.Get("used_percent").Number() is not { } used) continue;
            int? minutes = online ? (window.Get("limit_window_seconds").Number() is { } seconds ? (int)(seconds / 60) : null) :
                (window.Get("window_minutes").Number() is { } mins ? (int)mins : null);
            var reset = window.Get(online ? "reset_at" : "resets_at").Date();
            if (reset is null && window.Get("reset_after_seconds").Number() is { } after && after is >= 0 and < 31536000) reset = now.AddSeconds(after);
            string label = minutes switch { 300 => "5 小时额度", 10080 => "每周额度", > 0 => $"{minutes} 分钟额度", _ => field.StartsWith("primary") ? "主要额度" : "次要额度" };
            result.Add(new(label, Math.Clamp(used, 0, 100), reset, minutes));
        }
        return result;
    }
    public static List<Quota> Claude(JsonElement root) => new[] { ("five_hour", "5 小时额度", 300), ("seven_day", "每周额度", 10080) }
        .Where(x => root.Get(x.Item1).Get("utilization").Number() is not null)
        .Select(x => new Quota(x.Item2, Math.Clamp(root.Get(x.Item1).Get("utilization").Number()!.Value, 0, 100), root.Get(x.Item1).Get("resets_at").Date(), x.Item3)).ToList();
    public static List<Quota> Cursor(JsonElement root)
    {
        var plan = root.Get("planUsage");
        if (plan.ValueKind != JsonValueKind.Object) plan = root.Get("individualUsage").Get("plan");
        var reset = root.Get("billingCycleEnd").Date();
        var result = new List<Quota>();
        foreach (var (key, label) in new[] { ("autoPercentUsed", "Cursor 模型"), ("apiPercentUsed", "其他模型") })
            if (plan.Get(key).Number() is { } percent) result.Add(new(label, Math.Clamp(percent, 0, 100), reset));
        if (result.Count == 0 && plan.Get("totalPercentUsed").Number() is { } total) result.Add(new("账期额度", Math.Clamp(total, 0, 100), reset));
        return result;
    }
    public static List<Quota> Antigravity(JsonElement root)
    {
        var result = new List<Quota>();
        var groups = root.Get("response").Get("groups");
        if (groups.ValueKind != JsonValueKind.Array) groups = root.Get("groups");
        foreach (var group in groups.Items())
            foreach (var bucket in group.Get("buckets").Items())
                if (bucket.Get("remainingFraction").Number() is { } fraction)
                {
                    var id = bucket.Get("bucketId").Text();
                    var label = id switch { "gemini-5h" => "Gemini · 5 小时", "gemini-weekly" => "Gemini · 每周", "3p-5h" => "Claude · 5 小时", "3p-weekly" => "Claude · 每周", _ => id };
                    int? minutes = id switch { "gemini-5h" or "3p-5h" => 300, "gemini-weekly" or "3p-weekly" => 10080, _ => null };
                    result.Add(new(label, (1 - Math.Clamp(fraction, 0, 1)) * 100, bucket.Get("resetTime").Date(), minutes));
                }
        if (result.Count > 0) return result;
        var configs = root.Get("userStatus").Get("cascadeModelConfigData").Get("clientModelConfigs");
        foreach (var model in configs.Items())
            if (model.Get("quotaInfo").Get("remainingFraction").Number() is { } fraction)
                result.Add(new(model.Get("label").Text(), (1 - Math.Clamp(fraction, 0, 1)) * 100, model.Get("quotaInfo").Get("resetTime").Date()));
        return result;
    }
}
