using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
