# Edge-Drop → BrimDeck WPF 移植索引

固定来源为 Edge-Drop 0.4.0，提交 `767b6d63d7841d7746189fe97ba67d27dd378d34`。原始源码保留在 `third_party/EdgeDrop`，归档与许可证校验值见 `EdgeDrop.port.json`。原始 Apache-2.0 许可证（含上游附带的第三方图形声明）随应用分发；本次不使用 Twemoji 图形。上游快照没有独立 NOTICE 文件。

下表描述本地改写的来源和平台差异。除直接嵌入未改写的 Phosphor 路径数据外，快照不参与应用构建，不运行其安装脚本或 Electron 进程。表情目录使用与上游锁定依赖一致的 `emoji-data v16.0.0`（来源与 SHA256 见 `EdgeDropEmoji.port.json`），在本地按原类别、排序、可用性、肤色变体与最多 77 条最近使用规则构建。WPF 用 Windows Unicode 字体绘制，不分发 Twemoji 图形；最近使用仅限本次会话，不新增剪贴板历史存储。

| 上游文件／符号 | 本地实现 | 必要差异与验证 |
|---|---|---|
| `src/components/ClipboardItem.tsx`、`src/styles/item.css`、`src/styles/tokens.css`：内容区、底栏、悬停操作胶囊、固定状态 | `src/BrimDeck/IslandContentCard.cs`、`ClipboardCard.cs`、`MainWindow.FileTray.cs` | DOM/CSS 译为 WPF Grid/Border；颜色、圆角与动效接入宿主语义资源。单击选择，双击/Enter 沿用宿主复用设置。验证键盘聚焦显示操作、隐藏操作不命中、固定和失效状态。 |
| `src/components/LinkPreviewCard.tsx`：站点／标题／可读链接三层 | `src/BrimDeck/ClipboardPreview.cs`、`src/BrimDeck.Core/EdgeDropPresentation.cs` | 纯离线解析；保留原始粘贴内容，清理仅用于预览。服务规则采用精确域名边界。验证 GitHub、短链、本地地址、追踪参数、相似域名。 |
| `src/lib/urlPreview.ts`：`trimLinkText`、`stripTracking`、`unwrapRedirect`、`cleanLink`、标题和站点规则 | `src/BrimDeck.Core/EdgeDropPresentation.cs` | URL/URLSearchParams 译为 System.Uri 与保序查询分割；不移植联网功能及完整站点目录。验证用例取自 `tests/urlPreview.test.ts` 的对应分支。 |
| `src/lib/previewImageFit.ts`：`fitPreviewImage` 及常量 | `src/BrimDeck.Core/EdgeDropPresentation.cs`、`src/BrimDeck/ClipboardView.Preview.cs` | CSS px 译为 DIP，DPR 使用 WPF DPI 与宿主缩放。验证全屏截图、长截图、竖图、小图、坏尺寸及边界，期望值取自 `tests/previewImageFit.test.ts`。 |
| `src/components/StackItems.tsx`：`fileStackEntries`、`stackOpenWidth`、`StackItems`、`StackHeader`；`ClipboardItem.tsx`：文件扇形叠层、展开切换 | `src/BrimDeck/IslandFilePreview.cs`、`src/BrimDeck/ClipboardView.Preview.cs` | 保留文件原路径，整组与单项操作交还宿主；本轮不增加拆分／合并持久化。置物架独立条目不强行分组。验证数量、文件名、展开／Esc、整组与单项拖出边界。 |
| `src/lib/itemSignature.ts`：`itemRenderKey`；`ItemList.tsx` 的稳定 ID | `src/BrimDeck/ClipboardCard.cs`、`ClipboardView.Library.cs`、`MainWindow.FileTray.cs` | 按实际可见字段区分预览／元数据变更，稳定集合与卡片；不把未展示的大体积 HTML 纳入预览键。验证同 ID 元数据更新不重新解码图片，时钟不重建列表。 |
| `src/components/Panel.tsx`：目标区域提示、内容与接收层分离；`useDragOut.ts` | `src/BrimDeck/MainWindow.FileTray.cs`、`src/BrimDeck.Shelves/ShelfIslandDropTarget.cs`、`ClipboardDragDrop.cs` | 保留 OLE、DropShelf 源标记和受控附件缓存，内部收藏夹整理优先；已有飞卡算法不替换。验证拒收自身导出、取消不写入、接收后更新当前置物架。 |
| `src/components/SegmentedControl.tsx`：同槽几何、按键导航与提交状态 | `src/BrimDeck/IslandSegmentedControl.cs`、`ClipboardView.cs` | 收藏夹／应用沿用宿主两类独立筛选与可访问名称；同槽滑块、方向键／Home／End 与移动 Tab 停靠点，不改业务分类。 |

## 顶部横向 Dock 布局契约

本轮将 `Panel.tsx` 顶部 1080×210 分支、`Header.tsx` 的 40 DIP 横向头部、`ShelfSearch.tsx` 的居中 300×30 搜索、`panel.css` 的 14／6／8 列表边距与 10 DIP 间距，以及 `item.css` 的 210×146 卡片直接映射到 `ClipboardDockLayout.cs`。全部为未缩放 DIP，DPI 和宿主缩放分别只应用一次。剪贴板页使用整个内容区域；公共页眉在第七轮重新接回同一本体控件并可按需展开。第十轮将相同 Dock 契约接入岛内文件中转，不改变原版浮动置物架。

`ClipboardView.Dock.cs` 译入 `Header.horizontal`／`ShelfSearch`／`panel.css`，`EdgeDropGlyph.cs` 直接读取固定上游 Phosphor Bold 路径（含原 MIT 许可），`ClipboardView.Emoji.cs` 译入独立表情视图和本地目录算法。`ClipboardCard.DockMode` 隔离横向正文与底栏，独立管理器不启用该配置；第十轮的岛内置物架使用 `ShelfIslandCard.DockMode`，数据仍归原版当前置物架。`Settings.Normalize` 的一次性版本迁移备份旧尺寸与布局；设置提供恢复参考尺寸和迁移前布局，不改采集开关或保留规则。

### 本体工具栏适配（第二轮）

保持上述 1080×210、40 DIP 单行头部与 210×146 卡片不变；本轮按 BrimDeck 分类需求适配，而非继续声称完全照搬上游 Header：左侧六个复制类型图标固定可见，不再因窄屏隐藏；搜索收为 30 DIP 图标胶囊，点击／Ctrl+F 以本体动效展开，最大 300 DIP，主动借用输入焦点。快捷键唤起先聚焦折叠的搜索按钮，↓ 可直接进入历史，不自动展开输入框；语言重建按已有开合状态恢复焦点。收起保留查询，Esc 依次处理菜单、预览／编辑、查询、搜索展开和面板。

腾出的单行区域直接挂载原有 `IslandSegmentedControl`「标签／应用」胶囊与可横向滚动的分类标签，不新建分类数据。应用依旧只读来源导航，标签模式保留收藏夹管理与拖拽；窄屏展开搜索时临时借用分类区域，类型图标、收藏与设置始终保留。辅助按钮仅剩收藏和设置；数量、清空、更多与表情不再占工具栏。设置左键仍直接打开本体剪贴板设置；第八轮移除独立的工具栏右键／Shift+F10 更多菜单，工具栏右键遵循本体「右键打开设置」开关。应用标签的固定菜单、收藏夹与卡片菜单保持各自边界；Ctrl+Tab 页面切换保留。

### 卡片复用、计数与排序（第三轮）

固定快照继续为 `767b6d63d7841d7746189fe97ba67d27dd378d34`，本轮不是重新设计近似交互：

| 上游代码 | 本地翻译／接入 | 必要差异 |
|---|---|---|
| `ClipboardItem.tsx`：`handlePointerDown`、`handlePointerMove`、`handleCardClick`、`onPaste` | `ClipboardView.Actions.cs`、`ClipboardDragDrop.AttachSource` | 单击松手回贴，动作按钮不穿透；拖动或尝试拖动后不粘贴，点击正在预览的卡片仅收起预览。Shift+单击纯文本。多文件记录沿用宿主显式预览入口，不强制单击展开。 |
| `CardMenu.tsx`：`MenuList`、关闭后的点击阻断 | `ClipboardView.Actions.cs`：卡片原生右键／Shift+F10 菜单 | 粘贴、纯文本、复制、备注／编辑、收藏、本地预览、删除及按类型显示的打开链接、清理链接、文件定位／路径、图片保存、颜色格式转换；保留宿主归档与收藏夹管理。原生 ContextMenu 负责屏幕约束和外点捕获，防关闭菜单的点击落到卡片后回贴。图片使用显式「另存为」而非静默写入下载目录，删除保留宿主确认。 |
| `colorUtils.ts`：`rgbToHsl`、`alphaText`、`colorFormats` | `EdgeDropPresentation.ColorFormats` | 直接翻译转换公式；输入采用现有离线单色解析器，只支持宿主已经接受的单色，渐变不转换。 |
| `electron/store/ItemStore.ts`：`add`、`touch`；`ClipboardItem.tsx`：`meta-hit-count` | `ClipboardHistory.Add`／`RecordUse`、`ClipboardEntry.CopyCount`、`ClipboardCard.BuildDockFooter` | 首次为 1，重复捕获与成功写入剪贴板／成功拖出各累加一次；显示阈值同上游为大于 1，本体将「×N」放在卡片右下角。与 `UseCount` 复用统计独立，管理编辑不累加。保留本体 `CopiedAt` 的采集时间语义和保留策略，不因复用延长历史寿命。 |
| `electron/main/ipc.ts`：`item:drag` 的完成记账；`useDragOut.ts` | 现有 `ClipboardDragDrop` 与 DropShelf 原生 `ShelfDragService` | 保留 OLE 文件／Unicode 文本／链接载荷，支持投放到接受对应格式的窗口；取消和内部分类整理不累加。普通文件仍为原路径，图片数据才使用有释放宽限的受控缓存；不写系统剪贴板、不删除源文件。上游禁止文本拖出，本体按用户要求保留原生文本拖出。 |
| `ItemList.tsx`：稳定 key 与保留容器 | `ClipboardView.Sort.cs`、`TimelineItems` | 固定版上游没有按次数排序控件；本体在现有 Query 层增加时间降序／CopyCount 降序（同次数按时间、ID 稳定排序），按钮紧邻设置左侧。保存为本体剪贴板偏好，不改独立管理窗口排版或第三套数据；只对同一组稳定 ID 的顺序变化应用位移过渡，使用本体 `Soft.Move`／`Soft.Follow`，禁动画立即就位，不用整列淡出冒充重排序。 |

快照版本升为 4；旧版本未记录重复捕获总次数，因此仅恢复首份加已知 `UseCount`，不编造不可恢复的历史次数。新版本持久化 `CopyCount`，输入边界限制 1–1,000,000。增加排序按钮后，最窄工作区仅收紧头部外边距，固定类型、搜索、收藏和设置仍保留。

### 悬停展开与卡片锚点（第四轮）

移译固定版 `PreviewFlyout.tsx` 的 `previewItemRect`／`cardCenterXInBlade`／`flyoutLeft`：展开预览横向对准触发卡片的中心，再约束到当前显示器工作区。WPF 适配增加岛外下／上／右／左候选位置，下方不足时翻到上方或侧边，不再固定在整个列表的左下角；窗口移动和卡片重排会更新锚点。几何边界统一使用设备像素，正文保持 DIP，只应用一次系统 DPI 与本体缩放。

灵动岛 `DockMode` 卡片移除标题／来源的正文 ToolTip 和次数的文字 ToolTip；来源、完整时间和次数放入现有离线展开预览。悬停 450 ms 后惰性展开，移入预览保留，离开留出 220 ms 跨越间隙。悬停预览不捕获鼠标、不借焦点，第一次单击仍回贴，按下／滚动／右键或页面隐藏会取消待展开；显式展开可固定预览并沿用原有 Esc／外点关闭语义。管理窗口与置物架不启用悬停展开，动作按钮的简短用途提示保留。

第四轮验证：`--only clipboard-preview` 55 项通过，涵盖悬停排程／关闭、跨隙保活、单击不被吞、原生动作与排序，以及工作区负坐标／上下左右／超小屏边界；`--only edgedrop` 79 项通过。实际生产 `App`／编译 `MainWindow.xaml` 宿主 292 项通过，包含顶部／底部、左右触发卡片、100%／150% 的真实 Popup 屏幕位置、主题和状态保持。`../output/clipboard-hover-preview/clipboard-preview-*.png` 使用合成历史，在实际岛与 Popup 的真实屏幕相对位置合成，不读真实历史或系统剪贴板。生产项目随宿主串行构建 0 警告、0 错误；输出为 `src/BrimDeck/bin/clipboard-hover-preview/Debug/BrimDeck.exe`。从已核实路径的旧版 PID 81084 托盘退出后，仅清理其无窗口残余，再启动新版 PID 61472；真实主岛 HWND 7933816 的 `WM_NULL` 响应正常，已通过新版托盘打开剪贴板页。仅检查工具栏存在性，没有读取真实卡片内容或触发粘贴／拖出；记录见 `../output/clipboard-hover-live.json`。

完整 `--only clipboard` 本轮未通过：最初分别暴露测试夹具的非公开滚动事件构造器与旧 Popup 引用问题，均已修正为真实滚动／当前 Popup；随后在既有 `clipboard keyboard: focused rows reveal an unfavorited card's star` 前台焦点断言处中断（此前 157 项通过，未到本轮新增用例）。未修改或跳过该断言；新增独立入口复用同一组合成卡片测试，避免把抢占前台的环境偶发失败当作本轮功能已通过。

### 连续预览与灵敏度（第五轮）

修复第四轮每次跨卡片都等待 450 ms、重建 Popup 的滞留机制：首次悬停使用设置中的灵敏度等待，已展开后按 `DispatcherPriority.Render` 只处理最新卡片，复用同一个原生 Popup 和 Border。最新内容即时替换，位置以本体 `Spring` 携带速度重定向，不排队播放旧卡片；预览禁用离场快照，避免上一张内容的残影。跨隙保活、显式固定、单击回贴／拖出／右键和滚动取消边界仍保留。

`WakeSlider.tsx`／`WakeSlider.css` 与 `Settings.renderHoverDelayControl` 直接移译为 `EdgeDropWakeSlider.cs`／`ClipboardPreviewSensitivityControl.cs`：28 根等宽圆角条、28／8 DIP 波峰／静止高度、3 DIP 间距、44 DIP 命中区，沿用 `SETTLE=9.23`、`FULL_SPEED=320`、`MIN_REACH=1.5`、`reach=6`、`skew=.6`、`glide=.3`、`smoothing=100` 的速度波公式与双弹簧；拖动逐帧合并、松手补最后坐标，方向键／PageUp／PageDown／Home／End、原生 Slider UI Automation 均保留。颜色接入本体设置资源，禁动画保持平条，不引入音效或其他设置重做。

本体「设置 → 剪贴板 → 展开预览」新增 `PreviewSensitivity` 0–100%，默认 70%（首次等待 300 ms）；越高越灵敏，100% 为立即、0% 为 1000 ms。预设标签同上游设置，适配为 0%／50%／100%。滑动即时作用于各岛待展开预览，只在松手／键盘／预设提交时进入本体现有保存队列，不对每个拖动帧重新应用采集、快捷键或整页布局。旧设置自动获得默认值，不自动启用记录，也不更改保留策略。

第五轮验证：修复前同组六次划过用例在第一次跨卡片处稳定失败（预览 ID 仍为前一张）；修复后 `--only clipboard-preview` 93 项通过，覆盖六次连续划过、20 次同帧事件合并、网格跨格、关闭取消排程、灵敏度立即生效，以及 WakeSlider 的几何、键盘／UI Automation、拖动提交／JSON／边界／波峰／禁动画。`--only edgedrop` 79 项通过。实际生产宿主 `DOCK RESULT: 304 checks passed`，包括同一 Popup／Border 的六次更新、跟随弹簧重定向并最终精确停靠、真实设置窗口中的控件可达性和深浅主题。生产随宿主构建 0 警告、0 错误，输出 `src/BrimDeck/bin/clipboard-fluid-preview/Debug/BrimDeck.exe`；截图为 `../output/clipboard-fluid-preview/clipboard-preview-settings.png`、`clipboard-preview-slider-{light,dark}.png`、`clipboard-preview-rapid-settled.png`。检查全部使用合成历史和隔离资源，不读取附图中的内容、真实历史或系统剪贴板；未重跑完整剪贴板套件，不将第四轮已报告的焦点环境阻断宣称为通过。

### 自适应网格与重排（第六轮）

网格是本体对固定版 `ItemList` 稳定 ID／保留卡片的扩展，不声称上游顶部 Dock 已有此布局。`IslandCardPanel` 以实际滚动视口（排除可见滚动条）决定列数，210 DIP 为最小卡宽，10 DIP 为间距，剩余空间在同排各列均分；补偿容器最后一列的尾间距，完整一排右边缘与视口对齐。卡片在网格中按槽拉伸、146 DIP 高度保持，横向列表仍为原版 210×146。没有日期标题的网格不再暗中按日期强制换行，按时间与次数都连续铺排，不改变查询顺序、历史保留或管理窗口排版。

换列前捕获稳定条目的实际显示位置，复用 `ClipboardView.Sort` 的 `Soft.Move`／`Soft.Follow` 双轴位移动效；连续调整宽度直接重定向，禁动画立即就位。滚动中换列保留首个可见条目与行内偏移，网格／横向切换同样使用重排而非整列淡入。跨可见区域移动条目时，虚拟化面板根据 `OldPosition` 清理原容器，避免把未实现的目标位置（索引 −1）用于删除子元素；复用容器仍按 ID 清除旧位移。

第六轮验证：合成 1080 DIP 网格修复前剩余 168.67 DIP，修复后同一完整行剩余 0 DIP。`--only clipboard-preview` 121 项通过，新增 440／660／880／1040／1080／1320 DIP 等宽分配、跨日期铺排、稳定预览复用、连续换列／次数排序与禁动画检查；`--only edgedrop` 79 项通过。真实生产宿主 491 项通过，覆盖五类主题、100%／150%、六档宽度（含 1120 DIP 换列边界）、滚动条出现／消失、滚动锚、双轴重排及原有预览／草稿／千条虚拟化。首次宿主回归在多行排序触发旧容器索引错误后中断（469 项已通过），修正上述清理位置后完整通过；未放宽失败断言。生产随宿主构建 0 警告、0 错误，输出 `src/BrimDeck/bin/clipboard-adaptive-grid/Debug/BrimDeck.exe`；整页合成历史截图为 `../output/clipboard-adaptive-grid/clipboard-grid-*.png`。未读取附图正文、真实历史或系统剪贴板，未向外部应用粘贴／发送。

### 结果胶囊与公共页眉（第七轮）

删除原来覆盖在列表底部、被 `Refresh()` 不断保留的 `_organizeMessage`／`_statusLine`。整理、收藏及卡片操作失败通过 `ClipboardView.Notice` 的单个结果胶囊显示，位于当前展开面板内的右下角，最大宽度受实际面板与两侧 14 DIP 边距约束。显示 3 秒后以本体 `Soft.Leave` 退出；新结果替换旧结果并重新计时，离开／销毁视图立即清空，不因列表刷新或重新打开而重放旧提示。不拦截卡片点击，不新增系统窗口、不取得焦点。上游 `Toast.tsx`／`panel.css` 是瞬时结果浮层的参考，本体复用已有 Popup 颜色与 `Soft.Enter`／`Soft.Leave`，采用用户指定的右下角单胶囊而非照搬上游居中堆栈。

剪贴板重新接回现有 `Toolbar`／`PageSwitch`／`ClockLabel`／`SettingsButton`，不是另建一套导航。`ClipboardSettings.HeaderMode` 持久化两种模式：默认「顶部边缘唤出」，展开后先离开顶部边缘、再次进入面板正上方 6 DIP 屏幕边缘才显示；回到内容 450 ms 后收起，公共页眉焦点／交互期间保留。「常驻显示」每次展开均保留 36 DIP 页眉。复用 `SetExpanded` 的 360 ms `PanelMotion.Reshape`／`Soft.InOut`，同步向下位移页眉和内容并调整岛体高度；空间充足保留原剪贴板视口，工作区不足时压缩内容。当前显示器的设备像素与 WPF DPI 各处理一次，降低位置的胶囊与屏幕边缘之间仍可跨越且不拦截桌面点击。状态、主题、语言、查询、选中、滚动和预览仍归原视图所有；切到其他公共页面时清理页眉变形与边缘触发状态。设置入口位于「设置 → 剪贴板 → 公共页眉 → 显示方式」，不改变其他页面、采集开关或保留策略。

第七轮验证使用合成历史、隔离文件和真实生产 `App`／`MainWindow.xaml` 宿主，不读取用户历史或系统剪贴板，不向外部应用粘贴／发送。通知定向检查使用实际 Dispatcher 时间验证到期、替换延时、窄屏宽度、隐藏清理和退出回调竞争；公共页眉覆盖首次／再次边缘触发、离开回收、360 ms 中间帧／反转、常驻模式、工作区不足、五类主题 × 100%／150%、真实页面按钮与切页恢复、JSON 兼容。截图输出到 `../output/clipboard-shared-header/clipboard-shared-header-*.png`，包含公共页面、右下角结果及真实设置入口；输出程序为 `src/BrimDeck/bin/clipboard-shared-header/Debug/BrimDeck.exe`。

### 应用固定与工具栏输入（第八轮）

应用自动分类增加本体导航偏好 `ClipboardSettings.PinnedApplications`，不改来源字段、分类计数、历史数据或收藏夹。右键具体应用标签仅显示「固定到最前」／「取消固定」；总历史始终在第一位，新固定应用排在其他应用之前，固定顺序随普通设置保存。无记录的来源不生成虚假标签，偏好仍可在该来源再次出现时恢复；未知来源使用独立的空来源键，与总历史分离。图钉沿用固定快照的 Phosphor 图形，菜单沿用宿主主题、缩放和 PopupMotion，不宣称上游已有本体应用分类固定功能。

删除截图中的工具栏更多菜单及其键盘入口，不在工具栏／设置按钮吞右键事件；由原有 `MainWindow` 设置操作处理，尊重本体开关。收藏夹管理、应用固定、卡片菜单仍只处理自己的命中区域。分类区域每次滚轮按当前真实视口与内容范围判断溢出，不缓存宽度、不假设参考面板大小。需要横向滚动时区域独占滚轮（到头也不误切页面）；未溢出时解除 WPF ScrollViewer 对空轴的默认吞轮，让同一原始事件到达本体页面／音量行为，不合成第二次输入。固定类型、搜索及操作按钮与工具栏留白同样保留本体滚轮路径。排序及键盘页面选择的临时菜单禁止系统右键自动打开，防止 WPF 关闭动画暂留绑定时旧菜单复现。

第八轮定向入口 `--only clipboard-toolbar` 140 项通过，包含核心来源／偏好持久化、固定／取消固定／未知来源、原始鼠标路由、480–1320 DIP 连续宽度、收藏夹未溢出及窄屏搜索隐藏／恢复；原 `--only clipboard-preview` 131 项、`--only edgedrop` 79 项通过。实际生产宿主定向入口在输出目录后添加 `--only-toolbar`，396 项通过；最终完整宿主 `DOCK RESULT: 680 checks passed`。真实宿主覆盖五类主题 × 100%／150% × 480／660／880／1080／1320 DIP、边界吞轮、关闭主机滚轮操作、页面轮换、本体右键设置开关和音量半格累积（不发送真实音量键），保留原有预览、草稿与虚拟化检查。最初完整回归在工具栏前的预览用例因 Popup 脱离 PresentationSource 中断（261 项已通过），未放宽或修改该断言；移除测试对已退役更多菜单的依赖后，最终完整运行通过，不将之前中断称为通过。程序输出为 `src/BrimDeck/bin/clipboard-toolbar-pins/Debug/BrimDeck.exe`，合成数据的宿主截图位于 `../output/clipboard-toolbar-pins-final/`；未读取真实历史、系统剪贴板或向外部程序粘贴／发送。

### QQ 图片采集与卡片（第九轮）

`ClipboardListener.ReadContent` 原来优先把 `CF_HDROP` 判为文件，即使 QQ 复制的是一张聊天图片，仍生成文件名卡片和附件预览。固定版 Edge-Drop `formats.ts` 也使用文件优先，因此本轮是宿主兼容修正，不宣称直接照搬上游已有 QQ 支持：仅来源进程名精确匹配 QQ／QQNT／TIM、且文件列表仅一项并为图片扩展名时，尝试读取本机实际图片像素并归类为 Image。普通来源、非图片附件及多文件复制仍保持 Files，不能因系统提供缩略位图而丢失其他文件。原始文件路径保留在同一条内容中，普通文件不复制到置物架缓存；拖出时有效原路径优先，来源已消失时才将已有图片数据导出为受控附件。历史删除不删除原文件，不迁移或重新读取旧记录；旧文件卡片需要重新复制后获得新的图片记录。

图片格式列表复用原版 DropShelf `DropDataReader.EncodedImageFormats` 的 PNG／image/png／JFIF／image/jpeg／GIF／image/gif；读取后使用现有 WPF 图片解码、PNG 编码与历史像素限制，而不调用置物架导入器或新建第二份缓存。文件探测只接受本机盘符路径，拒绝网络盘、UNC、设备路径和重解析点；编码文件不超过 8 MiB，宽高不超过 8192、总像素不超过 16,777,216，解码失败仍按原文件引用处理。QQ 单图由「记录图片」控制，不因「记录文件」关闭而丢失图片，也不借文件回退绕过图片禁用。图片卡片与展开预览直接复用现有 Image 分支，分别采用铺满裁剪和等比完整显示，不再显示文件名缩略格；粘贴仍提供 Bitmap／PNG 和隐私标记，而非附件列表。

合成单 PNG 的 QQ 文件列表在修复前稳定复现为 Files；修复后 `--only clipboard-images` 34 项通过，覆盖来源边界、单图与混合文件、PNG-only、位图回退、解码失败、超大尺寸／体积、来源消失、图片开关、原路径和图片复用格式。`--only clipboard-preview` 131、`--only edgedrop` 79 项通过。生产宿主最终 `DOCK RESULT: 708 checks passed`，新增竖图／横图／方图采集到实际卡片和 Popup 的检查，覆盖深／浅／高对比、100%／150%；第一轮宿主因新测试误引用不存在的 `_previewBody` 字段中断，修正为真实 Popup.Child 后完整通过，未更改生产预览来迎合测试。产物为 `src/BrimDeck/bin/clipboard-qq-images/Debug/BrimDeck.exe`，合成渲染在 `../output/clipboard-qq-images/clipboard-qq-*.png`。没有读取截图对应文件、QQ 聊天记录或真实剪贴板，也未向外部应用粘贴；QQ 当前版本的实际复制／接收仍需用户在新版中复核。

### 岛内文件中转 Dock（第十轮）

`MainWindow.FileTray.Dock.cs` 直接复用 `ClipboardDockLayout`、`ClipboardView.Dock` 的单行头部结构、折叠搜索、固定类型图标和宿主资源，不另起一套几何或数据管理器。参考尺寸为 1080×210 DIP，头部 40、卡片 210×146、间距 10、左右边距 14；图片正文使用共享模型已加载的本地缩略图。工具栏包含全部／文件／文件夹／图片／文本／链接六种投影、搜索、当前置物架选择、固定筛选、横向／网格、原版快速操作、更多及设置。窄屏将网格与快速操作收到「更多」，固定类型、搜索、固定、更多与设置保持可达；「更多」也保留当前架选择、新建、打开浮动架和清空未固定项目。

仅岛内视图启用这些布局：当前原版 `ShelfViewModel.Items` 仍是唯一条目集合，搜索与类型只是可见投影，文件保持原路径、条目保持平铺。`ShelfDockLayout` 独立版本迁移备份原尺寸／网格，并提供恢复参考尺寸或迁移前布局；`TraySettings.HeaderMode` 与剪贴板偏好独立，沿用同一套本体公共页眉、顶部边缘再进入触发、450 ms 回收和 360 ms 面板变形，不重建页面。查询、过滤器、滚动锚和稳定卡片随主题／语言更新保留；Ctrl+F 明确借用输入焦点，Esc 依次处理拖动、菜单、查询、搜索再收起面板，IME 事件不接管。

自适应网格按实际视口分配等宽列，关闭列内独立布局取整以免高 DPI 小数像素逐列累积；按 ID 保留卡片，双轴 `Soft.Move`／`Soft.Follow` 重排以零变换为最终基值，禁动画立即就位。`ShelfViewModel.SetViewportItems` 为岛内视口申报可见项，复用原版惰性缩略图加载与缓存；每个宿主视口分别释放，不能取消浮动列表或其他显示器仍在使用的缩略图。缩略图、文件可用性等界面刷新仍不触发持久化通知。动作结果复用剪贴板同款右下角非交互胶囊，显示 3 秒后清空，替换重计时，隐藏立即清除。

真实生产 `App`／编译 `MainWindow.xaml` 的 `--only-shelf` 入口最终 208 项通过，覆盖五类主题 × 100%／150% × 1080／440 DIP、六档网格宽度、原模型身份、原路径、可见缩略图所有权、筛选、滚动锚、连续重排、原生公共页眉及结果胶囊。完整剪贴板宿主回归 `DOCK RESULT: 718 checks passed`；共享置物架 70、Edge-Drop 卡片 79、文件分享 23 项隔离回归全部通过；最终生产随宿主构建 0 警告、0 错误。合成宿主 PNG 在 `../output/shelf-dock/`，产物为 `src/BrimDeck/bin/shelf-dock/Debug/BrimDeck.exe`。早期检查发现网格累计取整和重排结束恢复旧偏移，均在生产布局层修复后通过；离屏窗口没有真实按住鼠标，自动排序检查只验证捕获后的取消／提交状态，不声称验证真实鼠标捕获或跨应用 OLE 接收。

专项命令：`dotnet build tests/BrimDeck.Dock.Tests/BrimDeck.Dock.Tests.csproj --no-restore -m:1 -p:Platform=x64 -p:OutputPath=bin/shelf-dock/Debug/ -v:minimal`，随后 `dotnet tests/BrimDeck.Dock.Tests/bin/shelf-dock/Debug/BrimDeck.Dock.Tests.dll <截图目录> --only-shelf`；必须出现 `SHELF DOCK RESULT` 完成行。所有数据为临时目录下的合成文件／历史，不读取真实剪贴板、用户历史、聊天记录或外部接收窗口。

### 手动预览浮窗（第十一轮）

手动预览入口统一命名为「预览浮窗」，不再固定自动悬停 Popup。`ClipboardPreviewWindow` 通过 `ShelfToolWindow` 复用 DropShelf 2.6.7 的 `ShelfWindow.xaml`／`ShelfWindow.xaml.cs`／`ShelfWindow.Entrance.cs` 外壳适配、原版 `AppWindowStyle`、图钉与关闭按钮资源，以及 `ShelfMotion`、`WindowPlacement`、`WindowEffects`；来源保留 MIT 归属。浮窗接入本体主题、像素直角、高对比、缩放与禁动画，不另起应用、托盘、置物架模型或剪贴板历史。

`ClipboardPreviewWindows` 仅登记应用生命周期内的呈现窗口，内容仍引用唯一 `ClipboardHistory`。同条记录复用原窗口及移动后的位置，不同记录独立打开；隐藏灵动岛、关闭其 Popup、切换布局或销毁视图不会关闭手动浮窗。自动悬停预览保留原有灵敏度、连续跟随与跨隙保活。主题、语言和元数据更新不重建已解码内容，保留滚动和窗口位置；删除或过期记录关闭对应浮窗，原文件不删除。

浮窗支持拖动、调整大小、置顶／取消置顶、复制、显式回贴及原生拖出。复制保持窗口打开；复制失败在浮窗内反馈且保留内容，显式回贴成功后仅关闭当前浮窗，并使用该窗口保存的外部接收目标。Enter 沿用「激活时粘贴」偏好，Ctrl+Enter 仅复制；焦点位于操作按钮时保留按钮自身的 Enter 行为。文件整组与单项操作沿用原路径，预览保持本地惰性解码和非执行内容边界。

本轮最终构建 0 警告、0 错误；真实生产宿主 `--only-preview-window` 71 项通过，涵盖五类主题 × 100%／150%、独立生命周期、同条复用、复制失败、按钮键盘边界及本体实际语言刷新入口。完整剪贴板宿主 718 项通过；隔离悬停／卡片检查 131、Edge-Drop 79、工具栏 148 项通过。完整宿主最初因布局切换测试仍把手动预览当作固定 Popup 而中断，改为独立窗口断言后通过，同时保留原选中和滚动锚检查。合成浮窗 PNG 在 `../output/preview-window/`，已查看深色、150% 像素及高对比效果；没有读取真实历史、系统剪贴板或向外部接收程序粘贴／发送。

构建产物为 `src/BrimDeck/bin/preview-window/Debug/BrimDeck.exe`。从已核实的功能分支旧版托盘退出 PID 28684，窗口全部关闭后只清理其无窗口残余；新版 PID 61584 的实际 WPF HWND 13830124 通过 `WM_NULL` 响应检查。未修改或重启相邻分支。

### 应用标签动作与代码筛选（第十二轮）

应用标签右键在原有固定之外增加「隐藏该应用（保留记录）」和「清空该应用的记录…」，岛内与独立管理窗口共用 `ClipboardApplicationMenu`。清空由唯一 `ClipboardHistory.ClearApplication` 按大小写不敏感的精确来源一次提交，不受当前搜索、类型、收藏或归档筛选影响；确认框明确包含收藏、归档及收藏夹中的记录，不删除原文件、不写系统剪贴板。空来源键仅清空未知应用，不表示全部历史，也不等同于名字恰好是 Unknown app 的真实应用。

`ClipboardSettings.HiddenApplications` 仅为可持久化的导航偏好，不改采集排除名单、来源字段、收藏夹归属或记录。隐藏当前来源后返回全部历史并保留其他筛选；新复制仍会被原有采集规则接收，隐藏标签不会自动重现。右键应用分类中的「全部历史 → 显示已隐藏的应用」可逐一恢复或全部显示，两处界面与设置保存队列同步。

`ApplicationLabelMotion` 复用本体 `PageHandover.Snapshot` 和 `Soft.Move`／`Soft.Out`／`Soft.InOut`／`Soft.Follow`；宿主没有既成粉碎组件，因此仅在共享快照之上添加最多 7×3 个标签碎片，飞散淡出并平滑补齐其余标签。数据操作即时完成，不等待动画；碎片只包含标签、不可交互、受实际可视区约束。完成、隐藏、卸载、缩放或销毁时释放快照与位移动效；禁动画立即就位。不是另建历史，也不宣称上游已有应用标签粉碎。

左侧固定类型现在为七个，代码按钮紧邻颜色右侧，复用本体已有的代码矢量图标与 `ClipboardContentKind.Code` 查询。原文本按钮仍包含纯文本、富文本和代码，保持兼容；最窄面板只收紧按钮宽度与两侧留白，不隐藏类型、搜索、收藏、排序或设置。

本轮定向隔离检查：`--only clipboard-applications` 101 项、`--only clipboard-toolbar` 161 项通过。生产宿主 `--only-applications` 318 项、`--only-toolbar` 407 项通过，覆盖五类主题 × 100%／150%、七类型与窄屏搜索、菜单、确认取消、来源范围、原文件保留、粉碎中间帧、回收及禁动画。已检查浅色菜单、深色 150% 碎片、像素代码筛选、高对比菜单和窄屏展开搜索渲染，位于 `../output/application-actions-host/` 与 `../output/application-toolbar-host-final/`。

最初隔离检查发现新建标签尚未挂入树时菜单样式查找失败，已改由所属视图查找；新增测试的 Path／Button 名称遮蔽与初始化可空警告已修正。额外工具栏宿主回归先后暴露离屏自动收起计时器干扰，以及旧断言假设 1320 DIP 必定不溢出；测试隔离鼠标离开状态，并按新增第七个按钮后的实际溢出验证，保留原输入和几何断言，最终完整定向入口通过。未运行核心全量或全部剪贴板回归，不读取真实历史、系统剪贴板或向外部应用粘贴。

构建命令为 `dotnet build tests/BrimDeck.Dock.Tests/BrimDeck.Dock.Tests.csproj --no-restore -m:1 -p:Platform=x64 -p:OutputPath=bin/application-actions/Debug/ -v:minimal`；随后运行同目录测试 DLL，传入截图目录和对应 `--only-applications`／`--only-toolbar`，以完成行与退出码为准。生产产物为 `src/BrimDeck/bin/application-actions/Debug/BrimDeck.exe`。从路径已核实的旧版 PID 58736 托盘安全退出，窗口关闭后仅清理其无窗口残余，再启动新版 PID 70160。实际主岛 HWND 10358152 通过 `WM_NULL` 响应检查；托盘打开剪贴板后，仅按自动化标识检查工具栏几何，颜色按钮 X=1314／宽 42，代码按钮 X=1356／宽 42，确认紧邻右侧且可见。首次过早查询时控件尚未生成，随后实际窗口结构检查已确认；没有读取卡片正文或执行复制／粘贴，没有重启相邻分支。

## 验证入口

`tests/BrimDeck.WindowsTests` 使用人工生成的独立历史、文件与 WPF 离屏窗口；不读取真实剪贴板内容，不向真实应用粘贴。剪贴板测试会短暂注册并立即释放隔离快捷键与消息监听适配器，但不运行实际应用的采集流程。构建与回归串行进行；构建通过后只启动或重启路径位于 `BrimDeck-feature` 的可执行文件。

### 卡片交互与计数排序验证（第三轮，2026-10-31）

- 真实宿主 `DOCK RESULT: 264 checks passed`；五类主题、100%／150%、宽／窄工作区的三态整页 PNG 输出 `../output/clipboard-card-actions/`，另有 `clipboard-card-count-order.png`、`clipboard-card-sort-menu.png`、`clipboard-card-context-menu.png`。菜单图将真实独立 HWND 的 WPF 菜单视觉合成在真实岛体下方，不伪造菜单位置或用人工壳代替宿主。
- 已查看默认深色、浅色应用分类、150% 窄屏高对比搜索，以及右键菜单和次数排序结果：右下角「×4／×7」与时间并列，排序按钮直接位于设置左侧；按次数后 ×7 的链接先于 ×4 文本。菜单与卡片使用本体主题，窄屏固定类型和全部右侧操作均未被挤出。
- 最新剪贴板定向回归 310 项通过，包含原 `ClipboardHistoryTests`／`ClipboardLibraryTests` 和新增鼠标事件、拖动抑制、预览关闭、右键／键盘菜单、纯文本回贴、离线干净链接、原文件路径复制、颜色转换、v3→v4 计数迁移、偏好持久化、跨日期网格排序与重排序位移动效。禁动画路径立即就位。
- Edge-Drop 79、文件分享 23、置物架 70 项通过；加剪贴板共 482 项。真实外部目标窗口粘贴／拖入未自动执行，检查均用合成历史、隔离文件与截获的激活回调；不读取真实历史，不写入真实系统剪贴板，不向 QQ／微信发送。
- 生产独立构建 0 警告、0 错误，输出 `src/BrimDeck/bin/clipboard-card-actions/Debug/BrimDeck.exe`；构建完成前保留原运行实例。新增的 `ClipboardView.Actions.cs`／`ClipboardView.Sort.cs` 同时接入显式源码列表的 Windows 回归项目。
- 从原托盘退出旧 PID 10972；窗口全部关闭后仅清理其路径已核实的无窗口残余。新版 PID 81084、实际 WPF HWND 3212902，`WM_NULL` 在 2 秒内响应。已从新版托盘打开剪贴板页，只读取工具栏控件几何与可见性：12 个预期控件可见，排序按钮在设置左侧；没有读取卡片文本、系统剪贴板或触发粘贴／拖出。运行验收记录为 `../output/clipboard-card-actions-live.json`，没有修改或重启其他分支。

### 工具栏适配验证（第二轮，2026-10-31）

- 最新真实宿主回归 `DOCK RESULT: 262 checks passed`：生产 `MainWindow` 和实际资源，覆盖固定六类型、折叠／展开搜索、标签／应用导航、窄屏边界、焦点、主题／语言、预览／草稿、虚拟化和表情复制边界。
- 最新串行回归：剪贴板 189、Edge-Drop 79、文件分享 23、置物架 70、原版置物架 61，共 422 项通过；原版隔离 runner 报告 0 失败，仍明确跳过真实剪贴板、全局输入、junction／7z 与缓存竞态压力测试。
- 真实宿主整页 PNG 输出在 `../output/clipboard-dock-adapt/`，五类主题 × 100%／150% × 宽／窄工作区，每组包含浏览、展开搜索与应用分类三态。已查看深色浏览／搜索、浅色应用、像素应用和 150% 窄屏高对比搜索：头部保持单行，六类型与收藏／设置未被搜索挤出；窄屏展开时只临时让出分类区域，卡片仍为横向布局。
- 设置右键入口已直接绑定按钮，避免依赖 WPF 父级直接鼠标事件重新派发；回归通过真实按钮事件覆盖菜单。原生 UTF-16 内部拖放检查改用常驻分类按钮，不再反射已移除的分类弹窗。
- 独立生产构建 0 警告、0 错误，输出 `src/BrimDeck/bin/clipboard-dock-toolbar/Debug/BrimDeck.exe`；构建成功前保留旧运行版本。原托盘退出 PID 66208 后所有窗口关闭，仅清理其路径已核实的无窗口残余；新版 PID 10972、WPF HWND 3217164，`WM_NULL` 在 2 秒内响应。重启记录位于 `../output/clipboard-dock-adapt-restart.json`，没有启动或终止其他分支。随后从新版托盘打开剪贴板页，UI Automation 确认六种类型、搜索、两种分类模式与收藏／设置共 11 个控件可见，搜索保持折叠，窗口继续响应；只读取这些工具栏控件的几何与可见性，未读取真实历史文本或剪贴板值，记录位于 `../output/clipboard-dock-adapt-live.json`。
- 现有核心全量测试的监控页断言问题见下节；本轮未修改该无关逻辑，也不声称核心全量通过。

本轮命令沿用下方回归入口。真实宿主使用 `dotnet build tests/BrimDeck.Dock.Tests/BrimDeck.Dock.Tests.csproj --no-restore -m:1 -p:Platform=x64 -p:OutputPath=bin/clipboard-dock-adapt/Debug/ -v:minimal`，随后运行对应输出的 `BrimDeck.Dock.Tests.dll` 并传入截图目录；必须出现 `DOCK RESULT` 完成行。独立生产构建将 `OutputPath` 设为 `bin/clipboard-dock-toolbar/Debug/`，避开旧版运行文件锁。

### 顶部 Dock 首轮验证（2026-10-31，适配前）

`tests/BrimDeck.Dock.Tests` 直接引用生产 `BrimDeck.dll`，载入实际 `App.xaml` 资源与编译后的 `MainWindow.xaml`，调用生产 `PrepareClipboardDock`／`RenderClipboardPage`／`SetExpanded`。测试阻断 App 启动与窗口 Loaded 中的真实采集、账号刷新及音乐监听；历史、图片和文件均为合成数据。表情复制由测试回调截获，不触碰系统剪贴板。

- 181 项通过：20 组整页矩阵（深／浅／自定义／像素／合成高对比，100%／150%，默认宽／窄工作区）；1080×210 基准、单头部、居中搜索、210×146 卡片、10 DIP 间距、左右底栏、横向不换行与局部露出的下一张卡片。
- 覆盖版本迁移、旧布局恢复、不改变采集与其他页设置、语言重建、主题与未保存草稿、稳定选中与预览、横向／网格滚动锚、千条数据虚拟化、表情／肤色分支和会话最近使用上限。
- 20 张真实宿主整页 PNG 输出到 `../output/clipboard-dock/clipboard-dock-{theme}-{100|150}-{wide|narrow}.png`；已检查深浅、像素、高对比、自定义及窄屏图。截图使用合成内容，不宣称外部应用实机交互已验收。
- 下表五组回归本轮串行重跑，共 409 项通过；其中剪贴板包含启用／禁用动画的完整菜单和列表动效检查。
- 额外核心全量测试在 `SystemMonitorTests.Pages` 的既有「monitor comes after usage and music」断言停止：该旧测试仍以 `Tray.Enabled` 判断可见性，而现有宿主使用 `Tray.Placement`。失败发生在本轮剪贴板尺寸断言之前；未改置物架逻辑或放宽该断言，本次不宣称核心全量通过。

本轮构建 0 警告、0 错误后，从原托盘「退出」关闭旧 PID 14228 的全部 WPF 窗口；旧版留有无窗口残余进程，仅清理了路径已核对的该 PID。新版 PID 66208 从上述独立输出启动；可见 WPF HWND 3804936 的 `WM_NULL` 在 2 秒内响应。非激活悬浮窗的 `Process.MainWindowHandle` 为 0，不作为窗口未启动的证据；未重启其他分支或应用。随后通过新版托盘打开剪贴板页，仅检查 `clipboard-search` 的可见性与几何，并再次确认该 HWND 响应；未读取系统剪贴板或真实历史条目，也未触发复制／粘贴。

真实宿主检查命令：`dotnet build tests/BrimDeck.Dock.Tests/BrimDeck.Dock.Tests.csproj --no-restore -m:1 -p:Platform=x64 -p:OutputPath=bin/clipboard-dock/Debug/ -v:minimal`，然后 `dotnet tests/BrimDeck.Dock.Tests/bin/clipboard-dock/Debug/BrimDeck.Dock.Tests.dll <截图目录>`。必须看到 `DOCK RESULT` 完成行，不能仅用进程返回码判断 WPF 检查完成。

### 既有功能回归（首轮记录）

| 入口 | 结果 | 覆盖范围 |
|---|---|---|
| `dotnet run --project tests/BrimDeck.WindowsTests/BrimDeck.WindowsTests.csproj --no-restore -- --only edgedrop` | 79 项通过 | 原版图片适配／叠层几何、离线 URL 分支、1000 条记录横向及网格虚拟化、稳定预览、键盘聚焦、文件组展开、UTF-16 原生内部标记、五类主题状态保持、禁用动画、缺失源文件整组拒绝导出。 |
| 同项目 `--only clipboard` | 176 项通过 | 原有剪贴板读写格式适配、搜索／分类整理、独立管理窗口、草稿、焦点、菜单、布局与通用动效；删除收藏夹不关闭未保存外观草稿，也不重建已删除分类。 |
| 同项目 `--only shelves` | 70 项通过 | 原版共享模型、固定／排序、原路径、虚拟附件缓存、拖入拒收边界、完整设置及展示位置矩阵；缩略图／可用性只刷新界面，不触发磁盘保存。 |
| 同项目 `--only file-share` | 23 项通过 | 原路径分享载荷、来源消失时拒绝部分分享、保留原文件与防回流边界。 |
| `dotnet run --project tests/BrimDeck.Shelves.Tests/BrimDeck.Shelves.Tests.csproj --no-restore` | 原版 61 项通过，0 失败 | 隔离运行保留的原版测试方法；真实剪贴板、全局输入／快捷键探针、junction／7z 外部命令及缓存竞态压力测试明确不执行。 |
| `dotnet build src/BrimDeck/BrimDeck.csproj --no-restore -m:1 -p:Platform=x64 -p:OutputPath=bin/clipboard-dock/Debug/ -v:normal` | 0 警告、0 错误 | WPF 宿主与共享库；本轮独立输出 `src/BrimDeck/bin/clipboard-dock/Debug/BrimDeck.exe`，不覆盖仍在运行的旧 x64 输出。分发保留 Edge-Drop、Phosphor 和 emoji-data 的许可。 |

`--only clipboard --render <目录>` 输出深浅色、网格、紧凑布局、收藏夹和管理窗口；`--only edgedrop --render <目录>` 输出深浅色、像素、自定义和高对比置物架卡片。透明卡片在宿主底色上合成，不把透明像素显示器的白底误判为主题效果。像素主题同时检查圆角和裁剪几何；切换主题保持搜索、选中条目、滚动及展开预览。

### 明确边界

这是固定源码的 WPF 翻译移植，不是完整 Electron 应用或跨平台运行时移植。链接预览只包含已映射的离线规则，不宣称覆盖上游完整站点目录；不请求网页、远程缩略图或执行 HTML／SVG。文件组仅展开现有多文件剪贴板记录，置物架条目仍保持平铺。不替换 DropShelf 原生拖出、动作执行或飞卡算法，不增加第三套数据所有权。

实际 QQ／微信等外部应用的接收行为、真实用户历史及全局输入没有自动实操；隔离测试通过不等于这些第三方交互已经人工验收。
