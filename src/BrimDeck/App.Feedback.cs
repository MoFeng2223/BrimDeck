using System.IO;
using System.Windows;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    internal string ModuleProblem { get; private set; } = "";
    private void InitializeFeedback() => UserPrompts.Configure(() => Settings.Prompts, SavePromptPreferences, () =>
    {
        ModuleProblem = Loc.T("确认窗口暂不可用，操作已取消；未跳过保存检查。请重新打开设置后重试。", "The confirmation surface is unavailable. The action was canceled; save checks were not bypassed. Reopen settings and retry.");
        _settingsWindow?.SaveStatus(ModuleProblem, true);
        ReportFeedback("prompt-unavailable", ModuleProblem);
    });
    private bool SavePromptPreferences(PromptPreferences preferences)
    {
        // Commit in the ordinary writer's order, but do not grant a remembered confirmation until it reached disk.
        _save.Stop();
        var next = Settings.Copy(); next.Prompts = preferences.Copy();
        string text = Store.Serialize(next);
        var write = _writing.ContinueWith(_ => Store.Write(text), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _writing = write;
        try { write.GetAwaiter().GetResult(); Settings.Prompts = preferences; return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ModuleProblem = Loc.T("提示偏好未保存，下次仍会询问。请检查设置目录权限。", "Prompt preference was not saved. You will be asked again; check settings permissions.");
            _settingsWindow?.SaveStatus(ModuleProblem, true);
            ReportFeedback("prompt-save-failed", ModuleProblem);
            return false;
        }
    }
    internal void RestorePrompt(PromptId? id = null)
    {
        var next = Settings.Prompts.Copy();
        if (id is { } prompt) next.Restore(prompt); else next.Suppressed.Clear();
        // Restoring a warning is safe immediately, even if persisting the preference fails.
        Settings.Prompts = next; SavePromptPreferences(next);
    }
    internal bool ConfirmPrompt(Window? owner, PromptRequest request) => UserPrompts.Show(owner, request);
    internal void ReportFeedback(string key, string message, Window? owner = null)
    {
        if (Exiting || string.IsNullOrWhiteSpace(message)) return;
        if (owner is ClipboardLibraryWindow { IsVisible: true } library) { library.ShowFeedback(message); return; }
        if (owner is ClipboardPreviewWindow { IsVisible: true, IsClosing: false } preview) { preview.ShowFeedback(message); return; }
        var deck = owner as MainWindow ?? (Decks.FirstOrDefault(panel => panel is not null && panel.IsVisible && panel.IsExpanded) ?? Deck);
        deck?.ShowModuleFeedback(key, message);
    }
}
