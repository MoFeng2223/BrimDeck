using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Linq;
using BrimDeck.Core;
namespace BrimDeck;
// 天气领域的可视资源统一宿主（惯例同 MusicVisuals / PowerGlyphs）：动画、矢量图标、天气图形、
// 月相，以及跨视图复用的天空渐变 / 天气文字 / 风向箭头。页面类不承担资源职责。
internal static class WeatherMotion
{
    internal static void Attach(FrameworkElement element)
    {
        var scale=new ScaleTransform(1,1); element.RenderTransform=scale; element.RenderTransformOrigin=new Point(.5,.5);
        void Go(double value)
        {
            // 系统设置「减少动态效果」时不做缩放动画，直接落到目标值。
            if (!SystemParameters.ClientAreaAnimation)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX=scale.ScaleY=value;
                return;
            }
            foreach(var property in new[]{ScaleTransform.ScaleXProperty,ScaleTransform.ScaleYProperty}) scale.BeginAnimation(property,new DoubleAnimation(value,System.TimeSpan.FromMilliseconds(160)) { EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut } });
        }
        element.MouseEnter+=(_,_)=>Go(1.035); element.MouseLeave+=(_,_)=>Go(1); element.PreviewMouseLeftButtonDown+=(_,_)=>Go(.95); element.PreviewMouseLeftButtonUp+=(_,_)=>Go(1.035);
    }
}
internal enum WeatherMetaKind { Gust, DewPoint, Uv, AirQuality, WindSock, NoPrecip, Precipitation, Thermo, Droplet, Rain, Snow, Ice, Mixed, Wind, Sunrise, Gauge, Eye, Cloud, Location }
// 元信息矢量图标：16 网格线稿，圆头描边，风格同 MonitorGlyph —— 温度计 / 水滴 / 风 / 日升 / 定位等读数标注符号。
internal sealed class WeatherMetaIcon : FrameworkElement
{
    private static readonly Dictionary<WeatherMetaKind, Geometry> Shapes = Create();
    private static Dictionary<WeatherMetaKind, Geometry> Create()
    {
        var map=new Dictionary<WeatherMetaKind, Geometry>();
        foreach(var (kind,data) in new[] {
            (WeatherMetaKind.WindSock,"M3,14 V2 L13.5,4.2 V7.5 L3,8.5 M7,3 V8"),
            (WeatherMetaKind.Gust,"M1.5,5 H9 C13,5 13,1.4 10.5,1.8 M3.5,8 H13.8 M1.5,11 H8.5 C12.5,11 12,15 9.5,14"),
            (WeatherMetaKind.DewPoint,"M5,2 C4,4.5 1.8,6.5 1.8,8.4 A3.2,3.2 0 0 0 8.2,8.4 C8.2,6.5 6,4 5,2 Z M11,9 V3 A1.4,1.4 0 0 1 13.8,3 V9 A2.5,2.5 0 1 1 11,9 Z"),
            (WeatherMetaKind.Uv,"M2,7 A5,5 0 0 0 9,2 M2,2 L4.5,4.5 M5.5,9 L9.5,13 M9,7 L13,11 M11,3.5 L14.5,7"),
            (WeatherMetaKind.AirQuality,"M2,13 H14 M4,10 V8 M8,10 V5.5 M12,10 V2.5"),
            (WeatherMetaKind.NoPrecip,"M8,2 C7,4 3.5,7.5 3.5,10 A4.5,4.5 0 0 0 12.5,10 C12.5,7.5 9,4 8,2 Z M2,14 L14,2"),
            (WeatherMetaKind.Precipitation,"M3,3 V13 H13 V3 M3,8 H13 M10,5 H13 M10,10.5 H13"),
            (WeatherMetaKind.Thermo,"M6,9.6 V4.4 A2,2 0 0 1 10,4.4 V9.6 A3.2,3.2 0 1 1 6,9.6 Z M8,7.2 V12"),
            (WeatherMetaKind.Droplet,"M8,2.5 C11,6.2 13,9.4 13,11.2 A5,5 0 1 1 3,11.2 C3,9.4 5,6.2 8,2.5 Z"),
            (WeatherMetaKind.Rain,"M3,8 A2.4,2.4 0 0 1 3.8,3.4 A3.4,3.4 0 0 1 10.3,3.6 A2.3,2.3 0 1 1 12,8 Z M5,10 L3.5,13 M9,10.5 L7.5,14 M13,10 L11.5,13"),
            (WeatherMetaKind.Snow,"M8,2.5 V13.5 M3.2,5.25 L12.8,10.75 M3.2,10.75 L12.8,5.25 M6,4 L8,2.5 L10,4 M6,12 L8,13.5 L10,12 M2.2,6.98 L3.2,5.25 L4.2,3.52 M11.8,12.48 L12.8,10.75 L13.8,9.02 M4.2,12.48 L3.2,10.75 L2.2,9.02 M13.8,6.98 L12.8,5.25 L11.8,3.52"),
            (WeatherMetaKind.Ice,"M8,2.5 L13,8 L8,13.5 L3,8 Z M8,2.5 V13.5 M3,8 H13"),
            (WeatherMetaKind.Mixed,"M5,3 C7,6 8.5,8.5 8.5,10 A3.5,3.5 0 1 1 1.5,10 C1.5,8.5 3,6 5,3 Z M11.5,5.5 V12.5 M8.6,7.25 L14.4,10.75 M8.6,10.75 L14.4,7.25"),
            (WeatherMetaKind.Wind,"M2.5,5.2 H6 C7.1,5.2 7.7,4 8.8,4 C9.9,4 9.9,5.7 8.6,5.7 M2.5,9.5 H5.7 C6.7,9.5 7.5,8.3 8.5,8.3 C9.6,8.3 9.9,9.8 8.6,9.8"),
            (WeatherMetaKind.Sunrise,"M4.2,13 A3.8,3.8 0 0 1 11.8,13 M2.5,13 H13.5 M3.9,8.4 L2.9,6.3 M8,6.6 V4.4 M12.1,8.4 L13.1,6.3"),
            (WeatherMetaKind.Gauge,"M8,3 A5,5 0 1 1 7.99,3 Z M8,8 L10.4,5.6 M5.2,12.5 H10.8"),
            (WeatherMetaKind.Eye,"M2.5,8 C4.5,5.2 6.2,4 8,4 C9.8,4 11.5,5.2 13.5,8 C11.5,10.8 9.8,12 8,12 C6.2,12 4.5,10.8 2.5,8 Z M8,6.2 A1.8,1.8 0 1 0 8,9.8 A1.8,1.8 0 1 0 8,6.2 Z"),
            (WeatherMetaKind.Cloud,"M4.8,11.5 A2.9,2.9 0 0 1 5.2,5.8 A4.3,4.3 0 0 1 12.8,7 A2.7,2.7 0 0 1 12,12 H5.2 A2.7,2.7 0 0 1 4.8,11.5 Z"),
            (WeatherMetaKind.Location,"M8,2.5 C10.6,2.5 12.3,4.4 12.3,6.8 C12.3,10.2 8,13.4 8,13.4 C8,13.4 3.7,10.2 3.7,6.8 C3.7,4.4 5.4,2.5 8,2.5 Z M8,5.3 A1.7,1.7 0 1 0 8,8.7 A1.7,1.7 0 1 0 8,5.3 Z"),
        }) { var g=Geometry.Parse(data); g.Freeze(); map[kind]=g; }
        return map;
    }
    private readonly WeatherMetaKind kind; private readonly Pen pen;
    internal WeatherMetaIcon(WeatherMetaKind kind,Color ink) { this.kind=kind; pen=new Pen(new SolidColorBrush(ink),1.5){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round}; IsHitTestVisible=false; }
    protected override void OnRender(DrawingContext d)
    {
        base.OnRender(d); double s=Math.Min(ActualWidth,ActualHeight)/16; if(s<=0) return;
        d.PushTransform(new MatrixTransform(s,0,0,s,(ActualWidth-16*s)/2,(ActualHeight-16*s)/2));
        d.DrawGeometry(null,pen,Shapes[kind]);
        d.Pop();
    }
}
internal sealed class WeatherGlyph : FrameworkElement
{
    private readonly int code; private readonly bool day; private readonly bool animated;
    private System.Windows.Threading.DispatcherTimer? timer; private double phase;
    // 只有左上角大图标传 animated=true：悬浮时播放天气动效；小图标保持静态，避免指针掠过预报列时成片抖动。
    internal WeatherGlyph(int code,bool day,bool animated=false)
    {
        this.code=code;this.day=day;this.animated=animated;
        if(!animated) return;
        MouseEnter += (_, _) => { timer ??= new System.Windows.Threading.DispatcherTimer { Interval=System.TimeSpan.FromMilliseconds(28) }; timer.Tick -= Tick; timer.Tick += Tick; timer.Start(); };
        MouseLeave += (_, _) => { if(timer is null) return; timer.Stop(); phase=0; InvalidateVisual(); };
        Unloaded += (_, _) => timer?.Stop();
    }
    // 整个图标矩形都参与命中测试：雨滴/光线之间是透明的，不补这个重写点图标会有点不到的空隙。
    protected override HitTestResult? HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this,p.HitPoint);
    private void Tick(object? sender,System.EventArgs e) { phase=(phase+0.02)%1; InvalidateVisual(); }
    private static LinearGradientBrush Shading(uint top, uint bottom)
    {
        static Color Rgb(uint value) => Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        var brush = new LinearGradientBrush(Rgb(top), Rgb(bottom), 70);
        brush.Freeze();
        return brush;
    }

    private static readonly Brush sunlight = Shading(0xfff4ac, 0xffaa32);
    private static readonly Brush moonlight = Shading(0xf2f9ff, 0x98b8ec);
    private static readonly Brush fairCloud = Shading(0xf7fcff, 0xa3b9d4);
    private static readonly Brush stormCloud = Shading(0xd3e0ee, 0x7185a4);
    private static readonly Brush water = Shading(0x81e2ff, 0x3998df);
    private static readonly Geometry cloud = Geometry.Parse("M20,54 C10,54 6,48 7,41 C8,34 13,30 20,30 C23,18 34,14 44,19 C49,21 52,25 54,30 C63,28 71,34 71,42 C71,49 66,54 58,54 Z");
    private static readonly Geometry bolt = Geometry.Parse("M41,48 L54,48 45,59 52,59 32,77 38,63 31,63 Z");

    private static readonly Pen sunRay = Frozen(RoundedPen(sunlight, 3));
    private static readonly Pen fogLine = Frozen(RoundedPen(moonlight, 4));
    private static readonly Pen snowArm = Frozen(RoundedPen(moonlight, 2.6));
    private static readonly Pen rainLine = Frozen(RoundedPen(water, 4));
    private static readonly Pen cloudOutline = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(110, 108, 133, 165)), 1.6));
    private static readonly Brush dayGlow = Frozen(new RadialGradientBrush(Color.FromArgb(125, 255, 192, 78), Colors.Transparent));
    private static readonly Brush nightGlow = Frozen(new RadialGradientBrush(Color.FromArgb(80, 159, 203, 255), Colors.Transparent));
    private static readonly Geometry star = Frozen(Geometry.Parse("M0,-6 Q1,-1 5,0 Q1,1 0,6 Q-1,1 -5,0 Q-1,-1 0,-6 Z"));

    private static T Frozen<T>(T resource) where T : Freezable
    {
        resource.Freeze();
        return resource;
    }

    protected override void OnRender(DrawingContext d)
    {
        base.OnRender(d);
        double scale = Math.Min(ActualWidth, ActualHeight) / 80;
        if (scale <= 0) return;
        if (code < 0) return;
        d.PushTransform(new TranslateTransform((ActualWidth - 80 * scale) / 2, (ActualHeight - 80 * scale) / 2));
        d.PushTransform(new ScaleTransform(scale, scale));
        double wave = Math.Sin(2 * Math.PI * phase);
        bool cloudy = code > 0;
        if (code <= 2)
        {
            var center = cloudy ? new Point(55, 25) : new Point(38, 39);
            double radius = cloudy ? 13 : 21;
            var glow = day ? dayGlow : nightGlow;
            d.DrawEllipse(glow, null, center, radius * 1.75, radius * 1.75);
            if (day)
            {
                for (int i = 0; i < 8; i++)
                {
                    double angle = i * Math.PI / 4 + wave * .08;
                    var axis = new Vector(Math.Cos(angle), Math.Sin(angle));
                    d.DrawLine(sunRay, center + axis * (radius + 5), center + axis * (radius + 10));
                }
                d.DrawEllipse(sunlight, null, center, radius, radius);
            }
            else
            {
                var crescent = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(center, radius, radius),
                    new EllipseGeometry(center + new Vector(radius * .50, -radius * .38), radius * .92, radius * .92));
                d.DrawGeometry(moonlight, null, crescent);
                d.PushTransform(new TranslateTransform(center.X + radius * .85, center.Y - radius * .55));
                d.DrawGeometry(moonlight, null, star);
                d.Pop();
            }
        }
        if (cloudy)
        {
            d.PushTransform(new TranslateTransform(0, wave + (code <= 3 ? 7 : 0)));
            if (code == 1) d.PushTransform(new ScaleTransform(.80, .80, 15, 45));
            d.DrawGeometry(code >= 51 || code == 3 ? stormCloud : fairCloud,
                cloudOutline, cloud);
            if (code == 1) d.Pop();
            d.Pop();
        }
        if (code is 45 or 48)
        {
            d.DrawLine(fogLine, new Point(12, 59), new Point(58, 59));
            d.DrawLine(fogLine, new Point(25, 67), new Point(68, 67));
        }
        else if (code >= 95)
        {
            d.DrawGeometry(sunlight, null, bolt);
            if (code >= 96)
                foreach (double x in new[] { 22d, 59d }) d.DrawEllipse(moonlight, null, new Point(x, 65 + wave), 3.8, 3.8);
        }
        else if (code >= 51)
        {
            bool snow = code is >= 71 and <= 77 or 85 or 86;
            bool ice = code is 56 or 57 or 66 or 67;
            for (int i = 0; i < 3; i++)
            {
                var center = new Point(23 + 16 * i, 62 + (i % 2) * 5 + wave * 2);
                if (snow)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        var arm = new Vector(5 * Math.Cos(j * Math.PI / 3), 5 * Math.Sin(j * Math.PI / 3));
                        d.DrawLine(snowArm, center - arm, center + arm);
                    }
                }
                else if (ice && i == 1 || code < 60)
                    d.DrawEllipse(ice ? moonlight : water, null, center, 2.6, 3.2);
                else
                    d.DrawLine(rainLine, center + new Vector(2, -3), center + new Vector(-2, 5));
            }
        }
        d.Pop();
        d.Pop();
    }

    private static Pen RoundedPen(Brush brush, double width) =>
        new(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
}

// 月相图标：按 8 档月相文本
// 亮区由外圆半弧与终结线椭圆弧围成：蛾眉/残月终结线贴亮侧成窄月牙、上/下弦退化为半圆、
// 盈/亏凸月亮面越过中线；盈月亮在右、亏月亮在左，与北半球视角一致。新月只画淡虚线盘面，满月整圆实心。
internal sealed class MoonPhaseIcon : FrameworkElement
{
    private readonly int phase;                     // 0 新月 1 蛾眉 2 上弦 3 盈凸 4 满 5 亏凸 6 下弦 7 残月；-1 无数据/未知
    internal MoonPhaseIcon(string phaseText)
    {
        IsHitTestVisible = false;
        string key=new(phaseText.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        phase=key switch
        {
            "new" or "newmoon" or "新月" => 0,
            "waxingcrescent" or "蛾眉月" => 1,
            "firstquarter" or "waxingquarter" or "上弦月" => 2,
            "waxinggibbous" or "盈凸月" => 3,
            "full" or "fullmoon" or "满月" => 4,
            "waninggibbous" or "亏凸月" => 5,
            "lastquarter" or "thirdquarter" or "waningquarter" or "下弦月" => 6,
            "waningcrescent" or "残月" => 7,
            _ => -1
        };
    }
    protected override void OnRender(DrawingContext d)
    {
        base.OnRender(d);
        if (phase < 0) return;
        double radius = Math.Min(ActualWidth, ActualHeight) * .37;
        if (radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var disc = new EllipseGeometry(center, radius, radius);
        bool waxing = phase <= 4;
        var halo = new RadialGradientBrush();
        double strength = (1 - Math.Cos(phase * Math.PI / 4)) / 2;
        halo.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(18 + 92 * strength), 150, 205, 255), .70));
        halo.GradientStops.Add(new GradientStop(Color.FromArgb(0, 150, 205, 255), 1));
        d.DrawEllipse(halo, null, center, radius * 1.35, radius * 1.35);
        d.DrawGeometry(new RadialGradientBrush(Color.FromRgb(51, 64, 83), Color.FromRgb(30, 39, 55)), null, disc);
        if (phase == 0) return;

        Geometry lit = phase == 4 ? disc : MoonLit(center.X, center.Y, radius, waxing,
            phase is 1 or 7 ? .71 : phase is 2 or 6 ? 0 : -.71);
        d.PushClip(lit);
        var surface = new RadialGradientBrush(Color.FromRgb(251, 250, 237), Color.FromRgb(162, 186, 211))
        {
            GradientOrigin = new Point(waxing ? .68 : .32, .33)
        };
        d.DrawGeometry(surface, null, disc);
        var maria = new SolidColorBrush(Color.FromArgb(38, 65, 83, 108));
        void Mare(double x, double y, double rx, double ry) =>
            d.DrawEllipse(maria, null, new Point(center.X + x * radius, center.Y + y * radius), rx * radius, ry * radius);
        Mare(-.32, -.23, .27, .21);
        Mare(.24, -.16, .19, .16);
        Mare(.48, .04, .14, .11);
        Mare(-.27, .38, .12, .08);
        d.Pop();
    }
    // 亮区几何：外半圆弧（盈月亮在右、亏月在左）+ 终结线椭圆弧（k=0 即直线半圆、k>0 贴亮侧月牙、k<0 越过中线凸月）。
    private static Geometry MoonLit(double cx,double cy,double r,bool waxing,double k)
    {
        var geo=new StreamGeometry();
        using(var g=geo.Open())
        {
            g.BeginFigure(new Point(cx,cy-r),true,true);
            g.ArcTo(new Point(cx,cy+r),new Size(r,r),0,false,waxing?SweepDirection.Clockwise:SweepDirection.Counterclockwise,true,false);
            if (k == 0) g.LineTo(new Point(cx, cy-r), true, false);
            else g.ArcTo(new Point(cx,cy-r),new Size(Math.Abs(k)*r,r),0,false,waxing==(k<0)?SweepDirection.Clockwise:SweepDirection.Counterclockwise,true,false);
        }
        geo.Freeze(); return geo;
    }
}

// 天气领域跨视图复用的可视工具：天空渐变、天气条件文案、风向箭头。
// 设置页与灵动岛页共用，页面类不再承担这些资源职责（惯例同 MusicVisuals.Icon / PowerGlyphs）。
internal static class WeatherVisuals
{
    // WMO 码 → 最短一档天气文字：0 晴、1/2 多云、3 阴、45/48 雾、51~67 雨、71~77 雪、80~82 阵雨、
    // 85/86 阵雪、95+ 雷雨。码值已由 WeatherCodes 归一到同一套区间，这里只负责翻成文字。
    internal static string Condition(int code) => code switch
    {
        0 => Loc.T("晴", "Sunny"), 1 or 2 => Loc.T("多云", "Cloudy"), 3 => Loc.T("阴", "Overcast"),
        45 or 48 => Loc.T("雾", "Foggy"), >= 51 and <= 67 => Loc.T("雨", "Rain"), >= 71 and <= 77 => Loc.T("雪", "Snow"),
        >= 80 and <= 82 => Loc.T("阵雨", "Showers"), 85 or 86 => Loc.T("阵雪", "Snow showers"), >= 95 => Loc.T("雷雨", "Thunderstorm"), _ => Loc.T("多云", "Cloudy")
    };
    // 天空渐变随昼夜与天气切换：晴昼蓝、阴霾灰、雨夜深蓝灰、雷雨紫，夜晚统一深邃蓝黑。
    internal static LinearGradientBrush Sky(int code, bool day)
    {
        var (a, b) = day ? code switch {
            <= 2 => (Color.FromRgb(31, 77, 143), Color.FromRgb(85, 127, 175)),
            3 => (Color.FromRgb(74, 86, 104), Color.FromRgb(113, 128, 150)),
            45 or 48 => (Color.FromRgb(96, 102, 114), Color.FromRgb(140, 148, 160)),
            >= 95 => (Color.FromRgb(59, 46, 86), Color.FromRgb(110, 91, 138)),
            >= 71 and <= 77 or 85 or 86 => (Color.FromRgb(93, 122, 154), Color.FromRgb(176, 196, 222)),
            _ => (Color.FromRgb(44, 58, 77), Color.FromRgb(93, 109, 126))
        } : (Color.FromRgb(13, 18, 48), Color.FromRgb(35, 40, 74));
        var brush = new LinearGradientBrush(a, b, 150);
        brush.Freeze(); return brush;
    }
    // 实况风速卡的方向图标：同一套 8 方位角度，颜色跟随卡片，尺寸与其他元信息图标一致。
    internal static FrameworkElement WindArrowIcon(double deg, Color ink) => new Path
    {
        Data = Geometry.Parse("M8,1.5 L11.2,13 L8,10.2 L4.8,13 Z"),
        Fill = new SolidColorBrush(ink),
        Width = 15, Height = 15, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = new Point(.5, .5),
        RenderTransform = new RotateTransform(double.IsNaN(deg) ? 0 : WeatherCodes.Compass8Angle(deg))
    };
}