# BrimDeck 模组开发与贡献指南

适用契约：**1.6，实验阶段**。本指南、[API 参考](api.md)、[平台规范](../../../mods/README.md)是三篇主文档，覆盖开发、构建、安装和维护的必要规则。使用独立 SDK 开发包与自己的工程即可完成本文示例，不需要阅读或引用宿主源码。其他主题文章与仓库示例是可选的深入材料。

| 阅读顺序 | 回答的问题 |
| --- | --- |
| 本指南 | 怎样创建工程、构建、检查、安装、调试与分发？ |
| API 参考 | 类型如何调用，线程、生命周期、设置、布局、数据的规则是什么？ |
| 平台规范 | 清单、目录、启停、版本、信任、恢复和验收遵守什么要求？ |

平台规范的规划章节不是现行 API。当前尚无 NuGet 包、公开 SDK 下载渠道或市场；维护者可在文末按命令导出开发包，作者拿到包后不用宿主仓库。

## 1. 开发环境与 SDK

使用 Windows、**.NET 10 SDK** 和支持 WPF 的编辑器。目标框架为 `net10.0-windows10.0.19041.0`，桌面宿主为 x64。这是编译条件，不扩大项目 README 的系统支持承诺。`dotnet --list-sdks` 应列出 10.x；只有运行时不能编译。

解压与目标宿主契约匹配的 SDK，例如到 `E:/BrimDeck-sdk`：

```text
BrimDeck-sdk/
  sdk.json                          # 契约版本、目标框架和模板目录
  ref/                              # 只供编译，不能执行或安装
    BrimDeck.Modules.Abstractions.dll
    BrimDeck.UI.dll
    BrimDeck.Core.dll                # UI 的传递元数据依赖，不是作者 API
  build/ModuleSdk.props
  build/ModulePackage.targets
  tools/checker/BrimDeck.ModuleCheck.dll
  templates/{clock-demo,counter-demo,usage-guard}/
  docs/zh-CN/modules/{contributing,api}.md
  mods/README.md
  files.json                        # 文件摘要，不代表签名认证
```

SDK 的 `ref/` 是 reference assembly。运行时使用宿主提供的实现，不将其带进模组包。公共 UI 类型使用 `BrimDeck` 命名空间，实际来自 `BrimDeck.UI.dll`；不要因此引用主程序。不要依赖 `BrimDeck.dll`、`BrimDeck.Modules.dll` 或 Core 业务类型。

下面命令可在仓库外执行，支持含空格的路径；复制时使用新的目标工程目录：

```powershell
$sdk = 'E:/BrimDeck-sdk'
$work = 'E:/My modules'
$artifacts = 'E:/My module builds'
New-Item -ItemType Directory -Force -Path $work | Out-Null
Copy-Item -LiteralPath "$sdk/templates/clock-demo" -Destination "$work/clock-demo" -Recurse
dotnet msbuild "$work/clock-demo/BrimDeck.ClockDemo.csproj" -restore -t:GetModulePackage -p:Configuration=Release "-p:BrimDeckSdk=$sdk" "-p:ArtifactsPath=$artifacts" -nologo
```

产物在 `$artifacts/bin/BrimDeck.ClockDemo/release/package/clock-demo/`。`ArtifactsPath` 仅控制构建输出，TEMP、NuGet 等缓存沿用本机配置，需要换盘时另行配置。SDK 可以移动，重新传入路径即可。

包扫描、签名摘要、内部依赖检查、复制和静态检查使用同一目录限制：深度最多 64 层、6000 项、2000 个文件、64 MiB；禁止文件、目录及上层路径中的链接。宿主在创建加载缓存或安装输出前也检查目标路径。随附包的源目录和安装目录不得相同或互相包含。检测到发现前后内容摘要变化时，该次结果失效；完成构建后再刷新列表。

## 2. 第一个完整模组：时钟

下面三个文件就是 SDK 中 `templates/clock-demo` 的全部源码。可复制模板，也可直接按文档建立文件；模板由本文代码块生成，不维护另一份实现。

`BrimDeck.ClockDemo.csproj`：

<!-- module-example: clock-demo project -->
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>BrimDeck.ClockDemo</AssemblyName>
    <ModuleId>clock-demo</ModuleId>
  </PropertyGroup>
  <Import Project="$(BrimDeckSdk)/build/ModuleSdk.props" />
  <ItemGroup><None Update="module.json" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>
  <Import Project="$(BrimDeckSdk)/build/ModulePackage.targets" />
</Project>
```

`module.json`：

<!-- module-example: clock-demo manifest -->
```json
{
  "apiVersion": 1, "apiMinor": 6,
  "id": "clock-demo", "name": "时钟示例", "version": "1.0.0",
  "description": "按宿主可见性刷新当前时间。",
  "descriptionEn": "A clock refreshed while its view is visible.",
  "author": "Example author",
  "assembly": "BrimDeck.ClockDemo.dll", "entryType": "BrimDeck.ClockDemo.ClockModule",
  "capabilities": ["ui.pages"], "dependencies": []
}
```

`Module.cs`：

<!-- module-example: clock-demo source -->
```csharp
using System.Windows;
using System.Windows.Controls;
using BrimDeck;
using BrimDeck.Modules;

namespace BrimDeck.ClockDemo;

public sealed class ClockModule : IModule
{
    public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.Platform().Registries.Register("clock",
            new ModulePage("时钟", "Clock", view => new ClockView(view), 200));
        return ValueTask.CompletedTask;
    }
}
internal sealed class ClockView : Grid, IModuleView
{
    private readonly TextBlock _time = ModuleUi.Text("", 28);
    public FrameworkElement Element => this;
    internal ClockView(ModuleViewContext context)
    {
        Children.Add(_time);
        context.OnVisibilityChanged(visible => { if (visible) Refresh(); });
        context.EveryVisible(TimeSpan.FromSeconds(1), Refresh);
        Refresh();
    }
    public void SetViewport(double width, double height, double scale = 1) { }
    public void Refresh()
    {
        _time.Text = DateTime.Now.ToString("HH:mm:ss");
        _time.Foreground = ModuleUi.Brush(ModuleUi.Primary);
    }
    public void Dispose() => Children.Clear();
}
```

局部 ID `clock` 自动变为 `clock-demo:clock`，注册时不自行加前缀。`ModuleId` 必须与清单 `id` 一致，程序集名和入口完整类型名也须对应。制作自己的模组时同时修改这些名称，保留公开无参入口。

### 静态检查与安装

```powershell
$package = "$artifacts/bin/BrimDeck.ClockDemo/release/package/clock-demo"
dotnet "$sdk/tools/checker/BrimDeck.ModuleCheck.dll" $package --json --warnings-as-errors
```

默认不执行模组代码。退出码 0 为接受，1 为失败（严格模式也包含警告），2 为命令用法错误。JSON 的 `Passed` 仅表达没有错误，`Accepted` 是包含警告策略的最终结果。自动化使用退出码或 `Accepted`。依赖检查可加 `--modules-root '<实际 mods 目录>'`，它不能证明依赖已启用或批准。

1. 在 BrimDeck「设置 → 工具 → 模组」开启系统，点击「打开模组文件夹」。以实际打开目录为准，不猜测 AppData 或便携路径。
2. 复制整个 `package/clock-demo` 到该目录，得到 `mods/clock-demo/module.json`。不要复制构建输出父目录，否则多套一层。
3. 刷新并手动开启「时钟示例」。外来模组默认关闭，开启即批准当前包内容。
4. 开启小程序工作区，展开岛，从小程序页面菜单选择「时钟」。
5. 修改后重新构建、检查、替换原来的同 ID 目录，刷新并重新开启。不要保留两份同 ID 包。内容变更会使旧批准失效；未变化的普通刷新不会重启实例。

宿主从加载副本执行，原始包可更新。当前没有自动开发授权或一键调试附加；可用编辑器附加到测试宿主的 `BrimDeck` 进程调试自己的 DLL。静态检查不验证 UI 和业务行为，交付还需本文与平台规范的实际验收。

## 3. 完整交互模组：页面、活动、组件与设置

`counter-demo` 展示一个模组共享计数，独立创建页面与活动视图，组件开关撤销页面，设置仍保留。只在操作时保存；切页、多屏和通知覆盖不重置计数。

`BrimDeck.CounterDemo.csproj`：

<!-- module-example: counter-demo project -->
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>BrimDeck.CounterDemo</AssemblyName>
    <ModuleId>counter-demo</ModuleId>
  </PropertyGroup>
  <Import Project="$(BrimDeckSdk)/build/ModuleSdk.props" />
  <ItemGroup><None Update="module.json" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>
  <Import Project="$(BrimDeckSdk)/build/ModulePackage.targets" />
</Project>
```

`module.json`：

<!-- module-example: counter-demo manifest -->
```json
{
  "apiVersion": 1, "apiMinor": 6,
  "id": "counter-demo", "name": "计数交互示例", "version": "1.0.0",
  "assembly": "BrimDeck.CounterDemo.dll", "entryType": "BrimDeck.CounterDemo.CounterModule",
  "capabilities": ["ui.pages", "ui.activities", "ui.widgets", "ui.settings", "ui.presentation", "storage.data"],
  "permissions": [], "dependencies": []
}
```

`Module.cs`：

<!-- module-example: counter-demo source -->
```csharp
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Modules;

namespace BrimDeck.CounterDemo;

public sealed record CounterPreferences(bool Enabled = true, int Count = 0, double Step = 1);
public sealed class CounterModule : IModule, IModuleActivity, IModuleSettingsNavigation
{
    private IModuleContext _context = null!;
    private IModulePlatform _platform = null!;
    private IModulePresentation _presentation = null!;
    private IModuleRegistration? _page;
    private CounterPreferences _preferences = new();
    public event Action? Changed;
    public string Name => "计数器";
    public string NameEn => "Counter";
    public bool Visible => true;
    public ModuleSettingsPlacement Placement => ModuleSettingsPlacement.Modules;
    public IReadOnlyList<ModuleSetting> Fields => [
        new("step", "每次增加", "Increment", ModuleSettingKind.Number, 1, 10) { Step = 1 }
    ];
    public ActivityState State => new(_preferences.Enabled && _preferences.Count > 0,
        Math.Clamp(_preferences.Count / 100d, 0, 1), false, ModuleUi.Accent);
    public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context = context; _platform = context.Platform();
        _presentation = _platform.Services.Get<IModulePresentation>();
        var saved = context.Storage.ReadResult<CounterPreferences>("preferences");
        if (saved.Status is ModuleReadStatus.Corrupt or ModuleReadStatus.Unsupported)
            throw new InvalidOperationException(saved.Error ?? "Cannot read preferences; original data retained.");
        if (saved.Value is { } value)
            _preferences = value with { Count = Math.Clamp(value.Count, 0, 9999),
                Step = double.IsFinite(value.Step) ? Math.Clamp(Math.Round(value.Step), 1, 10) : 1 };
        _platform.Registries.Register<IModuleSettings>("settings", this);
        _platform.Registries.Register<IModuleActivity>("counter", this);
        _platform.Registries.Register("counter", new ModuleWidget("计数器", "Counter",
            context.ModuleId + ":settings", () => _preferences.Enabled, SetEnabled));
        SynchronizePage();
        return ValueTask.CompletedTask;
    }
    private void SynchronizePage()
    {
        if (_preferences.Enabled && _page is null)
            _page = _platform.Registries.Register("counter",
                new ModulePage("计数器", "Counter", view => new CounterView(this, view, false, true)));
        if (!_preferences.Enabled) { _page?.Dispose(); _page = null; }
    }
    private void Commit(CounterPreferences next)
    {
        _context.Storage.Write("preferences", next);
        _preferences = next; // Failed saves keep the previous in-memory value.
        SynchronizePage(); Changed?.Invoke(); _presentation.Invalidate();
    }
    private void SetEnabled(bool enabled) => Commit(_preferences with { Enabled = enabled });
    internal void Add() => Commit(_preferences with { Count = Math.Min(9999, _preferences.Count + (int)_preferences.Step) });
    internal void Clear() => Commit(_preferences with { Count = 0 });
    internal int Count => _preferences.Count;
    internal void Report(Exception error) => _context.Report("counter interaction", error);
    public object Read(string key) => key == "step" ? _preferences.Step : throw new ArgumentException("Unknown setting.");
    public void Write(string key, object value)
    {
        if (key != "step" || value is not double number || !double.IsFinite(number)
            || number < 1 || number > 10 || number != Math.Round(number))
            throw new ArgumentException("Increment must be an integer between 1 and 10.");
        Commit(_preferences with { Step = number });
    }
    public void Reset() => Commit(_preferences with { Step = 1 });
    public IModuleView CreateView(ModuleViewContext context, ActivityViewKind kind)
        => new CounterView(this, context, kind == ActivityViewKind.Compact, false);
}
internal sealed class CounterView : Grid, IModuleView, IModuleLayoutView
{
    private readonly CounterModule _model;
    private readonly double _naturalWidth;
    private readonly RollingDigitsView _digits = new(26) { Width = 88 };
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    public FrameworkElement Element => this;
    internal CounterView(CounterModule model, ModuleViewContext context, bool compact, bool page)
    {
        _model = model; _naturalWidth = compact ? 88 : page ? 304 : 250; _row.Children.Add(_digits);
        if (!compact)
        {
            AddButton("+", model.Add); AddButton("归零", model.Clear);
            AddButton("设置", () => context.OpenSettings("counter-demo:settings"));
            if (page) AddButton("驻岛", () => context.ShowActivity("counter-demo:counter"));
        }
        Children.Add(_row);
        model.Changed += Refresh;
        context.OnDispose(() => model.Changed -= Refresh);
        Refresh();
    }
    private void AddButton(string label, Action action)
    {
        var button = new Button { Content = label, Width = 48, Height = 32, Margin = new Thickness(3, 0, 3, 0) };
        button.SetResourceReference(StyleProperty, "PanelTextButton");
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => {
            try { action(); button.Content = label; }
            catch (Exception error) { _model.Report(error); button.Content = "失败"; button.ToolTip = error.Message; }
        };
        _row.Children.Add(button);
    }
    public void Refresh() { _digits.Text = _model.Count.ToString(); _digits.Foreground = ModuleUi.Brush(ModuleUi.Accent); }
    public void SetViewport(double width, double height, double scale = 1)
    {
        double factor = Math.Clamp(Math.Min(scale, Math.Min(width / _naturalWidth, height / 48)), .25, 8);
        _row.LayoutTransform = new ScaleTransform(factor, factor);
    }
    public void SetViewport(ModuleViewport viewport) => SetViewport(viewport.Width, viewport.Height, viewport.ContentScale);
    public void Dispose() => Children.Clear();
}
```

复制 `counter-demo` 模板，按时钟的命令构建并检查；输出程序集目录改为 `BrimDeck.CounterDemo`。加一次计数后活动才活跃，「驻岛」选择活动，归零结束。此例 `Placement=Modules` 让设置独立保留；希望设置跟随组件显隐时选 `Automatic` 或 `MiniApps`，完整规则见 API 第 6 节。

接口贡献显式注册为 `IModuleSettings`、`IModuleActivity`，不能使用具体类泛型。组件开关的业务由作者实现，宿主不会仅凭开关自动撤销任意页面。`Changed` 刷新活动状态，`Invalidate()` 刷新组件／设置元数据，不按帧重建内容。

## 4. 完整数据模组：Codex 用量提醒

`usage-guard` 不提供页面，独立订阅用量，达到 80% 后每个已知配额周期接受一次提醒。不读取账号目录，不增加网络请求。需要自定义阈值可按上一节添加设置。

`BrimDeck.UsageGuard.csproj`：

<!-- module-example: usage-guard project -->
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>BrimDeck.UsageGuard</AssemblyName>
    <ModuleId>usage-guard</ModuleId>
  </PropertyGroup>
  <Import Project="$(BrimDeckSdk)/build/ModuleSdk.props" />
  <ItemGroup><None Update="module.json" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>
  <Import Project="$(BrimDeckSdk)/build/ModulePackage.targets" />
</Project>
```

`module.json`：

<!-- module-example: usage-guard manifest -->
```json
{
  "apiVersion": 1, "apiMinor": 6,
  "id": "usage-guard", "name": "用量提醒示例", "version": "1.0.0",
  "assembly": "BrimDeck.UsageGuard.dll", "entryType": "BrimDeck.UsageGuard.UsageGuardModule",
  "capabilities": ["usage.read", "notifications.show", "storage.data"],
  "permissions": ["usage.read", "notifications.show"], "dependencies": []
}
```

`Module.cs`：

<!-- module-example: usage-guard source -->
```csharp
using BrimDeck.Modules;

namespace BrimDeck.UsageGuard;

public sealed record AlertHistory(string[] Accepted);
public sealed class UsageGuardModule : IModule
{
    private const double Threshold = 80;
    private IModuleContext _context = null!;
    private IModuleUsage _usage = null!;
    private IModuleNotifications _notices = null!;
    private readonly List<string> _accepted = [];
    public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); _context = context;
        var saved = context.Storage.ReadResult<AlertHistory>("alert-history");
        if (saved.Status is ModuleReadStatus.Corrupt or ModuleReadStatus.Unsupported)
            throw new InvalidOperationException(saved.Error ?? "Cannot read alert history.");
        _accepted.AddRange(saved.Value?.Accepted?.TakeLast(64) ?? []);
        var services = context.Platform().Services;
        _usage = services.Get<IModuleUsage>(); _notices = services.Get<IModuleNotifications>();
        context.Own(_usage.Subscribe(Evaluate));
        // Timers start after Ready; first tick reads cached data and retries failed delivery.
        context.Every(TimeSpan.FromSeconds(10), () => Evaluate(_usage.GetSnapshot()));
        return ValueTask.CompletedTask;
    }
    private void Evaluate(ModuleUsageSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in snapshot.Entries.Where(e => e.ProviderId == "codex" && e.Status == ModuleDataStatus.Available))
        foreach (var metric in entry.Metrics)
        {
            if (metric.Unlimited || metric.UsedPercent is not double used || !double.IsFinite(used) || used < Threshold
                || metric.ResetsAt is not { } reset || reset <= now
                || metric.ExpiresAt is { } expiry && expiry <= now) continue;
            string key = metric.Id + ":" + reset.ToUnixTimeSeconds();
            if (_accepted.Contains(key)) continue;
            string message = $"{metric.Label} 已使用 {used:0}%";
            if (message.Length > 160) message = message[..157] + "...";
            var result = _notices.Show(new("quota-warning", "Codex 用量提醒", message)
            { Tone = ModuleNoticeTone.Warning, ProgressPercent = used });
            if (result is not (ModuleNoticeResult.Shown or ModuleNoticeResult.Queued)) continue;
            _accepted.Add(key);
            while (_accepted.Count > 64) _accepted.RemoveAt(0);
            _context.Storage.Write("alert-history", new AlertHistory(_accepted.ToArray()));
        }
    }
}
```

构建与检查同上，程序集目录为 `BrimDeck.UsageGuard`。需要本体先配置并读取到 Codex 用量，无条目时安静等待。示例不对缺少重置时间的指标告警；`NotReady`、`RateLimited`、`Unavailable` 后继续重试，`Shown`／`Queued` 仅表示宿主接受，不保证用户已经看到。重启后最近 64 个周期去重；通知与持久化之间没有事务，通知已接受但写盘失败后跨重启可能重试。

媒体和系统也使用订阅后读 `GetSnapshot()` 的模式：声明 `media.read`／`system.read` 能力与同名权限，取得 `IModuleMedia`／`IModuleSystem`。字段、单位、过期处理和通知限额见 API 第 9 节，无须阅读本体采样器。

## 5. 生命周期与状态归属

`ActivateAsync` 建立模组级状态并注册贡献。页面工厂在页面真正呈现时创建视图。切换页面只结束视图；停用、替换模组或退出应用才结束模组。

| 内容 | 应放在哪里 | 结束时机 |
| --- | --- | --- |
| 计时会话、业务缓存、待保存草稿 | 模组实例或它拥有的服务 | 模组停用／替换／退出 |
| 按钮、编辑焦点、局部订阅、界面刷新 | 每次创建的视图 | 页面或显示实例移除 |
| 展开／收起外壳、临时通知优先级 | 宿主适配器 | 宿主根据呈现状态管理 |
| 用户偏好、运行快照 | 各自的持久化数据 | 不随页面销毁而删除 |

同一模组在不同显示器上有不同视图。不要把同一个 `FrameworkElement` 放进两个容器，也不要为每个视图重复创建业务计时会话。

使用 `Own`、`OnDispose`、`Every`、`Subscribe<T>` 管理模组资源；使用视图上下文管理页面资源。清理要可重复调用，自行订阅的外部事件必须登记解绑。后台任务观察本次上下文的取消令牌，更新 WPF 控件时返回对应调度线程。

1.3 起五秒是慢启动提示阈值，超过后继续等待，不默认判失败；后续完成照常提交。互不依赖的模组分别启动，依赖方等待目标就绪。不要在激活中等待自己的定时器或订阅完成：贡献、订阅和定时器一起暂存，就绪后才生效，这样的相互等待不会自行解除。应直接异步读取初始数据，再完成激活。

停用或代码更新会取消本代令牌，等待中的依赖方也会退出。任务应及时响应取消；即使迟到返回，旧上下文的注册、存储和事件发布也失效，旧错误不污染新实例。清理按后登记先执行，跨模组则先清理依赖方。同步入口和清理不能被取消令牌强制打断，耗时操作不得阻塞 UI。

## 6. 页面、活动与交互

普通页面按 `Order`、完整 ID 排序。标签、设置标题同时提供中文和英文。页面只操作自己的视图内容，不通过 `Owner` 向上查找并修改主窗口。

需要异步读取数据的页面使用 `ModulePage.Async`，在工厂里用 `await` 配合 `view.Lifetime`，最后创建并返回 `IModuleView`。不要阻塞等待，也不要在后台线程直接创建 WPF 控件。工厂若主动使用 `ConfigureAwait(false)`，创建控件前须回到 `view.Owner.Dispatcher`。首个布局和主题以挂载完成时为准，挂载等待不会结束模组业务。

示例 [`NotesModule.cs`](../../../mods/notes/NotesModule.cs) 的「示例说明」页面使用可取消的 150 ms 演示延迟，方便观察统一等待提示、内容交接和快速切页取消。正式模组应等待实际需要的数据，不必增加人为延迟。活动的异步视图使用 `IModuleAsyncActivity`，每个紧凑／展开视图各自创建，仍共享模组会话。

持续活动通过 `IModuleActivity` 提供两种视图，状态变化后发出 `Changed`。进度是 0–1 的比例，`Indeterminate` 表示没有确定进度；`Completion` 是完成序号，发生新完成事件时递增，不要每个刷新周期递增。外壳负责完成交接和通知覆盖后的恢复。

当前展开活动获得 320 × 76 DIP。旧 `SetViewport` 的第三个参数统一为内容缩放。展开视图推荐另实现 `IModuleLayoutView`，使用 `ModuleViewport` 中分别给出的内容缩放、设备缩放、留白与圆角；宿主不再将 `18/30` 作为缩放传入。`Owner` 是已经配置资源的局部容器，导航仅允许本模组贡献。

1.4 的异步等待期间可继续改变尺寸，最后一次规范化后的布局在挂载时传入。视图级 `Every` 等到挂载成功才启动；页面离开或加载失败时自动释放，迟到视图不能重新显示。不要跨页面或跨显示器复用控件实例，恢复编辑内容应使用模组级数据。

输入框应正常处理文本、Tab 与输入法组合输入；局部编辑优先消费 Escape，未消费时由宿主处理收起。当前 SDK 尚未完整提供焦点归还和输入租约服务，测试时必须覆盖这些路径。

## 7. 组件与设置

一个模组可贡献多个组件。组件关闭时由模组撤销相应页面或隐藏设置，偏好保留。计时器仍遵守同一时刻只有一个计时会话的业务规则。

当前 `IModuleSettings` 自行实现读取、写入、规范化和重置，宿主负责校验并绘制数字、布尔、选项、文本和颜色控件；字段支持步长、分组、说明和占位符，非法字段跳过并提供非致命诊断。字段键、默认值、存储和变更订阅尚未统一到宿主 schema 存储服务。不要将 `object` 的任意值直接写入业务状态。设置位置、顺序和独立图标通过 `IModuleSettingsNavigation` 声明，多组件共享入口自动去重，完整规则见[宿主规则](host-rules.md)。

不要把 API 密钥、令牌放进普通 JSON 或数字／布尔字段。当前 SDK 没有模组级密钥服务；需要凭据的功能应等待该服务，或明确保持为本地实验，不把其他平台的 `secret` 字段写进本平台清单。

读取本体用量、正在播放和系统监控时，使用 `IModuleUsage`、`IModuleMedia`、`IModuleSystem`，不自己解析账号目录或重复创建采样器。清单同时列出相应能力与权限；界面在模组详情展示权限。缺失数据用破折号或等待状态，不用 0 冒充；过期用量不触发阈值提醒。动画提醒使用 `IModuleNotifications`，不抢占宿主窗口。

参考可运行的[用量提醒示例](../../../mods/usage-alerts/README.md)。它只依赖公开 SDK，包含 Codex 阈值、配额周期去重、限流后的重试、独立设置和三类数据展示。持久化使用 `ReadResult<T>`，明确区分缺失、有效值和损坏；损坏文件不自动覆盖。

契约 1.2 的 `ModuleApi.Catalog` 是能力描述的唯一来源。它列出 SDK 认识的能力；当前宿主的真实适配情况由 `IModuleHostInfo.GetInfo().CapabilityDetails` 提供。不要仅因静态目录存在某条能力便认定当前宿主可用。新增宿主接口时，先登记能力描述，再绑定公开契约与工厂；权限名称和文档从目录派生。类型、权限、种类不一致，或者桌面宿主漏接适配器时，启动校验和检查会失败。

## 8. 数据与性能

`context.Storage` 的键只使用小写字母、数字及连字符，首字符为字母或数字。读缺失键返回默认值，JSON 损坏以 `storage-corrupt` 抛错。优先使用 `ReadResult<T>` 区分缺失、有效值与损坏，并在业务层校验，不能把损坏文件自动覆盖成默认值。

持久化在用户操作、阶段变化和退出时进行。计时进度从单调时钟计算，显示刷新不用不断写盘。一般文件缓存与程序集加载缓存不要混用；当前 `modules-cache` 是加载器缓存，没有对模组开放通用缓存清理服务。

用户关闭模组保留设置和进度，确认卸载会删除该模组的安装文件、Storage 数据及加载缓存。宿主先停止实例与依赖方，再隔离旧目录并清理；占用文件进入重试，不影响其他可清理数据，也不删除后来重装的数据。清空缓存独立保留业务数据与运行状态。不要在清理后用遗留任务重新创建用户已经卸载的数据目录。

目录被替换或旧维护记录缺少身份时，宿主会保留不能确认归属的内容，提示刷新并在需要时重新确认卸载。操作忙时会明确拒绝；写盘失败不会当作卸载完成。总开关关闭时刷新仍可处理待清理项，不为此加载模组。

性能页统计启动耗时、宿主管理的同步回调与视图调用、五秒平均回调用时、最长回调、至少 16 ms 的慢调用和错误次数。自行创建的线程、任务或计时器不在该统计范围内。这些数字不是每个模组的独立 CPU 或内存占用。

## 9. 调试、检查与发布

开发闭环是：修改 → 构建 → 覆盖原始模组目录 → 刷新 → 重新启用 → 检查页面、设置和性能。文件内容改变后原确认失效；当前没有开发目录豁免。

重刷未变更的模组不会重新激活。更新时需要新内容摘要，加载器复制整个模组目录，私有依赖也随这一版更新；原始 DLL 不因运行中的模组被锁定。

交付前至少验证：首次启用；页面关闭重开；主题变化；多视图；暂停恢复；停用清理；连续启停；临时通知覆盖；组件关闭后的设置隐藏；依赖缺失；错误提示；退出及重启恢复。没有运行的检查应记录为待验证。

当前没有市场、签名认证、ZIP 安装器或正式发布渠道。分发包应包含源码地址、版本、实际依赖、许可证和已知问题；不要附带宿主、账户数据、调试日志或本机设置。更新模组版本时提供迁移说明。

API 文档签名由维护者生成并检查；作者直接使用开发包，无须运行源码工具。分发前对最终包运行第 2 节的静态检查，随后按平台规范「作者交付验收」实际操作；检查器不激活模组，不能代替功能与界面验收。

统一打包只收集输出根目录的 DLL、`.deps.json` 和 `module.json`。额外资源或子目录需明确放入成品并检查；没有公开资源根目录服务，固定资源可嵌入程序集，不要猜测宿主工作目录。共享 SDK、检查工具、日志和用户设置不属于分发包。

## 10. 常见问题

**列表没有模组？** 检查是否放在设置按钮打开的实际 `mods` 目录，清单是否直接位于子目录内，入口 DLL 是否存在。当前不支持解压包多套一层目录。

**有条目但启动失败？** 展开模组查看错误，检查入口完整类型名、公开无参构造函数、API 版本、依赖启用状态及 DLL 是否随包带齐。

**修改音乐或监控后小程序也坏了？** 检查是否引用了主程序、内部页面或依赖未公开的资源。普通页面实现应隔离；公共 SDK、主题资源和动效服务变更仍需要兼容性检查。

**为什么没有示例里某个 API？** 以本仓库 API 参考为准。目标规范、Folia/Folium 文档和 C# `public` 可见性不自动构成当前 BrimDeck 的稳定扩展承诺。

**编译提示找不到导入文件或 UI 类型？** `BrimDeckSdk` 指向解压根目录，包含 sdk.json/ref/build；带空格的整个参数加引号。公共 UI 的命名空间是 BrimDeck，但程序集为 BrimDeck.UI，不能通过引用主程序解决。

**字段、服务或导航报错？** API 第 12 节列出精确限制、写入值类型及错误码。先核对清单能力与权限，再核对字段定义、接口注册类型和本模组完整导航 ID，勿通过私有实现绕开限制。

**问题模组影响启动？** `BrimDeck.exe --no-modules` 可单次跳过，也可在下次恢复提示选择跳过。原开关、批准和业务数据保留；在管理页关闭问题项后正常重启。当前不提供运行期无响应看门狗。

## 11. 维护者：导出与验证开发包

只有维护者需要宿主仓库与 PowerShell 7+。仓库根目录执行以下命令，输出目录必须尚不存在，失败会保留 staging 便于诊断，不删除旧目录：

```powershell
./build/ExportModuleSdk.ps1 -OutputDirectory E:/BrimDeck-sdk -ArtifactsDirectory E:/BrimDeck-sdk-build
./build/TestModuleAuthoring.ps1 -SdkDirectory E:/BrimDeck-sdk -WorkDirectory E:/BrimDeck-authoring-check
```

导出构建 reference assembly 与静态工具，核对 API 签名及能力表，提取本文的三个完整模板，附带这三篇主文档和可选深入材料；不包含宿主源码。开发包内 mods 的可选参考工程说明原仓库实现，不作为独立模板构建入口。

验证将模板复制到仓库外的全新目录，仅使用 SDK 引用构建、统一打包、严格静态检查，核对无宿主 ProjectReference、无重复 SDK DLL，以及开发包文档链接。维护者还可传 `-RuntimeTestRunner '<BrimDeck.ModuleTests.exe>'` 对独立产物执行专门的宿主集成检查。维护者源码签名校验命令为 `dotnet run --project tools/BrimDeck.ModuleDocs/BrimDeck.ModuleDocs.csproj -- --check .`。

导出工具与验证工具不会调整宿主偏好。开发包是当前实验契约的一次快照，不能仅因模板编译成功就宣布历史二进制兼容、签名审核或整个平台规划已完成。
