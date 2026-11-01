namespace BrimDeck.Modules;

internal enum ModulePermissionChoice { Deny, Once, Remember }
internal sealed record ModulePermissionIdentity(string ModuleId, string Digest, string Capability, int RiskVersion);

// Only gates supported SDK operations. An in-process DLL can bypass the SDK; this is not a sandbox.
internal sealed class ModulePermissionGate(Func<ModulePermissionIdentity, bool> remembered,
    Func<ModulePermissionIdentity, CancellationToken, ValueTask<ModulePermissionChoice>> prompt,
    Action<ModulePermissionIdentity> remember)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _revisions = new(StringComparer.Ordinal);
    private readonly HashSet<ModulePermissionIdentity> _pending = [];
    internal void Revoke(string id) { lock (_gate) _revisions[id] = _revisions.GetValueOrDefault(id) + 1; }
    internal async ValueTask<bool> ExecuteAsync(ModulePermissionIdentity identity, CancellationToken lifetime, Action commit)
    {
        long revision;
        lock (_gate) { revision = _revisions.GetValueOrDefault(identity.ModuleId); if (lifetime.IsCancellationRequested) return false; }
        bool allowed = remembered(identity);
        if (!allowed)
        {
            lock (_gate) if (!_pending.Add(identity)) return false;
            ModulePermissionChoice choice;
            try { choice = await prompt(identity, lifetime); }
            finally { lock (_gate) _pending.Remove(identity); }
            lock (_gate)
            {
                if (lifetime.IsCancellationRequested || _revisions.GetValueOrDefault(identity.ModuleId) != revision || choice == ModulePermissionChoice.Deny) return false;
                if (choice == ModulePermissionChoice.Remember) remember(identity);
            }
            allowed = true;
        }
        lock (_gate)
        {
            if (!allowed || lifetime.IsCancellationRequested || _revisions.GetValueOrDefault(identity.ModuleId) != revision) return false;
            commit(); return true;
        }
    }
}
