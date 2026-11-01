namespace BrimDeck.Core;

public enum PromptId
{
    ClipboardDeleteEntry, ClipboardDeleteCollection, ClipboardClearHistory, ClipboardClearAll, ClipboardClearApplication,
    ClipboardDiscardDraft, ShelfDeleteAction, ShelfDiscardDraft, ClipboardEndSession, ShelfEndSession,
    ClipboardSaveFailed, ShelfSaveFailed, ModuleChangeFailed, ShelfUninstallFailed
}
public enum PromptKind { Confirmation, Fault, Information }

// Risk versions belong to the operation contract, not the translated wording or the individual content being deleted.
public sealed record PromptDefinition(PromptId Id, string Key, int Version, PromptKind Kind, string Title, string Action);
public static class PromptCatalog
{
    public static PromptDefinition Get(PromptId id) => id switch
    {
        PromptId.ClipboardDeleteEntry => Define(id, "clipboard.delete-entry", Loc.T("删除剪贴板记录", "Delete clipboard entry"), Loc.T("删除记录", "Delete entry")),
        PromptId.ClipboardDeleteCollection => Define(id, "clipboard.delete-collection", Loc.T("删除分类", "Delete collection"), Loc.T("删除分类", "Delete collection")),
        PromptId.ClipboardClearHistory => Define(id, "clipboard.clear-history", Loc.T("清空普通历史", "Clear unprotected history"), Loc.T("清空历史", "Clear history")),
        PromptId.ClipboardClearAll => Define(id, "clipboard.clear-all", Loc.T("删除全部历史及收藏", "Delete all history and favorites"), Loc.T("删除全部", "Delete all")),
        PromptId.ClipboardClearApplication => Define(id, "clipboard.clear-application", Loc.T("清空应用记录", "Clear an app's entries"), Loc.T("清空应用记录", "Clear app entries")),
        PromptId.ClipboardDiscardDraft => Define(id, "clipboard.discard-draft", Loc.T("放弃剪贴板草稿", "Discard clipboard drafts"), Loc.T("放弃修改", "Discard changes")),
        PromptId.ShelfDeleteAction => Define(id, "shelf.delete-action", Loc.T("删除快速操作", "Delete quick action"), Loc.T("删除操作", "Delete action")),
        PromptId.ShelfDiscardDraft => Define(id, "shelf.discard-draft", Loc.T("放弃快速操作编辑", "Discard quick-action edits"), Loc.T("放弃修改", "Discard changes")),
        PromptId.ClipboardEndSession => Define(id, "clipboard.end-session", Loc.T("结束未持久化的剪贴板会话", "End an unsaved clipboard session"), Loc.T("结束会话", "End session")),
        PromptId.ShelfEndSession => Define(id, "shelf.end-session", Loc.T("结束置物架会话", "End shelf sessions"), Loc.T("结束会话", "End sessions")),
        PromptId.ClipboardSaveFailed => Define(id, "clipboard.save-failed", Loc.T("剪贴板未能保存", "Clipboard could not be saved"), Loc.T("知道了", "Got it"), PromptKind.Fault),
        PromptId.ShelfSaveFailed => Define(id, "shelf.save-failed", Loc.T("置物架未能保存", "Shelves could not be saved"), Loc.T("知道了", "Got it"), PromptKind.Fault),
        PromptId.ModuleChangeFailed => Define(id, "module.change-failed", Loc.T("模块切换未完成", "Module change did not complete"), Loc.T("知道了", "Got it"), PromptKind.Information),
        PromptId.ShelfUninstallFailed => Define(id, "shelf.uninstall-failed", Loc.T("卸载程序未能启动", "Uninstaller could not start"), Loc.T("知道了", "Got it"), PromptKind.Information),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };
    public static IEnumerable<PromptDefinition> All => Enum.GetValues<PromptId>().Select(Get);
    private static PromptDefinition Define(PromptId id, string key, string title, string action, PromptKind kind = PromptKind.Confirmation)
        => new(id, key, 1, kind, title, action);
}
public sealed record PromptRequest(PromptId Id, string Message)
{
    public IReadOnlyList<PromptRequest> Additional { get; init; } = [];
    public IEnumerable<PromptRequest> Parts => new[] { this }.Concat(Additional);
}
public readonly record struct PromptAnswer(bool Accepted, bool DoNotShowAgain);

public sealed class PromptPreferences
{
    public Dictionary<string, int> Suppressed { get; set; } = new(StringComparer.Ordinal);
    public bool IsSuppressed(PromptId id)
    {
        var definition = PromptCatalog.Get(id);
        return Suppressed.TryGetValue(definition.Key, out int version) && version == definition.Version;
    }
    public void Remember(PromptRequest request, PromptAnswer answer)
    {
        if (!answer.Accepted || !answer.DoNotShowAgain) return;
        foreach (var part in request.Parts)
        {
            var definition = PromptCatalog.Get(part.Id);
            Suppressed[definition.Key] = definition.Version;
        }
    }
    public void Restore(PromptId id) => Suppressed.Remove(PromptCatalog.Get(id).Key);
    public PromptPreferences Copy() => new() { Suppressed = new(Suppressed ?? [], StringComparer.Ordinal) };
    public void Normalize()
    {
        var known = PromptCatalog.All.ToDictionary(definition => definition.Key, StringComparer.Ordinal);
        Suppressed = (Suppressed ?? []).Where(pair => known.TryGetValue(pair.Key, out var definition) && pair.Value == definition.Version)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }
}
