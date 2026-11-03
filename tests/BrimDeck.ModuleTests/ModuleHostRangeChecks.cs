using System.IO;
using System.Text.Json;
using BrimDeck;
using BrimDeck.Modules;

internal static partial class Program
{
    private static void ModuleHostRangeChecks(string sampleBuild)
    {
        string Root() { string root = Path.Combine(_root, "hostrange-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
        DiscoveredModule Package(string root, ModuleManifest manifest, string assembly)
        {
            string dir = Path.Combine(root, "mods", manifest.Id); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "module.json"), JsonSerializer.Serialize(manifest));
            File.Copy(Path.Combine(sampleBuild, assembly), Path.Combine(dir, manifest.Assembly!));
            return new(manifest, dir, ModuleCatalog.Fingerprint(dir));
        }
        ModuleManifest Manifest(string id, ModuleHostRequirement? host = null) => new()
        {
            Id = id, Name = id, Version = "1.0.0", Assembly = "Example.dll", EntryType = "Example.Entry", Host = host
        };

        // Version range outside the running application: listed, explained, never activated.
        {
            string root = Root(); Package(root, Manifest("future", new ModuleHostRequirement("9.0.0")), "BrimDeck.Sample.dll");
            var catalog = new ModuleCatalog(Path.Combine(root, "mods"), "", [], "1.6.0");
            var found = catalog.Discover();
            Check("host range is judged against the application version", found.Count == 1 && !found[0].HostCompatible && found[0].HostRange == ">= 9.0.0");
            using var host = Host();
            host.ReconcileAsync([catalog.Definition(found[0], true)]).GetAwaiter().GetResult();
            var state = host.Modules.Single();
            Check("an incompatible host range is listed instead of activated",
                state.Status == ModuleStatus.HostMismatch && state.Error is not null && host.Registry.List<ModulePage>().Count == 0);
        }
        {
            string root = Root(); Package(root, Manifest("current", new ModuleHostRequirement("1.0.0", "2.0.0")), "BrimDeck.Sample.dll");
            var catalog = new ModuleCatalog(Path.Combine(root, "mods"), "", [], "1.6.0");
            var found = catalog.Discover();
            Check("a matching host range stays compatible", found.Count == 1 && found[0].HostCompatible && found[0].HostRange == ">= 1.0.0 < 2.0.0");
        }

        // Reaching into assemblies outside the contract requires an explicit range.
        {
            string root = Root();
            string dir = Path.Combine(root, "mods", "internals"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "module.json"), JsonSerializer.Serialize(Manifest("internals")));
            File.Copy(Path.Combine(sampleBuild, "BrimDeck.Sample.dll"), Path.Combine(dir, "Example.dll"));
            File.Copy(Path.Combine(sampleBuild, "BrimDeck.UI.dll"), Path.Combine(dir, "BrimDeck.UI.dll"));
            var scan = ModuleAssemblyScan.ReferencesInternals(dir);
            var catalog = new ModuleCatalog(Path.Combine(root, "mods"), "", [], "1.6.0");
            var found = catalog.Discover();
            Check("an assembly that reaches into host internals is detected", scan);
            Check("internals without a host range are rejected with a reason",
                found.Count == 0 && catalog.Failures.Count == 1 && catalog.Failures[0].Error.Contains("host version range"));
            File.WriteAllText(Path.Combine(dir, "module.json"), JsonSerializer.Serialize(Manifest("internals", new ModuleHostRequirement("1.0.0"))));
            var allowed = new ModuleCatalog(Path.Combine(root, "mods"), "", [], "1.6.0").Discover();
            Check("internals with a host range are accepted and flagged", allowed.Count == 1 && allowed[0].UsesInternals && allowed[0].HostCompatible);
        }
    }
}
