using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    internal const int MiniAppsPage = 11;

    private void MiniAppsSettingsPage()
    {
        SectionTitle(Loc.T("小程序模块", "Mini-app module"));
        Group(Toggle(Loc.T("显示小程序页面", "Show mini-app page"), S.MiniApps.Enabled,
                value => { Change(s => s.MiniApps.Enabled = value); RefreshMiniAppsNavigation(); },
                help: Loc.T("控制岛内的小程序页面。模组的启用和停用在模组管理中调整。", "Control the island's mini-app page. Enable or disable modules in module management.")));
        SectionTitle(Loc.T("小组件与顺序", "Widgets and order"), help: () => Loc.T("拖动手柄调整同一模组内的组件顺序。关闭组件会隐藏其设置入口，偏好继续保留。", "Drag to reorder widgets within a module. Disabled widgets keep preferences and hide their settings entry."));
        foreach (var module in ModuleWidgetEntries().GroupBy(w => w.ModuleId))
        {
            SectionTitle(_app.Modules.Modules.FirstOrDefault(m => m.Id == module.Key)?.Name ?? module.Key);
            var rows = new System.Windows.Controls.StackPanel();
            foreach (var widget in module) rows.Children.Add(ModuleWidgetManagementRow(widget, rows));
            UpdateDividers(rows); Group(rows);
        }

        SectionTitle(Loc.T("展开面板", "Expanded panel"));
        Group(Collect(MiniAppsSizeControls));
    }
    private void MiniAppsSizeControls()
    {
        var dimensions = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(0, 8, 0, 0) };
        dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.MiniApps.Width, 440, 1200, value => Resize(s => s.MiniApps.Width = value, DeckPage.MiniApps));
        var height = Dimension(Loc.T("高度", "Height"), S.MiniApps.Height, 140, 400, value => Resize(s => s.MiniApps.Height = value, DeckPage.MiniApps));
        width.Margin = new System.Windows.Thickness(0, 0, 20, 0); height.Margin = new System.Windows.Thickness(20, 0, 0, 0);
        dimensions.Children.Add(width); System.Windows.Controls.Grid.SetColumn(height, 1); dimensions.Children.Add(height); Target.Children.Add(dimensions);
    }
}
