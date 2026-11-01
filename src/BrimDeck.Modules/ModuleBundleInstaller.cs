namespace BrimDeck.Modules;

public sealed record ModuleBundleChange(string Id, string? PreviousDigest, string Digest);

// Distribution bootstrap only; installed modules still use the ordinary catalog, approval and load context.
// Receipts are host-owned. An unrelated manifest cannot grant itself auto-start or replace an installed module.
public static class ModuleBundleInstaller
{
    public static IReadOnlyList<ModuleBundleChange> Install(string source, string destination, IDictionary<string, string> receipts)
        => Install(source, destination, receipts, null);
    internal static IReadOnlyList<ModuleBundleChange> Install(string source, string destination, IDictionary<string, string> receipts, IReadOnlySet<string>? suppressed)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        if (!Directory.Exists(source)) return [];
        string targetRoot = Path.GetFullPath(destination);
        string sourceTree = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string targetTree = targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (sourceTree.StartsWith(targetTree, StringComparison.OrdinalIgnoreCase) || targetTree.StartsWith(sourceTree, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Bundle source and installation directory must not overlap.");
        ModuleManifestReader.Unlinked(targetRoot, allowMissing: true);
        var catalog = new ModuleCatalog(source, Path.Combine(destination, ".unused-cache"), []);
        var modules = catalog.Discover();
        if (catalog.Failures.Count > 0) throw new InvalidDataException("Invalid bundled module: " + catalog.Failures[0].Error);
        Directory.CreateDirectory(targetRoot);
        ModuleManifestReader.Unlinked(targetRoot);
        var changed = new List<ModuleBundleChange>();
        foreach (var module in modules)
        {
            if (suppressed?.Contains(module.Manifest.Id) == true) continue;
            string target = Path.Combine(targetRoot, module.Manifest.Id);
            ModuleManifestReader.Unlinked(target, allowMissing: true);
            string? previous = receipts.TryGetValue(module.Manifest.Id, out var receipt) ? receipt : null;
            if (Directory.Exists(target))
            {
                string actual = ModuleCatalog.Fingerprint(target);
                if (actual == module.Digest) continue;
                if (previous != actual) continue;
            }
            string staging = Path.Combine(targetRoot, ".install-" + Guid.NewGuid().ToString("N"));
            string backup = Path.Combine(targetRoot, ".previous-" + Guid.NewGuid().ToString("N"));
            bool installed = false;
            try
            {
                // Fingerprinting checks links, total bytes and file count before and after the copy.
                if (ModuleCatalog.Fingerprint(module.Directory) != module.Digest) throw new IOException("Bundle changed during installation.");
                Directory.CreateDirectory(staging);
                foreach (string file in ModuleCatalog.Files(module.Directory))
                {
                    string output = Path.Combine(staging, Path.GetRelativePath(module.Directory, file));
                    ModuleManifestReader.Unlinked(file); ModuleManifestReader.Unlinked(output, allowMissing: true);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    ModuleManifestReader.Unlinked(output, allowMissing: true); File.Copy(file, output);
                }
                if (ModuleCatalog.Fingerprint(staging) != module.Digest) throw new IOException("Bundle changed during installation.");
                if (Directory.Exists(target)) Directory.Move(target, backup);
                try { Directory.Move(staging, target); }
                catch { if (Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target); throw; }
                receipts[module.Manifest.Id] = module.Digest;
                changed.Add(new(module.Manifest.Id, previous, module.Digest));
                installed = true;
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                // If restoring a failed swap also failed, preserve the only copy of the previous installation.
                if (installed && Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            }
        }
        return changed.AsReadOnly();
    }
}
