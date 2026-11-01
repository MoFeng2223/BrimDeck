using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Modules;

public sealed record DiscoveredModule(ModuleManifest Manifest, string Directory, string Digest)
{
    public IReadOnlyList<ModuleDiagnostic> Warnings { get; init; } = [];
    // References an assembly outside the contract (BrimDeck, BrimDeck.Core, BrimDeck.Modules).
    public bool UsesInternals { get; init; }
    public bool HostCompatible { get; init; } = true;
    public string? HostRange { get; init; }
    public ModuleSignature? Signature { get; init; }
}
public sealed record DiscoveryFailure(string Path, string Error);

// Discovery reads metadata only. Loading is deferred to the enabled definition's factory.
public sealed class ModuleCatalog(string directory, string cacheDirectory, IEnumerable<Assembly> sharedAssemblies, string? applicationVersion = null)
{
    private static readonly object CopyGate = new();
    private readonly List<(string Path, WeakReference Context)> _cacheUsers = [];
    internal T WithCacheLease<T>(Func<IReadOnlyList<string>, T> action)
    {
        lock (CopyGate)
        {
            _cacheUsers.RemoveAll(e => !e.Context.IsAlive);
            return action(_cacheUsers.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }
    private readonly Dictionary<string, Assembly> _shared = sharedAssemblies.Append(typeof(IModule).Assembly)
        .Distinct().ToDictionary(a => a.GetName().Name!, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<DiscoveryFailure> Failures { get; private set; } = [];
    public IReadOnlyList<DiscoveredModule> Discover()
    {
        var found = new List<DiscoveredModule>(); var failures = new List<DiscoveryFailure>();
        if (!System.IO.Directory.Exists(directory)) { Failures = []; return found; }
        string[] folders;
        try { ModuleManifestReader.Unlinked(directory); folders = System.IO.Directory.EnumerateDirectories(directory).Order().ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Failures = [new(directory, ex.Message)]; return found; }
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (Path.GetFileName(folder).StartsWith('.')) continue;
            try
            {
                string digest = Fingerprint(folder);
                var manifest = ModuleManifestReader.Read(Path.Combine(folder, "module.json"));
                var (resolved, warnings) = ModuleDeprecations.Resolve(manifest);
                manifest = resolved;
                ModuleIds.Require(manifest.Id);
                ModulePolicy.Validate(manifest);
                if (manifest.Id.StartsWith("builtin-", StringComparison.Ordinal)) throw new InvalidDataException("The builtin- namespace belongs to the host.");
                if (manifest.ApiVersion != 1 || !Version.TryParse(manifest.Version, out _) || string.IsNullOrWhiteSpace(manifest.Name)
                    || manifest.Dependencies is null || manifest.Dependencies.Any(dep => dep is null || !ModuleIds.Valid(dep.Id) || !Version.TryParse(dep.MinimumVersion, out _))) throw new InvalidDataException("Invalid manifest or incompatible API version.");
                if (manifest.Assembly is null || manifest.Assembly.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 || manifest.Assembly.Any(char.IsControl)
                    || Path.GetFileName(manifest.Assembly) != manifest.Assembly || !manifest.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(manifest.EntryType)) throw new InvalidDataException("Specify a local assembly file and entryType.");
                if (!File.Exists(Path.Combine(folder, manifest.Assembly))) throw new FileNotFoundException("Module assembly is missing.");
                bool internals = ModuleAssemblyScan.ReferencesInternals(folder);
                // A module that reaches into internals must pin the application version: those assemblies are not a contract.
                if (internals && manifest.Host is null) throw new InvalidDataException("Referencing host internals requires a host version range (host.minimumVersion).");
                var signature = ModuleSignatureVerifier.Verify(manifest, folder);
                if (Fingerprint(folder) != digest) throw new IOException("Module changed during discovery; refresh the module list.");
                if (conflicts.Contains(manifest.Id)) throw new InvalidDataException("Duplicate module id: " + manifest.Id);
                if (found.Find(m => m.Manifest.Id == manifest.Id) is { } previous)
                {
                    found.Remove(previous); conflicts.Add(manifest.Id);
                    failures.Add(new(previous.Directory, "Duplicate module id: " + manifest.Id));
                    throw new InvalidDataException("Duplicate module id: " + manifest.Id);
                }
                found.Add(new(manifest, folder, digest)
                {
                    Warnings = warnings,
                    UsesInternals = internals,
                    HostCompatible = Compatible(manifest.Host),
                    HostRange = RangeText(manifest.Host),
                    Signature = signature
                });
            }
            catch (Exception ex) { failures.Add(new(folder, ex.Message)); }
        }
        Failures = failures; return found;
    }
    private bool Compatible(ModuleHostRequirement? host)
    {
        if (host is null) return true;
        // Without a known application version the range cannot be judged; the manifest's format is validated separately.
        if (!Version.TryParse(applicationVersion, out var current)) return true;
        if (!Version.TryParse(host.MinimumVersion, out var minimum) || current < minimum) return false;
        return host.MaximumVersionExclusive is not { } upper || !Version.TryParse(upper, out var maximum) || current < maximum;
    }
    private string? RangeText(ModuleHostRequirement? host) => host is null ? null
        : host.MaximumVersionExclusive is { } upper ? $">= {host.MinimumVersion} < {upper}" : ">= " + host.MinimumVersion;
    internal static IReadOnlyList<string> Files(string folder)
    {
        ModuleManifestReader.Unlinked(folder);
        var files = new List<string>(); long bytes = 0; int entries = 0;
        void Walk(string dir, int depth)
        {
            if (depth > 64) throw new IOException("Module directory depth exceeds 64.");
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked module directories are not supported.");
            var children = System.IO.Directory.EnumerateFileSystemEntries(dir).Take(6001 - entries).ToArray();
            entries += children.Length;
            if (entries > 6000) throw new IOException("Module exceeds 6000 directory entries.");
            foreach (var entry in children.Order())
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked module files are not supported.");
                if ((attributes & FileAttributes.Directory) != 0) Walk(entry, depth + 1);
                else
                {
                    files.Add(entry); bytes += new FileInfo(entry).Length;
                    if (files.Count > 2000 || bytes > 64 * 1024 * 1024) throw new IOException("Module exceeds 2000 files or 64 MiB.");
                }
            }
        }
        Walk(folder, 0); return files;
    }
    public static string Fingerprint(string folder)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        foreach (var file in Files(folder))
        {
            ModuleManifestReader.Unlinked(file);
            byte[] name = Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, file).Replace('\\', '/'));
            hash.AppendData(BitConverter.GetBytes(name.Length)); hash.AppendData(name);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            total += input.Length; if (total > 64L * 1024 * 1024) throw new IOException("Module exceeds 64 MiB.");
            byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public ModuleDefinition Definition(DiscoveredModule module, bool enabled)
    {
        PluginLoadContext? loadContext = null;
        return new(module.Manifest, module.Digest, enabled, () =>
        {
            if (Fingerprint(module.Directory) != module.Digest) throw new IOException("Module files changed; refresh the module list before enabling.");
            string copy = Path.Combine(cacheDirectory, module.Manifest.Id, module.Digest);
            lock (CopyGate)
            {
                ModuleManifestReader.Unlinked(copy, allowMissing: true);
                if (!System.IO.Directory.Exists(copy))
                {
                    string staging = copy + ".pending-" + Guid.NewGuid().ToString("N");
                    ModuleManifestReader.Unlinked(staging, allowMissing: true);
                    foreach (var file in Files(module.Directory))
                    {
                        string target = Path.Combine(staging, Path.GetRelativePath(module.Directory, file));
                        ModuleManifestReader.Unlinked(file); ModuleManifestReader.Unlinked(target, allowMissing: true);
                        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        ModuleManifestReader.Unlinked(target, allowMissing: true); File.Copy(file, target);
                    }
                    if (Fingerprint(staging) != module.Digest) throw new IOException("Module changed while copying.");
                    System.IO.Directory.Move(staging, copy);
                }
                if (Fingerprint(copy) != module.Digest) throw new IOException("Module cache changed. Remove this module's cache before trying again.");
                loadContext = new PluginLoadContext(Path.Combine(copy, module.Manifest.Assembly!), _shared);
                _cacheUsers.Add((copy, new WeakReference(loadContext)));
            }
            string entry = Path.Combine(copy, module.Manifest.Assembly!);
            var type = loadContext.LoadFromAssemblyPath(entry).GetType(module.Manifest.EntryType!, throwOnError: true)!;
            if (!typeof(IModule).IsAssignableFrom(type) || type.IsAbstract) throw new InvalidDataException("Entry type must implement IModule.");
            return (IModule)(Activator.CreateInstance(type) ?? throw new InvalidDataException("Module construction failed."));
        }, Release: () => { loadContext?.Unload(); loadContext = null; })
        {
            Warnings = module.Warnings,
            UsesInternals = module.UsesInternals,
            HostRange = module.HostRange,
            Signature = module.Signature,
            Rejection = module.HostCompatible ? null
                : $"Requires BrimDeck {module.HostRange}" + (string.IsNullOrWhiteSpace(applicationVersion) ? "" : $"; this is {applicationVersion}")
        };
    }
    private sealed class PluginLoadContext(string entry, IReadOnlyDictionary<string, Assembly> shared) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(entry);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is not null && shared.TryGetValue(name.Name, out var common))
            {
                if (name.Version?.Major != common.GetName().Version?.Major) throw new FileLoadException("Incompatible shared contract: " + name.Name);
                return common;
            }
            if (string.Equals(name.Name, "BrimDeck", StringComparison.OrdinalIgnoreCase)) throw new FileLoadException("Modules must depend on the public SDK, not the desktop application.");
            string? resolved = _resolver.ResolveAssemblyToPath(name);
            if (resolved is null && name.Name is not null)
            {
                string local = Path.Combine(Path.GetDirectoryName(entry)!, name.Name + ".dll");
                if (File.Exists(local)) resolved = local;
            }
            return resolved is null ? null : LoadFromAssemblyPath(resolved);
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            string? resolved = _resolver.ResolveUnmanagedDllToPath(name);
            return resolved is null ? 0 : LoadUnmanagedDllFromPath(resolved);
        }
    }
}
