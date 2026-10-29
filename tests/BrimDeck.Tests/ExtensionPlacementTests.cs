using BrimDeck.Core;

// Which slot each plugin folder lands in. What the reader arranges is written in slots, so this rule is what keeps a
// tab where the reader put it: a folder taken away must not slide the rest left, and a folder that was only edited must
// not drift to another slot when the plugins are loaded again.
internal static class ExtensionPlacementTests
{
    private static Dictionary<string, int> Places(params (string Id, int Slot)[] recorded) => recorded.ToDictionary(pair => pair.Id, pair => pair.Slot, StringComparer.Ordinal);

    private static string[] Names(params string[] ids) => ids;

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

    internal static void Run(Action<string, bool> check)
    {
        // ── what the file says wins ──────────────────────────────────────────────────────────────────────
        // Nothing recorded, nothing live: the name order the scan reads is the slot order the reader sees.
        check("Place: with nothing recorded the folders go into the slots in the order they were read",
            ExtensionPlacement.Place(Names("alpha", "beta", "gamma"), [null, null, null, null], null).SequenceEqual([0, 1, 2]));
        // The recorded places are filled first, so a cold start puts every tab back where the reader left it.
        check("Place: a recorded place is taken again rather than the next free slot",
            ExtensionPlacement.Place(Names("alpha", "beta", "gamma"), [null, null, null, null], Places(("gamma", 0), ("alpha", 2))).SequenceEqual([2, 1, 0]));
        // The point of the whole rule: one folder removed leaves the others exactly where they were.
        check("Place: taking a plugin away does not slide the others into its slot",
            ExtensionPlacement.Place(Names("alpha", "gamma"), [null, null, null, null], Places(("alpha", 0), ("beta", 1), ("gamma", 2))).SequenceEqual([0, 2]));
        // A folder the file never heard of goes to the first slot that is free, however far along it is.
        check("Place: a new folder goes into the first free slot",
            ExtensionPlacement.Place(Names("alpha", "beta", "gamma"), [null, null, null, null], Places(("alpha", 0), ("gamma", 2))).SequenceEqual([0, 1, 2]));

        // ── what is on screen wins over what was written ─────────────────────────────────────────────────
        // A scan that reloads the plugins must not move one it is already showing: the reader's tabs, the pages
        // already open, and the alarms a page is owed all hang off the slot it stands in now.
        check("Place: a slot the scan is holding keeps its plugin, whatever the file recorded",
            ExtensionPlacement.Place(Names("alpha", "beta"), ["beta", null, null, null], Places(("alpha", 0), ("beta", 1))).SequenceEqual([1, 0]));
        check("Place: two folders recording one slot — the slot already held is not that one",
            ExtensionPlacement.Place(Names("alpha", "beta"), ["beta", null, null, null], Places(("alpha", 0), ("beta", 0))).SequenceEqual([1, 0]));

        // ── what the file cannot say ─────────────────────────────────────────────────────────────────────
        // A slot outside the four, and a row that never wrote one, both read as "no place recorded".
        check("Place: a recorded place outside the slots is not a place",
            ExtensionPlacement.Place(Names("alpha", "beta"), [null, null, null, null], Places(("alpha", 7), ("beta", -1))).SequenceEqual([0, 1]));
        // A folder that declares an id somebody else already declares is a mistake, not a second plugin: the scan's own
        // check reports it, and a place cannot be worked out twice for one id anyway.
        check("Place: a second folder declaring the same id gets no slot",
            ExtensionPlacement.Place(Names("alpha", "alpha", "beta"), [null, null, null, null], null).SequenceEqual([0, -1, 1]));
        // Four slots and five folders: the last one is told there was no room, rather than replacing a plugin.
        check("Place: a fifth folder is left without a slot",
            ExtensionPlacement.Place(Names("a", "b", "c", "d", "e"), [null, null, null, null], null).SequenceEqual([0, 1, 2, 3, -1]));
        // A folder whose recorded place is held by a plugin already standing there takes a free slot instead.
        check("Place: a recorded place somebody else holds gives way to a free slot",
            ExtensionPlacement.Place(Names("alpha", "beta"), [null, "beta", null, null], Places(("alpha", 1), ("beta", 1))).SequenceEqual([0, 1]));

        // ── the rows the file carries ────────────────────────────────────────────────────────────────────
        // What the scan wrote down is what the next start reads: a row made for a plugin that is not loaded keeps its
        // place for the folder's return, which is the same promise the page's size and its switch already make.
        var settings = new DeckSettings();
        check("Record: nothing written reads as no place recorded",
            settings.PlaceExtension("alpha", 0) && ExtensionPlacement.SavedPlaces(settings.Extensions)["alpha"] == 0);
        settings.PlaceExtension("beta", 2);
        check("Record: the same slot again changes nothing",
            !settings.PlaceExtension("alpha", 0) && settings.Extensions.Count == 2);
        settings.PlaceExtension("alpha", 3);
        check("Record: a plugin moved to another slot leaves one row with the new place",
            settings.Extensions.Count == 2 && settings.Extensions.Single(e => e.Id == "alpha").Slot == 3);
        // A row a reader wrote for a size or a value takes the place the scan records without losing what was there.
        var sized = new DeckSettings();
        sized.Extensions.Add(new ExtensionPageSettings("alpha") { Width = 640, Height = 220, Shown = false });
        sized.PlaceExtension("alpha", 1);
        check("Record: a size and a switch already on the row travel with the place",
            sized.Extensions.Single() is { Width: 640, Height: 220, Shown: false, Slot: 1 });
        // A hand-edited file can name any number; the load reads only the four that exist.
        var wild = new DeckSettings { Extensions = [new ExtensionPageSettings("alpha", Slot: 9), new ExtensionPageSettings("beta", Slot: -3)] };
        wild.Normalize();
        check("Normalize: a place outside the slots is read back as no place",
            wild.Extensions.Select(e => e.Slot).SequenceEqual([-1, -1]) && ExtensionPlacement.SavedPlaces(wild.Extensions).Count == 0);
        // The loop the reader lives through: three plugins arranged, one folder taken away, the app started again. The
        // two that stay come back in the slots they were arranged in, so the order that was chosen is still the order
        // the panel switches in — which is what a name-order scan could not promise.
        var arranged = new DeckSettings();
        arranged.PlaceExtension("alpha", 0); arranged.PlaceExtension("beta", 1); arranged.PlaceExtension("gamma", 2);
        arranged.PageOrder = [DeckPage.Extension1, DeckPage.Extension2, DeckPage.Extension0];
        arranged.Normalize();
        var seats = ExtensionPlacement.Place(Names("alpha", "gamma"), [null, null, null, null], ExtensionPlacement.SavedPlaces(arranged.Extensions));
        check("Restart: a plugin taken away leaves the other two in the slots the reader arranged",
            seats.SequenceEqual([0, 2]));
        var back = new string?[4];
        for (int at = 0; at < seats.Length; at++) if (seats[at] >= 0) back[seats[at]] = Names("alpha", "gamma")[at];
        using (Load(back))
            check("Restart: the tabs that stay are switched in the order the reader made, with the built-in three behind them",
                DeckPages.Arrangement(arranged).SequenceEqual([DeckPage.Extension1, DeckPage.Extension2, DeckPage.Extension0, DeckPage.Usage, DeckPage.Music, DeckPage.Monitor]));
    }
}
