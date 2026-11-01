using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

// Source actions are distinct from collection management. Hidden sources stay in history, search and capture.
internal static class ClipboardApplicationMenu
{
    internal static void Populate(ContextMenu menu, FrameworkElement owner, FrameworkElement anchor, string source, bool unknown, ClipboardHistory history,
        Func<ClipboardSettings> preferences, Action<string, bool> pin, Action<string, bool> hide, Func<PromptRequest, bool> confirm,
        Action<FrameworkElement, Action> removeLabel, Action<string> feedback)
    {
        Style leaf = (Style)owner.FindResource("TrayMenuItem");
        MenuItem Item(string label, string id, Action action)
        {
            var item = new MenuItem { Header = label, Style = leaf };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, e) => { menu.IsOpen = false; action(); e.Handled = true; };
            menu.Items.Add(item); return item;
        }
        if (source.Length > 0 || unknown)
        {
            bool pinned = preferences().IsApplicationPinned(source);
            Item(pinned ? Loc.T("取消固定", "Unpin") : Loc.T("固定到最前", "Pin to start"), "clipboard-application-pin", () => pin(source, !pinned));
            Item(Loc.T("隐藏该应用（保留记录）", "Hide app (keep entries)"), "clipboard-application-hide", () =>
            {
                removeLabel(anchor, () => hide(source, true));
                feedback(Loc.T("应用标签已隐藏；右键「全部历史」可恢复，记录仍可搜索。", "App label hidden. Right-click All history to restore it; entries remain searchable."));
            });
            menu.Items.Add(new Separator());
            Item(Loc.T("清空该应用的记录…", "Clear this app's entries…"), "clipboard-application-clear", () =>
            {
                int count = history.ApplicationCount(source);
                if (count == 0) return;
                string name = unknown ? Loc.T("未知应用", "Unknown app") : source;
                if (!confirm(new(PromptId.ClipboardClearApplication, Loc.T($"清空“{name}”的全部 {count} 条记录（包括收藏、归档和收藏夹中的记录）？此操作不可撤销。原文件、系统剪贴板和其他应用的记录不会更改。",
                    $"Clear all {count} entries from “{name}”, including favorites, archived entries and entries in collections? This cannot be undone. Original files, the system clipboard and other apps' entries are unchanged.")))) return;
                int removed = 0;
                removeLabel(anchor, () => removed = history.ClearApplication(source));
                feedback(Loc.T($"已清空该应用的 {removed} 条记录", $"Cleared {removed} entries from this app"));
            });
            return;
        }
        var hidden = preferences().HiddenApplications.ToArray();
        if (hidden.Length == 0) return;
        var restore = new MenuItem { Header = Loc.T("显示已隐藏的应用", "Show hidden apps"), Style = (Style)owner.FindResource("LibraryMenuItem") };
        AutomationProperties.SetAutomationId(restore, "clipboard-application-restore-menu"); menu.Items.Add(restore);
        restore.Loaded += (_, _) => { if (restore.Template?.FindName("PART_Popup", restore) is System.Windows.Controls.Primitives.Popup popup) PopupMotion.Attach(popup); };
        foreach (string value in hidden)
        {
            string name = value.Length == 0 ? Loc.T("未知应用", "Unknown app") : value;
            var item = new MenuItem { Header = name, Style = leaf };
            AutomationProperties.SetAutomationId(item, "clipboard-application-restore-" + value);
            item.Click += (_, e) => { menu.IsOpen = false; hide(value, false); e.Handled = true; };
            restore.Items.Add(item);
        }
        var all = new MenuItem { Header = Loc.T("全部显示", "Show all"), Style = leaf };
        AutomationProperties.SetAutomationId(all, "clipboard-application-restore-all");
        all.Click += (_, e) => { menu.IsOpen = false; foreach (string value in hidden) hide(value, false); e.Handled = true; };
        restore.Items.Add(new Separator()); restore.Items.Add(all);
    }
}
