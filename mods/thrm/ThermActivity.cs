using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Modules;
using Loc = BrimDeck.ModuleLocalization;

namespace BrimDeck.Thrm;

// The compact row holds the three readings at the row's scale, as one part of the collapsed row beside the built-in
// readings; the expanded strip (the module's control bar, shown only when a view calls ShowActivity) holds the same
// three readings larger with the bridge's mode beneath. Both redraw from the workspace's latest sample.
internal sealed class ThermActivityView : Grid, IModuleView, IModuleLayoutView, IModuleCompactWidth
{
    private readonly ThermWorkspace _workspace;
    private readonly ActivityViewKind _kind;
    private readonly ThermReading _cpu = new("C", "100°"), _gpu = new("G", "100°"), _fan = new("R", "8888");
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _state = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
    private double _scale = -1;

    public FrameworkElement Element => this;

    // The room the three readings ask for at scale 1, from the fixed columns ThermReading holds (their dials, gaps and
    // measured figure columns), so the row's arrangement never waits for a layout pass. The host scales it like the
    // built-in readings. Only the compact view offers itself as a row part.
    public double? PreferredWidth => _kind == ActivityViewKind.Expanded ? null : Math.Ceiling(_cpu.Width + _gpu.Width + _fan.Width + 3 * 6);
    // Down a side column the three readings stack, one per line: the column is narrow, so the part takes three lines and
    // the widest reading's width rather than the sum of them.
    public double? PreferredHeight => _kind == ActivityViewKind.Expanded ? null : Math.Ceiling(3 * 24.0 + 2 * 2);
    // A narrower form when the row is short of room: the temperatures only, the fan speed gives way (it is the reading a
    // glance cares about least, and its figures are the widest).
    public double? CompactWidth => _kind == ActivityViewKind.Expanded ? null : Math.Ceiling(_cpu.Width + _gpu.Width + 2 * 6);
    /// <summary>Whether the fan reading is shown at the current width: the view drops it when the room is the compact one.</summary>
    private bool _compactFan = true;


    internal ThermActivityView(ThermWorkspace workspace, ModuleViewContext context, ActivityViewKind kind)
    {
        _workspace = workspace; _kind = kind;
        foreach (var reading in new[] { _cpu, _gpu, _fan }) _row.Children.Add(reading.Root);
        if (kind == ActivityViewKind.Expanded)
        {
            _state.SetResourceReference(TextBlock.ForegroundProperty, "PanelTertiary");
            var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            column.Children.Add(_row);
            column.Children.Add(_state);
            Children.Add(column);
        }
        else
        {
            // The compact view serves both directions: a wide viewport keeps the row, a narrow one stacks the readings.
            Children.Add(_row);
            SizeChanged += (_, _) =>
            {
                _row.Orientation = ActualWidth >= ActualHeight ? Orientation.Horizontal : Orientation.Vertical;
                // The host may give the compact room (CompactWidth); the fan reading is what goes.
                bool fan = ActualWidth >= PreferredWidth - .5 || _row.Orientation == Orientation.Vertical;
                if (fan != _compactFan) { _compactFan = fan; _fan.Root.Visibility = fan ? Visibility.Visible : Visibility.Collapsed; }
            };
        }
        workspace.Changed += Refresh;
        Refresh();
    }

    public void SetViewport(double width, double height, double scale = 1)
    {
        if (_kind == ActivityViewKind.Expanded) return;
        ApplyScale(Math.Clamp(scale, .75, 2));
    }

    public void SetViewport(ModuleViewport viewport)
    {
        if (_kind == ActivityViewKind.Expanded)
        {
            Margin = new Thickness(viewport.HorizontalInset, 0, viewport.HorizontalInset, 0);
            return;
        }
        ApplyScale(Math.Clamp(viewport.ContentScale, .75, 2));
    }

    private void ApplyScale(double scale)
    {
        if (Math.Abs(_scale - scale) > .01)
        {
            _scale = scale;
            foreach (var reading in new[] { _cpu, _gpu, _fan }) reading.Rescale(scale);
            double gap = ThermVisuals.Whole(6 * scale);
            for (int at = 0; at < _row.Children.Count; at++)
                ((FrameworkElement)_row.Children[at]).Margin = at < _row.Children.Count - 1 ? new Thickness(0, 0, gap, 0) : default;
        }
        Refresh();
    }

    public void Refresh()
    {
        var sample = _workspace.Latest;
        if (sample is null)
        {
            _cpu.Show("—", 0, ThermLevels.Green);
            _gpu.Show("—", 0, ThermLevels.Green);
            _fan.Show("—", 0, ThermLevels.Green);
        }
        else
        {
            // The figures stay neutral; the level shows in the dial's arc alone.
            _cpu.Show($"{sample.CpuTemp:0}°", sample.CpuTemp / 100, ThermLevels.Temperature(sample.CpuTemp));
            _gpu.Show($"{sample.GpuTemp:0}°", sample.GpuTemp / 100, ThermLevels.Temperature(sample.GpuTemp));
            _fan.Show($"{sample.CpuFan:0}", Math.Clamp(sample.CpuFan / 5000, 0, 1), ThermLevels.Fan(sample.CpuFan));
        }
        _state.Text = sample is null ? Loc.T("THRM 桥未运行", "THRM bridge unreachable")
            : sample.Mode.Length > 0 ? sample.Mode : "";
        _state.Visibility = _state.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        string name = sample is null ? Loc.T("散热器读数：等待 THRM 桥", "Cooler readings: waiting for the THRM bridge")
            : Loc.T($"散热器读数：CPU {sample.CpuTemp:0}°，GPU {sample.GpuTemp:0}°，风扇 {sample.CpuFan:0} 转每分",
                $"Cooler readings: CPU {sample.CpuTemp:0}°, GPU {sample.GpuTemp:0}°, fan {sample.CpuFan:0} rpm");
        AutomationProperties.SetName(this, name);
    }

    public void Dispose()
    {
        _workspace.Changed -= Refresh;
        Children.Clear();
    }
}
