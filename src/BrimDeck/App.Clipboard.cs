using System.Windows;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;
using DropShelf;

namespace BrimDeck;

public partial class App
{
    private ClipboardService? _clipboard;
    private ClipboardLibraryWindow? _clipboardLibrary;
    private ClipboardPreviewWindows? _clipboardPreviews;
    private IntPtr _clipboardTarget, _clipboardFocusTarget;
    private readonly DispatcherTimer _clipboardPasteWait = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private int _clipboardPasteTicks;
    private bool _pasteRestored;

    internal ClipboardService? ClipboardHistoryService => _clipboard;
    internal void InitializeClipboard(string folder)
    {
        if (Settings.Clipboard.Mode == ModuleMode.Off || _clipboard is not null) return;
        _clipboardImages ??= HostedResources.AcquireImaging();
        _clipboard = new ClipboardService(folder, () => Settings.Clipboard, Dispatcher);
        _clipboard.StatusChanged += ClipboardStatusChanged;
        _clipboardPasteWait.Tick -= PasteClipboardWhenReady; _clipboardPasteWait.Tick += PasteClipboardWhenReady;
    }
    private void ClipboardStatusChanged()
    {
        _clipboardPreviews?.RefreshText();
        foreach (var deck in Decks) deck.ClipboardChanged();
        _clipboardLibrary?.RefreshStatus(); _clipboardLibrary?.RefreshTheme();
    }
    internal void OpenClipboardPreview(ClipboardEntry entry, Rect card, Rect panel, double scale)
    {
        if (Exiting || ModulesChanging) return;
        if (!ClipboardRunning || _clipboard is not { } service) { OpenClipboardSettings(); return; }
        _clipboardPreviews ??= new ClipboardPreviewWindows(service.History, () => Settings.Clipboard, ActivatePreviewEntry, () =>
        {
            if (ClipboardPaste.IsExternalWindow(ClipboardPaste.Foreground)) CaptureClipboardTarget();
            return _clipboardTarget;
        });
        service.OpenedOrResumed();
        _clipboardPreviews.Show(entry, card, panel, scale);
    }
    private bool ActivatePreviewEntry(ClipboardEntry entry, bool copyOnly, IntPtr target)
    {
        _clipboardPasteWait.Stop();
        if (ModulesChanging || _clipboard is null || !_clipboard.Copy(entry)) return false;
        if (!copyOnly)
        {
            // The floating window keeps its own receiver; another island recall cannot overwrite this target.
            _clipboardTarget = target; BeginClipboardPaste();
        }
        return true;
    }
    public void OpenClipboardLibrary() => OpenClipboardLibraryFor(null);
    internal void OpenClipboardLibraryFor(string? entryId)
    {
        if (Exiting || ModulesChanging) return;
        if (!ClipboardRunning || _clipboard is not { } service) { OpenClipboardSettings(); return; }
        _clipboardPasteWait.Stop();
        if (ClipboardPaste.IsExternalWindow(ClipboardPaste.Foreground) || _clipboardLibrary?.IsVisible != true) CaptureClipboardTarget();
        if (_clipboardLibrary is null)
        {
            var library = _clipboardLibrary = new ClipboardLibraryWindow(service.History, ActivateLibraryEntry,
                () => (Settings.Clipboard.Enabled, service.Paused, service.Status),
                () => { if (!Settings.Clipboard.Enabled) EnableClipboard(); else service.TogglePause(); }, OpenClipboardSettings, preferences: () => Settings.Clipboard,
                setApplicationPinned: SetClipboardApplicationPinned, setApplicationHidden: SetClipboardApplicationHidden);
            library.Closed += (_, _) => { if (_clipboardLibrary == library) _clipboardLibrary = null; };
            // Remember the last external focus on a real switch away, not a stale target from a previous island recall.
            library.Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            { if (!Exiting && library.IsVisible && ClipboardPaste.IsExternalWindow(ClipboardPaste.Foreground)) CaptureClipboardTarget(); });
        }
        if (_clipboardLibrary.WindowState == WindowState.Minimized) _clipboardLibrary.WindowState = WindowState.Normal;
        service.OpenedOrResumed();
        _clipboardLibrary.Show(); _clipboardLibrary.Activate(); _clipboardLibrary.FocusEntry(entryId);
    }
    private void ActivateLibraryEntry(ClipboardEntry item, bool copyOnly)
    {
        if (ModulesChanging || _clipboard is null || !_clipboard.Copy(item)) return;
        if (copyOnly) return;
        _clipboardLibrary?.Hide(); BeginClipboardPaste();
    }
    private void BeginClipboardPaste()
    {
        _pasteRestored = false; _clipboardPasteTicks = 0; _clipboardPasteWait.Start();
    }

    public void ToggleClipboard()
    {
        if (Exiting || ModulesChanging) return;
        if (!ClipboardRunning || _clipboard is null) { OpenClipboardSettings(); return; }
        _clipboardPasteWait.Stop();
        if (Decks.FirstOrDefault(deck => deck.IsOpenForHotkey && deck.IsClipboardPage) is { } open) { open.CollapseFromHotkey(); return; }
        CaptureClipboardTarget(); _clipboard.OpenedOrResumed();
        // Showing the page does not enable recording. A page hidden in appearance settings is made visible on explicit recall.
        if (!Settings.ClipboardPage) { var settings = Settings.Copy(); settings.ClipboardPage = true; UpdateSettings(settings); }
        var monitor = GlobalHotkeys.CursorMonitor();
        (Decks.FirstOrDefault(deck => deck.HotkeyMonitor == monitor) ?? Deck).OpenClipboardPage();
    }
    internal void CaptureClipboardTarget()
    {
        var previous = ClipboardPaste.Foreground;
        // Focus can return to our settings window, but pasted content may only go to an external application.
        // A tray/settings click must never reuse a stale paste target from an earlier recall.
        _clipboardFocusTarget = previous;
        _clipboardTarget = ClipboardPaste.IsExternalWindow(previous) ? previous : IntPtr.Zero;
    }
    internal void RestoreClipboardFocus() => ClipboardPaste.RestoreFocus(_clipboardFocusTarget);
    internal void EnableClipboard() => EditClipboardSettings(settings => settings.Enabled = true);
    internal void EditClipboardSettings(Action<ClipboardSettings> edit)
    {
        var settings = Settings.Copy(); edit(settings.Clipboard); UpdateSettings(settings);
    }
    internal void SetClipboardPreviewSensitivity(int value, bool save)
    {
        // A preview-only preference must not reapply capture services, hotkeys and the whole island on every drag frame.
        Settings.Clipboard.PreviewSensitivity = Math.Clamp(value, 0, 100);
        foreach (var deck in Decks) deck.ClipboardPreviewSensitivityChanged();
        if (save) QueueSave();
    }
    internal void SetClipboardApplicationPinned(string source, bool pinned)
    {
        if (!Settings.Clipboard.SetApplicationPinned(source, pinned)) return;
        foreach (var deck in Decks) deck.ClipboardChanged();
        _clipboardLibrary?.Refresh(); QueueSave();
    }
    internal void SetClipboardApplicationHidden(string source, bool hidden)
    {
        if (!Settings.Clipboard.SetApplicationHidden(source, hidden)) return;
        foreach (var deck in Decks) deck.ClipboardChanged();
        _clipboardLibrary?.Refresh(); QueueSave();
    }
    internal async Task<(bool Succeeded, string Message)> SetClipboardStorageDirectoryAsync(string directory)
    {
        if (_clipboard is null || Exiting) return (false, Loc.T("剪贴板服务尚未就绪。", "The clipboard service is not ready."));
        return await _clipboard.ChangeStorageDirectoryAsync(directory, async preference =>
        {
            _save.Stop();
            Task pending;
            do { pending = _writing; await pending; } while (pending != _writing);
            if (Exiting) throw new System.IO.IOException("Application is exiting.");
            _save.Stop();
            var settings = Settings.Copy(); settings.Clipboard.StorageDirectory = preference;
            // One explicit path change commits synchronously after the normal queue drains, so a crash cannot leave a
            // saved pointer to a deleted source. Ordinary settings and capture writes retain their background queues.
            Store.Write(Store.Serialize(settings));
            Settings.Clipboard.StorageDirectory = preference;
        });
    }
    internal void OpenClipboardSettings()
    {
        OpenSettings(); _settingsWindow?.ShowPage(SettingsWindow.ClipboardPage);
    }
    private void ApplyClipboardSettings()
    {
        _clipboard?.ApplySettings();
        ClipboardStatusChanged();
    }
    internal void ActivateClipboardEntry(MainWindow deck, ClipboardEntry item, bool copyOnly)
    {
        if (ModulesChanging || _clipboard is null) return;
        // A non-activating mouse browse still has the external application in the foreground.
        var foreground = ClipboardPaste.Foreground;
        if (ClipboardPaste.IsExternalWindow(foreground)) _clipboardTarget = foreground;
        if (!_clipboard.Copy(item)) return;
        bool paste = !copyOnly;
        if (paste || Settings.Clipboard.CloseAfterAction) deck.CollapseFromHotkey();
        if (!paste) return;
        BeginClipboardPaste();
    }
    private void PasteClipboardWhenReady(object? sender, EventArgs args)
    {
        if (++_clipboardPasteTicks > 30 || Exiting) { _clipboardPasteWait.Stop(); PasteClipboardFailed(); return; }
        if (!ClipboardPaste.ModifiersReleased) return;
        if (!_pasteRestored)
        {
            _pasteRestored = true;
            if (!ClipboardPaste.Restore(_clipboardTarget)) { _clipboardPasteWait.Stop(); PasteClipboardFailed(); }
            return;
        }
        _clipboardPasteWait.Stop();
        if (!ClipboardPaste.Send(_clipboardTarget)) PasteClipboardFailed();
    }
    private void PasteClipboardFailed()
    {
        if (Exiting || ModulesChanging) return;
        // UIPI and focus restrictions are real boundaries: retain the copied payload and explain the manual Ctrl+V path.
        MessageBox.Show(Loc.T("内容已复制，但无法向原窗口自动粘贴。请切回目标程序按 Ctrl+V（管理员窗口可能会阻止模拟按键）。",
            "The content was copied, but automatic paste into the original window was blocked. Switch to your target app and press Ctrl+V (elevated windows may block simulated input)."),
            Loc.T("剪贴板", "Clipboard"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void DisposeClipboard()
    {
        _clipboardPasteWait.Stop(); _clipboardPasteWait.Tick -= PasteClipboardWhenReady;
        _clipboardPreviews?.Dispose(); _clipboardPreviews = null;
        _clipboardLibrary?.ForceClose(); _clipboardLibrary = null;
        if (_clipboard is not null) { _clipboard.StatusChanged -= ClipboardStatusChanged; _clipboard.Dispose(); _clipboard = null; }
    }
}
