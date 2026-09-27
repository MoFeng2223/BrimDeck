# 发布流程

本文面向维护者，说明如何打包、发布和验证一个版本。相关的设计决定见 [decisions.md](decisions.md#安装与更新)。

## 版本号与更新说明

- 版本号采用 `主.次.修订`（例如 `0.2.0`）。项目文件 `src/BrimDeck/BrimDeck.csproj` 中的 `<Version>` 是本地构建的默认版本；发布时由标签注入，建议同步修改项目文件。
- 每个版本在 `releases/<版本>.md` 写更新说明。GitHub Release 显示整个文件；应用内的更新窗口以纯文本显示当前界面语言的那一段，因此每段只写简短的列表，不使用表格或链接。
- 多种语言写在同一个文件里，每种语言以一行标记开头，标记是 HTML 注释，在 GitHub 页面上不显示：

  ```markdown
  <!-- lang: zh-CN -->
  - 中文说明

  <!-- lang: en-US -->
  - English notes
  ```

  打包时按标记拆分，写入 `update.json` 的 `notes`（按语言分开的对象）。应用依次选择：当前界面语言、同一语言的其他地区写法、英文、文件中的第一种语言，所以新增一种语言不会影响旧版本。没有标记的文件对所有语言显示同一段文字；第一个标记之前出现文字、同一语言出现两次或某种语言内容为空时，打包会报错。
- 发布后修改说明：改好 `releases/<版本>.md` 并提交，然后运行 `./scripts/Update-ReleaseNotes.ps1 -Version <版本>`。它同时更新 GitHub Release 的说明、`update.json` 中的 `notes` 和 `SHA256SUMS.txt` 中对应的一行，安装包不变。需要已登录的 GitHub CLI（`gh`）。已经提示过这一版本的用户不会再次收到提示，下次打开更新窗口时看到新说明。
- 设置格式发生改名、拆分、合并或含义变化时，在 `SettingsMigrations.cs` 中添加以该版本号标注的迁移步骤（见 [decisions.md](decisions.md#设置与语言)）。

## 本地打包

需要 Windows、.NET 10 SDK 和 PowerShell。Inno Setup 编译器（固定为 7.1.0）由 `scripts/Get-InnoSetup.ps1` 通过 NuGet 获取，不需要另行安装。

```powershell
./scripts/Publish-Release.ps1 -Version 0.2.0
```

脚本依次执行：

1. 以 `Release`、`win-x64`、自包含方式发布应用，并注入版本号。
2. 用 `installer/BrimDeck.iss` 编译安装包。
3. 读取 `releases/<版本>.md`（可用 `-ReleaseNotes` 指定其他文件），生成 `update.json`。
4. 生成所有文件的校验清单。

输出目录默认为 `artifacts/releases`（可用 `-OutputDirectory` 指定），每次运行前清空其中的文件：

| 文件 | 内容 |
| --- | --- |
| `BrimDeck-<版本>-Setup.exe` | 安装包，未签名。 |
| `update.json` | `version`、`notes`（一段文字，或按语言分开的对象）、`file`（安装包文件名）、`size`、`sha256`。 |
| `SHA256SUMS.txt` | 其他文件的 SHA-256。 |

日常开发不需要打包：直接构建或运行 `src/BrimDeck` 即可。这类副本读写同一个 `%LOCALAPPDATA%\BrimDeck`，但不提供应用内更新。只有测试安装、更新和卸载本身时才需要打包。

## 发布到 GitHub

1. 添加 `releases/<版本>.md` 并提交。
2. 创建并推送标签 `v<版本>`，例如 `v0.2.0`。只推送代码不会发布版本。

推送标签后，`.github/workflows/release.yml` 在 `windows-latest` 上执行：

1. 检查标签格式必须是 `v主.次.修订`，不接受预发布后缀。
2. 运行核心测试 `tests/BrimDeck.Tests`。
3. 运行 `scripts/Publish-Release.ps1` 生成安装包和 `update.json`。
4. 运行 `scripts/Test-AppUpdate.ps1`，验证安装、应用内更新和卸载。
5. 用仓库自带的 `GITHUB_TOKEN` 先以草稿创建 Release 并上传全部文件，再改为正式版本并标为最新。

应用读取 `releases/latest/download/update.json`，这一地址不包含草稿和预发布版本，因此所有文件上传完成后才会被用户看到。安装包从 `releases/download/v<版本>/<文件名>` 下载。

## 验证

```powershell
dotnet run --project tests/BrimDeck.Tests -c Release                   # 包含 update.json 与下载校验、下载后被改动的安装包不运行等
./scripts/Test-AppUpdate.ps1                                           # 安装、更新、卸载的端到端测试
```

`Test-AppUpdate.ps1` 使用独立的应用标识、名称、数据目录、互斥体和自启动项，不影响本机已安装的 BrimDeck。它把测试程序安装到名称含中文和空格的目录，通过应用内更新安装新版本，检查旧文件已清除、设置保持不变、快捷方式和自启动项保留，最后分别验证保留数据和删除数据两种卸载。

`github` 检查读取公开的最新 Release；尚未发布任何版本时会报告文件不存在。

## 安装程序要点

- 默认"仅为我安装"，不需要管理员权限；也可以选择"为所有用户安装"。应用内更新沿用原来的安装方式和目录。
- `installer/BrimDeck.iss` 中的 `AppId` 必须与 `src/BrimDeck/Updates/GitHubUpdates.cs` 中的 `InstallerAppId` 一致，并且永远不能更改，否则更新会安装出第二份副本。
- 已安装旧版本时，安装程序先以 `/UPGRADE` 静默卸载旧版本（保留设置、数据和自启动项），再安装新版本。
- 卸载时的确认框带"删除用户数据"复选框，默认不勾选；静默卸载加 `/DELETEUSERDATA` 参数才删除数据。
- 应用内更新以 `/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH` 运行安装包，安装完成后重新启动 BrimDeck。
- 安装程序语言按 Windows 显示语言选择：中文（含繁体）为简体中文，其他为英文。
- 每次安装都会在 `%TEMP%` 写入 `Setup Log <日期> #nnn.txt`，用于排查失败的安装和更新。
