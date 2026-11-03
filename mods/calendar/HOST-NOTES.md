# 宿主模组系统：实测笔记

写给文档作者，也写给后来的模组作者。契约版本 **1.5**，基于一次本地构建实测；
每条都标注来源文件，行号会随代码漂移，**以符号名为准**。

这份文件补的不是 API，而是文档目前没有覆盖的三类"规格"：

1. **设计令牌** —— 用什么颜色、字号、圆角、按钮样式，才能看起来像本体的一部分；
2. **宿主规则** —— 宿主会怎么对待你的贡献（放哪里、图标从哪来、什么时候自动跑）；
3. **组件用法** —— 有签名但没有规矩的公共控件（动效、步进器、定时器）。

---

## 1. 设计令牌

### 1.1 主题色资源键（`src/BrimDeck/App.xaml`）

宿主用 `DynamicResource` 暴露主题色，模组通过 `SetResourceReference` 取用。可用的键：

| 键 | 用途 |
| --- | --- |
| `PanelSurface` | 面板底色（弹窗、浮层正文） |
| `PanelPrimary` / `PanelSecondary` / `PanelTertiary` | 三级文字 |
| `PanelAccent` | 强调色：**键盘焦点、选中日期** |
| `PanelFill` / `PanelFillSelected` | 卡片底、选中项底 |
| `PanelHover` / `PanelPressed` | 悬停、按下 |
| `PanelTrack` / `PanelRingTrack` | 进度轨、配额环轨 |
| `PanelPopup` / `PanelPopupBorder` | 弹出层（对话框、菜单） |
| `PanelBubble` / `PanelBubbleBorder` | 气泡 |
| `PanelScrollThumb` / `PanelSwitchFill` / `PanelSeekRail` | 滚动条、开关、进度条填充 |

语义来自 `src/BrimDeck.Core/PanelThemes.cs` 的 `Tokens` 表，其中两条最关键：

- `accent`：*"强调色（键盘焦点、选中日期）"*
- `onAccent`：*"强调色上的文字（选中的日期）"* —— **没有对应的 `PanelOnAccent` 资源键**，
  需要时可从 `ModuleUi.Accent` 的亮度推白色/黑色。这是文档应该点名的一处缺口。

> 只读值：`ModuleUi.Primary` / `Accent` / `FillSelected` / `Dark` / `AnimationsEnabled`、`ModuleTheme.Dark` / `Generation`。

### 1.2 字体与文字

`src/BrimDeck.UI/UI.cs`：

- `PanelFont = "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"`（正文与数字）
- `DisplayFont = "Segoe UI Variable Display, ..."`（`RollingDigitsView` 内部使用，未公开导出）
- `ModuleUi.Text(text, size)` → `UI.Centered(...)`，它同时做四件事：
  **等宽数字**（`Typography.NumeralAlignment = Tabular`）、**NoWrap**、**尾部省略**、垂直居中。

所以：**不要自己 `new TextBlock`**，否则数字会在刷新时左右跳、超长文字会换行撑破布局。
需要别的颜色时用 `ModuleUi.Centered(...)` 之后 `SetResourceReference(TextBlock.ForegroundProperty, "PanelSecondary")`。

### 1.3 按钮三件套（`src/BrimDeck.UI/PanelControls.xaml`）

| 样式 | 尺寸 | 关键属性 |
| --- | --- | --- |
| `PanelIconButton`（基类） | 32×28，圆角 5 | `Foreground=PanelSecondary`、`Cursor=Hand`、`FocusVisualStyle=PanelKeyboardFocus` |
| `PanelSourceButton` | 高 24，圆角 6 | 悬停/按下双层 + 透明 chrome，可设 `Background` 表示选中 |
| `PanelGlyphButton` | 继承 32×28 | 无背景，靠字形透明度反馈：静止 .78 / 悬停 1 / 按下 .6 / 禁用 .4 |
| `PanelTextButton` | 高 24，`Padding 8,2`，字号 11 | 右对齐、禁用时 `Opacity=.28` |

写自定义可点区域（例如日历格子）时，要么复用上面的样式，要么自建模板 + 挂两件事：

```csharp
Feedback.SetFade(button, true);                                  // 悬停/按下淡入
button.FocusVisualStyle = (Style)owner.TryFindResource("PanelWideKeyboardFocus");
```

### 1.4 键盘焦点环（`src/BrimDeck/App.xaml`）

| 键 | 形状 | 适用 |
| --- | --- | --- |
| `PanelKeyboardFocus` | 1px accent，圆角 5 | 小按钮、列表行 |
| `PanelWideKeyboardFocus` | 圆角 8 | 卡片式的大目标（日期格） |
| `PanelOutsetKeyboardFocus` | 圆角 10、`Margin=-2` | 自身已有蓝色描边的控件 |

**自绘的可点元素必须自己挂 `FocusVisualStyle`**，否则键盘用户看不到焦点。

### 1.5 悬停/按下反馈（`src/BrimDeck.UI/Feedback.cs`）

`Feedback.Fade` 是一个附加属性，要求按钮模板里有**按名字注册**的元素：

- `HoverLayer`、`PressLayer`：两层 `Border`，初始 `Opacity=0`；
- `Glyph`（可选）：只靠字形透明度反馈时用。

它按 `Soft.Hover`/`Soft.Follow` 的曲线淡入淡出，并遵守 `Soft.Enabled`
（`SystemParameters.ClientAreaAnimation && 应用动画开关`）。

### 1.6 卡片与间距惯例（来自 `mods/timers`，文档未写）

- 卡片：`Border`，`Background=PanelFill`，`CornerRadius=12`，`Padding 3–6`，`Margin 2`
- 字阶：9 / 10 / 11 / 12 / 13 / 16 / 22–32（`RollingDigitsView`）
- 行高：列表行 22，按钮 24–26，标题行 28
- **面板内不放滚动条**：内容超出就分页（timers 的分段记录、日历的日程列表都是这样）

---

## 2. 宿主规则

### 2.1 设置页出现在哪一栏 —— 由"有没有组件引用"决定

`src/BrimDeck/SettingsModuleWidgets.cs` · `VisibleSettingsPages()`：

```csharp
if (page == ModulesPage) foreach (int extra in ExternalSettingsPages()) yield return extra;      // 模组下方
if (page == MiniAppsPage && ...)
    foreach (var widget in ModuleWidgetEntries().Where(WidgetEnabled))                            // 小程序下方
        if (ModuleSettingEntry(widget.Value.SettingsId) is { } entry && ...) yield return SettingsPageFor(entry);
```

- **有 `ModuleWidget` 指向它** → 列在「小程序」下方，**每个组件一条**，标题取设置页的 `Name`；
- **没有组件指向** → `ExternalSettingsPages()`（`SettingsModules.cs`），列在「模组」正下方。

推论与坑：

- 两个组件共用一个设置页 → **侧栏出现两条同名条目**（我踩过）。修法：组件与设置页 1:1，或宿主对该循环去重。
- 想"设置归在模组下"就不能注册 `ModuleWidget`，代价是失去小程序组件列表里的开关（可用设置页里的 Toggle 字段替代）。

### 2.2 侧栏图标只能来自组件

`src/BrimDeck/SettingsWindow.cs` · `NavigationIcon()`：

```csharp
var widget = ModuleWidgetEntries().FirstOrDefault(w => w.Value.SettingsId == id);
if (widget?.Value.IconPath is { } icon) { ... }   // 否则落到内置图标表，未知页是通用圆圈
```

`IModuleSettings` **没有 `IconPath`**。所以"独立设置页"与"自定义图标"目前互斥。

### 2.3 小程序页面切换器是下拉，不是标签

`src/BrimDeck/MainWindow.Modules.cs` · `RenderMiniApps()`：

- 页面数 `> 1` → 渲染一个 `PanelSourceButton`（当前页标题 + `⌄`）+ `ContextMenu`；
- 槽位高度固定 **34**（`selector = entries.Length > 1 ? 34 : 0`），并从页面 viewport 里扣掉；
- 视图可用区域 = `面板宽 − 2×Side`，`面板高 − Row − bottom − selector`。

模组无法影响这个形态。

### 2.4 什么时候会自动激活

`src/BrimDeck/App.Modules.cs`：

```csharp
Settings.Modules.Enabled
  && Settings.Modules.EnabledModules.Contains(module.Manifest.Id)
  && Settings.Modules.Approvals.GetValueOrDefault(module.Manifest.Id) == module.Digest
```

**批准绑定内容摘要**：重新构建、文件一变，之前的批准就失效，必须重新确认。
这既是"改完要刷新+重新启用"的原因，也是"下载即崩溃"的防线。

### 2.5 目录（`src/BrimDeck/App.xaml.cs`、`App.Modules.cs`）

```
%LOCALAPPDATA%\BrimDeck\mods\                 模组本体（每个模组一个子目录）
%LOCALAPPDATA%\BrimDeck\modules-data\<id>\   context.Storage 的落点，<key>.json
%LOCALAPPDATA%\BrimDeck\modules-cache\<id>\<摘要>\   加载器副本（不参与发现）
```

`IModuleStorage` 只有 `Read`/`Write`：**没有 Delete / Exists / List**，所以模组无法自己清空数据，
卸载后 `modules-data` 会留着。要清只能用户手动删。

### 2.6 打包（`build/ModulePackage.targets`）

- 校验 `ModuleId`：`^[a-z0-9][a-z0-9-]{0,79}$`
- 只拷 `*.dll`、`*.deps.json`、`module.json`
- **排除** `BrimDeck.dll`、`BrimDeck.Modules.dll`、`BrimDeck.Core.dll`、`BrimDeck.UI.dll`、`BrimDeck.Modules.Abstractions.dll`

### 2.7 收起刘海 ≠ 页面不可见

`MainWindow.SetExpanded()` 不改变内容区 `Visibility`，只有切到「用量/音乐/监控」时
`UsagePanel.cs` 才会把 `MiniAppsContent.Visibility = Collapsed`。

结果：**收起成刘海时，模组页面仍然挂载、`IsVisible` 仍为 true**，模组的轮询会继续跑。
WPF 的 `IsVisible` 只能覆盖"切页 / 最小化"，覆盖不到"收起"。这正是需要
`ModuleViewContext.IsVisible` + `VisibilityChanged` 的地方。

---

## 3. 组件用法

### 3.1 `PageHandover` 在动画关闭时是空操作

`src/BrimDeck.UI/PageHandover.cs`：

| 方法 | 语义 |
| --- | --- |
| `Hold(Panel)` / `Release(content, Handover)` | 重建前收起旧内容、重建后按 `Morph`/`Forward`/`Back` 交接 |
| `Arrive(element, motion)` | 只对 `Forward`/`Back` 生效，让工具栏部件随内容一起进场 |
| `Brighten(element)` | 从 0.3 淡入（局部刷新） |
| `Appear(element)` | 从 0 淡入（弹窗、临时提示） |

**坑**：`Soft.Enabled == false` 时这些都提前返回，**不改变元素透明度**。
所以"先把 `Opacity` 设成 0、再调 `Appear`"在关掉动画的机器上会让元素永远看不见。
正确做法：默认 `Opacity=1`，只在动画开启时才交给 `Appear` 从 0 拉起。

### 3.2 `RollingStepper` 依赖宿主资源，且目标类型必须是 `RepeatButton`

`src/BrimDeck.UI/RollingStepper.cs`：

```csharp
Style = owner.TryFindResource("TimerStepButton") as Style
```

- 资源不存在 → 退化成默认按钮外观（不报错）；
- 资源存在但 `TargetType` 不是 `RepeatButton`（例如写成 `Button`）→ **运行时抛样式不兼容**。
  自己造测试宿主时必须把 `TimerStepButton` 定义成 `TargetType="RepeatButton"`。

其余要点：`new RollingStepper(owner, label, value, min, max)`、`Fit(w,h)`、
`ValueChanged`、`TakeDraft()/RestoreDraft()`（重建时保留未确认的输入）、`IsAdjusting`。

### 3.3 `ModuleViewContext.Every` 挂载后才启动

`src/BrimDeck.UI/ModuleViews.cs`：视图在 `_attached && !_ready` 期间登记的定时器会**排队**，
等 `CompleteMount()` 才启动。所以：

- 不要在视图工厂里等待"由这个定时器驱动的结果"——会互相等待；
- 视图卸载/加载失败时定时器不会遗留（1.4 起）。

### 3.4 异步视图的优先级

`ModulePage.Async(...)` 设 `CreateViewAsync`，宿主优先用它；此时同步的 `CreateView`
会抛 `async-view-required`。返回前应检查 `view.Lifetime`，迟到视图会被丢弃。

### 3.5 默认实现与必须实现

| 成员 | 默认实现？ |
| --- | --- |
| `IModuleView.IsAdjusting` | ✅ 默认 `false` |
| `IModuleView.CancelAdjustment()` | ✅ 默认 `false` |
| `IModuleSettings.Visible` | ❌ **必须实现** |

### 3.6 `ReadResult` 的第三种状态

`HostApis.cs`：`storage.ReadResult<T>(key)` 是扩展方法；宿主没有实现
`IModuleStructuredStorage` 时返回 `Unsupported`，旧宿主上不要把它当成 `Missing`。

### 3.7 同一局部 ID 可以跨贡献类型复用

注册 ID 最终是 `<模组ID>:<局部ID>`。设置、组件、活动可以共用同一个局部 ID
（timers 的三个模式就是这么做的），**同一类型内**重复才被拒绝。
自己写测试用假宿主时，注册表要按类型分键，否则会误碰撞。

---

## 4. 踩坑对照表

| 现象 | 根因 | 修法 |
| --- | --- | --- |
| 侧栏出现两条同名设置 | 两个组件共用一个设置页 | 1:1 拆分，或宿主对 `VisibleSettingsPages` 去重 |
| 设置跑到「小程序」下面 | 有 `ModuleWidget` 引用即归入小程序 | 去掉组件，开关改为设置页字段 |
| 设置页图标变成通用圆圈 | `IconPath` 只能由 `ModuleWidget` 提供 | 接受，或宿主给 `IModuleSettings` 加图标 |
| 关掉系统动画后弹窗不显示 | `Opacity=0` + `Appear` 在动画关闭时是空操作 | 默认 1，只在动画开启时从 0 拉起 |
| 编辑器里点步进器崩 | 测试宿主的 `TimerStepButton` 目标类型不是 `RepeatButton` | 对齐类型 |
| 卡片样式对、悬停没反应 | 自建模板缺少 `HoverLayer`/`PressLayer`，或没挂 `Feedback.Fade` | 按 1.5 补 |
| 数字刷新时左右跳 | 自己 `new TextBlock`，没有等宽数字 | 走 `ModuleUi.Centered` |
| 改完代码重启也没变化 | 内容摘要变了，批准失效 | 刷新 + 重新启用 |
| 收起面板后仍在读外部数据 | 收起不改变 `Visibility` | 等宿主开放可见性，或自行限制频率 |

---

## 5. 建议补进 `docs/zh-CN/modules/` 的三章

1. **`ui.md`** — 令牌表（1.1–1.4）+ 五个可复制配方：可点格子、确认式删除、模态浮层、
   分页列表、空状态。目标：不读源码也能做出与本体一致的界面。
2. **`host-rules.md`** — 2.1–2.7。标题建议直白些：「宿主会怎样对待你的贡献」。
3. **`recipes.md`** — 轮询与可见性、外部数据读取（含 `winsqlite3.dll` 这类系统库的最小封装）、
   主题/语言变化后的重建、多显示器各自视图。

## 6. 不属于文档、但值得改的宿主（按成本排序）

1. `VisibleSettingsPages()` 对 `SettingsId` 去重（顺手修掉重复条目）。
2. `IModuleSettings` 增加 `IconPath`；或给设置页加显式归属（`Modules` / `MiniApps`）。
3. `IModuleStorage.Delete(key)`（卸载残留、清空数据）。
4. `ModuleViewContext.IsVisible` + `VisibilityChanged`（省掉收起时的无用轮询）。
5. 启动失败回退：启动标记 + 连续崩溃自动停用最后激活的模组（防"打不开本体"）。
6. `files.pick`（把"任意路径读"变成用户显式选择，` .ics` 导入才干净）。

---

*本文件由日历模组的开发过程整理，所有条目均在 1.5 契约下实测；示例代码见同目录
`CalendarPageView.cs`（格子/行/浮层）、`CalendarModule.cs`（设置/存储/外部源）、
`Core/Sqlite.cs`（系统库 P/Invoke 的最小封装）。*
