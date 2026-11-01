# 插件平台升级与验收记录

## 起点与版本

升级从插件分支 `codex/miniapps-plugin-design` 的 `9928eb0` 开始，对应 PR #181；工作区当时无未提交修改。该提交包含 dev `5541b7e`。执行时远端 dev 已为 `a5f1eea`，没有将新的上游变更混入本次升级。实际安装／数据／加载缓存目录继续是 `mods/`、`modules-data/`、`modules-cache/`，不同时扫描第二套目录。

契约保持 1.6，既有接口、构造签名及枚举数值未改动。公共 API 和能力基线来自 `api.md` 的生成区块及 `ModuleApi.Catalog`；`BrimDeck.ModuleDocs --check` 同时检查两者。新增宿主偏好 `Modules.SchemaVersion = 1`，旧 JSON 缺字段时采用初始 schema；内容批准及既有 ID 不迁移或失效。安装清单格式、平台契约、宿主版本和业务数据版本仍是不同概念。

## 阶段一：可靠性

基线复现：402 次断言，三个缓存断言失败，随后检查器夹具路径异常，后续组没有执行。缓存路径混合 `/` 与 `\` 时，字符串比较未保护活跃目录。维护器现在先规范化完整路径，忽略大小写和末尾分隔符，也保护祖先／后代租约；清空缓存不删除业务数据。

测试构建生成 `module-test-artifacts.txt`，记录 notes、InspectionFixture、ModuleCheck 和 timers 的真实产物路径，不再由 notes 的位置推算其他工程。正常 bin 输出与统一 artifacts 输出使用同一入口：

```powershell
dotnet build BrimDeck.sln -c Release --no-restore -m:1 --tl:off
dotnet run --project tests/BrimDeck.ModuleTests -c Release --no-build -- --test-root <隔离目录> --repository <仓库>
```

可传 `--artifacts <输出清单>` 或 `--group runtime,files`；旧三个位置参数仅覆盖 notes 路径，其他位置仍从构建清单读取。0 表示所选组通过，1 表示断言／组失败，2 表示参数／产物缺失。JSON 分别列出每组的执行、失败、未选择与 UI 前提阻塞状态。UI 初始化失败时不继续依赖它的组。每次创建独立合成目录，保留结果文件用于调查，不读取真实业务数据。CI 已将插件回归列为必需步骤。

停止时先撤销接单资格、取消令牌，再逆序释放本代贡献和视图。仅同步收尾回调所在的线程可在该阶段完成最后存储写入；其他迟到任务不可写，新代不得被旧句柄覆盖。旧 `OnDispose` 出错仍继续释放其他资源并记录所属代，但维护屏障会拒绝后续删除，重复维护也不会忘记失败，要求重启后重试。第三方同步阻塞无法安全强制终止；这不是异步强停协议或进程隔离。

## C01–C34 当前证据与边界

“覆盖”只表示对应现行子行为有自动回归，不等于整项目标全部验收；硬件、真实 DPI、多屏和未实现服务不由构建成功代替。

| ID | 当前状态／证据 |
| --- | --- |
| C01 | 覆盖：CapabilityChecks，构造前拒绝未知必需能力 |
| C02 | 部分：HostApiChecks／HostRangeChecks；完整历史二进制矩阵待阶段七 |
| C03 | 覆盖：CapabilityChecks、生成器 --check，共用能力目录 |
| C04 | 覆盖：HostApiChecks，公开接口绑定而非暴露具体实现 |
| C05 | 覆盖：RuntimeChecks／LifecycleChecks，贡献和订阅暂存 |
| C06 | 覆盖：RuntimeChecks／AsyncViewChecks，失败回滚与逆序幂等清理 |
| C07 | 覆盖：LifecycleChecks／AsyncViewChecks／ModuleFileChecks，迟到和存储归属 |
| C08 | 覆盖：LifecycleChecks，无关异步启动独立运行 |
| C09 | 覆盖：CapabilityChecks，贡献白名单校验 |
| C10 | 覆盖：RuntimeChecks，重复拒绝与旧句柄失效 |
| C11 | 部分：TimerPluginChecks，组件开关／排序；平台统一偏好待完善 |
| C12 | 部分：PlatformChecks，页面请求与回退分离；统一页面和快捷键待阶段三／五 |
| C13 | 覆盖：HostApiChecks／AsyncViewChecks，结构化布局；实际设备 DPI 仍须人工 |
| C14 | 部分：视图独立归属回归；真实多屏待人工 |
| C15 | 部分：UI Refresh 与业务会话分离；编辑态主题保持待阶段三 |
| C16 | 部分：AsyncViewChecks／ViewVisibilityChecks；通用帧服务未承诺 |
| C17 | 部分：PlatformChecks，活动精确 ID；多活动仲裁待阶段四 |
| C18 | 部分：HostApiChecks，轻量通知队列；统一通知待 notice 依赖 |
| C19 | 待阶段五：统一命令和快捷键 |
| C20 | 部分：现有宿主输入优先级；插件编辑器端到端待人工 |
| C21 | 部分：SettingsSchemaChecks，五种字段；共同修订服务待阶段二 |
| C22 | 待阶段二：统一修订／补丁 |
| C23 | 部分：RuntimeChecks／TimerPluginChecks，缺失／损坏；通用迁移不承诺 |
| C24 | 部分：单文件 1 MiB 与写失败保留；总配额不在本轮承诺 |
| C25 | 不承诺：通用秘密管理服务未实现 |
| C26 | 覆盖：HostApiChecks，插件不能发布宿主事件 |
| C27 | 覆盖：PlatformChecks，稳定优先级与异常隔离 |
| C28 | 覆盖现行接口：HostApiChecks；新增授权待阶段二／五 |
| C29 | 部分：生命周期撤销；后台任务不受进程外隔离保护 |
| C30 | 覆盖：LifecycleChecks／PerformanceChecks，旧代错误与有界明细 |
| C31 | 部分：PerformanceChecks／ViewVisibilityChecks；同机性能矩阵待测 |
| C32 | 覆盖：StartupGuardChecks，本次跳过不改永久偏好 |
| C33 | 待阶段七：保留真实旧产物，不用重编替代 |
| C34 | 部分：BoundaryChecks／PackageInspectorChecks；独立作者闭环待 PowerShell 7 |

普通 Release 输出与统一 artifacts 输出均完成 **514 次插件断言、0 失败**；同轮串行 solution 构建 0 警告／0 错误，Core、Media、Windows 托管子集均以 0 退出。新增回归保留原断言，覆盖路径分隔符、大小写、末尾分隔符、祖先租约及保存失败后的重复维护屏障。首次修改验证因 XML 注释中双连字符导致构建失败，修正后重新完整验证，未运行旧产物冒充通过。

## 尚未交付

本记录不是路线图功能已完成的声明。插件管理、统一页面、多区域、命令和受控宿主操作仍按各阶段验收。完整通知必须接入已合入或明确获准的 notice 引擎，不另建假兼容实现。当前环境未发现 PowerShell 7（`pwsh`），SDK 导出和独立作者脚本尚未执行；不能将 PowerShell 5 当成满足前提。
