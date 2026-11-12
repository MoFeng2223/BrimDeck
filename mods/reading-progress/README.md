# 阅读进度示例 / Reading progress sample

记录一本书的目标页数和当前页码，提供收起行进度、快捷键更新、撤销和完成高亮。无需网络、账号、本机桥接服务或硬件传感器。它是一份可独立安装的作者验收示例，不默认随应用分发。

## 构建与安装

需要 .NET 10 SDK 和从本分支构建的契约 1.10 宿主；正式版 1.3.0 尚不包含该插件平台。本分支同时修复了命令子能力目录、临时色重绘和面板状态事件，验收时请使用配套宿主，不能只把示例安装到修复前的 1.10 构建。

```powershell
dotnet build BrimDeck.sln -c Release
dotnet build mods/reading-progress/BrimDeck.ReadingProgress.csproj -c Release -t:GetModulePackage
```

将 `mods/reading-progress/bin/Release/net10.0-windows10.0.19041.0/package/sample-reading-progress/` 整个目录，复制到宿主设置中「打开模组文件夹」指向的目录。刷新并手动启用「阅读进度」。包内仅有本模组 DLL、deps.json 和 module.json，不应复制 SDK 或宿主 DLL。

也可以把本目录的源码复制到仓库外，用导出的 SDK 构建：

```powershell
dotnet build BrimDeck.ReadingProgress.csproj -c Release -t:GetModulePackage -p:BrimDeckSdk=C:/path/to/extracted-sdk
```

该模式不引用宿主源码工程。安装路径以实际数据目录为准；不要直接修改 `modules-cache`。

## 使用

- 在「阅读进度」设置中填写书名、目标页数、当前页码和每次前进的页数。
- 页面中的「＋1 页」「按步长前进」与快捷键调用同一份命令；「撤销」恢复上次页码。
- 进度保存到本模组的 `reading` 存储键。切页、收起和停用不会清除；卸载由宿主按其规则清理数据。
- 收起行显示 `阅读 当前页/目标页`；空间较窄时只保留数字，侧边竖排分行显示。它不占用持续活动的控制条。
- 达到目标后可以点「完成高亮」，向宿主申请临时强调色。「验收详情 → 释放高亮」、停用模组或用户切换主题会结束覆盖；不写入用户主题。

## 作者验收步骤

| 步骤 | 预期 |
| --- | --- |
| 首次启用 | 页面、设置、收起行部件与 8 条命令可见；不自动展开，不改变主题，不新增轮询 |
| 书名设为任意文本，目标设 7 页，步长设 5 页，当前设 0 | 页面和收起行同步显示 0/7 |
| 点「＋1 页」，再点「＋5 页」 | 两处都显示 6/7；再前进到 7/7 后按钮停止增长 |
| 点「撤销」 | 回到 6/7；再撤销不可用，不会成为无限来回切换 |
| 设置快捷键「阅读：前进一页」 | 收起或切到其他页后，快捷键只更新进度；绑定「打开阅读进度」才展开并切到本页 |
| 停用后按上述快捷键，再重新启用 | 停用期间不执行；绑定偏好由宿主保留，启用后恢复；页码仍在 |
| 关闭再打开页面、刷新未变更的模组、重启宿主 | 进度保留；每个挂载创建独立控件，多个显示器共享同一进度 |
| 拖窄收起行、切到侧边、在设置中排序/关闭该部件 | 正常降级或由宿主说明空间不足；不会遮住其他部件、抢占活动或停止内置读数 |
| 到达目标后点「完成高亮」，然后切换主题 | 临时色出现；用户主题变化清除它，再点一次可重新申请；停用也会释放 |
| 展开「验收详情」 | 能看到宿主交付的初始面板状态；切页/主题/缩放后重新打开可看到新的修订 |
| 点「验证后台拦截」 | `Host` 来源的展开请求返回 `user-initiated-only`，没有展开副作用 |
| 点「验证页面归属」 | 其他模组页面返回 `page-not-own`，不发生导航 |

不同显示器、DPI、系统深浅色与高对比模式应由拥有相应环境的验收者实测。页面中的验收详情是开发辅助，默认折叠。

## 接口与边界

`ui.compact-parts`、`ui.pages`、`ui.settings`、`storage.data`、`ui.commands` / `commands.invoke`、`host.state`、`host.control.page/expand/accent`。页面按钮经命令服务调用本模组命令；读取面板状态只有一个激活期订阅，没有周期刷新任务。

此示例不读取系统监控或媒体数据，也不调用播放控制。它使用 `BrimDeck.Modules.Abstractions` 与 `BrimDeck.UI` 两个公开 SDK 程序集，未引用桌面主程序或宿主内部程序集。无法读取的原存储会保留并明确提示本次不保存，不将损坏数据覆盖为默认值。

---

Track a book and page goal with a compact progress part, persistent page updates, undo, shortcut commands and a temporary completion accent. No network, account, local service or hardware access is required. This sample is installed explicitly and is not bundled by default. Use a contract-1.10 host built from this branch, including its command capability, accent repaint and panel-state fixes; earlier 1.10 builds and the current 1.3.0 release are not the acceptance target.

Build the package with the command above, copy its `sample-reading-progress` directory into the module folder opened by host settings, refresh and enable it. The optional `BrimDeckSdk` build mode works outside the repository without host project references. Buttons and shortcuts use the same commands; only the open-page command opens the panel. Acceptance details expose the initial panel snapshot and safe refusal probes; background expansion and foreign-page navigation must be rejected. Unreadable saved data is preserved. Theme changes, disabling the module or releasing the override end the temporary accent.
