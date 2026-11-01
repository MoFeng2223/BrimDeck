# BrimDeck 模组系统升级：实施简报

> **读者的任务**：修改 BrimDeck **宿主**的模组系统（`src/`），不是修改某个模组。
> 日历模组（`mods/calendar/`）在这里只作为**回归基线**，用来验证你的改动没有破坏现有契约。
> 生成时间：2026-10-26，对应契约 **1.5**。文中每一条"现状"都经实测，不是推测。

---

## 0. 任务与边界

- 目标：把模组系统从"能用"推到"可治理"——**签名 + 审核 + AI 看代码**是既定路线，
  参考对象是 Folia 的模组平台 Folium（形状参照 Minecraft Forge：注册表 / 事件总线 / 服务）。
- 允许改：`src/BrimDeck.Modules*`、`src/BrimDeck.UI/ModuleViews.cs`、`src/BrimDeck/App.Modules.cs`、
  `src/BrimDeck/MainWindow.Modules.cs`、`src/BrimDeck/SettingsModule*.cs`、`build/`、`tools/`、`tests/`、`docs/`。
- 不允许改：`mods/calendar/**`（回归基线，除非为验证而临时改）；不得让模组引用 `BrimDeck` 主程序集。
- 交付要求：`dotnet build` 0 警告 0 错误；`tests/BrimDeck.ModuleTests` 全绿；
  `tools/BrimDeck.ModuleDocs --check` 通过；现有模组不重编也能继续用。

---

## 1. 快速事实

| 项 | 值 |
|---|---|
| 仓库 | `E:\123pan\AI xianmu\nixiang\BrimDeck` |
| 技术栈 | .NET 10 / WPF / `net10.0-windows10.0.19041.0`，x64 |
| 契约 | `ModuleApi.Major = 1`、`ModuleApi.Minor = 5`（`src/BrimDeck.Modules.Abstractions/HostApis.cs`） |
| 应用数据 | `%LOCALAPPDATA%\BrimDeck\{mods, modules-data, modules-cache, themes}` + `settings.json` |
| 构建宿主 | `dotnet build src/BrimDeck/BrimDeck.csproj -c Release` |
| 构建模组 | `dotnet build mods/calendar/BrimDeck.Calendar.csproj -c Release --artifacts-path E:/BrimDeck-build -t:GetModulePackage` |
| 测试 | `dotnet run --project tests/BrimDeck.ModuleTests`（另有 `BrimDeck.Tests`、`BrimDeck.WindowsTests`） |
| 文档生成 | `dotnet run --project tools/BrimDeck.ModuleDocs/BrimDeck.ModuleDocs.csproj -- --check .` |

参考实现（宿主自带、与第三方走同一加载流程）：`mods/timers`（最完整）、`mods/notes`（最小）、`mods/usage-alerts`（数据与通知）。

---

## 2. 代码地图

### 2.1 契约层 `src/BrimDeck.Modules.Abstractions/`

| 文件 | 主要类型 | 职责 |
|---|---|---|
| `Contracts.cs` | `IModule` `IModuleContext` `IModuleStorage` `ModuleManifest` `ModuleDependency` `ModuleStatus` `ModuleInfo` `ModuleFault` `Contribution<T>` `ModuleAction` | 模组侧唯一的稳定契约 |
| `Platform.cs` | `IModulePlatform` `IModuleRegistries` `IModuleEvents` `IModuleServices` `IModulePresentation` `ModuleEventPriority` `ModuleOpeningEvent` / `ModulePageOpening` / `ModuleActivityOpening` | 1.5 的"注册表/事件/服务"三入口 |
| `HostApis.cs` | `ModuleApi` `ModuleCapability` `ModuleHostInfo` `IModuleUsage/Media/System/Notifications` `ModuleReadResult` `IModuleStructuredStorage` `ModuleStorageExtensions` | 能力目录、宿主服务契约、结构化读取 |
| `Capabilities.cs` | `ModuleCapabilities.Create()` | 能力目录的唯一数据源（ID/种类/权限/契约类型） |

### 2.2 宿主实现 `src/BrimDeck.Modules/`

| 文件 | 职责 | 改动频率 |
|---|---|---|
| `ModuleCatalog.cs` | 扫目录、读清单、内容摘要、解析私有依赖、`DiscoveredModule` | 高（安装器、签名、内部接口检测都落这里） |
| `ModuleHost.cs` | `ModuleDefinition`、宿主主体、注册表持有 | 中 |
| `ModuleHost.Lifecycle.cs` | 激活/停用/依赖排序/暂存-提交-回滚/按 `Generation` 归属 | **高**（安全启动、慢阈值在这里） |
| `ModuleHost.Views.cs` | `ModuleViewLease`、视图挂载/释放、`ModuleViewContext` 绑定 | 高（可见性、上下文订阅） |
| `ModuleRegistry.cs` | 贡献注册表、`ModuleIds` 规则 | 中 |
| `ModulePolicy.cs` | `ModuleKind`（贡献白名单 + 校验）、`ModuleService`、清单校验 `Validate` | **高**（别名表、诊断分级、host 版本范围） |
| `ModuleStorage.cs` | `IModuleStorage` + `IModuleStructuredStorage` 实现（单键 1 MiB、临时文件替换） | 高（`Delete`/`Keys`、data/cache 目录） |
| `ModuleScope.cs` | 一次激活的作用域（`Lifetime`、资源表、调度器） | 中 |
| `ModuleSnapshotSource.cs` | 快照服务基类、`ModuleServiceScope`（权限/能力绑定） | 低 |
| `ModulePerformance.cs` | `ModulePerformanceTracker`、五秒窗口、慢调用统计 | 低（呈现层在 `SettingsModuleCard`） |
| `ModuleNoticeQueue.cs` | 岛内提醒队列与限流 | 低 |
| `ModuleBundleInstaller.cs` | 把 exe 同级 `mods/` 复制到用户目录 | 中（原子安装要扩展它） |

### 2.3 界面契约 `src/BrimDeck.UI/`

| 文件 | 内容 |
|---|---|
| `ModuleViews.cs` | `ModuleViewContext`、`IModuleView`、`ModulePage`、`IModuleActivity`、`ModuleWidget`、`IModuleSettings`、`ModuleSetting`、`ModuleSettingKind` |
| `ModuleDesign.cs` | `ModuleUi` / `ModuleTheme` / `ModuleLocalization` / `ModuleMotion`（模组可用的设计入口） |
| `PageHandover.cs` / `Feedback.cs` / `PanelControls.xaml` / `RollingStepper.cs` / `RollingDigitsView.cs` / `UI.cs` / `PanelTheme.cs` / `Soft.cs` | 视觉与动效设施；**改这些会影响所有模组，需回归** |

### 2.4 应用层 `src/BrimDeck/`

| 文件 | 职责 |
|---|---|
| `App.Modules.cs` | 组装 `ModuleHost` / `ModuleCatalog`；定义 `ModuleDirectory`；**激活条件**；启动时调 `ReconcileAsync` |
| `MainWindow.Modules.cs` | 小程序的页面宿主：页面切换器、viewport 计算、`OpenModulePage`、活动呈现 |
| `SettingsModules.cs` | 设置页的模组侧：`RenderModuleSettings`、`ExternalSettingsPages`、`ModuleSettingVisible` |
| `SettingsModuleWidgets.cs` | **侧栏生成规则**（`VisibleSettingsPages`）、小程序内组件列表、缓存清理入口（未来） |
| `SettingsModuleCard.cs` | 模组卡片：状态、版本、开关、展开详情、性能数字 |
| `SettingsWindow.cs` | 侧栏导航与 `NavigationIcon`（图标只从 `ModuleWidget.IconPath` 取） |

---

## 3. 现有机制（行为级，够动手）

**发现与审批**（`App.Modules.cs`）：

```csharp
bool activate = Settings.Modules.Enabled
    && Settings.Modules.EnabledModules.Contains(module.Manifest.Id)
    && Settings.Modules.Approvals.GetValueOrDefault(module.Manifest.Id) == module.Digest;
```

摘要变了 → 批准失效 → 模组保持禁用。首次发现不执行代码。

**激活**：`ReconcileAsync` 建 `ModuleDefinition` 列表 → 依赖排序 → 逐个 `ActivateAsync`；
期间的贡献**暂存**，成功后一起发布；失败回滚。五秒是"慢"提示，不是超时。

**注册表**：`ModuleKind` 白名单（`ModulePage` / `IModuleActivity` / `ModuleWidget` / `IModuleSettings`），
每类可带 `Validate` 与 `ValidateOwner`（如 widget 的 `SettingsId` 必须属于本模组）。注册 ID = `<module>:<local>`。

**存储**：`modules-data/<id>/<key>.json`，单键 1 MiB，临时文件替换；`ReadResult<T>` 区分
`Missing/Value/Corrupt/Unsupported`，损坏不自动覆盖。

**设置**：`IModuleSettings` 提供 `Fields/Read/Write/Reset/Visible`；宿主渲染数字/开关/选项三种控件。
**侧栏规则**（关键且隐式）：

```
foreach page in PageOrder:
  yield page
  if page == ModulesPage:  yield ExternalSettingsPages()      // 未被任何 widget 引用的设置页 → 模组下方
  if page == MiniAppsPage: 每个启用 widget → 它指向的设置页     // 被引用的 → 小程序下方，每组件一条
```

**视图**：`ModuleViewLease` 负责一次挂载；`ModuleViewContext` 的 `Every` 在**挂载完成后**才启动；
页面切换/停用会取消 `Lifetime`，迟到视图被丢弃。

**事件与服务**：`IModuleEvents.On<T>(handler, priority)` 五级优先级、可取消的仅有
`ModulePageOpening` / `ModuleActivityOpening`；宿主事件由 `IModuleHostEvent` 标记，模组不得发布。
服务按"清单能力 + 权限 + 本次激活"绑定，停用即失效。

**诊断**：`ModuleInfo`（状态/版本/`Slow`/`Generation`）、`ModuleFault`（最多 200 条）、
`ModulePerformance`（启动耗时、五秒窗口、慢调用、错误数）。

**打包**：`build/ModulePackage.targets` 只拷 `*.dll/*.deps.json/module.json`，排除宿主与共享 SDK 的 5 个 DLL；
校验 `ModuleId` 正则。

---

## 4. 必须遵守的不变量

1. **向后兼容**：`apiVersion 1 / apiMinor ≤ 5` 的旧清单必须继续可用；新增能力只能 additive，
   `ModuleApi.Minor` 加一（当前 → 6）。旧能力名继续可用（进度见 P2-1 的别名表）。
2. **失败必须回滚**：激活中途抛错 → 该模组本次注册的贡献、订阅、定时器全部撤销；其他模组不受影响。
3. **按代归属**：错误/迟到任务都记在 `Generation` 上；旧代迟到不得覆盖新代状态。
4. **清理顺序**：后登记先清理，单个清理抛错不阻断其余。
5. **不得改现有成员签名或语义**。要改就新增成员并给旧的标 `[Obsolete("…", false)]`。
6. **模组不得引用 `BrimDeck` 主程序集**（加载器会拒绝），这条不能放宽。
7. **UI 契约改动要过回归**：`ModuleViews.cs`、`ModuleDesign.cs`、`PanelControls.xaml` 影响所有模组，
   尤其 `mods/timers`（最复杂）与 `mods/calendar`（最新）。
8. 文档与实现同步：改契约就跑 `ModuleDocs --check`；改行为就更新 `docs/zh-CN/modules/*.md`。

---

## 5. 改动清单（可直接派工）

每条含：目标 / 落点 / 契约变化 / 兼容 / 验收。优先级按"防事故 → 可治理 → 体验"排。

### P0-1 安全启动（防"模组把本体搞到打不开"）

- **目标**：启动期崩溃后，下次启动在**运行任何模组代码之前**询问"要不要不带模组启动"。
- **落点**：`App.Modules.cs`（`ConfigureModulesAsync` 之前写记号、主窗口首帧后撤除）、`ModuleHost`
  （一次性"本次不加载"开关，不写设置）、`SettingsModuleCard.cs`（状态显示「这次没有启动」）。
- **机制**：`mod-start-note.json`；正常退出（用户退出/关机/更新重启）也撤除；启动期间页面每秒上报"我在响应"，
  退出时最近 3 秒听到过才撤；记号删不掉时另写一个作废文件，避免每次启动都问。
- **契约变化**：无（纯宿主行为）；可加 `ModuleStatus` 之外的展示状态，但**不要**改枚举既有数值。
- **验收**：模拟启动期崩溃（激活里 `Environment.FailFast`）→ 下次启动出现询问；选"不带"后本次不加载、
  设置未被修改；再次正常启动不再询问。

### P0-2 `BrimDeck.ModuleCheck` 工具

- **目标**：模组作者与审核方能"离线跑一遍模组"，输出它注册了什么、有没有越界。
- **落点**：`tools/BrimDeck.ModuleCheck/`（新项目）；复用 `ModuleHost` + 一个空实现的 `IModuleContext`。
- **能力**：校验清单（id/版本/能力/权限/入口类型/依赖）、打印注册清单（页面/活动/组件/设置的数量与 id）、
  打印 `Storage` 键、捕获激活异常与耗时。
- **契约变化**：无。
- **验收**：对 `mods/calendar`、`mods/timers`、`mods/notes` 各跑一次，输出稳定；故意写坏清单能给出可读错误。

### P1-1 签名四状态 + 原因码

- **目标**：区分「官方认证 / 先行测试 / 未验证 / 签名不匹配」，并把原因说清楚。
- **落点**：`ModuleCatalog.cs`（读包内 `BRIMDECK.SIG`、校验 Ed25519、写 `PackageTrust`）、`App.Modules.cs`（只影响展示与提示，
  **不改变激活条件**）、`SettingsModuleCard.cs`（徽章）、启用确认窗口（措辞）。
- **契约变化**：`ModuleInfo` 加 `Trust`（additive）；清单不加字段（签名是包内文件）。
- **规则**：签名覆盖除 `BRIMDECK.SIG` 与 OS 垃圾文件（`.DS_Store`/`Thumbs.db`/`desktop.ini`）外的全部普通文件；
  清单 `sha256  path` 按码元排序、路径 `NFC` 规范化；签名消息固定为
  `brimdeck-signature/1` / `modId=` / `modVersion=` / `digest=` / `keyId=` / `signedAt=` 六行（改版本号即失效）。
- **验收**：篡改任一文件 → `digest-mismatch`；换 key → `unknown-key`；无签名 → `unsigned` 且仍可启用。

### P1-2 开发目录豁免

- **目标**：仓库 `mods/` 下的开发版改文件后自动更新授权（安装版永不豁免）。
- **落点**：`App.Modules.cs` 中比较 digest 的那一处，按"来源目录"分流；`ModuleCatalog.DiscoveredModule` 记录来源。
- **验收**：改 `mods/calendar` 里任一文件 → 刷新后无需重新确认；`%LOCALAPPDATA%\BrimDeck\mods\*` 仍要求重新确认。

### P1-3 设置字段扩容 + 非法声明降级

- **目标**：`ModuleSettingKind` 增加 `Text` / `Color`（`Secret` 见 P2-2 之后单独做）；字段加 `Group`/`Description`/`Placeholder`；
  **非法字段丢弃并报告，而不是让整个模组 Failed**。
- **落点**：`ModuleViews.cs`（枚举与记录）、`ModulePolicy.cs`（校验从抛错改为收集诊断）、`SettingsModules.cs`（渲染新类型）、
  `ModuleInfo`（携带 `Diagnostics`）。
- **兼容**：旧模组只用到 `Number/Toggle/Choice`，不受影响；新字段对旧宿主不可见（它们在旧宿主里会被忽略——需确认解析策略）。

### P1-4 存储 `Delete`/`Keys` + `data`/`cache` 分离

- **目标**：模组能清空自己的数据；大文件有地方放；用户能在面板里清缓存。
- **落点**：`Contracts.cs`（`IModuleStorage` 加 `Delete/Keys`）、`ModuleStorage.cs`（实现 + 目录结构
  `modules-data/<id>/files` 与 `modules-cache/<id>/user`）、`SettingsModuleCard.cs`（"数据 X / 缓存 Y / 清空缓存"）。
- **兼容**：只增成员；旧模组不受影响。旧宿主上调用会缺方法——因此需要 `apiMinor` 6。

### P2-1 `experimental[]` + 弃用通道

- **目标**：稳定面 1.x 只增不改；实验接口逐个显式选用；旧名继续可用并被提醒。
- **落点**：`ModuleManifest`（`Experimental`）、`ModulePolicy`（`Validate` 拆 `Validate`/`Diagnose`；
  能力/权限检查前先过**别名表**）、`ModuleDeprecations.cs`（新，诊断表）、`ModuleInfo.Diagnostics`、
  `SettingsModuleCard.cs`、宿主版本变化后的兼容报告（`SettingsModules.cs`）。
- **提示格式**：`module.json capability "x": removed in BrimDeck 2.0, use "y"`
- **关键限制**：**.NET 运行期无法发现模组调用了哪个成员**（无动态代理）。成员级弃用只能靠
  `[Obsolete]`（编译期）与检查器的 IL 扫描（工具期）；运行期只做清单层面的别名。

### P2-2 内部接口检测 + `host` 版本范围

- **目标**：模组引用 `BrimDeck.Core`（非契约）时宿主必须知情；宿主版本不符时明确拒绝而不是 `Failed`。
- **落点**：`ModuleCatalog.cs`（用 `PEReader` 读 `AssemblyReferences`，命中 `BrimDeck.Core`/`BrimDeck.Modules`/`BrimDeck`
  即标 `UsesInternals`）、`ModuleManifest`（新增 `Host`，复用 `ModuleDependency` 的上下界形状）、
  `ModulePolicy`（`UsesInternals && Host is null` 拒绝）、`ModuleStatus`（追加 `HostMismatch = 6`，
  沿用 1.3 追加 `Waiting = 5` 的做法）、`ModuleInfo`（`UsesInternals` / `HostRangeText`）、卡片与确认窗口。
- **验收**：把 `mods/calendar` 临时加一行 `using BrimDeck.Core;` → 卡片出现「使用内部接口」，且未声明 `host` 时拒绝加载。

### P2-3 市场条目与 `scope`

- **目标**：让用户看懂"它会盖住我的任务栏"这类影响范围，而不只是权限名。
- **落点**：`ModuleManifest`（`Scope` / `Intrusive`）、`SettingsModuleCard.cs`、确认窗口、文档。
- 该条依赖"市场"是否存在；若暂无市场，可先只做字段与展示。

### P3-1 视图上下文订阅：`Visible` / `Frame` / `Changed`

- **目标**：替掉模组的轮询。
- **落点**：`ModuleViews.cs`（`ModuleViewContext` 加 `IsVisible` + `VisibilityChanged`，以及 `OnFrame`/`OnChange`）、
  `ModuleHost.Views.cs`（挂载/卸载处驱动）、`MainWindow.Modules.cs`（切页与展开/收起时置位）。
- **注意**：**收起刘海不会改变内容区 `Visibility`**（只有切到用量/音乐页才 `Collapsed`），
  所以不能靠 WPF 的 `IsVisible` 推断"面板是否展开"，必须由宿主显式驱动。

### P3-2 原子安装（zip）

- **落点**：`ModuleBundleInstaller.cs` 扩展为通用安装器：拒绝路径穿越/绝对路径；包与解压各 ≤64 MB、
  单文件 ≤32 MB、条目 ≤2000；解压到 `.staging/` 校验清单与入口文件 → 换入，失败回滚。
- **验收**：故意塞 `../x.dll`、超大文件、缺少入口的包，均被拒绝且旧版保持可用。

### P3-3 主题贡献 `IModuleTheme`

- **落点**：新增能力 `ui.theme`；`IModuleTheme` 提供 `PanelThemeFile`（Base + Colors + Apps + Clock + Effects）；
  注册后进入「设置 → 外观 → 主题」列表，**由用户选择才生效**，停用即移除并回落。
- **为什么这么设计**：主题只在内存里分层，不落盘 → 卸载后不存在残留；不覆盖用户偏好。
- 契约：`IModuleTheme` + 能力目录新增一条（`Capabilities.cs`）；`PanelTheme` 的分层合并在宿主侧完成。

---

## 6. 回归基线：日历模组能验证什么

`mods/calendar` 是当前最新的第三方形态模组，改动后至少验证这些对应关系：

| 改动 | 用日历怎么验 |
|---|---|
| 设置管线 / 侧栏规则 | 设置 → 工具 → 模组 → 日历：只应出现**一条**设置入口；页面/活动开关在设置页内 |
| 存储 | 新建事件 → 重启 → 事件还在；`modules-data/calendar/events.json` 结构未变 |
| 活动 | 收起面板显示"距下一件"；今天无事项时整条消失 |
| 事件与提醒 | 事件开始前收到岛内通知；同一事件只提醒一次 |
| 视图生命周期 | 切页回来草稿不丢；关页面后 5 秒轮询停止（vivo 关闭时不读取） |
| 打包 | `-t:GetModulePackage` 产物仍是 4 个文件、不含宿主 DLL |
| 弃用/内部接口（P2） | 临时改用旧能力名 → 卡片提示；临时 `using BrimDeck.Core` → 徽章 |

---

## 7. 验证协议

```powershell
# 1. 宿主编译（0 警告是硬要求）
dotnet build src/BrimDeck/BrimDeck.csproj -c Release

# 2. 契约与行为测试
dotnet run --project tests/BrimDeck.ModuleTests
dotnet run --project tests/BrimDeck.Tests

# 3. 文档签名区与实现一致
dotnet run --project tools/BrimDeck.ModuleDocs/BrimDeck.ModuleDocs.csproj -- --check .

# 4. 模组侧回归（不重编也能跑是目标；改了契约才重编）
dotnet build mods/calendar/BrimDeck.Calendar.csproj -c Release --artifacts-path E:/BrimDeck-build -t:GetModulePackage
```

**手动步骤**（每次改动后）：设置 → 工具 → 模组 → 刷新 → 启用；开关一次「显示日历页面」；开关一次「显示岛内日程」；
新建/删除一条事件；切换明暗主题；最小化再还原。

**离屏验证**（可选，用于 UI 改动）：`E:/BrimDeck-build/uitest`（不在仓库内）用 `RenderTargetBitmap` 渲染，
覆盖 480×266 / 560×320 / 800×400 / 1200×140 四种尺寸与明暗两套主题。**不要只靠看图判断**，用
`VisualTreeHelper` 打印控件坐标核对。

---

## 8. 风险与禁区

1. **不要引入双进程**。WPF 单进程是既成事实；引入 `main`/`client` 式分层会摧毁现有 UI 契约，且拿不到沙箱收益。
2. **不要在同一个 PR 里同时改契约与 UI 样式**。两者回归面不同，混在一起无法定位。
3. **不要放宽"模组不得引用主程序集"**。
4. **不要动性能采集的五秒窗口语义**（`ModulePerformanceTracker`）：它已被 `ModuleTests` 与设置页依赖。
5. **不要用"自动停用"处理错误或慢启动**。现有契约明确"宿主不会因为出错/慢关掉任何东西"，这是有意设计。
6. **改 `ModulePolicy.Validate` 的抛错行为要非常小心**：现有模组可能依赖"清单非法 → 直接失败"的语义；
   降级（P1-3/P2-1）应只针对"内容性问题"，结构性错误（入口类型、清单缺失）继续失败。

---

## 9. Definition of Done

- [ ] `dotnet build` 0 警告 0 错误；三套测试全绿；`ModuleDocs --check` 通过
- [ ] `apiMinor` 已递增，`docs/zh-CN/modules/{modules,contributing,api}.md` 已更新
- [ ] `mods/timers` 与 `mods/calendar` 不重编即可正常加载、启用、使用
- [ ] 每个新增能力在 `Capabilities.cs` 有登记（ID/种类/权限/契约类型），否则启动校验会失败
- [ ] 新增状态/诊断有对应的 UI 呈现（卡片、确认窗口任选其一以上）
- [ ] 改动范围内每个新行为都有测试（`tests/BrimDeck.ModuleTests` 优先）
- [ ] 在 PR 描述里写清"破坏面"：哪些模组行为会变、旧模组是否仍可用

---

## 10. 附：已有设计依据

`mods/calendar/` 下四份文档（可直接引用为设计输入）：

| 文件 | 用途 |
|---|---|
| `HOST-NOTES.md` | 宿主规则与设计令牌的实测笔记（含 9 条踩坑），改 UI 契约前必读 |
| `SANDBOX.md` | 为什么同进程无沙箱、能做的与不能做的、"隔离 vs 遏制"、残留治理 |
| `FOLIUM-COMPARE.md` | 与 Folium 的逐项对照、A/B/C 分档、`platform.raw` / `internals` 范式、市场条目、弃用通道、内部接口与版本范围、落地顺序 |
| `README.md` | 日历模组本身的功能与数据格式（回归时对照） |
