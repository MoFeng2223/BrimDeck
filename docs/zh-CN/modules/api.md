# BrimDeck 模组 API 参考

本页记录当前代码可用的实验 API，当前契约版本为 `1.6`。本轮增加独立设置导航、真实呈现可见性、可见时定时器、文本／颜色字段、字段元数据与非致命诊断。基础数据服务要求 minor 1，能力描述要求 minor 2，异步挂载要求 minor 4，平台入口要求 minor 5；使用本轮新 API 时声明 minor 6。旧清单省略 minor 按 0 兼容，既有构造函数与枚举数值保持不变。

与[开发指南](contributing.md)、[平台规范](../../../mods/README.md)组成三篇主文档。本文包含独立开发必须了解的规则，第 12 节提供完整速查；其他主题文章为可选补充。公开类型的来源是 `BrimDeck.Modules.Abstractions` 与 `BrimDeck.UI`；界面类型当前使用 `BrimDeck` 命名空间。

## 1. 使用范围

| 程序集 | 作者用途 | 稳定性 |
| --- | --- | --- |
| `BrimDeck.Modules.Abstractions` | 入口、激活上下文、清单、存储与事件类型 | 实验 |
| `BrimDeck.UI` | WPF 视图、页面、活动、组件、设置及公共 UI 入口 | 实验；部分历史类型仍属于内部实现 |
| `BrimDeck.Modules` | 宿主加载器、注册表、统计与管理 | 宿主基础设施，不通过模组上下文提供 |
| `BrimDeck.Core` | 内置业务模型 | 不是通用模组 SDK 的整体兼容承诺 |
| `BrimDeck` | 桌面应用 | 模组禁止直接引用 |

下面的签名区从当前程序集提取，仅包含指定的契约类型；不会把整个 WPF 继承层或所有 `public` 基础设施都当作 SDK。

### 能力目录

<!-- BEGIN GENERATED MODULE CAPABILITIES -->

此表由 `ModuleApi.Catalog` 生成，与清单校验、宿主绑定校验及权限名称共用同一来源。列出 SDK 已知能力；本机实际可用项以 `IModuleHostInfo.GetInfo()` 为准。

| ID | 类型 | 名称 / English | 引入 minor | 必要权限 | 环境 | 资源归属 | 公开契约 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `host.info` | Intrinsic | 宿主信息 / Host information | 1 | 无 | `any` | `activation` | `BrimDeck.Modules.Abstractions`: `BrimDeck.Modules.IModuleHostInfo` |
| `usage.read` | Service | 读取用量 / Read usage | 1 | `usage.read` | `windows-desktop` | `activation` | `BrimDeck.Modules.Abstractions`: `BrimDeck.Modules.IModuleUsage` |
| `media.read` | Service | 读取正在播放 / Read now playing | 1 | `media.read` | `windows-desktop` | `activation` | `BrimDeck.Modules.Abstractions`: `BrimDeck.Modules.IModuleMedia` |
| `system.read` | Service | 读取系统监控 / Read system metrics | 1 | `system.read` | `windows-desktop` | `activation` | `BrimDeck.Modules.Abstractions`: `BrimDeck.Modules.IModuleSystem` |
| `notifications.show` | Service | 显示岛内提醒 / Show island notices | 1 | `notifications.show` | `windows-desktop` | `activation` | `BrimDeck.Modules.Abstractions`: `BrimDeck.Modules.IModuleNotifications` |
| `ui.presentation` | Service | 刷新模组界面 / Refresh module presentation | 5 | 无 | `windows-desktop` | `activation` | `BrimDeck.Modules.Abstractions`: `BrimDeck.Modules.IModulePresentation` |
| `ui.pages` | Contribution | 小程序页面 / Mini-app pages | 0 | 无 | `windows-desktop` | `activation` | `BrimDeck.UI`: `BrimDeck.ModulePage` |
| `ui.activities` | Contribution | 持续活动 / Persistent activities | 0 | 无 | `windows-desktop` | `activation` | `BrimDeck.UI`: `BrimDeck.IModuleActivity` |
| `ui.widgets` | Contribution | 组件 / Widgets | 0 | 无 | `windows-desktop` | `activation` | `BrimDeck.UI`: `BrimDeck.ModuleWidget` |
| `ui.settings` | Contribution | 设置 / Settings | 0 | 无 | `windows-desktop` | `activation` | `BrimDeck.UI`: `BrimDeck.IModuleSettings` |
| `storage.data` | Intrinsic | 模组数据 / Module data | 0 | 无 | `any` | `activation` | `BrimDeck.Modules.Abstractions`: `BrimDeck.Modules.IModuleStorage` |

当前目录中的能力均为实验接口。`activation` 表示贡献或服务归属本次模组激活，具体视图另由视图上下文释放。
<!-- END GENERATED MODULE CAPABILITIES -->

<!-- BEGIN GENERATED MODULE API -->

此区块由 `tools/BrimDeck.ModuleDocs` 从指定契约类型生成；不要手工编辑。基础类型为模组作者使用，末尾的状态与统计 DTO 为宿主诊断使用。

### `IModuleSettingsNavigation`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

继承：`IModuleSettings`。

| 成员签名 |
| --- |
| `string? IconPath { get; }` |
| `int Order { get; }` |
| `ModuleSettingsPlacement Placement { get; }` |

### `ModuleSettingsPlacement`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `Automatic = 0` |
| `Modules = 1` |
| `MiniApps = 2` |

### `ModuleDiagnostic`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleDiagnostic(string Code, string Source, string Message, ModuleDiagnosticSeverity Severity = Warning)` |
| `string Code { get; init; }` |
| `string Message { get; init; }` |
| `ModuleDiagnosticSeverity Severity { get; init; }` |
| `string Source { get; init; }` |

### `ModuleDiagnosticSeverity`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `Information = 0` |
| `Warning = 1` |

### `IModulePlatform`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

继承：`IModuleContext`。

| 成员签名 |
| --- |
| `IModuleEvents Events { get; }` |
| `IModuleRegistries Registries { get; }` |
| `IModuleServices Services { get; }` |

### `IModuleRegistries`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `IModuleRegistration Register<T>(string id, T contribution) where T : class` |

### `IModuleRegistration`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

继承：`IDisposable`。

| 成员签名 |
| --- |
| `string Id { get; }` |

### `IModuleEvents`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `IDisposable On<T>(Action<T> listener, ModuleEventPriority priority = Normal)` |
| `void Publish<T>(T message)` |

### `IModuleServices`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `T Get<T>() where T : class` |

### `ModuleContextExtensions`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `static IModulePlatform Platform(IModuleContext context)` |

### `ModuleEventPriority`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `Highest = 0` |
| `High = 1` |
| `Normal = 2` |
| `Low = 3` |
| `Lowest = 4` |

### `ModuleOpeningEvent`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `bool Cancelled { get; }` |
| `string ContributionId { get; }` |
| `void Cancel()` |

### `ModulePageOpening`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModulePageOpening(string contributionId)` |

### `ModuleActivityOpening`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleActivityOpening(string contributionId)` |

### `IModulePresentation`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `void Invalidate()` |

### `ModuleSettingOption`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `ModuleSettingOption(int Value, string Label, string LabelEn)` |
| `string Label { get; init; }` |
| `string LabelEn { get; init; }` |
| `int Value { get; init; }` |

### `ModuleTheme`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `static bool Dark { get; }` |
| `static int Generation { get; }` |

### `ModuleLocalization`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `static string Language { get; }` |
| `static string T(string chinese, string english)` |

### `ModuleMotion`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `const int Quick = 160` |
| `static bool Enabled { get; }` |
| `static void Animate(Animatable target, DependencyProperty property, double value, bool animate, int milliseconds)` |

### `Handover`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `None = 0` |
| `Dissolve = 1` |
| `Morph = 2` |
| `Forward = 3` |
| `Back = 4` |

### `PageHandover`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `PageHandover(Grid layer)` |
| `static void Appear(UIElement element)` |
| `static void Arrive(UIElement element, Handover motion)` |
| `static void Brighten(UIElement element)` |
| `static Image? Capture(FrameworkElement page)` |
| `void Drop()` |
| `static VisualBrush Exactly(Visual element, double width, double height)` |
| `static void Glide(UIElement element, DependencyProperty property, double to, bool animate, int milliseconds = 315)` |
| `void Hold(Panel content, UIElement? keep = null)` |
| `static Thickness Place(FrameworkElement content)` |
| `void Release(FrameworkElement content, Handover motion)` |
| `void Send(UIElement? picture, Thickness place, FrameworkElement content, Handover motion)` |

### `RollingDigitsView`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `RollingDigitsView(double size = 30)` |
| `double FontSize { get; set; }` |
| `Brush Foreground { get; set; }` |
| `int MinimumTimeDigits { get; set; }` |
| `string Text { get; set; }` |

### `RollingStepper`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `RollingStepper(FrameworkElement owner, string label, int value, int minimum, int maximum)` |
| `EditDraft? Draft { get; }` |
| `bool IsAdjusting { get; }` |
| `int Maximum { get; }` |
| `int Minimum { get; }` |
| `int Value { get; }` |
| `event Action<int>? ValueChanged` |
| `void BeginEdit()` |
| `bool CancelAdjustment()` |
| `bool CommitEdit()` |
| `void Fit(double width, double height)` |
| `void RestoreDraft(EditDraft draft)` |
| `void SetValue(int value, bool notify = true)` |
| `EditDraft? TakeDraft()` |

### `EditDraft`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `EditDraft(string Text, int Start, int Length, bool Focused)` |
| `bool Focused { get; init; }` |
| `int Length { get; init; }` |
| `int Start { get; init; }` |
| `string Text { get; init; }` |

### `IModule`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken)` |

### `IModuleContext`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `CancellationToken Lifetime { get; }` |
| `string ModuleId { get; }` |
| `IModuleStorage Storage { get; }` |
| `IDisposable Every(TimeSpan interval, Action callback)` |
| `T GetService<T>() where T : class` |
| `IDisposable OnDispose(Action cleanup)` |
| `IDisposable Own(IDisposable resource)` |
| `void Publish<T>(T message)` |
| `IDisposable Register<T>(string id, T contribution) where T : class` |
| `void Report(string operation, Exception error)` |
| `IDisposable Subscribe<T>(Action<T> listener)` |

### `IModuleStorage`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `T? Read<T>(string key)` |
| `void Write<T>(string key, T value)` |

### `IModuleScheduler`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `IDisposable Every(TimeSpan interval, Action callback)` |

### `ModuleManifest`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleManifest()` |
| `int ApiMinor { get; init; }` |
| `int ApiVersion { get; init; }` |
| `string? Assembly { get; init; }` |
| `string Author { get; init; }` |
| `string[] Capabilities { get; init; }` |
| `ModuleDependency[] Dependencies { get; init; }` |
| `string Description { get; init; }` |
| `string DescriptionEn { get; init; }` |
| `string? EntryType { get; init; }` |
| `ModuleHostRequirement? Host { get; init; }` |
| `string Id { get; init; }` |
| `string Name { get; init; }` |
| `string[] OptionalCapabilities { get; init; }` |
| `string[] Permissions { get; init; }` |
| `ModuleRequirements? Requires { get; init; }` |
| `string Version { get; init; }` |

### `ModuleDependency`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleDependency(string Id, string MinimumVersion = "1.0.0")` |
| `string Id { get; init; }` |
| `string? MaximumVersionExclusive { get; init; }` |
| `string MinimumVersion { get; init; }` |

### `ModuleHostRequirement`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleHostRequirement(string MinimumVersion, string? MaximumVersionExclusive = null)` |
| `string? MaximumVersionExclusive { get; init; }` |
| `string MinimumVersion { get; init; }` |

### `ModuleAction`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleAction(Action action)` |
| `void Dispose()` |

### `ModuleSuspending`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleSuspending()` |

### `ModulePreferencesChanged`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModulePreferencesChanged()` |

### `ModuleUiChanged`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleUiChanged()` |

### `ModulePage`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `ModulePage(string Title, string TitleEn, Func<ModuleViewContext, IModuleView> CreateView, int Order = 500)` |
| `Func<ModuleViewContext, IModuleView> CreateView { get; init; }` |
| `Func<ModuleViewContext, ValueTask<IModuleView>>? CreateViewAsync { get; init; }` |
| `int Order { get; init; }` |
| `string Title { get; init; }` |
| `string TitleEn { get; init; }` |
| `static ModulePage Async(string title, string titleEn, Func<ModuleViewContext, ValueTask<IModuleView>> create, int order = 500)` |

### `IModuleView`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

继承：`IDisposable`。

| 成员签名 |
| --- |
| `FrameworkElement Element { get; }` |
| `bool IsAdjusting { get; }` |
| `bool CancelAdjustment()` |
| `void Refresh()` |
| `void SetViewport(double width, double height, double scale = 1)` |

### `ModuleViewContext`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `ModuleViewContext(FrameworkElement owner, Action<string> openPage, Action<string> openSettings, Action<string> showActivity)` |
| `bool IsVisible { get; }` |
| `CancellationToken Lifetime { get; }` |
| `Action<string> OpenPage { get; }` |
| `Action<string> OpenSettings { get; }` |
| `FrameworkElement Owner { get; }` |
| `Action<string> ShowActivity { get; }` |
| `void Dispose()` |
| `IDisposable Every(TimeSpan interval, Action callback)` |
| `IDisposable EveryVisible(TimeSpan interval, Action callback)` |
| `IDisposable OnDispose(Action cleanup)` |
| `IDisposable OnVisibilityChanged(Action<bool> listener)` |
| `IDisposable Own(IDisposable resource)` |

### `IModuleActivity`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `ActivityState State { get; }` |
| `event Action? Changed` |
| `IModuleView CreateView(ModuleViewContext context, ActivityViewKind kind)` |

### `IModuleAsyncActivity`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

继承：`IModuleActivity`。

| 成员签名 |
| --- |
| `ValueTask<IModuleView> CreateViewAsync(ModuleViewContext context, ActivityViewKind kind)` |

### `ActivityState`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `ActivityState(bool Active, double Progress, bool Indeterminate, string Accent, int Completion = 0)` |
| `string Accent { get; init; }` |
| `bool Active { get; init; }` |
| `int Completion { get; init; }` |
| `bool Indeterminate { get; init; }` |
| `double Progress { get; init; }` |

### `ActivityViewKind`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `Compact = 0` |
| `Expanded = 1` |

### `ModuleWidget`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `ModuleWidget(string Name, string NameEn, string SettingsId, Func<bool> Enabled, Action<bool> SetEnabled, int Order = 500)` |
| `Func<bool> Enabled { get; init; }` |
| `string? IconPath { get; init; }` |
| `Action<int>? MoveTo { get; init; }` |
| `string Name { get; init; }` |
| `string NameEn { get; init; }` |
| `int Order { get; init; }` |
| `Func<int>? ReadOrder { get; init; }` |
| `Action<bool> SetEnabled { get; init; }` |
| `string SettingsId { get; init; }` |

### `IModuleSettings`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `IReadOnlyList<ModuleSetting> Fields { get; }` |
| `string Name { get; }` |
| `string NameEn { get; }` |
| `bool Visible { get; }` |
| `object Read(string key)` |
| `void Reset()` |
| `void Write(string key, object value)` |

### `ModuleSetting`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `ModuleSetting(string Key, string Label, string LabelEn, ModuleSettingKind Kind, double Minimum = 0, double Maximum = 1, string Unit = "", string UnitEn = "")` |
| `string Description { get; init; }` |
| `string DescriptionEn { get; init; }` |
| `string Group { get; init; }` |
| `string GroupEn { get; init; }` |
| `string Key { get; init; }` |
| `ModuleSettingKind Kind { get; init; }` |
| `string Label { get; init; }` |
| `string LabelEn { get; init; }` |
| `double Maximum { get; init; }` |
| `int MaximumLength { get; init; }` |
| `double Minimum { get; init; }` |
| `IReadOnlyList<ModuleSettingOption> Options { get; init; }` |
| `string Placeholder { get; init; }` |
| `string PlaceholderEn { get; init; }` |
| `double Step { get; init; }` |
| `string Unit { get; init; }` |
| `string UnitEn { get; init; }` |

### `ModuleSettingKind`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `Number = 0` |
| `Toggle = 1` |
| `Choice = 2` |
| `Text = 3` |
| `Color = 4` |

### `ModuleUi`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `static string Accent { get; }` |
| `static bool AnimationsEnabled { get; }` |
| `static bool Dark { get; }` |
| `static string FillSelected { get; }` |
| `static string OnAccent { get; }` |
| `static FontFamily PanelFont { get; }` |
| `static string Primary { get; }` |
| `static string Secondary { get; }` |
| `static string Tertiary { get; }` |
| `static void Appear(UIElement element)` |
| `static Brush Brush(string color)` |
| `static TextBlock Centered(string text, double size, string color)` |
| `static TextBlock Text(string text, double size = 13)` |

### `ModuleControlButton`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `ModuleControlButton()` |
| `bool AnimationsEnabled { get; set; }` |

### `ModuleInfo`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleInfo(string Id, string Name, string Version, ModuleStatus Status, string? Error, bool BuiltIn, string Revision)` |
| `bool BuiltIn { get; init; }` |
| `IReadOnlyList<ModuleDiagnostic> Diagnostics { get; init; }` |
| `string? Error { get; init; }` |
| `Guid Generation { get; init; }` |
| `string? HostRange { get; init; }` |
| `string Id { get; init; }` |
| `string Name { get; init; }` |
| `string Revision { get; init; }` |
| `ModuleSignature? Signature { get; init; }` |
| `bool Slow { get; init; }` |
| `ModuleStatus Status { get; init; }` |
| `bool UsesInternals { get; init; }` |
| `string Version { get; init; }` |

### `ModuleStatus`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `Disabled = 0` |
| `Starting = 1` |
| `Ready = 2` |
| `Failed = 3` |
| `DependencyFailed = 4` |
| `Waiting = 5` |
| `HostMismatch = 6` |

### `ModuleFault`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleFault(string ModuleId, string Operation, string Message, DateTimeOffset At)` |
| `DateTimeOffset At { get; init; }` |
| `Guid Generation { get; init; }` |
| `string Message { get; init; }` |
| `string ModuleId { get; init; }` |
| `string Operation { get; init; }` |

### `Contribution<T>`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `Contribution<T>(string Id, string ModuleId, T Value)` |
| `string Id { get; init; }` |
| `string ModuleId { get; init; }` |
| `T Value { get; init; }` |

### `ModulePerformance`

程序集：`BrimDeck.Modules`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModulePerformance(string ModuleId, double? StartupMilliseconds, long Activations, double MillisecondsPerSecond, double TotalMilliseconds, double LongestMilliseconds, long Calls, long SlowCalls, long Errors, string? LongestOperation)` |
| `long Activations { get; init; }` |
| `long Calls { get; init; }` |
| `long Errors { get; init; }` |
| `double LongestMilliseconds { get; init; }` |
| `string? LongestOperation { get; init; }` |
| `double MillisecondsPerSecond { get; init; }` |
| `string ModuleId { get; init; }` |
| `long SlowCalls { get; init; }` |
| `double? StartupMilliseconds { get; init; }` |
| `double TotalMilliseconds { get; init; }` |

### `ModuleApi`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `const int Major = 1` |
| `const int Minor = 6` |
| `const string HostInfo = "host.info"` |
| `const string UsageRead = "usage.read"` |
| `const string MediaRead = "media.read"` |
| `const string SystemRead = "system.read"` |
| `const string NotificationsShow = "notifications.show"` |
| `const string UiPresentation = "ui.presentation"` |
| `const string UiPages = "ui.pages"` |
| `const string UiActivities = "ui.activities"` |
| `const string UiWidgets = "ui.widgets"` |
| `const string UiSettings = "ui.settings"` |
| `const string StorageData = "storage.data"` |
| `static IReadOnlyList<string> Capabilities { get; }` |
| `static IReadOnlyList<ModuleCapability> Catalog { get; }` |
| `static IReadOnlyList<string> Permissions { get; }` |
| `static ModuleCapability? Describe(string id)` |

### `ModuleApiException`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleApiException(string code, string message)` |
| `string Code { get; }` |

### `ModuleRequirements`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleRequirements()` |
| `string[] Capabilities { get; init; }` |
| `int ContractMinor { get; init; }` |

### `IModuleHostEvent`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |

### `ModuleDeprecations`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `static IReadOnlyList<ModuleDeprecation> Table { get; }` |
| `static ValueTuple<ModuleManifest, IReadOnlyList<ModuleDiagnostic>> Resolve(ModuleManifest manifest, IReadOnlyList<ModuleDeprecation>? table = null)` |

### `ModuleDeprecation`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleDeprecation(ModuleDeprecationKind Kind, string Old, string New, string RemovedIn, string? Note = null)` |
| `ModuleDeprecationKind Kind { get; init; }` |
| `string New { get; init; }` |
| `string? Note { get; init; }` |
| `string Old { get; init; }` |
| `string RemovedIn { get; init; }` |

### `ModuleDeprecationKind`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `Capability = 0` |
| `Permission = 1` |

### `ModuleSignature`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleSignature(ModuleSignatureStatus Status, ModuleSignatureChannel Channel, string? KeyId, string? SignedAt, string? Digest, string? Reason)` |
| `ModuleSignatureChannel Channel { get; init; }` |
| `string? Digest { get; init; }` |
| `string? KeyId { get; init; }` |
| `string? Reason { get; init; }` |
| `string? SignedAt { get; init; }` |
| `ModuleSignatureStatus Status { get; init; }` |

### `ModuleSignatureStatus`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `Unsigned = 0` |
| `Verified = 1` |
| `Beta = 2` |
| `Invalid = 3` |

### `ModuleSignatureChannel`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `Release = 0` |
| `Beta = 1` |

### `ModuleSigningKey`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleSigningKey(string Id, ModuleSignatureChannel Channel, string PublicKey, bool Revoked = false)` |
| `ModuleSignatureChannel Channel { get; init; }` |
| `string Id { get; init; }` |
| `string PublicKey { get; init; }` |
| `bool Revoked { get; init; }` |

### `ModuleRevocation`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleRevocation(string Id, string Version)` |
| `string Id { get; init; }` |
| `string Version { get; init; }` |

### `ModuleViewport`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `ModuleViewport(double Width, double Height)` |
| `double ContentScale { get; init; }` |
| `double CornerRadius { get; init; }` |
| `double DeviceScale { get; init; }` |
| `double Height { get; init; }` |
| `double HorizontalInset { get; init; }` |
| `double Width { get; init; }` |
| `ModuleViewport Normalize()` |

### `IModuleLayoutView`

程序集：`BrimDeck.UI`；命名空间：`BrimDeck`。

| 成员签名 |
| --- |
| `void SetViewport(ModuleViewport viewport)` |

### `IModuleStructuredStorage`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleReadResult<T> ReadResult<T>(string key)` |

### `ModuleReadResult<T>`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleReadResult<T>(ModuleReadStatus Status, T Value, string? Error = null)` |
| `string? Error { get; init; }` |
| `ModuleReadStatus Status { get; init; }` |
| `T Value { get; init; }` |

### `ModuleReadStatus`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `Missing = 0` |
| `Value = 1` |
| `Corrupt = 2` |
| `Unsupported = 3` |

### `ModuleStorageExtensions`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `static ModuleReadResult<T> ReadResult<T>(IModuleStorage storage, string key)` |

### `IModuleHostInfo`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleHostInfo GetInfo()` |

### `ModuleHostInfo`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleHostInfo(string ApplicationVersion, int ContractMajor, int ContractMinor, IReadOnlyList<string> Capabilities)` |
| `string ApplicationVersion { get; init; }` |
| `IReadOnlyList<string> Capabilities { get; init; }` |
| `IReadOnlyList<ModuleCapability> CapabilityDetails { get; init; }` |
| `int ContractMajor { get; init; }` |
| `int ContractMinor { get; init; }` |

### `IModuleSnapshotReader<T>`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `T GetSnapshot()` |
| `IDisposable Subscribe(Action<T> listener)` |

### `ModuleDataStatus`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `Unavailable = 0` |
| `Available = 1` |
| `Stale = 2` |
| `Disabled = 3` |

### `IModuleUsage`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

继承：`IModuleSnapshotReader<ModuleUsageSnapshot>`。

| 成员签名 |
| --- |

### `ModuleUsageSnapshot`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleUsageSnapshot(long Revision, DateTimeOffset ObservedAt, IReadOnlyList<ModuleUsageEntry> Entries)` |
| `IReadOnlyList<ModuleUsageEntry> Entries { get; init; }` |
| `DateTimeOffset ObservedAt { get; init; }` |
| `long Revision { get; init; }` |

### `ModuleUsageEntry`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleUsageEntry(string ProviderId, string Name, ModuleDataStatus Status, DateTimeOffset? UpdatedAt, IReadOnlyList<ModuleUsageMetric> Metrics, bool TokenUsageAvailable, long InputTokens, long OutputTokens, long TotalTokens)` |
| `long CacheReadTokens { get; init; }` |
| `long CacheWriteTokens { get; init; }` |
| `long InputTokens { get; init; }` |
| `IReadOnlyList<ModuleUsageMetric> Metrics { get; init; }` |
| `string Name { get; init; }` |
| `long OutputTokens { get; init; }` |
| `string ProviderId { get; init; }` |
| `ModuleDataStatus Status { get; init; }` |
| `bool TokenUsageAvailable { get; init; }` |
| `bool TokenUsageComplete { get; init; }` |
| `long TotalTokens { get; init; }` |
| `DateTimeOffset? UpdatedAt { get; init; }` |

### `ModuleUsageMetric`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleUsageMetric(string Id, string Kind, string Label, double? UsedPercent, Decimal? Amount, Decimal? Used, Decimal? Total, string Unit, string Currency, bool Unlimited, DateTimeOffset? ResetsAt, int? WindowMinutes)` |
| `Decimal? Amount { get; init; }` |
| `bool AtLeast { get; init; }` |
| `string Currency { get; init; }` |
| `DateTimeOffset? ExpiresAt { get; init; }` |
| `string Id { get; init; }` |
| `string Kind { get; init; }` |
| `string Label { get; init; }` |
| `DateTimeOffset? ResetsAt { get; init; }` |
| `Decimal? Total { get; init; }` |
| `string Unit { get; init; }` |
| `bool Unlimited { get; init; }` |
| `Decimal? Used { get; init; }` |
| `double? UsedPercent { get; init; }` |
| `int? WindowMinutes { get; init; }` |

### `IModuleMedia`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

继承：`IModuleSnapshotReader<ModuleMediaSnapshot>`。

| 成员签名 |
| --- |

### `ModuleMediaSnapshot`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleMediaSnapshot(long Revision, DateTimeOffset ObservedAt, ModuleDataStatus Status, string PlaybackState, string Source, string Title, string Artist, string Album, double? PositionSeconds, double? DurationSeconds)` |
| `string Album { get; init; }` |
| `string Artist { get; init; }` |
| `double? DurationSeconds { get; init; }` |
| `DateTimeOffset ObservedAt { get; init; }` |
| `double PlaybackRate { get; init; }` |
| `string PlaybackState { get; init; }` |
| `double? PositionSeconds { get; init; }` |
| `long Revision { get; init; }` |
| `string Source { get; init; }` |
| `ModuleDataStatus Status { get; init; }` |
| `string Title { get; init; }` |

### `IModuleSystem`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

继承：`IModuleSnapshotReader<ModuleSystemSnapshot>`。

| 成员签名 |
| --- |

### `ModuleSystemSnapshot`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleSystemSnapshot(long Revision, DateTimeOffset ObservedAt, ModuleDataStatus Status, double? CpuUsedPercent, double? MemoryUsedPercent, UInt64? MemoryUsedBytes, UInt64? MemoryTotalBytes, double? GpuUsedPercent, double? DownloadBytesPerSecond, double? UploadBytesPerSecond)` |
| `double? CpuUsedPercent { get; init; }` |
| `double? DownloadBytesPerSecond { get; init; }` |
| `double? GpuUsedPercent { get; init; }` |
| `UInt64? MemoryTotalBytes { get; init; }` |
| `UInt64? MemoryUsedBytes { get; init; }` |
| `double? MemoryUsedPercent { get; init; }` |
| `DateTimeOffset ObservedAt { get; init; }` |
| `long Revision { get; init; }` |
| `ModuleDataStatus Status { get; init; }` |
| `double? UploadBytesPerSecond { get; init; }` |

### `IModuleNotifications`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleNoticeResult Show(ModuleNotice notice)` |

### `ModuleNotice`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleNotice(string Id, string Title, string Message)` |
| `int DurationMilliseconds { get; init; }` |
| `string Id { get; init; }` |
| `string Message { get; init; }` |
| `double? ProgressPercent { get; init; }` |
| `string Title { get; init; }` |
| `ModuleNoticeTone Tone { get; init; }` |

### `ModuleNoticeResult`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `Shown = 0` |
| `Queued = 1` |
| `RateLimited = 2` |
| `Unavailable = 3` |
| `NotReady = 4` |

### `ModuleNoticeTone`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `Info = 0` |
| `Success = 1` |
| `Warning = 2` |

### `ModuleCapability`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `ModuleCapability(string Id, ModuleCapabilityKind Kind, string Name, string NameEn, int IntroducedMinor, string ContractAssembly, string ContractType, string? Permission)` |
| `string ContractAssembly { get; init; }` |
| `string ContractType { get; init; }` |
| `string Environment { get; init; }` |
| `bool Experimental { get; init; }` |
| `string Id { get; init; }` |
| `int IntroducedMinor { get; init; }` |
| `ModuleCapabilityKind Kind { get; init; }` |
| `string Name { get; init; }` |
| `string NameEn { get; init; }` |
| `string? Permission { get; init; }` |
| `string ResourceLifetime { get; init; }` |

### `ModuleCapabilityKind`

程序集：`BrimDeck.Modules.Abstractions`；命名空间：`BrimDeck.Modules`。

| 成员签名 |
| --- |
| `Intrinsic = 0` |
| `Service = 1` |
| `Contribution = 2` |

<!-- END GENERATED MODULE API -->

## 2. 激活上下文语义

`IModule.ActivateAsync` 在一次激活中调用一次。入口需要公开无参构造函数。正常返回后提交暂存贡献；抛错或异步失败回滚并记录故障。1.3 起默认五秒是慢启动提示阈值，超过后保持 Starting 并继续等待，之后正常完成仍可提交；不因慢而撤销用户启用意图。宿主测试或专用运行环境可以显式设置有限 `StartTimeout`，这才是取消并失败的硬上限。

互不依赖的模组独立启动。依赖方显示 `Waiting`，等待其引用的确切依赖激活代就绪后才调用入口；依赖失败、停用、循环或版本不符时不调用依赖方工厂。后续刷新复用未变化的待启动实例，不能反复重置其启动时间。配置更新不等待无关模组的异步初始化结束；`ReconcileAsync` 返回的任务仍表示该次配置所涉及的启动等待完成。

应用先显示原生主窗口，再在后台读取外部模组元数据并开始配置。模组系统关闭时不发起目录发现。入口和清理函数的同步部分仍在宿主线程执行，作者不得阻塞或执行死循环；上述调度和取消不能强制终止同步代码。

`IModuleContext` 属于激活代，不仅属于模组 ID。不得缓存到新一代模组继续使用。

| 成员组 | 当前规则 |
| --- | --- |
| `ModuleId` / `Lifetime` | 宿主分配的身份与本次激活的取消令牌 |
| `Register<T>` | 局部 ID 加上模组命名空间；同类型重复 ID 拒绝；激活成功后发布；返回句柄可提前撤销 |
| `Own` / `OnDispose` | 后登记先清理，重复释放幂等，单个错误不阻断后续清理；结束后才登记的资源立即释放 |
| `Every` | 间隔 16 ms 至一天，越界抛错；激活提交后才启动，桌面宿主在 UI 调度线程执行 |
| `Subscribe<T>` | 激活提交后生效，每个订阅独立归属和清理；异常被记录，不阻断下一订阅者 |
| `Publish<T>` | Ready 后同步分发，没有重放；拒绝模组发布 `IModuleHostEvent`，包括以 object 包装的消息 |
| `GetService<T>` | 按公开接口、清单能力与权限绑定到本次激活代；停用后保留的服务也失效；不提供具体宿主类型 |
| `Report` | 记录操作名、异常信息、模组 ID 与时间；不保证将任意异常栈完整暴露给界面 |

宿主仅接受下表中的贡献类型。未知 CLR 类型会以 `unknown-contribution` 拒绝。minor 1 模组还须声明相应 `ui.pages`、`ui.activities`、`ui.widgets`、`ui.settings` 能力；minor 0 保留旧贡献声明兼容。

## 3. 贡献与注册句柄

| 注册类型 | 桌面行为 | 顺序／入口 |
| --- | --- | --- |
| `ModulePage` | 在小程序工作区提供页面，多页面使用统一选择器 | `Order` 默认 500，同值按完整 ID |
| `IModuleActivity` | 提供岛的持续活动视图 | 优先最近显式选中的活跃 ID，否则首个活跃项；同时呈现一个 |
| `ModuleWidget` | 在模组设置中提供组件开关 | `Order` 默认 500；状态与开关副作用由模组负责 |
| `IModuleSettings` | 生成独立设置入口，`Visible` 控制是否展示 | 由宿主分配设置页，不公开整数页号 |

同一个模组、同一个局部 ID 可以用于不同贡献类型。注册结果是 `IDisposable`，没有公开 `Invalidate`、`Update` 或 `Replace` 方法。旧句柄只能撤销自己那次注册，不能撤销重启后的同 ID 条目。

`ModulePage.CreateView` 和 `IModuleActivity.CreateView` 保留同步工厂兼容。1.4 的页面使用 `ModulePage.Async(title, titleEn, factory, order)`，异步工厂返回 `ValueTask<IModuleView>`；`CreateViewAsync` 优先于同步工厂。活动可实现 `IModuleAsyncActivity.CreateViewAsync(context, kind)`，仍按 `IModuleActivity` 注册，默认接口实现免去重复同步工厂。普通注册句柄的更新修订号与局部失效通知尚未提供。

异步挂载先展示宿主的静态加载提示，完成后使用公共内容交接动效，不改变外壳尺寸或业务会话。工厂从 UI 线程开始，耗时工作应异步执行；WPF 根元素须在 `context.Owner.Dispatcher` 创建。视图上下文在工厂调用前绑定确切模组激活代；切页、停用或重载立即取消其 `Lifetime`。返回前应检查令牌；仍迟到返回的视图在宿主调度器运行期间立即清理，不能重新挂回或影响新视图。取消后才抛出的异常不计入新视图错误；有效挂载失败显示局部错误提示，模组与其他页面继续运行。

同一 `IModuleView` 实例不能用于多个挂载，根元素也不能已经属于其他容器。宿主拒绝重复归属，不会为第二个无效请求销毁第一个正常视图。异步工厂仍须返回各显示实例自己的控件。缓存应保存业务数据，不缓存已经释放的视图对象。

## 4. 视图与布局

`IModuleView.Element` 必须属于当前视图，不能跨两个显示实例复用。宿主会调用布局、刷新与释放；这些调用中的普通异常有故障边界。应用调度器不是插件可接管的后台执行器。

`SetViewport` 的宽高单位为 DIP，第三个参数统一表示内容缩放，不能用作留白。展开视图实现 `IModuleLayoutView` 后会收到 `ModuleViewport`，分别包含 `DeviceScale`、`ContentScale`、`HorizontalInset`、`CornerRadius`。展开活动目前可用空间为 320 × 76 DIP；旧接口回退获得内容缩放 1。

1.4 起等待中的视图缓存最新布局，完成后只使用最后一次值，并刷新当前主题／状态。`ModuleViewport.Normalize()` 把非有限宽高恢复为 1 DIP，宽高夹取到 1–32768，设备／内容缩放分别夹取到 0.25–8（非有限时为 1），留白不超过宽度一半，圆角不超过短边一半。宿主传入视图前统一规范化；旧接口仍只收到内容缩放，不会把设备缩放或留白当成同一个值。

`IsAdjusting` 用于表示正在调节控件，避免宿主立刻收起；`CancelAdjustment` 返回是否已消费本次取消操作。视图离开后不应保留键盘捕获、鼠标捕获或控件引用。

`ModuleViewContext`：

- `Owner` 是局部视图容器，挂载前已配置共享资源；不再直接传入主窗口。
- `OpenPage`、`OpenSettings` 接受本模组的完整贡献 ID，跨模组调用以 `navigation-owner-mismatch` 拒绝；所有模组采用同样的导航归属限制。
- `ShowActivity` 精确选择本模组指定的活跃 ID，无效或未活跃 ID 不改变当前活动。视图释放后导航委托失效。
- `Own`、`OnDispose`、`Every` 管理当前视图的资源。1.4 中 `Every` 在挂载完成后才启动，加载失败不会遗留计时器；不要在工厂中等待由该计时器才能完成的任务。`Lifetime` 同时响应视图退出和所属模组激活结束。结束后登记的清理立即执行。
- 1.6 的 `IsVisible`、`OnVisibilityChanged` 和 `EveryVisible` 表达宿主实际呈现；初始 false，异步挂载完成后才可见。显示用轮询可随收起、遮挡停止，业务任务不因此暂停。订阅不重放初值，重复状态不通知。完整规则见[宿主规则](host-rules.md)。
- 公共 `OnFrame`、`OnResize`、视图级主题变化订阅和焦点租约尚未提供。

## 5. 活动状态

| 字段 | 含义与约束 |
| --- | --- |
| `Active` | 是否参与持续活动呈现 |
| `Progress` | 有限的 0–1 比例；当前外壳会夹取，非有限值按零处理 |
| `Indeterminate` | 没有确定进度，当前指示条按不定进度方式处理 |
| `Accent` | WPF 可解析的强调色字符串，非法颜色会记故障 |
| `Completion` | 新的完成事件序号；应在完成时递增 |

`Changed` 表示状态需要重新读取，不携带可变业务对象。共享会话放在模组级，紧凑态与展开态只做呈现。临时通知覆盖活动时不得暂停业务计时；通知结束由宿主恢复持续活动。

## 6. 设置

`ModuleSettingKind` 保留 Number=0、Toggle=1、Choice=2，追加 Text=3、Color=4。数字字段使用有限的 `Minimum`、`Maximum` 和正数 `Step`（默认 1）；Choice 使用唯一整数选项；Text 使用 `MaximumLength`（默认 256）；Color 仅接受 `#RRGGBB`。`Group/GroupEn`、`Description/DescriptionEn`、`Placeholder/PlaceholderEn` 提供分组、说明和占位符。缺失英语文案回退中文。

宿主验证定义与读取值、最多展示前 128 个字段，无效字段跳过并产生去重诊断，不把整页拖垮。文本在 Enter 或失焦提交，Escape 恢复最近提交值；颜色复用本体选择器。定义和数据限制见[宿主规则](host-rules.md#字段定义与诊断)。

`IModuleSettings.Read` / `Write` 使用 `object`。作者仍负责默认值、业务验证、原子修改、持久化及迁移。`Reset` 恢复该贡献的业务默认值。`Visible` 为假隐藏入口但保留配置。

可选 `IModuleSettingsNavigation` 显式提供 `Placement`、`IconPath`、`Order`。`Automatic` 保留按组件关联归类的规则，`Modules` 独立于组件显隐，`MiniApps` 跟随小程序开关；多个组件共享同一设置只产生一个入口。图标及位置无效时安全回退。详见[导航规则](host-rules.md#设置导航)与[完整示例](recipes.md#多个组件共用一个独立设置页)。

尚未提供：密钥、统一快捷键字段、字段条件可见性、设置修订号、批量更新、统一 schema 存储和自定义设置面板。

## 7. 存储、事件与资源

`Storage.Read<T>(key)` 从 `<应用数据>/modules-data/<模组ID>/<key>.json` 读取；缺失返回默认值，损坏以 `storage-corrupt` 抛错。推荐使用扩展方法 `Storage.ReadResult<T>(key)`，通过 `Missing`、`Value`、`Corrupt` 区分缺失、有效零／null 与损坏；不支持结构化读取的旧宿主返回 `Unsupported`。其他文件访问异常仍可抛出，不应把损坏自动回写为默认设置。

`Storage.Write<T>` 同步序列化并写盘，当前限制是每个键文件 1 MiB，不是整个模组 1 MiB。写入使用临时文件替换；没有跨键事务、公开异步队列、迁移服务、列举／删除／存在性方法。进行中的作用域写入会在停用收尾前完成；已结束的一代不能写入替代实例的数据。

存储路径的目录、文件及其上层路径不能是符号链接／目录联接；这类访问抛 `storage-path-invalid`，不会转成缺失或零值。写入创建独立的新临时文件，不复用可预先放置的固定临时路径；原子替换之前不会改写已有目标文件。读取在持有拒绝并发写入的文件句柄时检查 1 MiB 限制。上述规则是公开存储接口的归属约束，不构成进程安全沙箱。

停用保留业务数据；用户确认卸载则由宿主在清理回调结束后删除该模组的数据与缓存。清理回调可保存最后快照，但不得另起不受生命周期管理的任务重新写入。详情中的清空缓存只处理加载副本并保护运行中的版本，不会重置 Storage。卸载、磁盘占用统计与待清理重试是宿主管理功能，不增加模组侧 API，也不允许模组清理其他 ID。

宿主通知包括 `ModuleSuspending`、`ModulePreferencesChanged`、`ModuleUiChanged`，均实现 `IModuleHostEvent`，只有宿主可以发布。模组尝试发布会得到 `host-event-publish-denied`。其他模组消息仍按 CLR 类型同步分发，不提供跨模组身份认证或重放。

`IModuleScheduler` 是宿主注入的调度器契约；作者通常使用上下文的 `Every`，不用自己取得调度器。`ModuleAction` 包装清理动作，保证动作只调用一次。

## 8. 公共 UI 与诊断

`ModuleUi.Text` 创建匹配当前面板的文本；主题变化后，视图通过 `Refresh` 更新已创建控件的颜色。`ModuleUi.Appear` 使用公共内容进入动效。`AnimationsEnabled` 综合应用与系统动画开关，`Dark`、`Primary`、`Accent` 为当前主题只读值。

`ModuleControlButton`、`RollingDigitsView`、`RollingStepper`、`PageHandover` 提供与本体一致的按钮、滚动数字、步进和布局交接。随附模组与第三方使用同一公共入口，没有示例专用友元权限。`ModuleUi.Secondary`、`Tertiary`、`OnAccent` 在 1.6 补充语义颜色；资源及交互约定见 [UI 规范](ui.md)。

宿主诊断模型包括 `ModuleInfo`、`ModuleStatus`、`ModuleFault`、`ModulePerformance`。它们用于模组管理页；当前没有向作者公开整个 `ModuleHost` 的服务。性能统计区分启动耗时与回调耗时，嵌套回调按独占时间归属，最近五秒窗口不保留逐次调用历史。

1.6 的 `ModuleInfo.Diagnostics` 是只读配置诊断，按本代、来源及代码去重，最多保留 200 项；不增加运行错误计数，重载后清除旧代。无效字段、导航位置和图标进入诊断；作者 getter 或回调抛出的异常仍进入故障记录。

1.3 的 `ModuleInfo.Generation` 是本次激活身份，停用或重载后更换；不用于永久设置键。`Slow` 标记启动超过提示阈值，完成后保留。`ModuleStatus.Waiting = 5` 追加在旧枚举末尾。故障记录带所属 `Generation`；已停止实例的迟到报错不会覆盖新状态或计为新实例故障。依赖方先于依赖项清理，撤销和释放均针对确切实例，旧任务完成不能撤掉替代实例。

`Invoke<T>` 在宿主适配器中捕获异常并返回默认值，因此不能把任意 `false`、`0` 或 `null` 都解释成业务拒绝原因。下一版需要有明确错误码的操作结果，替代隐含默认值协议。

## 9. 本体数据与岛内提醒

清单声明 `apiMinor: 1`。`capabilities` 是必需能力，`optionalCapabilities` 是可选探测能力，`requires.contractMinor` 和 `requires.capabilities` 也参与启动校验。宿主拒绝未来 minor、未知必需能力及不存在的宿主能力。依赖可用 `maximumVersionExclusive` 设定不包含的版本上界。`IModuleHostInfo.GetInfo()` 无需额外权限，返回应用版本、契约版本与实际能力；可选能力须先检查后调用。

契约 1.2 另提供 `ModuleApi.Catalog` 与 `ModuleApi.Describe(id)`，描述 SDK 已知能力的名称、权限、引入版本、环境、归属和公开契约；它不表示当前宿主已安装全部适配器。`GetInfo().CapabilityDetails` 仅包含当前宿主实际提供的描述项，和原有 `Capabilities` 一一对应，两个列表均不可修改。旧四参数 `ModuleHostInfo` 构造函数保留不变。

宿主初始化按目录核对能力种类、完整 CLR 契约名与程序集、必要权限和重复绑定；不执行模组或服务工厂来做这些校验。桌面应用还会核对适配器完整性，漏接能力在执行模组前报错。接口的实际可用数据仍由快照状态表达。

| 服务 | 能力及同名权限 | 数据 |
| --- | --- | --- |
| `IModuleUsage` | `usage.read` | 各服务的用量指标、重置／过期时间、余额／数量、token 汇总与完整性标记 |
| `IModuleMedia` | `media.read` | 本体选中的播放器、曲名、作者、专辑、播放状态、进度和播放速率 |
| `IModuleSystem` | `system.read` | CPU、内存、GPU、网络吞吐；没有进程路径或进程明细 |
| `IModuleNotifications` | `notifications.show` | 由宿主呈现的短时文字／进度提醒，复用岛内交接动效 |

读取接口只返回独立不可变快照；不暴露凭据、账户详情字典、数据源路径或本体可变 store。权限是公开接口的访问控制，同进程 DLL 仍须来自可信作者。

`GetSnapshot()` 获取最新缓存；`Subscribe()` 不重放旧值，激活提交后才接收变化，随激活代自动释放。订阅后再读最新快照可避免首次挂载漏读。回调在本体发布数据的 UI 线程执行，应快速返回；异常会归属到模组并隔离。快照包含 `Revision` 与 `ObservedAt`。`Available`、`Unavailable`、`Stale`、`Disabled` 表示数据状态，当前宿主主要使用前三者；缺失数字为 null，不能解释成 0。

用量跟随本体已有刷新来源，不新增网络请求；某服务未启用或尚未读取时可能没有条目。必须检查条目状态、`TokenUsageAvailable`、`TokenUsageComplete`，并按具体指标的 `ResetsAt`／`ExpiresAt` 判断有效期。百分比是已用比例，可能超过 100。系统监控订阅按需复用原采样器，不启动进程扫描；超过 6 秒的缓存读取标为 Stale。媒体读取根据时间和播放速率估计进度，订阅仅在本体媒体状态更新时触发。

提醒标题最多 60 字符、正文最多 160 字符，ID 使用局部模组 ID 规则；时长夹取到 900–8000 ms，进度夹取到 0–100，非有限值拒绝。Ready 前返回 `NotReady`。已展开、输入法正在切换、已有提示或全屏抑制时排队，最多等待 30 秒。每模组每分钟最多接收 4 条，队列总计 16 条，同一激活代同 ID 的待显示项合并。`Shown`／`Queued` 仅代表接收结果，不能当成已经被用户看见。关闭模组撤销其待显示与当前提示；通知结束恢复持续计时，不修改计时会话。当前投递到主岛，不向所有屏幕重复广播。

完整可运行示例见 [`BrimDeck.UsageAlerts`](../../../mods/usage-alerts/README.md)：设置 Codex 阈值、一次配额周期提醒一次、跳过过期数据，并读取媒体和系统快照。

## 10. 当前没有的能力

命令与用户绑定的全局快捷键、宿主管理的网络／文件授权／密钥、跨模组服务协商、通用缓存目录、主题样式扩展点、统一共享帧订阅和运行期无响应恢复均未完成。播放控制、在线音源、均衡器和导出接口不属于当前 API。

这些缺口的目标行为、版本策略及验收条件写在[平台规范的下一版要求](../../../mods/README.md#8-下一版核心契约验收要求)。宿主已有[启动恢复和独立静态检查器](checker.md)，不新增模组侧接口；运行期无响应恢复仍未提供。本页只有签名区由工具生成；调用语义仍须与实现和行为测试一起维护。

## 11. 平台入口与交互钩子（1.5）

`IModulePlatform` 扩展旧 `IModuleContext`。入口通过 `context.Platform()` 取得它；旧宿主返回 `platform-unavailable`，使用者应在清单声明 `apiMinor: 5`。旧的 `Register`、`Subscribe`、`GetService` 仍使用同一实现和约束。

| 入口 | 语义 |
| --- | --- |
| `Registries.Register<T>` | 校验贡献白名单及声明能力；暂存到激活成功；返回 `IModuleRegistration`，`Id` 是 `<module>:<local>`，`Dispose` 撤销本句柄。重复释放不移除后续替代条目。 |
| `Events.On<T>` | `Highest`、`High`、`Normal`、`Low`、`Lowest` 顺序，同优先级按登记次序；每个回调独立异常边界与性能计量。发布过程中取消订阅的待执行回调不再执行。非法优先级拒绝。 |
| `Events.Publish<T>` | 同步发布自定义 CLR 类型消息；宿主事件禁止由模组伪造，包括装箱后发布。暂未提供跨 DLL 的事件名称目录、重放或依赖鉴权。 |
| `Services.Get<T>` | 与 `GetService<T>` 共用本次激活缓存，要求宿主绑定、清单能力及必要权限；停用后服务失效。 |

`ModulePageOpening` 和 `ModuleActivityOpening` 在用户或视图显式请求打开页面／展开活动控制条前同步发布；`ContributionId` 为精确完整 ID，`Cancel()` 是不可撤销的取消标记。回调在触发打开的 UI 线程执行，不应等待网络或阻塞。取消只影响本次打开，不停止后台会话，不修改当前页面。宿主在钩子完成后重新检查目标的确切贡献实例；钩子内的重入导航请求不执行。活动自动选择、进度刷新与关闭不是这些钩子的触发点。

`IModulePresentation` 对应 `ui.presentation`（无额外数据权限）。`Invalidate()` 只在就绪激活中请求刷新，宿主合并 UI 工作；不重启模组、不销毁未变化的页面、不提供任意窗口控制。改变组件开关、顺序或设置可见性后调用，避免按帧或每秒调用。

`ModuleWidget.ReadOrder` 可动态返回排序，未提供时使用原 `Order`；`MoveTo(index)` 由模组保存同组顺序。宿主只在同一模组的组件组内提供拖动，复用既有行位移动效。`IconPath` 是最多 4096 字符的 WPF 几何路径，非法路径在注册时拒绝。`SettingsId` 必须属于本模组，否则为 `foreign-contribution-reference`。组件关闭时宿主隐藏对应设置侧栏；再次启用重新显示，字段和偏好由模组保留。

`ModuleTheme`、`ModuleLocalization`、`ModuleMotion`、`RollingDigitsView`、`RollingStepper`、`PageHandover` 是所有作者都能使用的共享设计入口，调用与宿主相同的实现。主题和语言只读；动效遵守应用与系统动画开关。不要依赖 `UI`、`Soft`、`PanelTheme` 等内部类型，计时器也没有这些友元权限。

## 12. 独立开发契约速查

本节补全前文涉及的精确限制，无需阅读宿主源码或其他规范。开发包的 `ref/` 只供编译，宿主提供运行实现；公共 API 白名单是本页列出的契约，不能将 DLL 中所有 public 类型理解为受支持 SDK。

### 线程与资源

桌面宿主在 UI 调度线程调用激活同步部分、事件、定时器、设置读取／写入、视图创建／布局／刷新／释放。异步方法从该线程开始；耗时操作使用 await，不使用 Result/Wait 阻塞。ConfigureAwait(false) 后创建控件前须通过 `Owner.Dispatcher.InvokeAsync` 回 UI 线程。自行启动的后台工作观察 `Lifetime`，资源登记不会自动切换执行线程。

上下文属于确切激活代，视图上下文属于确切挂载。先取消生命周期，再按后登记先释放；单项清理错误不阻断其他资源，结束后登记的清理立即执行。清理回调结束前可保存最后快照，完全释放或迟到任务不得再写；不要靠 Loaded/Unloaded 判断业务结束。

异步工厂取消后的迟到视图仍会释放。即使错误地从另一条界面线程返回，宿主也会尝试在根元素自己的 Dispatcher 清理，且不会重复释放；这不表示支持跨线程挂载。请始终在 Owner.Dispatcher 创建界面；若该 Dispatcher 已关闭，不能保证依赖其线程的释放回调还能执行。

### 可见性

已挂载不等于可见，隐藏也不一定释放。初始 `IsVisible=false`，异步挂载成功且实际呈现才为 true。收起页面、切换控制条、窗口隐藏／最小化、全屏抑制或宿主通知遮挡会关闭对应可见性；线条模式不呈现紧凑活动视图。它不检测其他应用的像素遮挡，也不单纯因鼠标移走变为 false。

`OnVisibilityChanged` 只报告后续变化，不重放，订阅后主动读取初值。重复状态不通知；正常 UI 释放报告最后一次 false，但父代先取消时不能依赖该回调保存业务。`EveryVisible` 隐藏停止，显示后等待完整间隔再触发，需要立即同步时在可见回调调用 Refresh。旧 Every 隐藏不停；业务计时与告警使用模组级任务。

### 设置字段与提交类型

一个设置贡献最多读取前 128 项。非法定义跳过；异常返回值显示合法回退值并保留控件供用户修正，不自动写回，两者均产生去重诊断。作者 getter 或回调抛错仍是运行错误。字段值累计读取预算为 250 毫秒，在同步调用返回后检查，不能打断单个阻塞 getter 或枚举器。英语缺失回退中文。

| 项目 | 有效值 |
| --- | --- |
| Key | 非空、无控制字符、最多 80 字符；区分大小写，同页不重复 |
| Label / LabelEn | 各最多 160 字符；中文非空 |
| Group / Description / Placeholder | 分别最多 80 / 1000 / 256 字符；英语同限，按出现次序分组 |
| Toggle | Read 与 Write 为 bool |
| Number | Read 可为有限整数、float、double、decimal；排除字符串／bool／日期；Minimum < Maximum，值在范围内，Step 有限且 > 0；宿主 Write 提交 double，需要整数时作者自行验证转换 |
| Choice | 1–128 个唯一整数选项，Read/Write 为其中一个 int，选项标签同样最多 160 字符 |
| Text | string；MaximumLength 为 1–8192，默认 256；Enter/失焦提交，Escape 恢复最近提交值 |
| Color | string，仅 #RRGGBB；显示转大写不隐式回写；点击外部取消未提交选择 |

`Write`、`Reset`、校验、默认值、保存、迁移仍归作者；建议“校验 → 保存成功 → 提交内存 → 通知视图”。宿主不会将值统一存入 JSON，直接调用 Write 时也要防御非法键和值。

| Placement | 侧栏与显示条件 |
| --- | --- |
| Automatic | 有关联组件放小程序，否则放模组；要求 Visible；小程序位置还要求总开关与至少一个关联组件开启 |
| Modules | 模组下独立设置，仅要求 Visible；组件与小程序开关不隐式隐藏它 |
| MiniApps | 要求 Visible、小程序总开关；有关联组件时至少一个开启，无关联时独立展示 |

一个设置贡献只有一个入口，多组件关联不复制设置页。无导航接口时按关联组件最小顺序，实现后采用 Order；模组间先按 ID 排列，同组按顺序及贡献 ID。IconPath 为最多 4096 字符的 WPF Geometry 路径，无效图标回退默认；无效 Placement 回退 Automatic，均提供非致命诊断。隐藏入口保留数据，停用模组撤销所有贡献。

### 公共设计资源与交接

资源来自视图 Owner 的资源树。用 SetResourceReference 自动跟随主题，不从 MainWindow 查找资源。颜色字符串是当前快照，使用时在 Refresh 重新读取。

| 用途 | 入口／动态画刷资源 |
| --- | --- |
| 主／次／弱文字 | ModuleUi.Primary/Secondary/Tertiary；PanelPrimary/PanelSecondary/PanelTertiary |
| 强调色／其上文字 | ModuleUi.Accent/OnAccent；PanelAccent；没有 PanelOnAccent 资源 |
| 背景、选中、轨道 | PanelFill、PanelFillSelected、PanelTrack、PanelRingTrack |
| 悬停、按压、弹层 | PanelHover、PanelPressed、PanelPopup、PanelPopupBorder |
| 控件样式 | ModuleControl 用于圆形图标按钮；PanelTextButton 用于文字按钮 |
| 字体／语言 | ModuleUi.PanelFont；ModuleLocalization.Language、T(中文,英文) |

标题／正文／说明可从 18/13/11–12 DIP 起步。数字、状态与按钮预留稳定区域，进位、选中、暂停不改变外框。按分配宽高重排，不再次乘 WPF 设备缩放。`RollingDigitsView.Text` 原位更新；`RollingStepper` 支持加减、长按、拖动、滚轮、键盘、双击输入，ValueChanged 写业务值，视图转发 IsAdjusting/CancelAdjustment，重建时用 TakeDraft/RestoreDraft 保留草稿。

内容区域之上放独立 Grid 作为离场层，创建 `PageHandover(layer)`；重排前 Hold(contentPanel)，重建后 Release(contentPanel,Handover.Morph)，释放时 Drop。只换读数优先原位更新，需要淡入用 Dissolve，深入／返回用 Forward/Back。单次进入用 ModuleUi.Appear；外壳动画由宿主负责，避免重复驱动。所有动画遵守 ModuleMotion.Enabled，关闭时到最终状态。按钮提供 AutomationProperties.Name，保留 Tab、输入法组合输入和局部 Escape，释放时解除捕获。

### 数据单位与有效性

| 字段 | 单位与判定 |
| --- | --- |
| ProviderId | 本体来源 ID 小写，Codex 为 codex；不用可翻译 Name 判断 |
| Metric.Id / Kind | ID 来自数据源，缺省生成 window-/metric- 标识；Kind 当前 percent/balance/spend/count/text，未知值兼容为未知 |
| UsedPercent | 0–100 百分比，不是 0–1；可能超额；null 不等于零；Unlimited 不做额度耗尽告警 |
| Amount / Used / Total | 按 Unit/Currency 解读；余额、花费、数量不可互换 |
| InputTokens/OutputTokens/Cache*Tokens/TotalTokens | token 数，先看 Available/Complete；Total 已含缓存读写，不重复相加 |
| ResetsAt/ExpiresAt/UpdatedAt/ObservedAt | DateTimeOffset 时间点；按重置时间标识周期；ObservedAt 不代表来源刚刷新，过期不告警 |
| 媒体 PositionSeconds/DurationSeconds | 秒，null 表示无时间轴；PlaybackState 为小写状态，未知值显示未知 |
| 系统 *UsedPercent / *Bytes | 百分比 0–100／字节；网络 Download/Upload 是字节每秒；可用快照的单字段仍可为 null |

Revision 表示当前快照变更，不是永久 ID，不能跨服务比较或假定重启后仍单调递增。订阅不重放，先订阅再读取完成初始同步；告警归模组级，多屏不重复发。可选服务先检查 host.info 的实际 Capabilities，再 Get；能力可用也可能暂时无数据。

存储键为 1–80 个小写字母、数字、连字符，首字符为字母或数字。使用 System.Text.Json 默认序列化，DTO 要有可序列化的公开属性；schema 版本由作者保存。Value 状态也可能是合法 null，仍须业务校验。Corrupt 不得默默覆盖；其他访问／磁盘错误仍可抛出。JSON 无秘密存储，单键 1 MiB 不是总配额，没有跨键事务。

### 最低 minor 与错误

| 最低 minor | 新增入口 |
| --- | --- |
| 0 | 基础入口、同步页面／活动、组件、Number/Toggle 设置、上下文资源与存储 |
| 1 | 能力权限声明、本体读取与通知、结构化存储读取与布局 |
| 2 | 能力描述目录及真实宿主能力明细 |
| 3 | 独立启动、慢提示和激活代诊断 |
| 4 | 异步页面／活动 |
| 5 | Platform 注册表／事件／服务、动态组件顺序与公共设计适配器 |
| 6 | 可见性、设置导航、Text/Color、字段元数据与扩展语义颜色 |

新模组建议声明当前 1.6；降低最低版本前核对所有成员。requires.contractMinor 可以提高最低要求，不替代能力声明。主版本、minor、模组版本、数据 schema 分开管理。

| ModuleApiException.Code | 处理 |
| --- | --- |
| contract-version-mismatch | 升级宿主或移除新 API 后调整最低版本 |
| unknown-capability / unknown-permission | 修正不在当前能力目录的 ID |
| capability-unavailable / service-unavailable | 本机无适配器；可选功能先探测，不伪造数据 |
| capability-not-declared / permission-denied | 补能力与必要权限，再更新包并确认内容 |
| unknown-contribution | 按白名单类型注册，接口显式指定泛型 |
| host-event-publish-denied / module-not-ready | 不伪造宿主事件，不在 Ready 前发布普通消息 |
| navigation-owner-mismatch / foreign-contribution-reference | 导航／关联用本模组完整 ID，注册仍用局部 ID |
| ui-thread-required | 回视图 UI 调度线程导航 |
| storage-corrupt | 保留原数据，提供修复／明确重置 |
| storage-path-invalid | 存储路径包含链接；修复路径后重试，不绕过接口改写其他目录 |

参数、重复 ID、已释放上下文与 I/O 也可能抛普通 .NET 异常，不是所有错误都有 Code。只处理能恢复的异常，用 context.Report(operation,error) 记录可定位业务错误，不在失败后仍显示成功。静态检查器的报告码、范围与命令已包含在开发指南与平台规范，不能将静态通过解释为行为或安全认证。
