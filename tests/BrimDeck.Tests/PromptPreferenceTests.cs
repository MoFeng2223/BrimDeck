using System.Text.Json;
using BrimDeck.Core;

internal static class PromptPreferenceTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        var settings = new DeckSettings();
        var request = new PromptRequest(PromptId.ClipboardDeleteEntry, "Synthetic risk");
        check("prompts: old settings ask by default without enabling recording", !settings.Prompts.IsSuppressed(request.Id) && !settings.Clipboard.Enabled);
        settings.Prompts.Remember(request, new(false, true));
        check("prompts: checking the option and canceling never authorizes an operation", settings.Prompts.Suppressed.Count == 0);
        settings.Prompts.Remember(request, new(true, false));
        check("prompts: accepting without remembering keeps future confirmation", settings.Prompts.Suppressed.Count == 0);
        settings.Prompts.Remember(request, new(true, true));
        check("prompts: an accepted remembered risk suppresses only its own type", settings.Prompts.IsSuppressed(request.Id) && !settings.Prompts.IsSuppressed(PromptId.ClipboardClearAll) && !settings.Prompts.IsSuppressed(PromptId.ShelfDeleteAction));
        var copy = settings.Copy(); copy.Prompts.Restore(request.Id);
        check("prompts: settings copies do not share authorization dictionaries", settings.Prompts.IsSuppressed(request.Id) && !copy.Prompts.IsSuppressed(request.Id));
        var combined = new PromptRequest(PromptId.ClipboardDiscardDraft, "Draft loss") { Additional = [new(PromptId.ShelfEndSession, "Shelf restore limits")] };
        settings.Prompts.Remember(combined, new(true, true));
        check("prompts: a combined decision remembers exactly its listed risks", settings.Prompts.IsSuppressed(PromptId.ClipboardDiscardDraft) && settings.Prompts.IsSuppressed(PromptId.ShelfEndSession) && !settings.Prompts.IsSuppressed(PromptId.ClipboardEndSession));
        settings.Prompts.Remember(new(PromptId.ClipboardSaveFailed, "Save failure"), new(true, true));
        check("prompts: fault preferences are distinct from destructive confirmation", PromptCatalog.Get(PromptId.ClipboardSaveFailed).Kind == PromptKind.Fault && settings.Prompts.IsSuppressed(PromptId.ClipboardSaveFailed) && !settings.Prompts.IsSuppressed(PromptId.ClipboardClearAll));
        string directory = Path.Combine(root, "prompt-preferences");
        var store = new SettingsStore(directory); store.Save(settings);
        var loaded = new SettingsStore(directory).Load();
        check("prompts: stable keys and versions survive a real settings reload", loaded.Prompts.IsSuppressed(request.Id) && loaded.Prompts.IsSuppressed(PromptId.ShelfEndSession));
        string language = Loc.Language;
        try { Loc.Use(Loc.English); check("prompts: translation changes never change authorization identity", loaded.Prompts.IsSuppressed(request.Id)); }
        finally { Loc.Use(language); }
        string key = PromptCatalog.Get(request.Id).Key;
        loaded.Prompts.Suppressed[key]++;
        check("prompts: a different risk version requires fresh authorization", !loaded.Prompts.IsSuppressed(request.Id));
        loaded.Prompts.Suppressed["unknown.future-risk"] = 1; loaded.Prompts.Normalize();
        check("prompts: malformed and unknown grants cannot disable a warning", !loaded.Prompts.Suppressed.ContainsKey(key) && !loaded.Prompts.Suppressed.ContainsKey("unknown.future-risk"));
        loaded.Prompts.Restore(PromptId.ShelfEndSession);
        check("prompts: restoring one type does not restore unrelated types", !loaded.Prompts.IsSuppressed(PromptId.ShelfEndSession) && loaded.Prompts.IsSuppressed(PromptId.ClipboardDiscardDraft));
        loaded.Prompts.Suppressed.Clear(); store.Save(loaded);
        check("prompts: restoring all warnings persists without changing module or capture consent", new SettingsStore(directory).Load().Prompts.Suppressed.Count == 0 && !loaded.Clipboard.Enabled && loaded.Clipboard.Mode == settings.Clipboard.Mode);
        var malformed = JsonSerializer.Deserialize<DeckSettings>("{\"Prompts\":{\"Suppressed\":null}}")!; malformed.Normalize();
        check("prompts: missing or null legacy preference fields normalize to asking", malformed.Prompts.Suppressed.Count == 0);
        check("prompts: every business risk has a distinct stable key and positive version", PromptCatalog.All.Select(item => item.Key).Distinct().Count() == Enum.GetValues<PromptId>().Length && PromptCatalog.All.All(item => item.Version > 0));
    }
}
