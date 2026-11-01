namespace BrimDeck.Modules;

public sealed record ModuleKind(Type Contract, string Id, Action<object>? Validate = null)
{
    public Action<string, object>? ValidateOwner { get; init; }
    public static ModuleKind Of<T>(string id, Action<T>? validate = null) where T : class => new(typeof(T), id, value => validate?.Invoke((T)value));
}
public sealed record ModuleService(Type Contract, string? Capability, string? Permission, Func<ModuleServiceScope, object> Create)
{
    public static ModuleService Of<T>(string capability, Func<ModuleServiceScope, T> create) where T : class
    {
        var descriptor = ModuleApi.Describe(capability) ?? throw new ModuleApiException("unknown-capability", "Unknown host capability: " + capability);
        return Of(capability, descriptor.Permission, create);
    }
    public static ModuleService Of<T>(string? capability, string? permission, Func<ModuleServiceScope, T> create) where T : class
    { if (!typeof(T).IsInterface || !typeof(T).IsVisible) throw new ArgumentException("Public host services must be public interfaces."); return new(typeof(T), capability, permission, scope => create(scope)); }
}
public static class ModulePolicy
{
    internal static void ValidateBindings(IEnumerable<ModuleKind> kinds, IEnumerable<ModuleService> services)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Check(Type contract, string? id, string? permission, ModuleCapabilityKind kind)
        {
            var entry = id is not null ? ModuleApi.Describe(id) : null;
            if (entry is null || entry.Kind != kind || entry.Permission != permission || !ids.Add(entry.Id)
                || contract is null || !contract.IsVisible || contract.FullName != entry.ContractType || contract.Assembly.GetName().Name != entry.ContractAssembly)
                throw new ModuleApiException("invalid-host-binding", "Host binding does not match the capability catalog: " + (id ?? "<missing>"));
        }
        foreach (var kind in kinds) Check(kind.Contract, kind.Id, null, ModuleCapabilityKind.Contribution);
        foreach (var service in services)
        {
            Check(service.Contract, service.Capability, service.Permission, ModuleCapabilityKind.Service);
            if (!service.Contract.IsInterface || service.Create is null) throw new ModuleApiException("invalid-host-binding", "Host services require a public interface and factory.");
        }
    }
    public static void Validate(ModuleManifest manifest)
    {
        if (!ModuleIds.Valid(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || !Version.TryParse(manifest.Version, out _))
            throw new ModuleApiException("invalid-manifest", "A module needs a valid id, name and version.");
        if (manifest.ApiVersion != ModuleApi.Major || manifest.ApiMinor < 0 || manifest.ApiMinor > ModuleApi.Minor || manifest.Requires?.ContractMinor is < 0
            || manifest.Requires?.ContractMinor > ModuleApi.Minor) throw new ModuleApiException("contract-version-mismatch", "Unsupported module contract version.");
        if (manifest.Capabilities is null || manifest.OptionalCapabilities is null || manifest.Permissions is null || manifest.Requires is { Capabilities: null })
            throw new ModuleApiException("invalid-manifest", "Capability and permission lists cannot be null.");
        if (manifest.Capabilities.Concat(manifest.OptionalCapabilities).Concat(manifest.Permissions).Concat(manifest.Requires?.Capabilities ?? []).Any(string.IsNullOrWhiteSpace))
            throw new ModuleApiException("invalid-manifest", "Capability and permission identifiers cannot be empty.");
        foreach (string capability in manifest.Capabilities.Concat(manifest.Requires?.Capabilities ?? []))
            if (!ModuleApi.Capabilities.Contains(capability)) throw new ModuleApiException("unknown-capability", "Unsupported required capability: " + capability);
        foreach (string permission in manifest.Permissions)
            if (!ModuleApi.Permissions.Contains(permission)) throw new ModuleApiException("unknown-permission", "Unsupported permission: " + permission);
        if (manifest.Host is { } host)
        {
            bool validMinimum = Version.TryParse(host.MinimumVersion, out var minimum);
            bool validMaximum = host.MaximumVersionExclusive is not { } text || Version.TryParse(text, out _);
            if (!validMinimum || !validMaximum || (host.MaximumVersionExclusive is { } upper && Version.Parse(upper) <= minimum))
                throw new ModuleApiException("invalid-manifest", "Invalid host version range.");
        }
        foreach (var dependency in manifest.Dependencies ?? throw new ModuleApiException("invalid-manifest", "Dependencies cannot be null."))
            if (dependency is null || !ModuleIds.Valid(dependency.Id) || !Version.TryParse(dependency.MinimumVersion, out var minimum)
                || dependency.MaximumVersionExclusive is { } upper && (!Version.TryParse(upper, out var maximum) || maximum <= minimum))
                throw new ModuleApiException("invalid-dependency", "Invalid dependency version bounds.");
    }
    internal static bool Declares(ModuleManifest manifest, string capability) => capability == ModuleApi.HostInfo
        || manifest.Capabilities.Contains(capability) || manifest.OptionalCapabilities.Contains(capability) || (manifest.Requires?.Capabilities.Contains(capability) ?? false);
}
