# 岛屿拓展页（扩展插件）

BrimDeck 支持在不修改主程序源码的情况下添加自定义页面。一个扩展是一个 DLL，放在：

```
%LOCALAPPDATA%\BrimDeck\extensions\
```

主程序启动时按文件名顺序加载该目录下的每个 DLL，发现其中实现了 `IIslandExtension` 接口、带无参构造的类，就按顺序分配到四个页面槽位（`Extension0..3`）。加载失败的 DLL 会被跳过，不影响主程序与其他扩展。

## 契约

引用 `BrimDeck.Island` 程序集（`src/BrimDeck.Island/`），实现：

```csharp
public interface IIslandExtension
{
    string Id { get; }        // 稳定 id，设置按它保存；字母、数字、- 和 .
    string TitleZh { get; }   // 页签标题（中文）
    string TitleEn { get; }   // 页签标题（英文）
    string Glyph { get; }     // 页签图标：24×24 描边路径（Path Data），圆角端点
    FrameworkElement CreateView(IslandColors colors);  // 页面内容，每个面板调用一次
    void SetDemand(bool shown);                        // 是否有面板正在显示该页
}
```

`IslandColors` 提供 `Normal / Warning / Critical / Primary / Secondary / Tertiary / Track / Fill` 八个 `#RRGGBB` 颜色字符串，来自面板当前的监控配色与主题，主题变化时视图会按新配色重建。

## 规则

- **视图完全归扩展所有**：宿主只把它放进页面并让它随页面拉伸，不碰它的内容。实时刷新由扩展自己驱动（后台线程读数据、`Dispatcher` 回 UI 更新）。
- **按需工作**：`SetDemand(true)` 在任何面板显示该页时到来一次，`SetDemand(false)` 在最后一个面板离开后到来。读取服务、连接、轮询都应在这两个边界之间启停；没有人看的页面应当零开销。第一次 `true` 可能早于任何 `CreateView`。
- **不弹不抢**：扩展不应对外弹窗、抢焦点或注册全局热键；需要交互放自己的页面里。
- **依赖**：扩展只应引用 `BrimDeck.Island`，它由宿主提供、与宿主共用同一份类型；扩展自己的其他依赖 DLL 可以放在同一目录，宿主会从插件文件夹解析。不要自带一份 `BrimDeck.Island.dll`。
- **文件可更新**：宿主从缓存副本加载 DLL，插件文件夹保持可写，直接覆盖旧 DLL 再点「重新加载」即可完成替换。

## 用户侧：设置 → 插件

所有插件统一在「设置 → 插件」页管理：

- **已加载列表**：每个插件一行——图标、标题、id 与来源 DLL；行内「显示页签」开关控制其页签是否出现在灵动岛上，宽高为该插件页面的尺寸（也可直接在面板上拖拽边缘调整）。
- **打开插件文件夹**：打开（必要时创建）`extensions` 目录。
- **启用/停用**：每个插件行的头部开关。停用后页签从灵动岛上消失、其后台读取停止（无论面板展开或收起）；启用即恢复。设置会按插件保留。
- **重新加载（热加载）**：重新扫描目录——新放入的 DLL 被装入、被修改的 DLL 被替换（旧实例停用并释放，替换无需重启）、被删除的 DLL 对应插件被停用。插件在独立的可收集程序集上下文中加载，替换与移除尽力释放旧版本。
- **未能加载**：加载失败或槽位已满的 DLL 会在这里按文件名列出原因。
- 该页的「恢复默认」把所有插件的开关与尺寸还原为默认。

删除扩展：删除 `extensions` 目录下对应 DLL（连同它引用的 `BrimDeck.Island.dll` 共享副本可保留），重启后页签消失；它的设置会保留，放回 DLL 后恢复。

## 示例

`samples/BrimDeck.Island.Clock/` 是一个最小示例：一个显示时间与日期的页面，只依赖契约程序集与 WPF，可作为新扩展的骨架——实现接口、绘制页面、在 `SetDemand` 里启停自己的计时器。

构建并安装它：

```
dotnet build samples/BrimDeck.Island.Clock -c Release
```

把 `samples/BrimDeck.Island.Clock/bin/Release/net10.0-windows10.0.19041.0/BrimDeck.Island.Clock.dll` 拷进 `%LOCALAPPDATA%\BrimDeck\extensions\`，然后在「设置 → 插件」里点「重新加载」，新页签立即出现。契约程序集 `BrimDeck.Island.dll` 已随主程序安装，插件不必自带。

主程序启动时按文件名顺序加载该目录下的每个 DLL，发现其中实现了 `IIslandExtension` 接口、带无参构造的类，就按顺序分配到四个页面槽位（`Extension0..3`）。加载失败的 DLL 会被跳过，不影响主程序与其他扩展。

## 契约

引用 `BrimDeck.Island` 程序集（`src/BrimDeck.Island/`），实现：

```csharp
public interface IIslandExtension
{
    string Id { get; }        // 稳定 id，设置按它保存；字母、数字、- 和 .
    string TitleZh { get; }   // 页签标题（中文）
    string TitleEn { get; }   // 页签标题（英文）
    string Glyph { get; }     // 页签图标：24×24 描边路径（Path Data），圆角端点
    FrameworkElement CreateView(IslandColors colors);  // 页面内容，每个面板调用一次
    void SetDemand(bool shown);                        // 是否有面板正在显示该页
}
```

`IslandColors` 提供 `Normal / Warning / Critical / Primary / Secondary / Tertiary / Track / Fill` 八个 `#RRGGBB` 颜色字符串，来自面板当前的监控配色与主题，主题变化时视图会按新配色重建。

## 规则

- **视图完全归扩展所有**：宿主只把它放进页面并让它随页面拉伸，不碰它的内容。实时刷新由扩展自己驱动（后台线程读数据、`Dispatcher` 回 UI 更新）。
- **按需工作**：`SetDemand(true)` 在任何面板显示该页时到来一次，`SetDemand(false)` 在最后一个面板离开后到来。读取服务、连接、轮询都应在这两个边界之间启停；没有人看的页面应当零开销。第一次 `true` 可能早于任何 `CreateView`。
- **不弹不抢**：扩展不应对外弹窗、抢焦点或注册全局热键；需要交互放自己的页面里。
- **自包含**：扩展不应引用主程序的其他程序集（BrimDeck.exe、BrimDeck.Core），只依赖 `BrimDeck.Island`。绘图、本地化、数据读取都自带。

## 用户侧：设置 → 插件

所有插件统一在「设置 → 插件」页管理：

- **已加载列表**：每个插件一行——图标、标题、id 与来源 DLL；行内「显示页签」开关控制其页签是否出现在灵动岛上，宽高为该插件页面的尺寸（也可直接在面板上拖拽边缘调整）。
- **打开插件文件夹**：打开（必要时创建）`extensions` 目录。
- **启用/停用**：每个插件行的头部开关。停用后页签从灵动岛上消失、其后台读取停止（无论面板展开或收起）；启用即恢复。设置会按插件保留。
- **重新加载（热加载）**：重新扫描目录——新放入的 DLL 被装入、被修改的 DLL 被替换（旧实例停用并释放，替换无需重启）、被删除的 DLL 对应插件被停用。插件在独立的可收集程序集上下文中加载，替换与移除尽力释放旧版本。
- **未能加载**：加载失败或槽位已满的 DLL 会在这里按文件名列出原因。
- 该页的「恢复默认」把所有插件的开关与尺寸还原为默认。

删除扩展：删除 `extensions` 目录下对应 DLL（连同它引用的 `BrimDeck.Island.dll` 共享副本可保留），重启后页签消失；它的设置会保留，放回 DLL 后恢复。

