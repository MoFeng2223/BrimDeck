using System.Text.Json;

namespace BrimDeck.Modules;

internal sealed record ModuleDiskUsage(long? DataBytes, long? CacheBytes, bool CachePending, bool RemovalPending, string? Error);
internal sealed record ModuleFileResult(bool Pending, string? Error = null, bool ReplacementPreserved = false);

// Host-owned maintenance state. It never deletes a path supplied directly by a manifest.
// A removal first moves each owned directory to a unique trash location. Retried deletion
// can therefore never target a later installation or its freshly created data.
internal sealed class ModuleFiles
{
    private sealed class Installed
    { public Installed() { } public string Id { get; set; } = ""; public string Folder { get; set; } = ""; public string Digest { get; set; } = ""; }
    private sealed class Removal
    {
        public Removal() { }
        public string Id { get; set; } = ""; public string Folder { get; set; } = ""; public string Digest { get; set; } = "";
        public string Token { get; set; } = "";
        public string? PackageIdentity { get; set; } public string? DataIdentity { get; set; } public string? CacheIdentity { get; set; }
        public bool ReplacementPreserved { get; set; }
        public bool PackageMoved { get; set; } public bool DataMoved { get; set; } public bool CacheMoved { get; set; }
    }
    private sealed class FileState
    {
        public FileState() { }
        public int Version { get; set; } = 1;
        public List<Installed> Installed { get; set; } = [];
        public List<Removal> Removing { get; set; } = [];
        public List<string> CacheRequests { get; set; } = [];
        public List<string> SuppressedBundles { get; set; } = [];
    }
    private readonly object _gate = new();
    private readonly string _root, _journal;
    private FileState _state = new();
    private string? _stateError;
    internal ModuleFiles(string directory)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _journal = Path.Combine(_root, "module-files.json");
        try
        {
            Guard(_journal);
            if (File.Exists(_journal))
            {
                if (new FileInfo(_journal).Length > 1024 * 1024) throw new InvalidDataException("Module maintenance journal is too large.");
                using var input = new FileStream(_journal, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > 1024 * 1024) throw new InvalidDataException("Module maintenance journal is too large.");
                using var document = JsonDocument.Parse(input);
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1
                    || new[] { "Installed", "Removing", "CacheRequests", "SuppressedBundles" }.Any(name => !document.RootElement.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array))
                    throw new InvalidDataException("Incomplete module maintenance journal.");
                _state = document.RootElement.Deserialize<FileState>(new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                    ?? throw new InvalidDataException("Invalid module maintenance journal.");
                if (_state.Version != 1 || _state.Installed is null || _state.Removing is null || _state.CacheRequests is null || _state.SuppressedBundles is null
                    || _state.Installed.Count > 4096 || _state.Removing.Count > 4096 || _state.CacheRequests.Count > 4096 || _state.SuppressedBundles.Count > 4096
                    || _state.Installed.Any(e => e is null || !ModuleIds.Valid(e.Id) || !Folder(e.Folder) || !Digest(e.Digest))
                    || _state.Removing.Any(e => e is null || !ModuleIds.Valid(e.Id) || !Folder(e.Folder) || !Digest(e.Digest) || !Guid.TryParseExact(e.Token, "N", out _)
                        || !Identity(e.PackageIdentity) || !Identity(e.DataIdentity) || !Identity(e.CacheIdentity))
                    || _state.CacheRequests.Concat(_state.SuppressedBundles).Any(id => !ModuleIds.Valid(id))
                    || _state.Installed.Select(e => e.Id).Distinct().Count() != _state.Installed.Count || _state.Removing.Select(e => e.Id).Distinct().Count() != _state.Removing.Count
                    || _state.Removing.Select(e => e.Token).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _state.Removing.Count)
                    throw new InvalidDataException("Invalid module maintenance journal entries.");
            }
        }
        catch (Exception error) when (FileError(error) || error is JsonException)
        { _state = new(); _stateError = error.Message; }
    }
    internal IReadOnlySet<string> SuppressedBundles { get { lock (_gate) return _state.SuppressedBundles.ToHashSet(StringComparer.Ordinal); } }
    internal IReadOnlySet<string> PendingRemovals { get { lock (_gate) return _state.Removing.Select(r => r.Id).ToHashSet(StringComparer.Ordinal); } }
    internal bool HasPending { get { lock (_gate) return _state.Removing.Count > 0 || _state.CacheRequests.Count > 0; } }
    internal int PendingCount { get { lock (_gate) return _state.Removing.Count + _state.CacheRequests.Count; } }
    internal string? Problem => _stateError;
    private static bool Folder(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") && !value.StartsWith('.')
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && Path.GetFileName(value) == value;
    private static bool Digest(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Identity(string? value) => value is null || value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    private static bool FileError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    private void Writable() { if (_stateError is not null) throw new IOException("Module maintenance journal needs repair: " + _stateError); }
    private string Child(string area, string name) { string path = Path.Combine(_root, area, name); Guard(path); return path; }
    private void Guard(string path)
    {
        string full = Path.GetFullPath(path);
        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Module path escapes the application data directory.");
        // Check all existing ancestors as well, including junctions above the data root.
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked module paths are not supported."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    private void Save()
    {
        Writable(); Guard(_journal); Directory.CreateDirectory(_root);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(_state);
        if (bytes.Length > 1024 * 1024) throw new IOException("Module maintenance journal is too large.");
        string temporary = Path.Combine(_root, ".module-files-" + Guid.NewGuid().ToString("N") + ".tmp"); Guard(temporary);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            Guard(_journal); File.Move(temporary, _journal, true);
        }
        finally { Guard(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal void Remember(IReadOnlyList<DiscoveredModule> modules)
    {
        lock (_gate)
        {
            Writable(); bool changed = false; var previousEntries = _state.Installed; _state.Installed = [.. previousEntries];
            try
            {
            foreach (var module in modules)
            {
                if (_state.Removing.Any(r => r.Id == module.Manifest.Id)) continue;
                string full = Path.GetFullPath(module.Directory), parent = Path.GetDirectoryName(full)!;
                if (!string.Equals(parent, Path.Combine(_root, "mods"), StringComparison.OrdinalIgnoreCase)) throw new IOException("Expected a direct module installation directory.");
                Guard(full); string folder = Path.GetFileName(full);
                if (!Folder(folder) || !Digest(module.Digest)) throw new IOException("Invalid module installation identity.");
                var previous = _state.Installed.Find(e => e.Id == module.Manifest.Id);
                if (previous?.Folder == folder && previous.Digest == module.Digest) continue;
                _state.Installed.RemoveAll(e => e.Id == module.Manifest.Id);
                _state.Installed.Add(new() { Id = module.Manifest.Id, Folder = folder, Digest = module.Digest }); changed = true;
            }
            if (changed) Save();
            }
            catch { _state.Installed = previousEntries; throw; }
        }
    }
    internal IReadOnlyList<string> Missing(IReadOnlyList<DiscoveredModule> present)
    {
        lock (_gate)
        {
            Writable(); string mods = Child("mods", "");
            if (!Directory.Exists(mods)) return [];
            return _state.Installed.Where(e => !present.Any(p => p.Manifest.Id == e.Id)
                && !Directory.Exists(Child("mods", e.Folder)) && !File.Exists(Child("mods", e.Folder))).Select(e => e.Id).ToArray();
        }
    }
    // Data directories with no installation record: the package is gone (or was never recorded), yet the business
    // data stayed. Never cleaned automatically, because the module may be reinstalled; offered in the UI instead.
    internal IReadOnlyList<string> Orphans()
    {
        lock (_gate)
        {
            if (Problem is not null) return [];
            string data = Child("modules-data", "");
            if (!Directory.Exists(data)) return [];
            string[] names;
            try { names = Directory.EnumerateDirectories(data).Select(Path.GetFileName).Where(name => name is not null).ToArray()!; }
            catch (Exception error) when (FileError(error)) { return []; }
            return names.Where(name => ModuleIds.Valid(name) && !_state.Installed.Any(e => e.Id == name) && !_state.Removing.Any(r => r.Id == name))
                .Order(StringComparer.Ordinal).ToArray();
        }
    }
    internal void RequestRemoval(string id, DiscoveredModule? module = null)
    {
        ModuleIds.Require(id);
        lock (_gate)
        {
            Writable();
            if (module is not null)
            {
                string source = Path.GetFullPath(module.Directory); Guard(source);
                if (!string.Equals(Path.GetDirectoryName(source), Path.Combine(_root, "mods"), StringComparison.OrdinalIgnoreCase)) throw new IOException("Expected an owned installation directory.");
                if (module.Manifest.Id != id || ModuleCatalog.Fingerprint(source) != module.Digest) throw new IOException("Module files changed; refresh before uninstalling.");
            }
            if (_state.Removing.Find(r => r.Id == id) is { } pending)
            {
                if (module is not null && !pending.PackageMoved)
                {
                    var previous = (pending.Digest, pending.Folder, pending.PackageIdentity, pending.DataIdentity, pending.CacheIdentity);
                    var identities = (Package: ModuleDirectoryIdentity.Read(module.Directory),
                        Data: !pending.DataMoved ? ModuleDirectoryIdentity.Read(Child("modules-data", id)) : pending.DataIdentity,
                        Cache: !pending.CacheMoved ? ModuleDirectoryIdentity.Read(Child("modules-cache", id)) : pending.CacheIdentity);
                    pending.Digest = module.Digest; pending.Folder = Path.GetFileName(module.Directory);
                    pending.PackageIdentity = identities.Package; pending.DataIdentity = identities.Data; pending.CacheIdentity = identities.Cache;
                    try { Save(); }
                    catch { (pending.Digest, pending.Folder, pending.PackageIdentity, pending.DataIdentity, pending.CacheIdentity) = previous; throw; }
                }
                return;
            }
            if (module is not null) Remember([module]);
            var installed = _state.Installed.Find(e => e.Id == id);
            // An orphan has no installation record; only its data and cache are removed, never a package folder.
            if (installed is null && module is not null) throw new IOException("Unknown module installation.");
            // Placeholders keep the journal's entry validation unchanged for a removal without a package.
            var removal = new Removal
            {
                Id = id, Folder = installed?.Folder ?? "orphan", Digest = installed?.Digest ?? new string('0', 64),
                Token = Guid.NewGuid().ToString("N"),
                PackageIdentity = installed is null ? null : ModuleDirectoryIdentity.Read(Child("mods", installed.Folder)),
                DataIdentity = ModuleDirectoryIdentity.Read(Child("modules-data", id)), CacheIdentity = ModuleDirectoryIdentity.Read(Child("modules-cache", id)) };
            removal.PackageMoved = removal.PackageIdentity is null; removal.DataMoved = removal.DataIdentity is null; removal.CacheMoved = removal.CacheIdentity is null;
            var previousSuppressed = _state.SuppressedBundles.ToList(); var previousCache = _state.CacheRequests.ToList();
            _state.Removing.Add(removal);
            if (!_state.SuppressedBundles.Contains(id)) _state.SuppressedBundles.Add(id);
            _state.CacheRequests.Remove(id);
            try { Save(); }
            catch { _state.Removing.Remove(removal); _state.SuppressedBundles = previousSuppressed; _state.CacheRequests = previousCache; throw; }
        }
    }
    internal void RestoreBundles()
    {
        lock (_gate)
        {
            Writable(); var previous = _state.SuppressedBundles; _state.SuppressedBundles = previous.Where(id => _state.Removing.Any(r => r.Id == id)).ToList();
            try { Save(); } catch { _state.SuppressedBundles = previous; throw; }
        }
    }
    internal ModuleFileResult ClearCache(string id, IReadOnlyList<string> inUse)
    {
        ModuleIds.Require(id);
        lock (_gate)
        {
            Writable(); if (!_state.CacheRequests.Contains(id)) { _state.CacheRequests.Add(id); try { Save(); } catch { _state.CacheRequests.Remove(id); throw; } }
            return CleanCache(id, inUse);
        }
    }
    private ModuleFileResult CleanCache(string id, IReadOnlyList<string> inUse)
    {
        string root = Child("modules-cache", id); bool pending = false; string? problem = null;
        // Leases and directory enumeration can use different separators or a trailing slash.
        // Compare directory identities, including ancestor leases, before deleting any cache entry.
        var protectedPaths = inUse.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).ToArray();
        try
        {
            if (File.Exists(root)) DeleteTree(root);
            else if (Directory.Exists(root))
            {
                foreach (string child in Directory.GetFileSystemEntries(root))
                {
                    string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
                    if (protectedPaths.Any(p => string.Equals(p, candidate, StringComparison.OrdinalIgnoreCase)
                        || p.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || candidate.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) { pending = true; continue; }
                    try { DeleteTree(child); }
                    catch (Exception error) when (FileError(error)) { pending = true; problem = error.Message; }
                }
                if (!pending) Directory.Delete(root);
            }
        }
        catch (Exception error) when (FileError(error)) { pending = true; problem = error.Message; }
        if (!pending)
        {
            int index = _state.CacheRequests.IndexOf(id); _state.CacheRequests.Remove(id);
            try { Save(); } catch { if (index >= 0) _state.CacheRequests.Insert(index, id); throw; }
        }
        return new(pending, problem);
    }
    internal ModuleFileResult ProcessPending(IReadOnlyList<string> inUse)
    {
        lock (_gate)
        {
            Writable(); string? problem = null; bool preserved = false;
            foreach (var removal in _state.Removing.ToArray())
            {
                try
                {
                    string trash = Child(".module-trash", removal.Token);
                    string package = Child("mods", removal.Folder);
                    if (!removal.PackageMoved && removal.PackageIdentity is not null && !Directory.Exists(Path.Combine(trash, "package"))
                        && ModuleDirectoryIdentity.Read(package) == removal.PackageIdentity
                        && ModuleCatalog.Fingerprint(package) != removal.Digest) throw new IOException("Module files changed; refresh before uninstalling.");
                    void Move(string source, string name, string? identity, bool moved, Action<bool> mark)
                    {
                        string target = Path.Combine(trash, name); Guard(source); Guard(target);
                        string? actual = ModuleDirectoryIdentity.Read(source);
                        bool previousPreserved = removal.ReplacementPreserved;
                        if (moved)
                        {
                            if (actual is not null && !previousPreserved)
                            {
                                removal.ReplacementPreserved = true;
                                try { Save(); } catch { removal.ReplacementPreserved = previousPreserved; throw; }
                            }
                            return;
                        }
                        if (!Directory.Exists(target) && actual is not null)
                        {
                            if (identity is null || actual != identity) removal.ReplacementPreserved = true;
                            else
                            {
                                if (name == "package" && ModuleCatalog.Fingerprint(source) != removal.Digest) throw new IOException("Module files changed; refresh before uninstalling.");
                                Directory.CreateDirectory(trash); Directory.Move(source, target);
                            }
                        }
                        else if (Directory.Exists(target) && actual is not null) removal.ReplacementPreserved = true;
                        mark(true);
                        try { Save(); } // Ownership transfer must be durable before deletion.
                        catch { mark(false); removal.ReplacementPreserved = previousPreserved; throw; }
                    }
                    var problems = new List<string>();
                    void Area(string source, string name, string? identity, bool moved, Action<bool> mark)
                    {
                        try { Move(source, name, identity, moved, mark); DeleteTree(Path.Combine(trash, name)); }
                        catch (Exception failure) when (FileError(failure)) { problems.Add(failure.Message); }
                    }
                    Area(package, "package", removal.PackageIdentity, removal.PackageMoved, value => removal.PackageMoved = value);
                    Area(Child("modules-data", removal.Id), "data", removal.DataIdentity, removal.DataMoved, value => removal.DataMoved = value);
                    Area(Child("modules-cache", removal.Id), "cache", removal.CacheIdentity, removal.CacheMoved, value => removal.CacheMoved = value);
                    if (problems.Count > 0) throw new IOException(string.Join("; ", problems.Distinct()));
                    DeleteTree(trash);
                    var previousInstalled = _state.Installed; _state.Installed = previousInstalled.Where(e => e.Id != removal.Id).ToList();
                    int index = _state.Removing.IndexOf(removal); _state.Removing.Remove(removal);
                    try { Save(); } catch { _state.Removing.Insert(index, removal); _state.Installed = previousInstalled; throw; }
                }
                catch (Exception error) when (FileError(error)) { problem = error.Message; }
                preserved |= removal.ReplacementPreserved;
            }
            foreach (string id in _state.CacheRequests.ToArray())
            {
                try { var result = CleanCache(id, inUse); problem ??= result.Error; }
                catch (Exception failure) when (FileError(failure)) { problem ??= failure.Message; }
            }
            return new(HasPending, problem, preserved);
        }
    }
    internal ModuleDiskUsage Measure(string id)
    {
        ModuleIds.Require(id);
        lock (_gate)
        {
            long? data = null, cache = null; string? error = _stateError;
            try { data = Size(Child("modules-data", id)); foreach (var job in _state.Removing.Where(r => r.Id == id)) data += Size(Child(".module-trash", job.Token) + Path.DirectorySeparatorChar + "data"); }
            catch (Exception failure) when (FileError(failure)) { data = null; error = failure.Message; }
            try { cache = Size(Child("modules-cache", id)); foreach (var job in _state.Removing.Where(r => r.Id == id)) cache += Size(Child(".module-trash", job.Token) + Path.DirectorySeparatorChar + "cache"); }
            catch (Exception failure) when (FileError(failure)) { cache = null; error = failure.Message; }
            return new(data, cache, _state.CacheRequests.Contains(id), _state.Removing.Any(r => r.Id == id), error);
        }
    }
    private long Size(string path)
    {
        long total = 0; foreach (string file in Tree(path).Where(File.Exists)) { long length = new FileInfo(file).Length; total = length > long.MaxValue - total ? long.MaxValue : total + length; } return total;
    }
    private IReadOnlyList<string> Tree(string path)
    {
        var entries = new List<string>();
        void Visit(string current, int depth)
        {
            Guard(current); if (depth > 64 || entries.Count >= 100000) throw new IOException("Module directory is too large to maintain.");
            if (!Directory.Exists(current) && !File.Exists(current)) return;
            entries.Add(current);
            if (Directory.Exists(current)) foreach (string child in Directory.GetFileSystemEntries(current)) Visit(child, depth + 1);
        }
        Visit(path, 0); return entries;
    }
    private void DeleteTree(string path)
    {
        foreach (string entry in Tree(path).Reverse())
        {
            Guard(entry);
            if (Directory.Exists(entry)) Directory.Delete(entry);
            else if (File.Exists(entry)) { File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly); File.Delete(entry); }
        }
    }
}
