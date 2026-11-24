using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace BrimDeck;
internal static class WeatherMotion
{
    internal static void Attach(FrameworkElement element)
    {
        var scale=new ScaleTransform(1,1); element.RenderTransform=scale; element.RenderTransformOrigin=new Point(.5,.5);
        void Go(double value) { foreach(var property in new[]{ScaleTransform.ScaleXProperty,ScaleTransform.ScaleYProperty}) scale.BeginAnimation(property,new DoubleAnimation(value,System.TimeSpan.FromMilliseconds(160)) { EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut } }); }
        element.MouseEnter+=(_,_)=>Go(1.035); element.MouseLeave+=(_,_)=>Go(1); element.PreviewMouseLeftButtonDown+=(_,_)=>Go(.95); element.PreviewMouseLeftButtonUp+=(_,_)=>Go(1.035);
    }
}
internal enum WeatherMetaKind { Thermo, Droplet, Rain, Snow, Ice, Mixed, Wind, Sunrise, Gauge, Eye, Leaf, Cloud, Location, ChevronDown, ChevronLeft, Check, Close, Add }
// 元信息矢量图标：16 网格线稿，圆头描边，风格同 MonitorGlyph —— 温度计 / 水滴 / 风 / 日出，及定位、选择、添加、返回等通用交互符号。
internal sealed class WeatherMetaIcon : FrameworkElement
{
    private static readonly Dictionary<WeatherMetaKind, Geometry> Shapes = Create();
    private static Dictionary<WeatherMetaKind, Geometry> Create()
    {
        var map=new Dictionary<WeatherMetaKind, Geometry>();
        foreach(var (kind,data) in new[] {
            (WeatherMetaKind.Thermo,"M6,9.6 V4.4 A2,2 0 0 1 10,4.4 V9.6 A3.2,3.2 0 1 1 6,9.6 Z M8,7.2 V12"),
            (WeatherMetaKind.Droplet,"M8,2.5 C11,6.2 13,9.4 13,11.2 A5,5 0 1 1 3,11.2 C3,9.4 5,6.2 8,2.5 Z"),
            // 降水四型用纯符号，不套云：雨=水滴、雪=六角雪花、冰=菱形冰粒、混合=水滴+雪花。
            (WeatherMetaKind.Rain,"M8,2.5 C11,6.2 13,9.4 13,11.2 A5,5 0 1 1 3,11.2 C3,9.4 5,6.2 8,2.5 Z"),
            (WeatherMetaKind.Snow,"M8,2.5 V13.5 M3.2,5.25 L12.8,10.75 M3.2,10.75 L12.8,5.25 M6,4 L8,2.5 L10,4 M6,12 L8,13.5 L10,12 M2.2,6.98 L3.2,5.25 L4.2,3.52 M11.8,12.48 L12.8,10.75 L13.8,9.02 M4.2,12.48 L3.2,10.75 L2.2,9.02 M13.8,6.98 L12.8,5.25 L11.8,3.52"),
            (WeatherMetaKind.Ice,"M8,2.5 L13,8 L8,13.5 L3,8 Z M8,2.5 V13.5 M3,8 H13"),
            (WeatherMetaKind.Mixed,"M5,3 C7,6 8.5,8.5 8.5,10 A3.5,3.5 0 1 1 1.5,10 C1.5,8.5 3,6 5,3 Z M11.5,5.5 V12.5 M8.6,7.25 L14.4,10.75 M8.6,10.75 L14.4,7.25"),
            (WeatherMetaKind.Wind,"M2.5,5.2 H6 C7.1,5.2 7.7,4 8.8,4 C9.9,4 9.9,5.7 8.6,5.7 M2.5,9.5 H5.7 C6.7,9.5 7.5,8.3 8.5,8.3 C9.6,8.3 9.9,9.8 8.6,9.8"),
            (WeatherMetaKind.Sunrise,"M4.2,13 A3.8,3.8 0 0 1 11.8,13 M2.5,13 H13.5 M3.9,8.4 L2.9,6.3 M8,6.6 V4.4 M12.1,8.4 L13.1,6.3"),
            (WeatherMetaKind.Gauge,"M8,3 A5,5 0 1 1 7.99,3 Z M8,8 L10.4,5.6 M5.2,12.5 H10.8"),
            (WeatherMetaKind.Eye,"M2.5,8 C4.5,5.2 6.2,4 8,4 C9.8,4 11.5,5.2 13.5,8 C11.5,10.8 9.8,12 8,12 C6.2,12 4.5,10.8 2.5,8 Z M8,6.2 A1.8,1.8 0 1 0 8,9.8 A1.8,1.8 0 1 0 8,6.2 Z"),
            (WeatherMetaKind.Leaf,"M3,10.5 C3,5.8 6.2,3 12.5,3 C12.5,9.8 9.2,12.5 4.8,12.5 C4,12.5 3.5,11.5 3,10.5 Z M3.5,12.5 C6,10.2 8.6,8.2 11.5,6.2"),
            (WeatherMetaKind.Cloud,"M4.8,11.5 A2.9,2.9 0 0 1 5.2,5.8 A4.3,4.3 0 0 1 12.8,7 A2.7,2.7 0 0 1 12,12 H5.2 A2.7,2.7 0 0 1 4.8,11.5 Z"),
            (WeatherMetaKind.Location,"M8,2.5 C10.6,2.5 12.3,4.4 12.3,6.8 C12.3,10.2 8,13.4 8,13.4 C8,13.4 3.7,10.2 3.7,6.8 C3.7,4.4 5.4,2.5 8,2.5 Z M8,5.3 A1.7,1.7 0 1 0 8,8.7 A1.7,1.7 0 1 0 8,5.3 Z"),
            (WeatherMetaKind.ChevronDown,"M4,6.5 L8,10.5 L12,6.5"),
            (WeatherMetaKind.ChevronLeft,"M12,6.5 L8,10.5 L4,6.5"),
            (WeatherMetaKind.Check,"M3.5,8.5 L6.5,11.5 L12.5,4.5"),
            (WeatherMetaKind.Close,"M4.5,4.5 L11.5,11.5 M11.5,4.5 L4.5,11.5"),
            (WeatherMetaKind.Add,"M8,3 V13 M3,8 H13"),
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
        MouseEnter += (_, _) => { timer ??= new System.Windows.Threading.DispatcherTimer { Interval=System.TimeSpan.FromMilliseconds(28) }; timer.Tick += Tick; timer.Start(); };
        MouseLeave += (_, _) => { if(timer is null) return; timer.Stop(); phase=0; InvalidateVisual(); };
        Unloaded += (_, _) => timer?.Stop();
    }
    // 整个图标矩形都参与命中测试：雨滴/光线之间是透明的，不补这个重写点图标会有点不到的空隙。
    protected override HitTestResult? HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this,p.HitPoint);
    private void Tick(object? sender,System.EventArgs e) { phase=(phase+0.02)%1; InvalidateVisual(); }
    protected override void OnRender(DrawingContext d)
    {
        base.OnRender(d); d.PushTransform(new ScaleTransform(ActualWidth/80,ActualHeight/80));
        double t = timer is { IsEnabled:true } ? phase : 0;
        var sun=new SolidColorBrush(Color.FromRgb(255,196,72)); var moon=new SolidColorBrush(Color.FromRgb(246,236,196));
        bool hasCloud = code>0;   // 晴(0)只有天体；多云(1/2)画云，天体位于云右上角、下部被云遮住。
        // 天体先画，云后画，云自然盖住天体下部。晴空天体居中，多云天体偏右上。
        // 月牙亮部落在左下、开口朝右上，星星点缀在开口侧。
        if(code<=2)
        {
            if(day)
            {
                double cx=hasCloud?56:40, cy=hasCloud?29:34, r=hasCloud?12:17, spin=2*System.Math.PI*t*0.12;
                for(int i=0;i<8;i++) { double a=i*System.Math.PI/4+spin;
                    d.DrawLine(new Pen(sun,2.5),new Point(cx+(r+5)*System.Math.Cos(a),cy+(r+5)*System.Math.Sin(a)),new Point(cx+(r+10)*System.Math.Cos(a),cy+(r+10)*System.Math.Sin(a))); }
                d.DrawEllipse(sun,null,new Point(cx,cy),r,r);
            }
            else
            {
                double cx=hasCloud?53:34, cy=hasCloud?27:32, r=hasCloud?13:23;
                // 切圆圆心移到右上，留在左下的亮部自然形成斜月牙；两尖端一在上方、一在右下。
                var crescent=new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(new Point(cx,cy),r,r),
                    new EllipseGeometry(new Point(cx+r*0.52,cy-r*0.34),r*0.94,r*0.94));
                d.DrawGeometry(moon,null,crescent);
                // 四芒星比小圆点醒目：主星加两颗副星，晴空与多云分别按天体位置布点。
                void Star(double x,double y,double arm,double width) {
                    var sp=new Pen(moon,width){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
                    d.DrawLine(sp,new Point(x-arm,y),new Point(x+arm,y));
                    d.DrawLine(sp,new Point(x,y-arm),new Point(x,y+arm));
                }
                // 星星落在月牙开口（切圆）内部，靠两尖端之间的空白区，不散到月牙外侧。
                if(hasCloud) { Star(59,18,2.6,1.6); Star(64,24,1.6,1.2); Star(56,26,1.2,1); }
                else { Star(52,17,3.6,1.8); Star(58,27,2.4,1.4); Star(47,33,1.8,1.2); }
            }
        }
        // 云与降水整体轻微浮动；晴空太阳光线缓慢自转，月牙与星星保持静态。
        d.PushTransform(new TranslateTransform(0, animated ? 1.2*System.Math.Sin(2*System.Math.PI*t) : 0));
        if(hasCloud) { var cloud=new SolidColorBrush(Color.FromRgb(238,242,250));d.DrawRoundedRectangle(cloud,null,new Rect(13,36,57,22),11,11);d.DrawEllipse(cloud,null,new Point(31,34),15,15);d.DrawEllipse(cloud,null,new Point(48,37),13,13); }
        if(code>=51) for(int i=0;i<3;i++) { double p=(t*1.5+i*0.31)%1;
            d.DrawLine(new Pen(code>=71&&code<=77?Brushes.AliceBlue:Brushes.DodgerBlue,3),new Point(25+i*15,63+p*7),new Point(22+i*15,71+p*7)); }
        d.Pop();
        d.Pop();
    }
}
