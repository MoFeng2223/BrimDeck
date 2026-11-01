// ClipboardItem.handleCardClick / CardMenu.MenuList translation from Edge-Drop 767b6d63 (Apache-2.0).
// Native ContextMenu owns outside-click capture, placement, keyboard dismissal and the host's shared popup motion.
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BrimDeck.Core;

namespace BrimDeck;

internal sealed partial class ClipboardView
{
    private bool _cardPressBlocked;

    private void InitializeCardActions()
    {
        _list.PreviewMouseDown += (_, e) =>
        {
            StopPreviewTimers();
            if (e.ChangedButton != MouseButton.Left) { CloseHoverPreview(); return; }
            CloseHoverPreview();
            _cardPressBlocked = _activeMenu?.IsOpen == true || _editor?.IsOpen == true;
        };
        _list.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Right) return;
            if (Find<ListBoxItem>(e.OriginalSource as DependencyObject) is { } row) row.IsSelected = true;
        };
        _list.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.ChangedButton != MouseButton.Right || _drag.IsDragging || Find<ButtonBase>(e.OriginalSource as DependencyObject) is not null
                || Find<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not ClipboardEntry entry) return;
            OpenItemMenu(entry, true); e.Handled = true;
        }), true);
        AutomationProperties.SetHelpText(_list, Loc.T("单击按默认动作复制或回贴；Shift+单击使用纯文本；Ctrl+Enter 仅复制；右键管理卡片。", "Click follows the default copy/paste action; Shift+click uses plain text; Ctrl+Enter copies only; right-click for card actions."));
    }
    private void ClickCard(ClipboardEntry entry)
    {
        if (_cardPressBlocked || _drag.IsDragging) return;
        // A detached preview is not a dismiss gesture: clicking its original card still performs the normal action.
        if (_preview is not null) _preview.IsOpen = false;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && entry.Content.Text.Length > 0) entry = PlainEntry(entry);
        _activate(entry, !_settings().PasteOnActivate);
    }
    private static ClipboardEntry PlainEntry(ClipboardEntry entry) => entry with { Content = new ClipboardContent { Kind = ClipboardContentKind.Text, Text = entry.Content.Text } };
    private void CopyText(ClipboardEntry entry, string text) => _activate(entry with { Content = ClipboardContent.FromText(text) }, true);
    private void OpenItemMenu(ClipboardEntry entry, bool pointer)
    {
        if (_drag.IsDragging) return;
        CloseHoverPreview();
        if (_activeMenu is not null) _activeMenu.IsOpen = false;
        var menu = ItemMenu(entry); _list.ContextMenu = menu;
        menu.PlacementTarget = _list.ItemContainerGenerator.ContainerFromItem(entry) as UIElement ?? _list;
        menu.Placement = pointer ? PlacementMode.MousePoint : PlacementMode.Bottom;
        menu.IsOpen = true;
    }
    private ContextMenu ItemMenu(ClipboardEntry item)
    {
        var menu = Menu();
        void Row(string id, string label, string glyph, Action action)
        {
            AddMenu(menu, label, () => { menu.IsOpen = false; action(); });
            var row = (MenuItem)menu.Items[^1]; row.Icon = EdgeDropGlyph.Create(glyph, 14);
            AutomationProperties.SetAutomationId(row, "clipboard-card-menu-" + id);
        }
        Row("paste", Loc.T("粘贴到目标应用", "Paste into target app"), "clipboardText", () => _activate(item, false));
        if (item.Content.Text.Length > 0 && (item.Content.Html.Length > 0 || item.Content.Rtf.Length > 0 || item.Content.Text.Contains('\t')))
            Row("plain", Loc.T("粘贴为纯文本", "Paste as plain text"), "textT", () => _activate(PlainEntry(item), false));
        Row("copy", Loc.T("仅复制 · Ctrl+Enter", "Copy only · Ctrl+Enter"), "copy", () => _activate(item, true));
        menu.Items.Add(new Separator { Style = (Style)FindResource("TrayMenuSeparator") });
        Row("details", Loc.T("详情 / 重命名 / 编辑…", "Details / rename / edit…"), "textT", () => ShowDetails(item));
        Row("favorite", item.Favorite ? Loc.T("取消收藏", "Remove favorite") : Loc.T("收藏", "Favorite"), "pushPin", () => _history.ToggleFavorite(item.Id));
        Row("archive", item.Archived ? Loc.T("取消归档", "Unarchive") : Loc.T("归档", "Archive"), "folderOpen", () => _history.Archive(item.Id, !item.Archived));
        var collections = new MenuItem { Header = Loc.T("移到收藏夹", "Move to collection"), Style = (Style)FindResource("LibraryMenuItem"), IsEnabled = _history.Collections.Count > 0 };
        foreach (var group in _history.Collections)
        {
            var move = new MenuItem { Header = group.Name, Style = (Style)FindResource("TrayMenuItem") };
            move.Click += (_, e) => { menu.IsOpen = false; _history.Move(item.Id, group.Id); e.Handled = true; }; collections.Items.Add(move);
        }
        menu.Items.Add(collections);
        if (item.CollectionId.Length > 0) Row("unfile", Loc.T("移出分类", "Remove from collection"), "folderOpen", () => _history.Move(item.Id, ""));
        menu.Items.Add(new Separator { Style = (Style)FindResource("TrayMenuSeparator") });
        if (item.Content.Kind == ClipboardContentKind.Link)
        {
            Row("open-link", Loc.T("打开链接", "Open link"), "arrowSquareOut", () => OpenLink(item.Content.Text));
            string clean = EdgeDropPresentation.CleanLink(item.Content.Text);
            if (clean != item.Content.Text.Trim()) Row("clean-link", Loc.T("复制干净链接", "Copy clean link"), "link", () => CopyText(item, clean));
        }
        if (item.Content.Kind == ClipboardContentKind.Color)
            foreach (var (format, text) in EdgeDropPresentation.ColorFormats(item.Content.Text))
                Row("color-" + format.ToLowerInvariant(), Loc.T("复制为 ", "Copy as ") + format, "palette", () => CopyText(item, text));
        if (item.Content.Kind == ClipboardContentKind.Files)
        {
            Row("reveal", Loc.T("在文件夹中显示", "Show in folder"), "folderOpen", () => RevealFile(item.Content.Files[0]));
            Row("paths", item.Content.Files.Length > 1 ? Loc.T("复制文件路径（全部）", "Copy all file paths") : Loc.T("复制文件路径", "Copy file path"), "copy", () => CopyText(item, string.Join("\r\n", item.Content.Files)));
        }
        if (item.Content.Kind is ClipboardContentKind.Image or ClipboardContentKind.Screenshot)
            Row("save-image", Loc.T("图片另存为…", "Save image as…"), "downloadSimple", () => SaveImage(item));
        if (menu.Items[^1] is not Separator) menu.Items.Add(new Separator { Style = (Style)FindResource("TrayMenuSeparator") });
        Row("preview", Loc.T("预览浮窗…", "Preview window…"), "arrowsOutSimple", () => ShowPreview(item));
        if (_openLibrary is not null) Row("library", Loc.T("在剪贴板库中管理…", "Manage in Clipboard Library…"), "folderOpen", () => _openLibrary(item));
        Row("delete", Loc.T("删除记录… · Delete", "Delete entry… · Delete"), "trash", () => DeleteEntry(item));
        return menu;
    }
    private void OpenLink(string value)
    {
        // Only an explicit menu action launches a browser; local/executable URI schemes never enter this boundary.
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { CardActionFailed(); }
    }
    private void RevealFile(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) { CardActionFailed(); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "/select,\"" + path + "\"" }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { CardActionFailed(); }
    }
    private void SaveImage(ClipboardEntry item)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "Clipboard.png", DefaultExt = ".png", Filter = "PNG (*.png)|*.png" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllBytes(dialog.FileName, item.Content.Image); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { CardActionFailed(); }
    }
    private void CardActionFailed()
    {
        ShowNotice(Loc.T("操作未完成，请检查源文件或目标程序。原内容未被删除。", "The action could not be completed. Check the original file or target app; the original content was not deleted."));
    }
}
