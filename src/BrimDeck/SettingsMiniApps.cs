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
        var width = Dimension(Loc.T("宽度", "Width"), S.MiniApps.Width, 440, 1200, value => ResizeMiniAppsSize(s => s.MiniApps.Width = value));
        var height = Dimension(Loc.T("高度", "Height"), S.MiniApps.Height, 140, 400, value => ResizeMiniAppsSize(s => s.MiniApps.Height = value));
        width.Margin = new System.Windows.Thickness(0, 0, 20, 0); height.Margin = new System.Windows.Thickness(20, 0, 0, 0);
        dimensions.Children.Add(width); System.Windows.Controls.Grid.SetColumn(height, 1); dimensions.Children.Add(height); Target.Children.Add(dimensions);
    }

    // The global size is the starting point for module pages, not a setting they follow: a page whose module declared a
    // Size keeps it, and the rest take the new size through their own preference (their clamps still apply). The size
    // write goes through the same Change callback that applies the edit, on the settings object that callback installs —
    // writing through a directory built before Change would land on a discarded copy. The preview lands on the first
    // page that moves; without such pages the legacy mini-app page is sized as before.
    private void ResizeMiniAppsSize(Action<DeckSettings> edit)
    {
        var movable = _app.PageDirectory(S).All.Where(p => p.BuiltIn is null
            && _app.Modules.Registry.List<ModulePage>().Any(e => e.Id == p.Id && e.Value.Size is null)).ToArray();
        if (movable.Length == 0) { Resize(edit, DeckPage.MiniApps); return; }
        Change(s =>
        {
            edit(s);
            var directory = _app.PageDirectory(s);
            foreach (var page in movable) directory.SetSize(page.Id, s.MiniApps.Width, s.MiniApps.Height);
        });
        if (!S.Modules.Enabled || _preview) return;
        _preview = true; _previewPage = DeckPage.MiniApps; _app.Deck.Preview(true);
        _app.Deck.SelectPage(movable[0].Id, remember: false);
    }
}
