# 模组 UI 与交互规范

适用契约：1.6，WPF。宿主管理岛的形状、位置、页面导航、展开收起和通知交接；模组在分配的内容区域内排版。先看[宿主规则](host-rules.md)和[可运行示例](../../../mods/timers/README.md)，类型签名见 [API 参考](api.md)。

## 颜色、字体和资源

使用语义颜色，不用黑白常量推断主题。以下入口读取当前主题；已经创建的控件应在 `Refresh` 中更新，或使用下表的动态画刷资源。

| 用途 | 公共入口 | 动态画刷资源 |
| --- | --- | --- |
| 主要文字 | `ModuleUi.Primary` | `PanelPrimary` |
| 次级说明 | `ModuleUi.Secondary` | `PanelSecondary` |
| 弱化信息 | `ModuleUi.Tertiary` | `PanelTertiary` |
| 强调色 | `ModuleUi.Accent` | `PanelAccent` |
| 强调色上的文字 | `ModuleUi.OnAccent` | 使用公共入口；没有 `PanelOnAccent` 资源 |
| 普通／选中填充 | `ModuleUi.FillSelected` 只提供选中填充 | `PanelFill`／`PanelFillSelected` |
| 轨道 | 使用动态资源 | `PanelTrack`／`PanelRingTrack` |
| 悬停／按压 | 使用动态资源 | `PanelHover`／`PanelPressed` |
| 弹层 | 使用动态资源 | `PanelPopup`／`PanelPopupBorder` |

资源来自 `ModuleViewContext.Owner` 的资源树。模组不要从 `Application.Current.MainWindow` 找资源，也不要假定设置窗口的 `Settings*` 样式属于模组 SDK。动态资源示例：

```csharp
var caption = ModuleUi.Text("下一阶段", 12);
caption.SetResourceReference(TextBlock.ForegroundProperty, "PanelSecondary");
```

`ModuleUi.PanelFont` 是面板字体。标题、正文、说明推荐从 18、13、11–12 DIP 开始，根据可用空间调整。数字用 `RollingDigitsView`，不要自行引入另一套字体、计时器和逐帧滚动。颜色字符串是当前主题快照；缓存字符串不会自动随主题变化。

## 排版与稳定性

- 宽高均为 DIP；设备缩放与内容缩放分别读取 `ModuleViewport.DeviceScale`、`ContentScale`。不要再次把设备缩放乘进 WPF 尺寸。
- 空间变化时先改变列数和间距，再缩放文字；不要仅依据屏幕分辨率排版。`HorizontalInset` 是内容留白，不能当成缩放。
- 数字和状态文字预留稳定区域。暂停／运行、选中／未选中和个位进位不能改变按钮或卡片外框尺寸。
- 计时器示例在给定空间内完整展示，复用 `PageHandover` 交接不同排版；其他内容是否需要滚动由业务决定，不能用裁切隐藏必要操作。
- 每个显示器创建独立视图，业务状态共享。不要把一个 `FrameworkElement` 放入多个容器。

## 控件、焦点与反馈

圆形播放、暂停等操作复用 `ModuleControlButton` 和 `ModuleControl` 样式，文字按钮可复用 `PanelTextButton`。文字按钮应使用自己的合适宽度，不能套用只容纳图标的固定宽度。控件提供可辨识的悬停、按压、禁用和键盘焦点，使用 `AutomationProperties.SetName` 标注操作含义。

`RollingStepper` 提供加减、长按、纵向拖动、滚轮与键盘微调、双击输入；通过 `ValueChanged` 写入业务值。视图的 `IsAdjusting` 应反映控件的调整状态，`CancelAdjustment` 优先取消局部编辑。需要因布局重建控件时，用 `TakeDraft`／`RestoreDraft` 保留输入草稿，避免失焦意外提交。不要每次读数更新都重建输入框。

普通文本保留输入法组合输入、Tab 顺序和局部 Escape 行为。当前没有通用焦点租约 API，模组必须在释放时解除捕获、关闭自建弹层并解绑外部事件。

设置优先注册 `IModuleSettings`，让宿主统一绘制开关、滑条、选项、文本和颜色。文本在 Enter 或失焦时提交，Escape 恢复最近成功提交值；颜色复用宿主选择器，点击外部取消未提交修改。字段说明用于解释用户可见的行为，不展示实现细节。

## 动效与生命周期

| 变化 | 推荐行为 |
| --- | --- |
| 数字更新 | 更新 `RollingDigitsView`，不重建整页 |
| 相同布局换数据 | 就地更新；需要交接时用 `Handover.Dissolve` |
| 列数或卡片布局变化 | `PageHandover` 配合 `Handover.Morph` |
| 进入更深页面／返回 | `Handover.Forward`／`Back` |
| 单次内容进入 | `ModuleUi.Appear` |
| 岛外壳展开、收起、通知覆盖 | 由宿主完成；模组不重复驱动外壳 |

动效遵守 `ModuleMotion.Enabled`／`ModuleUi.AnimationsEnabled`。关闭动画时直接到达最终状态。连续操作从当前呈现继续，释放视图时结束自有动画和订阅。`PageHandover` 使用完调用 `Drop`，并放入视图清理。

页面可能在收起后仍挂载。显示用定时器使用 `EveryVisible`；计时进度、提醒阈值等业务继续归模组级管理。进入前后不能暂停业务或重新初始化会话。详细可见性规则见[宿主规则](host-rules.md)，实现方式见[开发配方](recipes.md)。

## 交付前检查

检查深浅主题、动画关闭、紧凑与展开、最小可用区域、缩放、多显示器、连续切换、键盘、中文输入、通知覆盖及返回。区分已执行与待人工确认的检查，不用一张静态截图代替交互验证。
