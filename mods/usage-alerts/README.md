# 本体数据与用量提醒示例

独立 DLL 示例，只引用 `BrimDeck.Modules.Abstractions`、`BrimDeck.UI`。提供一个「用量提醒」页面和独立设置：默认 Codex 80% 阈值，读取本体的配额、正在播放和系统监控。数据尚未读取时展示等待，不虚构用量。

构建到 E 盘：

```powershell
dotnet build mods/usage-alerts/BrimDeck.UsageAlerts.csproj -c Release --artifacts-path E:\BrimDeck-build
```

将输出中的 `BrimDeck.UsageAlerts.dll`、`BrimDeck.UsageAlerts.deps.json`、`module.json` 放到实际应用数据目录的 `mods/sample-usage-alerts/`，不要复制宿主和共享 SDK DLL。在模组管理中刷新并启用；文件更新后重新启用确认新的内容摘要。本仓库 E 盘便携预览会包含关闭状态的示例。

清单要求契约 1.1、三种读取权限和通知权限；页面与设置也声明 UI 能力。代码不读取账号文件或密钥，不直接引用本体、Core 或监控页面。

- `UsageAlertsModule.cs`：订阅与视图生命周期、读取和设置、示例页面、动画测试入口。
- `ThresholdGate.cs`：独立阈值规则，可脱离桌面测试；同一已知配额周期只提醒一次。没有重置时间时，低于阈值至少 2 个百分点后重新武装。
- 用量更新跟随本体刷新，不发起额外网络请求。已过期、不可用、无限配额和缺失百分比不触发提醒；首次读取已经超过阈值也可提醒一次。
- 提醒返回 Shown／Queued 后记录去重，RateLimited／Unavailable／NotReady 不消耗本次触发机会；排队接收不代表已经可见。关闭模组会撤销未显示项。
- 设置或去重状态变化才写盘，不随每秒刷新写盘。损坏数据会记录故障并停止自动覆盖该文件，需检查或手动备份修复。
- 「测试动画」明确使用示例内容，不改变真实配额或阈值去重；收起岛后显示。真实提醒包含触发的配额名称与已用百分比。

自动检查在 `tests/BrimDeck.ModuleTests`，覆盖跨阈值、周期重置、重启去重、过期数据、限流重试和边界抖动。更多调用规则见[API 参考](../../docs/zh-CN/modules/api.md)。
