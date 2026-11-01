using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Modules;
using Microsoft.Win32;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private async void InstallPluginFolder()
    {
        if (_app.ModuleFilesBusy) return;
        var dialog = new OpenFolderDialog { Title = Loc.T("选择含 module.json 的插件文件夹", "Select a plugin folder containing module.json"), Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var report = await Task.Run(() => ModulePackageInspector.Inspect(dialog.FolderName));
            if (!report.Passed || report.Manifest is null || report.Digest is null)
            {
                SaveStatus(string.Join("\n", report.Issues.Where(i => i.Severity == ModuleCheckSeverity.Error).Take(8).Select(i => i.Code + ": " + i.Message)), true); return;
            }
            var previous = _app.ExternalModules.FirstOrDefault(m => m.Manifest.Id == report.Manifest.Id);
            string action = previous is null ? Loc.T("安装插件", "Install plugin") : Loc.T("更新插件", "Update plugin");
            string message = Loc.T($"{report.Manifest.Name} · v{report.Manifest.Version}\n作者：{report.Manifest.Author}\n\n安装不等于启用，新内容需要单独信任。", $"{report.Manifest.Name} · v{report.Manifest.Version}\nAuthor: {report.Manifest.Author}\n\nInstallation does not enable this plugin. New content requires separate approval.");
            if (previous is not null) message += Loc.T("\n旧插件和依赖方会停止，保留旧包恢复点；业务数据不会被替换。恢复旧包前仍需确认数据版本兼容。", "\nThe old plugin and its dependents will stop. A package restore point is kept; business data is not replaced. Confirm data compatibility before restoring an older package.");
            if (!Confirm(action, message, action)) return;
            var result = await _app.InstallModuleFolderAsync(dialog.FolderName, report.Digest, previous);
            _moduleActionMessage = result.Pending ? Loc.T("安装尚未完成，旧包或暂存包已保留。解除文件占用后重启并检查恢复点。", "Installation is pending; the previous or staged package was kept. Release file locks, restart and inspect restore points.")
                : Loc.T("插件已安装但未启用，请检查详情后信任并启用。", "Plugin installed, not enabled. Review its details before trusting and enabling it.");
            if (result.Error is not null) SaveStatus(result.Error, true);
            if (IsLoaded) ShowPage(ModulesPage);
        }
        catch (Exception error) { SaveStatus(error.Message, true); }
    }

    private void RenderPluginDetails(string id)
    {
        var module = _app.Modules.Modules.FirstOrDefault(m => m.Id == id);
        if (module is null) { Group(TextLine(Loc.T("插件当前不可用，已保留偏好。", "Plugin unavailable; preferences were kept."), 12, TextSecondary)); return; }
        var package = _app.ExternalModules.FirstOrDefault(m => m.Manifest.Id == id);
        var body = new StackPanel();
        body.Children.Add(TextLine(module.Name + " · v" + module.Version, 16, TextPrimary, FontWeights.SemiBold));
        body.Children.Add(TextLine(ModuleStateLabel(module), 12, TextSecondary));
        ModuleManagementDetails(module, package, body); _content.Children.Add(ModuleCard(body));
        var actions = new WrapPanel();
        actions.Children.Add(ModuleLink(Loc.T("重新加载此插件", "Reload this plugin"), async () =>
        {
            if (!Confirm(Loc.T("重新加载插件", "Reload plugin"), Loc.T("此插件和依赖方将停止并重新激活。未变化的其他插件保持运行。", "This plugin and its dependents will stop and reactivate. Other unchanged plugins remain running."), Loc.T("重新加载", "Reload"))) return;
            try { await _app.ReloadModuleAsync(id); if (IsLoaded) ShowPage(_page); }
            catch (Exception error) { SaveStatus(error.Message, true); }
        }));
        actions.Children.Add(ModuleLink(Loc.T("撤销敏感能力授权", "Revoke sensitive permissions"), () =>
        {
            _app.RevokeModulePermissions(id); SaveStatus(Loc.T("敏感能力授权已撤销，旧句柄和排队操作立即失效。", "Sensitive permissions revoked; old handles and queued operations are no longer authorized."), false);
        }));
        _content.Children.Add(actions);
        foreach (var page in _app.Modules.Registry.List<ModulePage>().Where(p => p.ModuleId == id))
        {
            string title = Loc.T(page.Value.Title, page.Value.TitleEn);
            if (page.Value.SupportsLocalTheme)
                _content.Children.Add(ModuleLink(Loc.T("预览：", "Preview: ") + title, () => new ModulePreviewWindow(_app.Modules, page, this) { Owner = this }.Show()));
            else _content.Children.Add(TextLine(title + Loc.T("：此旧页面未声明支持局部主题预览。", ": this legacy page does not declare local theme preview support."), 12, TextSecondary));
        }
        if (package is not null)
        {
            IReadOnlyList<string> points;
            try { points = _app.ModuleRestorePoints(id); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SaveStatus(error.Message, true); points = []; }
            if (points.Count > 0)
                _content.Children.Add(ModuleLink(Loc.T("恢复旧安装包", "Restore previous package"), async () =>
                {
                    if (!Confirm(Loc.T("仅恢复插件文件", "Restore package files only"), Loc.T("仅恢复旧 DLL 和资源，不回滚业务数据。请先确认旧版本兼容当前数据；恢复后仍需重新信任。", "Only DLLs and resources are restored. Business data is not rolled back. Confirm the old version supports current data; approval is required again."), Loc.T("恢复旧包", "Restore package"))) return;
                    try
                    {
                        var result = await _app.RestoreModulePackageAsync(id, points[^1]);
                        if (result.Pending) SaveStatus(result.Error ?? Loc.T("恢复待重启后继续。", "Restore pending until restart."), true);
                        if (IsLoaded) ShowPage(ModulesPage);
                    }
                    catch (Exception error) { SaveStatus(error.Message, true); }
                }));
        }
        foreach (var entry in ModuleSettingsEntries().Where(e => e.Contribution.ModuleId == id && e.Visible))
        {
            var settings = entry.Contribution;
            SectionTitle(_app.Modules.Invoke(id, "settings title", () => Loc.T(settings.Value.Name, settings.Value.NameEn)) ?? settings.Id);
            _content.Children.Add(ModuleLink(Loc.T("恢复当前设置默认值", "Reset these settings"), () =>
            {
                if (!Confirm(Loc.T("恢复插件设置", "Reset plugin settings"), Loc.T("只重置当前设置分组，不删除业务数据、展示偏好或授权。", "Reset only this settings group, not business data, presentation preferences or permissions."), Loc.T("恢复默认", "Reset"))) return;
                _app.Modules.Invoke(id, "reset settings", () => { settings.Value.Reset(); return true; }); ShowPage(_page);
            }));
            RenderModuleSettings(settings.Id);
        }
        UpdateModuleMonitoring();
        if (!ModuleSettingsEntries().Any(e => e.Contribution.ModuleId == id && e.Visible))
            Group(TextLine(Loc.T("此插件没有自定义设置。", "This plugin has no custom settings."), 12, TextSecondary));
    }
}
