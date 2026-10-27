using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Modules;

internal static partial class Program
{
    private static async Task ModuleFileChecks(string sampleBuild)
    {
        string Root() { string root = Path.Combine(_root, "files-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
        void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        DiscoveredModule Package(string root, string id = "sample", string? folder = null)
        {
            string dir = Path.Combine(root, "mods", folder ?? id); Directory.CreateDirectory(dir);
            var manifest = new ModuleManifest { Id = id, Name = id, Assembly = "Example.dll", EntryType = "Example.Entry" };
            Write(Path.Combine(dir, "module.json"), JsonSerializer.Serialize(manifest)); Write(Path.Combine(dir, "Example.dll"), "test fixture metadata");
            return new(manifest, dir, ModuleCatalog.Fingerprint(dir));
        }
        {
            string root = Root(); var files = new ModuleFiles(root);
            Write(Path.Combine(root, "modules-data", "sample", "preferences.json"), new string('a', 709));
            string current = Path.Combine(root, "modules-cache", "sample", "current"), old = Path.Combine(root, "modules-cache", "sample", "old");
            Write(Path.Combine(current, "a.dll"), "12345"); Write(Path.Combine(old, "a.dll"), "123");
            Check("module storage reports actual data and cache bytes", files.Measure("sample") is { DataBytes: 709, CacheBytes: 8 });
            Check("unknown module sizes are explicitly zero", files.Measure("missing") is { DataBytes: 0, CacheBytes: 0, Error: null });
            Check("cache clear defers a protected active generation", files.ClearCache("sample", [current]).Pending && Directory.Exists(current) && !Directory.Exists(old));
            Check("cache clear preserves business data", files.Measure("sample") is { DataBytes: 709, CacheBytes: 5, CachePending: true });
            files = new(root); Check("cache cleanup request survives restart", files.HasPending && files.Problem is null);
            Check("startup clears released cache without changing data", !files.ProcessPending([]).Pending && files.Measure("sample") is { CacheBytes: 0, DataBytes: 709, CachePending: false });
            Throws("cache clear rejects path traversal", () => files.ClearCache("../../outside", []));
            Throws("disk usage rejects invalid module identity", () => files.Measure("sample/../outside"));
        }
        {
            string root = Root(); var files = new ModuleFiles(root); var module = Package(root, folder: "custom-folder"); files.Remember([module]);
            Write(Path.Combine(root, "modules-data", "sample", "state.json"), "user data"); Write(Path.Combine(root, "modules-cache", "sample", "digest", "a.dll"), "cache");
            files.RequestRemoval("sample", module);
            Check("uninstall records its intent before deleting files", Directory.Exists(module.Directory) && files.PendingRemovals.Contains("sample"));
            files = new(root); Check("uninstall intent is restored without losing the actual folder name", files.PendingRemovals.Contains("sample"));
            Check("uninstall removes package data and cache", !files.ProcessPending([]).Pending && !Directory.Exists(module.Directory)
                && !Directory.Exists(Path.Combine(root, "modules-data", "sample")) && !Directory.Exists(Path.Combine(root, "modules-cache", "sample")));
            Check("uninstall suppresses automatic bundle resurrection", new ModuleFiles(root).SuppressedBundles.Contains("sample"));
            module = Package(root, folder: "custom-folder"); Write(Path.Combine(root, "modules-data", "sample", "new.json"), "new data");
            files.ProcessPending([]);
            Check("completed uninstall does not delete a later same-id installation", Directory.Exists(module.Directory) && File.ReadAllText(Path.Combine(root, "modules-data", "sample", "new.json")) == "new data");
        }
        {
            string root = Root(); var files = new ModuleFiles(root); var module = Package(root); files.Remember([module]);
            string locked = Path.Combine(root, "modules-cache", "sample", "digest", "locked.dll"); Write(locked, "old cache");
            Write(Path.Combine(root, "modules-data", "sample", "old.json"), "old data");
            files.RequestRemoval("sample", module);
            using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check("locked uninstall files remain pending instead of reporting success", files.ProcessPending([]).Pending && files.PendingRemovals.Contains("sample"));
                Check("locked cache never delays deletion of unrelated module data", !Directory.Exists(Path.Combine(root, "modules-data", "sample"))
                    && !Directory.EnumerateFiles(Path.Combine(root, ".module-trash"), "old.json", SearchOption.AllDirectories).Any());
                module = Package(root); Write(Path.Combine(root, "modules-data", "sample", "new.json"), "new data");
            }
            files = new(root);
            Check("restarted cleanup deletes only quarantined files", !files.ProcessPending([]).Pending && Directory.Exists(module.Directory)
                && File.ReadAllText(Path.Combine(root, "modules-data", "sample", "new.json")) == "new data");
        }
        {
            string root = Root(); var files = new ModuleFiles(root); var module = Package(root); files.Remember([module]);
            files.RequestRemoval("sample", module); Write(Path.Combine(module.Directory, "Example.dll"), "updated between confirmation and cleanup");
            Check("uninstall never removes changed unconfirmed package files", files.ProcessPending([]).Pending && Directory.Exists(module.Directory));
            files.RequestRemoval("sample", new(module.Manifest, module.Directory, ModuleCatalog.Fingerprint(module.Directory)));
            Check("explicit renewed uninstall confirmation can finish a changed package", !files.ProcessPending([]).Pending && !Directory.Exists(module.Directory));
        }
        {
            string root = Root(); var files = new ModuleFiles(root); var module = Package(root); files.Remember([module]);
            Check("invalid discovery alone does not count as manual deletion", files.Missing([]).Count == 0);
            string moved = Path.Combine(root, "mods", "renamed"); Directory.Move(module.Directory, moved);
            Check("renaming a package preserves its data identity", files.Missing([new(module.Manifest, moved, module.Digest)]).Count == 0);
            Directory.Move(moved, module.Directory);
            File.Delete(Path.Combine(module.Directory, "Example.dll")); File.Delete(Path.Combine(module.Directory, "module.json")); Directory.Delete(module.Directory);
            Check("actual removed package is identified for automatic cleanup", files.Missing([]).SequenceEqual(["sample"]));
            files.RequestRemoval("sample"); Check("manual removal cleanup is idempotent", !files.ProcessPending([]).Pending && !files.HasPending);
        }
        {
            string root = Root(); Write(Path.Combine(root, "module-files.json"), "broken journal");
            Write(Path.Combine(root, "modules-cache", "sample", "digest", "x"), "retained"); var files = new ModuleFiles(root);
            Check("corrupt maintenance journal reports a problem", files.Problem is not null);
            Throws("corrupt maintenance journal blocks destructive maintenance", () => files.ClearCache("sample", []));
            Check("corrupt journal is preserved for recovery", File.ReadAllText(Path.Combine(root, "module-files.json")) == "broken journal" && File.Exists(Path.Combine(root, "modules-cache", "sample", "digest", "x")));
        }
        {
            string root = Root(), outside = Root(); Write(Path.Combine(outside, "retained.txt"), "retained");
            string link = Path.Combine(root, "modules-cache", "sample", "link"); Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(argument);
            using var junction = Process.Start(start)!; junction.WaitForExit();
            if (junction.ExitCode != 0) throw new IOException("Cannot create E: junction fixture: " + junction.StandardError.ReadToEnd());
            var files = new ModuleFiles(root);
            Check("storage measurement never follows junctions", files.Measure("sample") is { CacheBytes: null, Error: not null });
            Check("cache deletion refuses a junction and preserves its target", files.ClearCache("sample", []).Pending && File.ReadAllText(Path.Combine(outside, "retained.txt")) == "retained");
            Directory.Delete(link); // Delete this fixture's junction itself, never its target.
            Check("removing the unsafe entry allows queued cleanup", !files.ProcessPending([]).Pending);
        }
        {
            string source = Root(), root = Root(); Package(source, "bundled"); var receipts = new Dictionary<string, string>();
            ModuleBundleInstaller.Install(Path.Combine(source, "mods"), Path.Combine(root, "mods"), receipts);
            var installed = new ModuleCatalog(Path.Combine(root, "mods"), "", []).Discover().Single(); var files = new ModuleFiles(root); files.RequestRemoval("bundled", installed); files.ProcessPending([]); receipts.Remove("bundled");
            Check("refresh does not silently reinstall removed bundles", ModuleBundleInstaller.Install(Path.Combine(source, "mods"), Path.Combine(root, "mods"), receipts, files.SuppressedBundles).Count == 0 && !Directory.Exists(installed.Directory));
            files.RestoreBundles();
            Check("explicit bundle restore can reinstall normally", ModuleBundleInstaller.Install(Path.Combine(source, "mods"), Path.Combine(root, "mods"), receipts, files.SuppressedBundles).Count == 1);
        }
        {
            int starts = 0; IModuleContext? old = null; var trace = new List<string>(); var pending = new TaskCompletionSource();
            using var host = Host();
            var a = Def("a", c => { old = c; starts++; c.OnDispose(() => { trace.Add("a"); c.Storage.Write("last", "paused"); }); return ValueTask.CompletedTask; });
            var b = Def("b", c => { c.OnDispose(() => trace.Add("b")); return ValueTask.CompletedTask; }, depends: ["a"]);
            var slow = Def("unrelated", async _ => await pending.Task);
            var running = host.ReconcileAsync([a, b, slow]); await host.StopForMaintenanceAsync("a");
            Check("maintenance stops dependents before their dependency", trace.SequenceEqual(["b", "a"]));
            Check("maintenance is independent of unrelated async startup", !running.IsCompleted && host.Modules.Single(m => m.Id == "a").Status == ModuleStatus.Disabled);
            Throws("retired generation cannot recreate uninstalled data", () => old!.Storage.Write("late", "late"));
            var stale = host.ReconcileAsync([a, b, slow]); pending.SetResult(); await Task.WhenAll(running, stale);
            Check("stale configuration cannot restart a module being removed", starts == 1);
            host.ResumeAfterMaintenance("a"); await host.ReconcileAsync([a, b]);
            Check("explicit reinstallation can leave maintenance state", starts == 2);
        }
        {
            string root = Root(), dir = Path.Combine(root, "mods", "sample-notes"); Directory.CreateDirectory(dir);
            foreach (string file in new[] { "module.json", "BrimDeck.Sample.dll", "BrimDeck.Sample.Helper.dll", "BrimDeck.Sample.deps.json" }) File.Copy(Path.Combine(sampleBuild, file), Path.Combine(dir, file));
            var catalog = new ModuleCatalog(Path.Combine(root, "mods"), Path.Combine(root, "modules-cache"), [typeof(ModuleUi).Assembly, typeof(DeckSettings).Assembly]);
            using var host = Host(); var found = catalog.Discover().Single(); await host.ReconcileAsync([catalog.Definition(found, true)]);
            var files = new ModuleFiles(root); var clear = catalog.WithCacheLease(paths => files.ClearCache(found.Manifest.Id, paths));
            Check("loaded DLL cache stays protected while the module is running", clear.Pending && host.Modules.Single().Status == ModuleStatus.Ready && host.Registry.List<ModulePage>().Count == 2);
        }
    }
}
