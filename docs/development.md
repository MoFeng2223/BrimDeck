# 开发

需要 Windows 11 和 .NET 10 SDK。

```powershell
dotnet build src/BrimDeck/BrimDeck.csproj -c Release
# 输出：src/BrimDeck/bin/Release/net10.0-windows10.0.19041.0/BrimDeck.exe

dotnet run --project tests/BrimDeck.Tests -c Release       # 核心测试
dotnet run --project tests/BrimDeck.MediaTests -c Release  # 媒体来源适配与网易云桥接脚本测试
./scripts/Test-AppUpdate.ps1                               # 安装、应用内更新与卸载的端到端测试
```

命令行参数 `--settings` 在启动时打开设置窗口。

## 文档

- [架构](architecture.md)
- [设计决定](decisions.md)
- [自定义配额来源](custom-sources.md)
- [数据与隐私](privacy.md)
- [发布流程](releasing.md)

## README 图片

README 中的截图和动画由 [`scripts/readme-media`](../scripts/readme-media) 生成：用 BrimDeck 自己的界面代码和虚构数据离屏渲染，不读取本机数据，也不截取屏幕。

```powershell
dotnet build scripts/readme-media/ReadmeMedia.csproj -c Release
scripts/readme-media/bin/Release/net10.0-windows10.0.19041.0/ReadmeMedia.exe output/readme-media/zh-CN zh-CN
python scripts/readme-media/compose.py output/readme-media/zh-CN docs/images/zh-CN
```

第二个参数为界面语言（`zh-CN` 或 `en-US`）。合成脚本需要 Python 3 与 Pillow。
