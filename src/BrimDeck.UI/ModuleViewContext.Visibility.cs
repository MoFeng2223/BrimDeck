using BrimDeck.Modules;

namespace BrimDeck;

public sealed partial class ModuleViewContext
{
    private readonly Dictionary<long, Action<bool>> _visibilityListeners = [];
    private volatile bool _visible;
    public bool IsVisible => _visible && !_ended && !_lifetime.IsCancellationRequested;
    internal Action<Action>? RunVisibilityCallback { get; set; }

    public IDisposable OnVisibilityChanged(Action<bool> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (!Owner.Dispatcher.CheckAccess()) throw new ModuleApiException("ui-thread-required", "View subscriptions belong to the UI thread.");
        long id;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_ended || _lifetime.IsCancellationRequested, this);
            id = ++_next; _visibilityListeners.Add(id, listener);
        }
        void Remove() { lock (_gate) _visibilityListeners.Remove(id); }
        var lease = OnDispose(Remove);
        return new ModuleAction(() => { Remove(); lease.Dispose(); });
    }

    // Opt-in presentation timer. Existing Every retains its mount-lifetime semantics.
    public IDisposable EveryVisible(TimeSpan interval, Action callback)
    {
        if (!Owner.Dispatcher.CheckAccess()) throw new ModuleApiException("ui-thread-required", "View timers belong to the UI thread.");
        ObjectDisposedException.ThrowIf(_ended || _lifetime.IsCancellationRequested, this);
        if (interval < TimeSpan.FromMilliseconds(16) || interval > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(interval));
        ArgumentNullException.ThrowIfNull(callback);
        bool stopped = false;
        var timer = new PromptTimer(Owner.Dispatcher, () =>
        {
            if (stopped || !IsVisible) return;
            try { if (RunCallback is { } run) run(callback); else callback(); } catch (Exception error) { Fault?.Invoke(error); }
        }) { Interval = interval };
        var timerLease = OnDispose(() => { stopped = true; timer.Stop(); });
        var visibleLease = OnVisibilityChanged(visible => { if (visible && !stopped) timer.Start(); else timer.Stop(); });
        if (IsVisible) timer.Start();
        return new ModuleAction(() => { visibleLease.Dispose(); timerLease.Dispose(); });
    }

    internal void SetVisible(bool value)
    {
        Owner.Dispatcher.VerifyAccess();
        value = value && (!_attached || _ready) && !_ended && !_lifetime.IsCancellationRequested;
        if (_visible == value) return;
        _visible = value; NotifyVisibility(value);
    }
    private void NotifyVisibility(bool value)
    {
        KeyValuePair<long, Action<bool>>[] listeners;
        lock (_gate) listeners = _visibilityListeners.ToArray();
        foreach (var listener in listeners)
        {
            lock (_gate) if (_visible != value || !_visibilityListeners.ContainsKey(listener.Key)) continue;
            try { if (RunVisibilityCallback is { } run) run(() => listener.Value(value)); else listener.Value(value); }
            catch (Exception error) { Fault?.Invoke(error); }
        }
    }
    private void EndVisibility()
    {
        if (!_visible) return;
        _visible = false;
        // Normal view release runs on the owner dispatcher; cancellation from other threads still revokes IsVisible immediately.
        if (Owner.Dispatcher.CheckAccess()) NotifyVisibility(false);
    }
}
