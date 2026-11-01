using System.Text.Json;
using BrimDeck.Core;

internal static class ClipboardHistoryTests
{
    public static void Run(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var settings = new ClipboardSettings { MaximumItems = 3, RetentionDays = 0 };
        check("Clipboard: recording is opt-in and the default shortcut avoids Windows clipboard history", !settings.Enabled && settings.Hotkey == "Ctrl+Alt+V");
        var history = new ClipboardHistory();
        var first = history.Add(ClipboardContent.FromText("A useful prompt"), "Editor", "", now, settings)!;
        history.ToggleFavorite(first.Id); history.Move(first.Id, "prompts");
        history.Add(ClipboardContent.FromText("#0080ff"), "Browser", "", now.AddSeconds(1), settings);
        history.Add(ClipboardContent.FromText("https://example.org/docs"), "Browser", "", now.AddSeconds(2), settings);
        var duplicate = history.Add(ClipboardContent.FromText("A useful prompt"), "Notes", "", now.AddSeconds(3), settings)!;
        check("Clipboard: duplicates move to the front without losing identity, favorite or collection", history.Items.Count == 3 && history.Items[0].Id == first.Id && duplicate.Favorite && duplicate.CollectionId == "prompts" && duplicate.CreatedAt == now && duplicate.CopiedAt == now.AddSeconds(3));
        check("Clipboard: search is case-insensitive and all words must match", history.Query("USEFUL notes").Single().Id == first.Id && !history.Query("useful absent").Any());
        check("Clipboard: URLs and hex colors are classified without network requests", history.Query(collectionId: "colors").Single().Content.Kind == ClipboardContentKind.Color && history.Items.Any(item => item.Content.Kind == ClipboardContentKind.Link));
        history.Add(ClipboardContent.FromText("new"), "Editor", "", now.AddSeconds(4), settings);
        check("Clipboard: the oldest non-favorite goes first at capacity", history.Items.Count == 3 && history.Items.Any(item => item.Id == first.Id) && !history.Items.Any(item => item.Content.Kind == ClipboardContentKind.Color));
        check("Clipboard: favorites filter returns real counts", history.Query(favoritesOnly: true).Single().Id == first.Id);
        var group = history.AddCollection("Work")!;
        check("Clipboard: collections can be created but not duplicated", group is not null && history.AddCollection("work") is null && history.AddCollection(" ") is null);
        history.Move(first.Id, group!.Id);
        check("Clipboard: a moved item is browsable in its collection", history.Query(collectionId: group.Id).Single().Id == first.Id);
        check("Clipboard: rename preserves collection identity", history.RenameCollection(group.Id, "Projects") && history.Collections.Any(item => item.Id == group.Id && item.Name == "Projects"));
        check("Clipboard: deleting any collection never deletes its contents", history.RemoveCollection(group.Id) && history.Items.Any(item => item.Id == first.Id && item.CollectionId == "") && history.RemoveCollection("assets"));
        settings.RetentionDays = 1;
        history.Clean(settings, now.AddDays(2));
        check("Clipboard: retention clears old items but preserves favorites", history.Items.Count == 1 && history.Items[0].Favorite);
        history.Add(ClipboardContent.FromText("temporary"), "", "", now.AddDays(2), settings);
        check("Clipboard: clear keeps favorites unless explicitly included", history.Clear() == 1 && history.Items.Count == 1 && history.Clear(false) == 1 && history.Items.Count == 0);
        check("Clipboard: empty, oversized and disabled formats are rejected", history.Add(ClipboardContent.FromText(" "), "", "", now, settings) is null && history.Add(ClipboardContent.FromText(new string('x', 1_000_001)), "", "", now, settings) is null);
        settings.CaptureImages = false;
        check("Clipboard: disabled image capture cannot enter history", history.Add(new ClipboardContent { Kind = ClipboardContentKind.Image, Image = [1], PixelWidth = 1, PixelHeight = 1 }, "", "", now, settings) is null);
        settings.MaximumItems = 1;
        var favorite = history.Add(ClipboardContent.FromText("kept"), "", "", now, settings)!; history.ToggleFavorite(favorite.Id);
        check("Clipboard: a full favorite history refuses new entries rather than evicting favorites", history.Add(ClipboardContent.FromText("refused"), "", "", now, settings) is null && history.Items.Count == 1);
        var serialized = JsonSerializer.Serialize(history.Snapshot());
        var loaded = new ClipboardHistory(); loaded.Restore(JsonSerializer.Deserialize<ClipboardSnapshot>(serialized)!, settings, now);
        check("Clipboard: content, favorites and source metadata survive a snapshot round trip", loaded.Items.Single().Content.Text == "kept" && loaded.Items[0].Favorite);
        loaded.Restore(new ClipboardSnapshot { Items = [new() { Content = new() { Text = null! } }] }, settings, now);
        check("Clipboard: malformed persisted entries are discarded at the storage boundary", loaded.Items.Count == 0);
        settings.MaximumItems = -1; settings.RetentionDays = 9999; settings.Hotkey = "a+b"; settings.Normalize();
        check("Clipboard: settings normalize limits and invalid shortcuts", settings.MaximumItems == 1 && settings.RetentionDays == 365 && settings.Hotkey == "");
        var copy = new DeckSettings(); var cloned = copy.Copy(); cloned.Clipboard.MaximumItems = 40;
        check("Clipboard: settings copies do not share clipboard preferences", copy.Clipboard.MaximumItems == 200);
        var roundTrip = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(cloned))!; roundTrip.Normalize();
        check("Clipboard: clipboard settings survive ordinary settings storage", roundTrip.Clipboard.MaximumItems == 40);
        cloned.Clipboard.SetApplicationPinned("Browser fixture", true); cloned.Clipboard.SetApplicationPinned("Editor fixture", true);
        check("Clipboard: application pins are newest-first preferences and ignore repeated case variants", !cloned.Clipboard.SetApplicationPinned("browser fixture", true) && cloned.Clipboard.PinnedApplications.SequenceEqual(new[] { "Editor fixture", "Browser fixture" }) && copy.Clipboard.PinnedApplications.Length == 0);
        var pinnedCopy = cloned.Copy(); pinnedCopy.Clipboard.PinnedApplications[0] = "Independent fixture";
        check("Clipboard: application pin arrays are independent across settings copies", cloned.Clipboard.PinnedApplications[0] == "Editor fixture");
        roundTrip = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(cloned))!; roundTrip.Normalize();
        check("Clipboard: pinned sources survive ordinary settings storage without enabling capture", roundTrip.Clipboard.PinnedApplications.SequenceEqual(cloned.Clipboard.PinnedApplications) && !roundTrip.Clipboard.Enabled);
        roundTrip.Clipboard.PinnedApplications = ["Editor fixture", "editor fixture", null!, "", "   "]; roundTrip.Normalize();
        check("Clipboard: malformed application pins normalize to unique source keys", roundTrip.Clipboard.PinnedApplications.SequenceEqual(new[] { "Editor fixture", "" }) && roundTrip.Clipboard.SetApplicationPinned("EDITOR fixture", false) && !roundTrip.Clipboard.IsApplicationPinned("Editor fixture"));
        check("Clipboard: legacy preferences have no pins", JsonSerializer.Deserialize<ClipboardSettings>("{}")!.PinnedApplications.Length == 0);
        check("Clipboard: process exclusions match whole process names, not substrings", new ClipboardSettings().Excludes("KeePassXC.exe") && !new ClipboardSettings().Excludes("NotKeePassXC"));
        check("Clipboard: short color codes expand and non-hex text stays text", ClipboardContent.ColorHex("#08F") == "#0088FF" && ClipboardContent.ColorHex("#0080FF") == "#0080FF" && ClipboardContent.ColorHex("#00oops") is null);
        CheckUnlimited(check, now);
        ClipboardLibraryTests.Run(check);
    }

    private static void CheckUnlimited(Action<string, bool> check, DateTimeOffset now)
    {
        var settings = new ClipboardSettings { MaximumItems = 0, RetentionDays = 0 }; settings.Normalize();
        check("Clipboard unlimited: zero survives normalization and ordinary settings storage", settings.MaximumItems == 0 && settings.ItemLimit == int.MaxValue
            && JsonSerializer.Deserialize<ClipboardSettings>(JsonSerializer.Serialize(settings))!.MaximumItems == 0);
        var history = new ClipboardHistory();
        var entries = Enumerable.Range(0, 1205).Select(i => new ClipboardEntry { Id = "unlimited-" + i, Content = ClipboardContent.FromText("Unlimited fixture " + i), CopiedAt = now.AddSeconds(-i), Favorite = true }).ToList();
        history.Restore(new ClipboardSnapshot { Items = entries }, settings, now);
        check("Clipboard unlimited: restore does not silently truncate at the old thousand-entry boundary", history.Items.Count == 1205);
        var next = history.Add(ClipboardContent.FromText("Past a thousand protected entries"), "Fixture", "", now, settings);
        check("Clipboard unlimited: capture is not rejected by protected entry count", next is not null && history.Items.Count == 1206);
        var snippet = history.CreateSnippet("Fixture", "", [], ClipboardContent.FromText("Manual unlimited fixture"), now, settings, out _);
        check("Clipboard unlimited: manual entries follow the same count policy", snippet is not null && history.Items.Count == 1207);
        var restored = new ClipboardHistory(); restored.Restore(history.Snapshot(), settings, now);
        check("Clipboard unlimited: all entries survive another snapshot restore", restored.Items.Count == 1207);
        settings.RetentionDays = 1; history.Clean(settings, now.AddDays(2));
        check("Clipboard unlimited: retention still cleans unprotected entries", history.Items.Count == 1205 && history.Items.All(item => item.Favorite));
        settings.MaximumItems = 50; settings.RetentionDays = 0; history.Clear(false);
        entries = entries.Select(entry => entry with { Favorite = false }).ToList(); history.Restore(new ClipboardSnapshot { Items = entries }, settings, now);
        check("Clipboard unlimited: selecting a finite limit restores ordinary count cleanup", history.Items.Count == 50);
        settings.MaximumItems = 0;
        history.Clear(false);
        for (int i = 0; i < 5; i++)
        {
            var pixels = new byte[ClipboardHistory.MaxItemBytes]; pixels[0] = (byte)i;
            history.Add(new ClipboardContent { Kind = ClipboardContentKind.Image, Image = pixels, PixelWidth = 1, PixelHeight = 1 }, "", "", now.AddSeconds(i), settings);
        }
        check("Clipboard unlimited: entry-count opt-out retains the existing payload safety budget", history.Items.Count == 4 && history.Items.Sum(item => item.Content.ByteCount) == ClipboardHistory.MaxTotalBytes);
    }
}
