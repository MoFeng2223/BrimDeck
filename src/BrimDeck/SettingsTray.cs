using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

// Settings › File tray. The page opens with one question — where the tray appears — and the rest of it only matters for the
// place that is on, so the sections that do not apply are shown disabled rather than hidden: what the tray can be set to do
// should be readable before the tray is switched to that place.
public sealed partial class SettingsWindow
{
    private void TraySettingsPage()
    {
        bool notch = S.Tray.Placement is FileTrayPlacement.Notch or FileTrayPlacement.Both;
        bool floating = S.Tray.Placement is FileTrayPlacement.Floating or FileTrayPlacement.Both;
        bool both = S.Tray.Placement == FileTrayPlacement.Both;

        SectionTitle(Loc.T("出现位置", "Where it appears"), help: () => Loc.T("文件中转仍是测试中的功能：默认关闭，打开后在面板的页面切换里多一页，文件只在托盘里等待，不会被复制。",
            "The file tray is still in testing: off by default, it adds one page to the panel's page switch when on, and files only wait in it without being copied."));
        Group(MusicChoice(Loc.T("文件中转出现在", "The file tray appears"), S.Tray.Placement,
                [(FileTrayPlacement.Notch, Loc.T("岛内", "In the notch")), (FileTrayPlacement.Floating, Loc.T("浮动", "Floating")), (FileTrayPlacement.Both, Loc.T("两处", "Both"))],
                v => { Change(s => s.Tray.Placement = v); ShowPage(TrayPage); }),
            Toggle(Loc.T("启用文件中转", "File tray on"), S.Tray.Enabled, v => Change(s => s.Tray.Enabled = v), help:
                Loc.T("关闭后收起面板的页面切换里不再有文件托盘，浮动托盘也不再出现；面板仍会为用量、音乐与系统监控展开。",
                    "When off, the file tray leaves the panel's page switch and the floating tray stops appearing; the panel still opens for usage, music and the system monitor.")));

        SectionTitle(Loc.T("岛内", "In the notch"));
        Group(Toggle(Loc.T("放入后打开托盘页", "Open the tray after a drop"), S.Tray.OpenAfterDrop,
                v => Change(s => s.Tray.OpenAfterDrop = v), enabled: notch, keepPreview: true, help:
                Loc.T("放入文件后切换到文件托盘页，刚加进去的文件立刻可见。关闭时只保留在托盘里，切过去才看得到。",
                    "Switches to the tray page after a drop, so what was just added is on screen. Off, items wait in the tray until the page is opened.")),
            MusicChoice(Loc.T("自动清理", "File retention"), S.Tray.RetentionMinutes,
                [(0, Loc.T("不清理", "Keep")), (5, Loc.T("5 分钟", "5 min")), (15, Loc.T("15 分钟", "15 min")), (30, Loc.T("30 分钟", "30 min")), (60, Loc.T("1 小时", "1 h"))],
                v => Change(s => s.Tray.RetentionMinutes = v), enabled: notch, keepPreview: true));

        SectionTitle(Loc.T("浮动托盘", "Floating file tray"));
        Group(Toggle(Loc.T("立即出现", "Instant appear"), S.Tray.FloatingInstant,
                v => { Change(s => s.Tray.FloatingInstant = v); ShowPage(TrayPage); }, enabled: floating, help:
                Loc.T("关闭后，浮动托盘在延迟结束后才出现，短暂掠过时不会闪出来。",
                    "Off, the floating tray appears only after the delay, so a passing cursor does not make it flash.")),
            MusicChoice(Loc.T("出现延迟", "Appear delay"), S.Tray.FloatingDelayMs,
                [(0, Loc.T("无", "None")), (150, Loc.T("0.15 秒", "0.15 s")), (250, Loc.T("0.25 秒", "0.25 s")), (500, Loc.T("0.5 秒", "0.5 s")), (1000, Loc.T("1 秒", "1 s"))],
                v => Change(s => s.Tray.FloatingDelayMs = v), enabled: floating && !S.Tray.FloatingInstant),
            MusicChoice(Loc.T("自动隐藏", "Auto-hide"), S.Tray.AutoHideDelayMs,
                [(1200, Loc.T("1.2 秒", "1.2 s")), (2400, Loc.T("2.4 秒", "2.4 s")), (4000, Loc.T("4 秒", "4 s")), (8000, Loc.T("8 秒", "8 s"))],
                v => Change(s => s.Tray.AutoHideDelayMs = v), enabled: floating),
            Toggle(Loc.T("多个浮动托盘", "Multiple floating trays"), S.Tray.MultipleFloatingTrays,
                v => Change(s => s.Tray.MultipleFloatingTrays = v), enabled: floating, help:
                Loc.T("允许同时保留多个浮动托盘，每个在光标旁各占一处；关闭时只保留最近的一个。",
                    "Keeps more than one floating tray at a time, each staying where it was called; off, only the latest stays.")));

        SectionTitle(Loc.T("两处", "Both trays"));
        Group(Toggle(Loc.T("两处同步", "Sync between trays"), S.Tray.SyncTrays,
            v => Change(s => s.Tray.SyncTrays = v), enabled: both, help:
            Loc.T("岛内的托盘与浮动托盘共用同一份列表：一处放入或移除，另一处立即跟上。",
                "The notch's tray and the floating tray share one list: an item added or removed in one appears in the other.")));

        SectionTitle(Loc.T("移出文件", "Moving files out"));
        Group(Toggle(Loc.T("拖出后移除", "Auto-remove"), S.Tray.AutoRemoveAfterDragOut,
                v => Change(s => s.Tray.AutoRemoveAfterDragOut = v), help:
                Loc.T("把文件拖到别处后，该条目从托盘移除，托盘随用随空。关闭时条目留在原地，需要自己清理。",
                    "Takes the item off the tray once it is dragged out, so the tray empties as it is used. Off, items stay until cleared.")),
            Toggle(Loc.T("保护原文件", "Protect originals"), S.Tray.ProtectOriginals,
                v => Change(s => s.Tray.ProtectOriginals = v), help:
                Loc.T("开启时拖出总是复制，原文件留在原处。关闭时拖到同一个磁盘是移动，原文件会离开原来的文件夹；拖到另一个磁盘始终是复制。",
                    "Always copies instead of moving, so the original stays where it was. Off, dragging to the same disk moves the file out of its folder; dragging to another disk always copies.")));

    }

    // The tray page's own size, in the expanded-view page's list: it is a page of the panel like the other three.
    private void TraySizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.TrayWidth, 440, 1200, value => Resize(s => s.TrayWidth = value, DeckPage.FileTray));
        width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.TrayHeight, 140, 400, value => Resize(s => s.TrayHeight = value, DeckPage.FileTray));
        height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
    }

}
