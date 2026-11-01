# 剪贴板与文件中转：契约及源码导航

本文面向维护者，集中说明两个模块的数据归属、运行模式、保存与释放顺序、交互边界及源码入口。以当前源码为准；第三方逐项移植过程见 [Edge-Drop 移植索引](../../third_party/EdgeDrop.PORTING.md)。本文不是完整上游应用功能清单，也不是性能验收声明。

## 1. 架构与数据归属

| 层次 | 剪贴板 | 文件中转 |
| --- | --- | --- |
| 应用所有者 | BrimDeck `App` 创建服务、注册入口、统一退出 | 同一个 `App` 托管 `DropShelf.AppController`，不启动第二个 Application 或托盘 |
| 唯一运行时数据 | `ClipboardService` 持有一个 `ClipboardHistory` | 原版 `ShelfManager` 管理各个 `ShelfWindow.ViewModel`；`ShelfTrayAdapter` 指向当前架 |
| 呈现 | 岛内 `ClipboardView`、独立库窗口和单条预览浮窗引用同一历史 | 岛内卡片与当前浮动架引用同一个 `ShelfViewModel.Items` |
| 持久化 | `ClipboardHistoryStore` 保存历史快照 | 当前架普通路径投影交给 `FileTrayStore`；原版设置与最近关闭历史由 `AppController` 管理 |
| 共享资源 | 主题、图像线程、解码缓存及受控附件缓存有各自所有权 | 释放本模块持有的引用，不销毁另一模块仍在使用的共享资源 |

必须保持：

- 不为岛内、独立窗口、多显示器或搜索结果复制出另一份可变历史／置物架集合。筛选、排序与虚拟化是投影，不是新的业务数据源。
- `src/BrimDeck.Core/FileTray.cs` 中的旧模型目前用于规则、兼容迁移和普通路径快照；不是托管置物架的第二份实时内容所有者。
- 设置、语言、主题、开机自启、托盘及退出流程统一归 BrimDeck；原版置物架的自启设置必须委托宿主，不另建启动注册。
- `BrimDeck.Shelves` 保留程序集名和命名空间 `DropShelf`，用于兼容原版资源 URI；这不表示另起一个应用。

## 2. 运行模式、采集许可与页面可见性

两个模块各有独立的 `ModuleMode`，存放在 `Settings.Clipboard.Mode` 与 `Settings.Tray.Mode`。`Full / Performance / Off` 的枚举值需要保持持久化兼容；缺失或无效值兼容为 `Full`。

| 模式 | 运行与交互契约 |
| --- | --- |
| 完整 `Full` | 保留现有核心功能及用户的完整模式动效、悬停、灵敏度与手势偏好 |
| 性能 `Performance` | 保留显式唤出、搜索、收藏、复制／回贴、拖放、显式预览；停用模块装饰动画、自动悬停／手势呼出和对应常驻轮询，按需降低呈现像素预算 |
| 关闭 `Off` | 在确认、操作完成及保存屏障之后，释放模块监听、窗口、视图和运行时持有资源；保留已保存数据与原文件，不以隐藏窗口冒充关闭 |

这些维度不能混为一个开关：

- 剪贴板 `Enabled` 是采集许可，默认不启用；打开页面、管理窗口或切换运行模式不能替用户同意采集。
- `ClipboardPage` 决定岛内页面是否显示；隐藏不等于关闭采集。显式剪贴板快捷入口可以重新显示该页，但不启用记录。
- 文件中转 `Placement` 为 `Notch / Floating / Both`，决定岛内／浮动位置；不是模块启停开关。
- `Tray.Enabled` 仅保留旧配置兼容，不再拥有当前模块启停或页面可见性。

页面判断统一由 [`DeckPages.cs`](../../src/BrimDeck.Core/DeckPages.cs) 实现：

```text
剪贴板页可见 = Clipboard.Mode != Off && ClipboardPage
文件中转页可见 = Tray.Mode != Off && Placement 属于 Notch 或 Both
```

默认模式为完整、剪贴板页显示、文件中转两处显示，但剪贴板记录仍未获许可。页面顺序来自保存的 `PageOrder`，隐藏页面回退到首个可见页；全部关闭时回退用量页展示关闭状态。不要因新页面默认可见而删除页面顺序或全关闭测试。

### 性能策略的作用域

- 策略入口为 [`ModuleRuntimePolicy.cs`](../../src/BrimDeck.Core/ModuleRuntimePolicy.cs)，只降低可再生的展示资源，不缩减原始图片、文件内容或历史载荷。
- 性能模式下卡片／预览／缩略图预算分别为 256／768／128 像素；完整模式为 400／1000／512。性能模式缓存预算常量为 16 MiB，不能据此声称整个进程内存不超过该值。
- `MainWindow.Modules.cs` 将减少动效策略作用到模块与岛体；性能页使用事件驱动窗口状态，显式移动期间仍可临时跟踪指针，结束后停止。
- 不为优化模块而全局关闭音乐、系统监控或用量同步。功能检查通过也不等于完整模式无性能回退；资源比较必须注明二进制、场景、时长和采样方法。

## 3. 生命周期与安全关闭

模式切换入口为 [`App.Modules.cs`](../../src/BrimDeck/App.Modules.cs) 的 `ChangeModuleModeAsync`；应用退出通过同文件的 `PrepareModuleExitAsync` 进入保存屏障。切换过程串行，退出或切换中的入口不能重新接收操作。

### 冷关闭

- `InitializeClipboard` 在 `Off` 时不构造 `ClipboardService`。
- `InitializeShelves` 在 `Off` 时不构造 `AppController`／`ShelfTrayAdapter`，也不调用原版 `Start`。
- 主题等公共宿主配置可独立存在；不能将共享配置仍在误判为模块偷偷运行。

### 热关闭剪贴板

1. 存储目录正在迁移时不开始关闭；存在未保存草稿或非持久化会话历史时先确认。
2. 停止待执行回贴，暂停采集，关闭待展开预览和输入交互，等待该历史的拖出操作结束。
3. 等待 `ClipboardService.FlushAsync`；失败则保留原服务和记录，恢复可重试交互并提示，不能先清空数据再报错。
4. 成功后释放岛内视图、库窗口、预览窗口、监听、调度器和本模块图像引用；最后应用新模式。

### 热关闭文件中转

1. 检查设置草稿，停止接收新操作，取消待展开与内部重排。
2. 等待 `ShelfManager.Operations.WhenIdle`。操作租约覆盖载荷接收、原生拖放和结果应用的完整边界，不能用进度条的 `IsBusy` 或固定延时替代。
3. 有内容时提示恢复范围，保存当前架的普通路径快照，再记录各打开架的最近历史并等待控制器刷新完成。
4. 保存失败或用户取消时保留会话，恢复接收；成功后释放设置视图、岛内视图、适配器、控制器及本模块图像引用。
5. **先解绑 `ShelfTray.PersistedChanged` 和适配器，再销毁原版窗口／控制器。** 反向操作可能把销毁时清空的模型保存为空，覆盖可恢复路径。

关闭模块不是完整会话备份：当前架快照只保存普通文件／文件夹原路径及相应元数据；最近关闭历史最多 10 个架、每架 500 项，文本、链接、临时附件以及其他架的固定／加入时间不能完整恢复。不可将此描述为所有内容都能无损重启恢复。

共享图像工作线程通过 `HostedResources.AcquireImaging()` 分别取得引用；附件缓存也有独立持有者。当前 `ConfigureCacheRoot` 对同一规范化目录直接返回，若仍有持有者则禁止切换到不同目录。模块重新启用不得重置其他持有者或清理其附件。

## 4. 文件、存储与隐私边界

### 普通原文件与受控附件

- 普通文件始终保留原路径；移除卡片、清空历史／置物架、关闭模块本身不删除原文件，也不先复制全部普通文件到缓存。
- 用户显式触发的原版文件操作仍遵循其复制／移动等选项；不要将“移除条目不删文件”误读为禁止用户主动文件操作。
- 虚拟附件、像素载荷与允许缓存的易失图片才进入受控缓存。缓存持有关系覆盖接收结果、历史／置物架和进行中的操作；已导出的附件需保留接收方读取宽限，不能一拖出就删除。
- 当前共享附件根由宿主设置为系统临时目录下 `BrimDeck/Shelves/Attachments`。这不是永久用户文档目录，不能承诺临时附件长期保存。
- 缩略图、可用性、大小或类型名刷新只更新显示；不能触发普通路径快照重写。内容、固定或顺序变更才发送 `PersistedChanged`。

### 剪贴板持久化

- `ClipboardHistoryStore` 保存 `clipboard-history.bin`；临时替换文件为同名 `.tmp`。整个快照（含图像载荷）通过当前 Windows 用户的 DPAPI 加密，再替换目标文件，不额外保存明文摘录或历史缩略图。
- DPAPI 不是可任意跨 Windows 用户移植的备份格式。普通路径快照、原版置物架设置／最近历史及主动导出的附件并不因此自动获得相同加密保证。
- `MaximumItems == 0` 只取消条目数量上限；不取消单条载荷、保留天数、像素或存储安全限制。当前历史存储的明文序列化／读取文件大小检查为 48 MiB。
- `ClipboardService` 按需加载历史；变化合并到 400 ms 保存队列，后台串行写入。展示偏好调整不应重写历史或重复注册监听。到期清理按下一次过期时间安排，而非无条件每分钟扫描。
- 关闭持久化会删除本地历史文件，但当前内存会话仍可使用；之后关闭模块可能丢失这段会话，因此需要确认。
- 自定义目录无效时显示错误，不静默读取或覆盖默认目录。迁移顺序是排空旧写入、准备目标加密副本、持久保存设置中的路径指针，再删除旧副本；目标已有历史则拒绝覆盖，失败保留原历史。旧副本清理失败必须单独反馈。

### 本地内容安全

链接解析和图片预览使用本地规则，不抓取远程缩略图、不执行 HTML／SVG。来源应用筛选与隐藏仅是导航偏好，不是采集排除名单。隐藏标签不删除记录、不停用该来源采集；清空指定应用按精确来源处理，不能受当前搜索结果限制而只删一部分。

## 5. 交互与呈现契约

- **不抢外部焦点**：岛内普通鼠标浏览保持非激活；搜索／编辑等显式输入才临时借用焦点。`WindowsHost.SetOverlayInput` 通过 `WM_MOUSEACTIVATE` 管理非激活点击，不恢复会破坏 WGC 捕获的旧 `WS_EX_NOACTIVATE` 方案。
- **回贴有接收边界**：目标必须是外部窗口，不能回贴进自身。独立预览浮窗保留各自的接收目标。权限或焦点限制导致自动回贴失败时，保留已复制内容并提示手动 Ctrl+V，不声称回贴成功。
- **拖动与单击分离**：开始或尝试拖出后不再当作单击回贴；自身导出回流与收藏夹内部整理不得当作新的外部接收或重复分享。取消拖动不计成功复用。
- **计数与时间分离**：`CopyCount` 记录重复复制及成功复用，与 `UseCount` 和采集时间分开；编辑、内部整理及取消操作不伪增次数。按次数排序使用稳定 ID 与确定性次序，不用整列重建冒充重排。
- **预览所有权分离**：自动悬停预览属于视图，可按最新卡片重定向并在隐藏时取消；手动「预览浮窗」属于应用窗口登记，关闭岛内视图不关闭它，同一条记录复用浮窗，删除记录才关闭对应浮窗。
- **统一布局资源**：岛内两模块复用顶部 Dock 契约，参考 1080×210 DIP、40 DIP 头部、210×146 卡片、10 DIP 间距和两侧 14 DIP 留白；独立管理窗口与原版浮动架不强制套用此布局。
- **自适应而非截图常量**：网格按真实视口分配等宽列，保留滚动锚和稳定卡片；设备像素、WPF DPI 和本体缩放不得重复应用。窄屏固定操作保持可达。
- **共用宿主动效**：页面变形、重排、菜单和通知接入本体主题与 `Soft` 等现有机制；禁动画／性能模式立即就位并释放快照。结果胶囊在当前面板右下角显示 3 秒，替换重新计时，隐藏清空且不抢点击。

## 6. 源码结构与修改入口

以下路径均从仓库根开始；按职责导航，不要求在相邻页面再造一套服务。

### Core：不依赖窗口的规则与格式

| 文件 | 职责 |
| --- | --- |
| [`src/BrimDeck.Core/ClipboardHistory.cs`](../../src/BrimDeck.Core/ClipboardHistory.cs) | 设置、历史条目、快照、去重／计数、保留与收藏夹等历史操作 |
| [`src/BrimDeck.Core/ClipboardLibrary.cs`](../../src/BrimDeck.Core/ClipboardLibrary.cs) | 库查询、分类和展示所需规则 |
| [`src/BrimDeck.Core/ClipboardContentTypes.cs`](../../src/BrimDeck.Core/ClipboardContentTypes.cs) | 内容类型识别规则 |
| [`src/BrimDeck.Core/ModuleRuntimePolicy.cs`](../../src/BrimDeck.Core/ModuleRuntimePolicy.cs) | 独立三态与展示预算 |
| [`src/BrimDeck.Core/DeckPages.cs`](../../src/BrimDeck.Core/DeckPages.cs)、[`Settings.cs`](../../src/BrimDeck.Core/Settings.cs) | 页面可见性／顺序／尺寸及宿主设置 |
| [`src/BrimDeck.Core/ClipboardDockLayout.cs`](../../src/BrimDeck.Core/ClipboardDockLayout.cs)、[`ShelfDockLayout.cs`](../../src/BrimDeck.Core/ShelfDockLayout.cs) | 各模块独立的 Dock 尺寸迁移与恢复 |
| [`src/BrimDeck.Core/FileTray.cs`](../../src/BrimDeck.Core/FileTray.cs) | 旧列表规则与 `file-tray.json` 普通路径快照，非实时共享模型 |

### BrimDeck：服务、窗口与 Windows 边界

| 文件／文件族 | 职责 |
| --- | --- |
| [`src/BrimDeck/App.Modules.cs`](../../src/BrimDeck/App.Modules.cs) | 切换事务、确认、保存屏障、失败恢复及共享资源初始化 |
| [`src/BrimDeck/App.Clipboard.cs`](../../src/BrimDeck/App.Clipboard.cs) | 剪贴板服务、快捷入口、库／预览窗口、回贴目标与目录迁移设置提交 |
| [`src/BrimDeck/ClipboardService.cs`](../../src/BrimDeck/ClipboardService.cs) | 按需加载、监听、保存队列、到期调度、目录迁移与存储统计 |
| [`src/BrimDeck/App.Shelves.cs`](../../src/BrimDeck/App.Shelves.cs)、[`App.ShelfTheme.cs`](../../src/BrimDeck/App.ShelfTheme.cs) | 原版控制器托管、当前架路径投影、主题和统一自启／退出接入 |
| [`src/BrimDeck/MainWindow.Modules.cs`](../../src/BrimDeck/MainWindow.Modules.cs) | 岛内视图释放、输入恢复、性能页呈现及事件驱动外壳 |
| [`src/BrimDeck/MainWindow.Clipboard.cs`](../../src/BrimDeck/MainWindow.Clipboard.cs)、[`MainWindow.ClipboardHeader.cs`](../../src/BrimDeck/MainWindow.ClipboardHeader.cs) | 岛内页入口、输入生命周期与公共页眉 |
| [`src/BrimDeck/ClipboardView.cs`](../../src/BrimDeck/ClipboardView.cs) 及同名前缀分文件 | `Dock` 工具栏／横向布局、`Library` 整理、`Actions` 操作、`Preview` 悬停、`Sort` 重排、`Notice` 通知、`Emoji` 本地表情 |
| [`src/BrimDeck/ClipboardLibraryWindow.cs`](../../src/BrimDeck/ClipboardLibraryWindow.cs)、[`ClipboardPreviewWindows.cs`](../../src/BrimDeck/ClipboardPreviewWindows.cs)、[`ClipboardPreviewWindow.cs`](../../src/BrimDeck/ClipboardPreviewWindow.cs) | 完整管理窗口、预览浮窗登记与单条窗口 |
| [`src/BrimDeck/ClipboardCard.cs`](../../src/BrimDeck/ClipboardCard.cs)、[`ClipboardPreview.cs`](../../src/BrimDeck/ClipboardPreview.cs)、[`ClipboardDragDrop.cs`](../../src/BrimDeck/ClipboardDragDrop.cs) | 卡片呈现、本地内容预览、原生拖出与历史拖放租约 |
| [`src/BrimDeck/ClipboardApplicationMenu.cs`](../../src/BrimDeck/ClipboardApplicationMenu.cs)、[`ClipboardCollectionAppearance.cs`](../../src/BrimDeck/ClipboardCollectionAppearance.cs) | 两种宿主共用的应用动作与收藏夹外观编辑 |
| [`src/BrimDeck/MainWindow.FileTray.cs`](../../src/BrimDeck/MainWindow.FileTray.cs) 及 `.Dock.cs`／`.Notice.cs`、[`ShelfIslandCard.cs`](../../src/BrimDeck/ShelfIslandCard.cs) | 共享置物架的岛内投影、工具栏、重排、可见缩略图申报及通知 |
| [`src/BrimDeck/SettingsClipboard.cs`](../../src/BrimDeck/SettingsClipboard.cs) 及 `.Storage.cs`、[`SettingsShelf.cs`](../../src/BrimDeck/SettingsShelf.cs)、[`SettingsModules.cs`](../../src/BrimDeck/SettingsModules.cs) | 本体设置、存储配置、原版设置嵌入与模式切换 UI |
| [`src/BrimDeck/Native/ClipboardListener.cs`](../../src/BrimDeck/Native/ClipboardListener.cs)、[`ClipboardHistoryStore.cs`](../../src/BrimDeck/Native/ClipboardHistoryStore.cs)、[`ClipboardPaste.cs`](../../src/BrimDeck/Native/ClipboardPaste.cs) | 系统采集、DPAPI 历史文件、外部窗口激活及回贴 |
| [`src/BrimDeck/Native/WindowsHost.cs`](../../src/BrimDeck/Native/WindowsHost.cs)、[`WindowContextEvents.cs`](../../src/BrimDeck/Native/WindowContextEvents.cs)、[`FileShareService.cs`](../../src/BrimDeck/Native/FileShareService.cs) | 非激活输入、窗口状态事件与系统分享边界 |

### BrimDeck.Shelves 与第三方来源

- [`src/BrimDeck.Shelves/BrimDeck.Shelves.csproj`](../../src/BrimDeck.Shelves/BrimDeck.Shelves.csproj) 链接编译 `third_party/DropShelf/src/DropShelf` 的原版源文件及 XAML，明确排除上游 `App.xaml`／`App.xaml.cs`；修改原版功能时需注意文件实际位于该目录，而非臆造本地副本。
- [`ShelfTrayAdapter.cs`](../../src/BrimDeck.Shelves/ShelfTrayAdapter.cs)：当前架模型适配、属性通知与持久化通知分离。
- [`ShelfIslandDropTarget.cs`](../../src/BrimDeck.Shelves/ShelfIslandDropTarget.cs)、[`ShelfItem.Hosting.cs`](../../src/BrimDeck.Shelves/ShelfItem.Hosting.cs)：岛内 OLE 接收和条目宿主扩展。
- [`EmbeddedHostOptions.cs`](../../src/BrimDeck.Shelves/EmbeddedHostOptions.cs)：宿主回调与配置边界；[`HostedResources.cs`](../../src/BrimDeck.Shelves/HostedResources.cs)：样式范围、主题与图像线程引用；[`DecodedImageCache.cs`](../../src/BrimDeck.Shelves/DecodedImageCache.cs)：解码缓存；[`OperationTracker.cs`](../../src/BrimDeck.Shelves/OperationTracker.cs)：完整操作租约。
- [`Views/ShelfToolWindow.cs`](../../src/BrimDeck.Shelves/Views/ShelfToolWindow.cs)：共享小窗口外壳；同目录 `ShelfSettingsContent.xaml` 及分文件：嵌入本体的原版设置内容。
- [`third_party/DropShelf`](../../third_party/DropShelf)：2.6.7 原版与宿主适配的物理源码，保留 MIT 许可。`AppController`、`ShelfManager`、`ShelfViewModel`、`VirtualFileDropService`、`ImagingThread` 等需在此查找。
- [`third_party/EdgeDrop`](../../third_party/EdgeDrop)：固定 v0.4.0 快照，不运行 Electron 或安装脚本；WPF 翻译和图形来源见 [`EdgeDrop.PORTING.md`](../../third_party/EdgeDrop.PORTING.md)、[`EdgeDrop.port.json`](../../third_party/EdgeDrop.port.json)、[`EdgeDropEmoji.port.json`](../../third_party/EdgeDropEmoji.port.json)。保留相关许可证，不将图形数据与运行时代码混为同一种移植。

## 7. 验证入口与证据边界

所有命令从仓库根运行，需要 .NET 10；Windows 原生与真实宿主检查需要 Windows／WPF。按顺序构建与运行，任一步非零即停止，不通过重试直到偶然成功来替代定位。依赖已恢复时可使用以下 `--no-restore`；新环境需先正常恢复项目依赖。

### Core 全量与模块规则

```powershell
dotnet run --project tests/BrimDeck.Tests -c Release --no-restore
# 已构建时对应 CI 的执行入口：
dotnet run --project tests/BrimDeck.Tests -c Release --no-build
# 只查模块策略和历史：
dotnet run --project tests/BrimDeck.Tests -c Release --no-build -- --only modules
```

`ModulePolicyTests` 已接入默认全量流程，覆盖模式 × 位置的可见性、页面顺序、回退及采集许可独立性。`SystemMonitorTests.Pages` 显式隔离默认可见模块，保留原页面顺序、尺寸与全关闭断言。2026-11-01 本地 Release 全量记录为 **2556 checks passed、退出码 0**；这是本地结果，不替代 GitHub CI 状态。

### Windows 适配层

```powershell
dotnet build tests/BrimDeck.WindowsTests/BrimDeck.WindowsTests.csproj -c Release --no-restore -m:1 --tl:off
dotnet run --project tests/BrimDeck.WindowsTests -c Release --no-build -- --only clipboard
dotnet run --project tests/BrimDeck.WindowsTests -c Release --no-build -- --only shelves
dotnet run --project tests/BrimDeck.WindowsTests -c Release --no-build -- --only module-resources
dotnet run --project tests/BrimDeck.WindowsTests -c Release --no-build -- --only capture
```

其他定向选择包括 `clipboard-preview`、`clipboard-images`、`clipboard-storage`、`clipboard-toolbar`、`clipboard-applications`、`clipboard-collections`、`file-share` 与 `edgedrop`，具体参数匹配见 [`Program.cs`](../../tests/BrimDeck.WindowsTests/Program.cs)。

### 真实生产 WPF 宿主

```powershell
dotnet build tests/BrimDeck.Dock.Tests/BrimDeck.Dock.Tests.csproj -c Release --no-restore -m:1 --tl:off
dotnet run --project tests/BrimDeck.Dock.Tests -c Release --no-build -- ../output/module-contract-check --only-module-lifecycle
dotnet run --project tests/BrimDeck.Dock.Tests -c Release --no-build -- ../output/module-contract-check --only-shelf
```

宿主使用生产 `App`／编译后的 `MainWindow.xaml`，合成历史和隔离文件；截图输出目录由第一个参数指定。其他入口包括 `--only-preview-window`、`--only-toolbar`、`--only-applications`、`--only-storage` 及耗时更长的 `--only-performance`。必须同时检查退出码和相应 `RESULT` 完成行，不能仅凭进程消失认定测试完成。

测试代码按层分布：

- [`tests/BrimDeck.Tests`](../../tests/BrimDeck.Tests)：核心规则、历史、库和设置迁移。
- [`tests/BrimDeck.WindowsTests`](../../tests/BrimDeck.WindowsTests)：Windows 消息、采集格式、存储、拖放适配、共享模型、资源边界。
- [`tests/BrimDeck.Dock.Tests`](../../tests/BrimDeck.Dock.Tests)：实际生产岛体、生命周期、主题／缩放、预览窗口和工具栏交互。
- [`tests/BrimDeck.Shelves.Tests`](../../tests/BrimDeck.Shelves.Tests)：原版置物架的隔离检查。
- [`tests/BrimDeck.Benchmark.Tests`](../../tests/BrimDeck.Benchmark.Tests)：独立基准入口；需要固定版本及测试条件，不和普通功能回归混为一项。

安全与验收边界：

- 自动检查使用合成历史、自建测试文件和隔离目录，不读取真实系统剪贴板、用户历史、聊天记录，也不向 QQ／微信等真实接收程序发送内容。
- 面板 150% 缩放渲染不等于真实 OS DPI 切换；离屏多窗口不等于硬件多屏，截获的拖放／回贴不等于外部应用端到端接收。
- 历史移植文档中的检查次数是当轮记录，不代表当前全部套件都重跑。报告必须注明本次实际命令、结果和未覆盖范围。
- 本地 Core 通过不等于发版全流程通过；构建、其他 CI 检查、人工场景和版本发布仍有各自边界。

## 8. 尚未实现的后续方向

以下只作规划，不应写入当前能力或持久化兼容承诺：

1. 剪贴板时间线组件与独立每日回顾，兼顾美观、查找和复用。
2. 置物架内部的文件夹／分组组织；当前“接收真实文件夹路径”不等于已有此功能。
3. 可在鼠标旁自定义唤出的简洁剪贴板小面板；不同于完整库窗口和单条预览浮窗。
4. 精简冗余悬浮提示，改进图片的自适应完整展示。
5. 加强稳定性、异常路径、真实 DPI／多屏及外部接收端测试。
6. 围绕日常高频操作完善键盘、批量整理和实用流程。
