using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Modules;
using Loc = BrimDeck.ModuleLocalization;

namespace BrimDeck.Thrm;

// The page, shaped as the cooler software's monitor panel: a row of three gauge cards — CPU temperature, GPU
// temperature, fan speed — each with its mark and title, an arc filled to the reading's share, the figure large
// with its unit small, and a second line beneath. The row keeps the accepted design's proportions on a fixed 592
// design width and scales as one piece when the panel gives less room. The figures follow the palette; only the
// arcs take the readings' level colors.
internal sealed class ThermPageView : Grid, IModuleView
{
    private readonly ThermWorkspace _workspace;
    private readonly GaugeCard _cpu, _gpu, _fan;
    private readonly TextBlock _state = new() { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };

    public FrameworkElement Element => this;

    internal ThermPageView(ThermWorkspace workspace, ModuleViewContext context)
    {
        _workspace = workspace;
        _cpu = new GaugeCard("CPU 温度", "°C", 0, 100, ThermVisuals.CpuGlyph);
        _gpu = new GaugeCard("GPU 温度", "°C", 0, 100, ThermVisuals.GpuGlyph);
        _fan = new GaugeCard("风扇转速", "RPM", 0, 5000, ThermVisuals.FanGlyph);
        _state.SetResourceReference(TextBlock.ForegroundProperty, "PanelTertiary");
        var cards = new UniformGrid { Rows = 1, Columns = 3 };
        foreach (var card in new[] { _cpu, _gpu, _fan }) cards.Children.Add(card.Root);
        var sheet = new StackPanel { Width = 592, VerticalAlignment = VerticalAlignment.Center };
        sheet.Children.Add(cards);
        sheet.Children.Add(_state);
        Children.Add(new Viewbox { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, Child = sheet });
        workspace.Changed += Refresh;
        Refresh();
    }

    // The card row scales as one piece with the panel's size, so no per-viewport work is needed.
    public void SetViewport(double width, double height, double scale = 1) { }

    public void Refresh()
    {
        foreach (var card in new[] { _cpu, _gpu, _fan }) card.SetTitle();
        var sample = _workspace.Latest;
        if (sample is null)
        {
            _cpu.Show(0, "—", "—", ThermLevels.Green);
            _gpu.Show(0, "—", "—", ThermLevels.Green);
            _fan.Show(0, "—", "—", ThermLevels.Green);
            _state.Text = Loc.T("THRM 桥未运行", "THRM bridge unreachable");
            _state.Visibility = Visibility.Visible;
            return;
        }
        // The arc carries the level's color; the figures stay in the palette's primary, as the panel's own numbers do.
        _cpu.Show(sample.CpuTemp, $"{sample.CpuTemp:0}", $"{sample.CpuFan:0} RPM", ThermLevels.Temperature(sample.CpuTemp));
        _gpu.Show(sample.GpuTemp, $"{sample.GpuTemp:0}", $"{sample.GpuFan:0} RPM", ThermLevels.Temperature(sample.GpuTemp));
        string second = sample.TargetRpm > 0 ? Loc.T($"目标 {sample.TargetRpm:0} RPM", $"target {sample.TargetRpm:0} rpm") : "";
        if (sample.Mode.Length > 0) second = second.Length > 0 ? second + " · " + sample.Mode : sample.Mode;
        _fan.Show(sample.CpuFan, $"{sample.CpuFan:0}", second, ThermLevels.Fan(sample.CpuFan));
        _state.Text = sample.Mode.Length > 0 ? sample.Mode : "";
        _state.Visibility = _state.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Dispose()
    {
        _workspace.Changed -= Refresh;
        Children.Clear();
    }

    // One gauge card: its mark and title across the top, a half-circle arc filled to the reading's share (round
    // caps, drawn from the left end clockwise over the top), the figure large with its unit small at its foot, and
    // a second line under that.
    private sealed class GaugeCard
    {
        private const double ArcWidth = 150, ArcHeight = 82, ArcStroke = 5.5;
        private static readonly Geometry ArcGeometry = Arc();
        private readonly string _title, _unit;
        private readonly double _min, _max;
        private readonly TextBlock _titleText = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        private readonly FrameworkElement _icon;
        private readonly TextBlock _value = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock _unitText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 0, 6) };
        private readonly TextBlock _sub = new() { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly Path _track = new() { Data = ArcGeometry, StrokeThickness = ArcStroke, Stretch = Stretch.None, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly Path _fill = new() { Data = ArcGeometry, StrokeThickness = ArcStroke, Stretch = Stretch.None, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly Border _root;

        public FrameworkElement Root => _root;

        public GaugeCard(string title, string unit, double min, double max, string icon)
        {
            _title = title; _unit = unit; _min = min; _max = max;
            _icon = ThermVisuals.Icon(icon, 13, "PanelSecondary");
            _titleText.SetResourceReference(TextBlock.ForegroundProperty, "PanelSecondary");
            _value.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary");
            _unitText.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary");
            _sub.SetResourceReference(TextBlock.ForegroundProperty, "PanelTertiary");
            _track.SetResourceReference(Shape.StrokeProperty, "PanelTrack");

            var head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
            head.Children.Add(_icon);
            head.Children.Add(_titleText);
            var arc = new Canvas { Width = ArcWidth, Height = ArcHeight, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            arc.Children.Add(_track);
            arc.Children.Add(_fill);
            var figure = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -40, 0, 0) };
            figure.Children.Add(_value);
            figure.Children.Add(_unitText);
            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(arc);
            stack.Children.Add(figure);
            _sub.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(_sub);
            _root = new Border { Child = stack, Padding = new Thickness(10, 12, 10, 14), Margin = new Thickness(7, 0, 7, 0), CornerRadius = new CornerRadius(12) };
            _root.SetResourceReference(Border.BackgroundProperty, "PanelFill");
        }

        internal void SetTitle() => _titleText.Text = Loc.T(_title, _title switch { "CPU 温度" => "CPU temp", "GPU 温度" => "GPU temp", _ => "Fan speed" });

        public void Show(double value, string figure, string second, string levelColor)
        {
            _value.Text = figure;
            _unitText.Text = _unit;
            _sub.Text = second;
            _sub.Visibility = second.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _fill.Stroke = ThermVisuals.Brush(levelColor);
            double share = _max > _min ? Math.Clamp((value - _min) / (_max - _min), 0, 1) : 0;
            // StrokeDashArray counts multiples of the stroke width, not pixels.
            double span = Math.PI * (ArcWidth / 2 - ArcStroke) / ArcStroke;
            _fill.StrokeDashArray = [span * share, span * 2];
        }

        // The half circle, round caps, drawn from the left end clockwise over the top.
        private static Geometry Arc()
        {
            double r = ArcWidth / 2 - ArcStroke, cx = ArcWidth / 2, cy = ArcHeight - 8;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(cx - r, cy), false, false);
                ctx.ArcTo(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            return geometry;
        }
    }
}
