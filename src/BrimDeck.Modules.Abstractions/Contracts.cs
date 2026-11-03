namespace BrimDeck.Modules;
// Signature types live with the loader (ModuleSignature.cs) and are re-exported here for the module list.

public interface IModule
{
    ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken);
}

// A context belongs to one activation, never just to a module id.
public interface IModuleContext
{
    string ModuleId { get; }
    CancellationToken Lifetime { get; }
    IModuleStorage Storage { get; }
    T GetService<T>() where T : class;
    IDisposable Register<T>(string id, T contribution) where T : class;
    IDisposable Own(IDisposable resource);
    IDisposable OnDispose(Action cleanup);
    IDisposable Every(TimeSpan interval, Action callback);
    IDisposable Subscribe<T>(Action<T> listener);
    void Publish<T>(T message);
    void Report(string operation, Exception error);
}

public interface IModuleStorage
{
    T? Read<T>(string key);
    void Write<T>(string key, T value);
}

public interface IModuleScheduler
{
    IDisposable Every(TimeSpan interval, Action callback);
}

public sealed record ModuleSuspending : IModuleHostEvent;
public sealed record ModulePreferencesChanged : IModuleHostEvent;
public sealed record ModuleUiChanged : IModuleHostEvent;

public sealed record ModuleManifest
{
    public int ApiVersion { get; init; } = 1;
    public int ApiMinor { get; init; }
    public ModuleRequirements? Requires { get; init; }
    public string[] Capabilities { get; init; } = [];
    public string[] OptionalCapabilities { get; init; } = [];
    public string[] Permissions { get; init; } = [];
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = "";
    public string DescriptionEn { get; init; } = "";
    public string Author { get; init; } = "";
    public string? Assembly { get; init; }
    public string? EntryType { get; init; }
    public ModuleDependency[] Dependencies { get; init; } = [];
    // Application version range this module needs (not the contract version): internal assemblies only exist in
    // specific builds, so a module that uses them must pin the range.
    public ModuleHostRequirement? Host { get; init; }
}

public sealed record ModuleHostRequirement(string MinimumVersion, string? MaximumVersionExclusive = null);
public sealed record ModuleDependency(string Id, string MinimumVersion = "1.0.0")
{
    public string? MaximumVersionExclusive { get; init; }
}
public enum ModuleStatus { Disabled, Starting, Ready, Failed, DependencyFailed, Waiting, HostMismatch = 6 }
public sealed record ModuleInfo(string Id, string Name, string Version, ModuleStatus Status, string? Error, bool BuiltIn, string Revision)
{
    public Guid Generation { get; init; }
    public bool Slow { get; init; }
    public IReadOnlyList<ModuleDiagnostic> Diagnostics { get; init; } = Array.AsReadOnly(Array.Empty<ModuleDiagnostic>());
    public bool UsesInternals { get; init; }
    public string? HostRange { get; init; }
    public ModuleSignature? Signature { get; init; }
}
public enum ModuleDiagnosticSeverity { Information, Warning }
public sealed record ModuleDiagnostic(string Code, string Source, string Message, ModuleDiagnosticSeverity Severity = ModuleDiagnosticSeverity.Warning);
public sealed record ModuleFault(string ModuleId, string Operation, string Message, DateTimeOffset At)
{ public Guid Generation { get; init; } }
public sealed record Contribution<T>(string Id, string ModuleId, T Value) where T : class;

public sealed class ModuleAction(Action action) : IDisposable
{
    private Action? _action = action;
    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}
