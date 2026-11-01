using System.IO;
using System.Windows;
using BrimDeck.Core;
using DropShelf;
using DropShelf.Services;

namespace BrimDeck;

public partial class App
{
    private string _moduleDirectory = "";
    private bool _modulesChanging;
    internal bool ModulesChanging => _modulesChanging;
    internal bool ClipboardRunning => Settings.Clipboard.Mode != ModuleMode.Off && _clipboard is not null;
    internal bool ShelfRunning => Settings.Tray.Mode != ModuleMode.Off && Shelves is not null;
    private IDisposable? _clipboardImages, _shelfImages;

    private void InitializeSharedModules(string folder)
    {
        _moduleDirectory = folder;
        InitializeFeedback();
        HostedResources.ConfigureHost(() => Settings.Animations && Settings.Tray.Mode == ModuleMode.Full,
            Path.Combine(Path.GetTempPath(), "BrimDeck", "Shelves", "Attachments"));
        InitializeShelfTheme();
    }
    private bool ConfirmModuleClose(bool clipboard, bool shelf)
    {
        var risks = new List<PromptRequest>();
        if (clipboard)
        {
            if (_clipboardLibrary?.HasUnsavedChanges == true || Decks.Any(deck => deck?.ClipboardHasDraft == true))
                risks.Add(new(PromptId.ClipboardDiscardDraft, Loc.T("结束会话将放弃尚未保存的剪贴板编辑草稿。", "Ending the session discards unsaved clipboard drafts.")));
            if (_clipboard is { Loaded: true } transient && !Settings.Clipboard.PersistHistory && transient.History.Items.Count > 0)
                risks.Add(new(PromptId.ClipboardEndSession, Loc.T("持久化存储未开启，本次会话历史将丢失；系统剪贴板与原文件不变。", "Persistence is off. Session history will be lost; the system clipboard and original files are unchanged.")));
        }
        if (shelf)
        {
            if (_settingsWindow?.ShelfHasUnsavedDraft == true)
                risks.Add(new(PromptId.ShelfDiscardDraft, Loc.T("结束会话将放弃尚未保存的快速操作编辑。", "Ending the session discards unsaved quick-action edits.")));
            if (Shelves?.Shelves.OpenShelves.Any(window => window.ViewModel.HasItems) == true)
                risks.Add(new(PromptId.ShelfEndSession, Loc.T("全部置物架会话将结束。当前架的普通文件路径会保存，最近关闭最多保留 10 个架、每架 500 项；文本、链接、临时附件及其他架的固定/加入时间不能完整恢复，原文件不会删除。", "All shelf sessions will end. Current ordinary paths are saved; recent history holds up to 10 shelves of 500 items. Text, links, temporary attachments and other shelves' pin/time metadata cannot be fully restored. Original files are not deleted.")));
        }
        return risks.Count == 0 || ConfirmPrompt(_settingsWindow is { IsVisible: true } ? _settingsWindow : Decks.FirstOrDefault(deck => deck?.IsVisible == true),
            risks[0] with { Additional = risks.Skip(1).ToArray() });
    }

    internal async Task<bool> ChangeModuleModeAsync(bool clipboard, ModuleMode mode)
    {
        if (_modulesChanging || _exitPreparing || Exiting || !Enum.IsDefined(mode)) return false;
        var previous = clipboard ? Settings.Clipboard.Mode : Settings.Tray.Mode;
        if (mode == previous) return true;
        _modulesChanging = true;
        try
        {
            if (mode == ModuleMode.Off)
            {
                if (clipboard)
                {
                    if (_clipboard?.ChangingStorage == true) return false;
                    if (!ConfirmModuleClose(true, false)) return false;
                    CancelClipboardPaste();
                    _clipboard?.SuspendCapture();
                    foreach (var deck in Decks) deck?.PrepareClipboardStop();
                    if (_clipboard is { Loaded: true } dragging) await ClipboardDragDrop.SuspendAsync(dragging.History);
                    if (_clipboard is { } service && !await service.FlushAsync())
                    { ClipboardDragDrop.Resume(service.History); service.ResumeCapture(); ModuleFailure(service.Status, PromptId.ClipboardSaveFailed); return false; }
                    foreach (var deck in Decks) deck?.ReleaseClipboardView();
                    DisposeClipboard(); DecodedImageCache.RemovePrefix("clipboard:"); _clipboardImages?.Dispose(); _clipboardImages = null;
                }
                else if (Shelves is { } controller)
                {
                    controller.Accepting = false;
                    foreach (var deck in Decks) deck?.PrepareShelfStop();
                    // Each lease ends after its payload/output has been applied. No timer waits for IsBusy.
                    await controller.Shelves.Operations.WhenIdle;
                    if (!ConfirmModuleClose(false, true))
                    { controller.Accepting = true; foreach (var deck in Decks) deck?.ResumeShelfInput(); return false; }
                    if (!await SaveShelfBeforeStopAsync())
                    { controller.Accepting = true; foreach (var deck in Decks) deck?.ResumeShelfInput(); return false; }
                    _settingsWindow?.ReleaseShelfSettings();
                    foreach (var deck in Decks) deck?.ReleaseShelfView();
                    DisposeShelves(); _shelfImages?.Dispose(); _shelfImages = null;
                }
            }
            var next = Settings.Copy();
            if (clipboard) next.Clipboard.Mode = mode; else next.Tray.Mode = mode;
            UpdateSettings(next);
            if (mode != ModuleMode.Off)
            {
                if (clipboard && _clipboard is null) InitializeClipboard(_moduleDirectory);
                else if (!clipboard && Shelves is null) InitializeShelves(_moduleDirectory);
            }
            foreach (var deck in Decks) if (deck is not null) { deck.ResumeShelfInput(); deck.ApplyModulePresentation(); deck.ApplySettings(); }
            _clipboardPreviews?.Refresh(); _clipboardLibrary?.Refresh();
            ModuleProblem = "";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Initialization can fail after settings are applied; never leave the selected mode claiming a runtime exists.
            if ((clipboard ? Settings.Clipboard.Mode : Settings.Tray.Mode) != previous)
            {
                var rollback = Settings.Copy();
                if (clipboard) rollback.Clipboard.Mode = previous; else rollback.Tray.Mode = previous;
                if (previous == ModuleMode.Off)
                {
                    if (clipboard) { DisposeClipboard(); _clipboardImages?.Dispose(); _clipboardImages = null; }
                    else { DisposeShelves(); _shelfImages?.Dispose(); _shelfImages = null; }
                }
                UpdateSettings(rollback);
                if (previous != ModuleMode.Off)
                {
                    if (clipboard && _clipboard is null) InitializeClipboard(_moduleDirectory);
                    else if (!clipboard && Shelves is null) InitializeShelves(_moduleDirectory);
                }
            }
            if (Shelves is { } controller) controller.Accepting = true;
            if (_clipboard is { Loaded: true } resumed) ClipboardDragDrop.Resume(resumed.History);
            _clipboard?.ResumeCapture();
            foreach (var deck in Decks) deck?.ResumeShelfInput();
            ModuleFailure(Loc.T("模块切换失败；未清空原数据。请检查本地存储权限。", "Mode change failed; original data was not cleared. Check local storage permissions."));
            return false;
        }
        finally { _modulesChanging = false; }
    }
    private async Task<bool> PrepareModuleExitAsync()
    {
        _modulesChanging = true;
        bool prepared = false;
        try
        {
            CancelClipboardPaste(); _clipboard?.SuspendCapture();
            if (_clipboard is { Loaded: true } dragging) await ClipboardDragDrop.SuspendAsync(dragging.History);
            if (Shelves is { } controller)
            {
                controller.Accepting = false;
                foreach (var deck in Decks) deck.PrepareShelfStop();
                await controller.Shelves.Operations.WhenIdle;
            }
            if (!ConfirmModuleClose(true, true)) return false;
            if (_clipboard is { } service && !await service.FlushAsync()) { ModuleFailure(service.Status, PromptId.ClipboardSaveFailed); return false; }
            if (!await SaveShelfBeforeStopAsync()) return false;
            prepared = true; return true;
        }
        finally
        {
            _modulesChanging = false;
            // A failed barrier leaves the existing session live and retryable.
            if (!prepared)
            {
                if (_clipboard is { Loaded: true } resumed) ClipboardDragDrop.Resume(resumed.History);
                _clipboard?.ResumeCapture();
                if (Shelves is { } controller) controller.Accepting = true;
                foreach (var deck in Decks) deck.ResumeShelfInput();
            }
        }
    }
    private void ModuleFailure(string text, PromptId id = PromptId.ModuleChangeFailed)
    {
        ModuleProblem = text;
        _settingsWindow?.SaveStatus(text, true);
        ReportFeedback("module-save-failed", text);
        // Suppression controls presentation only. Every caller still returns failure and retains the live session.
        ConfirmPrompt(_settingsWindow, new(id, text));
    }

    private async Task<bool> SaveShelfBeforeStopAsync()
    {
        if (Shelves is not { } controller) return true;
        var snapshot = IslandShelfSnapshot();
        if (_trayStore is not null && !await Task.Run(() => _trayStore.TrySave(snapshot)))
        { ModuleFailure(Loc.T("文件路径保存失败，置物架仍保持开启。", "Could not save file paths. The shelf remains running."), PromptId.ShelfSaveFailed); return false; }
        foreach (var shelf in controller.Shelves.OpenShelves) controller.History.Record(shelf.ViewModel.Items);
        if (!await controller.FlushAsync())
        { ModuleFailure(Loc.T("置物架设置或历史保存失败，内容仍保留在当前会话。", "Shelf settings/history could not be saved. Content remains in this session."), PromptId.ShelfSaveFailed); return false; }
        controller.ShutdownPrepared = true;
        return true;
    }
}
