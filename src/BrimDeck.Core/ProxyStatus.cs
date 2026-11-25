using System.Text.Json;

namespace BrimDeck.Core;

// The routing mode reported by Clash / Mihomo cores.
public enum ProxyMode { Unknown = 0, Rule = 1, Global = 2, Direct = 3 }

// Which traffic-capture indicators the collapsed proxy icon shows beside the routing mode.
// Saved as numbers, so new members go at the end.
public enum ProxyIndicators { Both = 0, SystemProxy = 1, Tun = 2 }

// Current read-only state of a running local proxy client (primarily Clash Verge / Mihomo).
public sealed record ProxySnapshot(bool Running, ProxyMode Mode, bool SystemProxy, bool Tun, string Client = "")
{
    public static readonly ProxySnapshot Stopped = new(false, ProxyMode.Unknown, false, false, "");

    public bool Active(ProxyIndicators indicators) => indicators switch
    {
        ProxyIndicators.SystemProxy => SystemProxy,
        ProxyIndicators.Tun => Tun,
        _ => SystemProxy || Tun
    };
}

// Values extracted from Clash Verge's own app settings (verge.yaml).
public readonly record struct VergeConfigValues(bool? EnableSystemProxy, bool? EnableTunMode);

// Values extracted from a Clash / Mihomo core config (clash-verge.yaml or config.yaml).
public readonly record struct ClashConfigValues(ProxyMode Mode, bool? TunEnabled, string? TunDevice, string? ExternalController, string? Secret);

// Pure parsers and merge logic for Clash Verge / Mihomo local controller responses and YAML config files.
// Only routing mode, TUN state and loopback controller address are read; proxy nodes, servers and subscriptions are ignored.
public static class ProxyParsers
{
    public static ProxyMode ParseMode(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "rule" => ProxyMode.Rule,
        "global" => ProxyMode.Global,
        "direct" => ProxyMode.Direct,
        _ => ProxyMode.Unknown
    };

    // Parses the JSON body of Mihomo / Clash GET /configs.
    public static bool TryParseMihomoConfigs(string? json, out ProxyMode mode, out bool? tunEnabled, out string? tunDevice)
    {
        mode = ProxyMode.Unknown;
        tunEnabled = null;
        tunDevice = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var root = doc.RootElement;
            bool recognized = false;
            if (root.TryGetProperty("mode", out var modeProp) && modeProp.ValueKind == JsonValueKind.String)
            {
                mode = ParseMode(modeProp.GetString());
                recognized |= mode != ProxyMode.Unknown;
            }
            if (root.TryGetProperty("tun", out var tunProp) && tunProp.ValueKind == JsonValueKind.Object)
            {
                recognized = true;
                if (tunProp.TryGetProperty("enable", out var enableProp) && enableProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    tunEnabled = enableProp.GetBoolean();
                if (tunProp.TryGetProperty("device", out var deviceProp) && deviceProp.ValueKind == JsonValueKind.String)
                {
                    var dev = deviceProp.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(dev)) tunDevice = dev;
                }
            }
            // A valid Mihomo /configs payload always has at least port/mixed-port/mode/tun.
            if (!recognized && (root.TryGetProperty("mixed-port", out _) || root.TryGetProperty("port", out _) || root.TryGetProperty("allow-lan", out _)))
                recognized = true;
            return recognized;
        }
        catch (JsonException) { return false; }
    }

    // Extracts enable_system_proxy and enable_tun_mode from Clash Verge's verge.yaml without pulling in a YAML dependency.
    public static VergeConfigValues ParseVergeYaml(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return default;
        bool? sysProxy = null, tun = null;
        foreach (var raw in yaml.AsSpan().EnumerateLines())
        {
            if (raw.Length == 0 || char.IsWhiteSpace(raw[0])) continue;
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var val = Unquote(line[(colon + 1)..].Trim());
            if (key.Equals("enable_system_proxy", StringComparison.OrdinalIgnoreCase) && TryParseBool(val, out var sp))
                sysProxy = sp;
            else if (key.Equals("enable_tun_mode", StringComparison.OrdinalIgnoreCase) && TryParseBool(val, out var tm))
                tun = tm;
        }
        return new(sysProxy, tun);
    }

    // Extracts top-level mode, external-controller, secret and tun.{enable,device} from Clash / Mihomo YAML.
    public static ClashConfigValues ParseClashConfigYaml(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return default;
        var mode = ProxyMode.Unknown;
        bool? tunEnabled = null;
        string? tunDevice = null, controller = null, secret = null;
        bool inTun = false;
        int tunIndent = -1;
        foreach (var raw in yaml.AsSpan().EnumerateLines())
        {
            int indent = LeadingSpaces(raw);
            var trimmed = StripComment(raw).Trim();
            if (trimmed.Length == 0) continue;
            if (indent == 0)
            {
                inTun = false;
                tunIndent = -1;
                int colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;
                var key = trimmed[..colon].Trim();
                var val = Unquote(trimmed[(colon + 1)..].Trim());
                if (key.Equals("mode", StringComparison.OrdinalIgnoreCase))
                    mode = ParseMode(val.ToString());
                else if (key.Equals("external-controller", StringComparison.OrdinalIgnoreCase))
                    controller = val.Length > 0 ? val.ToString() : null;
                else if (key.Equals("secret", StringComparison.OrdinalIgnoreCase))
                    secret = val.Length > 0 ? val.ToString() : null;
                else if (key.Equals("tun", StringComparison.OrdinalIgnoreCase) && val.Length == 0)
                    inTun = true;
            }
            else if (inTun)
            {
                if (tunIndent < 0) tunIndent = indent;
                if (indent != tunIndent) continue;
                int colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;
                var key = trimmed[..colon].Trim();
                var val = Unquote(trimmed[(colon + 1)..].Trim());
                if (key.Equals("enable", StringComparison.OrdinalIgnoreCase) && TryParseBool(val, out var enabled))
                    tunEnabled = enabled;
                else if (key.Equals("device", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
                    tunDevice = val.ToString();
            }
        }
        return new(mode, tunEnabled, tunDevice, controller, secret);
    }

    // Validates and normalizes an external-controller value to a loopback HTTP base URI.
    // Non-loopback hosts are refused so BrimDeck never sends requests or secrets off this PC.
    public static Uri? NormalizeLoopbackController(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (text.StartsWith(':')) text = "127.0.0.1" + text;
        if (text.StartsWith("0.0.0.0:", StringComparison.Ordinal)) text = "127.0.0.1:" + text["0.0.0.0:".Length..];
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) return null;
        if (uri.Port is <= 0 or > 65535) return null;
        var host = uri.Host;
        bool loopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.Ordinal)
            || host.Equals("::1", StringComparison.Ordinal)
            || host.Equals("[::1]", StringComparison.Ordinal);
        if (!loopback) return null;
        return new UriBuilder("http", host.Trim('[', ']'), uri.Port).Uri;
    }

    // Matches a Windows network adapter that belongs to Clash / Mihomo TUN (avoiding WireGuard / Tailscale Wintun false positives).
    public static bool IsClashTunInterface(string? name, string? description, string? configuredDevice = null)
    {
        var n = (name ?? "").Trim();
        var d = (description ?? "").Trim();
        if (n.Length == 0 && d.Length == 0) return false;
        if (!string.IsNullOrWhiteSpace(configuredDevice) && n.Equals(configuredDevice.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;
        // Exact/prefix names used by Mihomo / Clash Verge / Clash clients.
        if (n.Equals("Meta", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Mihomo", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Clash", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Mihomo", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("verge", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("FlClash", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Nyanpasu", StringComparison.OrdinalIgnoreCase))
            return true;
        if (d.Contains("Clash", StringComparison.OrdinalIgnoreCase) || d.Contains("Mihomo", StringComparison.OrdinalIgnoreCase))
            return true;
        // Generic utun adapter is only counted when backed by Wintun/TUN.
        if (n.StartsWith("utun", StringComparison.OrdinalIgnoreCase) && d.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    // Merges live controller API, local config files and Windows OS state into one snapshot.
    // Live API (named pipe or loopback HTTP) is authoritative for mode and TUN; Windows registry is authoritative for system proxy.
    public static ProxySnapshot Resolve(bool running, string client, ProxyMode? apiMode, bool? apiTun,
        ClashConfigValues config, VergeConfigValues verge, bool? osSystemProxy, bool? osTunInterface)
    {
        if (!running) return ProxySnapshot.Stopped;
        var mode = apiMode is { } m && m != ProxyMode.Unknown ? m : config.Mode;
        bool tun = apiTun ?? osTunInterface ?? verge.EnableTunMode ?? config.TunEnabled ?? false;
        bool sysProxy = osSystemProxy ?? verge.EnableSystemProxy ?? false;
        return new(true, mode, sysProxy, tun, client);
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

    private static bool TryParseBool(ReadOnlySpan<char> value, out bool result)
    {
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) { result = true; return true; }
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) { result = false; return true; }
        result = false;
        return false;
    }
}

// Localized descriptions for screen readers and tooltips.
public static class ProxyText
{
    public static string ModeName(ProxyMode mode) => mode switch
    {
        ProxyMode.Rule => Loc.T("规则模式", "Rule"),
        ProxyMode.Global => Loc.T("全局模式", "Global"),
        ProxyMode.Direct => Loc.T("直连模式", "Direct"),
        _ => Loc.T("运行中", "Running")
    };

    public static string IndicatorChoiceName(ProxyIndicators choice) => choice switch
    {
        ProxyIndicators.SystemProxy => Loc.T("仅系统代理", "System proxy"),
        ProxyIndicators.Tun => Loc.T("仅 TUN", "TUN only"),
        _ => Loc.T("全部显示", "Both")
    };

    public static string Summary(ProxySnapshot snapshot, ProxyIndicators indicators)
    {
        if (!snapshot.Running) return Loc.T("代理未运行", "Proxy not running");
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(snapshot.Client)) parts.Add(snapshot.Client);
        parts.Add(ModeName(snapshot.Mode));
        if (indicators is ProxyIndicators.Both or ProxyIndicators.SystemProxy)
            parts.Add(snapshot.SystemProxy ? Loc.T("系统代理已开启", "System proxy on") : Loc.T("系统代理已关闭", "System proxy off"));
        if (indicators is ProxyIndicators.Both or ProxyIndicators.Tun)
            parts.Add(snapshot.Tun ? Loc.T("TUN 已开启", "TUN on") : Loc.T("TUN 已关闭", "TUN off"));
        return string.Join(" · ", parts);
    }
}
