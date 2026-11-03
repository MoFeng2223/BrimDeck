using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Modules;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private static string ModuleByteSize(long? size)
    {
        if (size is null) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"]; double value = size.Value; int index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return value.ToString(index == 0 ? "N0" : "0.#") + " " + units[index];
    }
    private FrameworkElement ModuleStorageDetails(ModuleInfo module, DiscoveredModule? external)
    {
        var container = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var row = new Grid { MinHeight = 58 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(28) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(Stroke("M2,6 L4,2 H12 L14,6 V12 H2 Z M2,6 H14 M5,9 H5.1 M8,9 H8.1", 17, 17, TextTertiary));
        var amounts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var data = TextLine(Loc.T("数据  —", "Data  —"), 11.5, TextSecondary);
        var cache = TextLine(Loc.T("缓存  —", "Cache  —"), 11.5, TextSecondary); cache.Margin = new Thickness(0, 4, 0, 0);
        data.TextTrimming = cache.TextTrimming = TextTrimming.CharacterEllipsis;
        amounts.Children.Add(data); amounts.Children.Add(cache); Grid.SetColumn(amounts, 1); row.Children.Add(amounts);
        Button? clear = null; bool measuring = false;
        async Task ReadUsage()
        {
            if (measuring || !row.IsLoaded) return; measuring = true;
            try
            {
                var usage = await Task.Run(() => _app.ModuleFiles.Measure(module.Id));
                if (!row.IsLoaded) return;
                data.Text = Loc.T("数据  ", "Data  ") + ModuleByteSize(usage.DataBytes);
                cache.Text = Loc.T("缓存  ", "Cache  ") + ModuleByteSize(usage.CacheBytes) + (usage.CachePending ? Loc.T(" · 待清理", " · pending") : "");
                row.ToolTip = usage.Error;
                clear!.IsEnabled = !_app.ModuleFilesBusy && usage.CacheBytes is > 0 && !usage.RemovalPending;
                clear.ToolTip = usage.CachePending ? Loc.T("使用中的缓存将在下次启动清理。", "Caches currently in use will be cleared at the next startup.") : Loc.T("清理缓存，保留模组数据。", "Clear caches and keep module data.");
            }
            catch (Exception error) { row.ToolTip = error.Message; clear!.IsEnabled = false; }
            finally { measuring = false; }
        }
        clear = ActionButton(Loc.T("清空缓存", "Clear cache"), async () =>
        {
            clear!.IsEnabled = false;
            try
            {
                var result = await _app.ClearModuleCacheAsync(module.Id);
                _moduleActionMessage = result.Pending ? Loc.T("未使用的缓存已清理，剩余文件将在解除占用后继续清理。", "Unused caches cleared. Remaining files will be retried after they are released.") : Loc.T("缓存已清空", "Cache cleared");
                if (result.Error is not null) SaveStatus(Loc.T("仍有文件待清理，解除占用后刷新或重新启动。", "Some files need cleanup. Release them, then refresh or restart."), true);
                if (IsLoaded && _page == ModulesPage) ShowPage(ModulesPage);
            }
            catch (Exception error) { SaveStatus(error.Message, true); await ReadUsage(); }
        });
        AutomationProperties.SetAutomationId(clear, "module-clear-cache-" + module.Id);
        clear.IsEnabled = false; clear.VerticalAlignment = VerticalAlignment.Center; clear.Padding = new Thickness(12, 5, 12, 5); Grid.SetColumn(clear, 2); row.Children.Add(clear);
        container.Children.Add(new Border { Child = row, Background = UI.Brush(Inset), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 0, 10, 0) });
        row.Loaded += async (_, _) => await ReadUsage();
        row.IsVisibleChanged += async (_, _) => { if (row.IsVisible) await ReadUsage(); };
        if (external is not null)
        {
            var uninstall = ActionButton(Loc.T("卸载模组", "Uninstall module"), async () =>
            {
                if (_app.ModuleFilesBusy) return;
                if (!Confirm(Loc.T("卸载插件", "Uninstall plugin"), Loc.T($"卸载“{module.Name}”？默认保留业务数据，重新安装后可以继续使用。\n依赖此插件的其他插件会停止。", $"Uninstall {module.Name}? Business data is kept by default for reinstallation.\nDependent plugins will stop."), Loc.T("卸载", "Uninstall"))) return;
                var choice = MessageBox.Show(this, Loc.T("是否同时删除插件业务数据？\n选择“否”保留数据（推荐）；选择“是”将无法撤销。", "Also delete this plugin's business data?\nChoose No to keep it (recommended). Choosing Yes cannot be undone."),
                    Loc.T("同时删除插件数据", "Delete plugin data too"), MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
                if (choice == MessageBoxResult.Cancel) return;
                bool deleteData = choice == MessageBoxResult.Yes;
                try
                {
                    var result = await _app.UninstallModuleAsync(module.Id, deleteData); _expandedModules.Remove(module.Id);
                    _moduleActionMessage = result.ReplacementPreserved
                        ? Loc.T("为保护数据，部分目录已保留。请刷新查看，必要时重新确认卸载。", "Some directories were preserved to protect data. Refresh and confirm uninstall again if needed.")
                        : result.Pending ? Loc.T("模组已停止，仍有文件待清理；解除占用后刷新或重新启动。", "Module stopped. Release remaining files, then refresh or restart to finish cleanup.") : deleteData ? Loc.T("插件已卸载，已确认的业务数据与缓存已删除。", "Plugin uninstalled. Confirmed business data and caches deleted.") : Loc.T("插件已卸载，业务数据已保留。", "Plugin uninstalled. Business data kept.");
                    if (IsLoaded) ShowPage(ModulesPage);
                }
                catch (Exception error) { SaveStatus(error.Message, true); }
            });
            uninstall.IsEnabled = !_app.ModuleFilesBusy;
            uninstall.Foreground = UI.Brush(_palette.Dark ? "#F0A6AA" : "#B12E3A");
            uninstall.HorizontalAlignment = HorizontalAlignment.Right; uninstall.Margin = new Thickness(0, 10, 0, 0);
            AutomationProperties.SetAutomationId(uninstall, "module-uninstall-" + module.Id); container.Children.Add(uninstall);
        }
        return container;
    }
}
