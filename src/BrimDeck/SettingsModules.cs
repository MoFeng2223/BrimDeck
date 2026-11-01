using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using BrimDeck.Modules;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    internal const int ModulesPage = 15;
    private readonly Dictionary<string, int> _moduleSettingsPages = [];
    private int _nextModuleSettingsPage = _pages.Length;
    private int _moduleTab;
    private string? _moduleActionMessage;
    private string _moduleSearch = "";
    private readonly HashSet<string> _expandedModules = [];
    private string? _moduleStructure;
    private string ModuleStructure() => S.Modules.Enabled + "/" + string.Join("|", _app.Modules.Modules.Select(m => $"{m.Id}:{m.Name}:{m.Version}:{m.Revision}:{m.Generation}:{m.Status}"))
        + "/pages/" + string.Join('|', _app.Modules.Registry.List<ModulePage>().Select(e => e.Id))
        + "/activities/" + string.Join('|', _app.Modules.Registry.List<IModuleActivity>().Select(e => e.Id))
        + "/widgets/" + string.Join('|', ModuleWidgetEntries().Select(e => e.Id + ":" + WidgetEnabled(e) + ":" + WidgetOrder(e)))
        + "/settings/" + string.Join('|', ModuleSettingsEntries().Select(e => $"{e.Contribution.Id}:{e.Placement}:{e.Visible}:{e.Order}:{e.IconPath}"))
        + "/discovery/" + string.Join('|', _app.ModuleCatalog.Failures.Select(f => f.Path + ":" + f.Error))
        + $"/files/{_app.ModuleFilesBusy}/{_app.PendingModuleCleanup}/{_app.HasRemovedBundles}/{_app.ModuleFileProblem}";
    private string SettingsPageTitle(int page) => page < _pages.Length ? _pages[page]
        : ModuleDetailId(page) is { } owner ? _app.ExternalModules.FirstOrDefault(m => m.Manifest.Id == owner)?.Manifest.Name ?? owner
        : ModuleSettingEntry(ModuleSettingsId(page)) is { } entry ? _app.Modules.Invoke(entry.ModuleId, "settings title", () => Loc.T(entry.Value.Name, entry.Value.NameEn)) ?? entry.Id : Loc.T("插件设置", "Plugin settings");
    private string? ModuleDetailId(int page) => ModuleSettingsId(page) is var id && id.StartsWith("plugin/", StringComparison.Ordinal) ? id[7..] : null;
    private int ModuleDetailPage(string id)
    {
        string key = "plugin/" + id;
        if (!_moduleSettingsPages.TryGetValue(key, out int page)) _moduleSettingsPages[key] = page = _nextModuleSettingsPage++;
        return page;
    }
    private Contribution<IModuleSettings>? ModuleSettingEntry(string id) => _app.Modules.Registry.List<IModuleSettings>().FirstOrDefault(e => e.Id == id);
    private IReadOnlyList<ModuleSettingsEntry> ModuleSettingsEntries() => ModuleSettingsIndex.Create(_app.Modules, S.Modules.Enabled && S.MiniApps.Enabled);
    private bool ModuleSettingVisible(Contribution<IModuleSettings> entry) => ModuleSettingsEntries().Any(e => ReferenceEquals(e.Contribution, entry) && e.Visible);
    private string ModuleSettingsId(int page) => _moduleSettingsPages.FirstOrDefault(pair => pair.Value == page).Key ?? "";
    private int SettingsPageFor(Contribution<IModuleSettings> entry)
    {
        if (!_moduleSettingsPages.TryGetValue(entry.Id, out int page)) _moduleSettingsPages[entry.Id] = page = _nextModuleSettingsPage++;
        return page;
    }
    private IEnumerable<int> ExternalSettingsPages()
    {
        if (!S.Modules.Enabled) yield break;
        foreach (var module in _app.Modules.Modules.Where(m => m.Status == ModuleStatus.Ready).OrderBy(m => S.Modules.EnabledModules.IndexOf(m.Id)).ThenBy(m => m.Id, StringComparer.Ordinal))
            yield return ModuleDetailPage(module.Id);
    }
    internal void ShowModuleSettings(string id)
    {
        string owner = ModuleSettingEntry(id)?.ModuleId ?? id.Split(':')[0];
        if (_app.Modules.Modules.Any(m => m.Id == owner)) { ShowPage(ModuleDetailPage(owner)); return; }
        ShowPage(ModulesPage);
    }
    internal void RefreshModules()
    {
        if (_scroll is null) return;
        string structure = ModuleStructure();
        if (_moduleStructure == structure) { if (_page == ModulesPage || ModuleDetailId(_page) is not null) RefreshModuleMetrics(); return; }
        _moduleStructure = structure;
        if (_page >= ModulesPage || _page == MiniAppsPage) ShowPage(_page);
        else BuildSettingsNavigation(_page);
    }
    private void RenderModuleSettings(string id)
    {
        if (id.StartsWith("plugin/", StringComparison.Ordinal)) { RenderPluginDetails(id[7..]); return; }
        var entry = ModuleSettingEntry(id);
        if (entry is null) { Group(TextLine(Loc.T("此组件当前未启用。", "This component is disabled."), 12, TextSecondary)); return; }
        if (entry.Value is IModuleCustomSettings custom)
        {
            var view = new ModuleViewHost(_app.Modules, entry.ModuleId, new ModuleViewContext(_content, target => _app.Deck.OpenModulePageFromSettings(target), _app.OpenModuleSettings, _ => { }), custom.CreateSettingsView);
            view.Height = 300; view.ClipToBounds = true; view.SetViewport(560, 300); view.SetPresented(true);
            view.SizeChanged += (_, _) => view.SetViewport(Math.Max(1, view.ActualWidth), 300);
            view.Unloaded += (_, _) => view.Dispose(); _content.Children.Add(view); return;
        }
        var fields = ModuleSettingsSchema.Read(_app.Modules, entry);
        long? revision = entry.Value is IModuleVersionedSettings versioned
            ? _app.Modules.Invoke(entry.ModuleId, "settings revision", () => versioned.GetSnapshot())?.Revision : null;
        if (entry.Value is IModuleVersionedSettings && revision is null) return;
        foreach (var group in fields.GroupBy(item => (item.Definition.Group, item.Definition.GroupEn)))
        {
            if (group.Key.Group.Length > 0) SectionTitle(Loc.T(group.Key.Group, group.Key.GroupEn));
            var rows = new List<FrameworkElement>();
            foreach (var item in group)
            {
                var field = item.Definition; object value = item.Value;
                bool Write(object next)
                {
                    bool current = _app.Modules.Registry.List<IModuleSettings>().Any(e => ReferenceEquals(e, entry));
                    bool saved = current && _app.Modules.Invoke(entry.ModuleId, "write setting", () =>
                    {
                        if (entry.Value is IModuleVersionedSettings schema)
                        {
                            var result = schema.Apply(new(revision!.Value, new Dictionary<string, object> { [field.Key] = next }));
                            if (result.Status != ModuleSettingsWriteStatus.Saved) return false;
                            revision = result.Revision; return true;
                        }
                        entry.Value.Write(field.Key, ModuleSettingsStore.Validate(field, next)); return true;
                    });
                    if (!saved) SaveStatus(Loc.T("模组设置未能保存，请查看模组详情。", "Module settings could not be saved. See module details."), true);
                    else SaveStatus("", false);
                    return saved;
                }
                string title = Loc.T(field.Label, field.LabelEn), description = Loc.T(field.Description, field.DescriptionEn);
                FrameworkElement row = field.Kind switch
                {
                    ModuleSettingKind.Toggle => Toggle(title, (bool)value, v => Write(v)),
                    ModuleSettingKind.Number => SliderRow(title, (double)value, field.Minimum, field.Maximum, field.Step, Loc.T(field.Unit, field.UnitEn), v => Write(v), fractional: true),
                    ModuleSettingKind.Choice => ChoiceRow(title, (int)value, field.Options.Select(o => (o.Value, Loc.T(o.Label, o.LabelEn))).ToArray(), v => Write(v)),
                    ModuleSettingKind.Text => ModuleTextField(field, (string)value, Write),
                    ModuleSettingKind.Color => ModuleColorField(field, (string)value, Write),
                    _ => throw new InvalidOperationException("The settings projection contained an unsupported field.")
                };
                AutomationProperties.SetAutomationId(row, "module-setting-" + entry.Id + ":" + field.Key);
                AutomationProperties.SetHelpText(row, description); rows.Add(row);
                if (description.Length > 0)
                {
                    var note = TextLine(description, 11.5, TextSecondary); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, -2, 0, 10); rows.Add(note);
                }
            }
            Group(rows.ToArray());
        }
        if (fields.Count == 0) Group(TextLine(Loc.T("暂无可显示的设置，详情见模组管理。", "No settings to display. See module management for details."), 12, TextSecondary));
    }
    private void ModulesSettingsPage()
    {
        _moduleStructure = ModuleStructure();
        var tabs = new UniformGrid { Columns = 2, Width = 248 };
        foreach (int tab in new[] { 0, 1 })
        {
            var button = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = tab == 0 ? Loc.T("插件管理", "Manage plugins") : Loc.T("性能监控", "Performance"), IsChecked = _moduleTab == tab, GroupName = "ModuleTab" };
            AutomationProperties.SetName(button, (string)button.Content); AutomationProperties.SetAutomationId(button, "module-tab-" + tab);
            button.Checked += (_, _) => { if (_moduleTab != tab) { _moduleTab = tab; _keptOffset = 0; _scroll.ScrollToTop(); ShowPage(ModulesPage); } }; tabs.Children.Add(button);
        }
        var segments = SegmentBorder(tabs); segments.HorizontalAlignment = HorizontalAlignment.Left; segments.Margin = new Thickness(0, 0, 0, 20); _content.Children.Add(segments);
        if (_moduleTab == 0 || !S.Modules.Enabled) ModuleManagementPage(); else ModulePerformancePage();
        UpdateModuleMonitoring();
    }
    private Border ModuleCard(FrameworkElement content, Thickness? padding = null)
        => new() { Child = content, Background = UI.Brush(_palette.Input), BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = padding ?? new Thickness(16), Margin = new Thickness(0, 0, 0, 12) };
    private CheckBox ModuleToggle(string title, bool enabled, Action<bool> change)
    {
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = enabled, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title); toggle.Checked += (_, _) => change(true); toggle.Unchecked += (_, _) => change(false); return toggle;
    }
    private void ModuleManagementPage()
    {
        if (_app.ModulesSkippedForSession)
            _content.Children.Add(ModuleNotice(_app.ModuleStartupProblem ?? Loc.T("本次未加载模组，已保存的开关和批准记录保留。重新启动后恢复正常加载。", "Modules are skipped for this run. Saved switches and approvals are retained. Restart to load modules normally.")));
        if (_app.PendingModuleCleanup > 0 || _app.ModuleFileProblem is not null)
        {
            var cleanup = ModuleNotice(Loc.T("有模组文件待清理，解除占用后刷新或重新启动继续。", "Some module files need cleanup. Release them, then refresh or restart."));
            cleanup.ToolTip = _app.ModuleFileProblem; _content.Children.Add(cleanup);
        }
        var orphans = _app.OrphanModuleData();
        if (orphans.Count > 0)
        {
            var orphanBody = new StackPanel();
            var orphanHeading = TextLine(Loc.T("未关联的数据目录：重新安装模组后仍可使用，确认不再需要再清理。",
                "Data folders with no module: reinstall the module to use them again; clear only what you no longer need."), 11, TextTertiary);
            orphanHeading.TextWrapping = TextWrapping.Wrap; orphanBody.Children.Add(orphanHeading);
            foreach (string id in orphans)
            {
                string orphan = id;
                var line = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 0) };
                var name = TextLine(orphan, 12, TextPrimary); DockPanel.SetDock(name, Dock.Left); line.Children.Add(name);
                line.Children.Add(ModuleLink(Loc.T("清理", "Clear"), async () =>
                {
                    if (!Confirm(Loc.T("删除未关联数据", "Delete unassociated data"), Loc.T($"永久删除 {orphan} 的插件数据与缓存？这不会删除源文件，但无法撤销。", $"Permanently delete plugin data and cache for {orphan}? Original source files are not deleted. This cannot be undone."), Loc.T("删除数据", "Delete data"))) return;
                    try { await _app.ClearOrphanDataAsync(orphan); }
                    catch (Exception error) { SaveStatus(error.Message, true); return; }
                    _moduleActionMessage = Loc.T($"已清理 {orphan} 的数据", $"Cleared data for {orphan}");
                    if (IsLoaded && _page == ModulesPage) ShowPage(ModulesPage);
                }));
                orphanBody.Children.Add(line);
            }
            _content.Children.Add(ModuleCard(orphanBody));
        }
        var subtitle = TextLine(Loc.T("统一管理内置与外部模组，关闭后保留设置和进度。", "Manage built-in and external modules; keep settings and progress when off."), 11.5, TextSecondary); subtitle.TextWrapping = TextWrapping.Wrap; subtitle.Margin = new Thickness(0, 5, 0, 0);
        var toggle = ModuleToggle(Loc.T("启用插件系统", "Enable plugins"), S.Modules.Enabled, value => Change(s => s.Modules.Enabled = value));
        toggle.IsEnabled = !_app.ModuleFilesBusy;
        AutomationProperties.SetAutomationId(toggle, "module-system-enabled");
        _content.Children.Add(ModuleCard(Row(Loc.T("插件系统", "Plugin system"), toggle, subtitle), new Thickness(18, 8, 18, 8)));
        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 12) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(ModuleLink(Loc.T("安装插件", "Install plugin"), InstallPluginFolder));
        actions.Children.Add(ModuleLink(Loc.T("打开插件文件夹", "Open plugin folder"), () =>
        {
            try { Directory.CreateDirectory(_app.ModuleDirectory); Process.Start(new ProcessStartInfo(_app.ModuleDirectory) { UseShellExecute = true }); _moduleActionMessage = Loc.T("已打开模组目录", "Module folder opened"); ShowPage(ModulesPage); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { SaveStatus(ex.Message, true); }
        }));
        actions.Children.Add(ModuleLink(Loc.T("刷新", "Refresh"), async () => { await _app.ConfigureModulesAsync(refresh: true); _moduleActionMessage = Loc.T("模组列表已更新", "Module list refreshed"); if (IsLoaded && _page == ModulesPage) ShowPage(ModulesPage); }));
        if (_app.HasRemovedBundles)
        {
            var restore = ModuleLink(Loc.T("恢复随附模组", "Restore bundled"), async () =>
            {
                try { await _app.RestoreBundledModulesAsync(); _moduleActionMessage = Loc.T("随附模组已恢复", "Bundled modules restored"); if (IsLoaded) ShowPage(ModulesPage); }
                catch (Exception error) { SaveStatus(error.Message, true); }
            });
            restore.IsEnabled = !_app.ModuleFilesBusy; actions.Children.Add(restore);
        }
        DockPanel.SetDock(actions, Dock.Right); toolbar.Children.Add(actions);
        var modules = _app.Modules.Modules;
        var count = TextLine("", 11.5, TextSecondary); toolbar.Children.Add(count);
        AddModuleMetricRefresh(_ => { var current = _app.Modules.Modules; SetMetricText(count, Loc.T($"{current.Count} 个模组 · {current.Count(m => m.Status == ModuleStatus.Ready)} 个运行中", $"{current.Count} modules · {current.Count(m => m.Status == ModuleStatus.Ready)} running")); });
        _content.Children.Add(toolbar);
        if (_moduleActionMessage is not null) { var note = TextLine(_moduleActionMessage, 11.5, TextSecondary); note.Margin = new Thickness(0, 0, 0, 12); _content.Children.Add(note); }
        var search = new TextBox { Style = (Style)FindResource("SettingsField"), Text = _moduleSearch, Height = 30, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(search, Loc.T("搜索插件", "Search plugins")); _content.Children.Add(search);
        var list = new StackPanel(); _content.Children.Add(list);
        if (!S.Modules.Enabled)
        {
            search.Visibility = Visibility.Collapsed;
            list.Children.Add(ModuleNotice(Loc.T("插件系统已关闭。刷新与文件维护不执行插件代码。", "Plugin system is off. Refresh and file maintenance do not execute plugin code.")));
            var maintenance = new StackPanel();
            foreach (var module in modules)
            {
                var external = _app.ExternalModules.FirstOrDefault(m => m.Manifest.Id == module.Id);
                maintenance.Children.Add(TextLine(module.Name + " · v" + module.Version, 13, TextPrimary));
                maintenance.Children.Add(ModuleStorageDetails(module, external));
            }
            list.Children.Add(new Expander { Header = Loc.T("文件与数据维护", "Files and data maintenance"), Foreground = UI.Brush(TextPrimary), Content = maintenance });
            return;
        }
        var cards = modules.Select(module => (module, card: ModuleEntryCard(module, false))).ToArray();
        foreach (var (_, card) in cards) list.Children.Add(card);
        void Filter()
        {
            _moduleSearch = search.Text.Trim();
            foreach (var (module, card) in cards)
            {
                string author = _app.ExternalModules.FirstOrDefault(m => m.Manifest.Id == module.Id)?.Manifest.Author ?? "";
                card.Visibility = _moduleSearch.Length == 0 || (module.Name + " " + module.Id + " " + author).Contains(_moduleSearch, StringComparison.CurrentCultureIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        search.TextChanged += (_, _) => Filter(); Filter();
        foreach (var failure in _app.ModuleCatalog.Failures)
            _content.Children.Add(ModuleCard(UI.Text(Path.GetFileName(failure.Path) + ": " + failure.Error, 11.5, TextSecondary)));
    }
    private Button ModuleLink(string title, Action open)
    {
        var button = ActionButton(title, open); button.HorizontalAlignment = HorizontalAlignment.Left; button.Margin = new Thickness(0, 0, 6, 0); return button;
    }
}
