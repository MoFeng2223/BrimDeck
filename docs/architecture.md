# 架构

本文面向维护者，说明代码结构、刷新流程、媒体处理流程、数据文件和命令行参数。设计决定及其原因见 [decisions.md](decisions.md)。

## 项目结构

| 目录 | 内容 |
| --- | --- |
| `src/BrimDeck.Core` | 与界面无关的逻辑，目标框架 `net10.0`，不引用 WPF。设置与迁移、各来源的读取与解析、脚本沙盒、价格、用量汇总、歌词、媒体规则、更新逻辑、本地化。 |
| `src/BrimDeck` | WPF 应用，目标框架 `net10.0-windows10.0.19041.0`，仅 x64。窗口、面板、设置界面和动画。 |
| `src/BrimDeck/Native` | 与 Windows 和其他应用交互的适配层：窗口环境检测、托盘、自启动、虚拟桌面、SQLite 只读查询、Claude 桌面版登录解密、密钥加密、媒体来源、音频采集。 |
| `src/BrimDeck/Updates` | GitHub Releases 更新源。 |
| `tests/BrimDeck.Tests` | 核心测试（控制台程序），覆盖 Core 与部分应用逻辑。 |
| `tests/BrimDeck.MediaTests` | 网易云与 QQ 音乐适配器的测试，以及用 Jint 运行的网易云桥接脚本测试。全部使用临时文件和本地模拟服务，不需要真实播放器。 |
| `tests/BrimDeck.UpdateHarness` | 供 `scripts/Test-AppUpdate.ps1` 使用的更新测试程序。 |
| `installer/` | Inno Setup 安装脚本 `BrimDeck.iss`；`InnoSetup.csproj` 只用于通过 NuGet 下载固定版本的编译器。 |
| `scripts/` | `Publish-Release.ps1`（打包）、`Get-InnoSetup.ps1`、`Test-AppUpdate.ps1`（安装与更新端到端测试）、`icon/`（图标生成程序）。 |
| `releases/` | 各版本的更新说明 `<版本>.md`。 |

## 模块

### Core

| 文件 | 作用 |
| --- | --- |
| `Settings.cs` | `DeckSettings`、`AppEntry`（看板条目）、`SettingsStore`（读写与版本快照）。 |
| `SettingsMigrations.cs` | 按版本号排列的设置迁移步骤。 |
| `ProviderCatalog.cs` | 10 种配额来源的名称、分组、站点地址规范化、请求去重键。 |
| `UsageService.cs` | 刷新入口；读取 Claude、Codex、Cursor、Antigravity、ZCode。 |
| `LogReader.cs` | Claude Code 与 Codex 的 `*.jsonl` 会话记录解析（带文件缓存）；`QuotaParser` 解析各账户接口。 |
| `AntigravityUsage.cs`、`ZCodeUsage.cs` | Antigravity 对话数据库、ZCode 请求记录与凭据。 |
| `ClaudeDesktopCache.cs`、`ClaudeUsageHistory.cs` | 筛选 Claude 桌面版登录缓存；读取桌面版自己的 `plan-usage-history.json`。 |
| `ScriptProvider.cs` | Jint 脚本沙盒 `ScriptSession` 与手动来源的调度 `ConfiguredProviders`。 |
| `ProviderScripts.cs` | 智谱 GLM、Z.ai GLM、New API、Sub2API 的内置脚本和自定义模板。 |
| `UsageMetric.cs` | 指标模型与脚本返回值解析 `MetricParser`。 |
| `Usage.cs`、`Plans.cs` | `ProviderSnapshot`、`TokenEntry`、`Quota`；套餐名称解析。 |
| `DashboardUsage.cs`、`ModelUsage.cs` | 把配额快照和用量快照分别对应到条目；模型明细的筛选与汇总。 |
| `Pricing.cs` | OpenRouter 价格目录、手动价格、费用估算。 |
| `Music.cs`、`MusicLayout.cs` | 媒体数据模型；收起形态与音乐页的布局计算。 |
| `MediaPlaybackStack.cs`、`MediaRouting.cs`、`MediaSeekPreview.cs` | 当前来源的选择、命令分派与控制补充的合并、拖动进度的预览。 |
| `NeteaseLog.cs` | 网易云日志事件解析与播放状态推算。 |
| `Lyrics.cs`、`QqLyrics.cs` | 歌词查询与缓存（`LyricsService`）、LRC 解析。 |
| `EqualizerSignal.cs` | 由音频样本计算律动条的数值。 |
| `AppUpdates.cs`、`InstallerUpdates.cs`、`ApplicationVersion.cs` | 更新状态机、`update.json` 校验、安装包下载与校验。 |
| `Loc.cs` | 界面语言、系统语言检测、数据中中文标签的翻译。 |
| `ScreenPolicy.cs` | 前台窗口环境分类（桌面、最大化、无边框全屏、独占全屏）。 |

### WPF 应用

| 文件 | 作用 |
| --- | --- |
| `App.xaml.cs` | 启动：单实例、数据目录、错误记录、创建服务与窗口、托盘、`--settings`。 |
| `MainWindow.xaml(.cs)` | 悬浮面板：尺寸与位置、展开收起动画、悬停判定、定时刷新、配额提醒。 |
| `UsageSynchronization.cs` | 自动同步开关与同步按钮。 |
| `UsagePanel.cs` | AI 用量页的列、配额分页、字号档位。 |
| `ModelDetails.cs`、`ModelDateWindow.cs` | 模型明细页与日期选择。 |
| `MainWindow.Music.cs`、`MainWindow.SourceMenu.cs` | 音乐页、收起形态的音乐内容、来源菜单、完整控制入口。 |
| `MusicVisuals.cs`、`MusicControls.cs` | 律动条、滚动文字、配额环往返滚动、进度条与按钮。 |
| `NeteaseControlWindow.cs` | 启用网易云完整控制前的确认窗口。 |
| `Settings*.cs`、`ScriptEditor.cs` | 设置窗口各页；脚本编辑器（AvalonEdit）。 |
| `PromptTimer.cs` | 在线程池计时、向界面线程投递的计时器，用于悬停等需要准时的操作。 |
| `IslandBorder.cs`、`CompactStylePreview.cs`、`UI.cs` | 面板外形绘制、设置中的形态预览、通用界面辅助。 |

### Native

| 文件 | 作用 |
| --- | --- |
| `WindowsHost.cs` | 每 400 毫秒检测前台窗口环境；主屏幕与工作区尺寸；面板窗口样式。 |
| `VirtualDesktopPresence.cs` | 把面板窗口固定到所有虚拟桌面。 |
| `TrayIcon.cs`、`StartupRegistration.cs`、`SettingsChrome.cs` | 托盘菜单、开机自启动（`HKCU\...\Run`）、设置窗口标题栏主题。 |
| `EscapeDismissal.cs`、`PointerPressObserver.cs` | 展开时用 Esc 收起；弹出层打开时观察鼠标按下。 |
| `DesktopSources.cs` | 用系统自带的 `winsqlite3` 以只读方式查询 SQLite；通过 WMI 与 `netstat` 查找 Antigravity 本地服务。 |
| `ClaudeDesktopReader.cs` | 用当前用户的 DPAPI 在内存中解密 Claude 桌面版登录缓存。 |
| `ProviderSecrets.cs` | `secrets.dat` 的 DPAPI 加密读写。 |
| `MediaSessions.cs` | 媒体来源的汇总与路由（见下文）。 |
| `NeteaseSource.cs`、`NeteaseLogReader.cs`、`NeteaseCdpConnection.cs`、`NeteaseBridgeScript.cs` | 网易云的进程发现、日志读取、调试端口连接与页面内桥接脚本。 |
| `QqMediaConnection.cs` | QQ 音乐命名管道连接。 |
| `AudioLevelMeter.cs`、`ProcessLoopbackCapture.cs` | 律动条的音频输入：按进程采集，不可用时读取会话峰值电平。 |

## 刷新流程

1. `MainWindow` 的计时器每 60 秒触发一次刷新。计时器只在"需要配额"（启用了 AI 用量页，或当前收起形态显示配额环）且自动同步开启时运行。同样在这一条件下，启动、更改影响数据的设置或切换语言后会立即刷新一次。模型明细页的同步按钮和选择更早日期时的补读不受自动同步开关限制。
2. 同一时间只进行一次刷新。刷新进行中再次请求时，最多排队一次后续刷新。
3. 每次刷新并行执行两件事：`Pricing.RefreshAsync`（每个进程第一次调用时联网，之后 24 小时内直接返回），以及 `UsageService.RefreshAsync`。
4. `UsageService` 取所有启用条目的配额来源与统计应用的并集。自动读取的来源各读取一次；手动来源交给 `ConfiguredProviders`，按"来源 + 站点 + 脚本 + 密钥"分组，每组请求一次，最多 4 组同时进行。
5. 每个来源返回一个 `ProviderSnapshot`。`DashboardUsage` 为每个条目分别取配额快照和用量快照，两者互不替代。
6. `MainWindow.SetSnapshots` 检查配额阈值变化，然后重绘收起形态与展开面板。

读取失败时的处理：

- Claude：保留本次运行中最近一次成功读取的配额（仅在内存中），或改用桌面版自己的用量记录；登录失效时不显示旧值。
- 手动来源：保留同一请求键最近一次成功的结果并标明读取时间；收到 HTTP 429 时，在 `Retry-After` 指定的时间之前不再请求（未指定时为 60 秒）。
- ZCode：Start Plan 与 MCP 每日额度的慢请求在后台完成，期间使用同一账户最近一次成功的结果；换账户时丢弃。Coding Plan 失败时显示同一账户上次成功读取的数据。
- 除上述情况外不另设重试，下一个 60 秒周期再次读取。

## 媒体处理流程

1. `MediaSessions` 同时使用三类输入：Windows 系统媒体会话（GSMTC）、网易云连接 `NeteaseSource`、QQ 音乐连接 `QqMediaConnection`。
2. `BuildRoutes` 为每个播放器建立一条路由，每条路由只有一个信息来源：
   - 网易云：日志模式下，网易云自己的系统媒体会话作为"控制补充"，只提供播放/暂停、上一首、下一首三个按钮（`MediaRouting.Merge`）；完整控制模式下只使用调试端口连接。
   - QQ 音乐：管道连接可用时使用管道；管道断开且存在 QQ 音乐的系统媒体会话时，整条路由改用系统媒体会话。
   - 其余系统媒体会话各自成为一条路由。
3. `MediaPlaybackStack` 决定当前显示的来源：最近开始播放的在前，短暂停止（3 秒内恢复）的来源保留原位置。用户手动选择或固定的来源优先。
4. 播放命令经 `MediaRouting.DispatchAsync` 分派：先发给专属连接；只有确认"未发送"时才改用系统媒体会话，已发送或结果不确定时不重复发送。
5. 歌词由 `LyricsService` 查询；律动条由 `AudioLevelMeter` 采集当前来源播放器的声音，经 `EqualizerSignal` 计算。

网易云的状态：

- `NeteaseSource` 每 5 秒检查一次网易云进程；每 30 秒从卸载信息或 `orpheus` 协议登记中查找一次安装位置。进程带有调试端口参数时尝试完整控制连接，否则使用日志模式。
- 日志模式：`NeteaseLogReader` 在日志文件（`%LOCALAPPDATA%\NetEase\CloudMusic\cloudmusic.elog`）变化时读取新增内容，由 `NeteaseLog` 推算曲目、状态和进度。解析器只保留所需字段，不保存原始日志或资源地址。
- 完整控制：用户确认后，通过客户端的 `--orpheus-startup=exit-process` 正常退出，再以 `--remote-debugging-port=21631 --remote-debugging-address=127.0.0.1` 启动。`NeteaseCdpConnection` 每次连接前检查该端口只由网易云进程在回环地址上监听。连接失败时恢复日志模式。

## 数据文件

| 位置 | 写入者 | 内容 |
| --- | --- | --- |
| `%LOCALAPPDATA%\BrimDeck\settings.json` | `SettingsStore` | 设置、`AppVersion`、旧版本设置快照 `Snapshots`。先写临时文件再替换。 |
| `%LOCALAPPDATA%\BrimDeck\secrets.dat` | `ProviderSecrets` | 以条目 `InstanceId` 为键、DPAPI（CurrentUser）加密的 API 密钥。 |
| `%LOCALAPPDATA%\BrimDeck\model-prices.json` | `Pricing` | OpenRouter 价格缓存与获取时间。 |
| `%LOCALAPPDATA%\BrimDeck\manual-model-prices.json` | `Pricing` | 手动添加的价格。无法读取时保留原文件并停止写入。 |
| `%LOCALAPPDATA%\BrimDeck\update-prompt.txt` | `AppUpdates` | 已弹出过提示的版本号。 |
| `%LOCALAPPDATA%\BrimDeck\updates\` | `InstallerUpdateBackend` | 下载的安装包与待安装记录 `pending.json`。 |
| `%LOCALAPPDATA%\BrimDeck\crash.log` | `App` | 未处理的错误；超过 1 MB 时清空后重新记录。 |

数据目录由 `Environment.GetFolderPath(LocalApplicationData)` 按当前用户解析，不写死用户名。

## 命令行参数

| 参数 | 作用 |
| --- | --- |
| `--settings` | 启动后打开设置窗口。 |

BrimDeck 只允许每个 Windows 用户运行一个实例（互斥体 `Local\BrimDeck.Instance.<用户名>`）。再次启动时，新进程通知已运行的实例打开设置，然后退出。
