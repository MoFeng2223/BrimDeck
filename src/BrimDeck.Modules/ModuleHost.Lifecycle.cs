using System.Diagnostics;

namespace BrimDeck.Modules;

public sealed partial class ModuleHost
{
    private sealed class Active
    {
        internal ModuleDefinition Definition { get; }
        internal ModuleScope Scope { get; }
        internal Guid Generation { get; } = Guid.NewGuid();
        internal TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Retired;
        internal volatile bool Slow;
        internal Active(ModuleHost host, ModuleDefinition definition)
        { Definition = definition; Scope = new((operation, error) => host.ReportOwned(this, operation, error)); }
    }
    private readonly HashSet<Guid> _retiring = [];
    private readonly HashSet<string> _maintenance = new(StringComparer.Ordinal);
    internal void ResumeAfterMaintenance(string id) { lock (_gate) _maintenance.Remove(id); }
    internal async Task StopForMaintenanceAsync(string id)
    {
        ModuleIds.Require(id);
        lock (_gate) _maintenance.Add(id); // Blocks already queued, stale configuration requests as well.
        await _serial.WaitAsync();
        try
        {
            Dictionary<string, Active> current; lock (_gate) current = new(_active);
            var stopping = new HashSet<string>(StringComparer.Ordinal) { id }; bool grew;
            do
            {
                grew = false;
                foreach (var pair in current)
                    if (pair.Value.Definition.Manifest.Dependencies.Any(d => stopping.Contains(d.Id))) grew |= stopping.Add(pair.Key);
            } while (grew);
            foreach (string module in DependencyOrder(current.ToDictionary(p => p.Key, p => p.Value.Definition)).Reverse())
                if (stopping.Contains(module)) { Retire(current[module]); State(current[module].Definition, ModuleStatus.Disabled); }
        }
        finally { _serial.Release(); }
    }
    private bool IsCurrent(Active active)
    { lock (_gate) return !_disposed && !active.Scope.Ended && ReferenceEquals(_active.GetValueOrDefault(active.Definition.Manifest.Id), active); }
    private void ReportOwned(Active active, string operation, Exception error)
    {
        lock (_gate)
            if (!ReferenceEquals(_active.GetValueOrDefault(active.Definition.Manifest.Id), active) && !_retiring.Contains(active.Generation)) return;
        Report(active.Definition.Manifest.Id, operation, error, active.Generation);
    }
    private T? InvokeOwned<T>(Active active, string operation, Func<T> run)
    {
        if (!IsCurrent(active) || !active.Scope.Ready) return default;
        try { return Measure(active.Definition.Manifest.Id, operation, run); }
        catch (Exception error) { ReportOwned(active, operation, error); return default; }
    }
    private T? InvokeOwned<T>(string id, ModuleScope scope, string operation, Func<T> run)
    {
        Active? active; lock (_gate) active = _active.GetValueOrDefault(id);
        return active is not null && ReferenceEquals(active.Scope, scope) ? InvokeOwned(active, operation, run) : default;
    }
    private bool State(Active active, ModuleStatus status, string? error = null)
    {
        var definition = active.Definition; string id = definition.Manifest.Id;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_active.GetValueOrDefault(id), active)) return false;
            _states[id] = new(id, definition.Manifest.Name, definition.Manifest.Version, status, error, definition.BuiltIn, definition.Revision)
            {
                Generation = active.Generation, Slow = active.Slow, UsesInternals = definition.UsesInternals, HostRange = definition.HostRange,
                Signature = definition.Signature
            };
            foreach (var warning in definition.Warnings)
                _diagnostics.TryAdd((id, active.Generation, warning.Source, warning.Code), warning);
        }
        Notify(); return true;
    }
    private void Retire(Active active)
    {
        if (Interlocked.Exchange(ref active.Retired, 1) != 0) return;
        string id = active.Definition.Manifest.Id;
        lock (_gate)
        {
            if (ReferenceEquals(_active.GetValueOrDefault(id), active)) _active.Remove(id);
            foreach (var key in _diagnostics.Keys.Where(k => k.Owner == id && k.Generation == active.Generation).ToArray()) _diagnostics.Remove(key);
            _retiring.Add(active.Generation);
        }
        active.Ready.TrySetResult(false);
        try
        {
            active.Scope.Dispose();
            try { active.Definition.Release?.Invoke(); } catch (Exception error) { ReportOwned(active, "unload", error); }
        }
        finally { lock (_gate) _retiring.Remove(active.Generation); }
    }

    // Serialize only changes to the activation graph. Async initialization never holds this lease.
    public async Task ReconcileAsync(IEnumerable<ModuleDefinition> definitions, CancellationToken cancellationToken = default)
    {
        var desired = new Dictionary<string, ModuleDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            ModuleIds.Require(definition.Manifest.Id);
            if (!desired.TryAdd(definition.Manifest.Id, definition)) throw new ArgumentException("Duplicate module id: " + definition.Manifest.Id);
        }
        var launch = new List<(Active Active, Active[] Dependencies, bool Valid)>();
        Task<bool>[] waiting;
        await _serial.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) foreach (string id in _maintenance) if (desired.TryGetValue(id, out var blocked)) desired[id] = blocked with { Enabled = false };
            Dictionary<string, Active> current; lock (_gate) current = new(_active);
            var replacing = current.Where(pair => !desired.TryGetValue(pair.Key, out var next) || !next.Enabled || next.Revision != pair.Value.Definition.Revision).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            bool grew;
            do
            {
                grew = false;
                foreach (var pair in current)
                    if (pair.Value.Definition.Manifest.Dependencies.Any(dep => replacing.Contains(dep.Id))) grew |= replacing.Add(pair.Key);
            } while (grew);
            foreach (string id in DependencyOrder(current.ToDictionary(p => p.Key, p => p.Value.Definition)).Reverse())
                if (replacing.Contains(id)) Retire(current[id]);
            lock (_gate) foreach (string id in _states.Keys.Except(desired.Keys).ToArray()) _states.Remove(id);

            var invalid = new Dictionary<string, Exception>();
            foreach (var pair in desired.Where(p => p.Value.Enabled))
            {
                try
                {
                    ModulePolicy.Validate(pair.Value.Manifest);
                    RequireCapabilities(pair.Value.Manifest.Capabilities.Concat(pair.Value.Manifest.Requires?.Capabilities ?? []));
                }
                catch (Exception error) { invalid[pair.Key] = error; }
            }
            var cycles = Cycles(desired, invalid.Keys.ToHashSet(StringComparer.Ordinal));
            foreach (var pair in desired)
            {
                var definition = pair.Value;
                if (!definition.Enabled) { State(definition, ModuleStatus.Disabled); continue; }
                if (definition.Rejection is { } rejection) { State(definition, ModuleStatus.HostMismatch, rejection); continue; }
                lock (_gate) if (_active.ContainsKey(pair.Key)) continue;
                if (invalid.TryGetValue(pair.Key, out var error))
                { State(definition, ModuleStatus.Failed, Describe(error)); Report(pair.Key, "validate contract", error); continue; }
                if (cycles.Contains(pair.Key)) { State(definition, ModuleStatus.DependencyFailed, "Dependency cycle: " + pair.Key); continue; }
                var active = new Active(this, definition);
                lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _active[pair.Key] = active; }
                State(active, definition.Manifest.Dependencies.Length > 0 ? ModuleStatus.Waiting : ModuleStatus.Starting);
                launch.Add((active, [], true));
            }
            // Capture dependency generations now; a later replacement cannot satisfy an older waiter.
            for (int index = 0; index < launch.Count; index++)
            {
                var active = launch[index].Active; var dependencies = new List<Active>(); bool valid = true;
                foreach (var dependency in active.Definition.Manifest.Dependencies)
                {
                    Active? target; lock (_gate) target = _active.GetValueOrDefault(dependency.Id);
                    if (target is null || !Compatible(target.Definition.Manifest.Version, dependency)) valid = false;
                    else dependencies.Add(target);
                }
                launch[index] = (active, dependencies.ToArray(), valid);
            }
            lock (_gate) waiting = desired.Keys.Select(id => _active.GetValueOrDefault(id)?.Ready.Task).OfType<Task<bool>>().ToArray();
        }
        finally { _serial.Release(); }
        foreach (var item in launch) _ = ActivateAsync(item.Active, item.Dependencies, item.Valid, cancellationToken);
        await Task.WhenAll(waiting).WaitAsync(cancellationToken);
    }

    private async Task ActivateAsync(Active active, Active[] dependencies, bool valid, CancellationToken cancellationToken)
    {
        Task? start = null; long started = 0;
        using var slowWatch = CancellationTokenSource.CreateLinkedTokenSource(active.Scope.Token, cancellationToken);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, active.Scope.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!valid || !await DependenciesReadyAsync(dependencies, linked.Token)
                || dependencies.Any(d => !IsCurrent(d) || !d.Scope.Ready))
            {
                State(active, ModuleStatus.DependencyFailed, "A dependency is missing, disabled, incompatible or failed."); return;
            }
            if (!IsCurrent(active)) return;
            if (!State(active, ModuleStatus.Starting) || !IsCurrent(active)) return;
            linked.Token.ThrowIfCancellationRequested(); started = Stopwatch.GetTimestamp();
            _ = WatchSlowStartAsync(active, slowWatch.Token);
            var module = active.Definition.Create();
            if (module is IDisposable disposable) active.Scope.Own(disposable);
            linked.Token.ThrowIfCancellationRequested();
            string id = active.Definition.Manifest.Id;
            var context = new Context(this, id, active.Scope, new ModuleStorage(Path.Combine(_dataDirectory, id)), active.Definition.Manifest, active.Definition.BuiltIn, active);
            start = module.ActivateAsync(context, active.Scope.Token).AsTask();
            await start.WaitAsync(StartTimeout, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(active)) return;
            context.Commit(); active.Scope.Activate();
            if (SlowStartThreshold != Timeout.InfiniteTimeSpan && Stopwatch.GetElapsedTime(started) >= SlowStartThreshold) active.Slow = true;
            if (!State(active, ModuleStatus.Ready)) return;
            _performance.Started(id, Stopwatch.GetTimestamp() - started); started = 0;
            active.Ready.TrySetResult(true);
        }
        catch (Exception error)
        {
            bool cancelled = error is OperationCanceledException && (active.Scope.Token.IsCancellationRequested || cancellationToken.IsCancellationRequested);
            if (IsCurrent(active))
            {
                if (started != 0) _performance.Started(active.Definition.Manifest.Id, Stopwatch.GetTimestamp() - started);
                State(active, cancelled ? ModuleStatus.Disabled : ModuleStatus.Failed, cancelled ? null : Describe(error));
                if (!cancelled) ReportOwned(active, "activate", error);
            }
            if (start is not null) _ = start.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
        finally { slowWatch.Cancel(); if (!active.Ready.Task.IsCompletedSuccessfully || !active.Ready.Task.Result) Retire(active); }
    }

    private async Task WatchSlowStartAsync(Active active, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SlowStartThreshold, cancellationToken);
            string id = active.Definition.Manifest.Id;
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_active.GetValueOrDefault(id), active) || _states.GetValueOrDefault(id)?.Status != ModuleStatus.Starting) return;
                active.Slow = true; _states[id] = _states[id] with { Slow = true };
            }
            Notify();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static async Task<bool> DependenciesReadyAsync(Active[] dependencies, CancellationToken cancellationToken)
    {
        var pending = dependencies.Select(d => d.Ready.Task).Distinct().ToList();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).WaitAsync(cancellationToken);
            if (!await completed) return false;
            pending.Remove(completed);
        }
        return true;
    }

    private static bool Compatible(string version, ModuleDependency dependency) => Version.TryParse(version, out var installed)
        && Version.TryParse(dependency.MinimumVersion, out var minimum) && installed >= minimum
        && (dependency.MaximumVersionExclusive is null || Version.TryParse(dependency.MaximumVersionExclusive, out var maximum) && installed < maximum);
    private static IReadOnlyList<string> DependencyOrder(IReadOnlyDictionary<string, ModuleDefinition> definitions)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal); var ordered = new List<string>();
        void Visit(string id)
        {
            if (!visited.Add(id) || !definitions.TryGetValue(id, out var definition)) return;
            foreach (var dependency in definition.Manifest.Dependencies) if (definitions.ContainsKey(dependency.Id)) Visit(dependency.Id);
            ordered.Add(id);
        }
        foreach (string id in definitions.Keys) Visit(id); return ordered;
    }
    private static HashSet<string> Cycles(IReadOnlyDictionary<string, ModuleDefinition> definitions, HashSet<string> invalid)
    {
        var color = new Dictionary<string, int>(StringComparer.Ordinal); var stack = new List<string>(); var cycles = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (!definitions.TryGetValue(id, out var definition) || !definition.Enabled || invalid.Contains(id)) return;
            if (color.TryGetValue(id, out int state))
            { if (state == 1) foreach (string member in stack.Skip(stack.IndexOf(id))) cycles.Add(member); return; }
            color[id] = 1; stack.Add(id);
            foreach (var dependency in definition.Manifest.Dependencies) Visit(dependency.Id);
            stack.RemoveAt(stack.Count - 1); color[id] = 2;
        }
        foreach (string id in definitions.Keys) Visit(id); return cycles;
    }
    public void Dispose()
    {
        Active[] active;
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            var snapshot = new Dictionary<string, Active>(_active);
            active = DependencyOrder(snapshot.ToDictionary(p => p.Key, p => p.Value.Definition)).Reverse().Select(id => snapshot[id]).ToArray();
        }
        foreach (var item in active) Retire(item);
    }
}
