namespace BrimDeck.Modules;

// Owned by the host. Readers receive immutable projections, never the host's stores or service instances.
public sealed class ModuleSnapshotSource<T>(T initial) where T : class
{
    private readonly object _gate = new();
    private T _snapshot = initial;
    private readonly Dictionary<long, Action<T>> _listeners = [];
    private long _next;
    public int SubscriberCount { get { lock (_gate) return _listeners.Count; } }
    public event Action<int>? DemandChanged;
    public T Read() { lock (_gate) return _snapshot; }
    public void Publish(T snapshot)
    {
        Action<T>[] listeners; lock (_gate) { _snapshot = snapshot; listeners = _listeners.Values.ToArray(); }
        foreach (var listener in listeners) listener(snapshot);
    }
    public IDisposable Subscribe(Action<T> listener)
    {
        long id; int count; lock (_gate) { id = ++_next; _listeners.Add(id, listener); count = _listeners.Count; }
        try { DemandChanged?.Invoke(count); }
        catch { lock (_gate) _listeners.Remove(id); throw; }
        return new ModuleAction(() => { lock (_gate) { if (!_listeners.Remove(id)) return; count = _listeners.Count; } DemandChanged?.Invoke(count); });
    }
}
public sealed class ModuleServiceScope
{
    private readonly Action _requireLive;
    private readonly Func<bool> _ready;
    private readonly Func<Func<IDisposable>, IDisposable> _ownSubscription;
    private readonly Action<string, Action> _invoke;
    private readonly Action<Action> _afterActivate;
    internal ModuleServiceScope(string id, CancellationToken lifetime, Action requireLive, Func<bool> ready, Func<Func<IDisposable>, IDisposable> ownSubscription,
        Action<string, Action> invoke, Action<Action> afterActivate)
    { ModuleId = id; Lifetime = lifetime; _requireLive = requireLive; _ready = ready; _ownSubscription = ownSubscription; _invoke = invoke; _afterActivate = afterActivate; }
    public string ModuleId { get; }
    public CancellationToken Lifetime { get; }
    public bool Ready => !Lifetime.IsCancellationRequested && _ready();
    public void RequireLive() => _requireLive();
    public T Read<T>(ModuleSnapshotSource<T> source) where T : class { RequireLive(); return source.Read(); }
    public IDisposable Subscribe<T>(ModuleSnapshotSource<T> source, Action<T> listener) where T : class
    {
        RequireLive(); ArgumentNullException.ThrowIfNull(listener);
        void Deliver(T value) { if (Ready) _invoke("host snapshot", () => listener(value)); }
        var lease = _ownSubscription(() => source.Subscribe(Deliver));
        // The reading in hand is handed over once, after this activation commits. A module then begins from a known value
        // instead of having to read it itself, and never gets the one the snapshot held while it was still starting.
        try { _afterActivate(() => Deliver(source.Read())); }
        catch { lease.Dispose(); throw; }
        return lease;
    }
    public IDisposable OnDispose(Action action) => _ownSubscription(() => new ModuleAction(action));
}
