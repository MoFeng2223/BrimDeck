using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Modules;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private DispatcherTimer? _modulePerformanceTimer;
    private readonly List<Action<IReadOnlyDictionary<string, ModulePerformance>>> _moduleMetricRefresh = [];
    private IReadOnlyDictionary<string, ModulePerformance>? _moduleInitialMetrics;
    private static string MetricTime(double value) => value is > 0 and < .1 ? "<0.1 ms" : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " ms";
    private static void SetMetricText(TextBlock block, string value) { if (block.Text != value) block.Text = value; }
    private void AddModuleMetricRefresh(Action<IReadOnlyDictionary<string, ModulePerformance>> update)
    { _moduleMetricRefresh.Add(update); update(_moduleInitialMetrics ??= _app.Modules.Performance.ToDictionary(p => p.ModuleId)); }
    private void StopModuleMonitoring()
    { _modulePerformanceTimer?.Stop(); _moduleMetricRefresh.Clear(); _moduleInitialMetrics = null; }
    private void UpdateModuleMonitoring()
    {
        bool run = _page == ModulesPage && IsVisible && WindowState != WindowState.Minimized && _moduleMetricRefresh.Count > 0;
        if (!run) { _modulePerformanceTimer?.Stop(); return; }
        _modulePerformanceTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshModuleMetrics(), Dispatcher);
        if (!_modulePerformanceTimer.IsEnabled) _modulePerformanceTimer.Start();
    }
    private void RefreshModuleMetrics()
    {
        if (_page != ModulesPage || !IsVisible || WindowState == WindowState.Minimized) { _modulePerformanceTimer?.Stop(); return; }
        var values = _app.Modules.Performance.ToDictionary(p => p.ModuleId);
        foreach (var update in _moduleMetricRefresh.ToArray()) update(values);
    }
    private Border MetricTile(string title, out TextBlock number)
    {
        var stack = new StackPanel(); var caption = TextLine(title, 10.5, TextSecondary); caption.Margin = new Thickness(0, 0, 0, 7); stack.Children.Add(caption);
        number = TextLine("—", 16, TextPrimary, FontWeights.SemiBold); number.FontFamily = UI.PanelFont; Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular); stack.Children.Add(number);
        return new Border { Background = UI.Brush(Inset), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 8, 8), Child = stack };
    }
    private void ModulePerformancePage()
    {
        var summary = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 8) };
        summary.Children.Add(MetricTile(Loc.T("回调用时 / 秒", "Callback time / sec"), out var time));
        summary.Children.Add(MetricTile(Loc.T("慢调用 ≥ 16 ms", "Slow calls ≥ 16 ms"), out var slow));
        summary.Children.Add(MetricTile(Loc.T("错误次数", "Errors"), out var errors));
        _content.Children.Add(summary);
        var scope = TextLine(Loc.T("最近 5 秒平均 · 统计宿主管理的回调和视图调用，不含模组自行创建的后台任务。", "Five-second average · Includes host-managed callbacks and views, excluding independent background tasks."), 11, TextTertiary); scope.TextWrapping = TextWrapping.Wrap; scope.Margin = new Thickness(0, 0, 0, 18); _content.Children.Add(scope);
        var moduleIds = _app.Modules.Modules.Select(m => m.Id).ToHashSet();
        AddModuleMetricRefresh(values =>
        {
            var known = values.Values.Where(p => moduleIds.Contains(p.ModuleId)).ToArray();
            SetMetricText(time, MetricTime(known.Sum(p => p.MillisecondsPerSecond))); SetMetricText(slow, known.Sum(p => p.SlowCalls).ToString("N0")); SetMetricText(errors, known.Sum(p => p.Errors).ToString("N0"));
        });
        foreach (var module in _app.Modules.Modules) _content.Children.Add(ModuleEntryCard(module, true));
        var note = TextLine(Loc.T("累计值从本次应用启动计算。离开模组页或最小化设置窗口后停止刷新。", "Totals cover this app session. Refresh pauses when leaving modules or minimizing settings."), 11, TextTertiary); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 4, 0, 0); _content.Children.Add(note);
    }
    private FrameworkElement ModuleMetrics(string id, bool full)
    {
        var grid = new UniformGrid { Columns = full ? 3 : 2, Margin = new Thickness(0, 0, -8, -8) };
        grid.Children.Add(MetricTile(Loc.T("最近启动", "Last startup"), out var startup));
        grid.Children.Add(MetricTile(Loc.T("回调用时 / 秒", "Callback time / sec"), out var rate));
        grid.Children.Add(MetricTile(Loc.T("最长回调", "Longest callback"), out var longest));
        grid.Children.Add(MetricTile(Loc.T("错误次数", "Errors"), out var errors));
        TextBlock? total = null, calls = null;
        if (full) { grid.Children.Add(MetricTile(Loc.T("累计回调用时", "Total callback time"), out total)); grid.Children.Add(MetricTile(Loc.T("调用次数", "Callbacks"), out calls)); }
        AddModuleMetricRefresh(values =>
        {
            values.TryGetValue(id, out var value);
            SetMetricText(startup, value?.StartupMilliseconds is { } elapsed ? MetricTime(elapsed) : "—");
            SetMetricText(rate, MetricTime(value?.MillisecondsPerSecond ?? 0)); SetMetricText(longest, value is { Calls: > 0 } ? MetricTime(value.LongestMilliseconds) : "—"); SetMetricText(errors, (value?.Errors ?? 0).ToString("N0"));
            if (total is not null) SetMetricText(total, MetricTime(value?.TotalMilliseconds ?? 0)); if (calls is not null) SetMetricText(calls, (value?.Calls ?? 0).ToString("N0"));
        });
        return grid;
    }
    private void ModulePerformanceDetails(ModuleInfo module, StackPanel body)
    {
        body.Children.Add(ModuleMetrics(module.Id, true));
        var detail = TextLine("", 11, TextSecondary); detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(detail);
        AddModuleMetricRefresh(values =>
        {
            values.TryGetValue(module.Id, out var value);
            SetMetricText(detail, Loc.T($"已启动 {value?.Activations ?? 0} 次 · 慢调用 {value?.SlowCalls ?? 0} 次（≥ 16 ms）", $"{value?.Activations ?? 0} activations · {value?.SlowCalls ?? 0} slow calls (≥ 16 ms)")
                + (value?.LongestOperation is { } operation ? "\n" + Loc.T("最长调用：", "Longest call: ") + ModuleOperationName(operation) : ""));
        });
        AddModuleErrors(module, body);
    }
    private static string ModuleOperationName(string operation) => operation switch
    {
        "timer" => Loc.T("模组定时任务", "Module timer"), "view timer" => Loc.T("界面定时任务", "View timer"), "mount view" => Loc.T("创建界面", "Create view"),
        "refresh view" => Loc.T("刷新界面", "Refresh view"), "layout view" => Loc.T("界面布局", "View layout"), "dispose view" => Loc.T("释放界面", "Dispose view"),
        _ when operation.StartsWith("event ", StringComparison.Ordinal) => Loc.T("响应应用事件", "App event"), _ => operation
    };
}
