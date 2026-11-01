using System.IO;
using System.Text.Json;
using BrimDeck.Modules;

internal static partial class Program
{
    private static void ModuleOrphanChecks()
    {
        string Root() { string root = Path.Combine(_root, "orphan-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
        void Write(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }

        {
            string root = Root(); var files = new ModuleFiles(root);
            string data = Path.Combine(root, "modules-data", "ghost"); Write(Path.Combine(data, "keep.json"), "data");
            Check("orphan data directories are listed", files.Orphans().SequenceEqual(["ghost"]));
            files.RequestRemoval("ghost");
            var result = files.ProcessPending([]);
            Check("clearing an orphan removes only its data", !result.Pending && !Directory.Exists(data) && !files.HasPending);
        }
        {
            string root = Root(); var files = new ModuleFiles(root);
            string dir = Path.Combine(root, "mods", "sample"); Directory.CreateDirectory(dir);
            var manifest = new ModuleManifest { Id = "sample", Name = "sample", Assembly = "Example.dll", EntryType = "Example.Entry" };
            File.WriteAllText(Path.Combine(dir, "module.json"), JsonSerializer.Serialize(manifest)); File.WriteAllText(Path.Combine(dir, "Example.dll"), "fixture");
            var module = new DiscoveredModule(manifest, dir, ModuleCatalog.Fingerprint(dir));
            files.Remember([module]); files.RequestRemoval("sample", module); files.ProcessPending([]);
            // Data written after the removal finished is never adopted by the older journal, but it is still visible.
            Write(Path.Combine(root, "modules-data", "sample", "late.json"), "late");
            Check("data created after removal is listed as an orphan", files.Orphans().SequenceEqual(["sample"]));
            files.RequestRemoval("sample"); files.ProcessPending([]);
            Check("an orphan left by a completed removal can be cleared", !Directory.Exists(Path.Combine(root, "modules-data", "sample")));
        }
    }
}
