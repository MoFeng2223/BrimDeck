using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Modules;
using Loc = BrimDeck.ModuleLocalization;

namespace BrimDeck.Thrm;

// The collapsed row's persistent reading: the three cooler figures where the built-in readings would sit. The
// readings are presence, not a running task, so the activity is always on (enabling the module is the switch) and
// Passive — expanding the panel opens the pages instead of a control bar. The state still reports the CPU
// temperature as the row indicator's share and color, for the line style's progress bar.
internal sealed class ThermActivity(ThermWorkspace workspace) : IModuleActivity
{
    public ActivityState State
    {
        get
        {
            var sample = workspace.Latest;
            double share = sample is null ? 0 : Math.Clamp(sample.CpuTemp / 100, 0, 1);
            return new(true, share, false, ThermLevels.Temperature(sample?.CpuTemp ?? 0)) { Passive = true };
        }
    }
    public event Action? Changed { add => workspace.Changed += value; remove => workspace.Changed -= value; }
    public IModuleView CreateView(ModuleViewContext context, ActivityViewKind kind) => new ThermActivityView(workspace, context, kind);
}

// The compact row holds the three readings at the row's scale; the expanded strip (the module's control bar, shown
// only when a view calls ShowActivity) holds the same three readings larger with the bridge's mode beneath. Both
// redraw from the workspace's latest sample.
internal sealed class ThermActivityView : Grid, IModuleView, IModuleLayoutView
{
    private readonly ThermWorkspace _workspace;
    private readonly ActivityViewKind _kind;
    private readonly ThermReading _cpu = new("C", "100°"), _gpu = new("G", "100°"), _fan = new("R", "8888");
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _state = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
    private double _scale = -1;

    public FrameworkElement Element => this;

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
        else Children.Add(_row);
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
