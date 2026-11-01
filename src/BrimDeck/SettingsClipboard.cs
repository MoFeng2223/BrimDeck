using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    internal const int ClipboardPage = 11;
    private void ClipboardSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.ClipboardWidth, ClipboardDockLayout.MinWidth, DeckPages.MaxWidth, value => Resize(s => { s.ClipboardWidth = value; s.ClipboardReferenceSize = false; }, DeckPage.Clipboard));
        width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.ClipboardHeight, ClipboardDockLayout.Height, DeckPages.MaxHeight, value => Resize(s => { s.ClipboardHeight = value; s.ClipboardReferenceSize = false; }, DeckPage.Clipboard));
        height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        var restore = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        restore.Children.Add(ActionButton(Loc.T("恢复参考尺寸 · 1200×210", "Reference size · 1200×210"), () => { Change(ClipboardDockLayout.Reset); ShowPage(_page); }));
        if (S.ClipboardPreviousLayout is not null) restore.Children.Add(ActionButton(Loc.T("恢复迁移前布局", "Restore previous layout"), () => { Change(ClipboardDockLayout.RestorePrevious); ShowPage(_page); }));
        Target.Children.Add(restore);
    }
    private void ClipboardSettingsPage()
    {
        ModuleModeControl(true);
        SectionTitle(Loc.T("剪贴板历史", "Clipboard history"), help: () => Loc.T(
            "仅记录启用后的复制操作；不上传、不读取旧剪贴板。支持文本、富文本、链接、代码、RGB / 渐变、截图、图片、SVG 和文件引用。密码管理器标记为私密的内容始终跳过。",
            "Only copies made after enabling are recorded. No uploads or pre-existing clipboard reads. Supports text, rich text, links, code, RGB/gradients, screenshots, images, SVG and file references. Password-manager private markers are always respected."));
        Group(Toggle(Loc.T("启用剪贴板记录", "Enable clipboard recording"), S.Clipboard.Enabled,
                value => { Change(s => s.Clipboard.Enabled = value); ShowPage(_page); }),
            Row(Loc.T("灵动岛剪贴板页面", "Island clipboard page"), ActionButton(Loc.T("打开剪贴板", "Open clipboard"), _app.ToggleClipboard)),
            Row(Loc.T("完整剪贴板库", "Full clipboard library"), ActionButton(Loc.T("打开管理窗口", "Open library manager"), _app.OpenClipboardLibrary)),
            HotkeyRow(HotkeyKind.Clipboard));

        SectionTitle(Loc.T("记录与清理", "History and cleanup"));
        var limits = new[] { 50, 100, 200, 500, 1000 }.Append(S.Clipboard.MaximumItems).Where(value => value > 0).Distinct().Order()
            .Select(value => (value, value.ToString())).Append((0, Loc.T("无上限", "Unlimited"))).ToArray();
        var retention = new[] { 0, 1, 7, 14, 30, 90, 365 }.Append(S.Clipboard.RetentionDays).Distinct().Order()
            .Select(value => (value, value == 0 ? Loc.T("不自动清理", "Never") : Loc.T($"{value} 天", $"{value} days"))).ToArray();
        var historyLimit = MusicChoice(Loc.T("历史记录上限", "History limit"), S.Clipboard.MaximumItems, limits, value => Change(s => s.Clipboard.MaximumItems = value),
            help: Loc.T("无上限仅取消记录条数限制；自动清理天数、单条 8 MB 和历史内容合计 32 MB 的安全限制仍生效。", "Unlimited removes only the entry-count limit. Retention, the 8 MB entry limit and 32 MB total content safety budget still apply."));
        System.Windows.Automation.AutomationProperties.SetAutomationId(historyLimit, "clipboard-history-limit");
        Group(historyLimit,
            MusicChoice(Loc.T("自动清理", "Auto-cleanup"), S.Clipboard.RetentionDays, retention, value => Change(s => s.Clipboard.RetentionDays = value)),
            Toggle(Loc.T("记录图片", "Capture images"), S.Clipboard.CaptureImages, value => Change(s => s.Clipboard.CaptureImages = value), help:
                Loc.T("单条最多 8 MB，图片最多 1600 万像素，全部历史的内容最多 32 MB。", "Up to 8 MB per entry, 16 megapixels per image and 32 MB of history content in total.")),
            Toggle(Loc.T("记录文件与文件夹", "Capture files and folders"), S.Clipboard.CaptureFiles, value => Change(s => s.Clipboard.CaptureFiles = value), help:
                Loc.T("仅保存路径，不复制或删除原文件；原文件不存在时不能重新填充。", "Only paths are saved; original files are never copied or deleted. Missing originals cannot be recalled.")));

        SectionTitle(Loc.T("面板与粘贴", "Panel and paste"));
        Group(Toggle(Loc.T("选择后快速粘贴", "Paste on activation"), S.Clipboard.PasteOnActivate, value => Change(s => s.Clipboard.PasteOnActivate = value), help:
                Loc.T("控制 Enter 或 Alt+数字是否同时粘贴。灵动岛卡片单击始终回贴；Ctrl+Enter 始终只复制。系统阻止按键时请手动 Ctrl+V。",
                    "Controls whether Enter or Alt+number also pastes. A single click on an island card always pastes; Ctrl+Enter always copies only. Use Ctrl+V if Windows blocks simulated keys.")),
            Toggle(Loc.T("复制后收起面板", "Collapse after copying"), S.Clipboard.CloseAfterAction, value => Change(s => s.Clipboard.CloseAfterAction = value)),
            Toggle(Loc.T("输入时失焦收起面板", "Collapse when input loses focus"), S.Clipboard.HideOnDeactivate, value => Change(s => s.Clipboard.HideOnDeactivate = value)),
            Toggle(Loc.T("网格视图", "Grid view"), S.Clipboard.GridView, value => Change(s => s.Clipboard.GridView = value)));

        SectionTitle(Loc.T("公共页眉", "Shared header"));
        var headerMode = MusicChoice(Loc.T("显示方式", "Display mode"), S.Clipboard.HeaderMode,
            [(ClipboardHeaderMode.Edge, Loc.T("顶部边缘唤出", "Reveal at top edge")), (ClipboardHeaderMode.Always, Loc.T("常驻显示", "Always visible"))],
            value => Change(s => s.Clipboard.HeaderMode = value), help: Loc.T(
                "顶部边缘唤出：剪贴板展开后，将鼠标再次移到面板正上方的屏幕顶部边缘，显示公共页面切换、时钟和设置；移回内容后自动收起。页眉使用灵动岛尺寸变化动效向下推开内容，空间不足时占用内容区域。常驻显示时每次展开都保留页眉。",
                "With the clipboard expanded, move to the screen's top edge directly above it to reveal the shared page switch, clock and settings. Returning to the content hides it. The island's resize motion pushes content down, using its space when the work area is full. Always visible keeps the header on every expansion."));
        System.Windows.Automation.AutomationProperties.SetAutomationId(headerMode, "clipboard-shared-header-mode");
        Group(headerMode);

        SectionTitle(Loc.T("悬停预览", "Hover preview"));
        Group(ClipboardPreviewSensitivityControl.Create(S.Clipboard.PreviewSensitivity, _app.SetClipboardPreviewSensitivity));

        SectionTitle(Loc.T("隐私与数据", "Privacy and data"));
        var exclusions = new TextBox { Text = S.Clipboard.ExcludedApplications, Style = (Style)FindResource("SettingsField"), Width = 300, MaxLength = 4096 };
        System.Windows.Automation.AutomationProperties.SetName(exclusions, Loc.T("不记录的程序", "Excluded applications"));
        exclusions.LostKeyboardFocus += (_, _) => { if (exclusions.Text != S.Clipboard.ExcludedApplications) Change(s => s.Clipboard.ExcludedApplications = exclusions.Text); };
        var persistence = Toggle(Loc.T("持久化存储", "Persistent storage"), S.Clipboard.PersistHistory, value => Change(s => s.Clipboard.PersistHistory = value), help:
            Loc.T("历史由 Windows 当前用户加密保存，重启后可恢复。关闭后删除磁盘历史，仅保留本次会话；收藏与归档不参与自动清理。选择无上限不改变保留天数和内容容量保护。",
                "History is encrypted for the current Windows user and restored after restart. Turning this off deletes disk history and keeps this session only. Favorites and archived entries survive cleanup. Unlimited does not change retention or content capacity safeguards."));
        System.Windows.Automation.AutomationProperties.SetAutomationId(persistence, "clipboard-persistent-storage");
        Group(persistence);
        ClipboardDataStorageControls();
        Group(Row(Loc.T("不记录的程序", "Excluded applications"), exclusions, help: () => Loc.T("填写程序进程名，以逗号分隔，例如 KeePassXC,1Password。保存于本机，不记录窗口标题。", "Comma-separated process names, e.g. KeePassXC,1Password. Stored locally; window titles are never captured.")),
            Row(Loc.T("清空未收藏记录", "Clear non-favorites"), ActionButton(Loc.T("清空历史", "Clear history"), () => _app.ClipboardHistoryService?.History.Clear())),
            Row(Loc.T("删除所有历史（包括收藏）", "Delete all history (including favorites)"), ActionButton(Loc.T("删除全部…", "Delete all…"), () =>
            {
                if (MessageBox.Show(this, Loc.T("删除全部剪贴板历史及收藏？此操作不可撤销。系统剪贴板和原文件不会被更改。", "Delete all clipboard history and favorites? This cannot be undone. The system clipboard and original files will not be changed."),
                    Loc.T("删除剪贴板历史", "Delete clipboard history"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                    _app.ClipboardHistoryService?.History.Clear(false);
            })));
    }
}
