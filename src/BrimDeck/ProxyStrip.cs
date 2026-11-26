using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// Unified pure-icon proxy status indicator in the collapsed notch or capsule (CompactPart.Proxy).
// Matches BrimDeck's native 16 DIP ring language (DeviceStrip / MonitorStrip):
//   - Inner glyph (9.5 DIP): routing mode (Rule split-arrows, Global globe, Direct arrow).
//   - Outer ring (16 DIP): lights up whenever traffic capture is active so TUN-only never looks lopsided:
//       * System Proxy on (or TUN-only setting active): mode color (sky-blue Rule, soft lavender Global, Primary Direct).
//       * Both setting with only TUN on: TUN emerald ring uniting smoothly with the lower-right emerald shield badge,
//         while the inner glyph keeps the routing mode color so mode stays readable at a glance.
//       * Capture off: theme ringTrack with dimmed inner glyph.
//   - Lower-right cutout badge (7.5 DIP): emerald shield disc when TUN is enabled (same geometry as DeviceStrip's charging badge).
internal sealed class ProxyStrip : FrameworkElement
{
    private PanelLook Look => PanelLook.Of(this);
    private ProxySnapshot _snapshot;
    private readonly ProxyIndicators _indicators;
    private readonly double _scale;

    public ProxyStrip(ProxySnapshot snapshot, ProxyIndicators indicators, double scale)
    {
        _snapshot = snapshot;
        _indicators = indicators;
        _scale = CompactMusicLayout.SafeScale(scale);
        Height = Row;
        ToolTip = ProxyText.Summary(snapshot, indicators);
    }

    private double Row => Math.Max(1, CompactMusicLayout.Whole(24 * _scale));
    // Matches DeviceStrip and MonitorStrip dials so every circular glyph in the collapsed row reads as one size.
    private double Ring => Math.Max(4, CompactMusicLayout.Whole(16 * _scale));
    private bool CanShowBadge => _indicators is ProxyIndicators.Both or ProxyIndicators.Tun;
    // Extra room on the right for the lower-right TUN badge overhang so CompactRegion never clips it.
    private double BadgeOverhang => CanShowBadge ? Math.Round(2 * _scale) : 0;

    public double PreferredWidth => Ring + BadgeOverhang;

    // Already fits comfortably inside a narrow side-edge column (CompactColumn.MinimumWidth = 36 DIP) without multi-row stacking.
    public void Stack(double gap) => Height = Row;

    // Only the indicator choice changes width or badge rules; mode and switch transitions repaint in place.
    public bool Holds(ProxyIndicators indicators) => _indicators == indicators;

    public void SetSnapshot(ProxySnapshot snapshot)
    {
        if (_snapshot == snapshot) return;
        _snapshot = snapshot;
        ToolTip = ProxyText.Summary(snapshot, _indicators);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        // Transparent hit box so ToolTip works when ClickToExpand is on while clicks still bubble to HoverZone.
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var look = Look;
        double ring = Ring, stroke = Math.Max(1, 1.5 * _scale), icon = Math.Round(9.5 * _scale);
        double left = Math.Max(0, Math.Round((ActualWidth - PreferredWidth) / 2));
        var center = new Point(left + ring / 2, Row / 2);

        bool showBadge = CanShowBadge && _snapshot.Tun;
        var ringStyle = _indicators switch
        {
            ProxyIndicators.SystemProxy => _snapshot.SystemProxy ? ProxyRingStyle.Mode : ProxyRingStyle.Off,
            ProxyIndicators.Tun => _snapshot.Tun ? ProxyRingStyle.Mode : ProxyRingStyle.Off,
            _ => _snapshot.SystemProxy ? ProxyRingStyle.Mode : _snapshot.Tun ? ProxyRingStyle.Tun : ProxyRingStyle.Off
        };

        ProxyGlyphs.DrawRingIcon(drawing, center, ring, stroke, icon, _snapshot.Mode, ringStyle, showBadge, _scale, look);
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        => new DrawnPeer(this, System.Windows.Automation.Peers.AutomationControlType.Text, () => ProxyText.Summary(_snapshot, _indicators));
}

internal enum ProxyRingStyle { Off, Mode, Tun }

// Vector drawing for the unified proxy ring glyph, using BrimDeck's cool pastel-vivid palette and contrast guarantees.
internal static class ProxyGlyphs
{
    private const double Grid = 24;
    // Cool, harmonious BrimDeck palette: sky-cyan Rule, soft lavender Global (ClockColors.DefaultCustom), emerald TUN (DevicesView.ChargingOn).
    private const string RuleHex = "#7CC8F2";
    private const string GlobalHex = "#B9A8FF";
    private const string TunHex = "#6BD68A";

    // Rule: crisp upward Y-branching split arrows with wide top separation so the two tips never merge at 9.5 DIP.
    private static readonly Geometry RuleShape = Frozen("M 12,20.5 V 12.2 M 12,12.2 L 5.2,5.4 M 5.2,9.8 V 5.4 H 9.6 M 12,12.2 L 18.8,5.4 M 14.4,5.4 H 18.8 V 9.8");
    // Global: horizontal equator line paired with circle + meridian ellipse primitives.
    private static readonly Geometry GlobalEquator = Frozen("M 3.5,12 H 20.5");
    // Direct: crisp horizontal pass-through arrow.
    private static readonly Geometry DirectShape = Frozen("M 4,12 H 20 M 13.5,5.5 L 20,12 L 13.5,18.5");
    // Filled miniature shield centered inside the lower-right TUN badge (distinguishable from a device's charging bolt).
    private static readonly Geometry TunShieldFill = Frozen("M 12,2.5 L 19.8,5.6 V 11.4 C 19.8,16.1 16.4,19.9 12,21.8 C 7.6,19.9 4.2,16.1 4.2,11.4 V 5.6 Z");

    private static Geometry Frozen(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }

    public static string ModeColor(ProxyMode mode, PanelLook look)
    {
        if (SystemParameters.HighContrast) return SystemColors.HighlightColor.ToString();
        return mode switch
        {
            ProxyMode.Rule => look.Readable(RuleHex, PanelThemes.GraphicContrast),
            ProxyMode.Global => look.Readable(GlobalHex, PanelThemes.GraphicContrast),
            ProxyMode.Direct => look.Primary,
            _ => look.Tertiary
        };
    }

    public static string TunColor(PanelLook look)
    {
        if (SystemParameters.HighContrast) return SystemColors.HighlightColor.ToString();
        return look.Readable(TunHex, PanelThemes.GraphicContrast);
    }

    public static void DrawRingIcon(DrawingContext drawing, Point center, double ringSize, double stroke, double iconSize,
        ProxyMode mode, ProxyRingStyle ringStyle, bool showTunBadge, double scale, PanelLook look)
    {
        double radius = (ringSize - stroke) / 2;
        string modeColor = ModeColor(mode, look);
        string tunColor = TunColor(look);

        // 1. Outer ring: subtle track when off; mode color when System Proxy is active (or in TUN-only setting);
        //    unified emerald when only TUN is active under Both, matching BrimDeck's charging ring aesthetic.
        drawing.DrawEllipse(null, Stroke(look.Hex("ringTrack"), stroke), center, radius, radius);
        if (ringStyle != ProxyRingStyle.Off)
            drawing.DrawEllipse(null, Stroke(ringStyle == ProxyRingStyle.Tun ? tunColor : modeColor, stroke), center, radius, radius);

        // 2. Inner routing mode glyph: Primary when ring carries the mode color (matching DeviceStrip);
        //    mode-tinted inside the emerald TUN-only ring so Rule/Global/Direct remain distinguishable by both shape and hue;
        //    dimmed Primary when standby.
        bool standby = ringStyle == ProxyRingStyle.Off && !showTunBadge;
        string glyphColor = ringStyle == ProxyRingStyle.Tun ? modeColor : look.Primary;
        if (standby && !SystemParameters.HighContrast) drawing.PushOpacity(.45);
        DrawModeGlyph(drawing, mode, new Rect(center.X - iconSize / 2, center.Y - iconSize / 2, iconSize, iconSize), glyphColor, scale);
        if (standby && !SystemParameters.HighContrast) drawing.Pop();

        // 3. Lower-right TUN cutout badge (exact proportion and cutout style as DevicesView.Badge).
        if (showTunBadge)
            DrawTunBadge(drawing, center, radius, Math.Round(7.5 * scale, MidpointRounding.AwayFromZero), tunColor, look);
    }

    public static void DrawModeGlyph(DrawingContext drawing, ProxyMode mode, Rect at, string color, double scale)
    {
        double factor = at.Width / Grid;
        double weight = Math.Max(0.95, 1.08 * scale) / factor;
        var pen = Stroke(color, weight);
        drawing.PushTransform(Place(at));
        switch (mode)
        {
            case ProxyMode.Rule:
                drawing.DrawGeometry(null, pen, RuleShape);
                break;
            case ProxyMode.Global:
                var innerPen = Stroke(color, Math.Max(0.8, 0.92 * scale) / factor);
                drawing.DrawEllipse(null, pen, new Point(12, 12), 8.4, 8.4);
                drawing.DrawEllipse(null, innerPen, new Point(12, 12), 4.0, 8.4);
                drawing.DrawGeometry(null, innerPen, GlobalEquator);
                break;
            case ProxyMode.Direct:
                drawing.DrawGeometry(null, Stroke(color, Math.Max(1.0, 1.2 * scale) / factor), DirectShape);
                break;
            default:
                drawing.DrawEllipse(UI.Brush(color), null, new Point(12, 12), 3.2, 3.2);
                break;
        }
        drawing.Pop();
    }

    private static void DrawTunBadge(DrawingContext drawing, Point center, double radius, double size, string color, PanelLook look)
    {
        var at = new Point(center.X + radius * .7071, center.Y + radius * .7071);
        double cut = Math.Max(1.4, size * .18);
        string surface = look.Hex("surface");
        drawing.DrawEllipse(UI.Brush(surface), null, at, size / 2 + cut, size / 2 + cut);
        drawing.DrawEllipse(UI.Brush(color), null, at, size / 2, size / 2);
        double shield = size * .66;
        drawing.PushTransform(Place(new Rect(at.X - shield / 2, at.Y - shield / 2, shield, shield)));
        drawing.DrawGeometry(UI.Brush(surface), null, TunShieldFill);
        drawing.Pop();
    }

    private static Transform Place(Rect at)
    {
        double s = at.Width / Grid;
        var t = new MatrixTransform(new Matrix(s, 0, 0, s, at.X, at.Y));
        t.Freeze();
        return t;
    }

    private static readonly Dictionary<(string Color, double Thickness), Pen> Pens = [];
    private static Pen Stroke(string color, double thickness)
    {
        if (Pens.TryGetValue((color, thickness), out var pen)) return pen;
        pen = new Pen(UI.Brush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        if (Pens.Count > 64) Pens.Clear();
        return Pens[(color, thickness)] = pen;
    }
}
