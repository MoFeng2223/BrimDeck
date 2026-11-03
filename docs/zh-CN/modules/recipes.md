# 模组开发配方

适用契约：1.6。先用[开发指南](contributing.md)创建项目；本文代码块注明所处范围，避免把示意片段误当作完整模组。界面规范见 [UI 规范](ui.md)，宿主行为见[宿主规则](host-rules.md)。

## 只在页面可见时更新显示

下面是完整的视图类，可直接放入已有模组项目。它只把显示刷新暂停；如果是倒计时，业务时钟仍应属于模组实例。

```csharp
using System.Windows;
using System.Windows.Controls;
using BrimDeck;

public sealed class VisibleClockView : Grid, IModuleView
{
    private readonly TextBlock _text = ModuleUi.Text("", 24);
    public FrameworkElement Element => this;

    public VisibleClockView(ModuleViewContext context)
    {
        Children.Add(_text);
        context.OnVisibilityChanged(visible => { if (visible) Refresh(); });
        context.EveryVisible(TimeSpan.FromSeconds(1), Refresh);
        Refresh();
    }
    public void Refresh()
    {
        _text.Text = DateTime.Now.ToString("HH:mm:ss");
        _text.Foreground = ModuleUi.Brush(ModuleUi.Primary);
    }
    public void SetViewport(double width, double height, double scale = 1) { }
    public void Dispose() => Children.Clear();
}
```

异步挂载可用 `ModulePage.Async`；工作完成后通过 `context.Owner.Dispatcher.InvokeAsync` 创建 WPF 视图，检查 `context.Lifetime`。不要从后台线程创建控件，也不要把工作结果保存在旧视图以外的静态控件变量。完整实现见[便笺示例](../../../mods/notes/NotesModule.cs)。

## 多个组件共用一个独立设置页

以下是完整设置类。写入验证由作者实现，这里只保存内存值；实际模组在成功写入后按需调用存储和 `IModulePresentation.Invalidate()`。分组由字段出现顺序决定，同一组的字段统一排列。

```csharp
using BrimDeck;

public sealed class AppearanceSettings : IModuleSettingsNavigation
{
    private string _title = "我的模组", _color = "#4488CC";
    private double _interval = 1;
    public string Name => "外观与行为";
    public string NameEn => "Appearance and behavior";
    public bool Visible => true;
    public ModuleSettingsPlacement Placement => ModuleSettingsPlacement.Modules;
    public int Order => 100;
    public string IconPath => "M4,4 L16,4 L16,16 L4,16 Z";
    public IReadOnlyList<ModuleSetting> Fields => [
        new("title", "名称", "Name", ModuleSettingKind.Text) {
            MaximumLength = 40, Group = "外观", GroupEn = "Appearance",
            Placeholder = "输入显示名称", PlaceholderEn = "Enter a display name" },
        new("accent", "强调色", "Accent", ModuleSettingKind.Color) {
            Group = "外观", GroupEn = "Appearance" },
        new("interval", "刷新间隔", "Refresh interval", ModuleSettingKind.Number, .25, 10, "秒", "s") {
            Step = .25, Group = "行为", GroupEn = "Behavior",
            Description = "只影响界面刷新频率。", DescriptionEn = "Only affects display refresh." }
    ];
    public object Read(string key) => key switch {
        "title" => _title, "accent" => _color, "interval" => _interval,
        _ => throw new ArgumentException("Unknown setting", nameof(key))
    };
    public void Write(string key, object value)
    {
        switch (key)
        {
            case "title" when value is string text && text.Length <= 40:
                _title = text; break;
            case "accent" when value is string color && color.Length == 7 && color[0] == '#'
                && color.AsSpan(1).ToArray().All(Uri.IsHexDigit):
                _color = color.ToUpperInvariant(); break;
            case "interval" when value is double number && double.IsFinite(number) && number >= .25 && number <= 10:
                _interval = number; break;
            default: throw new ArgumentException("Invalid setting value", nameof(value));
        }
    }
    public void Reset() { _title = "我的模组"; _color = "#4488CC"; _interval = 1; }
}
```

在 `ActivateAsync` 内登记，以下片段使用当前 `context`；清单声明 `ui.settings` 与 `ui.widgets`，minor 为 6：

```csharp
var registry = context.Platform().Registries;
registry.Register<IModuleSettings>("appearance", new AppearanceSettings());
string settingsId = context.ModuleId + ":appearance";
registry.Register("first", new ModuleWidget("组件一", "First", settingsId, () => true, _ => { }));
registry.Register("second", new ModuleWidget("组件二", "Second", settingsId, () => true, _ => { }));
```

宿主生成一个模组设置入口。生产代码应将组件开关委托接到真实偏好；此片段的固定开启仅用于说明共享引用。希望跟随小程序组件显隐时选 `MiniApps`，兼容旧关联规则时保留 `Automatic`。

## 用量达到阈值后提示

复用[用量提醒示例](../../../mods/usage-alerts/README.md)。它通过 `IModuleUsage` 读取快照，以配额周期去重，通过 `IModuleNotifications` 提示，不读取密钥或接管窗口。只对可用且未过期的数据判定阈值，限流后允许重试；模组停用后撤销订阅和未展示通知。

提醒判定归模组级，页面显示刷新归视图级。不要把用量告警放进 `EveryVisible`，否则收起页面会停止告警；也不要为每个显示器分别发一次业务通知。

## 设置有问题时定位

1. 打开模组管理详情查看配置诊断；同一代相同来源和代码只显示一条。
2. 检查字段键、返回类型、数值范围、选项唯一性和颜色格式。字段被跳过不等于模组整体失败。
3. 若存在运行错误，检查作者的 `Fields`、`Read`、`Write`、回调是否抛出异常。修复后构建、更新安装目录并刷新重新启用。
4. 查看性能时区分启动经过时间、宿主管理回调时间和自建后台任务；当前没有独立模组 CPU／内存测量，不把低回调耗时解释成没有后台开销。

## 状态、通知与多屏

完整计时器示例展示模组级单一会话，页面、展开控制条和紧凑视图共享状态而不共享控件。业务只发布一次 `Changed`，各视图按自己的区域刷新。通知由宿主覆盖活动并恢复；模组收到不可见变化时只停显示任务，不重置会话。

停用时通过激活上下文资源清理保存允许的最终快照；重新启动从数据恢复，不能依赖视图释放表示用户退出。数据缺失与损坏分别处理，推荐 `Storage.ReadResult<T>`。布局、主题、语言变化不得覆盖输入草稿或当前计时。
