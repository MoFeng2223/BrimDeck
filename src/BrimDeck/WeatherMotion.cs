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
internal enum WeatherMetaKind { Thermo, Droplet, Wind, Sunrise, Location, ChevronDown, ChevronLeft, Check, Close, Add }
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
            (WeatherMetaKind.Wind,"M2.5,5.2 H6 C7.1,5.2 7.7,4 8.8,4 C9.9,4 9.9,5.7 8.6,5.7 M2.5,9.5 H5.7 C6.7,9.5 7.5,8.3 8.5,8.3 C9.6,8.3 9.9,9.8 8.6,9.8"),
            (WeatherMetaKind.Sunrise,"M4.2,13 A3.8,3.8 0 0 1 11.8,13 M2.5,13 H13.5 M3.9,8.4 L2.9,6.3 M8,6.6 V4.4 M12.1,8.4 L13.1,6.3"),
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
    private readonly int code; private readonly bool day;
    // 图标不再挂 WeatherMotion：以前鼠标划过任何一个图标它都会放大 1.035，
    // 指针在卡片或预报列上挪动时一个个图标跟着抖，属于多余动效。图标只负责画，交互交给外层容器。
    internal WeatherGlyph(int code,bool day) { this.code=code;this.day=day; }
    protected override void OnRender(DrawingContext d)
    {
        base.OnRender(d); d.PushTransform(new ScaleTransform(ActualWidth/80,ActualHeight/80));
        var sun=new SolidColorBrush(Color.FromRgb(255,196,72));var moon=new SolidColorBrush(Color.FromRgb(246,236,196));
        if(code<=2) {
            if(day) { d.DrawEllipse(sun,null,new Point(36,32),17,17);for(int i=0;i<8;i++) {double a=i*System.Math.PI/4;d.DrawLine(new Pen(sun,2.5),new Point(36+22*System.Math.Cos(a),32+22*System.Math.Sin(a)),new Point(36+28*System.Math.Cos(a),32+28*System.Math.Sin(a)));} }
            else { var crescent=new CombinedGeometry(GeometryCombineMode.Exclude,new EllipseGeometry(new Point(35,33),24,24),new EllipseGeometry(new Point(46,26),23,23));d.DrawGeometry(moon,null,crescent);d.DrawEllipse(moon,null,new Point(65,18),2,2); }
        }
        if(code>0) { var cloud=new SolidColorBrush(Color.FromRgb(238,242,250));d.DrawRoundedRectangle(cloud,null,new Rect(13,36,57,22),11,11);d.DrawEllipse(cloud,null,new Point(31,34),15,15);d.DrawEllipse(cloud,null,new Point(48,37),13,13); }
        if(code>=51) for(int i=0;i<3;i++) d.DrawLine(new Pen(code>=71&&code<=77?Brushes.AliceBlue:Brushes.DodgerBlue,3),new Point(25+i*15,63),new Point(22+i*15,71));
        d.Pop();
    }
}
