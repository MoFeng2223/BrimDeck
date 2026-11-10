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

    // The lifetime of the running activation of a module, or a token that is already cancelled when it is not running. A
    // command's work ends when its module stops, so a command still in flight cannot outlive the activation that owns it.
    internal CancellationToken CommandLifetime(string id)
    {
        Active? active; lock (_gate) active = _active.GetValueOrDefault(id);
        return active is not null && active.Scope.Ready ? active.Scope.Token : new CancellationToken(canceled: true);
    }

    // The manifest of a module's running activation: a host service bound to it answers from the capabilities it declared.
    // Null once the activation has ended, so a retained service cannot act on a stale manifest.
    internal ModuleManifest? ManifestOf(string id)
    {
        Active? active; lock (_gate) active = _active.GetValueOrDefault(id);
        return active is not null && active.Scope.Ready ? active.Definition.Manifest : null;
    }
}
