using System.Linq;

namespace BrimDeck.Core;

// The alarms one plugin has registered, and which of its pages have already heard each of them.
//
// This lives apart from the pages themselves because the rules are pure: what a page is owed depends only on when an
// alarm came due, which pages exist, and which of them have been told. Kept here, they are testable without a browser —
// which matters, because the case that goes wrong is the one no single-window test can reach: a plugin shown on two
// displays at once is two pages, and an alarm that only one of them heard is a notification the other display silently
// lacks.
public sealed class AlarmBook
{
    // A fired alarm nobody came for is not kept for good. A page that has not loaded after this long is not coming, and
    // holding its alarm would use up one of the plugin's slots until the app restarts.
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    private sealed class Entry(DateTime due)
    {
        public DateTime Due { get; } = due;
        public bool Fired { get; set; }
        // Which pages have been told. A page is its own identity: two displays of one plugin are two pages.
        public HashSet<object> Delivered { get; } = new();
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    // Whether anything is still waiting for its moment. An alarm already fired and held for a page costs no waking:
    // that page's hello hands it over (Hear), so there is nothing left to decide on a tick.
    public bool HasPending => _entries.Values.Any(entry => !entry.Fired);

    // Whether something has come due and is waiting for a page to hear it.
    public bool HasFired => _entries.Values.Any(entry => entry.Fired);

    public bool Has(string id) => _entries.ContainsKey(id);

    // Registers or moves an alarm. The caller owns the ceilings and the "is this time reachable" question.
    public void Set(string id, DateTime due) => _entries[id] = new Entry(due);

    public void Clear(string id) => _entries.Remove(id);

    // One page finishing its load: what it has not heard yet, which it is about to be told. This is the path a page
    // that was reclaimed takes — the alarm came due while its document was blank, and the host held it.
    public IReadOnlyList<string> Hear(object page, IReadOnlyCollection<object> pages, DateTime now)
    {
        Sweep(pages, now);
        var owed = _entries.Where(entry => entry.Value.Fired && !entry.Value.Delivered.Contains(page))
            .Select(entry => entry.Key).ToList();
        foreach (var id in owed) _entries[id].Delivered.Add(page);
        Retire(pages);
        return owed;
    }

    // Every page that is here, told about every alarm that has come due. The pairs are what to send where.
    public IReadOnlyList<(object Page, string Alarm)> Deliver(IReadOnlyCollection<object> pages, DateTime now)
    {
        Sweep(pages, now);
        var owed = new List<(object, string)>();
        if (pages.Count == 0) return owed;
        foreach (var (id, entry) in _entries)
            if (entry.Fired)
                foreach (var page in pages)
                    if (entry.Delivered.Add(page)) owed.Add((page, id));
        Retire(pages);
        return owed;
    }

    // Marks what has come due and drops what has waited too long, without delivering anything: a plugin whose pages
    // cannot hear right now (all reclaimed) still has to have its alarms move from "later" to "owed".
    public void Sweep(IReadOnlyCollection<object> pages, DateTime now)
    {
        // A page that went away is not a page still owed this. Its mark would otherwise make an alarm look delivered to
        // a page that no longer exists, and the pages that remain would never be counted complete.
        foreach (var entry in _entries.Values) entry.Delivered.IntersectWith(pages);
        foreach (var (id, entry) in _entries.ToList())
        {
            if (!entry.Fired && now >= entry.Due) entry.Fired = true;
            if (entry.Fired && now - entry.Due > Retention) _entries.Remove(id);
        }
    }

    // An alarm is done once every page that exists has heard it: one display hearing it is not the other display's
    // answer, which is the whole reason delivery is counted per page.
    private void Retire(IReadOnlyCollection<object> pages)
    {
        foreach (var (id, entry) in _entries.ToList())
            if (entry.Fired && pages.Count > 0 && pages.All(entry.Delivered.Contains)) _entries.Remove(id);
    }
}
