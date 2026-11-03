using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BrimDeck.Modules;

internal static partial class Program
{
    private static void ModuleContinuationAuditChecks(string binaries)
    {
        string Root() { string path = Path.Combine(_root, "continuation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        void Junction(string path, string target)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string argument in new[] { "/c", "mklink", "/J", path, target }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException(process.StandardError.ReadToEnd());
        }
        DiscoveredModule Package(string source)
        {
            string directory = Path.Combine(source, "package"); Directory.CreateDirectory(directory);
            var manifest = new ModuleManifest { Id = "package", Name = "Package", Assembly = "Example.dll", EntryType = "Example.Entry" };
            File.WriteAllText(Path.Combine(directory, "module.json"), JsonSerializer.Serialize(manifest));
            File.WriteAllText(Path.Combine(directory, "Example.dll"), "fixture");
            return new(manifest, directory, ModuleCatalog.Fingerprint(directory));
        }
        {
            string root = Root(); File.WriteAllText(Path.Combine(root, "module-startup.json"), new string('x', 65537));
            try
            {
                var guard = new ModuleStartupGuard(root);
                Check("continuation: oversized startup journal is contained and requests recovery", guard.Recovery is { Required: true, Unreadable: true });
            }
            catch { Check("continuation: oversized startup journal is contained and requests recovery", false); }
        }
        {
            string root = Root(); new ModuleStartupGuard(root).Begin(["example"]);
            File.WriteAllText(Path.Combine(root, "module-startup-dismissed.json"), new string('x', 65537));
            try
            {
                var guard = new ModuleStartupGuard(root);
                Check("continuation: oversized dismissal cannot interrupt reading the valid recovery journal", guard.Recovery.Required && guard.Recovery.Modules.SequenceEqual(["example"]));
            }
            catch { Check("continuation: oversized dismissal cannot interrupt reading the valid recovery journal", false); }
        }
        {
            string source = Root(), output = Root(), outside = Root(); var package = Package(source);
            string link = Path.Combine(output, "linked"); Junction(link, outside);
            try
            {
                var catalog = new ModuleCatalog(source, Path.Combine(link, "cache"), []);
                Throws("continuation: loader rejects linked cache ancestors", () => catalog.Definition(package, true).Create());
                Check("continuation: cache path is checked before creating any external directory", !Directory.Exists(Path.Combine(outside, "cache")));
            }
            finally { Directory.Delete(link); }
        }
        {
            string source = Root(), output = Root(), outside = Root(); Package(source);
            string link = Path.Combine(output, "linked"); Junction(link, outside);
            try
            {
                Throws("continuation: bundle installer rejects linked destination ancestors", () => ModuleBundleInstaller.Install(source, Path.Combine(link, "installed"), new Dictionary<string, string>()));
                Check("continuation: installation path is checked before creating external directories", !Directory.Exists(Path.Combine(outside, "installed")));
            }
            finally { Directory.Delete(link); }
        }
        {
            string source = Root(); var package = Package(source); string nested = Path.Combine(source, "installed");
            Throws("continuation: bundle source and destination cannot contain each other", () => ModuleBundleInstaller.Install(source, nested, new Dictionary<string, string>()));
            Check("continuation: overlapping install leaves source untouched", !Directory.Exists(nested) && File.Exists(Path.Combine(package.Directory, "Example.dll")));
        }
        {
            string target = Root(), source = Path.Combine(target, "bundled"); Package(source);
            Throws("continuation: installer also rejects a destination containing its source", () => ModuleBundleInstaller.Install(source, target, new Dictionary<string, string>()));
        }
        {
            string root = Root();
            File.WriteAllText(Path.Combine(root, "00-native.dll"), "not a managed assembly");
            File.Copy(Path.Combine(binaries, "BrimDeck.UI.dll"), Path.Combine(root, "later.dll"));
            Check("continuation: an unreadable DLL cannot hide later internal references", ModuleAssemblyScan.ReferencesInternals(root));
        }
        {
            string root = Root(), subdirectory = Path.Combine(root, "private"); Directory.CreateDirectory(subdirectory);
            File.Copy(Path.Combine(binaries, "BrimDeck.UI.dll"), Path.Combine(subdirectory, "helper.dll"));
            Check("continuation: internal references in nested private DLLs are detected", ModuleAssemblyScan.ReferencesInternals(root));
        }
        {
            string root = Root(); var package = Package(root);
            File.WriteAllText(Path.Combine(package.Directory, "module.json"), "{\"id\":\"package\",\"name\":\"Package\",\"assembly\":\"Example.dll\",\"entryType\":\"Example.Entry\",\"host\":{\"minimumVersion\":\"1.0\",\"MinimumVersion\":\"2.0\"}}");
            var catalog = new ModuleCatalog(root, "", []);
            Check("continuation: host version bounds reject duplicate manifest fields", catalog.Discover().Count == 0 && catalog.Failures.Single().Error.Contains("Duplicate manifest field"));
            Check("continuation: checker and runtime agree on duplicate host version fields", ModulePackageInspector.Inspect(package.Directory).Issues.Any(i => i.Code == "duplicate-manifest-field"));
        }
        {
            string root = Root(), outside = Root(); File.WriteAllText(Path.Combine(outside, "secret.txt"), "retained");
            string link = Path.Combine(root, "linked"); Junction(link, outside);
            try
            {
                Check("continuation: signature digest refuses linked package roots", ModuleSignatureVerifier.Digest(link) is null);
                Check("continuation: signature digest refuses linked package subdirectories", ModuleSignatureVerifier.Digest(root) is null);
            }
            finally { Directory.Delete(link); }
        }
    }
}
