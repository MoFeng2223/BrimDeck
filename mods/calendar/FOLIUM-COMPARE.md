# Folium × BrimDeck：模组系统对照

来源：`chthollyphile/folia-major` 的 `mods/README.md`（`main`，1,519 行）与 `docs/folium/api.md`（2,312 行）。
BrimDeck 侧为契约 **1.5** 实测。本文只谈"能抄什么、怎么抄、抄不了什么"，不评价两个产品。

分三档：

- **A 可直接落地** —— 不需要改架构，属于补规则与补工具。
- **B 需改造** —— 形状可借，但宿主模型不同（Electron 双进程 vs WPF 单进程）。
- **C 不适用** —— 音乐域或渲染技术栈特有。

---

## 总览

| 能力 | Folium | BrimDeck 1.5 |
| --- | --- | --- |
| 清单 + 内容摘要审批 | ✅ 且签名绑定内容 | ✅ `Approvals[id] == Digest` |
| 签名 / 渠道 / 吊销 | ✅ 四状态 + 8 原因码 | ❌ |
| 开发目录豁免 | ✅ 仓库 `mods/` 自动更新授权 | ❌ |
| 启动失败回退 | ✅ 启动记号 + 心跳 + 一次性免模组启动 | ❌ |
| 窗口卡死回退 | ✅ 原生对话框 | ❌ |
| 每模组开销可见 | ✅ 启动/每秒/错误数/主进程耗时 | ⚠️ 有数据（`ModulePerformance`），呈现有限 |
| 实验接口显式选用 | ✅ `experimental[]` | ❌ |
| 废弃通道 | ✅ 旧名表 + 每模组提醒一次 + check | ❌ |
| 注册表数量 | 11（按宿主渲染位置切） | 4（page/activity/widget/settings） |
| 视图上下文 | `onFrame/onResize/onChange/every/onDispose` | `Every/Own/OnDispose/SetViewport` |
| 设置字段 | number/text/boolean/select/color/secret + group/desc | number/toggle/choice |
| 存储 | 1 MB 单文件 + `data`/`cache` 目录 + `delete/keys` | 单键 1 MiB + `Read/Write`（无 delete/keys） |
| 安装器 | zip 校验 + staging + 原子替换 + 回滚 | ❌ 手动拷贝 |
| 缺失条目降级 | ✅ 保留选择 + 标注来源 + 可恢复 | ❌ |
| 静态检查器 | ✅ `folium:check` | ⚠️ 只核对文档签名区 |
| 可信代码 / 沙箱 | 明确"不是沙箱" | 明确"不是沙箱" |

---

## A 档：可直接落地

### A1. 安全启动（防"模组把本体搞到打不开"）

**Folium**：启动前写 `mod-start-note.json`；全部模组启动完（或过 5 秒）且第一屏挂载后删除；正常退出也删，
只有被杀/崩溃/卡住才留。下次启动发现记号还在 → **在运行任何模组代码之前**问一次「这次要不要不带模组启动」；
选「不带」只影响本次运行、不改设置；期间面板把已启用的模组显示为「这次没有启动」。
启动期间页面每秒上报"我在响应"，退出时最近 3 秒听到过才删记号。记号删不掉时另写一个文件作废它，避免每次启动都问。

**为什么**：签名、审核、AI 看代码都挡不住"已批准、内容未变"的模组在启动期崩溃。这是唯一有效的兜底。

**BrimDeck 落点**：
- 写/删记号：`src/BrimDeck/App.Modules.cs`（`ConfigureModulesAsync` 之前）
- 启动完成后撤除：主窗口首次 `Loaded` 之后
- 询问与"本次不加载"：`ModuleHost` 增加一次性开关（不写 `Settings.Modules.EnabledModules`）
- 面板文案：`SettingsModuleCard.cs`，状态新增「这次没有启动」

### A2. 窗口无响应回退

**Folium**：有模组运行时窗口卡住 → 弹**原生**对话框："继续等"或"不带模组重新打开"（同样只管本次）。
**落点**：`MainWindow` 的 UI 线程卡死无法自检，需要在宿主侧加一个看门狗（后台线程 + `SendMessageTimeout` 探测），
或者退一步——只做 A1，把"卡死"当作"崩溃"处理（记号留着，下次询问）。**建议先只做 A1。**

### A3. 签名四状态 + 原因码

**Folium**：`verified / beta / unsigned / invalid`；原因码 `digest-mismatch`、`mod-mismatch`、`unknown-key`、
`revoked-key`、`revoked-mod`、`bad-signature`、`malformed`、`unverifiable`；渠道由**签名密钥**决定，不是清单字段；
签名覆盖除 `folium.sig.json` 与 OS 垃圾文件外的全部普通文件；清单可手工复现（`sha256sum` 同格式）；
签名消息固定六行，**改版本号即失效**；密钥与撤回列表随宿主发布，不联网。

**BrimDeck 落点**：`ModuleCatalog`（校验）→ 新增 `PackageTrust` 记录；`App.Modules.cs` 激活条件保持不变，
信任状态只用于展示与提示；`SettingsModuleCard.cs` 加徽章。清单字段不需要新增——签名是包内文件。

### A4. 开发目录豁免

**Folium**：仓库 `mods/` 下的开发版，首次仍需确认，之后文件变化**自动更新授权**；`userData/mods` 与
`resources/mods` 永远没有豁免。
**落点**：`App.Modules.cs` 里比较 digest 的那一处，按"来源目录"分流。这是我整个开发过程最痛的点（每次改代码都要
刷新+重新启用），改动量却极小。

### A5. 每模组开销可见 + 慢阈值 + 不因慢停用

**Folium**：启动耗时、运行时每秒毫秒、出错次数与位置、main 进程耗时；慢阈值：启动满 1.5 秒或超 5 秒未完成、
运行时每秒 ≥240 ms、主进程单次 ≥100 ms；**宿主不会因为慢关掉任何东西**。
**BrimDeck 现状**：`ModulePerformance` 已有 `StartupMilliseconds / MillisecondsPerSecond / SlowCalls / Errors /
LongestOperation`，只是面板没展示全。
**落点**：`SettingsModuleCard.cs` 的展开区；`ModuleInfo.Slow` 已有，补"运行时慢"与"最长操作"。

### A6. 缺失条目降级

**Folium**：用户选中的样式/背景/音源来自某模组，该模组停用或卸载后——**已保存的选择不改写**，界面显示默认项
并提示「来自未运行的模组 X」，模组重新启用后自动恢复。
**为什么重要**：这是"卸载即消失"的另一半。BrimDeck 将来做主题/皮肤时，卸载模组不应该把用户的设置改成默认值。
**落点**：任何"引用模组贡献"的宿主状态（未来的主题 id、皮肤 id）都要按此处理；当前日历模组没有这类引用，属于前瞻。

### A7. 设置字段扩容 + 非法声明降级

**Folium**：`number/text/boolean/select/color/secret`，带 `group`/`description`/`placeholder`/`min/max/step`；
**非法字段被丢弃，模组那一行写明 `settings <key>` 及原因**，模组继续运行。
**BrimDeck 现状**：`ModuleSettingKind` 只有 `Number/Toggle/Choice`；非法声明会在 `ModuleKind.Of` 的校验里抛错，
整个模组 `Failed`。
**落点**：`src/BrimDeck.UI/ModuleViews.cs` 扩枚举 + `ModuleSetting` 加 `Group/Description/Placeholder`；
`ModulePolicy` / `ModuleKind` 的校验从"抛错"改为"丢弃该字段并记录"。

### A8. 存储：`delete` / `keys` + `data`/`cache` 分离

**Folium**：`get/set/has/delete/keys`；`api.storage.paths.data` 与 `.cache` 两个目录，缓存可被用户随时清空，
文档明确"缓存随时可能消失，自己控制体积"。
**BrimDeck 现状**：`IModuleStorage` 只有 `Read/Write`，单键 1 MiB，事件多就得自己想办法分片。
**落点**：`Contracts.cs` 加 `Delete/Keys`；`ModuleStorage.cs` 实现；`ModuleHost` 暴露 `StorageDirectory`
（`modules-data/<id>/files`）；缓存目录独立成 `modules-cache/<id>/user`（与加载器缓存区分）。

### A9. 原子安装 + 包校验

**Folium**：拖入 zip → 拒绝路径穿越/绝对路径；≤64 MB 包、≤64 MB 解压、≤32 MB 单文件、≤2000 条目；
先解压到 `.staging/` 校验（清单、声明的入口文件必须存在）→ 通过才换入，失败回滚、旧版保留；同 id 覆盖即升级。
**BrimDeck**：现在只能手动拷目录。
**落点**：`ModuleBundleInstaller` 已有"从 exe 同级 mods 复制"的逻辑，可以扩展成通用安装器；
`build/ModulePackage.targets` 已有白名单，可作为"包内容合法"的定义。

### A10. `experimental[]` + 废弃表

**Folium**：稳定面 1.x 只增不改；实验接口必须逐个在清单里选用，任何 minor 可变；旧名字保留到 2.0，
用一次在模组行提醒一次；`folium:check` 同样列出来。
**落点**：`ModuleManifest` 加 `Experimental`；`ModuleApi` 加 `IsExperimental(id)`；
`ModulePolicy.Validate` 里校验"用了实验成员但没声明"；`tools/BrimDeck.ModuleDocs` 增加废弃表输出。

### A11. 稳定性约束成文

**Folium**：单模组失败不影响宿主与其他模组；依赖图损坏只波及相关子图；每次加载周期先停用再激活，不叠加多代；
导出/全局操作互斥。
**BrimDeck**：行为基本一致（按 `Generation` 归属、依赖失败只影响依赖方），但**文档没写死**。
**落点**：`docs/zh-CN/modules.md` 补一节"宿主承诺"。

---

## B 档：需改造

### B1. `main` / `client` 双入口

**Folium**：`client` 在渲染进程（无 Node），`main` 在主进程（完整 Node，只有 `api` 是契约）。
**改造**：WPF 单进程无法复刻权限分层。可借的是"**按用途分入口**"：`IModule`（UI）与 `IModuleBackground`
（无 UI、可跑长任务）分离，后者仍在同一进程，只是约定不碰 UI。
**收益**：让"外部数据源"这类代码集中在一处，便于审核。**代价**：没有权限收益，只有组织收益。

### B2. 容器 + ShadowRoot 隔离

**Folium**：宿主创建容器、注入 `--folium-*` 变量、模组不得查询容器外的 DOM。
**改造**：WPF 没有样式隔离，全局资源字典共享。可借的是**成文禁令**：模组不得修改 `Application.Resources`、
不得操作主窗口。这属于文档与审核规则，不是技术隔离。

### B3. 视图上下文注入

**Folium**：`onFrame / onResize / onChange / every / onDispose`，监听属于订阅它的那次挂载。
**改造**：`onResize` 已有对应（`SetViewport`），缺 `onFrame`（WPF 的 `CompositionTarget.Rendering`，需节流）、
`onChange`（按上下文声明它能观察什么）、`onVisible`（我们讨论过，需要宿主驱动）。
**落点**：`ModuleViewContext` 加三个订阅方法；`ModuleHost.Views` 在挂载/卸载/可见性变化处驱动。

### B4. `platform.raw` 式的外部数据源

**Folium** 的做法（可直接照搬结构）：不裸转发（凭据由宿主注入）、端点分类表（读/写/永远拒绝，表外拒）、
参数净化（能拼进 URL 的字段只收非负整数，防 `../`）、响应脱敏（任意深度剥凭据）、额度与在途上限、类型化错误码。
**改造**：BrimDeck 目前没有任何外部数据能力，`platform.raw` 没有直接对应物；但**本地应用数据**（如本模组的 vivo
日历）可以照这个形状做：声明 `datasource.<app>.read`、宿主提供"已知数据源表"、模组只拿解析后的快照、拿不到路径。
**这也是本模组应该被规范化改造的地方**——现在是硬编码路径 + 直接 P/Invoke。

### B5. `internals`（自由度出口）

**Folium**：直接给宿主内部对象，无兼容承诺，但必须钉死 `folia` 版本范围，否则抛
`internals-require-folia-range`；面板与确认窗口标「使用内部接口」；"反复出现的用法应变成稳定接口"。
**改造**：BrimDeck 没有这个概念，但可以给 `IModuleHostInfo` 加一个明确的"非契约访问"通道（或者干脆不做，
只保留"公开 API 不够就提需求"的流程）。

### B6. 权限粒度到成员

**Folium**：`filesystem.data` → `storage`；`render.export` → `exportVideo`；`playback.*` 分读/写。
**BrimDeck**：权限是能力级（`usage.read` 等），服务接口整体授权。
**改造**：当服务开始有写操作时，需要按方法分权限（例如 `calendar.write`），否则"只读"与"可写"无法区分。

---

## C 档：不适用

| 能力 | 原因 |
| --- | --- |
| 导出窗口 / ffmpeg / `export-*` | 音乐视频导出特有；BrimDeck 无对应产物 |
| CSS parts / `styles` 注册表 | Web 技术栈；WPF 的等价物是资源字典，无法安全开放 |
| `folium.omni` / `account` / `equalizer` | 音乐域服务；BrimDeck 的对应域是 `usage/media/system` |
| `api.rpc`（main↔client） | 双进程特有 |
| `embedOrigins`（内嵌网页） | 无 WebView 场景 |

---

## 两个"危险能力"的范式（单独列）

### 1. `platform.raw`：开放第三方私有接口

要点不是"允许"，而是**把危险的每一步都收窄**：

| 风险 | 做法 |
| --- | --- |
| 凭据外泄 | 请求经宿主代理，会话由 provider 注入，模组永远拿不到 cookie/token |
| 越权端点 | 分类表：读 / 写 / 永远拒绝（登录、改资料、转发任意请求、解密流量），表外一律 `endpoint-denied` |
| 路径注入 | 会拼进上游 URL 的参数字段只收非负整数或纯数字字符串，防 `../` |
| 用户身份泄漏 | "当前用户"类参数由宿主代填，模组不知道 uid |
| 响应夹带 | 返回体在任意深度剥掉凭据字段 |
| 滥用 | 120 次/分、8 在途，按模组 id 计，重载不重置；停用时中止在途请求 |
| 静默失败 | 类型化 `code`：`permission-denied` / `endpoint-denied` / `rate-limited` / `account-changed` / `aborted` … |
| 长期维护 | 有稳定接口时日志提示"改用 Omni"（每个端点一次） |

### 2. `internals`：给自由，不给承诺

- 访问前必须钉死宿主版本范围（否则抛错）；
- 面板与确认窗口明确标注；
- "同一种 internals 用法在多个模组里反复出现 → 它该变成稳定接口"——把长尾需求转成路线图信号。

---

## AI 审核在链路里的位置

| 环节 | 负责什么 | 谁来做 |
| --- | --- | --- |
| 静态检查 | 权限声明与调用是否一致、有没有用废弃名、包结构是否合法 | `folium:check` 类工具 |
| 行为边界 | 默认不给能力、端点表、额度 | 宿主 |
| 语义审查 | 有没有越权意图、阻塞、隐藏副作用 | **AI + 人** |
| 可追溯 | 谁签的、内容有没有变 | 签名 |

AI 能查的：权限与调用不一致、写 `themes/` 或别的模组目录、`Thread.Sleep` / `.Result` / 同步 IO、
`Dispose` 之外起线程、未声明 P/Invoke、把用户数据发往外部。
AI 查不到的：运行时条件触发、动态加载、混淆逻辑、性能退化。

**结论**：AI 审核是第三道，前面两道必须可执行。只靠 AI 看代码，等于赌"没人写得出来"。

---

## 市场条目与影响范围（scope）

### 一份真实条目长什么样

folium-compound 里一个「覆盖 Windows 任务栏的沉浸式歌词」模组，条目包含：

| 字段 | 值（示例） | 作用 |
| --- | --- | --- |
| 权限 | `runtime.playback`、`playback.control` | 读播放状态、控制播放 |
| 入口 | `main`（完整 Node.js 权限） | **真正的授权在这里** |
| 适用版本 | `>=0.7.21 <0.8.0` | 钉死宿主范围 |
| 维护者 / 源码 | `moneker` / GitHub | 可追溯 |
| **审查的提交** | `ad5b6c11b19a` | 审核绑定到具体 commit，不是"这个模组" |
| 签名密钥 / 时间 | `folium-ci-2026-1` / 2026-10-19 | 谁签的、何时签 |
| 签名摘要 | `sha256:bbe8dd…` | 签名覆盖的内容（清单式，可复现） |
| 文件 SHA-256 | `1680a8c5…` | 分发文件本身的指纹 |

后两项是两层：签名摘要证明"目录内容没被改"，文件 SHA-256 让用户核对"下载到的这个 zip 就是被审过的那个"。

三个观察：

1. **`runtime.playback` 是 1.x 的旧名字**（现名 `playback.read`）。Folium 允许旧名继续用，模组行提醒一次——说明"旧名保留 + 提醒"不是纸面规则，是实际在跑的。
2. **真正给它"替换任务栏"能力的是 `main`，不是那两条权限。** 权限表只覆盖播放控制；覆盖任务栏要调 Win32（AppBar / 置顶窗口），那是 `main` 的完整 Node 权限给的。
3. **审核绑 commit，签名绑字节。** 作者改一行，签名摘要失配 → 条目变成「签名不匹配（`digest-mismatch`）」→ 授权撤销、模组禁用，直到重新确认。

### 缺口：权限表达能力类别，不表达影响半径

一个"覆盖任务栏"的模组和一个"改歌词配色"的模组，权限列表可能都只写 `playback.control`。
但前者失败时用户**连开始菜单都点不到**。所以条目除权限外还需要一个影响范围声明：

```jsonc
"scope": ["shell.taskbar"],     // 或 window.overlay / filesystem / network / registry
"intrusive": true               // 确认窗口用更强的措辞，面板加显眼标记
```

BrimDeck 若做市场，这条比多写十个权限名更有用——**用户看得懂"它会盖住我的任务栏"，看不懂"它声明了 playback.control"**。

### 任务栏类模组的三条前置（若 BrimDeck 允许）

技术上 WPF 单进程可以替换任务栏（AppBar / 置顶窗口 / 枚举外壳），但风险等级完全不同：

| 风险 | Folium（双进程） | BrimDeck（同进程） |
| --- | --- | --- |
| 模组崩溃 | main 崩了整机进程一起没 | **宿主一起没**，任务栏可能停在"被盖住"的状态 |
| 恢复 | 下次启动问"要不要不带模组" | 需要同样机制，否则用户连开始菜单都没有 |
| 冲突 | 与 Folia 自身窗口无关 | 与宿主的窗口管理直接冲突 |

缺一不可：

1. **安全启动**（见 `SANDBOX.md`：记号 + 心跳 + 一次性免模组启动），否则一次失败就锁死；
2. **独立能力点** `shell.taskbar`（可声明、可展示、可拒绝），不要混进 `ui.*`；
3. **外壳复位成文**：停用、崩溃、宿主退出都必须还原任务栏，不能靠模组自觉。

### BrimDeck 市场条目的最小字段集

```
名称 / 一句话描述
影响范围 scope            ← 新增，见上
权限                      ← 现有 permissions
是否需要 main 级权限      ← 单独一行加粗，这才是分水岭
适用契约版本范围 / 依赖
维护者 / 源码 / 许可证
审查的提交 / 签名密钥 / 签名时间 / 签名摘要 / 文件 SHA-256
最近一次变更说明
```

## 宿主更新后的兼容提示（弃用通道）

### 现状问题：旧写法等于打不开

`ModulePolicy.Validate` 对未知能力/权限是**直接抛错**：

```csharp
if (!ModuleApi.Capabilities.Contains(capability)) throw new ModuleApiException("unknown-capability", ...);
if (!ModuleApi.Permissions.Contains(permission)) throw new ModuleApiException("unknown-permission", ...);
```

所以一旦宿主改了名字，用旧名的模组不是"提醒一下"，而是**整模组 Failed**。这比 Folium 差一档——
那边旧名继续能用，只在模组行提醒一次。

### 提示长什么样

```
module.json capability "ui.legacy": removed in BrimDeck 2.0, use "ui.pages"
module.json permission "runtime.playback": removed in BrimDeck 2.0, use "playback.read"
IModuleContext.Publish: removed in BrimDeck 2.0, use IModulePlatform.Events.Publish
```

三个展示位：

| 位置 | 内容 |
| --- | --- |
| 模组卡片（折叠） | `⚠ 2 处旧写法` |
| 模组卡片（展开） | 逐条：旧名 → 新名 → 将在哪个版本移除 |
| 启用确认窗口 | 同样逐条列出——用户正在同意它，这里最需要知道 |
| 宿主更新后 | 一次性横幅：「3 个模组使用了将在 2.0 移除的写法」，点进去看列表 |

### 四个检测面，手段各不相同

| 面 | 例子 | 手段 | 时机 |
| --- | --- | --- | --- |
| 清单字段 | `nameEn` 改名 | JSON 解析时做字段别名映射 | 运行期 |
| 能力 / 权限名 | `runtime.playback` | **别名表**：查表命中则用新名继续，同时记一条 | 运行期 |
| SDK 成员 | `IModuleContext.Publish` | `[Obsolete("removed in 2.0, use …", false)]` | **编译期** |
| 成员调用 | 模组 DLL 里调了哪个方法 | IL 扫描（Mono.Cecil） | **工具期**（检查器） |

关键现实：**.NET 运行期无法发现"模组调用了哪个成员"**——没有动态代理，方法调用是静态绑定的。
所以别指望运行时扫描 API 使用；能做的只有：清单别名表（运行期）、`[Obsolete]`（编译期）、
IL 扫描（检查器里，离线）。这一点比 Folium 更省事，因为编译器替我们做了大半。

### 数据结构与落点

```csharp
// src/BrimDeck.Modules.Abstractions/ModuleDeprecations.cs（新）
public enum ModuleDiagnosticLevel { Info, Deprecated, Removed }

public sealed record ModuleDiagnostic(
    ModuleDiagnosticLevel Level, string Kind, string Old, string? New, int? RemovedInMajor, string Message);

public static class ModuleDeprecations
{
    public static IReadOnlyList<ModuleDiagnostic> Table { get; } = [ /* 随宿主版本发布 */ ];
    public static ModuleDiagnostic? Lookup(string kind, string name);
}
```

| 落点 | 改动 |
| --- | --- |
| `ModulePolicy.Validate` | 拆成 `Validate`（致命）+ `Diagnose`（警告）；能力/权限检查前先过别名表 |
| `ModuleInfo`（DTO） | 加 `IReadOnlyList<ModuleDiagnostic> Diagnostics` |
| `SettingsModuleCard.cs` | 折叠行显示条数，展开逐条 |
| 启用确认 | 列出诊断，措辞用"将在 X 移除"而非"已移除" |
| `SettingsModules.cs` | 宿主版本变化时，对全部已发现模组跑一次清单扫描，出兼容报告 |
| `tools/BrimDeck.ModuleDocs` | 由同一张表生成文档里的"已弃用"小节；`--check` 校验样例是否用了弃用写法 |

### 分级与策略

- **Info**：仅提示（例如字段顺序建议）。
- **Deprecated**：仍可用，将在下一个 major 移除；模组行常驻显示。
- **Removed**：已不可用——此时才允许 `Validate` 抛错。

配套一个作者开关：`设置 → 模组 → 把弃用当错误`（等价于 `TreatWarningsAsErrors`），
CI 里打开，日常关掉。这样"兼容期"里作者能被强制清理，而用户不会因为旧写法用不了。

### 与 Folium 的差别

Folium 的模组是 JS，只能在运行期通过 Proxy 或静态检查发现旧名字；
BrimDeck 的模组是编译型语言，`[Obsolete]` 在**作者敲代码时**就提示，比"装上去才提醒"早得多。
所以这块 BrimDeck 有条件做得更好：**编译期提示 + 清单别名 + 更新后报告**，三层覆盖。

## 内部接口与宿主版本不匹配

这两个和"弃用"不是一类：弃用是"还能跑，但以后会断"；这两个是"现在就有条件"——
一个要求作者知情，一个要求宿主明确拒绝。**都不能含糊地报成 `Failed`**。

### 1. 使用内部接口（internals）

**Folium 的做法**：`folium.internals` 直接给宿主内部对象，**没有任何兼容承诺**；
访问的前提是清单里钉了 `folia` 版本范围，否则抛 `internals-require-folia-range`；
启用确认窗口与模组面板都标「使用内部接口」。

**BrimDeck 的现状**：没有 `internals` 门面，但**等价风险已经存在**——`ModuleCatalog` 把
`BrimDeck.UI` 与 `BrimDeck.Core` 都列为共享程序集：

```csharp
new ModuleCatalog(directory, cacheDirectory, [typeof(ModuleUi).Assembly, typeof(DeckSettings).Assembly])
```

`BrimDeck.UI` 是契约，没问题；**`BrimDeck.Core` 不是**（文档原话："不是通用模组 SDK 的整体兼容承诺"）。
模组只要写一行 `using BrimDeck.Core;` 就绕过了契约，而且宿主现在根本不知道。

**检测方式**（发现期做一次，不必加载程序集）：

```csharp
using var pe = new PEReader(File.OpenRead(dll));
foreach (var handle in pe.GetMetadataReader().AssemblyReferences)
{
    string name = pe.GetMetadataReader().GetString(pe.GetMetadataReader().GetAssemblyReference(handle).Name);
    // 契约：BrimDeck.Modules.Abstractions / BrimDeck.UI
    // 内部：BrimDeck.Core / BrimDeck.Modules / BrimDeck
    // 其余：System.*、第三方私有依赖，忽略
}
```

命中内部程序集 → 记 `UsesInternals`，并**强制要求清单声明宿主版本范围**（照 Folium 的
`internals-require-folia-range`）；没声明就拒绝加载并给出可操作的提示。

### 2. 宿主版本不匹配（host-version-mismatch）

**Folium**：清单里 `folia: ">=0.7.0 <0.8.0"`，超出范围即拒绝，条目写清楚要哪个版本。

**BrimDeck**：现在只有 `apiVersion` / `apiMinor`（**契约版本**），没有**应用版本**。
两者不是一回事：契约 1.5 可能在 1.4.0 和 1.5.0 两个应用版本里都存在，但内部接口只有具体版本才对。
所以清单需要新增：

```jsonc
"host": { "minimumVersion": "1.5.0", "maximumVersionExclusive": "2.0.0" }
```

结构与现有的 `ModuleDependency` 完全一致（`MinimumVersion` + `MaximumVersionExclusive`），
解析器可以直接复用。校验时机：**发现期**（读清单即可判），不进入激活流程。

### 3. 三种提示的分工

| 类型 | 触发 | 模组能不能跑 | 展示 |
| --- | --- | --- | --- |
| 弃用（Deprecated） | 用了旧名字/旧成员 | ✅ 能 | 卡片常驻 `⚠ N 处旧写法`，展开逐条 |
| 内部接口（Internals） | 引用了非契约程序集 | ✅ 能（钉了版本才允许） | 卡片徽章「使用内部接口」+ 确认窗口单独一段 |
| 版本不匹配（HostMismatch） | 宿主版本不在清单范围内 | ❌ 不能 | 状态行写明「需要 1.5.0 ≤ 版本 < 2.0.0，当前 2.0.1」，不进激活队列 |

前两类是"知情同意"，第三类是"明确拒绝"——拒绝时**必须给出可执行的下一步**（升级模组或降级宿主），
而不是 `Failed` + 一段异常。

### 4. 落点

| 位置 | 改动 |
| --- | --- |
| `ModuleManifest` | 新增 `Host`（复用 `ModuleDependency` 的上下界形状） |
| `ModuleStatus` | 追加 `HostMismatch = 6`（沿用 1.3 追加 `Waiting = 5` 的做法）；`Internals` 不改状态，只加标记 |
| `ModuleCatalog` | 发现期读 `AssemblyRef` 判内部接口；读清单判版本范围；两者写入 `DiscoveredModule` |
| `ModulePolicy.Validate` | `UsesInternals && Host is null` → 拒绝并提示"引用内部程序集必须声明 host 版本范围" |
| `ModuleInfo` | 加 `bool UsesInternals` 与 `HostRangeText` |
| `SettingsModuleCard.cs` | 徽章 + 状态行；版本不匹配时把「启用」按钮置灰并给原因 |
| 启用确认窗口 | 内部接口单独一段（照 Folium 的措辞：无兼容承诺，版本一变就可能失效） |

### 5. 为什么值得做

现在一个模组引用 `BrimDeck.Core` 时，宿主**完全无感**：不报错、不提示、升级后可能悄悄坏掉。
加上这两条之后，"用了不该用的东西"和"版本不对"都会在**启用之前**说清楚——
这也是把 `internals` 那套"给自由、不给承诺"引进来的最小代价版本。

## 落地顺序（建议）

| 顺序 | 内容 | 量级 |
| --- | --- | --- |
| 1 | A1 安全启动（记号 + 心跳 + 一次性免模组启动） | 1–2 天 |
| 2 | A4 开发目录豁免 + A5 开销呈现 | 1 天 |
| 3 | A7 设置字段扩容 + 非法声明降级 | 1–2 天 |
| 4 | A8 存储 `delete/keys` + `data/cache` 分离 | 1 天 |
| 5 | A3 签名四状态（先只展示，不强制） | 2–3 天 |
| 6 | A10 `experimental[]` + 废弃表 | 1–2 天 |
| 7 | A9 原子安装 | 2–3 天 |
| 8 | B3 上下文订阅（`onVisible` 先行） | 1 天 |
| 9 | 市场条目字段 + `scope` 声明（先只做字段与展示） | 1 天 |
| 9.5 | 弃用通道：别名表 + 诊断列表 + 更新后报告 + 作者开关 | 1 天 |
| 9.6 | 内部接口检测 + `host` 版本范围 + `HostMismatch` 状态 | 1 天 |
| 10 | B4/B5 外部数据源与自由度出口 | 视需求 |

---

## 一句话

Folium 的可学之处不在 API 数量，而在**每件事都有人负责**：
签名管"谁写的、改没改"，审核管"这类代码允不允许存在"，AI 管"语义有没有越界"，
安全启动管"崩了还能不能打开"，`experimental` 管"实验面敢不敢用"，
弃用通道管"旧写法什么时候停"，内部接口与 host 版本范围管"它凭什么能跑"，
`scope` 管"用户知不知道自己在同意什么"。
BrimDeck 现在前四件只做了一半，后两件完全没做——而它们恰好是"开放"能持续的前提。
