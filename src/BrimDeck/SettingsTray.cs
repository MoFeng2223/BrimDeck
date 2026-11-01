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
        ModuleModeControl(false);
        if (_app.Shelves is not null)
        {
            _shelfContent ??= CreateShelfContent();
            _shelfContent.ApplyResources(ShelfSettingsPalette());
            _shelfContent.SetFloatingEnabled(S.Tray.Placement != FileTrayPlacement.Notch && S.Tray.Mode == ModuleMode.Full);
        }

        Group(MusicChoice(Loc.T("文件中转出现在", "The file tray appears"), S.Tray.Placement,
                [(FileTrayPlacement.Notch, Loc.T("岛内", "In the notch")), (FileTrayPlacement.Floating, Loc.T("浮动", "Floating")), (FileTrayPlacement.Both, Loc.T("两处", "Both"))],
                v => { Change(s => s.Tray.Placement = v); ShowPage(TrayPage); }));
        var island = MusicChoice(Loc.T("岛内公共页眉", "Island shared header"), S.Tray.HeaderMode,
            [(ClipboardHeaderMode.Edge, Loc.T("顶部边缘唤出", "Reveal at top edge")), (ClipboardHeaderMode.Always, Loc.T("常驻显示", "Always visible"))],
            value => Change(s => s.Tray.HeaderMode = value), enabled: S.Tray.Placement != FileTrayPlacement.Floating,
            help: Loc.T("与剪贴板一致：展开后再次移到面板上方屏幕边缘，平滑展开公共页面切换；或始终保留页眉。不改变浮动置物架。", "Like the clipboard: revisit the top screen edge above the expanded panel to reveal shared navigation, or keep it visible. Floating shelves are unchanged."));
        System.Windows.Automation.AutomationProperties.SetAutomationId(island, "shelf-shared-header-mode");
        Group(island, Toggle(Loc.T("岛内网格视图", "Island grid view"), S.Tray.GridView, value => Change(s => s.Tray.GridView = value), enabled: S.Tray.Placement != FileTrayPlacement.Floating));
        _shelfSlot!.Content = _shelfContent;
        _shelfSlot.Visibility = _shelfContent is null ? Visibility.Collapsed : Visibility.Visible;
        _navigation.IsEnabled = _content.IsEnabled = _shelfContent?.IsModal != true;

    }

    // The tray page's own size, in the expanded-view page's list: it is a page of the panel like the other three.
    private void TraySizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.TrayWidth, ClipboardDockLayout.MinWidth, DeckPages.MaxWidth, value => Resize(s => { s.TrayWidth = value; s.TrayReferenceSize = false; }, DeckPage.FileTray));
        width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.TrayHeight, ClipboardDockLayout.Height, DeckPages.MaxHeight, value => Resize(s => { s.TrayHeight = value; s.TrayReferenceSize = false; }, DeckPage.FileTray));
        height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        var restore = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        restore.Children.Add(ActionButton(Loc.T("恢复参考尺寸 · 1080×210", "Reference size · 1080×210"), () => { Change(ShelfDockLayout.Reset); ShowPage(_page); }));
        if (S.TrayPreviousLayout is not null) restore.Children.Add(ActionButton(Loc.T("恢复迁移前布局", "Restore previous layout"), () => { Change(ShelfDockLayout.RestorePrevious); ShowPage(_page); }));
        Target.Children.Add(restore);
    }

}
