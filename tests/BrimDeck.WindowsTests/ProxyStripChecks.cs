using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;

internal static class ProxyStripChecks
{
    public static void Run(Action<string, bool> check)
    {
        var snap = new ProxySnapshot(true, ProxyMode.Rule, SystemProxy: true, Tun: true, "Clash Verge");
        var both = new ProxyStrip(snap, ProxyIndicators.Both, 1);
        var sysOnly = new ProxyStrip(snap, ProxyIndicators.SystemProxy, 1);
        var tunOnly = new ProxyStrip(snap, ProxyIndicators.Tun, 1);
        check("proxy strip: unified single-ring width matches BrimDeck 16 DIP ring footprint",
            sysOnly.PreferredWidth == 16 && both.PreferredWidth == 18 && tunOnly.PreferredWidth == 18);
        check("proxy strip: Holds matches only its own indicator selection",
            both.Holds(ProxyIndicators.Both) && !both.Holds(ProxyIndicators.SystemProxy) && !both.Holds(ProxyIndicators.Tun));

        var updated = new ProxySnapshot(true, ProxyMode.Global, SystemProxy: false, Tun: true, "Clash Verge");
        both.SetSnapshot(updated);
        var spoken = UIElementAutomationPeer.CreatePeerForElement(both)?.GetName() ?? "";
        check("proxy strip: SetSnapshot updates tooltip and spoken automation name",
            Equals(both.ToolTip, ProxyText.Summary(updated, ProxyIndicators.Both)) && spoken == ProxyText.Summary(updated, ProxyIndicators.Both));

        both.Stack(4);
        check("proxy strip: unified ring fits within side-edge column minimum width",
            both.PreferredWidth <= CompactColumn.MinimumWidth && both.Height == 24);

        // Render every mode x indicator x switch state on both dark and light themes without throwing and with visible ink.
        bool renderedAll = true;
        foreach (var theme in new[] { PanelThemes.Dark, PanelThemes.Light })
        {
            PanelTheme.Use(new DeckSettings { PanelTheme = theme });
            foreach (var mode in Enum.GetValues<ProxyMode>())
            {
                foreach (var indicators in Enum.GetValues<ProxyIndicators>())
                {
                    foreach (var (sys, tun) in new[] { (false, false), (true, false), (false, true), (true, true) })
                    {
                        var s = new ProxySnapshot(true, mode, sys, tun, "Clash Verge");
                        var strip = new ProxyStrip(s, indicators, 1);
                        strip.Width = strip.PreferredWidth;
                        strip.Measure(new Size(strip.PreferredWidth, strip.Height));
                        strip.Arrange(new Rect(0, 0, strip.PreferredWidth, strip.Height));
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(strip.PreferredWidth), (int)Math.Ceiling(strip.Height), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(strip);
                        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                        bool anyInk = false;
                        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] > 0) { anyInk = true; break; }
                        renderedAll &= anyInk;
                    }
                }
            }
        }
        PanelTheme.Use(new DeckSettings());
        check("proxy strip: draws visible vector ink across all modes, switch states, indicator options and themes", renderedAll);

        CheckAutoGroupTabLayout(check);
        CheckNodeListAccessibilityAndPressIdentity(check);
    }

    private static void CheckAutoGroupTabLayout(Action<string, bool> check)
    {
        var nodes = new List<ProxyNodeInfo>
        {
            new("🇭🇰 Hong Kong 01", "Shadowsocks", 48, false),
            new("🇯🇵 Japan 01", "Trojan", 72, false),
        };
        var autoGroups = new List<ProxyGroupInfo>
        {
            new("Automatic Global Low Latency Group Alpha", ProxyGroupKind.UrlTest, "🇭🇰 Hong Kong 01", nodes, false),
            new("Automatic Streaming Media Fallback Beta", ProxyGroupKind.Fallback, "🇯🇵 Japan 01", nodes, false),
            new("Automatic Research Balanced Group Gamma", ProxyGroupKind.LoadBalance, "🇭🇰 Hong Kong 01", nodes, false),
            new("Automatic Gaming Acceleration Delta", ProxyGroupKind.UrlTest, "🇯🇵 Japan 01", nodes, false),
        };
        var snapshot = new ProxyControlSnapshot(
            new ProxySnapshot(true, ProxyMode.Rule, true, false, "Clash Verge"),
            ApiConnected: true,
            Groups: autoGroups,
            Traffic: new ProxyTrafficSample(0, 0, 0, 0),
            Connections: new ProxyConnectionsSummary(0, 0, 0, 0),
            Subscription: null,
            LatencyConfig: ProxyLatencyConfig.Default,
            Error: null);

        var prevLang = Loc.Language;
        bool allFit = true;
        try
        {
            using var watcher = new BrimDeck.Native.ProxyWatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            using var controller = new BrimDeck.Native.ProxyController(System.Windows.Threading.Dispatcher.CurrentDispatcher, watcher);
            controller.SetPreviewState(snapshot, []);
            PanelTheme.Use(new DeckSettings { PanelTheme = PanelThemes.Dark });
            var look = PanelTheme.Main;

            foreach (var lang in new[] { Loc.Chinese, Loc.English })
            {
                Loc.Use(lang);
                // Standard preset 640x230 -> expanded content area 608x176.
                const double contentW = 608, contentH = 176;
                var view = new ProxyView(controller, autoGroups[0].Name, _ => { }) { Width = contentW, Height = contentH };
                PanelLook.Set(view, look);
                view.Measure(new Size(contentW, contentH));
                view.Arrange(new Rect(0, 0, contentW, contentH));
                view.UpdateState();
                view.Measure(new Size(contentW, contentH));
                view.Arrange(new Rect(0, 0, contentW, contentH));
                view.UpdateLayout();

                var more = view.MoreGroupsButton;
                var test = view.TestButton;
                bool visible = more is not null && more.Visibility == Visibility.Visible && more.ActualWidth > 0
                    && test is not null && test.Visibility == Visibility.Visible && test.ActualWidth > 0
                    && view.VisibleGroupTabButtons.Count is >= 1 and < 4;
                if (!visible)
                {
                    Console.WriteLine($"  [auto-tab diag {lang}] tabs={view.VisibleGroupTabButtons.Count} more={more?.Visibility}/{more?.ActualWidth} test={test?.Visibility}/{test?.ActualWidth}");
                    allFit = false;
                    continue;
                }

                double moreRight = more!.TransformToAncestor(view).Transform(new Point(more.ActualWidth, 0)).X;
                double testLeft = test!.TransformToAncestor(view).Transform(new Point(0, 0)).X;
                double testRight = test.TransformToAncestor(view).Transform(new Point(test.ActualWidth, 0)).X;
                // Allow 1 DIP for WPF UseLayoutRounding pixel snapping on non-integer DPI scales (e.g. 125%/150%).
                bool fits = moreRight <= testLeft + 0.5 && testRight <= contentW + 1.0;
                if (!fits)
                    Console.WriteLine($"  [auto-tab diag {lang}] tabs={view.VisibleGroupTabButtons.Count} moreRight={moreRight:F1} testLeft={testLeft:F1} testRight={testRight:F1} contentW={contentW}");
                allFit &= fits;
            }
        }
        finally
        {
            Loc.Use(prevLang);
            PanelTheme.Use(new DeckSettings());
        }
        check("proxy view: Standard size with long-name automatic groups keeps +N overflow and Test buttons unclipped", allFit);
    }

    private static void CheckNodeListAccessibilityAndPressIdentity(Action<string, bool> check)
    {
        var selectedCalls = new List<(string Group, string Node)>();
        var testedNodes = new List<string>();
        var list = new ProxyView.ProxyNodeList(
            onSelect: (g, n) => selectedCalls.Add((g, n)),
            onTestNode: n => testedNodes.Add(n),
            isTestingNode: _ => false)
        {
            Width = 340,
            Height = 140
        };
        PanelTheme.Use(new DeckSettings { PanelTheme = PanelThemes.Dark });
        PanelLook.Set(list, PanelTheme.Main);

        // Host inside a hidden HwndSource so WPF AutomationPeer.ProviderFromPeer resolves real UIA element providers.
        using var host = new HwndSource(new HwndSourceParameters("BrimDeckProxyNodeListTest") { Width = 360, Height = 160, WindowStyle = 0 })
        {
            RootVisual = list
        };

        var nodesA = new List<ProxyNodeInfo>
        {
            new("HK-01", "Shadowsocks", 45, false),
            new("HK-02", "Trojan", 80, false),
            new("JP-01", "VLESS", 120, false),
        };
        var selectorGroup = new ProxyGroupInfo("Proxies", ProxyGroupKind.Selector, "HK-01", nodesA, true);
        list.SetGroup(selectorGroup);
        list.Measure(new Size(340, 140));
        list.Arrange(new Rect(0, 0, 340, 140));
        list.UpdateLayout();

        // Keyboard focus, navigation, Enter/Space selection, and T single-node delay test.
        bool kbdOk = list.Focusable
            && list.HandleKey(Key.Down, ModifierKeys.None) && list.FocusedNodeName == "HK-02"
            && list.HandleKey(Key.Enter, ModifierKeys.None)
            && selectedCalls.Count == 1 && selectedCalls[0] == ("Proxies", "HK-02")
            && list.HandleKey(Key.T, ModifierKeys.None)
            && testedNodes.Count == 1 && testedNodes[0] == "HK-02";

        // UI Automation: List container + ISelectionProvider + child ListItem peers with names, selection state, Select & Invoke.
        var peer = UIElementAutomationPeer.CreatePeerForElement(list);
        var selProvider = peer?.GetPattern(PatternInterface.Selection) as ISelectionProvider;
        var children = peer?.GetChildren();
        if (children is { Count: 3 }) children[0].SetFocus();
        var item0Sel = children is { Count: 3 } ? children[0].GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider : null;
        var item2Sel = children is { Count: 3 } ? children[2].GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider : null;
        bool uiaBase = peer is not null
            && peer.GetAutomationControlType() == AutomationControlType.List
            && selProvider is not null && selProvider.GetSelection().Length == 1
            && children is { Count: 3 }
            && children[0].GetName() == "HK-01" && children[2].GetName() == "JP-01"
            && item0Sel?.IsSelected == true
            && item2Sel?.IsSelected == false
            && item0Sel?.SelectionContainer is not null;
        bool uiaSelectOk = false;
        if (item2Sel is not null)
        {
            item2Sel.Select();
            uiaSelectOk = selectedCalls.Count == 2 && selectedCalls[1] == ("Proxies", "JP-01");
        }
        bool uiaOk = uiaBase && uiaSelectOk;
        if (!uiaOk)
            Console.WriteLine($"  [uia detail] type={peer?.GetAutomationControlType()} selLen={selProvider?.GetSelection().Length} n0='{children?[0].GetName()}' n2='{children?[2].GetName()}' s0={item0Sel?.IsSelected} s2={item2Sel?.IsSelected} container={item0Sel?.SelectionContainer is not null} selectOk={uiaSelectOk} calls={selectedCalls.Count}");

        // Automatic group: Space / Invoke triggers single-node latency test instead of selection.
        var autoGroup = new ProxyGroupInfo("Auto", ProxyGroupKind.UrlTest, "HK-01", nodesA, false);
        list.SetGroup(autoGroup);
        var autoChildren = peer?.GetChildren();
        bool autoOk = list.HandleKey(Key.Space, ModifierKeys.None) && testedNodes.LastOrDefault() == "HK-01";
        if (autoChildren is { Count: 3 } && autoChildren[1].GetPattern(PatternInterface.Invoke) is IInvokeProvider autoInvoke)
        {
            autoInvoke.Invoke();
            autoOk &= testedNodes.LastOrDefault() == "HK-02";
        }

        // Pressed-node identity: reordering nodes between mouse down and mouse up must NOT select the new node at that slot.
        selectedCalls.Clear();
        list.SetGroup(selectorGroup);
        var firstCardCenter = new Point(60, 20);
        list.HandlePointerDown(firstCardCenter);
        var reorderedGroup = selectorGroup with { Nodes = [nodesA[1], nodesA[0], nodesA[2]] };
        list.SetGroup(reorderedGroup);
        list.HandlePointerUp(firstCardCenter);
        bool reorderSafe = selectedCalls.Count == 0;

        // Without reordering, clicking the second slot selects HK-01 (currently at index 1) cleanly.
        list.HandlePointerDown(firstCardCenter);
        list.HandlePointerUp(firstCardCenter);
        bool normalClickOk = selectedCalls.Count == 1 && selectedCalls[0] == ("Proxies", "HK-02");

        PanelTheme.Use(new DeckSettings());
        if (!(kbdOk && uiaOk && autoOk && reorderSafe && normalClickOk))
            Console.WriteLine($"  [node-list diag] kbdOk={kbdOk} uiaOk={uiaOk} autoOk={autoOk} reorderSafe={reorderSafe} normalClickOk={normalClickOk} selLen={selProvider?.GetSelection().Length} children={children?.Count}");
        check("proxy node list: supports keyboard navigation, UI Automation selection/invoke, and press-node identity across reorders",
            kbdOk && uiaOk && autoOk && reorderSafe && normalClickOk);
    }

    // Renders a zoomed sheet of all unified icon states on BrimDeck's dark and light island surfaces for visual inspection.
    public static void SavePreview(string path)
    {
        var samples = new (ProxyMode Mode, bool Sys, bool Tun, ProxyIndicators Choice)[]
        {
            (ProxyMode.Rule, true, false, ProxyIndicators.Both),
            (ProxyMode.Global, true, false, ProxyIndicators.Both),
            (ProxyMode.Direct, true, false, ProxyIndicators.Both),
            (ProxyMode.Rule, false, true, ProxyIndicators.Both),
            (ProxyMode.Global, false, true, ProxyIndicators.Both),
            (ProxyMode.Rule, true, true, ProxyIndicators.Both),
            (ProxyMode.Global, true, true, ProxyIndicators.Both),
            (ProxyMode.Rule, false, false, ProxyIndicators.Both),
            (ProxyMode.Rule, false, true, ProxyIndicators.Tun),
            (ProxyMode.Rule, false, false, ProxyIndicators.Tun),
        };
        const double zoom = 3;
        int cols = samples.Length;
        int w = (int)Math.Ceiling((cols * 28 + 20) * zoom);
        int h = (int)Math.Ceiling(88 * zoom);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(zoom, zoom));
            double y = 8;
            foreach (var theme in new[] { PanelThemes.Dark, PanelThemes.Light })
            {
                PanelTheme.Use(new DeckSettings { PanelTheme = theme });
                var look = PanelTheme.Main;
                dc.DrawRoundedRectangle(UI.Brush(look.Hex("surface")), new Pen(UI.Brush(look.Hex("rim")), 1), new Rect(6, y, cols * 28 + 8, 32), 16, 16);
                for (int i = 0; i < cols; i++)
                {
                    var (mode, sys, tun, choice) = samples[i];
                    var ringStyle = choice == ProxyIndicators.Tun ? (tun ? ProxyRingStyle.Mode : ProxyRingStyle.Off)
                        : choice == ProxyIndicators.SystemProxy ? (sys ? ProxyRingStyle.Mode : ProxyRingStyle.Off)
                        : sys ? ProxyRingStyle.Mode : tun ? ProxyRingStyle.Tun : ProxyRingStyle.Off;
                    bool showBadge = choice != ProxyIndicators.SystemProxy && tun;
                    var center = new Point(10 + i * 28 + 14, y + 16);
                    ProxyGlyphs.DrawRingIcon(dc, center, 16, 1.5, 9.5, mode, ringStyle, showBadge, 1, look);
                }
                y += 40;
            }
            dc.Pop();
        }
        PanelTheme.Use(new DeckSettings());
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(fullPath);
        encoder.Save(fs);
        SaveExpandedPreview(Path.Combine(dir ?? Environment.CurrentDirectory, "proxy-expanded-preview.png"));
    }

    // Renders Compact, Standard and Spacious expanded ProxyView layouts on BrimDeck's dark and light surfaces for visual inspection.
    private static void SaveExpandedPreview(string path)
    {
        var nodes = new List<ProxyNodeInfo>
        {
            new("🇭🇰 Hong Kong 01", "Hysteria2", 68, false),
            new("🇭🇰 Hong Kong 02", "Shadowsocks", 73, false),
            new("🇯🇵 Japan 18", "Trojan", 78, false),
            new("🇯🇵 Japan 19", "VLESS", 164, false),
            new("🇸🇬 Singapore 01", "Hysteria2", 92, false),
            new("🇸🇬 Singapore 02", "AnyTLS", null, false),
            new("🇺🇸 United States 01", "Trojan", 224, false),
            new("🇹🇼 Taiwan 01", "Shadowsocks", 84, false),
            new("🇬🇧 United Kingdom 01", "VMess", 460, false),
            new("🇩🇪 Germany 01", "Shadowsocks", 310, false),
        };
        var groups = new List<ProxyGroupInfo>
        {
            new("Proxies", ProxyGroupKind.Selector, "🇯🇵 Japan 18", nodes, true),
            new("Netflix", ProxyGroupKind.Selector, "🇭🇰 Hong Kong 01", nodes, true),
            new("HBO", ProxyGroupKind.Selector, "🇺🇸 United States 01", nodes, true),
            new("DisneyPlus", ProxyGroupKind.Selector, "🇸🇬 Singapore 01", nodes, true),
            new("YouTube", ProxyGroupKind.Selector, "🇯🇵 Japan 18", nodes, true),
            new("Bahamut", ProxyGroupKind.Selector, "🇹🇼 Taiwan 01", nodes, true),
            new("AI", ProxyGroupKind.Selector, "🇺🇸 United States 01", nodes, true),
        };
        var traffic = new List<ProxyTrafficSample>();
        long[] downs = [1200, 4800, 18000, 9200, 26000, 14500, 8100, 31000, 22000, 15000, 9000, 12400, 6200, 19800, 14200, 11800];
        long[] ups = [400, 1100, 3400, 2200, 5200, 2900, 1800, 6400, 4100, 2800, 1900, 2400, 1500, 3800, 2600, 2100];
        for (int i = 0; i < downs.Length; i++) traffic.Add(new ProxyTrafficSample(ups[i], downs[i], 580_000_000, 21_400_000_000));

        var snapshot = new ProxyControlSnapshot(
            new ProxySnapshot(true, ProxyMode.Rule, true, true, "Clash Verge"),
            ApiConnected: true,
            Groups: groups,
            Traffic: traffic[^1],
            Connections: new ProxyConnectionsSummary(52, 580_000_000, 21_400_000_000, 48_000_000),
            Subscription: new ProxySubscriptionInfo("Main Profile", 10_000_000_000, 32_800_000_000, 214_748_364_800, DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeSeconds()),
            LatencyConfig: ProxyLatencyConfig.Default,
            Error: null);

        var presets = new (string Label, double W, double H)[]
        {
            ("Compact 500x170", 500, 170),
            ("Standard 640x230", 640, 230),
            ("Spacious 880x320", 880, 320),
        };

        const double pad = 16;
        double colW = presets.Max(p => p.W);
        double totalW = colW * 2 + pad * 3;
        double totalH = presets.Sum(p => p.H) + pad * (presets.Length + 1);
        var visual = new DrawingVisual();
        using var watcher = new BrimDeck.Native.ProxyWatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        using var controller = new BrimDeck.Native.ProxyController(System.Windows.Threading.Dispatcher.CurrentDispatcher, watcher);
        controller.SetPreviewState(snapshot, traffic);

        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x24)), null, new Rect(0, 0, totalW, totalH));
            string[] themes = [PanelThemes.Dark, PanelThemes.Light];
            for (int t = 0; t < themes.Length; t++)
            {
                PanelTheme.Use(new DeckSettings { PanelTheme = themes[t] });
                var look = PanelTheme.Main;
                double x = pad + t * (colW + pad);
                double y = pad;
                foreach (var (_, pw, ph) in presets)
                {
                    // Simulate MainWindow's expanded panel chrome (16px side inset, ~42px top header, 12px bottom).
                    double contentW = pw - 32;
                    double contentH = ph - 54;
                    dc.DrawRoundedRectangle(UI.Brush(look.Hex("surface")), new Pen(UI.Brush(look.Hex("rim")), 1), new Rect(x, y, pw, ph), 20, 20);
                    var view = new ProxyView(controller, "Proxies", _ => { }) { Width = contentW, Height = contentH };
                    PanelLook.Set(view, look);
                    view.Measure(new Size(contentW, contentH));
                    view.Arrange(new Rect(0, 0, contentW, contentH));
                    view.UpdateState();
                    view.Measure(new Size(contentW, contentH));
                    view.Arrange(new Rect(0, 0, contentW, contentH));
                    view.UpdateLayout();
                    var bmpCard = new RenderTargetBitmap((int)Math.Ceiling(contentW), (int)Math.Ceiling(contentH), 96, 96, PixelFormats.Pbgra32);
                    bmpCard.Render(view);
                    dc.DrawImage(bmpCard, new Rect(x + 16, y + 42, contentW, contentH));
                    y += ph + pad;
                }
            }
        }
        PanelTheme.Use(new DeckSettings());
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(totalW), (int)Math.Ceiling(totalH), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}

namespace BrimDeck
{
    // Minimal test stub for MonitorDraw.Motion so WindowsTests can render ProxyView without pulling in SystemSampler.
    internal static class Motion
    {
        public static bool Enabled { get; set; }
        public static void Start(UIElement element, Func<double, bool> step) => step(Eased.Now);
    }
}
