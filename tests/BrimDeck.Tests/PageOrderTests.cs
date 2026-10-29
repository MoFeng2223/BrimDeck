using BrimDeck.Core;

// Which page comes after which. The panel's tab strip, its wheel, its hand-over direction and its fallback page all read
// this one list (DeckSettings.PageOrder), plugin pages included, so the tests here are about that list: what it says when
// a file has never been arranged, what a drag writes into it, and that nothing the reader did not touch moves.
internal static class PageOrderTests
{
    // Slots stand up as if the scanner had loaded them; the hooks are restored afterwards because they are static.
    private static IDisposable Load(params string?[] slots)
    {
        var (wasOccupied, wasId, wasMinimum) = (DeckPages.SlotOccupied, DeckPages.SlotId, DeckPages.SlotMinimum);
        DeckPages.SlotOccupied = slot => slots[slot] is { Length: > 0 };
        DeckPages.SlotId = slot => slots[slot];
        DeckPages.SlotMinimum = _ => (0, 0);
        return new Restore(() => { (DeckPages.SlotOccupied, DeckPages.SlotId, DeckPages.SlotMinimum) = (wasOccupied, wasId, wasMinimum); });
    }

    private sealed class Restore(Action act) : IDisposable
    {
        public void Dispose() => act();
    }

    private static ExtensionPageSettings Entry(string id, bool shown = true) => new(id, shown);
    private static DeckSettings Listed(params DeckPage[] pages) => new() { PageOrder = [.. pages] };

    internal static void Run(Action<string, bool> check)
    {
        // ── the order the panel switches in ──────────────────────────────────────────────────────────────
        using (Load("alpha", "beta", "gamma", null))
        {
            check("Order: a file nobody arranged keeps the built-in three and adds the loaded plugins in slot order",
                DeckPages.Arrangement(new DeckSettings()).SequenceEqual([DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension0, DeckPage.Extension1, DeckPage.Extension2]));
            check("Order: an empty slot adds no page",
                !DeckPages.Arrangement(new DeckSettings()).Contains(DeckPage.Extension3));

            var listed = Listed(DeckPage.Extension2, DeckPage.Usage, DeckPage.Music, DeckPage.Extension0, DeckPage.Monitor);
            check("Order: the saved list is read as written, and a plugin installed since joins at the end",
                DeckPages.Arrangement(listed).SequenceEqual([DeckPage.Extension2, DeckPage.Usage, DeckPage.Music, DeckPage.Extension0, DeckPage.Monitor, DeckPage.Extension1]));

            var hides = listed.Copy();
            hides.Extensions.Add(Entry("alpha", shown: false));
            check("Order: a hidden plugin leaves the switch but does not move the others",
                DeckPages.Visible(hides).SequenceEqual([DeckPage.Extension2, DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension1])
                && DeckPages.Visible(listed.Copy()).Contains(DeckPage.Extension0));
            // The fallback is the first shown page in that order, which is a plugin once the built-in three are off.
            var onlyPlugins = hides.Copy();
            onlyPlugins.UsagePage = onlyPlugins.MusicPage = onlyPlugins.MonitorPage = false;
            check("Order: the page the panel falls back to is the first shown one in that order",
                DeckPages.Resolve(onlyPlugins, DeckPage.Extension0) == DeckPage.Extension2);
        }

        // A page left behind by a plugin whose folder is gone is not shown, but the saved list is not edited either:
        // putting the folder back puts the tab back where the reader left it.
        using (Load("alpha", "beta", "gamma", null))
        {
            var ghost = Listed(DeckPage.Usage, DeckPage.Extension3, DeckPage.Music, DeckPage.Monitor);
            check("Order: an entry for a slot nothing is loaded in is dropped from the switch, not from the file",
                !DeckPages.Visible(ghost).Contains(DeckPage.Extension3) && ghost.PageOrder.Contains(DeckPage.Extension3));
            DeckPages.ReorderExtensions(ghost, [2, 0, 1]);
            check("Reorder: a ghost entry is not a place a drag may write into, and it does not shift the others",
                ghost.PageOrder.SequenceEqual([DeckPage.Usage, DeckPage.Extension3, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension2, DeckPage.Extension0, DeckPage.Extension1]));
        }

        // The settings file is read before the scanner has filled any slot, so normalizing it must not need to know
        // which plugin pages exist: a plugin page dropped here would be gone from the reader's file for good.
        {
            var saved = new[] { DeckPage.Extension1, DeckPage.Music, DeckPage.Usage, DeckPage.Monitor };
            check("Normalize: a plugin page survives the list being read while no slot is known yet",
                DeckPages.Normalize(saved).SequenceEqual(saved));
            check("Normalize: each page comes once, and a page the file never had joins at the end",
                DeckPages.Normalize([DeckPage.Music, DeckPage.Music]).SequenceEqual([DeckPage.Music, DeckPage.Usage, DeckPage.Monitor]));
            check("Normalize: a page number from a newer version is dropped rather than kept as a hole",
                !DeckPages.Normalize([DeckPage.Usage, (DeckPage)99, DeckPage.Music]).Contains((DeckPage)99));
        }

        // ── restoring the plugins page to its defaults ─────────────────────────────────────────────────
        using (Load("alpha", "beta", "gamma", null))
        {
            var arranged = Listed(DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension2, DeckPage.Extension0);
            arranged.Extensions.Add(Entry("beta", shown: false));
            DeckPages.ResetExtensionPlaces(arranged);
            check("Reset: the plugins page's defaults clear the plugin places, and only those",
                arranged.PageOrder.SequenceEqual([DeckPage.Usage, DeckPage.Music, DeckPage.Monitor]));
            check("Reset: with the places gone the loaded plugins stand in slot order again",
                DeckPages.Arrangement(arranged).SequenceEqual([DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension0, DeckPage.Extension1, DeckPage.Extension2]));

            // A built-in order the appearance page wrote is not this page's to undo.
            var builtins = Listed(DeckPage.Monitor, DeckPage.Extension0, DeckPage.Usage, DeckPage.Music);
            DeckPages.ResetExtensionPlaces(builtins);
            check("Reset: the built-in pages keep the order their own page arranged",
                builtins.PageOrder.SequenceEqual([DeckPage.Monitor, DeckPage.Usage, DeckPage.Music]));
        }

        // ── writing an order down ────────────────────────────────────────────────────────────────────────
        var before = Array.Empty<DeckPage>();
        using (Load("alpha", "beta", "gamma", "delta"))
        {
            var settings = new DeckSettings();
            DeckPages.ReorderExtensions(settings, [2, 0, 3, 1]);
            check("Reorder: the drag writes the plugin pages into the one saved list, built-in pages first as always",
                settings.PageOrder.SequenceEqual([DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension2, DeckPage.Extension0, DeckPage.Extension3, DeckPage.Extension1]));
            check("Reorder: arranging and then reading back is a fixed point",
                DeckPages.Arrangement(settings).SequenceEqual(settings.PageOrder)
                && DeckPages.Visible(settings).Skip(3).SequenceEqual([DeckPage.Extension2, DeckPage.Extension0, DeckPage.Extension3, DeckPage.Extension1]));
            before = settings.PageOrder.ToArray();

            // The built-in pages keep their places: only the positions the plugin rows already held are rewritten.
            var among = Listed(DeckPage.Extension0, DeckPage.Usage, DeckPage.Extension1, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension2);
            DeckPages.ReorderExtensions(among, [2, 0, 1]);
            // delta's page was never listed (installed after the file), so it joins at the end rather than being dropped.
            check("Reorder: built-in pages stay exactly where they stand, and the plugin pages take the places they held",
                among.PageOrder.SequenceEqual([DeckPage.Extension2, DeckPage.Usage, DeckPage.Extension0, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension1, DeckPage.Extension3]));

            var same = settings.Copy();
            DeckPages.ReorderExtensions(same, [2, 0, 3, 1]);
            check("Reorder: asking for the order it already has changes nothing",
                same.PageOrder.SequenceEqual(before));

            // A plugin the caller left out of the request is not erased by the drag of the others.
            var left = settings.Copy();
            DeckPages.ReorderExtensions(left, [0, 1]);
            check("Reorder: a loaded plugin the caller left out keeps a place rather than losing its tab",
                DeckPages.Arrangement(left).Count(page => DeckPages.SlotOf(page) >= 0) == 4 && DeckPages.Visible(left).Count(page => DeckPages.SlotOf(page) >= 0) == 4);

            var none = settings.Copy();
            DeckPages.ReorderExtensions(none, []);
            check("Reorder: an empty order is not a request to erase the list",
                none.PageOrder.SequenceEqual(before));

            // A row appears in 设置 → 插件 for reasons that say nothing about position: a declared value the plugin
            // itself saved, its own switch, its page size. None of them may move a tab — this is how the old shape of
            // the feature failed, with the rows standing in for the reader's arrangement.
            settings.SetExtensionValue("delta", "warn", new ExtensionSettingValue("number", false, 65));
            settings.Extensions.Add(new ExtensionPageSettings("beta") { Shown = false });
            settings.Extensions.Add(new ExtensionPageSettings("alpha") { Width = 640, Height = 220 });
            check("Reorder: rows made by a value, a switch or a size move nothing",
                settings.PageOrder.SequenceEqual(before));
            // What belongs to the plugin travels with it, since it is kept under the plugin's id and not under a place.
            check("Reorder: size and declared values travel with the plugin, not with its place",
                settings.Extensions.Single(e => e.Id == "alpha") is { Width: 640, Height: 220 } && settings.ExtensionValues("delta")["warn"].Number == 65);
            // beta's tab now hides, but its place in the list is kept for when it is switched back on.
            check("Reorder: switching a plugin off hides its tab and keeps its place",
                !DeckPages.Visible(settings).Contains(DeckPage.Extension1) && settings.PageOrder.Contains(DeckPage.Extension1));
        }

        // A file written by the build that kept each plugin's place on its own row still reads: the row is about the
        // plugin (its switch, its size, its declared values), and the one key this build no longer knows is ignored
        // rather than taken as a damaged file.
        {
            var folder = Path.Combine(Path.GetTempPath(), "BrimDeck-PageOrder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var store = new SettingsStore(folder, "1.2.2");
                var saved = new DeckSettings();
                saved.Extensions.Add(new ExtensionPageSettings("alpha") { Width = 640, Height = 220 });
                saved.PageOrder = [DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension0];
                store.Save(saved);
                var root = System.Text.Json.Nodes.JsonObject.Parse(File.ReadAllText(store.FilePath))!.AsObject();
                root["Extensions"]!.AsArray()[0]!.AsObject()["Order"] = 2;
                File.WriteAllText(store.FilePath, root.ToJsonString());

                var again = new SettingsStore(folder, "1.2.2");
                var read = again.Load();
                check("File: a place written on the row by an earlier build is ignored, not read as damage",
                    again.LoadWarning is null && read.Extensions.Single(e => e.Id == "alpha") is { Width: 640, Height: 220 }
                    && read.PageOrder.SequenceEqual([DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension0]));
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        }
    }
}
