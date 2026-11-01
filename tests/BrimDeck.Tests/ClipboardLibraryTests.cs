using System.Text.Json;
using BrimDeck.Core;

internal static class ClipboardLibraryTests
{
    internal static void Run(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var settings = new ClipboardSettings { MaximumItems = 100, RetentionDays = 0 };
        check("Library types: persisted legacy kind numbers stay compatible", (int)ClipboardContentKind.Text == 0 && (int)ClipboardContentKind.Link == 1 && (int)ClipboardContentKind.Color == 2 && (int)ClipboardContentKind.Image == 3 && (int)ClipboardContentKind.Files == 4);
        foreach (var (text, kind) in new (string, ClipboardContentKind)[]
        {
            ("{\"status\":200,\"items\":[1,2]}", ClipboardContentKind.Code), ("git status --short", ClipboardContentKind.Code),
            ("const value = 42;", ClipboardContentKind.Code), ("def main():\n    return 1", ClipboardContentKind.Code),
            ("ERROR database unavailable", ClipboardContentKind.Code), ("https://github.com/example/project", ClipboardContentKind.Link),
            ("rgb(10, 20, 30)", ClipboardContentKind.Color), ("rgba(10, 20, 30, 0.5)", ClipboardContentKind.Color),
            ("linear-gradient(90deg, #0080FF 0%, rgb(255, 0, 0) 100%)", ClipboardContentKind.Color),
            ("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M0 0L24 24\"/></svg>", ClipboardContentKind.Svg),
            ("Hello, thank you for your email. Kind regards, Alex", ClipboardContentKind.Text)
        }) check("Library classification: " + kind + " / " + text[..Math.Min(text.Length, 28)], ClipboardContent.FromText(text).Kind == kind);
        check("Library color: CSS alpha is converted to static ARGB", ClipboardColors.Swatches("#0080FF80").Single() == "#800080FF" && ClipboardColors.Swatches("rgba(0,128,255,.5)").Single() == "#800080FF");
        check("Library color: out-of-range RGB and invalid stops stay text", ClipboardColors.Swatches("rgb(300, 0, 0)").Count == 0 && ClipboardColors.Swatches("linear-gradient(red, invalid)").Count == 0);
        check("Library SVG: malformed XML and external entity documents are not vectors", !ClipboardContentTypes.IsSvg("<svg><path></svg>") && !ClipboardContentTypes.IsSvg("<!DOCTYPE svg [<!ENTITY external SYSTEM 'file:///never-read'>]><svg>&external;</svg>"));
        check("Library HTML: only inert decoded fragment text is searchable", ClipboardContentTypes.PlainHtml("Version:1.0\n<!--StartFragment--><b>Hello &amp; world</b><!--EndFragment-->") == "Hello & world");
        var history = new ClipboardHistory(); int changes = 0; history.Changed += () => changes++;
        var snippet = history.CreateSnippet("Release reply", "Café launch", ["Customer", "customer", "Support"], ClipboardContent.FromText("Thank you for contacting our team."), now, settings, out var problem)!;
        check("Library manual entry: one validated event stores metadata atomically", snippet is not null && problem == "" && changes == 1 && snippet.Tags.SequenceEqual(new[] { "Customer", "Support" }));
        int before = changes;
        check("Library manual entry: duplicate and invalid metadata never partially add", history.CreateSnippet("Duplicate", "", [], snippet!.Content, now, settings, out _) is null && history.CreateSnippet("Invalid", "", [new string('x', 65)], ClipboardContent.FromText("Different payload"), now, settings, out _) is null && history.Items.Count == 1 && changes == before);
        var code = history.Add(ClipboardContent.FromText("const customerId = 42;"), "Editor", "editor.exe", now.AddMinutes(2), settings)!;
        var url = history.Add(ClipboardContent.FromText("https://example.org/research?topic=design"), "Browser", "browser.exe", now.AddDays(-1), settings)!;
        var custom = history.AddCollection("Research")!; history.Move(url.Id, custom.Id); history.ToggleFavorite(code.Id);
        check("Library search: title, notes, tags and diacritics are included", history.Query("release cafe support").Single().Id == snippet.Id);
        check("Library search: bounded abbreviations match developer contents", history.Query("cstmrId").Single().Id == code.Id && !history.Query("unrelatedterm").Any());
        check("Library filters: source names match exactly and kinds are ORed", history.Query(new ClipboardQuery { Source = "editor", Kinds = [ClipboardContentKind.Code, ClipboardContentKind.Link] }).Single().Id == code.Id && !history.Query(new ClipboardQuery { Source = "Edit" }).Any());
        check("Library filters: multitype combines with collection and search", history.Query(new ClipboardQuery { Kinds = [ClipboardContentKind.Text, ClipboardContentKind.Link] }).Count() == 2 && history.Query(new ClipboardQuery { Text = "research", CollectionId = custom.Id, Source = "Browser" }).Single().Id == url.Id);
        var groups = ClipboardHistory.Timeline(history.Query());
        check("Library timeline: dates and entries use copied time, not insertion order", groups.Count == 2 && groups[0].Items[0].Id == code.Id && groups[1].Items.Single().Id == url.Id);
        check("Library edit: text and metadata preserve identity, source and capture timestamps", history.Update(code.Id, "Customer ID sample", "API response", ["backend"], ClipboardContent.FromText("{\"customerId\":43}"), now.AddHours(1), out problem)
            && history.Items.Single(item => item.Id == code.Id) is { Content.Kind: ClipboardContentKind.Code, Content.Language: "JSON", Favorite: true, SourceName: "Editor" } edited && edited.CreatedAt == code.CreatedAt && edited.CopiedAt == code.CopiedAt && edited.EditedAt == now.AddHours(1));
        check("Library edit: duplicate payload never overwrites another entry", !history.Update(code.Id, "Wrong", "", [], snippet.Content, now, out problem) && problem.Length > 0 && history.Items.Single(item => item.Id == code.Id).Title == "Customer ID sample");
        history.RecordUse(code.Id, now.AddHours(2)); history.RecordUse(code.Id, now.AddHours(3)); history.Archive(url.Id, true);
        check("Library archive: active and archived scopes stay separate", !history.Query().Any(item => item.Id == url.Id) && history.Query(new ClipboardQuery { Archive = ClipboardArchiveScope.Archived }).Single().Id == url.Id && history.Query(new ClipboardQuery { Archive = ClipboardArchiveScope.All }).Count() == 3);
        var stats = history.Statistics(now);
        check("Library statistics: totals, favorites, dates and successful reuses are real", stats.Total == 3 && stats.Active == 2 && stats.Archived == 1 && stats.Favorites == 1 && stats.Today == 2 && stats.Uses == 2 && stats.Bytes > 0);
        check("Library recall: usage does not change timeline capture ordering", history.Items.Single(item => item.Id == code.Id) is { UseCount: 2 } used && used.LastUsedAt == now.AddHours(3) && used.CopiedAt == code.CopiedAt);
        settings.RetentionDays = 1; history.Clean(settings, now.AddDays(10));
        check("Library cleanup: archived entries and favorites survive retention", history.Items.Count == 2 && history.Items.Any(item => item.Id == url.Id) && history.Clear() == 0);
        var restored = new ClipboardHistory(); restored.Restore(JsonSerializer.Deserialize<ClipboardSnapshot>(JsonSerializer.Serialize(history.Snapshot()))!, settings, now);
        check("Library persistence: metadata, archive and usage survive version two", restored.Items.Single(item => item.Id == url.Id).Archived && restored.Items.Single(item => item.Id == code.Id) is { Title: "Customer ID sample", Notes: "API response", UseCount: 2 } loaded && loaded.Tags.Single() == "backend");
        var legacy = JsonSerializer.Deserialize<ClipboardSnapshot>("{\"Version\":1,\"Items\":[{\"Id\":\"legacy\",\"Content\":{\"Kind\":0,\"Text\":\"Old saved text\"}}]}")!;
        settings.RetentionDays = 0; restored.Restore(legacy, settings, now);
        check("Library migration: version one gets safe metadata defaults", restored.Items.Single() is { Title: "", Notes: "", Archived: false, UseCount: 0, Tags.Length: 0 });
        restored.Restore(new ClipboardSnapshot { Items = [new() { Id = "bad-tags", Content = ClipboardContent.FromText("bad"), Tags = [null!] }, new() { Id = "counts", Content = ClipboardContent.FromText("counts"), UseCount = int.MaxValue }] }, settings, now);
        check("Library persistence boundary: malformed tags are dropped and counters bounded", restored.Items.Single() is { Id: "counts", UseCount: 1_000_000 });
        CheckCollections(check, now);
        CheckCollectionAppearance(check, now);
        CheckApplications(check, now);
        CheckApplicationActions(check, now);
        settings.MaximumItems = 1; restored.Archive("counts", true);
        check("Library capacity: archive protection cannot be bypassed by manual snippets", restored.CreateSnippet("", "", [], ClipboardContent.FromText("refused"), now, settings, out problem) is null && problem.Length > 0 && restored.Items.Count == 1);
    }
    private static void CheckApplications(Action<string, bool> check, DateTimeOffset now)
    {
        var history = new ClipboardHistory(); var settings = new ClipboardSettings { RetentionDays = 0 };
        var browser = history.Add(ClipboardContent.FromText("Browser research one"), "Browser", "browser.exe", now, settings)!;
        history.Add(ClipboardContent.FromText("Browser research two"), "browser", "browser.exe", now.AddMinutes(1), settings);
        var editor = history.Add(ClipboardContent.FromText("const app = 42;"), "Editor", "editor.exe", now.AddMinutes(2), settings)!;
        history.Add(ClipboardContent.FromText("Manual unknown entry"), "", "", now.AddMinutes(3), settings);
        history.Add(ClipboardContent.FromText("Whitespace unknown entry"), "   ", "", now.AddMinutes(4), settings);
        var archived = history.Add(ClipboardContent.FromText("Archived provenance"), "Archive only", "", now.AddMinutes(5), settings)!;
        history.Archive(archived.Id, true); history.Move(browser.Id, "prompts"); history.ToggleFavorite(editor.Id);
        var groups = history.ApplicationGroups(new());
        check("Library applications: automatic groups merge app-name casing and include unknown provenance", groups.Count == 4
            && groups.Single(group => group.Source.Equals("browser", StringComparison.OrdinalIgnoreCase)).Count == 2 && groups.Single(group => group.Unknown).Count == 2);
        check("Library applications: group totals cover all active history including assigned collections", groups.Sum(group => group.Count) == history.Query().Count()
            && history.Query(new ClipboardQuery { Source = "BROWSER" }).Any(item => item.Id == browser.Id));
        check("Library applications: source grouping ignores collection and previous source selection", history.ApplicationGroups(new ClipboardQuery { CollectionId = "assets", Source = "Editor", UnknownSourceOnly = true }).SequenceEqual(groups));
        check("Library applications: text, kind, favorite and archive scopes retain their filtering meaning", history.ApplicationGroups(new ClipboardQuery { Text = "research", Kinds = [ClipboardContentKind.Text] }).Sum(group => group.Count) == 2
            && history.ApplicationGroups(new ClipboardQuery { FavoritesOnly = true }).Sum(group => group.Count) == 1
            && history.ApplicationGroups(new ClipboardQuery { Archive = ClipboardArchiveScope.Archived }).Single(group => group.Source == "Archive only").Count == 1);
        check("Library applications: zero-match applications remain navigable while searching", history.ApplicationGroups(new ClipboardQuery { Text = "no matching applications" }).Count == groups.Count
            && history.ApplicationGroups(new ClipboardQuery { Text = "no matching applications" }).All(group => group.Count == 0));
        check("Library applications: unknown sources are distinct from all history and named apps", history.Query(new ClipboardQuery { UnknownSourceOnly = true }).Count() == 2
            && history.Query(new ClipboardQuery { UnknownSourceOnly = true, Source = "Editor" }).All(item => string.IsNullOrWhiteSpace(item.SourceName)));
        int changes = 0; history.Changed += () => changes++;
        string saved = JsonSerializer.Serialize(history.Snapshot()); history.ApplicationGroups(new()); history.Query(new ClipboardQuery { Source = "Editor" }).ToList();
        check("Library applications: grouping is a read-only projection without duplicate entries or stored categories", changes == 0 && JsonSerializer.Serialize(history.Snapshot()) == saved);
        var sameLabel = history.Add(ClipboardContent.FromText("A real application named Unknown app"), "Unknown app", "", now.AddMinutes(6), settings)!;
        check("Library applications: a real app with the unknown label keeps its own identity", history.ApplicationGroups(new()).Count(group => group.Unknown || group.Source == "Unknown app") == 2
            && history.Query(new ClipboardQuery { Source = "Unknown app" }).Single().Id == sameLabel.Id);
        history.Add(editor.Content, "New source", "new.exe", now.AddMinutes(7), settings);
        check("Library applications: duplicate recapture follows current provenance without losing collection metadata", !history.ApplicationGroups(new()).Any(group => group.Source == "Editor")
            && history.Query(new ClipboardQuery { Source = "New source" }).Single() is { Favorite: true } recaptured && recaptured.Id == editor.Id);
        var restored = new ClipboardHistory(); restored.Restore(JsonSerializer.Deserialize<ClipboardSnapshot>(JsonSerializer.Serialize(history.Snapshot()))!, settings, now);
        check("Library applications: groups reconstruct from existing saved provenance without migration", restored.ApplicationGroups(new()).SequenceEqual(history.ApplicationGroups(new())));
        history.Remove(sameLabel.Id);
        check("Library applications: deleting an application's final entry removes only that derived group", !history.ApplicationGroups(new()).Any(group => group.Source == "Unknown app")
            && history.Collections.Any(group => group.Id == "prompts") && history.Items.Single(item => item.Id == browser.Id).CollectionId == "prompts");
    }
    private static void CheckApplicationActions(Action<string, bool> check, DateTimeOffset now)
    {
        var settings = new ClipboardSettings { RetentionDays = 0 };
        settings.HiddenApplications = ["Browser", "BROWSER", "   ", "", null!]; settings.Normalize();
        check("Library app hiding: normalization merges casing and unknown keys without excluding capture", settings.HiddenApplications.SequenceEqual(new[] { "Browser", "" })
            && settings.IsApplicationHidden("browser") && !settings.Excludes("Browser"));
        var copy = settings.Copy(); copy.HiddenApplications[0] = "Other";
        check("Library app hiding: preference copies own their arrays", settings.HiddenApplications[0] == "Browser" && !ReferenceEquals(settings.HiddenApplications, copy.HiddenApplications));
        var loaded = JsonSerializer.Deserialize<ClipboardSettings>(JsonSerializer.Serialize(settings))!; loaded.Normalize();
        check("Library app hiding: reload restores labels without enabling capture", loaded.IsApplicationHidden("BROWSER") && loaded.IsApplicationHidden("") && !loaded.Enabled
            && JsonSerializer.Deserialize<ClipboardSettings>("{}")!.HiddenApplications.Length == 0);
        check("Library app hiding: hide and restore are idempotent and case insensitive", !loaded.SetApplicationHidden("browser", true) && loaded.SetApplicationHidden("BROWSER", false)
            && !loaded.IsApplicationHidden("Browser") && !loaded.SetApplicationHidden("browser", false));
        var history = new ClipboardHistory();
        var first = history.Add(ClipboardContent.FromText("Synthetic browser ordinary"), "Browser", "", now, settings)!;
        var second = history.Add(ClipboardContent.FromText("Synthetic browser favorite"), "browser", "", now.AddMinutes(1), settings)!;
        var third = history.Add(ClipboardContent.FromText("Synthetic browser archive"), "BROWSER", "", now.AddMinutes(2), settings)!;
        history.Move(first.Id, "prompts"); history.ToggleFavorite(second.Id); history.Archive(third.Id, true);
        history.Add(ClipboardContent.FromText("Synthetic other browser"), "Browser Helper", "", now, settings);
        history.Add(ClipboardContent.FromText("Synthetic unknown one"), "", "", now, settings);
        history.Add(ClipboardContent.FromText("Synthetic unknown whitespace"), "  ", "", now, settings);
        history.Add(ClipboardContent.FromText("Synthetic literal unknown name"), "Unknown app", "", now, settings);
        int changes = 0; history.Changed += () => changes++;
        check("Library app hiding: hidden sources remain queryable and continue to accept captures", history.Query(new ClipboardQuery { Source = "browser", Archive = ClipboardArchiveScope.All }).Count() == 3
            && history.ApplicationGroups(new()).Any(group => group.Source.Equals("browser", StringComparison.OrdinalIgnoreCase)) && settings.HiddenApplications.Length == 2);
        var collections = history.Collections.ToArray();
        check("Library app clearing: count covers exact provenance across favorites, archives and collections", history.ApplicationCount("bRoWsEr") == 3 && history.ApplicationCount("") == 2);
        check("Library app clearing: one atomic mutation removes all of that source and no prefix neighbor", history.ClearApplication("bRoWsEr") == 3 && changes == 1
            && history.Items.Count == 4 && history.Items.Any(item => item.SourceName == "Browser Helper") && history.Collections.SequenceEqual(collections));
        check("Library app clearing: clearing an absent source does not save or recreate a group", history.ClearApplication("Browser") == 0 && changes == 1 && history.ApplicationCount("Browser") == 0);
        check("Library app clearing: unknown key is never an alias for all history or the literal unknown label", history.ClearApplication("") == 2 && changes == 2 && history.Items.Count == 2
            && history.Items.Any(item => item.SourceName == "Unknown app"));
        check("Library app clearing: navigation preferences stay independent from history mutations", settings.IsApplicationHidden("Browser") && settings.IsApplicationHidden(""));
    }
    private static void CheckCollectionAppearance(Action<string, bool> check, DateTimeOffset now)
    {
        var history = new ClipboardHistory(); var settings = new ClipboardSettings { RetentionDays = 0 };
        int changes = 0; history.Changed += () => changes++;
        check("Library appearance: existing collections retain native default styles", history.Collections.All(group => group.BackgroundColor == "" && group.Icon == ""));
        var reply = history.Add(ClipboardContent.FromText("Appearance does not alter entries"), "Fixture", "", now, settings)!; history.Move(reply.Id, "prompts");
        int before = changes;
        check("Library appearance: color and icon are committed atomically with normalized color", history.SetCollectionAppearance("prompts", " #08f ", "star")
            && history.Collections.Single(group => group.Id == "prompts") is { BackgroundColor: "#0088FF", Icon: "star" } && changes == before + 1);
        string saved = JsonSerializer.Serialize(history.Snapshot()); before = changes;
        check("Library appearance: rejected color, alpha and unknown icons never partially save", !history.SetCollectionAppearance("prompts", "red", "folder")
            && !history.SetCollectionAppearance("prompts", "#AA0088FF", "folder") && !history.SetCollectionAppearance("prompts", "#123456", "unsupported")
            && !history.SetCollectionAppearance("missing", "#123456", "folder") && !history.SetCollectionAppearance("prompts", null!, "folder") && !history.SetCollectionAppearance("prompts", "", null!)
            && changes == before && JsonSerializer.Serialize(history.Snapshot()) == saved);
        check("Library appearance: no-op saves do not emit data changes", history.SetCollectionAppearance("prompts", "#0088ff", "star") && changes == before);
        check("Library appearance: name and appearance commit together with only one notification", history.UpdateCollection("prompts", " Updated replies ", "#abc", "heart", out _)
            && history.Collections.Single(group => group.Id == "prompts") is { Name: "Updated replies", BackgroundColor: "#AABBCC", Icon: "heart" } && changes == before + 1
            && history.Items.Single() == reply with { CollectionId = "prompts" });
        before = changes; saved = JsonSerializer.Serialize(history.Snapshot());
        check("Library appearance: invalid names and invalid appearance cannot save the other half", !history.UpdateCollection("prompts", "ASSETS", "#0088ff", "star", out _)
            && !history.UpdateCollection("prompts", " ", "#0088ff", "star", out _) && !history.UpdateCollection("prompts", new string('x', 33), "#0088ff", "star", out _)
            && !history.UpdateCollection("prompts", "Valid name", "invalid", "star", out _) && !history.UpdateCollection("missing", "Valid name", "#fff", "star", out _)
            && changes == before && JsonSerializer.Serialize(history.Snapshot()) == saved);
        check("Library appearance: a combined no-op save has no persistence notification", history.UpdateCollection("prompts", "Updated replies", "#aabbcc", "heart", out _) && changes == before);
        history.UpdateCollection("prompts", "Replies", "#0088ff", "star", out _); history.ReorderCollection("prompts", "assets", true);
        check("Library appearance: rename and arbitrary reorder preserve visual identity", history.Collections.Single(group => group.Id == "prompts") is { Name: "Replies", BackgroundColor: "#0088FF", Icon: "star" }
            && history.Items.Single().Id == reply.Id && history.Items.Single().CollectionId == "prompts");
        var restored = new ClipboardHistory(); restored.Restore(JsonSerializer.Deserialize<ClipboardSnapshot>(JsonSerializer.Serialize(history.Snapshot()))!, settings, now);
        check("Library appearance: collection order, tint and icon survive persistence", restored.Collections.SequenceEqual(history.Collections));
        check("Library appearance: icon and color can be changed or reset independently", history.SetCollectionAppearance("prompts", "", "star")
            && history.Collections.Single(group => group.Id == "prompts") is { BackgroundColor: "", Icon: "star" } && history.SetCollectionAppearance("prompts", "#123456", "")
            && history.Collections.Single(group => group.Id == "prompts") is { BackgroundColor: "#123456", Icon: "" }
            && history.SetCollectionAppearance("prompts", "", "") && history.Collections.Single(group => group.Id == "prompts") is { BackgroundColor: "", Icon: "" });
        restored.Restore(JsonSerializer.Deserialize<ClipboardSnapshot>("{\"Version\":3,\"Collections\":[{\"Id\":\"legacy\",\"Name\":\"Legacy label\"}]}")!, settings, now);
        check("Library appearance: legacy snapshots keep their original no-color no-icon look", restored.Collections.Single() is { BackgroundColor: "", Icon: "" });
        restored.Restore(new ClipboardSnapshot { Collections = [new("null", "Null values") { BackgroundColor = null!, Icon = null! }, new("invalid", "Invalid values") { BackgroundColor = "#000000FF", Icon = "file:///never-load" }, new("valid", "Valid") { BackgroundColor = "#abc", Icon = "code" }] }, settings, now);
        check("Library appearance: malformed storage falls back to native appearance without dropping collections", restored.Collections.Count == 3
            && restored.Collections[0] is { BackgroundColor: "", Icon: "" } && restored.Collections[1] is { BackgroundColor: "", Icon: "" } && restored.Collections[2] is { BackgroundColor: "#AABBCC", Icon: "code" });
    }
    private static void CheckCollections(Action<string, bool> check, DateTimeOffset now)
    {
        var settings = new ClipboardSettings { MaximumItems = 100, RetentionDays = 0 };
        var history = new ClipboardHistory(); int changes = 0; history.Changed += () => changes++;
        string[] original = history.Collections.Select(group => group.Id).ToArray();
        check("Library collections: arbitrary reorder inserts before the target", history.ReorderCollection("inspirations", "colors")
            && history.Collections.Select(group => group.Id).SequenceEqual(new[] { "prompts", "inspirations", "colors", "assets" }));
        check("Library collections: reverse reorder inserts after the target", history.ReorderCollection("prompts", "assets", true)
            && history.Collections.Select(group => group.Id).SequenceEqual(new[] { "inspirations", "colors", "assets", "prompts" }));
        int before = changes; string saved = JsonSerializer.Serialize(history.Snapshot());
        check("Library collections: invalid and adjacent no-op reorders are atomic", !history.ReorderCollection("missing", "colors") && !history.ReorderCollection("colors", "missing")
            && !history.ReorderCollection("colors", "colors") && !history.ReorderCollection("colors", "assets") && !history.ReorderCollection("assets", "colors", true)
            && changes == before && JsonSerializer.Serialize(history.Snapshot()) == saved);
        check("Library collections: starter collections can be renamed without changing identity", history.RenameCollection("colors", " Brand colors ") && history.Collections.Single(group => group.Id == "colors").Name == "Brand colors");
        before = changes;
        check("Library collections: blank, long and duplicate names never change the order", !history.RenameCollection("colors", " ") && !history.RenameCollection("colors", new string('x', 33))
            && !history.RenameCollection("colors", "ASSETS") && history.RenameCollection("colors", "Brand colors") && changes == before);
        var entry = history.Add(ClipboardContent.FromText("A reusable saved reply"), "Fixture", "", now, settings)!;
        history.ToggleFavorite(entry.Id); history.Move(entry.Id, "colors"); history.RecordUse(entry.Id, now.AddMinutes(1)); history.Archive(entry.Id, true);
        check("Library collections: starter deletion only removes membership", history.RemoveCollection("colors") && !history.RemoveCollection("colors")
            && history.Items.Single() is { CollectionId: "", Favorite: true, Archived: true, UseCount: 1 } kept && kept.Content == entry.Content && kept.CopiedAt == now);
        var swatch = history.Add(ClipboardContent.FromText("#0077FF"), "Fixture", "", now, settings)!;
        check("Library collections: removed starter ids no longer expose automatic groups", !history.Query(collectionId: "colors").Any());
        history.Remove(swatch.Id);
        var restored = new ClipboardHistory(); restored.Restore(JsonSerializer.Deserialize<ClipboardSnapshot>(JsonSerializer.Serialize(history.Snapshot()))!, settings, now);
        check("Library collections: current snapshot round trip preserves order and does not resurrect deleted starters", restored.Snapshot().Version == 4
            && restored.Collections.SequenceEqual(history.Collections) && restored.Items.Single().CollectionId == "");
        foreach (string id in restored.Collections.Select(group => group.Id).ToArray()) restored.RemoveCollection(id);
        var empty = restored.Snapshot(); restored.Restore(empty, settings, now);
        check("Library collections: intentionally empty collection lists survive restore", restored.Collections.Count == 0 && restored.Items.Count == 1);
        restored.Restore(new ClipboardSnapshot { Version = 2, Collections = [new("custom", "Custom"), new("colors", "Legacy colors")] }, settings, now);
        check("Library collections: legacy snapshots retain their four starter categories", original.All(id => restored.Collections.Any(group => group.Id == id))
            && restored.Collections.Single(group => group.Id == "colors").Name == "Legacy colors" && restored.Collections.Last().Id == "custom");
        restored.Restore(new ClipboardSnapshot { Collections = [new("b", "Beta"), new("a", "Alpha"), new("b", "Other"), new("c", "ALPHA"), new("", "Empty"), new("d", "   "), new("e", new string('x', 33))] }, settings, now);
        check("Library collections: restore validates duplicate identities and names without reordering valid groups", restored.Collections.SequenceEqual(new[] { new ClipboardCollection("b", "Beta"), new ClipboardCollection("a", "Alpha") }));
        restored.Restore(new ClipboardSnapshot { Collections = Enumerable.Range(0, 25).Select(i => new ClipboardCollection("group" + i, "Group " + i)).ToList() }, settings, now);
        check("Library collections: restore caps category count without dropping the valid prefix", restored.Collections.Count == 20 && restored.Collections[0].Id == "group0" && restored.Collections[^1].Id == "group19");
    }
}
