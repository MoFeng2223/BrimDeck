# 模组静态检查器与启动恢复

适用本体契约 1.6。这两项是宿主及作者工具，不增加模组侧必需接口，也不改变旧清单和设置值。开发入口见[贡献指南](contributing.md)，运行规则见[宿主规则](host-rules.md)。

## 检查一个分发目录

先构建并打包模组，然后对包含 `module.json`、入口 DLL、私有依赖和资源的目录检查。不要对只有源码的项目目录检查。独立开发包无需宿主仓库：

```powershell
dotnet E:/BrimDeck-sdk/tools/checker/BrimDeck.ModuleCheck.dll E:/MyModule-build/package/my-module --json --warnings-as-errors
```

维护者在仓库内也可直接构建并运行源码工具：

```powershell
dotnet build mods/notes/BrimDeck.Sample.csproj -c Release --artifacts-path E:/BrimDeck-build -t:GetModulePackage
dotnet run --project tools/BrimDeck.ModuleCheck/BrimDeck.ModuleCheck.csproj -- E:/BrimDeck-build/bin/BrimDeck.Sample/release/package/sample-notes --json
```

命令及参数：

```text
BrimDeck.ModuleCheck <package-directory> [--modules-root <directory>] [--json] [--warnings-as-errors]
```

| 参数／结果 | 行为 |
| --- | --- |
| 默认 | 输出可读报告，不执行模组代码，不创建应用数据或加载缓存 |
| `--json` | 输出结构化报告；`Mode=static`、`ExecutesModuleCode=false` |
| `--modules-root` | 提供安装目录以核对依赖版本、缺失项和循环；只检查有关子图，不判断启用或批准状态 |
| `--warnings-as-errors` | 任一警告使退出码变成 1，供作者 CI 使用；不改变宿主日常加载规则 |
| 退出码 0 | 静态检查通过当前策略 |
| 退出码 1 | 发现错误，或严格模式下有警告 |
| 退出码 2 | 参数缺失或不支持 |

JSON 的 `Passed` 表示没有静态错误；`WarningsAsErrors` 表示本次策略；`Accepted` 对应最终退出结果。存在警告时可能 `Passed=true` 而严格模式下 `Accepted=false`。

## 实际检查范围

- 清单：身份、契约、必需能力、权限、依赖版本上下界、入口字段、重复字段与未知字段提示。
- 包结构：禁止链接文件／目录及链接的上层路径，最多 2000 个文件、64 MiB；运行时和检查器共同限制目录深度 64、目录条目 6000、清单 1 MiB。超限与格式错误均返回结构化失败，不将检查器异常当作通过。
- DLL 元数据：程序集身份、私有依赖是否随包提供、是否夹带宿主／共享 SDK、全部随包程序集对桌面主程序的直接引用、P/Invoke 声明及模块初始化器。
- 入口：类型是否存在、是否公开且非抽象、是否有公开无参构造、可解析类型层次是否实现 `IModule`。不能完整解析的继承形状报告未验证，不能误报成已经证明兼容。
- SDK：依据能力目录与已知新增成员表提示能力／权限声明，检查可识别的新接口是否要求更高 minor。类型引用只能说明使用线索，不能证明发生过受限调用，因此声明提醒为警告。
- 依赖：指定目录后核对相关模组的清单版本及循环；未提供目录时明确提示检查不完整。

检查使用 PE 元数据读取，不调用 `Assembly.Load`、模组构造函数、`ActivateAsync`、视图工厂、设置 getter 或存储 API。报告不列出“实际注册的页面数量”或“用户存储键”，因为取得这些结果必须执行代码或读取业务数据。

静态扫描不能证明反射、动态生成代码、运行条件、线程阻塞、注册内容、字段返回值、原生库行为或安全性。已知 minor 检查也不是完整 IL 语义证明；枚举常量等可能在编译后失去原始名称。运行表现仍需在可信测试环境中验收。当前没有 `--probe` 执行模式，也不会把“离线检查”暗中变成激活测试。

## 常见报告

| 代码 | 含义／处理 |
| --- | --- |
| `desktop-reference-denied` | 模组或私有依赖引用桌面主程序；改用公开 SDK，改变程序集名称大小写也不能绕过 |
| `shared-assembly-bundled` | 打包了宿主或共享 SDK；复用通用打包目标并移除重复副本 |
| `private-dependency-missing` | 缺少随包的私有 DLL |
| `entry-type-missing`／`invalid-entry-type` | 核对清单类型名及公开无参构造 |
| `entry-contract-unverified` | 类型层次不能完整静态解析；需要进一步检查，不能当作已验证 |
| `sdk-minor-too-low` | 使用了已知的后续接口，调整清单最低 minor |
| `sdk-capability-not-declared`／`sdk-permission-not-declared` | 检查该类型是否实际用于受限服务，再补相应声明 |
| `native-import`／`module-initializer-present` | 观察到原生依赖或初始化器；没有执行，作者与审查者需要检查其行为 |
| `dependency-context-required` | 提供 `--modules-root` 完成依赖版本核对 |
| `package-changed` | 检查期间文件变化，停止更新后重试 |

## 启动恢复

本体在任何候选模组的程序集加载、初始化器、构造和激活之前，先将本次启动记录写入应用数据目录的 `module-startup.json`。记录只包含本次标识、时间及候选 ID，不含密钥；不用于自动归罪某个模组。

清除启动记录需要同时满足：主窗口已完成首屏呈现、当前配置操作及模组启动／等待阶段结束，以及至少两次界面心跳。心跳每秒运行，但不每秒写盘；通过检查点后停止，后续启用或重载时重新记录。慢启动不取消、不自动停用，未完成的异步启动不会仅因窗口先显示或超过五秒而丢掉记录。

异常终止后再次打开，宿主会在运行模组代码之前询问是否本次不加载模组，并列出本次启动的候选模组（最多 5 个）；启动记录损坏时说明损坏。默认选择不加载。恢复决定完成前，刷新及其他入口不能抢先启动模组。选择不加载时：

- 保留总开关、各模组开关、批准摘要及随附包收据；本次也不自动升级随附包或更新其批准。
- 管理页显示“本次未启动”，仍能关闭已经启用的问题模组；重新启用需要重启应用。
- 刷新只能读取列表，不能越过本次运行的加载限制。
- 正常下次启动恢复普通流程；可随时用 `BrimDeck.exe --no-modules` 主动进行一次无模组启动。

全局模组系统关闭时不询问、不加载。损坏的启动记录按需要恢复处理。删除失败但仍可读取的记录，可用绑定其确切内容的作废记录消除重复询问；旧作废记录不能隐藏新一次启动。无法写入新的启动记录时，本次不运行模组，并在管理页说明数据目录问题。

正常响应的退出会清除未完成启动记录；杀进程或卡住而未完成退出时保留。启动记录覆盖启动和重载阶段，当前没有运行期无响应看门狗，也不提供进程内权限沙箱。只有恢复流程本身不会修改偏好，用户在恢复界面主动关闭模组仍会保存该操作。
