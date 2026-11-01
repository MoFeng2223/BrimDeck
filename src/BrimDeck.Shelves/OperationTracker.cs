namespace DropShelf;

/// <summary>Tracks complete UI-thread operations, including result application, not only visible progress.</summary>
public sealed class OperationTracker
{
    private int _count;
    private TaskCompletionSource _idle = Completed();
    public bool Busy => _count > 0;
    public event Action? Changed;
    public Task WhenIdle => _idle.Task;
    public IDisposable Enter()
    {
        if (_count++ == 0) { _idle = new(TaskCreationOptions.RunContinuationsAsynchronously); Changed?.Invoke(); }
        return new Lease(this);
    }
    private void Leave()
    {
        if (--_count != 0) return;
        _idle.TrySetResult(); Changed?.Invoke();
    }
    private static TaskCompletionSource Completed() { var value = new TaskCompletionSource(); value.SetResult(); return value; }
    private sealed class Lease(OperationTracker owner) : IDisposable
    {
        private OperationTracker? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave();
    }
}
