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
        ControlParsers(check);
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
            external-controller-pipe: \\.\pipe\verge-mihomo-sidecar-release-abcdef1234
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
        check("Proxy YAML: extracts mode, controller, pipe, secret and tun block while ignoring other indented blocks",
            clash.Mode == ProxyMode.Global && clash.TunEnabled == true && clash.TunDevice == "Mihomo"
            && clash.ExternalController == "127.0.0.1:9097"
            && clash.ExternalControllerPipe == @"\\.\pipe\verge-mihomo-sidecar-release-abcdef1234"
            && clash.Secret == "my#secret");

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

        check("Proxy pipe: normalizes local pipes and rejects remote or traversal paths",
            ProxyParsers.NormalizePipeName(@"\\.\pipe\verge-mihomo-production-abc123") == "verge-mihomo-production-abc123"
            && ProxyParsers.NormalizePipeName("verge-mihomo") == "verge-mihomo"
            && ProxyParsers.NormalizePipeName("//./pipe/verge-mihomo") == "verge-mihomo"
            && ProxyParsers.NormalizePipeName(@"\\remote-pc\pipe\verge-mihomo") is null
            && ProxyParsers.NormalizePipeName(@"..\verge-mihomo") is null
            && ProxyParsers.NormalizePipeName(@"verge\mihomo") is null
            && ProxyParsers.NormalizePipeName("") is null);
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

    private static void ControlParsers(Action<string, bool> check)
    {
        const string proxiesJson = """
            {
              "proxies": {
                "GLOBAL": { "type": "Selector", "now": "Proxy", "all": ["Proxy", "Auto", "HiddenGroup", "DIRECT"] },
                "Auto": { "type": "URLTest", "now": "HK-01", "all": ["HK-01", "JP-01"] },
                "Proxy": { "type": "Selector", "now": "Auto", "all": ["Auto", "HK-01", "JP-01", "DIRECT"] },
                "HiddenGroup": { "type": "Selector", "now": "HK-01", "all": ["HK-01"], "hidden": true },
                "HK-01": { "type": "Shadowsocks", "history": [{ "delay": 88 }, { "delay": 42 }] },
                "JP-01": { "type": "VLESS", "history": [{ "delay": 0 }] },
                "DIRECT": { "type": "Direct" }
              }
            }
            """;

        var ruleGroups = ProxyControlParsers.ParseMihomoProxies(proxiesJson, ProxyMode.Rule);
        check("Proxy control: Rule mode orders Selector before URLTest, hides GLOBAL and hidden groups, and resolves latest node delays",
            ruleGroups.Count == 2
            && ruleGroups[0].Name == "Proxy" && ruleGroups[0].CanSelect && ruleGroups[0].Now == "Auto"
            && ruleGroups[1].Name == "Auto" && !ruleGroups[1].CanSelect
            && ruleGroups[0].Nodes.First(n => n.Name == "HK-01").DelayMs == 42
            && ruleGroups[0].Nodes.First(n => n.Name == "JP-01").DelayMs is null
            && ruleGroups[0].Nodes.First(n => n.Name == "Auto").IsGroup);

        var globalGroups = ProxyControlParsers.ParseMihomoProxies(proxiesJson, ProxyMode.Global);
        check("Proxy control: Global mode places GLOBAL first",
            globalGroups.Count == 3 && globalGroups[0].Name == "GLOBAL");

        var switched = ProxyControlParsers.WithSelectedNode(ruleGroups, "Proxy", "HK-01");
        var withDelays = ProxyControlParsers.WithDelays(switched, new Dictionary<string, int?> { ["JP-01"] = 95, ["HK-01"] = null });
        check("Proxy control: immutable selection and delay updates propagate cleanly",
            withDelays[0].Now == "HK-01"
            && withDelays[0].Nodes.First(n => n.Name == "JP-01").DelayMs == 95
            && withDelays[0].Nodes.First(n => n.Name == "HK-01").DelayMs is null
            && withDelays[1].Nodes.First(n => n.Name == "JP-01").DelayMs == 95
            && ProxyControlParsers.GroupsEqual(ruleGroups, [.. ruleGroups])
            && !ProxyControlParsers.GroupsEqual(ruleGroups, switched));

        var resolved = ProxyControlParsers.ResolveActiveNode(ruleGroups, ruleGroups[0]);
        check("Proxy control: resolves nested active node chain (Proxy -> Auto -> HK-01) with leaf delay",
            resolved.DirectChoice == "Auto" && resolved.LeafNode == "HK-01" && resolved.DelayMs == 42);

        var hk = ProxyControlParsers.SplitLeadingRegionFlag("\U0001F1ED\U0001F1F0 Hong Kong 01");
        var jp = ProxyControlParsers.SplitLeadingRegionFlag("\U0001F1EF\U0001F1F5Tokyo 02");
        var plain = ProxyControlParsers.SplitLeadingRegionFlag("Traffic Reset: 6 Days Left");
        check("Proxy control: splits leading regional indicator flags into crisp ASCII codes",
            hk == ("HK", "Hong Kong 01")
            && jp == ("JP", "Tokyo 02")
            && plain == (null, "Traffic Reset: 6 Days Left")
            && ProxyControlParsers.FormatNodeDisplayName("\U0001F1FA\U0001F1F8 US 01") == "US  US 01");

        check("Proxy control: parses /traffic, /memory, /connections and delay responses",
            ProxyControlParsers.TryParseTrafficSample("""{"up":1234,"down":56789,"upTotal":1000,"downTotal":2000}""", out var tr)
            && tr.UpBytesPerSec == 1234 && tr.DownBytesPerSec == 56789 && tr.DownTotalBytes == 2000
            && ProxyControlParsers.TryParseMemoryBytes("""{"inuse":41943040,"oslimit":0}""", out var mem) && mem == 41943040
            && ProxyControlParsers.TryParseConnectionsSummary("""{"downloadTotal":9000,"uploadTotal":3000,"connections":[{"id":"1"},{"id":"2"}]}""", out var conn, mem)
            && conn.ActiveCount == 2 && conn.DownloadTotal == 9000 && conn.MemoryInUseBytes == 41943040
            && ProxyControlParsers.ParseGroupDelayMap("""{"HK-01":35,"JP-01":0}""").Count == 1
            && ProxyControlParsers.ParseSingleDelay("""{"delay":61}""") == 61
            && ProxyControlParsers.ParseSingleDelay("""{"delay":0}""") is null);

        const string vergeControlYaml = """
            auto_close_connection: false
            default_latency_test: https://cp.cloudflare.com/generate_204
            default_latency_timeout: 3200
            """;
        var latencyCfg = ProxyControlParsers.ParseVergeControlYaml(vergeControlYaml);
        check("Proxy control: reads verge.yaml latency test URL, timeout and auto_close_connection",
            latencyCfg.TestUrl == "https://cp.cloudflare.com/generate_204" && latencyCfg.TimeoutMs == 3200 && !latencyCfg.AutoCloseConnection);

        const string profilesYaml = """
            current: uid-2
            items:
              - uid: uid-1
                type: remote
                name: OtherProfile
                url: https://secret.invalid/sub
                extra:
                  upload: 100
                  download: 200
                  total: 1000
                  expire: 1800000000
              - uid: uid-2
                type: remote
                name: MainProfile
                url: https://secret.invalid/sub2
                extra:
                  upload: 1073741824
                  download: 3221225472
                  total: 21474836480
                  expire: 1900000000
            """;
        var sub = ProxyControlParsers.ParseVergeProfilesYaml(profilesYaml);
        const string zeroIndentProfilesYaml = """
            current: uid-2
            items:
            - uid: uid-2
              type: remote
              name: ZeroIndentProfile
              extra:
                upload: 500
                download: 500
                total: 2000
            """;
        var subZero = ProxyControlParsers.ParseVergeProfilesYaml(zeroIndentProfilesYaml);
        check("Proxy control: reads active profile quota from both indented and zero-indent profiles.yaml",
            sub is not null && sub.ProfileName == "MainProfile" && sub.HasTraffic
            && sub.UsedBytes == 4294967296L && sub.TotalBytes == 21474836480L && Math.Abs(sub.UsedFraction - 0.2) < 0.001
            && subZero is not null && subZero.ProfileName == "ZeroIndentProfile" && subZero.UsedBytes == 1000 && subZero.TotalBytes == 2000);

        var pageSettings = new DeckSettings();
        check("Proxy page settings: off by default, valid Quick Size presets and enum values",
            !pageSettings.ProxyPage && (int)DeckPage.Proxy == 5 && (int)DefaultDeckPage.Proxy == 7
            && DeckPages.Opening(DefaultDeckPage.Proxy) == DeckPage.Proxy
            && DeckPages.Size(pageSettings, DeckPage.Proxy) == ProxySizes.Default
            && ProxySizes.For(PanelSize.Compact) == (500, 170)
            && ProxySizes.For(PanelSize.Standard) == (640, 230)
            && ProxySizes.For(PanelSize.Spacious) == (880, 320));
        DeckPages.SetSize(pageSettings, DeckPage.Proxy, 720, 260);
        check("Proxy page settings: SetSize updates Proxy page independently",
            DeckPages.Size(pageSettings, DeckPage.Proxy) == (720, 260) && pageSettings.Width == new DeckSettings().Width);
    }
}
