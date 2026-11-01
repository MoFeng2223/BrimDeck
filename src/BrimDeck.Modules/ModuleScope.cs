namespace BrimDeck.Modules;

internal sealed class ModuleScope(Action<string, Exception> fault) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, IDisposable> _resources = [];
    private readonly CancellationTokenSource _lifetime = new();
    private long _next;
    public CancellationToken Token => _lifetime.Token;
    private volatile bool _ended;
    public bool Ended => _ended;
    private volatile bool _released;
    public bool Released => _released;
    private volatile bool _ready;
    public bool Ready => _ready && !_ended && !Token.IsCancellationRequested;
    internal void Activate() => _ready = true;
    internal T UseStorage<T>(Func<T> operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            return operation();
        }
    }

    public IDisposable Own(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        long key;
        lock (_gate)
        {
            key = ++_next;
            if (!Ended)
            {
                _resources.Add(key, resource);
                return new ModuleAction(() => Release(key));
            }
        }
        SafeDispose(resource);
        return new ModuleAction(() => { });
    }
    private void Release(long key)
    {
        IDisposable? resource;
        lock (_gate) { if (!_resources.Remove(key, out resource)) return; }
        SafeDispose(resource);
    }
    private void SafeDispose(IDisposable resource)
    { try { resource.Dispose(); } catch (Exception ex) { fault("dispose", ex); } }
    public void CancelActivation()
    { try { _lifetime.Cancel(); } catch (Exception ex) { fault("cancel", ex); } }
    public void Dispose()
    {
        IDisposable[] resources;
        lock (_gate)
        {
            if (Ended) return;
            _ended = true;
            resources = _resources.Reverse().Select(pair => pair.Value).ToArray();
            _resources.Clear();
        }
        CancelActivation();
        foreach (var resource in resources) SafeDispose(resource);
        lock (_gate) _released = true;
        // Keep the cancelled token readable by a late activation continuation.
    }
}
