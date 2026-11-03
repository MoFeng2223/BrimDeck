# 计时器示例模组

这是完整可安装的原生模组，包含番茄钟、倒计时和秒表。它是宿主默认随附的参考实现，使用与第三方相同的发现、内容确认、加载上下文和公开 SDK，不由主程序直接创建。

需要契约 **1.5**、WPF 和 .NET 10。入口为 `TimerModule`；独立领域库 `Core/` 管理计时状态机，没有 WPF、宿主 Core 或桌面程序依赖。

## 能力

- 番茄钟：专注、短休息、长休息、轮次、暂停继续；休息完成后等待下一轮。
- 倒计时：预设、时分秒编辑、暂停继续、完成状态。
- 秒表：暂停继续、重置、分段记录及有界分页。
- 模组设置：默认模式；三个组件的独立参数、启停、图标与拖拽顺序。
- 岛内呈现：展开控制条、紧凑计时、阶段确认和滚动数字；复用公共控件、字体、主题与交接动效。
- 一个模组会话跨页面、跨显示器共享；离开页面不会停止计时。睡眠和模组停用时暂停，重新启动恢复为暂停状态。

## 三个扩展入口

`context.Platform()` 返回公开平台入口，三个部分各司其职：

```csharp
var platform = context.Platform();
platform.Registries.Register("timers", new ModulePage("计时器", "Timers", CreatePage));
platform.Events.On<ModuleSuspending>(_ => PauseSession(), ModuleEventPriority.High);
var presentation = platform.Services.Get<IModulePresentation>();
```

上面是入口关系示意；可编译的完整实现见 [TimerModule.cs](TimerModule.cs)。注册表返回带完整 ID 的撤销句柄；事件按优先级和登记顺序执行；服务按清单能力、权限和本次激活范围提供。停用时三者都由宿主管理清理。

`ModuleWidget` 的 `ReadOrder`、`MoveTo` 和 `IconPath` 供宿主通用组件管理页与侧栏使用。模组保存业务顺序，宿主复用已有拖拽交互。`IModulePresentation.Invalidate()` 合并请求刷新贡献呈现，不重新激活模组，不在每个计时脉冲调用。

没有计时器专用宿主服务、友元 UI 权限、私有窗口访问或在侧栏写死的三个入口。`InternalsVisibleTo` 仅供回归测试访问本模组内部类型。

## 构建和安装

在仓库根目录执行，先按本机需求配置 .NET、NuGet 和临时文件缓存：

```powershell
dotnet build mods/timers/BrimDeck.Timers.csproj -c Release --artifacts-path E:/BrimDeck-build -t:GetModulePackage
```

将 `E:/BrimDeck-build/bin/BrimDeck.Timers/release/package/timers/` 整个目录放入实际应用数据目录的 `mods/`。只分发 `module.json`、`BrimDeck.Timers.dll`、`BrimDeck.Timers.Core.dll` 和 `BrimDeck.Timers.deps.json`；不要附带共享 SDK DLL。

所有仓库示例共用 [ModulePackage.targets](../../build/ModulePackage.targets)。主程序构建通过同一个目标把随附模组放进分发目录的 `mods/`，首次运行由通用安装器复制到应用数据的 `mods/`，之后仍走普通加载流程。

## 数据和边界

业务偏好和会话分别位于 `modules-data/timers/preferences.json`、`session.json`，通过 `context.Storage` 读写。显示更新不写盘；显式状态变更及停用保存快照。损坏快照在没有新计时操作时保持原文件，问题记入模组诊断。

原实验版宿主 `MiniApps` 中的计时参数不再作为此模组的数据源；旧未知字段在宿主设置中原样保留。本轮不自动读取旧内置计时器的数据目录。清理测试数据或卸载模组都不会要求主程序加载计时类型。

完整契约见 [API 参考](../../docs/zh-CN/modules/api.md)和[平台规范](../README.md)。
