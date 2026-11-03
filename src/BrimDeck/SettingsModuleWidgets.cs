using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Modules;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private string _miniAppsNavigationKey = "";
    private bool _miniAppsReordering;
    private IReadOnlyList<Contribution<ModuleWidget>> ModuleWidgetEntries() => _app.Modules.Registry.List<ModuleWidget>()
        .OrderBy(w => w.ModuleId, StringComparer.Ordinal).ThenBy(WidgetOrder).ThenBy(w => w.Id, StringComparer.Ordinal).ToArray();
    private bool WidgetEnabled(Contribution<ModuleWidget> widget) => _app.Modules.Invoke(widget.ModuleId, "widget state", widget.Value.Enabled);
    private int WidgetOrder(Contribution<ModuleWidget> widget) => widget.Value.ReadOrder is { } read
        ? _app.Modules.Invoke(widget.ModuleId, "widget order", read) : widget.Value.Order;
    private string MiniAppsNavigationKey => $"{S.Modules.Enabled}/{S.MiniApps.Enabled}/" + string.Join('|', ModuleWidgetEntries().Select(w => w.Id + ":" + WidgetEnabled(w) + ":" + WidgetOrder(w)));
    private IEnumerable<int> VisibleSettingsPages()
    {
        foreach (int page in PageOrder)
        {
            yield return page;
            if (page == ModulesPage) foreach (int extra in ExternalSettingsPages()) yield return extra;
            // Legacy widget deep links resolve to their owning plugin's detail page.
        }
    }
    private void RefreshMiniAppsNavigation()
    {
        if (_scroll is null || _miniAppsNavigationKey == MiniAppsNavigationKey) return;
        if (_miniAppsReordering) { BuildSettingsNavigation(_page); RevealMiniAppsNavigation(_page); }
        else ShowPage(_page);
    }
    private void RevealMiniAppsNavigation(int page)
    {
        var widgetPages = ModuleSettingsEntries().Where(e => e.Placement == ModuleSettingsPlacement.MiniApps).Select(e => e.Contribution.Id).ToHashSet(StringComparer.Ordinal);
        if (page != MiniAppsPage && !widgetPages.Contains(ModuleSettingsId(page))) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (_page != page || _navigation.Parent is not ScrollViewer scroll || !scroll.IsLoaded) return;
            scroll.UpdateLayout();
            var ids = _moduleSettingsPages.Where(p => widgetPages.Contains(p.Key)).Select(p => "settings-page-" + p.Value).ToHashSet();
            ids.Add("settings-page-" + MiniAppsPage);
            var entries = _navigation.Children.OfType<Button>().Where(button => ids.Contains(AutomationProperties.GetAutomationId(button))).ToArray();
            if (entries.Length == 0) return;
            double top = Math.Max(0, entries[0].TransformToAncestor(_navigation).Transform(new Point()).Y - 34);
            double bottom = entries[^1].TransformToAncestor(_navigation).Transform(new Point(0, entries[^1].ActualHeight)).Y + 8;
            if (bottom - top <= scroll.ViewportHeight) scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset, Math.Max(0, bottom - scroll.ViewportHeight), top));
        });
    }
    private Border ExperimentalBadge()
    {
        var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Background = UI.Brush(Inset), Child = TextLine(Loc.T("测试中", "Beta"), 9.5, TextSecondary, FontWeights.Medium) };
        AutomationProperties.SetName(badge, Loc.T("测试中", "Beta")); return badge;
    }
    private Border ModuleWidgetManagementRow(Contribution<ModuleWidget> entry, StackPanel rows)
    {
        string title = Loc.T(entry.Value.Name, entry.Value.NameEn);
        var row = new Grid { Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(PartHandleWidth) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var handle = new Border { Width = 22, Background = Brushes.Transparent, Cursor = entry.Value.MoveTo is null ? Cursors.Arrow : Cursors.SizeAll,
            HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary), Opacity = entry.Value.MoveTo is null ? .3 : 1 };
        row.Children.Add(handle);
        var label = SettingLabel(title); Grid.SetColumn(label, 1); row.Children.Add(label);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (ModuleSettingEntry(entry.Value.SettingsId) is { } settingsEntry && settingsEntry.ModuleId == entry.ModuleId)
        {
            var settings = ActionButton(Loc.T("设置", "Settings"), () => ShowModuleSettings(entry.Value.SettingsId));
            settings.Visibility = WidgetEnabled(entry) ? Visibility.Visible : Visibility.Hidden; settings.Margin = new Thickness(0, 0, 12, 0);
            AutomationProperties.SetName(settings, title + Loc.T("设置", " settings")); actions.Children.Add(settings);
        }
        var toggle = ModuleToggle(title, WidgetEnabled(entry), enabled =>
        {
            _app.Modules.Invoke(entry.ModuleId, "toggle widget", () => { entry.Value.SetEnabled(enabled); return true; });
            RefreshMiniAppsNavigation();
        });
        AutomationProperties.SetAutomationId(toggle, "widget-enable-" + entry.Id); actions.Children.Add(toggle);
        Grid.SetColumn(actions, 2); row.Children.Add(actions);
        if (entry.Value.MoveTo is not null) AttachDrag(handle, title, () => rows, PartRowHeight, list =>
        {
            var order = list.Children.OfType<Border>().Select(item => item.Tag as string).Where(id => id is not null).ToArray();
            _miniAppsReordering = true;
            try
            {
                for (int index = 0; index < order.Length; index++)
                    if (ModuleWidgetEntries().FirstOrDefault(w => w.Id == order[index] && w.ModuleId == entry.ModuleId) is { } widget && widget.Value.MoveTo is { } move)
                    { int target = index; _app.Modules.Invoke(widget.ModuleId, "reorder widget", () => { move(target); return true; }); }
                BuildSettingsNavigation(_page);
                _moduleStructure = ModuleStructure(); // Keep the drag's existing row animation alive during the queued host refresh.
            }
            finally { _miniAppsReordering = false; }
        });
        AutomationProperties.SetHelpText(handle, Loc.T("拖动调整", "Drag to reorder ") + title);
        return new Border { Child = row, Height = PartRowHeight, BorderBrush = UI.Brush(_palette.RowDivider), Tag = entry.Id, RenderTransform = new TranslateTransform() };
    }
    private static void ResetMiniAppsModule(MiniAppsSettings options, MiniAppsSettings defaults)
    { options.Enabled = defaults.Enabled; options.Width = defaults.Width; options.Height = defaults.Height; }
}
