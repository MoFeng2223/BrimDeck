using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BrimDeck.Modules;

internal static partial class Program
{
    private static void ModuleBoundaryAuditChecks()
    {
        string Root() { string root = Path.Combine(_root, "audit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
        void Write(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        DiscoveredModule Package(string root, string id = "sample", string? folder = null)
        {
            string dir = Path.Combine(root, "mods", folder ?? id); Directory.CreateDirectory(dir);
            var manifest = new ModuleManifest { Id = id, Name = id, Assembly = "Example.dll", EntryType = "Example.Entry" };
            Write(Path.Combine(dir, "module.json"), JsonSerializer.Serialize(manifest)); Write(Path.Combine(dir, "Example.dll"), "fixture");
            return new(manifest, dir, ModuleCatalog.Fingerprint(dir));
        }
        void Link(string flag, string link, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "/c", "mklink", flag, link, target }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("Cannot create E: link fixture: " + process.StandardError.ReadToEnd());
        }
        {
            string root = Root(); var files = new ModuleFiles(root); var module = Package(root);
            string data = Path.Combine(root, "modules-data", "sample", "old.json"); Write(data, "old"); files.RequestRemoval("sample", module, deleteData: true);
            using (var held = new FileStream(Path.Combine(root, "module-files.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                files.ProcessPending([]); files.ProcessPending([]);
                Check("audit: failed journal commits cannot discard the pending removal", files.HasPending);
                Check("audit: deletion waits for durable transfer of each directory", Directory.EnumerateFiles(root, "old.json", SearchOption.AllDirectories).Any());
            }
            var replacement = Package(root); Write(Path.Combine(root, "modules-data", "sample", "new.json"), "new");
            new ModuleFiles(root).ProcessPending([]);
            Check("audit: restart after journal failure preserves replacement business data", File.Exists(Path.Combine(root, "modules-data", "sample", "new.json")));
        }
        {
            string root = Root(); var files = new ModuleFiles(root); var module = Package(root); files.RequestRemoval("sample", module, deleteData: true);
            Write(Path.Combine(root, "modules-data", "sample", "new.json"), "new after request"); files.ProcessPending([]);
            Check("audit: an initially absent data directory is never adopted by an older removal", File.Exists(Path.Combine(root, "modules-data", "sample", "new.json")));
        }
        {
            string root = Root(); var files = new ModuleFiles(root); var module = Package(root);
            string data = Path.Combine(root, "modules-data", "sample"); Write(Path.Combine(data, "old.json"), "old"); files.RequestRemoval("sample", module, deleteData: true);
            Directory.Move(data, Path.Combine(root, "saved-original")); Write(Path.Combine(data, "new.json"), "replacement");
            var result = files.ProcessPending([]);
            Check("audit: replacement directory identity blocks stale cleanup", result.ReplacementPreserved && File.Exists(Path.Combine(data, "new.json")));
            Check("audit: changed data ownership never follows the previous directory elsewhere", File.Exists(Path.Combine(root, "saved-original", "old.json")));
        }
        {
            string root = Root(), outside = Root(); Write(Path.Combine(outside, "secret.json"), "\"retained\"");
            string linked = Path.Combine(root, "linked-data"); Link("/J", linked, outside); var storage = new ModuleStorage(linked);
            Throws("audit: public storage rejects reads through a linked data directory", () => storage.Read<string>("secret"));
            Throws("audit: public storage rejects writes through a linked data directory", () => storage.Write("secret", "changed"));
            Check("audit: scoped storage cannot change a junction target", File.ReadAllText(Path.Combine(outside, "secret.json")) == "\"retained\"");
            Directory.Delete(linked);
        }
        {
            string root = Root(), outside = Root(); string protectedFile = Path.Combine(outside, "keep.txt"); Write(protectedFile, "retained");
            Link("/H", Path.Combine(root, "value.json.tmp"), protectedFile);
            new ModuleStorage(root).Write("value", "new value");
            Check("audit: preexisting predictable temporary files are never overwritten", File.ReadAllText(protectedFile) == "retained");
        }
        {
            string root = Root(); var module = Package(root); string deep = module.Directory;
            for (int i = 0; i < 66; i++) { deep = Path.Combine(deep, "d"); Directory.CreateDirectory(deep); }
            var catalog = new ModuleCatalog(Path.Combine(root, "mods"), "", []);
            Check("audit: runtime discovery bounds empty directory depth", catalog.Discover().Count == 0 && catalog.Failures.Count == 1);
            Check("audit: static directory limits return a structured failure", ModulePackageInspector.Inspect(module.Directory).Issues.Any(i => i.Code == "package-unreadable"));
        }
        {
            string root = Root(); var module = Package(root);
            Write(Path.Combine(module.Directory, "module.json"), "{\"id\":\"wrong\",\"ID\":\"sample\",\"name\":\"sample\",\"assembly\":\"Example.dll\",\"entryType\":\"Example.Entry\"}");
            var catalog = new ModuleCatalog(Path.Combine(root, "mods"), "", []);
            Check("audit: runtime rejects ambiguous duplicate manifest fields", catalog.Discover().Count == 0 && catalog.Failures.Count == 1);
            Write(Path.Combine(module.Directory, "module.json"), "[]");
            Check("audit: malformed static manifests return a report instead of throwing", ModulePackageInspector.Inspect(module.Directory).Issues.Any(i => i.Code == "invalid-manifest"));
        }
        {
            string root = Root(); Package(root, folder: "a"); Package(root, folder: "b"); Package(root, "other");
            var catalog = new ModuleCatalog(Path.Combine(root, "mods"), "", []); var found = catalog.Discover();
            Check("audit: duplicate IDs block all conflicting packages without hiding healthy modules", found.Count == 1 && found[0].Manifest.Id == "other" && catalog.Failures.Count == 2);
        }
        {
            string root = Root(); var files = new ModuleFiles(root); var module = Package(root); files.Remember([module]);
            Write(Path.Combine(root, "modules-cache", "sample", "generation", "x"), "retained");
            using (var held = new FileStream(Path.Combine(root, "module-files.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Throws("audit: failed removal intent commits are reported", () => files.RequestRemoval("sample", module, deleteData: true));
                Check("audit: failed intent commits restore in-memory removal and bundle state", !files.HasPending && !files.SuppressedBundles.Contains("sample"));
                Throws("audit: failed cache intent commits are reported", () => files.ClearCache("sample", []));
                Check("audit: failed cache intent never starts filesystem cleanup", !files.HasPending && File.Exists(Path.Combine(root, "modules-cache", "sample", "generation", "x")));
            }
        }
        {
            string root = Root(); Write(Path.Combine(root, "module-files.json"), "{\"Version\":1}"); var files = new ModuleFiles(root);
            Check("audit: incomplete maintenance records cannot silently become empty state", files.Problem is not null);
            Throws("audit: incomplete maintenance records block destructive operations", () => files.ClearCache("sample", []));
            Write(Path.Combine(root, "module-files.json"), "{\"Version\":\"invalid\",\"Installed\":[],\"Removing\":[],\"CacheRequests\":[],\"SuppressedBundles\":[]}");
            Check("audit: malformed maintenance version is contained instead of crashing startup", new ModuleFiles(root).Problem is not null);
        }
        {
            string root = Root(); var module = Package(root); string file = Path.Combine(root, "modules-data", "sample", "keep.json"); Write(file, "legacy data");
            var files = new ModuleFiles(root); files.RequestRemoval("sample", module, deleteData: true);
            string journal = Path.Combine(root, "module-files.json"); var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journal))!;
            var job = state["Removing"]![0]!.AsObject(); job.Remove("PackageIdentity"); job.Remove("DataIdentity"); job.Remove("CacheIdentity"); File.WriteAllText(journal, state.ToJsonString());
            var result = new ModuleFiles(root).ProcessPending([]);
            Check("audit: legacy unowned source directories are preserved rather than guessed", result.ReplacementPreserved && File.Exists(file) && Directory.Exists(module.Directory));
        }
        {
            string root = Root(), source = Root(); Package(source); string link = Path.Combine(root, "mods"); Link("/J", link, Path.Combine(source, "mods"));
            var catalog = new ModuleCatalog(link, "", []);
            Check("audit: runtime discovery rejects a linked installation root", catalog.Discover().Count == 0 && catalog.Failures.Count == 1);
            Check("audit: static inspection rejects linked ancestors", ModulePackageInspector.Inspect(Path.Combine(link, "sample")).Issues.Any(i => i.Code == "package-unreadable"));
            Directory.Delete(link);
        }
    }
}
