using System.Globalization;
using System.Text.Json;

namespace BrimDeck.Core;

// The routing strategy of a Mihomo / Clash proxy group.
public enum ProxyGroupKind { Selector, UrlTest, Fallback, LoadBalance, Other }

// One node (or nested group) inside a proxy group.
public sealed record ProxyNodeInfo(string Name, string Type, int? DelayMs, bool IsGroup = false);

// One proxy group exposed by Mihomo /proxies.
public sealed record ProxyGroupInfo(string Name, ProxyGroupKind Kind, string Now, IReadOnlyList<ProxyNodeInfo> Nodes, bool Hidden = false)
{
    public bool CanSelect => Kind == ProxyGroupKind.Selector;
}

// Instantaneous and cumulative proxy traffic reported by Mihomo GET /traffic.
public readonly record struct ProxyTrafficSample(long UpBytesPerSec, long DownBytesPerSec, long UpTotalBytes = 0, long DownTotalBytes = 0);

// Summary of active proxy connections and Mihomo memory footprint.
public readonly record struct ProxyConnectionsSummary(int ActiveCount, long UploadTotal, long DownloadTotal, long MemoryInUseBytes);

// Offline subscription usage metadata read from Clash Verge's local profiles.yaml (extra block only; URLs are ignored).
public sealed record ProxySubscriptionInfo(string ProfileName, long UploadBytes, long DownloadBytes, long TotalBytes, long? ExpireUnixSeconds)
{
    public long UsedBytes => Math.Max(0, UploadBytes) + Math.Max(0, DownloadBytes);
    public bool HasTraffic => TotalBytes > 0;
    public double UsedFraction => TotalBytes > 0 ? Math.Clamp((double)UsedBytes / TotalBytes, 0, 1) : 0;
}

// Delay-test and connection behavior preferences read from verge.yaml.
public sealed record ProxyLatencyConfig(string TestUrl, int TimeoutMs, bool AutoCloseConnection)
{
    public const string DefaultTestUrl = "https://www.gstatic.com/generate_204";
    public const int DefaultTimeoutMs = 5000;
    public static readonly ProxyLatencyConfig Default = new(DefaultTestUrl, DefaultTimeoutMs, true);
}

// Full state shown on the expanded Proxy control page.
public sealed record ProxyControlSnapshot(
    ProxySnapshot Status,
    bool ApiConnected,
    IReadOnlyList<ProxyGroupInfo> Groups,
    ProxySubscriptionInfo? Subscription,
    ProxyTrafficSample Traffic,
    ProxyConnectionsSummary Connections,
    ProxyLatencyConfig LatencyConfig,
    string? Error = null,
    IReadOnlyList<ProxyGroupInfo>? HiddenGroups = null)
{
    public static readonly ProxyControlSnapshot Empty = new(
        ProxySnapshot.Stopped, false, [], null, default, default, ProxyLatencyConfig.Default);
}

// Pure parsers and formatters for Mihomo REST/stream APIs and local Clash Verge metadata.
public static class ProxyControlParsers
{
    private static readonly HashSet<string> BuiltInProxies = new(StringComparer.OrdinalIgnoreCase)
    {
        "DIRECT", "REJECT", "REJECT-DROP", "PASS", "COMPATIBLE"
    };

    public static ProxyGroupKind ParseGroupKind(string? type) => (type ?? "").Trim().ToLowerInvariant() switch
    {
        "selector" => ProxyGroupKind.Selector,
        "urltest" => ProxyGroupKind.UrlTest,
        "fallback" => ProxyGroupKind.Fallback,
        "loadbalance" => ProxyGroupKind.LoadBalance,
        _ => ProxyGroupKind.Other
    };

    public static bool IsGroupType(string? type) => ParseGroupKind(type) != ProxyGroupKind.Other
        || (type ?? "").Trim().Equals("Relay", StringComparison.OrdinalIgnoreCase);

    // Parses Mihomo GET /proxies JSON into ordered visible proxy groups with member types and latest delays.
    public static List<ProxyGroupInfo> ParseMihomoProxies(string? json, ProxyMode mode = ProxyMode.Rule)
        => ParseMihomoProxiesWithHidden(json, mode).Visible;

    // Parses Mihomo GET /proxies JSON into visible groups (for display) and hidden groups (kept solely to resolve chained active nodes).
    // Preserves the user's declared group order from proxies["GLOBAL"].all whenever present.
    public static (List<ProxyGroupInfo> Visible, List<ProxyGroupInfo> Hidden) ParseMihomoProxiesWithHidden(string? json, ProxyMode mode = ProxyMode.Rule)
    {
        var visible = new List<ProxyGroupInfo>();
        var hiddenGroups = new List<ProxyGroupInfo>();
        if (string.IsNullOrWhiteSpace(json)) return (visible, hiddenGroups);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (visible, hiddenGroups);
            if (!doc.RootElement.TryGetProperty("proxies", out var proxiesObj) || proxiesObj.ValueKind != JsonValueKind.Object)
                return (visible, hiddenGroups);

            var entries = new Dictionary<string, (string Type, string Now, List<string> All, int? Delay, bool Hidden)>(StringComparer.Ordinal);
            foreach (var prop in proxiesObj.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var obj = prop.Value;
                string type = obj.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                string now = obj.TryGetProperty("now", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                bool hidden = obj.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True;
                var all = new List<string>();
                if (obj.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in a.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } member)
                            all.Add(member);
                }
                int? delay = ExtractLatestDelay(obj);
                entries[prop.Name] = (type, now, all, delay, hidden);
            }

            // Build order index from GLOBAL.all (which Mihomo populates in config proxy-groups order).
            var orderIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            if (entries.TryGetValue("GLOBAL", out var globalEntry))
            {
                for (int i = 0; i < globalEntry.All.Count; i++)
                    orderIndex.TryAdd(globalEntry.All[i], i);
            }

            foreach (var (name, entry) in entries)
            {
                if (entry.All.Count == 0) continue;
                if (BuiltInProxies.Contains(name)) continue;
                var kind = ParseGroupKind(entry.Type);
                if (kind == ProxyGroupKind.Other && !entry.Type.Equals("Relay", StringComparison.OrdinalIgnoreCase)) continue;
                bool isGlobal = name.Equals("GLOBAL", StringComparison.Ordinal);
                if (isGlobal && mode != ProxyMode.Global) continue;

                var nodes = new List<ProxyNodeInfo>(entry.All.Count);
                foreach (var memberName in entry.All)
                {
                    if (entries.TryGetValue(memberName, out var member))
                        nodes.Add(new ProxyNodeInfo(memberName, member.Type, member.Delay, member.All.Count > 0));
                    else
                        nodes.Add(new ProxyNodeInfo(memberName, "", null, false));
                }
                var info = new ProxyGroupInfo(name, kind, entry.Now, nodes, entry.Hidden);
                if (entry.Hidden) hiddenGroups.Add(info);
                else visible.Add(info);
            }

            visible.Sort((a, b) =>
            {
                bool aGlobal = a.Name.Equals("GLOBAL", StringComparison.Ordinal);
                bool bGlobal = b.Name.Equals("GLOBAL", StringComparison.Ordinal);
                if (aGlobal != bGlobal) return aGlobal ? -1 : 1;
                bool aSel = a.CanSelect;
                bool bSel = b.CanSelect;
                if (aSel != bSel) return aSel ? -1 : 1;
                int aOrd = orderIndex.TryGetValue(a.Name, out var ai) ? ai : int.MaxValue;
                int bOrd = orderIndex.TryGetValue(b.Name, out var bi) ? bi : int.MaxValue;
                if (aOrd != bOrd) return aOrd.CompareTo(bOrd);
                return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            });
            hiddenGroups.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            return (visible, hiddenGroups);
        }
        catch (JsonException) { return (visible, hiddenGroups); }
    }

    // Parses one JSON payload from Mihomo GET /traffic.
    public static bool TryParseTrafficSample(string? json, out ProxyTrafficSample sample)
    {
        sample = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var root = doc.RootElement;
            if (!TryGetInt64(root, "up", out var up) || !TryGetInt64(root, "down", out var down))
                return false;
            TryGetInt64(root, "upTotal", out var upTotal);
            TryGetInt64(root, "downTotal", out var downTotal);
            sample = new ProxyTrafficSample(Math.Max(0, up), Math.Max(0, down), Math.Max(0, upTotal), Math.Max(0, downTotal));
            return true;
        }
        catch (JsonException) { return false; }
    }

    // Parses Mihomo GET /memory payload (inuse bytes).
    public static bool TryParseMemoryBytes(string? json, out long inUseBytes)
    {
        inUseBytes = 0;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!TryGetInt64(doc.RootElement, "inuse", out var inuse)) return false;
            inUseBytes = Math.Max(0, inuse);
            return true;
        }
        catch (JsonException) { return false; }
    }

    // Parses Mihomo GET /connections payload into active count and cumulative totals.
    public static bool TryParseConnectionsSummary(string? json, out ProxyConnectionsSummary summary, long memoryInUseBytes = 0)
    {
        summary = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var root = doc.RootElement;
            bool hasTotal = TryGetInt64(root, "downloadTotal", out var down) | TryGetInt64(root, "uploadTotal", out var up);
            int count = 0;
            bool hasList = false;
            if (root.TryGetProperty("connections", out var list))
            {
                hasList = true;
                if (list.ValueKind == JsonValueKind.Array) count = list.GetArrayLength();
            }
            if (!hasTotal && !hasList) return false;
            summary = new ProxyConnectionsSummary(count, Math.Max(0, up), Math.Max(0, down), Math.Max(0, memoryInUseBytes));
            return true;
        }
        catch (JsonException) { return false; }
    }

    // Parses Mihomo GET /group/{name}/delay response: { "NodeA": 42, "NodeB": 120 }.
    public static Dictionary<string, int> ParseGroupDelayMap(string? json)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var ms) && ms > 0)
                    map[prop.Name] = ms;
            }
        }
        catch (JsonException) { }
        return map;
    }

    // Parses Mihomo GET /proxies/{name}/delay response: { "delay": 42 }.
    public static int? ParseSingleDelay(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("delay", out var prop)
                && prop.ValueKind == JsonValueKind.Number
                && prop.TryGetInt32(out var ms) && ms > 0)
                return ms;
        }
        catch (JsonException) { }
        return null;
    }

    // Reads delay-test preferences and auto_close_connection from Clash Verge's verge.yaml.
    public static ProxyLatencyConfig ParseVergeControlYaml(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return ProxyLatencyConfig.Default;
        string testUrl = ProxyLatencyConfig.DefaultTestUrl;
        int timeoutMs = ProxyLatencyConfig.DefaultTimeoutMs;
        bool autoClose = true;
        foreach (var raw in yaml.AsSpan().EnumerateLines())
        {
            if (raw.Length == 0 || char.IsWhiteSpace(raw[0])) continue;
            var line = StripComment(raw).Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var val = Unquote(line[(colon + 1)..].Trim());
            if (key.Equals("default_latency_test", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = val.ToString();
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                    && (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
                    testUrl = candidate;
            }
            else if (key.Equals("default_latency_timeout", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTimeout))
            {
                timeoutMs = Math.Clamp(parsedTimeout, 1000, 15000);
            }
            else if (key.Equals("auto_close_connection", StringComparison.OrdinalIgnoreCase))
            {
                if (val.Equals("true", StringComparison.OrdinalIgnoreCase)) autoClose = true;
                else if (val.Equals("false", StringComparison.OrdinalIgnoreCase)) autoClose = false;
            }
        }
        return new ProxyLatencyConfig(testUrl, timeoutMs, autoClose);
    }

    // Reads active profile display name and cached traffic quota (extra.upload/download/total/expire) from Clash Verge's profiles.yaml.
    // Subscription URLs and file paths are intentionally ignored.
    public static ProxySubscriptionInfo? ParseVergeProfilesYaml(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        string? currentUid = null;
        bool inItems = false;
        int itemIndent = -1;
        bool inExtra = false;
        int extraIndent = -1;

        string? uid = null, name = null;
        long upload = 0, download = 0, total = 0;
        long? expire = null;
        bool hasExtra = false;
        var parsedItems = new List<(string Uid, ProxySubscriptionInfo Info)>();

        void FlushItem()
        {
            if (uid is not null)
            {
                if (hasExtra && total > 0)
                    parsedItems.Add((uid, new ProxySubscriptionInfo(string.IsNullOrWhiteSpace(name) ? "" : name.Trim(), upload, download, total, expire is > 0 ? expire : null)));
                else if (!string.IsNullOrWhiteSpace(name))
                    parsedItems.Add((uid, new ProxySubscriptionInfo(name.Trim(), 0, 0, 0, null)));
            }
            uid = null; name = null; upload = 0; download = 0; total = 0; expire = null; hasExtra = false;
            inExtra = false; extraIndent = -1;
        }

        foreach (var raw in yaml.AsSpan().EnumerateLines())
        {
            int indent = LeadingSpaces(raw);
            var trimmed = StripComment(raw).Trim();
            if (trimmed.Length == 0) continue;

            if (indent == 0 && !(inItems && trimmed.StartsWith("- ", StringComparison.Ordinal)))
            {
                if (inItems) FlushItem();
                inItems = false;
                itemIndent = -1;
                int colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;
                var key = trimmed[..colon].Trim();
                var val = Unquote(trimmed[(colon + 1)..].Trim());
                if (key.Equals("current", StringComparison.OrdinalIgnoreCase) && val.Length > 0 && !val.Equals("null", StringComparison.OrdinalIgnoreCase))
                    currentUid = val.ToString();
                else if (key.Equals("items", StringComparison.OrdinalIgnoreCase))
                    inItems = true;
                continue;
            }

            if (!inItems) continue;

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && (itemIndent < 0 || indent <= itemIndent))
            {
                FlushItem();
                itemIndent = indent;
                var rest = trimmed[2..].Trim();
                ParseItemField(rest, ref uid, ref name, ref inExtra, ref extraIndent);
                continue;
            }

            if (inExtra)
            {
                if (extraIndent < 0) extraIndent = indent;
                if (indent >= extraIndent)
                {
                    int colon = trimmed.IndexOf(':');
                    if (colon > 0)
                    {
                        var key = trimmed[..colon].Trim();
                        var val = Unquote(trimmed[(colon + 1)..].Trim());
                        if (key.Equals("upload", StringComparison.OrdinalIgnoreCase) && long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u))
                        { upload = u; hasExtra = true; }
                        else if (key.Equals("download", StringComparison.OrdinalIgnoreCase) && long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
                        { download = d; hasExtra = true; }
                        else if (key.Equals("total", StringComparison.OrdinalIgnoreCase) && long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
                        { total = t; hasExtra = true; }
                        else if (key.Equals("expire", StringComparison.OrdinalIgnoreCase) && long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e))
                        { expire = e; hasExtra = true; }
                    }
                    continue;
                }
                inExtra = false;
                extraIndent = -1;
            }

            if (itemIndent >= 0 && indent > itemIndent)
                ParseItemField(trimmed, ref uid, ref name, ref inExtra, ref extraIndent);
        }

        if (inItems) FlushItem();
        if (currentUid is null) return null;
        ProxySubscriptionInfo? matched = null;
        foreach (var (itemUid, info) in parsedItems)
            if (itemUid.Equals(currentUid, StringComparison.Ordinal)) matched = info;
        return matched;
    }

    private static void ParseItemField(ReadOnlySpan<char> line, ref string? uid, ref string? name, ref bool inExtra, ref int extraIndent)
    {
        int colon = line.IndexOf(':');
        if (colon <= 0) return;
        var key = line[..colon].Trim();
        var val = Unquote(line[(colon + 1)..].Trim());
        if (key.Equals("uid", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
            uid = val.ToString();
        else if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            name = val.Length > 0 && !val.Equals("null", StringComparison.OrdinalIgnoreCase) ? val.ToString() : null;
        else if (key.Equals("extra", StringComparison.OrdinalIgnoreCase) && val.Length == 0)
        { inExtra = true; extraIndent = -1; }
    }

    // Resolves the active selection chain for a proxy group (e.g. Proxies -> Auto/HiddenGroup -> HK Hong Kong 01) with latest delay.
    public static (string DirectChoice, string LeafNode, int? DelayMs) ResolveActiveNode(
        IReadOnlyList<ProxyGroupInfo> groups,
        ProxyGroupInfo group,
        IReadOnlyList<ProxyGroupInfo>? hiddenGroups = null)
    {
        var direct = group.Now;
        if (string.IsNullOrWhiteSpace(direct)) return ("", "", null);
        var byName = new Dictionary<string, ProxyGroupInfo>(StringComparer.Ordinal);
        foreach (var g in groups) byName.TryAdd(g.Name, g);
        if (hiddenGroups is not null)
            foreach (var g in hiddenGroups) byName.TryAdd(g.Name, g);

        var visited = new HashSet<string>(StringComparer.Ordinal) { group.Name };
        var currentGroup = group;
        var currentChoice = direct;
        int? delay = group.Nodes.FirstOrDefault(n => n.Name.Equals(direct, StringComparison.Ordinal))?.DelayMs;

        while (byName.TryGetValue(currentChoice, out var nested) && visited.Add(nested.Name) && !string.IsNullOrWhiteSpace(nested.Now))
        {
            currentGroup = nested;
            currentChoice = nested.Now;
            var nodeDelay = currentGroup.Nodes.FirstOrDefault(n => n.Name.Equals(currentChoice, StringComparison.Ordinal))?.DelayMs;
            if (nodeDelay is not null) delay = nodeDelay;
        }
        return (direct, currentChoice, delay);
    }

    // Structural comparison of two group lists so unchanged periodic refreshes do not trigger UI rebuilds.
    public static bool GroupsEqual(IReadOnlyList<ProxyGroupInfo> a, IReadOnlyList<ProxyGroupInfo> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var ga = a[i]; var gb = b[i];
            if (ga.Kind != gb.Kind || ga.Hidden != gb.Hidden || !string.Equals(ga.Name, gb.Name, StringComparison.Ordinal)
                || !string.Equals(ga.Now, gb.Now, StringComparison.Ordinal) || ga.Nodes.Count != gb.Nodes.Count)
                return false;
            for (int j = 0; j < ga.Nodes.Count; j++)
            {
                if (ga.Nodes[j] != gb.Nodes[j]) return false;
            }
        }
        return true;
    }

    // Returns a new groups list with updated selection on one group.
    public static List<ProxyGroupInfo> WithSelectedNode(IReadOnlyList<ProxyGroupInfo> groups, string groupName, string nodeName)
    {
        var updated = new List<ProxyGroupInfo>(groups.Count);
        foreach (var group in groups)
        {
            updated.Add(group.Name.Equals(groupName, StringComparison.Ordinal)
                ? group with { Now = nodeName }
                : group);
        }
        return updated;
    }

    // Returns a new groups list with updated delay values merged across all matching node names.
    public static List<ProxyGroupInfo> WithDelays(IReadOnlyList<ProxyGroupInfo> groups, IReadOnlyDictionary<string, int?> delays)
    {
        if (delays.Count == 0) return [.. groups];
        var updated = new List<ProxyGroupInfo>(groups.Count);
        foreach (var group in groups)
        {
            bool changed = false;
            var nodes = new List<ProxyNodeInfo>(group.Nodes.Count);
            foreach (var node in group.Nodes)
            {
                if (delays.TryGetValue(node.Name, out var ms) && ms != node.DelayMs)
                {
                    nodes.Add(node with { DelayMs = ms });
                    changed = true;
                }
                else
                {
                    nodes.Add(node);
                }
            }
            updated.Add(changed ? group with { Nodes = nodes } : group);
        }
        return updated;
    }

    // Splits a leading Unicode regional-indicator flag pair (e.g. "🇭🇰 Hong Kong 01") into an ASCII 2-letter
    // region code ("HK") and the remaining display name ("Hong Kong 01") so Windows renders it at full size on baseline.
    public static (string? RegionCode, string DisplayName) SplitLeadingRegionFlag(string? nodeName)
    {
        if (string.IsNullOrEmpty(nodeName)) return (null, "");
        int i = 0;
        while (i < nodeName.Length && char.IsWhiteSpace(nodeName[i])) i++;
        if (i + 3 < nodeName.Length
            && char.IsHighSurrogate(nodeName[i]) && char.IsLowSurrogate(nodeName[i + 1])
            && char.IsHighSurrogate(nodeName[i + 2]) && char.IsLowSurrogate(nodeName[i + 3]))
        {
            int cp1 = char.ConvertToUtf32(nodeName[i], nodeName[i + 1]);
            int cp2 = char.ConvertToUtf32(nodeName[i + 2], nodeName[i + 3]);
            if (cp1 is >= 0x1F1E6 and <= 0x1F1FF && cp2 is >= 0x1F1E6 and <= 0x1F1FF)
            {
                char c1 = (char)('A' + (cp1 - 0x1F1E6));
                char c2 = (char)('A' + (cp2 - 0x1F1E6));
                var rest = nodeName[(i + 4)..].TrimStart();
                if (rest.Length > 0)
                    return (new string([c1, c2]), rest);
            }
        }
        return (null, nodeName);
    }

    public static string FormatNodeDisplayName(string? nodeName)
    {
        var (code, rest) = SplitLeadingRegionFlag(nodeName);
        return code is null ? rest : $"{code}  {rest}";
    }

    public static string FormatRate(long bytesPerSec) => FormatSize(bytesPerSec) + "/s";

    public static string FormatSize(long bytes)
    {
        double value = Math.Max(0, bytes);
        if (value < 1024) return $"{value:0} B";
        value /= 1024;
        if (value < 1024) return value < 10 ? $"{value:0.0} KB" : $"{value:0} KB";
        value /= 1024;
        if (value < 1024) return value < 100 ? $"{value:0.0} MB" : $"{value:0} MB";
        value /= 1024;
        if (value < 1024) return value < 100 ? $"{value:0.0} GB" : $"{value:0} GB";
        value /= 1024;
        return $"{value:0.00} TB";
    }

    public static string FormatDelay(int? delayMs) => delayMs is > 0 and var ms ? $"{ms} ms" : "—";

    public static string FormatExpiry(long? expireUnixSeconds, DateTimeOffset now)
    {
        if (expireUnixSeconds is not (> 0 and var ts)) return "";
        try
        {
            var expiry = DateTimeOffset.FromUnixTimeSeconds(ts);
            var remaining = expiry - now;
            if (remaining.TotalSeconds <= 0) return Loc.T("已过期", "Expired");
            int days = (int)Math.Ceiling(remaining.TotalDays);
            return Loc.T($"剩 {days} 天", $"{days}d left");
        }
        catch { return ""; }
    }

    private static int? ExtractLatestDelay(JsonElement obj)
    {
        if (!obj.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Array)
            return null;
        int? latest = null;
        foreach (var item in history.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("delay", out var delayProp)
                && delayProp.ValueKind == JsonValueKind.Number
                && delayProp.TryGetInt32(out var ms))
            {
                latest = ms > 0 ? ms : null;
            }
        }
        return latest;
    }

    private static bool TryGetInt64(JsonElement obj, string name, out long value)
    {
        if (obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out value))
            return true;
        value = 0;
        return false;
    }

    private static int LeadingSpaces(ReadOnlySpan<char> line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        return i;
    }

    private static ReadOnlySpan<char> StripComment(ReadOnlySpan<char> line)
    {
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                return line[..i];
        }
        return line;
    }

    private static ReadOnlySpan<char> Unquote(ReadOnlySpan<char> value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1].Trim();
        return value;
    }
}
