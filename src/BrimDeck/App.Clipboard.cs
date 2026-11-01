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
    private ClipboardPasteTarget _clipboardTarget, _libraryPasteTarget;
    private IntPtr _clipboardFocusTarget;
    private readonly DispatcherTimer _clipboardPasteWait = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private ClipboardPasteSession? _pasteSession;
    private Window? _pasteOwner;
    private long _pasteRequest, _pasteStarted;

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
    private bool ActivatePreviewEntry(ClipboardEntry entry, bool copyOnly, ClipboardPasteTarget target)
    {
        CancelClipboardPaste();
        if (ModulesChanging || _clipboard is null || !_clipboard.Copy(entry)) return false;
        if (!copyOnly)
        {
            // The floating window keeps its own receiver; another island recall cannot overwrite this target.
            BeginClipboardPaste(target);
        }
        return true;
    }
    public void OpenClipboardLibrary() => OpenClipboardLibraryFor(null);
    internal void OpenClipboardLibraryFor(string? entryId)
    {
        if (Exiting || ModulesChanging) return;
        if (!ClipboardRunning || _clipboard is not { } service) { OpenClipboardSettings(); return; }
        CancelClipboardPaste();
        if (ClipboardPaste.IsExternalWindow(ClipboardPaste.Foreground) || _clipboardLibrary?.IsVisible != true)
        { CaptureClipboardTarget(); _libraryPasteTarget = _clipboardTarget; }
        if (_clipboardLibrary is null)
        {
            var library = _clipboardLibrary = new ClipboardLibraryWindow(service.History, ActivateLibraryEntry,
                () => (Settings.Clipboard.Enabled, service.Paused, service.Status),
                () => { if (!Settings.Clipboard.Enabled) EnableClipboard(); else service.TogglePause(); }, OpenClipboardSettings, preferences: () => Settings.Clipboard,
                setApplicationPinned: SetClipboardApplicationPinned, setApplicationHidden: SetClipboardApplicationHidden);
            library.Closed += (_, _) => { if (_clipboardLibrary == library) _clipboardLibrary = null; };
            // Remember the last external focus on a real switch away, not a stale target from a previous island recall.
            library.Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            { if (!Exiting && library.IsVisible && ClipboardPaste.IsExternalWindow(ClipboardPaste.Foreground)) _libraryPasteTarget = ClipboardPaste.Capture(ClipboardPaste.Foreground); });
        }
        if (_clipboardLibrary.WindowState == WindowState.Minimized) _clipboardLibrary.WindowState = WindowState.Normal;
        service.OpenedOrResumed();
        _clipboardLibrary.Show(); _clipboardLibrary.Activate(); _clipboardLibrary.FocusEntry(entryId);
    }
    private void ActivateLibraryEntry(ClipboardEntry item, bool copyOnly)
    {
        CancelClipboardPaste();
        if (ModulesChanging || _clipboard is null) return;
        if (!_clipboard.Copy(item)) { ReportFeedback("clipboard-copy-failed", _clipboard.Status, _clipboardLibrary); return; }
        if (copyOnly) { ReportFeedback("clipboard-copied", Loc.T("已复制", "Copied"), _clipboardLibrary); return; }
        var target = _libraryPasteTarget;
        _clipboardLibrary?.Hide(); BeginClipboardPaste(target);
    }
    private void CancelClipboardPaste()
    {
        _clipboardPasteWait.Stop(); _pasteSession?.Cancel(); _pasteSession = null; _pasteOwner = null;
        ++_pasteRequest;
    }
    private void BeginClipboardPaste(ClipboardPasteTarget target, Window? owner = null)
    {
        _pasteStarted = ClipboardPaste.Platform.Now; _pasteOwner = owner;
        _pasteSession = new(target, ClipboardPaste.Sequence, _pasteStarted);
        _clipboardPasteWait.Start();
        // Request activation in the initiating user action rather than waiting until that activation grant is stale.
        PasteClipboardWhenReady(null, EventArgs.Empty);
    }

    public void ToggleClipboard()
    {
        if (Exiting || ModulesChanging) return;
        if (!ClipboardRunning || _clipboard is null) { OpenClipboardSettings(); return; }
        CancelClipboardPaste();
        if (Decks.FirstOrDefault(deck => deck.IsOpenForHotkey && deck.IsClipboardPage) is { } open) { open.CollapseFromHotkey(); return; }
        CaptureClipboardTarget(); _clipboard.OpenedOrResumed();
        // Showing the page does not enable recording. A page hidden in appearance settings is made visible on explicit recall.
        if (!Settings.ClipboardPage) { var settings = Settings.Copy(); settings.ClipboardPage = true; UpdateSettings(settings); }
        var monitor = GlobalHotkeys.CursorMonitor();
        (Decks.FirstOrDefault(deck => deck.HotkeyMonitor == monitor) ?? Deck).OpenClipboardPage();
    }
    internal void CaptureClipboardTarget(bool preserveInternal = false)
    {
        var previous = ClipboardPaste.Foreground;
        // Focus can return to our settings window, but pasted content may only go to an external application.
        // A tray/settings click must never reuse a stale paste target from an earlier recall.
        if (ClipboardPaste.IsExternalWindow(previous))
        { _clipboardFocusTarget = previous; _clipboardTarget = ClipboardPaste.Capture(previous); }
        else if (!preserveInternal) { _clipboardTarget = default; _clipboardFocusTarget = previous; }
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
        CancelClipboardPaste();
        if (ModulesChanging || _clipboard is null) return;
        // Browsing the non-activating island leaves the external input focused. Copy-only never changes the paste receiver.
        if (!copyOnly && ClipboardPaste.IsExternalWindow(ClipboardPaste.Foreground)) CaptureClipboardTarget();
        var target = _clipboardTarget;
        if (!_clipboard.Copy(item)) { ReportFeedback("clipboard-copy-failed", _clipboard.Status, deck); return; }
        if (!copyOnly || Settings.Clipboard.CloseAfterAction) deck.CollapseFromHotkey();
        if (copyOnly) { ReportFeedback("clipboard-copied", Loc.T("已复制", "Copied"), deck); return; }
        BeginClipboardPaste(target, deck);
    }
    private void PasteClipboardWhenReady(object? sender, EventArgs args)
    {
        if (Exiting || ModulesChanging) { CancelClipboardPaste(); return; }
        if (_pasteSession is not { } session) { _clipboardPasteWait.Stop(); return; }
        var result = session.Advance(ClipboardPaste.Platform);
        if (result == ClipboardPasteResult.Waiting) return;
        _clipboardPasteWait.Stop(); _pasteSession = null;
        var owner = _pasteOwner; _pasteOwner = null;
        System.Diagnostics.Trace.WriteLine($"Clipboard paste request={_pasteRequest} phase={result} target={session.Target.Window} pid={session.Target.Process} elapsed={ClipboardPaste.Platform.Now - _pasteStarted} sent={session.Input.Sent} error={session.Input.Error}");
        if (result is ClipboardPasteResult.Submitted or ClipboardPasteResult.Cancelled or ClipboardPasteResult.TargetChanged or ClipboardPasteResult.ClipboardChanged) return;
        string detail = result switch
        {
            ClipboardPasteResult.NoTarget => Loc.T("未选择接收窗口", "No target window"),
            ClipboardPasteResult.TargetUnavailable => Loc.T("原窗口已关闭或不可用", "The original window is unavailable"),
            ClipboardPasteResult.ModifiersTimeout => Loc.T("快捷键尚未释放", "Shortcut keys are still held"),
            ClipboardPasteResult.FocusTimeout => Loc.T("目标输入焦点未就绪", "Target input focus is not ready"),
            _ => Loc.T("目标程序未接受模拟按键", "The target did not accept simulated input")
        };
        ReportFeedback("clipboard-paste-unavailable", Loc.T("已复制，可按 Ctrl+V 粘贴", "Copied; press Ctrl+V to paste") + " · " + detail, owner);
    }
    private void DisposeClipboard()
    {
        CancelClipboardPaste(); _clipboardPasteWait.Tick -= PasteClipboardWhenReady;
        _clipboardPreviews?.Dispose(); _clipboardPreviews = null;
        _clipboardLibrary?.ForceClose(); _clipboardLibrary = null;
        if (_clipboard is not null) { _clipboard.StatusChanged -= ClipboardStatusChanged; _clipboard.Dispose(); _clipboard = null; }
    }
}
