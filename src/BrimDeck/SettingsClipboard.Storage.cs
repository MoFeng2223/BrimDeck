using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;
using DropShelf.Services;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private string? _clipboardStorageDraft;

    private void ClipboardDataStorageControls()
    {
        var service = _app.ClipboardHistoryService;
        var summary = new StackPanel { Margin = new Thickness(0, 6, 0, 8), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
        var content = TextLine("", 12, TextPrimary);
        var disk = TextLine("", 12, TextSecondary);
        var cache = TextLine("", 12, TextSecondary);
        content.TextWrapping = disk.TextWrapping = cache.TextWrapping = TextWrapping.Wrap;
        summary.Children.Add(content); summary.Children.Add(disk); summary.Children.Add(cache);
        AutomationProperties.SetAutomationId(summary, "clipboard-storage-usage");
        var refresh = ActionButton(Loc.T("刷新用量", "Refresh usage"), () => { });
        var path = new TextBox { Style = (Style)FindResource("SettingsField"), Text = _clipboardStorageDraft ?? service?.StorageDirectory ?? S.Clipboard.StorageDirectory, MinWidth = 100, MaxLength = 32768 };
        AutomationProperties.SetName(path, Loc.T("数据存储路径", "Data storage path"));
        AutomationProperties.SetAutomationId(path, "clipboard-storage-path");
        path.TextChanged += (_, _) => _clipboardStorageDraft = path.Text;
        var folder = TextLine(service?.StorageDirectory ?? "", 11, TextSecondary); folder.TextWrapping = TextWrapping.Wrap;
        var feedback = TextLine("", 11, TextSecondary); feedback.TextWrapping = TextWrapping.Wrap;
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 4) };
        bool busy = false;

        static string Bytes(long? bytes) => bytes is not { } value ? Loc.T("无法读取", "Unavailable") : value switch
        {
            < 1024 => $"{value} B",
            < 1024 * 1024 => $"{value / 1024d:0.##} KB",
            < 1024 * 1024 * 1024 => $"{value / (1024d * 1024):0.##} MB",
            _ => $"{value / (1024d * 1024 * 1024):0.##} GB"
        };
        async Task Refresh()
        {
            if (service is null) { content.Text = Loc.T("剪贴板服务尚未就绪", "Clipboard service is not ready"); return; }
            refresh.IsEnabled = false;
            try
            {
                var usage = await service.MeasureStorageAsync();
                content.Text = Loc.T($"{usage.Count:N0} 条记录 · 内容 {Bytes(usage.ContentBytes)}", $"{usage.Count:N0} entries · content {Bytes(usage.ContentBytes)}");
                disk.Text = Loc.T($"加密历史 {Bytes(usage.HistoryBytes)} · 写入临时文件 {Bytes(usage.PendingBytes)}", $"Encrypted history {Bytes(usage.HistoryBytes)} · pending file {Bytes(usage.PendingBytes)}");
                cache.Text = Loc.T($"共享附件缓存 {Bytes(usage.CacheBytes)}", $"Shared attachment cache {Bytes(usage.CacheBytes)}");
                folder.Text = service.StorageDirectory;
            }
            finally { refresh.IsEnabled = service is not null; }
        }
        refresh.Click += async (_, _) => await Refresh();
        async Task Apply(string requested)
        {
            if (busy || service is null) return;
            busy = true; buttons.IsEnabled = path.IsEnabled = false;
            try
            {
                var result = await _app.SetClipboardStorageDirectoryAsync(requested);
                feedback.Text = result.Message;
                if (result.Succeeded) { path.Text = service.StorageDirectory; _clipboardStorageDraft = null; }
                await Refresh();
            }
            finally { busy = false; buttons.IsEnabled = path.IsEnabled = true; }
        }
        var browse = ActionButton(Loc.T("选择文件夹…", "Choose folder…"), () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("选择剪贴板数据存储目录", "Choose clipboard data directory"), Multiselect = false };
            if (Directory.Exists(path.Text)) dialog.InitialDirectory = path.Text;
            if (dialog.ShowDialog(this) == true) path.Text = dialog.FolderName;
        });
        var apply = ActionButton(Loc.T("应用路径", "Apply path"), () => { }); apply.Click += async (_, _) => await Apply(path.Text);
        var reset = ActionButton(Loc.T("恢复默认", "Use default"), () => { }); reset.Click += async (_, _) => await Apply("");
        foreach (var button in new[] { browse, apply, reset }) { button.Margin = new Thickness(0, 0, 8, 0); buttons.Children.Add(button); }
        AutomationProperties.SetAutomationId(apply, "clipboard-storage-apply");
        AutomationProperties.SetAutomationId(reset, "clipboard-storage-reset");
        var storage = new StackPanel();
        storage.Children.Add(SettingLabel(Loc.T("数据存储路径", "Data storage path"), () => Loc.T(
            "应用后迁移当前加密历史；目标目录已有历史时拒绝覆盖或混合。保存新路径成功后才删除旧加密文件，失败时保留原位置。关闭持久化时仅更改之后的保存位置。普通源文件与共享附件缓存不迁移。",
            "Applying migrates the current encrypted history. Existing target history is never overwritten or merged. The old encrypted file is removed only after the new path is saved; failure keeps the original location. With persistence off, only the future save location changes. Original files and the shared attachment cache are not moved.")));
        path.Margin = new Thickness(0, 6, 0, 0); storage.Children.Add(path); storage.Children.Add(buttons); storage.Children.Add(feedback);
        storage.Margin = new Thickness(0, 10, 0, 12);
        var usageRow = Row(Loc.T("记录存储与缓存", "History storage and cache"), refresh, help: () => Loc.T(
            "内容用量是历史载荷的编码大小，不是进程内存。加密历史为实际磁盘文件大小。共享附件缓存由剪贴板拖出和文件中转置物架共用；不统计或读取普通原文件，不包含未落盘预览内存。刷新只统计文件尺寸，不清理缓存。",
            "Content usage is the encoded history payload, not process memory. Encrypted history is its actual disk size. Attachment cache is shared by clipboard exports and file-transfer shelves; ordinary originals and in-memory previews are excluded. Refresh only measures sizes; it does not clear the cache."));
        var block = new StackPanel(); block.Children.Add(usageRow); block.Children.Add(summary); block.Children.Add(folder); block.Children.Add(storage);
        AutomationProperties.SetAutomationId(block, "clipboard-data-storage");
        Group(block);
        block.Loaded += async (_, _) =>
        {
            if (service?.Loaded == true) await Refresh();
            else content.Text = service is null ? Loc.T("模块已关闭；数据保留在原位置。", "Module off; saved data stays in place.")
                : Loc.T("历史尚未加载，点击刷新用量时按需读取。", "History is not loaded. Refresh usage to read it on demand.");
        };
        buttons.IsEnabled = path.IsEnabled = service is not null && !service.ChangingStorage;
    }
}
