using System.Windows;
using BrimDeck.Core;
using BrimDeck.Modules;

namespace BrimDeck;

public partial class App
{
    private ModulePermissionGate? _modulePermissions;
    private static string PermissionKey(ModulePermissionIdentity identity) => identity.ModuleId + ":" + identity.Capability + ":" + identity.RiskVersion;
    private ModulePermissionGate ModulePermissions => _modulePermissions ??= new(
        identity => Settings.Modules.PermissionGrants.GetValueOrDefault(PermissionKey(identity)) == identity.Digest,
        async (identity, lifetime) =>
        {
            if (lifetime.IsCancellationRequested || Exiting || Dispatcher.HasShutdownStarted) return ModulePermissionChoice.Deny;
            return await Dispatcher.InvokeAsync(() =>
            {
                // Background callers cannot open an unbounded series of permission dialogs.
                if (_settingsWindow is not { IsVisible: true, IsActive: true } window) return ModulePermissionChoice.Deny;
                var choice = MessageBox.Show(window, Loc.T($"插件 {identity.ModuleId} 请求敏感能力：{identity.Capability}\n\n是：记住允许当前内容\n否：仅允许本次\n取消：拒绝（默认）", $"Plugin {identity.ModuleId} requests: {identity.Capability}\n\nYes: remember for this package content\nNo: allow once\nCancel: deny (default)"), Loc.T("插件权限", "Plugin permission"), MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                return choice switch { MessageBoxResult.Yes => ModulePermissionChoice.Remember, MessageBoxResult.No => ModulePermissionChoice.Once, _ => ModulePermissionChoice.Deny };
            });
        },
        identity => { Settings.Modules.PermissionGrants[PermissionKey(identity)] = identity.Digest; QueueSave(); });
    internal void RevokeModulePermissions(string id)
    {
        ModulePermissions.Revoke(id);
        foreach (string key in Settings.Modules.PermissionGrants.Keys.Where(k => k.StartsWith(id + ":", StringComparison.Ordinal)).ToArray()) Settings.Modules.PermissionGrants.Remove(key);
        QueueSave();
    }
}
