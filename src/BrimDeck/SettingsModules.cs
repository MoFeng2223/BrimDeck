using System.Windows;
using System.Windows.Automation;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private void ModuleModeControl(bool clipboard)
    {
        var mode = clipboard ? S.Clipboard.Mode : S.Tray.Mode;
        var choice = MusicChoice(Loc.T("运行模式", "Run mode"), mode,
            [(ModuleMode.Full, Loc.T("完整", "Full")), (ModuleMode.Performance, Loc.T("性能", "Performance")), (ModuleMode.Off, Loc.T("关闭", "Off"))],
            async value =>
            {
                await _app.ChangeModuleModeAsync(clipboard, value);
                if (IsVisible) ShowPage(_page);
            }, enabled: !_app.ModulesChanging, help: Loc.T(
                "完整：保留全部功能。性能：关闭此模块动画、自动悬停与手势呼出，使用快捷键或显式入口，按需低分辨率预览。关闭：释放模块后台资源，保留已保存数据。记录许可与展示位置独立保留。",
                "Full keeps all features. Performance disables this module's motion, hover/gesture opening and background polling, using explicit entry points and smaller previews. Off releases the runtime and keeps saved data. Recording consent and placement are unchanged."));
        AutomationProperties.SetAutomationId(choice, clipboard ? "clipboard-module-mode" : "shelf-module-mode");
        Group(choice);
    }

    internal bool ConfirmShelfModuleClose()
    {
        if (_shelfContent?.IsModal != true) return true;
        return MessageBox.Show(this, Loc.T("关闭文件中转将放弃尚未保存的快速操作编辑，继续？", "Turning off the shelf discards the unsaved action editor. Continue?"),
            Loc.T("关闭模块", "Turn off module"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
    internal void ReleaseShelfSettings()
    {
        _shelfContent?.Dispose(); _shelfContent = null;
        if (_shelfSlot is not null) _shelfSlot.Content = null;
        _navigation.IsEnabled = _content.IsEnabled = true;
    }
}
