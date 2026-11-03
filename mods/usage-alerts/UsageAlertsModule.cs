using System.Windows;
using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Modules;

namespace BrimDeck.UsageAlerts;

public sealed class AlertOptions
{
    public bool Enabled { get; set; } = true;
    public double Threshold { get; set; } = 80;
}
public sealed class UsageAlertsModule : IModule
{
    private AlertOptions _options = new();
    private IModuleContext _context = null!;
    private IModuleUsage _usage = null!;
    private IModuleMedia _media = null!;
    private IModuleSystem _system = null!;
    private IModuleNotifications _notices = null!;
    private ThresholdGate _gate = null!;
    private AlertState _state = new();
    private bool _storageWritable;
    public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        var options = context.Storage.ReadResult<AlertOptions>("options");
        var state = context.Storage.ReadResult<AlertState>("alerts");
        _storageWritable = (options.Status is ModuleReadStatus.Missing or ModuleReadStatus.Value) && (state.Status is ModuleReadStatus.Missing or ModuleReadStatus.Value);
        if (!_storageWritable) context.Report("read alert data", new InvalidDataException("Saved alert data is corrupt or unsupported; it will not be overwritten."));
        _options = options.Value ?? new();
        _options.Threshold = double.IsFinite(_options.Threshold) ? Math.Clamp(_options.Threshold, 1, 100) : 80;
        _state = state.Value is { Marks: not null } value ? value : new();
        foreach (string key in _state.Marks.Where(p => p.Value is null).Select(p => p.Key).ToArray()) _state.Marks.Remove(key);
        _gate = new(_state);
        _usage = context.GetService<IModuleUsage>(); _media = context.GetService<IModuleMedia>();
        _system = context.GetService<IModuleSystem>(); _notices = context.GetService<IModuleNotifications>();
        _usage.Subscribe(_ => Evaluate());
        // Acquire monitor/media demand once per module, rather than once per view or display.
        _media.Subscribe(_ => { }); _system.Subscribe(_ => { });
        context.Every(TimeSpan.FromSeconds(1), Evaluate);
        context.Register("dashboard", new ModulePage("用量提醒", "Usage alerts", view => new Dashboard(this, view), 400));
        context.Register<IModuleSettings>("preferences", new Preferences(this));
        return ValueTask.CompletedTask;
    }
    private void Evaluate()
    {
        if (!_options.Enabled) return;
        if (_gate.Evaluate(_usage.GetSnapshot(), _options.Threshold, DateTimeOffset.UtcNow, _notices.Show) && _storageWritable) _context.Storage.Write("alerts", _state);
    }
    private void SaveOptions()
    { if (_storageWritable) _context.Storage.Write("options", _options); }
    private sealed class Preferences(UsageAlertsModule module) : IModuleSettings
    {
        public string Name => "用量提醒";
        public string NameEn => "Usage alerts";
        public bool Visible => true;
        public IReadOnlyList<ModuleSetting> Fields => [new("enabled", "开启阈值提醒", "Enable alerts", ModuleSettingKind.Toggle), new("threshold", "Codex 提醒阈值", "Codex threshold", ModuleSettingKind.Number, 1, 100, "%", "%")];
        public object Read(string key) => key switch { "enabled" => module._options.Enabled, "threshold" => module._options.Threshold, _ => throw new ArgumentException("Unknown setting.") };
        public void Write(string key, object value)
        {
            if (key == "enabled" && value is bool enabled) module._options.Enabled = enabled;
            else if (key == "threshold" && value is double threshold && double.IsFinite(threshold)) module._options.Threshold = Math.Clamp(threshold, 1, 100);
            else throw new ArgumentException("Invalid setting.");
            module.SaveOptions();
        }
        public void Reset() { module._options = new(); module.SaveOptions(); }
    }
    private sealed class Dashboard : IModuleView
    {
        private readonly UsageAlertsModule _module;
        private readonly Grid _root = new();
        private readonly TextBlock _usage, _media, _system;
        public FrameworkElement Element => _root;
        internal Dashboard(UsageAlertsModule module, ModuleViewContext view)
        {
            _module = module;
            for (int i = 0; i < 4; i++) _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            _usage = Add(0); _media = Add(1); _system = Add(2);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Button Action(string label, Action run)
            {
                var button = new Button { Content = ModuleUi.Text(label, 12), Width = double.NaN, MinWidth = 80, Height = 28, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(12, 2, 12, 2) };
                button.SetResourceReference(FrameworkElement.StyleProperty, "PanelSourceButton"); AutomationProperties.SetName(button, label);
                button.Click += (_, _) => run(); actions.Children.Add(button); return button;
            }
            Action("提醒设置", () => view.OpenSettings("sample-usage-alerts:preferences"));
            var test = Action("测试动画", () => module._notices.Show(new("test", "用量提醒 · 测试", "这是示例动画") { Tone = ModuleNoticeTone.Warning, ProgressPercent = 80 }));
            test.ToolTip = "收起岛后显示；不改变真实用量和阈值记录。";
            Grid.SetRow(actions, 3); _root.Children.Add(actions); view.Every(TimeSpan.FromSeconds(1), Refresh); Refresh();
        }
        private TextBlock Add(int row)
        {
            var text = ModuleUi.Text("", 13); text.TextAlignment = TextAlignment.Left; text.HorizontalAlignment = HorizontalAlignment.Stretch;
            text.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary");
            text.TextTrimming = TextTrimming.CharacterEllipsis; text.Margin = new Thickness(8, 0, 8, 0);
            Grid.SetRow(text, row); _root.Children.Add(text); return text;
        }
        public void Refresh()
        {
            var codex = _module._usage.GetSnapshot().Entries.FirstOrDefault(e => e.ProviderId == "codex");
            _usage.Text = codex?.Status == ModuleDataStatus.Available
                ? "Codex  " + string.Join(" · ", codex.Metrics.Select(m => m.UsedPercent is { } p ? $"{m.Label} {p:0.#}%" : m.Label + " —"))
                : "Codex  " + (codex?.Status == ModuleDataStatus.Stale ? "数据已过期，等待本体更新" : "等待本体读取用量");
            var media = _module._media.GetSnapshot(); _media.Text = media.Status == ModuleDataStatus.Available ? $"正在播放  {media.Title} · {media.Artist}" : "正在播放  暂无媒体会话";
            var system = _module._system.GetSnapshot();
            string Number(double? value) => value is { } number ? $"{number:0.#}%" : "—";
            _system.Text = $"系统监控  CPU {Number(system.CpuUsedPercent)}   内存 {Number(system.MemoryUsedPercent)}" + (system.Status == ModuleDataStatus.Stale ? " · 数据过期" : "");
            foreach (var text in new[] { _usage, _media, _system }) text.ToolTip = text.Text;
        }
        public void SetViewport(double width, double height, double scale = 1) { }
        public void Dispose() => _root.Children.Clear();
    }
}
