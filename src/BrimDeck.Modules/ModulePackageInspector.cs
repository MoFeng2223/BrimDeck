using System.Text.Json;

namespace BrimDeck.Modules;

public enum ModuleCheckSeverity { Information, Warning, Error }
public sealed record ModuleCheckIssue(ModuleCheckSeverity Severity, string Code, string Location, string Message);
public sealed record ModuleAssemblyMetadata(string File, string Name, string Version, IReadOnlyList<string> References, IReadOnlyList<string> NativeImports);
public sealed record ModulePackageReport(string Directory, ModuleManifest? Manifest, string? Digest,
    IReadOnlyList<ModuleAssemblyMetadata> Assemblies, IReadOnlyList<ModuleCheckIssue> Issues)
{
    public bool Passed => Issues.All(i => i.Severity != ModuleCheckSeverity.Error);
    public string Mode => "static";
    public bool ExecutesModuleCode => false;
}

// Host/tool infrastructure, not a module-side SDK. Never uses Assembly.Load, a module factory, reflection activation or storage writes.
public static partial class ModulePackageInspector
{
    public static ModulePackageReport Inspect(string directory, string? modulesRoot = null)
    {
        var inspection = new Inspection(Path.GetFullPath(directory));
        inspection.Run(modulesRoot is null ? null : Path.GetFullPath(modulesRoot));
        return inspection.Report();
    }
    private sealed partial class Inspection(string directory)
    {
        private ModuleManifest? _manifest;
        private string? _digest;
        private readonly List<ModuleCheckIssue> _issues = [];
        private readonly List<ModuleAssemblyMetadata> _assemblies = [];
        private readonly Dictionary<string, AssemblyShape> _shapes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<(string Code, string Location)> _reported = [];
        private static readonly HashSet<string> Shared = new(["BrimDeck", "BrimDeck.Modules", "BrimDeck.Core", "BrimDeck.UI", "BrimDeck.Modules.Abstractions"], StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ManifestFields = typeof(ModuleManifest).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> FrameworkAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => string.Equals(Path.GetDirectoryName(p), Path.GetDirectoryName(typeof(object).Assembly.Location), StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension).OfType<string>().Concat(new[] { "mscorlib", "netstandard", "WindowsBase", "PresentationCore", "PresentationFramework", "PresentationFramework.Aero", "PresentationFramework.Aero2", "PresentationFramework.AeroLite", "PresentationFramework.Classic", "PresentationFramework.Luna", "PresentationFramework.Royale", "PresentationUI", "ReachFramework", "System.Xaml", "UIAutomationClient", "UIAutomationProvider", "UIAutomationTypes", "System.Windows.Controls.Ribbon", "System.Windows.Forms", "System.Windows.Forms.Primitives", "WindowsFormsIntegration", "Microsoft.VisualBasic.Forms", "Accessibility", "Microsoft.Win32.SystemEvents", "Microsoft.Windows.SDK.NET", "WinRT.Runtime" }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        private void Issue(ModuleCheckSeverity severity, string code, string location, string message)
        {
            if (_issues.Count >= 1000)
            { if (_reported.Add(("inspection-issue-limit", "package"))) _issues.Add(new(ModuleCheckSeverity.Error, "inspection-issue-limit", "package", "Issue limit reached; remaining findings are omitted.")); return; }
            if (_reported.Add((code, location))) _issues.Add(new(severity, code, location, message));
        }
        private void Error(string code, string location, string message) => Issue(ModuleCheckSeverity.Error, code, location, message);
        private void Warning(string code, string location, string message) => Issue(ModuleCheckSeverity.Warning, code, location, message);
        internal ModulePackageReport Report() => new(directory, _manifest, _digest, _assemblies.AsReadOnly(), _issues.AsReadOnly());
        internal void Run(string? modulesRoot)
        {
            IReadOnlyList<string> files;
            try { files = CollectFiles(); _digest = ModuleCatalog.Fingerprint(directory); }
            catch (Exception error) when (ReadError(error)) { Error("package-unreadable", directory, error.Message); return; }
            string manifestFile = Path.Combine(directory, "module.json");
            try
            {
                if (new FileInfo(manifestFile).Length > 1024 * 1024) throw new InvalidDataException("Manifest exceeds 1 MiB.");
                using var json = ModuleManifestReader.Open(manifestFile);
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("module.json must be an object.");
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in json.RootElement.EnumerateObject())
                {
                    if (!keys.Add(property.Name)) Error("duplicate-manifest-field", "module.json:" + property.Name, "Duplicate manifest fields are ambiguous.");
                    if (!ManifestFields.Contains(property.Name)) Warning("unknown-manifest-field", "module.json:" + property.Name, "Unknown field is ignored by the current host. Check for spelling mistakes.");
                }
                _manifest = ModuleManifestReader.Deserialize(json.RootElement);
                ModulePolicy.Validate(_manifest);
                if (_manifest.Id.StartsWith("builtin-", StringComparison.Ordinal)) throw new InvalidDataException("The builtin- namespace is reserved.");
                if (_manifest.Assembly is null || _manifest.Assembly.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 || _manifest.Assembly.Any(char.IsControl)
                    || Path.GetFileName(_manifest.Assembly) != _manifest.Assembly || !_manifest.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(_manifest.EntryType)) throw new InvalidDataException("Specify a local assembly file and entryType.");
            }
            catch (Exception error) when (ReadError(error) || error is JsonException or ModuleApiException)
            { Error(error is ModuleApiException api ? api.Code : "invalid-manifest", "module.json", error.Message); return; }
            foreach (string file in files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) ReadAssembly(file);
            CheckReferences(); CheckEntry(); CheckDependencies(modulesRoot);
            try { if (ModuleCatalog.Fingerprint(directory) != _digest) Error("package-changed", directory, "Package changed during inspection; run the check again."); }
            catch (Exception error) when (ReadError(error)) { Error("package-changed", directory, error.Message); }
            Issue(ModuleCheckSeverity.Information, "static-limits", "package", "Static metadata only. Does not validate registrations, setting getter values, runtime behavior, reflection/dynamic loading, or safety.");
        }
        private IReadOnlyList<string> CollectFiles() => ModuleCatalog.Files(directory);
        private void CheckReferences()
        {
            foreach (var assembly in _assemblies)
                foreach (string reference in assembly.References)
                {
                    string location = assembly.File + " -> " + reference;
                    if (reference.Equals("BrimDeck", StringComparison.OrdinalIgnoreCase)) Error("desktop-reference-denied", location, "Modules and private dependencies must not reference the desktop application.");
                    else if (reference.Equals("BrimDeck.Core", StringComparison.OrdinalIgnoreCase) || reference.Equals("BrimDeck.Modules", StringComparison.OrdinalIgnoreCase)) Warning("internal-assembly-reference", location, "References host infrastructure outside the general module SDK compatibility promise.");
                    else if (!Shared.Contains(reference) && !Framework(reference) && !_shapes.ContainsKey(reference))
                        Error("private-dependency-missing", location, "Referenced private assembly is missing from the package.");
                }
        }
        private void CheckDependencies(string? modulesRoot)
        {
            if (_manifest!.Dependencies.Length == 0) return;
            if (modulesRoot is null) { Warning("dependency-context-required", "module.json:dependencies", "Pass --modules-root to check installed module versions and dependency cycles. Enablement/approval are not checked offline."); return; }
            var available = new Dictionary<string, ModuleManifest>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (string folder in System.IO.Directory.EnumerateDirectories(modulesRoot).Take(2001))
                {
                    if (available.Count >= 2000) { Error("dependency-root-limit", "dependencies", "Modules root exceeds inspection limits."); break; }
                    if (Path.GetFileName(folder).StartsWith('.') || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                    string manifestFile = Path.Combine(folder, "module.json");
                    try
                    {
                        if ((File.GetAttributes(manifestFile) & FileAttributes.ReparsePoint) != 0 || new FileInfo(manifestFile).Length > 1024 * 1024) continue;
                        var found = ModuleManifestReader.Read(manifestFile);
                        ModulePolicy.Validate(found);
                        if (!available.TryAdd(found.Id, found)) ambiguous.Add(found.Id);
                    }
                    catch (Exception error) when (ReadError(error) || error is JsonException or ModuleApiException) { /* Only the required subgraph is relevant. */ }
                }
            }
            catch (Exception error) when (ReadError(error)) { Error("dependency-root-unreadable", "dependencies", error.Message); return; }
            available[_manifest.Id] = _manifest;
            if (ambiguous.Contains(_manifest.Id)) Error("duplicate-module-id", "dependencies:" + _manifest.Id, "Multiple folders declare this package's module id.");
            var active = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
            void Visit(ModuleManifest manifest)
            {
                if (active.Count >= 128) { Error("dependency-depth-limit", "dependencies", "Dependency graph exceeds 128 levels."); return; }
                if (!active.Add(manifest.Id)) { Error("dependency-cycle", "dependencies:" + manifest.Id, "Dependency graph contains a cycle."); return; }
                if (visited.Add(manifest.Id))
                    foreach (var dependency in manifest.Dependencies)
                    {
                        string location = manifest.Id + " -> " + dependency.Id;
                        if (ambiguous.Contains(dependency.Id)) { Error("duplicate-module-id", location, "Multiple folders declare the required module id."); continue; }
                        if (!available.TryGetValue(dependency.Id, out var found)) { Error("module-dependency-missing", location, "Required module was not found with a valid manifest in the supplied modules root."); continue; }
                        var version = Version.Parse(found.Version);
                        if (version < Version.Parse(dependency.MinimumVersion) || dependency.MaximumVersionExclusive is { } maximum && version >= Version.Parse(maximum))
                            Error("module-dependency-version", location, "Installed module is outside the declared version bounds.");
                        Visit(found);
                    }
                active.Remove(manifest.Id);
            }
            Visit(_manifest);
        }
        private static bool Framework(string name) => FrameworkAssemblies.Contains(name);
        private static bool ReadError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or System.Security.SecurityException;
    }
}
