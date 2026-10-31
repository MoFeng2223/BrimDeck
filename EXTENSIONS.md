# BrimDeck DLL 插件

插件以托管 .NET DLL 形式运行在 BrimDeck 进程中。插件加载后拥有当前用户进程的权限，可以访问本机文件、BrimDeck 数据（包括密钥），也可能使宿主崩溃或卡顿。BrimDeck 的 API 和权限声明是产品边界，不是安全沙箱；只启用可信来源的插件。

## 安装格式

每个插件是一个独立目录，放在当前数据目录的 `extensions` 下：

```text
sample-clock/
  plugin.json
  BrimDeck.Island.Clock.dll
  BrimDeck.Island.Clock.deps.json
  OtherDependency.dll
```

`plugin.json` 示例：

```json
{
  "id": "sample-clock",
  "entry": "BrimDeck.Island.Clock.dll",
  "version": "1.0.0",
  "apiVersion": 1,
  "repository": "https://github.com/example/sample-clock"
}
```

插件文件夹必须包含清单。插件 API 版本高于宿主时会被拒绝并显示原因。每个插件的依赖由独立、可回收的 `AssemblyLoadContext` 解析；`BrimDeck.Island.dll` 由宿主共享，插件包不要带自己的副本。旧原型直接放在 `extensions` 根目录的 DLL 仍会扫描。

## 契约

插件项目引用 `src/BrimDeck.Island/BrimDeck.Island.csproj` 并实现 `IIslandExtension`。默认 `Pages` 提供一个页面；插件可返回多个 `IslandPage`。目前宿主保留四个总页面槽位，后续要解除上限需要把设置模型从固定枚举迁移到页面 ID。

```csharp
public sealed class ClockPlugin : IIslandExtension
{
    public string Id => "sample-clock";
    public string TitleZh => "时钟";
    public string TitleEn => "Clock";
    public string Glyph => "M 12,3 A 9,9 0 1 1 11.99,3 M 12,7 V 12 L 15,14";

    public FrameworkElement CreateView(IslandColors colors) => new TextBlock
    {
        Text = DateTime.Now.ToString("HH:mm"),
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.Primary))
    };

    public void SetDemand(bool shown) { /* shown 时开始轮询，隐藏时停止 */ }
}
```

扩展还可以提供设置页、胶囊视图、托盘命令、主题和宿主事件回调；`IslandPluginContext` 提供插件独立数据目录、键值存储、宿主控制请求和数据订阅。实时系统监控快照按订阅推送。插件需要在 `SetDemand(false)` 和 `Dispose()` 中停止定时器、释放事件订阅与资源。

## 管理与资源控制

- 「设置 → 插件」可全局启停插件执行、安装插件文件夹、重新扫描、打开目录、查看加载失败并卸载插件。首次启用会显示进程内 DLL 权限说明；卸载时可保留或删除插件数据。
- 插件默认不执行。关闭插件执行会卸载 DLL 上下文；插件设置和数据仍保留。
- 重载会从缓存副本加载更新后的程序集。插件应避免静态事件、计时器或宿主引用阻止旧加载上下文回收。
- `SetDemand` 只在至少一个插件页面可见时为 `true`。关闭页面或面板时停止高频工作，降低内存和 CPU 占用。
- 自定义设置视图由插件绘制；主题颜色通过 `IslandColors` 和 `OnThemeChanged` 提供。
- 命令可从托盘菜单和插件管理页运行。插件间没有数据共享 API。

当前主程序尚未接入 brim-notify 的通知服务，也未把插件命令接入全局快捷键或把 `CreateCompactView` 插入收起态胶囊布局；这些能力需要与对应宿主子系统联调。DLL 插件也不能在同进程内获得可靠的故障隔离或权限沙箱。

## 示例

`samples/BrimDeck.Island.Clock/` 提供一个 WPF DLL 插件和 `plugin.json`。构建：

```powershell
dotnet build samples/BrimDeck.Island.Clock -c Release
```

将构建输出文件夹复制到插件目录，进入「设置 → 插件」，启用 DLL 插件执行并点击「重新加载」。页面会显示在扩展页签中。
