namespace BrimDeck.Modules;

// Host adapter only. Never returned through a module's service context.
internal sealed class ModuleViewLease(Guid generation, CancellationToken lifetime, Func<bool> current,
    Func<IDisposable, IDisposable> own, Action<string, Action> invoke, Action<string, Exception> report)
{
    internal Guid Generation { get; } = generation;
    internal CancellationToken Lifetime { get; } = lifetime;
    internal bool Current => !Lifetime.IsCancellationRequested && current();
    internal IDisposable Own(IDisposable resource) => own(resource);
    internal void Invoke(string operation, Action callback) => invoke(operation, callback);
    internal void Report(string operation, Exception error) => report(operation, error);
}

public sealed partial class ModuleHost
{
    internal ModuleViewLease? CaptureView(string id)
    {
        Active? active; lock (_gate) active = _active.GetValueOrDefault(id);
        if (active is null || !active.Scope.Ready || !IsCurrent(active)) return null;
        return new(active.Generation, active.Scope.Token, () => IsCurrent(active) && active.Scope.Ready,
            resource => active.Scope.Own(resource),
            (operation, callback) => InvokeOwned(active, operation, () => { callback(); return true; }),
            (operation, error) => ReportOwned(active, operation, error));
    }
}
