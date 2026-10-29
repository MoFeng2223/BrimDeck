namespace BrimDeck.Core;

// Which slot each plugin folder gets. What the reader arranges (DeckSettings.PageOrder) and what a page's own row holds
// are written in slots, so a folder has to come back to the slot it was in: placed by name order alone, taking away one
// plugin shifts the rest left and moves the tabs the reader put where they wanted them.
public static class ExtensionPlacement
{
    public const int SlotCount = 4;

    // `ids` are the folders a scan read, in the order it read them. `live` answers, per slot, which plugin the scan is
    // holding there and must not move — including a folder whose files merely changed, which keeps its place all the
    // same. `saved` is what the file records for each id, and a row with no recorded place reads the same as no row.
    // A recorded place somebody else holds is not a place: that plugin takes a free slot like a new one. Nothing is
    // left over when all the slots are gone, and the caller says so to the reader.
    public static int[] Place(IReadOnlyList<string> ids, IReadOnlyList<string?> live, IReadOnlyDictionary<string, int>? saved)
    {
        var slots = new string?[SlotCount];
        var held = new HashSet<string>(StringComparer.Ordinal);
        for (int at = 0; at < SlotCount && at < live.Count; at++)
            if (live[at] is { Length: > 0 } keeps && held.Add(keeps)) slots[at] = keeps;

        // The recorded places are filled before anything else, so a cold start puts every known plugin back where the
        // reader last saw it. Two rows naming one slot: the folder that comes first in the scan wins it.
        for (int at = 0; at < ids.Count; at++)
        {
            string id = ids[at];
            if (id.Length == 0 || held.Contains(id)) continue;
            if (saved is not null && saved.TryGetValue(id, out int want) && want >= 0 && want < SlotCount && slots[want] is null)
            { slots[want] = id; held.Add(id); }
        }
        // A folder installed after the file was written, one whose recorded slot is taken, and one that never had a slot
        // go to the first free slot — in the order the folders were read, which is the name order the scan keeps.
        for (int at = 0; at < ids.Count; at++)
        {
            string id = ids[at];
            if (id.Length == 0 || held.Contains(id)) continue;
            if (Array.IndexOf(slots, null) is var free and >= 0) { slots[free] = id; held.Add(id); }
        }

        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int at = 0; at < SlotCount; at++) if (slots[at] is { } sitting) found[sitting] = at;
        var answer = new int[ids.Count];
        var named = new HashSet<string>(StringComparer.Ordinal);
        // Read back in the order the folders came, so the answer lines up with `ids`. A second folder declaring an id
        // somebody else already answers with gets nothing: the id is one plugin, not two.
        for (int at = 0; at < ids.Count; at++) answer[at] = named.Add(ids[at]) && found.TryGetValue(ids[at], out int slot) ? slot : -1;
        return answer;
    }

    // What the file records, without the rows that never wrote a place. Kept out of the callers so a hand-edited file
    // with a slot outside the four — or any other value — reads as no place rather than as an index.
    public static Dictionary<string, int> SavedPlaces(IEnumerable<ExtensionPageSettings> entries)
        => entries.Where(entry => entry.Slot >= 0 && entry.Slot < SlotCount && entry.Id.Length > 0)
                  .GroupBy(entry => entry.Id, StringComparer.Ordinal)
                  .ToDictionary(group => group.Key, group => group.First().Slot, StringComparer.Ordinal);
}
