using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// One application's points (days or hours) in the usage trend, drawn in its colour.
internal sealed record UsageTrendSeries(string Name, string Color, IReadOnlyList<UsageTrendPoint> Points);

// Usage bars by day or by hour for the model-details page. Values are tokens or USD cost. With several applications each bar is
// their parts stacked in the order given, the first at the bottom; the tooltip names the day or hour and each application's amount.
internal sealed class UsageTrendGraph : FrameworkElement
{
    private PanelLook Look => PanelLook.Of(this);
    private IReadOnlyList<UsageTrendSeries> _series = [];
    private IReadOnlyList<UsageTrendPoint> _days = [];
    private bool _cost, _hourly;
    private int _hot = -1;
    private readonly ToolTip _tip = new();

    public UsageTrendGraph()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        _tip.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
        _tip.PlacementTarget = this;
    }

    public void Set(IReadOnlyList<UsageTrendSeries> series, bool cost, bool hourly)
    {
        _series = series; _days = UsageTrend.Sum([.. series.Select(s => s.Points)]); _cost = cost; _hourly = hourly;
        _hot = -1; _tip.IsOpen = false;
        InvalidateVisual();
    }

    private string Amount(UsageTrendPoint day) => _cost
        ? UI.Cost(new CostSummary(day.Cost, day.Cost > 0 ? 1 : 0, 0, day.Unpriced))
        : Loc.T($"{UI.Number(day.Tokens)} Token", $"{UI.Number(day.Tokens)} tokens");

    private UIElement TipContent(int index)
    {
        var day = _days[index];
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Line(string text, int column, int span = 1, TextAlignment alignment = TextAlignment.Left, double left = 0)
        {
            var block = new TextBlock { Text = text, TextAlignment = alignment, Margin = new Thickness(left, 0, 0, 0) };
            Grid.SetRow(block, grid.RowDefinitions.Count - 1); Grid.SetColumn(block, column); Grid.SetColumnSpan(block, span);
            grid.Children.Add(block);
        }
        void Row() => grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string when = _hourly ? $"{day.Start.ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture)}–{day.Start.AddHours(1).ToString("HH:00", CultureInfo.InvariantCulture)}"
            : day.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Row(); Line(when, 0, 3);
        if (_series.Count > 1)
        {
            // Listed top to bottom as the parts are stacked, the top part first.
            foreach (var series in _series.Reverse())
            {
                Row();
                var dot = new Ellipse { Width = 7, Height = 7, Fill = UI.Brush(series.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                Grid.SetRow(dot, grid.RowDefinitions.Count - 1); grid.Children.Add(dot);
                Line(series.Name, 1);
                Line(Amount(series.Points[index]), 2, alignment: TextAlignment.Right, left: 16);
            }
            Row(); Line(Loc.T("合计", "Total"), 0, 2); Line(Amount(day), 2, alignment: TextAlignment.Right, left: 16);
        }
        else { Row(); Line(Amount(day) + (_cost ? "" : $" ({day.Tokens:N0})"), 0, 3); }
        if (_cost && day.Unpriced > 0)
        {
            Row();
            Line(day.Cost == 0 ? Loc.T("未匹配到完整价格", "Prices could not be matched for every record")
                : Loc.T($"{day.Unpriced} 条记录未计价", Loc.Count(day.Unpriced, "record has", "records have") + " no price"), 0, 3);
        }
        return grid;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int index = Hit(e.GetPosition(this).X);
        if (index == _hot) return;
        _hot = index;
        if (index < 0 || index >= _days.Count) { _tip.IsOpen = false; InvalidateVisual(); return; }
        _tip.Content = TipContent(index);
        _tip.IsOpen = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hot = -1; _tip.IsOpen = false; InvalidateVisual();
    }

    private int Hit(double x)
    {
        if (_days.Count == 0 || ActualWidth <= 0) return -1;
        var (gap, bar) = Layout();
        if (bar <= 0) return -1;
        double at = Pad;
        for (int i = 0; i < _days.Count; i++)
        {
            if (x >= at && x < at + bar) return i;
            at += bar + gap;
        }
        return -1;
    }

    private const double Pad = UsageTrend.GraphPad, LabelBand = 16;
    private (double Gap, double Bar) Layout() => UsageTrend.Layout(_days.Count, ActualWidth);

    protected override void OnRender(DrawingContext dc)
    {
        // A transparent ground makes zero-height days and the gaps around the labels hit-testable.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_days.Count == 0 || ActualWidth <= 0 || ActualHeight <= LabelBand) return;
        var (gap, bar) = Layout();
        double pitch = bar + gap;
        double chartTop = 2, chartBottom = ActualHeight - LabelBand, chartHeight = Math.Max(1, chartBottom - chartTop);
        double Value(UsageTrendPoint day) => _cost ? (double)day.Cost : day.Tokens;
        double max = _days.Max(Value);
        if (max <= 0) max = 1;
        var fills = _series.Select(series => UI.Brush(series.Color)).ToArray();
        for (int i = 0; i < _days.Count; i++)
        {
            var day = _days[i];
            double x = Pad + i * pitch;
            // While a day is under the pointer the others step back.
            bool faded = _hot >= 0 && i != _hot;
            if (faded) dc.PushOpacity(0.5);
            if (_cost && day.Unpriced > 0 && day.Cost == 0)
            {
                // No price for any record that day: a low mark in the colour of the first application with unpriced records.
                int first = Math.Max(0, _series.ToList().FindIndex(series => series.Points[i].Unpriced > 0));
                dc.PushOpacity(0.45);
                dc.DrawRoundedRectangle(fills[first], null, new Rect(x, Math.Round(chartBottom - 2), bar, 2), 1, 1);
                dc.Pop();
            }
            else if (Value(day) > 0)
            {
                double height = Math.Max(2, chartHeight * (Value(day) / max));
                double top = Math.Round(chartBottom - height);
                // The parts are cut from one rounded bar, each from the rounded edge of the sum below it, so they meet without seams.
                dc.PushClip(new RectangleGeometry(new Rect(x, top, bar, chartBottom - top), 2, 2));
                double below = 0;
                for (int s = 0; s < _series.Count; s++)
                {
                    double value = Value(_series[s].Points[i]);
                    if (value <= 0) continue;
                    double lower = Math.Round(chartBottom - height * below / Value(day));
                    below += value;
                    double upper = s == _series.Count - 1 ? top : Math.Round(chartBottom - height * below / Value(day));
                    if (lower > upper) dc.DrawRectangle(fills[s], null, new Rect(x, upper, bar, lower - upper));
                }
                dc.Pop();
            }
            if (faded) dc.Pop();
        }
        DrawLabels(dc, bar, pitch, chartBottom);
    }

    // Labels centred under their bars. By day they count back from the last day, so the latest always has one. By hour they fall on
    // whole multiples of the step from the first midnight and read the hour, or the date where a day begins in a range of several
    // days or the step is a day or more. The step keeps them apart (UsageTrend.LabelStep); one pushed in from an edge that would
    // still touch its neighbour is left out.
    private void DrawLabels(DrawingContext dc, double bar, double pitch, double chartBottom)
    {
        var brush = UI.Brush(Look.Tertiary);
        var typeface = new Typeface(UI.PanelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool severalDays = _days[^1].Start.Date > _days[0].Start.Date;
        int step = 1;
        string Text(int i)
        {
            var start = _days[i].Start;
            bool date = !_hourly || step >= 24 || severalDays && start.Hour == 0;
            return start.ToString(date ? "MM-dd" : "HH:00", CultureInfo.InvariantCulture);
        }
        FormattedText Label(int i) => new(Text(i), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, brush, ppd);
        step = UsageTrend.LabelStep(pitch, Label(_days.Count - 1).Width, _hourly);
        int first = _hourly ? (_days.Count - 1) / step * step : _days.Count - 1;
        double nextLeft = double.PositiveInfinity;
        for (int i = first; i >= 0; i -= step)
        {
            var text = Label(i);
            double left = Math.Round(Math.Clamp(Pad + i * pitch + (bar - text.Width) / 2, 0, Math.Max(0, ActualWidth - text.Width)));
            if (left + text.Width + 4 > nextLeft) continue;
            dc.DrawText(text, new Point(left, chartBottom + 2));
            nextLeft = left;
        }
    }
}
