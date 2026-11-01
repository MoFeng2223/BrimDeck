# 外部模组示例

此示例只引用公开 SDK、公共 UI 和独立帮助库，不引用 BrimDeck 主程序。

- 「便笺」演示页面卸载后仍保留草稿，正常停用时保存。
- 「示例说明」演示独立依赖解析、可取消的异步挂载及仅在实际呈现时运行的定时器。示例刻意等待 150 ms 以展示加载和交接，不代表正式页面必须延迟。
- 组件开关演示撤销并重新注册页面、隐藏并恢复设置。
- 独立设置页演示布尔设置及持久化，并通过 `IModuleSettingsNavigation` 提供独立图标和明确的模组位置。

在仓库根目录构建，产物目录请按自己的环境调整：

```powershell
dotnet build mods/notes/BrimDeck.Sample.csproj -c Release --artifacts-path E:\BrimDeck-build
```

在应用「工具 → 模组」中打开模组文件夹，创建 `sample-notes` 子目录，从构建输出复制以下文件：

```text
module.json
BrimDeck.Sample.dll
BrimDeck.Sample.Helper.dll
BrimDeck.Sample.deps.json
```

不要复制 `BrimDeck.UI.dll`、`BrimDeck.Core.dll` 或 `BrimDeck.Modules.Abstractions.dll`；这些由宿主提供共享实例。点击刷新，再启用示例。修改源文件后重新构建并覆盖原始模组目录，刷新并再次启用。

示例不是完整便笺产品；异常退出可能丢失自上次正常保存后的草稿。开发契约、生命周期和限制见[模组文档](../../docs/zh-CN/modules.md)。

此版本需要契约 1.6。`EveryVisible` 随实际呈现停止／恢复，重新可见时立即刷新时钟，页面收起不结束业务。异步工厂用 `ModulePage.Async`，通过视图上下文的 `Lifetime` 取消加载；同步页面仍沿用原构造函数。切换到其他页面不会结束模组草稿或计时业务。
