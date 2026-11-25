using System.Text.Json;
using BrimDeck.Core;

internal static class ProxyStatusTests
{
    public static void Run(Action<string, bool> check)
    {
        MihomoApi(check);
        YamlParsing(check);
        LoopbackValidation(check);
        TunMatching(check);
        Resolution(check);
        SettingsAndText(check);
    }

    private static void MihomoApi(Action<string, bool> check)
    {
        const string sample = """
            {
              "port": 7890,
              "mixed-port": 7897,
              "mode": "rule",
              "tun": { "enable": true, "stack": "mixed", "device": "ClashVergeREV" }
            }
            """;
        check("Proxy API: parses mode, tun.enable and tun.device from /configs",
            ProxyParsers.TryParseMihomoConfigs(sample, out var mode, out var tun, out var device)
            && mode == ProxyMode.Rule && tun == true && device == "ClashVergeREV");

        check("Proxy API: parses Global and Direct case-insensitively with TUN off",
            ProxyParsers.TryParseMihomoConfigs("""{"mode":"Global","tun":{"enable":false}}""", out var globalMode, out var globalTun, out _)
            && globalMode == ProxyMode.Global && globalTun == false
            && ProxyParsers.TryParseMihomoConfigs("""{"mode":"DIRECT"}""", out var directMode, out var directTun, out _)
            && directMode == ProxyMode.Direct && directTun is null);

        check("Proxy API: rejects malformed or unrelated JSON",
            !ProxyParsers.TryParseMihomoConfigs("not json", out _, out _, out _)
            && !ProxyParsers.TryParseMihomoConfigs("[]", out _, out _, out _)
            && !ProxyParsers.TryParseMihomoConfigs("""{"hello":"world"}""", out _, out _, out _));
    }

    private static void YamlParsing(Action<string, bool> check)
    {
        const string vergeYaml = """
            # Clash Verge Rev settings
            language: zh
            enable_system_proxy: true # comment
            enable_tun_mode: false
            nested:
              enable_tun_mode: true
            """;
        var verge = ProxyParsers.ParseVergeYaml(vergeYaml);
        check("Proxy YAML: extracts top-level enable_system_proxy and enable_tun_mode from verge.yaml",
            verge.EnableSystemProxy == true && verge.EnableTunMode == false);

        const string clashYaml = """
            mixed-port: 7897
            mode: "global" # user switched
            external-controller: '127.0.0.1:9097'
            secret: "my#secret"
            dns:
              enable: false
            tun:
              enable: true
              stack: system
              device: Mihomo
            proxies:
              - name: test
            """;
        var clash = ProxyParsers.ParseClashConfigYaml(clashYaml);
        check("Proxy YAML: extracts mode, controller, secret and tun block while ignoring other indented blocks",
            clash.Mode == ProxyMode.Global && clash.TunEnabled == true && clash.TunDevice == "Mihomo"
            && clash.ExternalController == "127.0.0.1:9097" && clash.Secret == "my#secret");

        const string directYaml = "mode: direct\ntun:\n  enable: false\n";
        var direct = ProxyParsers.ParseClashConfigYaml(directYaml);
        check("Proxy YAML: reads direct mode and disabled tun",
            direct.Mode == ProxyMode.Direct && direct.TunEnabled == false && direct.ExternalController is null);
    }

    private static void LoopbackValidation(Action<string, bool> check)
    {
        check("Proxy loopback: accepts local addresses and shorthands",
            ProxyParsers.NormalizeLoopbackController("127.0.0.1:9097") == new Uri("http://127.0.0.1:9097/")
            && ProxyParsers.NormalizeLoopbackController(":9090") == new Uri("http://127.0.0.1:9090/")
            && ProxyParsers.NormalizeLoopbackController("0.0.0.0:9097") == new Uri("http://127.0.0.1:9097/")
            && ProxyParsers.NormalizeLoopbackController("http://localhost:9090") == new Uri("http://localhost:9090/")
            && ProxyParsers.NormalizeLoopbackController("[::1]:9097") == new Uri("http://[::1]:9097/"));

        check("Proxy loopback: rejects non-loopback hosts, HTTPS and invalid ports",
            ProxyParsers.NormalizeLoopbackController("192.168.1.5:9090") is null
            && ProxyParsers.NormalizeLoopbackController("example.com:9090") is null
            && ProxyParsers.NormalizeLoopbackController("https://127.0.0.1:9090") is null
            && ProxyParsers.NormalizeLoopbackController("127.0.0.1:0") is null
            && ProxyParsers.NormalizeLoopbackController("") is null);
    }

    private static void TunMatching(Action<string, bool> check)
    {
        check("Proxy TUN NIC: matches Clash / Mihomo adapters and custom configured device",
            ProxyParsers.IsClashTunInterface("Mihomo", "Wintun Userspace Tunnel")
            && ProxyParsers.IsClashTunInterface("Meta", "Wintun Userspace Tunnel")
            && ProxyParsers.IsClashTunInterface("ClashVergeREV", "Wintun Userspace Tunnel")
            && ProxyParsers.IsClashTunInterface("utun0", "Wintun Userspace Tunnel")
            && ProxyParsers.IsClashTunInterface("MyCustomTun", "Wintun Userspace Tunnel", "MyCustomTun"));

        check("Proxy TUN NIC: avoids false positives on Tailscale, WireGuard and regular adapters",
            !ProxyParsers.IsClashTunInterface("Tailscale", "Wintun Userspace Tunnel")
            && !ProxyParsers.IsClashTunInterface("WireGuard", "WireGuard Tunnel")
            && !ProxyParsers.IsClashTunInterface("Ethernet", "Intel(R) Ethernet Connection")
            && !ProxyParsers.IsClashTunInterface("Wi-Fi", "Intel(R) Wi-Fi 6E"));
    }

    private static void Resolution(Action<string, bool> check)
    {
        var cfg = new ClashConfigValues(ProxyMode.Direct, false, "Mihomo", null, null);
        var verge = new VergeConfigValues(true, true);

        check("Proxy resolve: not running always yields Stopped",
            ProxyParsers.Resolve(false, "Clash Verge", ProxyMode.Global, true, cfg, verge, true, true) == ProxySnapshot.Stopped);

        var live = ProxyParsers.Resolve(true, "Clash Verge", ProxyMode.Rule, true, cfg, verge, osSystemProxy: false, osTunInterface: false);
        check("Proxy resolve: live API wins for mode and TUN while OS registry wins for system proxy",
            live == new ProxySnapshot(true, ProxyMode.Rule, false, true, "Clash Verge"));

        var fallback = ProxyParsers.Resolve(true, "Clash Verge", apiMode: null, apiTun: null, cfg, verge, osSystemProxy: true, osTunInterface: true);
        check("Proxy resolve: falls back cleanly to YAML mode and OS TUN interface when API is unreachable",
            fallback == new ProxySnapshot(true, ProxyMode.Direct, true, true, "Clash Verge"));

        var noNic = ProxyParsers.Resolve(true, "Clash Verge", apiMode: null, apiTun: null, cfg, verge, osSystemProxy: false, osTunInterface: false);
        check("Proxy resolve: inactive OS TUN adapter overrides stale verge.yaml enable_tun_mode",
            !noNic.Tun && !noNic.SystemProxy && noNic.Mode == ProxyMode.Direct);
    }

    private static void SettingsAndText(Action<string, bool> check)
    {
        var defaults = new DeckSettings();
        check("Proxy settings: off by default, Both indicators, included in compact order",
            !defaults.NotchProxy && !defaults.CapsuleProxy && defaults.ProxyIndicators == ProxyIndicators.Both
            && !defaults.ShowsProxy(CompactStyle.Notch) && !defaults.ShowsProxy(CompactStyle.Capsule) && !defaults.ShowsProxy(CompactStyle.Line)
            && defaults.NotchOrder.Contains(CompactPart.Proxy) && (int)CompactPart.Proxy == 7);

        var bad = new DeckSettings { ProxyIndicators = (ProxyIndicators)99 };
        bad.Normalize();
        check("Proxy settings: unknown ProxyIndicators resets to Both", bad.ProxyIndicators == ProxyIndicators.Both);

        var saved = new DeckSettings { NotchProxy = true, CapsuleProxy = false, ProxyIndicators = ProxyIndicators.Tun };
        var back = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(saved))!;
        back.Normalize();
        check("Proxy settings: survive JSON roundtrip and respect style switches",
            back.ShowsProxy(CompactStyle.Notch) && !back.ShowsProxy(CompactStyle.Capsule) && !back.ShowsProxy(CompactStyle.Line)
            && back.ProxyIndicators == ProxyIndicators.Tun);

        var snap = new ProxySnapshot(true, ProxyMode.Rule, SystemProxy: true, Tun: false, "Clash Verge");
        check("Proxy active: respects indicator filter",
            snap.Active(ProxyIndicators.Both) && snap.Active(ProxyIndicators.SystemProxy) && !snap.Active(ProxyIndicators.Tun));

        var prevLang = Loc.Language;
        try
        {
            Loc.Use(Loc.Chinese);
            check("Proxy text: Chinese summary follows chosen indicators",
                ProxyText.Summary(snap, ProxyIndicators.Both) == "Clash Verge · 规则模式 · 系统代理已开启 · TUN 已关闭"
                && ProxyText.Summary(snap, ProxyIndicators.SystemProxy) == "Clash Verge · 规则模式 · 系统代理已开启"
                && ProxyText.Summary(snap, ProxyIndicators.Tun) == "Clash Verge · 规则模式 · TUN 已关闭");
            Loc.Use(Loc.English);
            check("Proxy text: English summary follows chosen indicators",
                ProxyText.Summary(snap, ProxyIndicators.Both) == "Clash Verge · Rule · System proxy on · TUN off");
        }
        finally { Loc.Use(prevLang); }
    }
}
