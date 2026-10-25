# 岛屿拓展页（扩展插件）

> [!WARNING]
> 插件是运行在 BrimDeck 进程内的普通 .NET 代码，**不是安全沙箱**：它能读取本机文件与设置中的密钥，能力与一个用户自己运行的程序相同。请只安装来自可信来源的插件；不确定来源的 DLL 不要放进插件文件夹。

BrimDeck 支持在不修改主程序源码的情况下添加自定义页面。一个扩展是一个 DLL，放在：

```
%LOCALAPPDATA%\BrimDeck\extensions\
```

主程序启动时按文件名顺序扫描该目录，把实现了 `IIslandExtension`、带无参构造的类按顺序分配到四个页面槽位（`Extension0..3`）。一个 DLL 可以导出多个扩展。加载失败的 DLL 会被跳过并记录原因，不影响主程序与其他扩展；不含扩展实现的 DLL 会列在「已跳过」中，便于发现放错位置的文件。

插件从缓存副本加载，插件文件夹保持可写：直接覆盖旧 DLL，再在设置里点「重新加载」即可完成替换；被删除的 DLL 对应插件会被停用。旧版本在替换后尽力释放。

## 契约

引用 `BrimDeck.Island` 程序集（由主程序提供，插件不要自带副本），实现：

```csharp
public interface IIslandExtension
{
    string Id { get; }        // 稳定 id，设置按它保存；字母、数字、- 和 .
    string TitleZh { get; }   // 页签标题（中文）
    string TitleEn { get; }   // 页签标题（英文）
    string Glyph { get; }     // 页签图标：24×24 描边路径（Path Data），圆角端点；无法解析时宿主绘制一个方块
    FrameworkElement CreateView(IslandColors colors);  // 页面内容，每个面板调用一次
    void SetDemand(bool shown);                        // 是否有面板正在显示该页
}
```

`IslandColors` 提供 `Normal / Warning / Critical / Primary / Secondary / Tertiary / Track / Fill` 八个 `#RRGGBB` 颜色字符串，来自面板当前的监控配色与主题。主题变化时宿主按新颜色重建视图；扩展若编辑中的输入不应丢失，请在自己的页面里保存草稿。

## 规则

- **视图完全归扩展所有**：宿主只把它放进页面并让它随页面拉伸，不碰它的内容。实时刷新由扩展自己驱动（后台线程读数据、`Dispatcher` 回 UI 更新）。
- **按需工作**：`SetDemand(true)` 在任意面板开始显示该页时到来一次，`SetDemand(false)` 在最后一个面板离开后到来（切页、隐藏、关闭面板、停用插件都会释放）。读取服务、连接、轮询都应在这两个边界之间启停；没有人看的页面应当零开销。第一次 `true` 可能早于任何 `CreateView`。
- **不弹不抢**：扩展不应对外弹窗、抢焦点或注册全局热键；需要交互放自己的页面里。
- **依赖**：扩展只引用 `BrimDeck.Island`（由宿主提供，与宿主共用同一份类型）；其他依赖 DLL 可以放在插件目录，宿主会从该目录解析。不要自带一份 `BrimDeck.Island.dll`。
- **失败隔离**：构造函数、`Id`、`SetDemand`、`CreateView`、`Glyph` 都有防护，单个插件抛错只影响它自己。

## 用户侧：设置 → 插件

所有插件统一在「设置 → 插件」页管理：

- **已加载列表**：每个插件一行——图标、标题、id 与来源 DLL；行内「启用」开关控制其页签是否出现在面板上（停用后页签消失，后台读取停止，面板展开或收起都一样）；展开行可调宽高（也可直接在面板上拖拽边缘调整）。
- **打开插件文件夹**：打开（必要时创建）`extensions` 目录。
- **重新加载**：重新扫描目录——新放入的 DLL 被装入、被修改的 DLL 被替换、被删除的 DLL 对应插件被停用，全程无需重启。
- **未能加载**：加载失败或槽位已满的 DLL，按文件名列出原因。
- **已跳过**：能读取但不含扩展实现的 DLL（依赖库、放错位置的文件等）。
- 该页的「恢复默认」把所有插件的开关与尺寸还原为默认。

删除扩展：删除 `extensions` 目录下对应 DLL，重启后页签消失；它的设置会保留，放回 DLL 后恢复。

## 示例

`samples/BrimDeck.Island.Clock/` 是一个最小示例：一个显示时间与日期的页面，只依赖契约程序集与 WPF，可作为新扩展的骨架——实现接口、绘制页面、在 `SetDemand` 里启停自己的计时器。

构建并安装它：

```
dotnet build samples/BrimDeck.Island.Clock -c Release
```

把 `samples/BrimDeck.Island.Clock/bin/Release/net10.0-windows10.0.19041.0/BrimDeck.Island.Clock.dll` 拷进 `%LOCALAPPDATA%\BrimDeck\extensions\`，然后在「设置 → 插件」里点「重新加载」，新页签立即出现。契约程序集 `BrimDeck.Island.dll` 已随主程序安装，插件不必自带。
