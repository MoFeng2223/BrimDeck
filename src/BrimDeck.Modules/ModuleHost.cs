using System.Diagnostics;

namespace BrimDeck.Modules;

public sealed record ModuleDefinition(ModuleManifest Manifest, string Revision, bool Enabled, Func<IModule> Create, bool BuiltIn = false, Action? Release = null)
{
    // Configuration diagnostics resolved at discovery (renamed capabilities and the like), shown with the module.
    public IReadOnlyList<ModuleDiagnostic> Warnings { get; init; } = [];
    public bool UsesInternals { get; init; }
    public string? HostRange { get; init; }
    // Set when the application version is outside the manifest's range: the module is listed but never activated.
    public string? Rejection { get; init; }
    public ModuleSignature? Signature { get; init; }
}

public sealed partial class ModuleHost : IDisposable
{
    private readonly Dictionary<string, Active> _active = [];
    private readonly Dictionary<string, ModuleInfo> _states = [];
    private readonly Dictionary<Type, object> _services;
    private readonly Dictionary<Type, ModuleKind> _kinds;
    private readonly Dictionary<Type, ModuleService> _serviceBindings;
    private readonly IReadOnlyList<string> _availableCapabilities;
    private readonly IReadOnlyList<ModuleCapability> _capabilityDetails;
    public string ApplicationVersion { get; init; } = "development";
    private sealed class EventListener(ModuleScope scope, string owner, Delegate callback, ModuleEventPriority priority)
    {
        internal readonly ModuleScope Scope = scope;
        internal readonly string Owner = owner;
        internal readonly Delegate Callback = callback;
        internal readonly ModuleEventPriority Priority = priority;
        internal volatile bool Live = true;
    }
    private readonly Dictionary<Type, List<EventListener>> _listeners = [];
    private readonly List<ModuleFault> _faults = [];
    private readonly Dictionary<(string Owner, Guid Generation, string Source, string Code), ModuleDiagnostic> _diagnostics = [];
    private readonly IModuleScheduler _scheduler;
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private volatile bool _disposed;
    public ModuleRegistry Registry { get; } = new();
    public event Action? Changed;
    public IReadOnlyList<ModuleInfo> Modules { get { lock (_gate) return _states.Values.OrderBy(x => x.Id).Select(x => x with
        { Diagnostics = Array.AsReadOnly(_diagnostics.Where(d => d.Key.Owner == x.Id && d.Key.Generation == x.Generation).Select(d => d.Value).ToArray()) }).ToArray(); } }
    internal void Diagnose(string id, string source, string code, string message)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(id, out var active) || !active.Scope.Ready) return;
            var key = (id, active.Generation, source, code);
            if (_diagnostics.ContainsKey(key)) return;
            if (_diagnostics.Count >= 200)
            {
                // Trim this module's oldest first, so a noisy module cannot evict another module's diagnostics.
                var victim = _diagnostics.Keys.FirstOrDefault(key => key.Owner == id);
                if (victim.Owner is null) victim = _diagnostics.Keys.First();
                _diagnostics.Remove(victim);
            }
            _diagnostics.Add(key, new(code, source, message));
        }
    }
    public IReadOnlyList<ModuleFault> Faults { get { lock (_gate) return _faults.ToArray(); } }
    public TimeSpan StartTimeout { get; init; } = Timeout.InfiniteTimeSpan;
    public TimeSpan SlowStartThreshold { get; init; } = TimeSpan.FromSeconds(5);

    public ModuleHost(string dataDirectory, IModuleScheduler scheduler, IEnumerable<object>? services = null)
        : this(dataDirectory, scheduler, services, [], []) { }
    public ModuleHost(string dataDirectory, IModuleScheduler scheduler, IEnumerable<object>? services, IEnumerable<ModuleKind> kinds, IEnumerable<ModuleService> serviceBindings)
    {
        _dataDirectory = dataDirectory; _scheduler = scheduler; _services = [];
        foreach (var service in services ?? [])
            foreach (var type in service.GetType().GetInterfaces().Where(t => t.IsVisible)) _services.TryAdd(type, service);
        var contributions = kinds.ToArray(); var bindings = serviceBindings.ToArray();
        ModulePolicy.ValidateBindings(contributions, bindings);
        _kinds = contributions.ToDictionary(k => k.Contract);
        _serviceBindings = bindings.ToDictionary(s => s.Contract);
        var available = contributions.Select(k => k.Id).Concat(bindings.Select(b => b.Capability!)).Concat(ModuleApi.Catalog.Where(c => c.Kind == ModuleCapabilityKind.Intrinsic).Select(c => c.Id)).ToHashSet(StringComparer.Ordinal);
        _capabilityDetails = Array.AsReadOnly(ModuleApi.Catalog.Where(c => available.Contains(c.Id)).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray());
        _availableCapabilities = Array.AsReadOnly(_capabilityDetails.Select(c => c.Id).ToArray());
    }
    public IReadOnlyList<string> AvailableCapabilities => _availableCapabilities;
    public void RequireCapabilities(IEnumerable<string> capabilities)
    {
        foreach (string id in capabilities)
            if (!_availableCapabilities.Contains(id)) throw new ModuleApiException("capability-unavailable", "Host adapter is missing: " + id);
    }
    public void Report(string id, string operation, Exception error) => Report(id, operation, error, Guid.Empty);
    private void Report(string id, string operation, Exception error, Guid generation)
    {
        _performance.Error(id);
        lock (_gate)
        {
            if (_faults.Count == 200) _faults.RemoveAt(0);
            _faults.Add(new(id, operation, Describe(error), DateTimeOffset.UtcNow) { Generation = generation });
        }
    }
    public T? Invoke<T>(string id, string operation, Func<T> run)
    { try { return Measure(id, operation, run); } catch (Exception ex) { Report(id, operation, ex); return default; } }
    public IDisposable Own(string id, IDisposable resource)
    {
        ModuleScope? scope; lock (_gate) scope = _active.GetValueOrDefault(id)?.Scope;
        if (scope is not null) return scope.Own(resource);
        try { resource.Dispose(); } catch (Exception ex) { Report(id, "dispose retired view", ex); }
        return new ModuleAction(() => { });
    }
    private static string Describe(Exception error)
    { try { return error.Message; } catch { return "Module exception details could not be read."; } }
    public void Publish<T>(T message)
    {
        ArgumentNullException.ThrowIfNull(message);
        EventListener[] list;
        lock (_gate) list = _listeners.GetValueOrDefault(typeof(T))?.OrderBy(e => e.Priority).ToArray() ?? [];
        foreach (var entry in list)
        {
            if (!entry.Live || !entry.Scope.Ready) continue;
            InvokeOwned(entry.Owner, entry.Scope, "event " + typeof(T).Name, () => { if (entry.Live) ((Action<T>)entry.Callback)(message); return true; });
        }
    }
    private IDisposable Listen<T>(string owner, ModuleScope scope, Action<T> listener, ModuleEventPriority priority)
    {
        var entry = new EventListener(scope, owner, listener, priority);
        lock (_gate)
        {
            if (!_listeners.TryGetValue(typeof(T), out var list)) _listeners[typeof(T)] = list = [];
            list.Add(entry);
        }
        return scope.Own(new ModuleAction(() =>
        {
            entry.Live = false;
            lock (_gate)
                if (_listeners.TryGetValue(typeof(T), out var list)) { list.Remove(entry); if (list.Count == 0) _listeners.Remove(typeof(T)); }
        }));
    }
    private void State(ModuleDefinition d, ModuleStatus status, string? error = null, Guid generation = default)
    {
        lock (_gate) _states[d.Manifest.Id] = new(d.Manifest.Id, d.Manifest.Name, d.Manifest.Version, status, error, d.BuiltIn, d.Revision)
        {
            Generation = generation, UsesInternals = d.UsesInternals, HostRange = d.HostRange, Signature = d.Signature
        };
        Notify();
    }
    private void Notify()
    { foreach (var listener in Changed?.GetInvocationList() ?? []) try { ((Action)listener)(); } catch { } }

    private sealed class Context(ModuleHost host, string id, ModuleScope scope, IModuleStorage storage, ModuleManifest manifest, bool builtIn, Active activation) : IModulePlatform, IModuleRegistries, IModuleEvents, IModuleServices
    {
        public IModuleRegistries Registries => this;
        public IModuleEvents Events => this;
        public IModuleServices Services => this;
        T IModuleServices.Get<T>() => GetService<T>();
        private sealed class Registration(string id, IDisposable lease) : IModuleRegistration
        { public string Id => id; public void Dispose() => lease.Dispose(); }
        IModuleRegistration IModuleRegistries.Register<T>(string name, T value) => new Registration(id + ":" + name, Register(name, value));
        private readonly List<StagedRegistration> _pending = [];
        private bool _committed;
        private readonly object _registrationGate = new();
        private readonly Dictionary<Type, object> _boundServices = [];
        public string ModuleId => id;
        public CancellationToken Lifetime => scope.Token;
        private readonly IModuleStorage _storage = new OwnedStorage(storage, scope);
        public IModuleStorage Storage => _storage;
        private void RequireLive() => ObjectDisposedException.ThrowIf(scope.Ended || scope.Token.IsCancellationRequested, this);
        public T GetService<T>() where T : class
        {
            RequireLive();
            if (typeof(T) == typeof(IModuleHostInfo)) return (T)(object)new HostInfo(host, RequireLive);
            if (host._serviceBindings.TryGetValue(typeof(T), out var binding))
            {
                if (binding.Capability is { } cap && !ModulePolicy.Declares(manifest, cap)) throw new ModuleApiException("capability-not-declared", "Declare capability " + cap);
                if (binding.Permission is { } permission && !manifest.Permissions.Contains(permission)) throw new ModuleApiException("permission-denied", "Permission required: " + permission);
                lock (_registrationGate)
                {
                    if (!_boundServices.TryGetValue(typeof(T), out var service))
                    {
                        var serviceScope = new ModuleServiceScope(id, scope.Token, RequireLive, () => scope.Ready, Stage, (operation, callback) => host.InvokeOwned(activation, operation, () => { callback(); return true; }));
                        _boundServices[typeof(T)] = service = binding.Create(serviceScope);
                    }
                    return (T)service;
                }
            }
            return builtIn && host._services.TryGetValue(typeof(T), out var value) ? (T)value : throw new ModuleApiException("service-unavailable", "Unavailable host service: " + typeof(T).Name);
        }
        public IDisposable Register<T>(string name, T value) where T : class
        {
            RequireLive(); ModuleIds.Require(name); ArgumentNullException.ThrowIfNull(value);
            if (!host._kinds.TryGetValue(typeof(T), out var kind)) throw new ModuleApiException("unknown-contribution", "Unsupported contribution: " + typeof(T).Name);
            if (manifest.ApiMinor >= 1 && !ModulePolicy.Declares(manifest, kind.Id)) throw new ModuleApiException("capability-not-declared", "Declare capability " + kind.Id);
            kind.Validate?.Invoke(value);
            kind.ValidateOwner?.Invoke(id, value);
            return Stage(() => host.Registry.Add(id, name, value));
        }
        private IDisposable Stage(Func<IDisposable> create)
        {
            RequireLive(); var registration = new StagedRegistration(create);
            var lease = scope.Own(registration);
            try { lock (_registrationGate) { if (_committed) registration.Commit(); else _pending.Add(registration); } }
            catch { lease.Dispose(); throw; }
            return lease;
        }
        internal void Commit()
        {
            RequireLive();
            lock (_registrationGate)
            {
                host.Registry.Batch(() =>
                {
                    try { foreach (var registration in _pending) registration.Commit(); _committed = true; }
                    catch { foreach (var registration in _pending) registration.Dispose(); throw; }
                    finally { _pending.Clear(); }
                });
            }
        }
        public IDisposable Own(IDisposable resource) => scope.Own(resource);
        public IDisposable OnDispose(Action cleanup) => Own(new ModuleAction(cleanup));
        public IDisposable Every(TimeSpan interval, Action callback)
        {
            RequireLive();
            if (interval < TimeSpan.FromMilliseconds(16) || interval > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(interval));
            ArgumentNullException.ThrowIfNull(callback);
            return Stage(() => host._scheduler.Every(interval, () => { if (scope.Ready) host.InvokeOwned(activation, "timer", () => { callback(); return true; }); }));
        }
        public IDisposable Subscribe<T>(Action<T> listener) => On(listener);
        public IDisposable On<T>(Action<T> listener, ModuleEventPriority priority = ModuleEventPriority.Normal)
        {
            RequireLive(); ArgumentNullException.ThrowIfNull(listener);
            if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
            return Stage(() => host.Listen(id, scope, listener, priority));
        }
        public void Publish<T>(T message)
        {
            RequireLive();
            if (message is IModuleHostEvent || typeof(IModuleHostEvent).IsAssignableFrom(typeof(T))) throw new ModuleApiException("host-event-publish-denied", "Host lifecycle events may only be published by the host.");
            if (!scope.Ready) throw new ModuleApiException("module-not-ready", "The module has not committed activation.");
            host.Publish(message);
        }
        public void Report(string operation, Exception error) => host.ReportOwned(activation, operation, error);
    }
    private sealed class StagedRegistration(Func<IDisposable> register) : IDisposable
    {
        private IDisposable? _live;
        private bool _ended;
        private readonly object _gate = new();
        public void Commit()
        {
            lock (_gate)
            {
                if (_ended || _live is not null) return;
                var live = register();
                if (_ended) live.Dispose(); else _live = live;
            }
        }
        public void Dispose() { lock (_gate) { _ended = true; _live?.Dispose(); _live = null; } }
    }
    private sealed class HostInfo(ModuleHost host, Action requireLive) : IModuleHostInfo
    { public ModuleHostInfo GetInfo() { requireLive(); return new(host.ApplicationVersion, ModuleApi.Major, ModuleApi.Minor, host.AvailableCapabilities) { CapabilityDetails = host._capabilityDetails }; } }
    private sealed class OwnedStorage(IModuleStorage storage, ModuleScope scope) : IModuleStorage, IModuleStructuredStorage
    {
        public T? Read<T>(string key) => scope.UseStorage(() => storage.Read<T>(key));
        public void Write<T>(string key, T value) => scope.UseStorage(() => { storage.Write(key, value); return true; });
        public ModuleReadResult<T> ReadResult<T>(string key) => scope.UseStorage(() => storage.ReadResult<T>(key));
    }
}
