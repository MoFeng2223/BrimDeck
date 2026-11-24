using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// Devices-style single card for a transient warning: ring + large % beside name and warning detail. Not a page tab.
internal sealed class AlertPanel : FrameworkElement
{
    public const double PanelWidth = 400, PanelHeight = 168;

    private readonly PanelLook _look;
    private readonly string _name, _detail, _ringColor, _figureColor;
    private readonly double? _percent;
    private readonly DeviceKind? _kind;
    private readonly bool _charging;
    private readonly DeviceSingleStep _step = DevicesLayout.Singles[1];

    public AlertPanel(PanelLook look, string name, string detail, double? percent, string ringColor, string figureColor, DeviceKind? kind = null, bool charging = false)
    {
        _look = look; _name = name; _detail = detail; _percent = percent;
        _ringColor = ringColor; _figureColor = figureColor; _kind = kind; _charging = charging;
        SnapsToDevicePixels = true;
        Width = PanelWidth;
        Height = PanelHeight;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    // Device battery warning: ring fill and colours follow DeviceInks; detail is the low / getting-low wording.
    public static AlertPanel ForDevice(PanelLook look, DeviceBattery device, DeviceInks inks, string warning)
    {
        int? percent = device.Lowest;
        return new(look, device.Name, warning, percent, inks.Ring(device), inks.Figure(device), device.Kind, device.Charging);
    }

    // Quota rise: ring uses the app theme colour; figure is the used (or left) share.
    public static AlertPanel ForQuota(PanelLook look, string name, string detail, double usedPercent, string color, bool remaining)
    {
        double share = remaining ? 100 - Math.Clamp(usedPercent, 0, 100) : Math.Clamp(usedPercent, 0, 100);
        return new(look, name, detail, share, color, color);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var s = _step;
        double contentW = s.Ring + s.Gap + DeviceSingleStep.TextWidth;
        double left = Math.Round((ActualWidth - contentW) / 2);
        double top = Math.Round((ActualHeight - s.Ring) / 2);
        var center = new Point(left + s.Ring / 2, top + s.Ring / 2);
        double? fill = _percent is { } p ? Math.Clamp(p / 100.0, 0, 1) : null;
        DevicesView.Ring(drawing, center, s.Ring, s.Stroke, fill, _ringColor, look: _look);
        if (_kind is { } kind)
            DeviceGlyphs.Draw(drawing, kind, new Rect(center.X - s.Icon / 2, center.Y - s.Icon / 2, s.Icon, s.Icon),
                _percent is null ? _look.Tertiary : _look.Primary);
        if (_charging && _percent is not null)
            DevicesView.Badge(drawing, center, (s.Ring - s.Stroke) / 2, Math.Round(s.Ring * .22), _ringColor, look: _look);

        double x = left + s.Ring + s.Gap, width = Math.Max(10, left + contentW - x);
        double block = s.FigureLine + 2 + s.NameLine + s.DetailLine, textTop = Math.Round(center.Y - block / 2);
        DrawFigure(drawing, _figureColor, x, textTop, s.Figure, s.FigureLine, s.Unit, ppd);
        textTop += s.FigureLine + 2;
        var name = MonitorDraw.Trimmed(_name, s.Name, _look.Primary, width, ppd);
        var detail = MonitorDraw.Trimmed(_detail, s.Detail, _figureColor, width, ppd);
        drawing.DrawText(name, new Point(x, Math.Round(textTop + (s.NameLine - name.Height) / 2)));
        drawing.DrawText(detail, new Point(x, Math.Round(textTop + s.NameLine + (s.DetailLine - detail.Height) / 2)));
    }

    private void DrawFigure(DrawingContext drawing, string accent, double x, double top, double size, double line, double unitSize, double ppd)
    {
        var number = MonitorDraw.Text(_percent is { } p ? Devices.Clamp((int)Math.Round(p)).ToString(CultureInfo.InvariantCulture) : "—", size,
            _percent is null ? _look.Tertiary : accent, ppd, display: true);
        var unit = _percent is null ? null : MonitorDraw.Text("%", unitSize, _look.Secondary, ppd);
        double y = Math.Round(top + (line - number.Height) / 2);
        drawing.DrawText(number, new Point(x, y));
        if (unit is not null) MonitorDraw.Baseline(drawing, unit, x + number.WidthIncludingTrailingWhitespace + 1, y + number.Baseline);
    }
}
