# 沙箱、残留与第三方扩展的治理

本文回答三个问题：

1. **为什么**模组能留下"删了还在"的永久残留？
2. 在 **.NET + 同进程 WPF** 的前提下，沙箱能做什么、不能做什么？
3. 如果要走 **"签名 + 审核"**（形状参照 Minecraft Forge：注册表 / 事件总线 / 服务），
   契约上还缺哪几块？

> 标注「现状」的是 1.5 契约下实测行为；标注「提案」的是尚未实现的设计。

---

## 1. 残留是怎么产生的

四步因果链，缺一步都不会有问题：

| 步骤 | 事实 | 后果 |
| --- | --- | --- |
| 1 | 模组是**普通程序集**，跑在宿主进程里。.NET 10 已移除 CAS 与 AppDomain 级权限隔离，`AssemblyLoadContext` 只负责加载/卸载 | 模组拥有当前用户的全部文件、注册表、网络、进程权限 |
| 2 | **贡献点是内存态**：页面、活动、设置、订阅由宿主记账，停用即撤 | 这部分天然可回滚 |
| 3 | **文件 / 注册表 / 子进程是外部态**：模组自己 `File.WriteAllText`、写 `HKCU\...\Run`、起 exe，宿主完全不知道 | 宿主**没有可撤销的对象** |
| 4 | 内容摘要只回答"跑的是不是你批准的那份代码" | 不回答"这份代码干了什么" |

一句话：**宿主能撤销"注册"，撤销不了"副作用"。**

现成的例子就在本模组：`modules-data/calendar/` 在卸载后仍在（`IModuleStorage` 没有 Delete，宿主也不清理），
vivo 镜像还被写进了 `events.json`。它没有恶意，但现象与"恶意残留"一模一样。

---

## 2. 沙箱能做到什么

先给结论：**只要模组与宿主同进程、共用一棵 WPF 可视化树，就不存在真正的沙箱。**
这不是实现偷懒——.NET 从 Core 3.0 起就没有进程内权限沙箱了。

| 方案 | 能挡住 | 代价 | 判断 |
| --- | --- | --- | --- |
| 进程内权限沙箱（CAS / 受限 AppDomain） | 一切 | .NET 10 无此能力 | **不存在** |
| 进程外渲染（子进程 + 位图/IPC） | 文件、网络、注册表 | 模组不能再放 WPF 控件，只能提交图片或有限描述 | 与"和本体一个质感"直接冲突 |
| AppContainer / Job Object + 低完整性令牌 | 文件、网络 | 仍是独立进程，无法共享 UI 树 | 同上 |
| **契约收紧 + 启动体检 + 签名审核** | 挡不住恶意，但能**消掉动机、暴露异常、可追溯** | 纯宿主侧，成本低 | **当前唯一现实的一条** |

推论：任何文档都不要暗示"模组是安全的"。现有 `modules.md` 写了"不是安全沙箱"，
但缺少反向约束——应补一句：*"内容摘要与能力声明用于兼容性与知情，不构成安全边界。"*

### 2.1 隔离 vs 遏制：两个词不能混用

讨论"沙箱"时，实际在说两件不同的事。BrimDeck 的文档现在把两者笼统称作"安全沙箱"，值得拆开：

| | **隔离**（isolation） | **遏制**（containment） |
| --- | --- | --- |
| 防的是 | 模组**拿到**不该有的能力 | 模组的**错误扩散**到别处 |
| 手段 | 进程、权限、句柄边界 | 错误边界 + 生命周期 + 计量 |
| 模组崩溃时 | 只死那一个单元 | 整个宿主仍可能一起死 |
| 本宿主 | **做不到**（同进程） | **可以做到**，且大部分已有 |

结论：BrimDeck 应当承诺的是"遏制"，而不是"沙箱"。文档里凡是写"沙箱"的地方，
要么改成"遏制"，要么明确写"这不是安全边界"。

### 2.2 参考实现：Folium 的 `main` / `client` 双入口

Folia 的模组平台 Folium 把入口分成 `main`（主进程）与 `client`（渲染进程）。`client` 拿不到
`fs`、`child_process`，只能用宿主经 IPC 暴露的 API，未声明的调用直接失败（fail closed）；
`pickFile` 返回 `folia-mod://_files/…` 而不是路径，因为**路径本身是能力**。

它提供的是**权限边界**（模组改不了机器），不是**界面隔离**：

| Folium 的规则 | 防的是什么 | 没防住什么 |
| --- | --- | --- |
| `client` 无 Node 能力 | 模组直接读写文件、起进程 | —— |
| 启动 5 秒 | 只做**计时**，条文明确写"超过不算错、继续等、不取消" | 同步死循环照样卡住界面线程 |
| 每个 `activate` / 监听 / `mount` 各自错误边界 | 一个处理器抛错拖垮整轮分发 | 栈溢出、OOM、原生崩溃 |
| 每处一行错误计数 + 每次运行只提示一次，可"不再提示" | 提示轰炸 | 错误本身 |
| **永不因出错自动停用、替换或取消选中** | 宿主擅自止损 | —— |
| 迟到/旧代的错误不记在新实例头上 | 旧任务污染新状态 | —— |

关键点：**渲染进程和界面是同一个 JS 线程**，所以 `client` 入口既不是沙箱，也不防"搞崩软件"。
真正的隔离来自"双进程"这个结构本身——主进程持有系统能力，渲染进程只拿句柄。

### 2.3 本宿主能抄的部分

| 遏制能力 | Folium | BrimDeck 1.5 |
| --- | --- | --- |
| 慢 ≠ 错（超时继续等，标"慢"） | ✅ | ✅ 已有（`ModuleInfo.Slow`） |
| 迟到/旧代错误不记在新实例 | ✅ | ✅ 已有（按 `Generation` 归属） |
| 错误按处理器隔离并计量 | ✅ 每处一行 + 次数 | ⚠️ 有 `ModuleFault`、`ModulePerformance.Errors`，但无"按来源聚合"的面板 |
| 每模组每次运行最多提示一次 + 不再提示 | ✅ | ❌ |
| "出错不自动停用/替换/取消选中"成文 | ✅ | ⚠️ 行为如此，但文档没写死 |
| 导出窗口的降级环境 | ✅ | ❌（无第二运行环境） |

也就是说：**界面侧的遏制，BrimDeck 结构上已经具备，缺的是"呈现"与"成文"**——
按来源聚合的错误行、一次提示、以及"宿主永不自动停用"写成契约。这三样都是纯增量。

至于"渲染单元崩了怎么办"，Folium 也没有答案（重载窗口，宿主 UI 一起重来）；
BrimDeck 的答案只能是"别在同进程里做会崩的事"，这也是为什么本文第 3 节的 P0 全是"消掉动机"，而不是"加强拦截"。

---

## 3. 现实策略：三层，按投入排序

### P0-1 把可扩展面做成"内存贡献"（消掉动机）

模组想改外观，正路应该是**提交一个对象**，而不是**去写文件**：

```csharp
// 提案
public interface IModuleTheme
{
    string Id { get; }              // 全局唯一：calendar:midnight
    string Name { get; }
    string NameEn { get; }
    PanelThemeFile Palette { get; } // Base + Colors + Apps + Clock + Effects
}
context.Register<IModuleTheme>("theme", new MidnightTheme());
```

宿主在激活时把主题加进「设置 → 外观 → 主题」列表，停用时移除；若正被使用则回落到上一个。
**主题只存在于内存，卸载后自然没有"残留"可言**——因为根本不需要擦。

同理：皮肤给令牌（`ModulePlayerSkin`）、图标给 `IconPath`、页面给 `ModulePage`。
**正路好走，歪路才没人走。**

### P0-2 启动体检（检测，不阻止）

宿主启动时扫三处，比对"已知写入者"：

| 位置 | 检查 | 处置 |
| --- | --- | --- |
| `themes/*.json` | 文件里是否有 `"source": "module:<id>"`，该模组是否启用 | 未激活 → 不加载，设置页显示「由已停用的模组 X 留下」+ 删除按钮 |
| `settings.json` | 只允许宿主写。`SettingsStore` 已保留 5 份快照 | 结构校验失败 → 回滚到上一份快照并提示 |
| `modules-data/<id>/` | 是否存在**未安装**模组的目录 | 在模组页列出「残留数据」，提供删除 |

这层挡不住故意破坏，但保证**用户看得见、退得回**。一天量级的工作量。

### P1 权限声明（先告知，后强制）

`module.json` 的 `permissions` 目前只有数据读权限（`usage.read` 等）。扩展：

```json
"permissions": ["theme.write", "registry.autostart", "network", "native.winsqlite3"]
```

宿主在启用页明确展示。现在只能"告知"（同进程拦不住），但它有两个长期价值：
将来做进程外隔离时，权限表就是现成的执行依据；并且把"偷偷干"变成"公开声明"。

### P2 真隔离（只在开放市场时才值得）

两条路，二选一：

- **进程外渲染**：模组提交图像/有限控件描述，宿主合成。代价是失去同质感，收益是真隔离。
- **签名 + 审核**：见第 5 节。收益是可追溯与降噪，**不是隔离**。

---

## 4. 三类残留的具体处置

| 残留 | 载体 | 对策 |
| --- | --- | --- |
| 主题 / 皮肤 | `%LOCALAPPDATA%\BrimDeck\themes\*.json` | 入口改为 `IModuleTheme`（P0-1）；文件加 `source` 标记，未激活即忽略（P0-2） |
| 开机自启 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | 启动时枚举并与"已知模组"比对；发现未知项**提示**而非自动删除（可能误伤用户自己加的） |
| 后台进程 / 服务 | 独立 exe | 同进程方案管不到。这是唯一必须靠**代码签名 + 人工审核**的场景 |

补充：`modules-data/` 的孤儿目录需要有删除入口。当前 `IModuleStorage` 只有 `Read`/`Write`，
建议加 `bool Delete(string key)`，并在模组详情页提供"删除此模组的数据"。

---

## 5. 签名 + 审核：挂在已有的 digest 枢纽上

宿主已经有这道闸（`src/BrimDeck/App.Modules.cs`）：

```csharp
Settings.Modules.Enabled
  && Settings.Modules.EnabledModules.Contains(module.Manifest.Id)
  && Settings.Modules.Approvals.GetValueOrDefault(module.Manifest.Id) == module.Digest
```

签名与审核只是**在同一个 digest 上再加两层背书**，不需要新的核心机制：

```
module.json + DLLs
  └─ 宿主计算 digest（已有）
       ├─ 发布者签名   BRIMDECK.SIG     → 内容未被篡改
       ├─ 审核背书     review.json      → 有人看过这份内容
       └─ 本地批准     Approvals[id]    → 用户同意
```

### 5.1 包内新增文件（提案）

```jsonc
// BRIMDECK.SIG
{
  "alg": "ed25519",
  "key": "<base64 发布者公钥>",
  "digest": "sha256:<与宿主计算的一致>",
  "sig": "<base64 对 digest 的签名>"
}
```

```jsonc
// review.json（可以随包分发，也可以由市场下发）
{
  "id": "calendar",
  "version": "1.0.0",
  "digest": "sha256:...",
  "reviewer": "<审核方 id>",
  "issuedAt": "2026-10-26T00:00:00Z",
  "sig": "<base64 审核方对以上字段的签名>"
}
```

### 5.2 判定（提案）

```csharp
bool trusted =
       Signature.Verify(pkg.Sig, digest, TrustedPublishers)                 // 发布者已知 + 内容未改
    && (Policy.RequireReview ? Review.Verify(pkg.Review, digest, TrustedReviewers) : true)
    && Settings.Modules.Approvals.GetValueOrDefault(id) == digest;          // 用户点过同意
```

三种信任状态在模组卡片上要能看见：**未签名 / 已签名（发布者 X） / 已审核（审核方 Y）**。
它们都不等于"安全"，只等于"来源可追溯、内容没被换过"。

### 5.3 审核该看什么（因为运行时看不到）

**能看**：

- 清单声明的权限与代码实际行为是否一致（有没有声明 `native.*`、`registry`、`network` 却实际用了）
- 是否写 `themes/`、`settings.json`、或**其它模组的** `modules-data`
- 是否阻塞 UI 线程：`Thread.Sleep`、`.Result`、`.Wait()`、同步 `File`/`Http` 调用
- 是否在 `Dispose` 之外起后台线程，`Every` 的回调里是否做重活
- 是否未声明就 P/Invoke（本模组的 `winsqlite3.dll` 正是例子，必须写成 `native.winsqlite3`）

**看不到**：运行时条件触发的破坏（"只在 2099 年删文件"）、动态加载的代码、混淆后的逻辑。

所以审核的定位是**降噪 + 提高作恶成本**，不是保证。

---

## 6. Forge 形状对照：缺的不是骨架，是厚度

| Forge | 本宿主（1.5） | 差距 |
| --- | --- | --- |
| `DeferredRegister`（注册后统一 freeze） | `Register<T>` 暂存到激活提交才发布 | 等价 |
| 依赖排序 / 加载上下文 | 依赖图 + `Waiting` 状态 + 独立启动 | 等价 |
| `MOD` 总线（生命周期）+ `FORGE` 总线（全局可取消） | 单一总线，5 级优先级，2 个可取消事件 | **事件太少** |
| `Capability` 按对象查询 | `Services.Get<T>`，按能力+权限绑定 | 基本等价 |
| **Mixin / 字节码补丁** | 明确禁止（不得引用主程序集） | 本质差异 |

Forge 的威力有很大一部分来自 Mixin：能改任意方法。本宿主用"公开契约 + 事件"代替，
所以**能扩展多少，完全取决于事件暴露得够不够厚**。注册表和服务已经够用，短板在事件。

### 6.1 事件目录草案（提案）

目前只有 5 个事件；下面是日历这类模组真正需要的（`C` = 可取消，`M` = 可修改上下文）：

| 事件 | 载荷 | C | M | 线程 | 频率 |
| --- | --- | --- | --- | --- | --- |
| `PanelExpanding` / `PanelExpanded` | — | ✅/— | — | UI | 每次展开 |
| `PanelCollapsing` / `PanelCollapsed` | — | — | — | UI | 每次收起 |
| `ModulePageShowing` / `ModulePageShown` | 页面 id | ✅/— | — | UI | 每次切页 |
| `ModulePageHiding` | 页面 id | — | — | UI | 每次切页 |
| `ThemeResolving` | 可变调色板 | — | ✅ | UI | 每次换主题 |
| `SettingsSaving` | 键值快照 | ✅ | ✅ | UI | 每次写入 |
| `MinuteTick` | 本地时间 | — | — | UI | 每分钟 |
| `NotificationRequested` | `ModuleNotice` | ✅ | ✅ | UI | 每条通知 |

其中 `MinuteTick` 和 `PanelExpanded` 就能替掉本模组现在的两处轮询（跨天检查、外部源同步）。

### 6.2 事件元数据（提案）

没有这份元数据，模组作者只能猜"哪个线程、能不能改、多频繁"：

```csharp
public sealed record HostEventInfo(
    string Id, Type Payload, ModuleEventPhase Phase,
    bool Cancellable, bool Mutable, bool UiThread, string Frequency);
```

配合 `ModuleApi.Catalog` 一起发布，模组可以在运行时探测某个事件是否存在。

### 6.3 内容级注册类型（提案）

现在只能注册 UI 四种。缺的（一旦成为注册类型，"卸载即消失"就是免费的）：

- `IModuleTheme` —— 主题层
- `IModuleDataSource` —— 外部数据源（日历、用量）
- `IModuleCommand` —— 命令 + 快捷键绑定
- `IModulePlayerSkin` —— 播放器令牌

---

## 7. v1.6 最小增量清单

按投入产出排序：

1. **事件目录**：补上 6.1 里的 8 个事件 + 元数据；先做 `PanelExpanded`、`MinuteTick`、`ThemeResolving`。
2. **`IModuleTheme` 注册**：机制现成（`PanelThemeFile` + `BaseChain` + 对比度校正），只差入口与生命周期。
3. **残留体检**：主题文件 `source` 标记 + 孤儿 `modules-data` 列表 + `IModuleStorage.Delete`。
4. **签名骨架**：`BRIMDECK.SIG` + `trustedPublishers` + 卡片徽章；先不做强制。
5. **权限声明扩展**：`theme.write` / `registry.autostart` / `network` / `native.*`，只做展示。

**仍然不做**：Mixin 式补丁、任意模板替换、进程内"沙箱"。用"事件够厚"换"代码不改"。

---

## 8. 一句话总结

- 残留的根因是**内存态可撤销、外部态不可撤销**，而不是某个功能的疏漏。
- 同进程 WPF **不可能**有真沙箱；能做的只有"让正路够用、让歪路可见、让来源可追溯"。
- 把两个词分开用：**隔离**（拿不到能力）做不到，**遏制**（错误不扩散）做得到——后者是 BrimDeck 现在真正该承诺的。
- 签名与审核挂在**已有的 digest 闸门**上，是治理层，与架构正交；它们买的是可追溯与降噪，不是安全。
- Forge 的形（注册表 + 事件 + 服务）本宿主已有，神（事件足够密）还没有——**补事件，比补沙箱更有效**。
