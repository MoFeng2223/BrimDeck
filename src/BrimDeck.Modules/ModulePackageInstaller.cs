namespace BrimDeck.Modules;

internal sealed record ModuleInstallResult(string Id, string Digest, string Directory, string? RestorePoint, bool Pending, string? Error = null);

// Third-party imports never mint bundle receipts or approvals. Business data is outside every path touched here.
internal sealed class ModulePackageInstaller(string dataRoot, Action<string>? checkpoint = null)
{
    private sealed record Transaction(int Version, string Id, string Target, string Digest, string StagedIdentity,
        string? PreviousDigest, string? PreviousIdentity, string Phase);
    internal ModuleInstallResult Install(string source, string? expectedDigest = null, DiscoveredModule? replacing = null)
        => Stage(source, expectedDigest, replacing, restoring: false);
    private ModuleInstallResult Stage(string source, string? expectedDigest, DiscoveredModule? replacing, bool restoring)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        string root = Path.GetFullPath(dataRoot), mods = Path.Combine(root, "mods");
        ModuleManifestReader.Unlinked(source); ModuleManifestReader.Unlinked(root, allowMissing: true);
        var report = ModulePackageInspector.Inspect(source);
        if (!report.Passed || report.Manifest is null || report.Digest is null)
            throw new InvalidDataException(string.Join("; ", report.Issues.Where(i => i.Severity == ModuleCheckSeverity.Error).Select(i => i.Code + ": " + i.Message)));
        if (expectedDigest is not null && report.Digest != expectedDigest) throw new IOException("Plugin changed after confirmation. Inspect and confirm it again.");
        string id = report.Manifest.Id;
        var catalog = new ModuleCatalog(mods, "", []); var installed = catalog.Discover();
        if (catalog.Failures.Count > 0) throw new IOException("Resolve installed package discovery errors before importing: " + catalog.Failures[0].Error);
        var current = installed.SingleOrDefault(p => p.Manifest.Id == id);
        if (current is not null && (replacing is null || current.Directory != replacing.Directory || current.Digest != replacing.Digest))
            throw new IOException("Plugin already installed. Confirm replacement of the current package.");
        if (current is null && replacing is not null) throw new IOException("The package to update is no longer installed.");
        string target = current?.Directory ?? Path.Combine(mods, id);
        static bool Within(string a, string b) => (a + Path.DirectorySeparatorChar).StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!restoring && (Within(source, root) || Within(root, source))) throw new IOException("Import source and application data directories must not overlap.");
        foreach (string directory in Transactions())
            if (Read(directory).Phase is "staged" or "previous-saved")
                throw new IOException("Resolve pending plugin installations before importing another package.");
        ModuleManifestReader.Unlinked(target, allowMissing: true);
        if (current is null && (Directory.Exists(target) || File.Exists(target))) throw new IOException("Installation directory already exists and is not this plugin.");
        string transaction = Path.Combine(root, ".module-updates", Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(transaction, "package");
        ModuleManifestReader.Unlinked(transaction, allowMissing: true);
        Directory.CreateDirectory(staging);
        foreach (string file in ModuleCatalog.Files(source))
        {
            string output = Path.Combine(staging, Path.GetRelativePath(source, file));
            ModuleManifestReader.Unlinked(file); ModuleManifestReader.Unlinked(output, allowMissing: true);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(file, output);
        }
        if (ModuleCatalog.Fingerprint(source) != report.Digest || ModuleCatalog.Fingerprint(staging) != report.Digest) throw new IOException("Plugin changed during staging. Original installation was retained.");
        var state = new Transaction(1, id, Path.GetFileName(target), report.Digest, ModuleDirectoryIdentity.Read(staging)!,
            current?.Digest, current is null ? null : ModuleDirectoryIdentity.Read(target), "staged");
        new ModuleStorage(transaction).Write("transaction", state);
        checkpoint?.Invoke("staged");
        return Complete(transaction, state);
    }
    private Transaction Read(string directory)
    {
        ModuleManifestReader.Unlinked(directory);
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) != Path.Combine(Path.GetFullPath(dataRoot), ".module-updates")
            || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) throw new IOException("Invalid plugin transaction directory.");
        var state = new ModuleStorage(directory).Read<Transaction>("transaction") ?? throw new IOException("Missing plugin transaction journal.");
        static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        static bool Identity(string? value) => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
        if (state.Version != 1 || !ModuleIds.Valid(state.Id) || string.IsNullOrWhiteSpace(state.Target) || state.Target.StartsWith('.')
            || state.Target.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(state.Target) != state.Target
            || !Hash(state.Digest) || !Identity(state.StagedIdentity)
            || (state.PreviousDigest is not null && !Hash(state.PreviousDigest))
            || (state.PreviousIdentity is not null && !Identity(state.PreviousIdentity))
            || (state.PreviousDigest is null) != (state.PreviousIdentity is null)
            || state.Phase is not ("staged" or "previous-saved" or "installed" or "rolled-back")) throw new IOException("Invalid plugin transaction journal.");
        return state;
    }
    private IEnumerable<string> Transactions()
    {
        string directory = Path.Combine(Path.GetFullPath(dataRoot), ".module-updates");
        ModuleManifestReader.Unlinked(directory, allowMissing: true);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateDirectories(directory).OrderBy(Directory.GetCreationTimeUtc).ThenBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
    }
    internal IReadOnlyList<string> PendingIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string directory in Transactions())
        {
            try { var state = Read(directory); if (state.Phase is "staged" or "previous-saved") ids.Add(state.Id); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ModuleApiException) { }
        }
        return ids.ToArray();
    }
    internal IReadOnlyList<ModuleInstallResult> Recover()
    {
        var results = new List<ModuleInstallResult>();
        foreach (string directory in Transactions())
        {
            try
            {
                var state = Read(directory);
                if (state.Phase is "installed" or "rolled-back") continue;
                results.Add(Complete(directory, state));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ModuleApiException)
            { results.Add(new("", "", directory, null, true, error.Message)); }
        }
        return results;
    }
    internal IReadOnlyList<string> RestorePoints(string id)
    {
        ModuleIds.Require(id); var points = new List<string>();
        foreach (string directory in Transactions())
        {
            try
            {
                var state = Read(directory); string previous = Path.Combine(directory, "previous");
                if (state.Id == id && state.Phase == "installed" && state.PreviousIdentity is not null
                    && Matches(previous, state.PreviousIdentity, state.PreviousDigest!)) points.Add(previous);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ModuleApiException) { }
        }
        return points;
    }
    internal ModuleInstallResult Restore(string point, DiscoveredModule replacing)
    {
        point = Path.GetFullPath(point);
        if (Path.GetFileName(point) != "previous") throw new IOException("Expected a package restore point.");
        var state = Read(Path.GetDirectoryName(point)!);
        if (state.Id != replacing.Manifest.Id || state.Phase != "installed" || state.PreviousIdentity is null
            || !Matches(point, state.PreviousIdentity, state.PreviousDigest!)) throw new IOException("Restore point changed or belongs to another plugin.");
        // Restore only code. Business data and its schema never roll back implicitly.
        return Stage(point, state.PreviousDigest, replacing, restoring: true);
    }
    private static bool Matches(string directory, string identity, string digest)
    {
        ModuleManifestReader.Unlinked(directory, allowMissing: true);
        return Directory.Exists(directory) && ModuleDirectoryIdentity.Read(directory) == identity && ModuleCatalog.Fingerprint(directory) == digest;
    }
    private ModuleInstallResult Complete(string directory, Transaction state)
    {
        string mods = Path.Combine(Path.GetFullPath(dataRoot), "mods"), target = Path.Combine(mods, state.Target);
        string staging = Path.Combine(directory, "package"), previous = Path.Combine(directory, "previous");
        void Save(string phase) { state = state with { Phase = phase }; new ModuleStorage(directory).Write("transaction", state); }
        try
        {
            ModuleManifestReader.Unlinked(target, allowMissing: true);
            if (Matches(target, state.StagedIdentity, state.Digest))
            { Save("installed"); return new(state.Id, state.Digest, target, Directory.Exists(previous) ? previous : null, false); }
            if (!Matches(staging, state.StagedIdentity, state.Digest))
            {
                if (!Directory.Exists(target) && !File.Exists(target) && state.PreviousIdentity is not null && Matches(previous, state.PreviousIdentity, state.PreviousDigest!))
                { Directory.Move(previous, target); Save("rolled-back"); }
                throw new IOException("Staged package changed or is missing. Previous package and business data were retained.");
            }
            if (Directory.Exists(target) || File.Exists(target))
            {
                if (state.PreviousIdentity is null || !Matches(target, state.PreviousIdentity, state.PreviousDigest!) || Directory.Exists(previous))
                    throw new IOException("Installation target has been replaced; no files were overwritten.");
                Directory.Move(target, previous); Save("previous-saved"); checkpoint?.Invoke("previous-saved");
            }
            else if (state.PreviousIdentity is not null && !Matches(previous, state.PreviousIdentity, state.PreviousDigest!))
                throw new IOException("Previous package identity changed. Installation remains pending.");
            Directory.CreateDirectory(mods); ModuleManifestReader.Unlinked(target, allowMissing: true);
            Directory.Move(staging, target); checkpoint?.Invoke("package-swapped"); Save("installed");
            return new(state.Id, state.Digest, target, Directory.Exists(previous) ? previous : null, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(state.Id, state.Digest, target, Directory.Exists(previous) ? previous : null, true, error.Message); }
    }
}
