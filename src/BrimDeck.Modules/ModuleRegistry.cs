namespace BrimDeck.Modules;

public sealed class ModuleRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(Type, string), object> _entries = [];
    private int _batch;
    private bool _dirty;
    public event Action? Changed;
    public IReadOnlyList<Contribution<T>> List<T>() where T : class
    { lock (_gate) return _entries.Where(pair => pair.Key.Item1 == typeof(T)).Select(pair => (Contribution<T>)pair.Value).ToArray(); }

    internal IDisposable Add<T>(string owner, string name, T value) where T : class
    {
        ModuleIds.Require(name);
        ArgumentNullException.ThrowIfNull(value);
        var entry = new Contribution<T>(owner + ":" + name, owner, value);
        var key = (typeof(T), entry.Id);
        lock (_gate)
        {
            if (!_entries.TryAdd(key, entry)) throw new InvalidOperationException("Duplicate contribution: " + entry.Id);
        }
        Notify();
        return new ModuleAction(() =>
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) return;
                _entries.Remove(key);
            }
            Notify();
        });
    }
    private void Notify()
    {
        lock (_gate) { if (_batch > 0) { _dirty = true; return; } }
        foreach (var listener in Changed?.GetInvocationList() ?? [])
            try { ((Action)listener)(); } catch { /* An observer cannot invalidate an accepted registration. */ }
    }
    internal void Batch(Action commit)
    {
        try { lock (_gate) { _batch++; try { commit(); } finally { _batch--; } } }
        finally
        {
            bool notify; lock (_gate) { notify = _batch == 0 && _dirty; if (notify) _dirty = false; }
            if (notify) Notify();
        }
    }
}

public static class ModuleIds
{
    public static bool Valid(string? id) => id is { Length: > 0 and <= 80 } && id[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && id.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    public static void Require(string id) { if (!Valid(id)) throw new ArgumentException("Module ids must be lowercase letters, digits or hyphens.", nameof(id)); }
}
