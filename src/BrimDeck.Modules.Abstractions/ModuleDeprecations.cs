namespace BrimDeck.Modules;

public enum ModuleDeprecationKind { Capability, Permission }

// A name the host still accepts. `RemovedIn` is the release that drops it, so the message can say when.
public sealed record ModuleDeprecation(ModuleDeprecationKind Kind, string Old, string New, string RemovedIn, string? Note = null);

// The only way to rename a capability or permission without breaking installed modules: a rename listed
// here keeps working and explains itself; a name that is absent stays fatal, so a typo never becomes a
// silent downgrade. The table ships with the host, like the capability catalog itself.
public static class ModuleDeprecations
{
    public static IReadOnlyList<ModuleDeprecation> Table { get; } = [];

    // Returns the manifest with renames applied plus one diagnostic per rename. `table` exists so tests
    // and tools can exercise the rule before a rename ships.
    public static (ModuleManifest Manifest, IReadOnlyList<ModuleDiagnostic> Diagnostics) Resolve(ModuleManifest manifest, IReadOnlyList<ModuleDeprecation>? table = null)
    {
        IReadOnlyList<ModuleDeprecation> renames = table ?? Table;
        if (renames.Count == 0) return (manifest, []);
        var diagnostics = new List<ModuleDiagnostic>();

        string[] Rename(string[] names, ModuleDeprecationKind kind)
        {
            if (names.Length == 0) return names;
            var result = new List<string>(names.Length);
            foreach (string name in names)
            {
                ModuleDeprecation? found = null;
                foreach (var candidate in renames)
                    if (candidate.Kind == kind && string.Equals(candidate.Old, name, StringComparison.Ordinal)) { found = candidate; break; }
                if (found is null) { result.Add(name); continue; }
                result.Add(found.New);
                string message = $"{kind.ToString().ToLowerInvariant()} \"{found.Old}\": {found.RemovedIn}, use \"{found.New}\"."
                    + (string.IsNullOrWhiteSpace(found.Note) ? "" : " " + found.Note);
                diagnostics.Add(new("renamed-" + kind.ToString().ToLowerInvariant(), "manifest:" + found.Old, message, ModuleDiagnosticSeverity.Information));
            }
            return [.. result.Distinct(StringComparer.Ordinal)];
        }

        var requires = manifest.Requires;
        var resolved = manifest with
        {
            Capabilities = Rename(manifest.Capabilities, ModuleDeprecationKind.Capability),
            OptionalCapabilities = Rename(manifest.OptionalCapabilities, ModuleDeprecationKind.Capability),
            Permissions = Rename(manifest.Permissions, ModuleDeprecationKind.Permission),
            Requires = requires is null ? null : new ModuleRequirements
            {
                ContractMinor = requires.ContractMinor,
                Capabilities = Rename(requires.Capabilities, ModuleDeprecationKind.Capability)
            }
        };
        return (resolved, diagnostics);
    }
}
