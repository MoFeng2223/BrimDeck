using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Island;

namespace BrimDeck;

// The extension pages' host: every DLL in the extensions folder that carries an IIslandExtension is loaded into a
// collectible assembly context and given a slot, in file order, up to the four slots the page switch holds. A DLL that
// fails to load is skipped, noted by name for the plugins page, and never takes the app with it.
//
// A scan is a full hot reload: a DLL that changed on disk is loaded fresh and every extension it exports replaces its
// slot, a DLL taken away has its slots retired, and what did not change is kept as it is. Each DLL is loaded from a
// copy in a cache folder, so the plugin folder itself stays writable while the app runs.
internal static class IslandExtensions
{
    public const int ApiVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record PluginManifest(string Id, string Entry, int ApiVersion = 1, string Version = "1.0.0", string Repository = "");
    private static bool ValidId(string? id) => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');

    public static readonly IIslandExtension?[] Slots = new IIslandExtension?[4];
    private static readonly AssemblyLoadContext?[] _contexts = new AssemblyLoadContext?[4];
    private static readonly string?[] _sources = new string?[4];
    // The id each extension reported when it was created; the host never asks a plugin for it a second time.
    private static readonly string?[] _ids = new string?[4];
    private static readonly string?[] _stamps = new string?[4];
    private static readonly Dictionary<string, HashSet<string>> _subscriptions = new(StringComparer.OrdinalIgnoreCase);

    public static string Folder { get; } = Path.Combine(AppPaths.Data, "extensions");
    // The copies the contexts are loaded from: a loaded assembly locks its file on Windows, and the plugin folder must
    // stay writable so a new DLL can be dropped over the old one.
    private static string CacheFolder => Path.Combine(AppPaths.Data, "extension-cache");
    public static string Source(int slot) => _sources[slot] is { } file ? Path.GetRelativePath(Folder, file) : "";

    // The DLLs that could not be loaded, by file name with the error's first line. Cleared on each scan.
    public static readonly List<(string File, string Error)> Failures = new();
    public static readonly List<(string File, string Note)> Skipped = new();

    // Raised with the slots whose extension was replaced or retired; the app drops their tabs and views and re-renders.
    public static event Action<IReadOnlyList<int>>? ExtensionsChanged;

    public static void Load() => Scan();

    public static void Scan()
    {
        Failures.Clear();
        Skipped.Clear();
        if (Settings?.ExtensionsEnabled != true)
        {
            var disabledChanges = new List<int>();
            for (int slot = 0; slot < Slots.Length; slot++)
            {
                if (Slots[slot] is null) continue;
                Notify(slot, false);
                try { Slots[slot]?.Dispose(); } catch { }
                _contexts[slot]?.Unload();
                Slots[slot] = null; _contexts[slot] = null; _sources[slot] = null; _ids[slot] = null; disabledChanges.Add(slot);
            }
            Skipped.Add((Folder, Loc.T("插件执行已关闭。", "Plugin execution is disabled.")));
            if (disabledChanges.Count > 0) ExtensionsChanged?.Invoke(disabledChanges);
            return;
        }
        var disk = new List<string>();
        var declarations = new Dictionary<string, PluginManifest>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(Folder))
            {
                // Keep the original root-level DLL format working for existing plugin prototypes.
                disk.AddRange(Directory.EnumerateFiles(Folder, "*.dll", SearchOption.TopDirectoryOnly));
                foreach (var pluginFolder in Directory.EnumerateDirectories(Folder).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    if (HasReparsePoints(pluginFolder)) { Failures.Add((pluginFolder, Loc.T("插件目录包含符号链接或联接点", "plugin folder contains a symbolic link or junction"))); continue; }
                    var manifestPath = Path.Combine(pluginFolder, "plugin.json");
                    if (!File.Exists(manifestPath)) { Skipped.Add((pluginFolder, Loc.T("缺少 plugin.json", "plugin.json is missing"))); continue; }
                    try
                    {
                        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), JsonOptions);
                        if (manifest is null || !ValidId(manifest.Id) || manifest.ApiVersion < 1 || manifest.ApiVersion > ApiVersion ||
                            string.IsNullOrWhiteSpace(manifest.Entry) || Path.IsPathRooted(manifest.Entry) || manifest.Entry.Contains("..") ||
                            !string.Equals(Path.GetExtension(manifest.Entry), ".dll", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(Loc.T("清单字段无效或插件 API 版本不兼容", "manifest fields are invalid or plugin API version is incompatible"));
                        var assemblyPath = Path.GetFullPath(Path.Combine(pluginFolder, manifest.Entry));
                        var folderPath = Path.GetFullPath(pluginFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!assemblyPath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
                            throw new InvalidDataException(Loc.T("清单指定的 DLL 不存在或路径越界", "manifest DLL is missing or outside the plugin folder"));
                        disk.Add(assemblyPath);
                        declarations[assemblyPath] = manifest;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
                    { Failures.Add((pluginFolder, ex.Message.Split('\n')[0])); }
                }
                disk = disk.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failures.Add((Path.GetFileName(Folder), ex.Message.Split('\n')[0]));
        }

        // A file whose stamp still matches every slot it filled is kept whole: all of its extensions stay, not only the
        // first one found. Everything else — a changed file, a new file, a file whose slots did not all survive — is
        // loaded fresh.
        var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in disk)
        {
            try { stamps[file] = ExtensionStamp.Of(Path.GetDirectoryName(file)!) ?? throw new IOException("Could not fingerprint plugin files."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Failures.Add((Path.GetFileName(file), ex.Message.Split('\n')[0])); }
        }
        var kept = new bool[Slots.Length];
        for (int slot = 0; slot < Slots.Length; slot++)
        {
            if (Slots[slot] is null || _sources[slot] is not { Length: > 0 } path) continue;
            if (stamps.TryGetValue(path, out var stamp) && stamp == _stamps[slot]) kept[slot] = true;
        }
        // A file is unchanged only when it filled slots and every one of them is kept.
        bool Unchanged(string path)
        {
            bool any = false;
            for (int slot = 0; slot < Slots.Length; slot++)
            {
                if (!string.Equals(_sources[slot], path, StringComparison.OrdinalIgnoreCase)) continue;
                any = true;
                if (Slots[slot] is not null && !kept[slot]) return false;
            }
            return any;
        }

        // Load the fresh DLLs from cached copies, each into its own collectible context so a replaced one can be let go.
        var fresh = new List<(IIslandExtension Extension, string Id, AssemblyLoadContext Context, string File, string Stamp, int FromFile)>();
        var perFile = new List<(AssemblyLoadContext Context, int Placed)>();
        try { Directory.CreateDirectory(CacheFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Failures.Add(("extension-cache", ex.Message.Split('\n')[0])); }
        foreach (var file in disk)
        {
            string name = Path.GetFileName(file);
            if (!stamps.TryGetValue(file, out var stamp) || Unchanged(file)) continue;
            // Everything in here runs third-party code — the assembly's types and the extensions' own constructors and Id
            // getters included — so every exception is caught per type: one bad extension is a row on the plugins page,
            // never a failed startup.
            AssemblyLoadContext? context = null;
            bool listed = false;
            try
            {
                var shadow = Shadow(file, stamp);
                context = new PluginLoadContext(shadow);
                var assembly = context.LoadFromAssemblyPath(shadow);
                int fromFile = perFile.Count;
                perFile.Add((context, 0));
                listed = true;
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsAbstract || !typeof(IIslandExtension).IsAssignableFrom(type)) continue;
                    if (type.GetConstructor(Type.EmptyTypes) is null) continue;
                    try
                    {
                        var extension = (IIslandExtension)Activator.CreateInstance(type)!;
                        var id = extension.Id;
                        if (!ValidId(id)) throw new InvalidDataException("Plugin id must contain 1–64 letters, digits, dots or dashes.");
                        if (declarations.TryGetValue(file, out var manifest) && !string.Equals(manifest.Id, id, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Plugin id in plugin.json does not match the DLL.");
                        var pages = extension.Pages;
                        if (pages.Count == 0) throw new InvalidDataException("Plugin does not contribute any pages.");
                        var runtime = new PluginRuntime(extension);
                        foreach (var page in pages)
                        {
                            if (page is null || !ValidId(page.Id)) throw new InvalidDataException("Page ids must contain 1–64 letters, digits, dots or dashes.");
                            string pageId = string.Equals(page.Id, "main", StringComparison.OrdinalIgnoreCase) ? id.Trim() : id.Trim() + "." + page.Id;
                            if (!ValidId(pageId)) throw new InvalidDataException("Combined plugin and page id exceeds the supported length.");
                            fresh.Add((new PluginPageAdapter(runtime, page, pageId), pageId, context, file, stamp, fromFile));
                        }
                    }
                    catch (Exception ex)
                    {
                        Failures.Add((name, ex.Message.Split('\n')[0]));
                    }
                }
            }
            catch (Exception ex)
            {
                if (context is not null && !listed) context.Unload();
                Failures.Add((name, ex.Message.Split('\n')[0]));
            }
        }

        // Place: the kept slots stay where they were; the fresh extensions fill the rest in file order. A repeated id
        // is skipped, and what does not fit a slot is noted by its file.
        var slots = new IIslandExtension?[4];
        var contexts = new AssemblyLoadContext?[4];
        var sources = new string?[4];
        var ids = new string?[4];
        var newStamps = new string?[4];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int slot = 0; slot < 4; slot++)
        {
            if (!kept[slot]) continue;
            slots[slot] = Slots[slot]; contexts[slot] = _contexts[slot]; sources[slot] = _sources[slot]; ids[slot] = _ids[slot]; newStamps[slot] = _stamps[slot];
            if (_ids[slot] is { } id) seen.Add(id);
        }
        int at = 0;
        foreach (var item in fresh)
        {
            if (!seen.Add(item.Id)) continue;
            while (at < 4 && slots[at] is not null) at++;
            if (at >= 4)
            {
                Failures.Add((item.File, Loc.T("槽位已满", "All four slots are filled")));
                continue;
            }
            slots[at] = item.Extension; contexts[at] = item.Context; sources[at] = item.File; ids[at] = item.Id; newStamps[at] = item.Stamp;
            perFile[item.FromFile] = (perFile[item.FromFile].Context, perFile[item.FromFile].Placed + 1);
        }
        // A context nothing was placed from is let go at once; so is the context of every slot that did not survive.
        for (int i = 0; i < perFile.Count; i++)
            if (perFile[i].Placed == 0) perFile[i].Context.Unload();

        var changed = new List<int>();
        for (int slot = 0; slot < 4; slot++)
        {
            if (ReferenceEquals(Slots[slot], slots[slot])) continue;
            Notify(slot, false);
            changed.Add(slot);
        }
        for (int slot = 0; slot < 4; slot++)
        {
            if (!ReferenceEquals(_contexts[slot], contexts[slot]) && _contexts[slot] is { } retired)
            {
                try { Slots[slot]?.Dispose(); } catch { }
                retired.Unload();
            }
            Slots[slot] = slots[slot]; _contexts[slot] = contexts[slot]; _sources[slot] = sources[slot]; _ids[slot] = ids[slot]; _stamps[slot] = newStamps[slot];
        }

        DeckPages.SlotOccupied = slot => Slots[slot] is not null;
        DeckPages.SlotId = slot => _ids[slot];
        if (changed.Count > 0) ExtensionsChanged?.Invoke(changed);
    }

    // A dependency next to the plugin is loaded from the plugin folder; the contract always comes from the host, so both
    // sides share one IIslandExtension type.
    // The cached copy a context loads from, made once per file version.
    private static string Shadow(string file, string stamp)
    {
        string sourceFolder = Path.GetDirectoryName(file)!;
        string cacheFolder = Path.Combine(CacheFolder, Path.GetFileName(sourceFolder) + "." + stamp[..16]);
        string relative = Path.GetRelativePath(sourceFolder, file);
        string shadow = Path.Combine(cacheFolder, relative);
        if (!File.Exists(shadow))
        {
            foreach (var source in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) continue;
                string destination = Path.Combine(cacheFolder, Path.GetRelativePath(sourceFolder, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, true);
            }
        }
        return shadow;
    }

    private static bool HasReparsePoints(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return true;
            foreach (var directory in Directory.EnumerateDirectories(folder))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return true;
                pending.Push(directory);
            }
            foreach (var file in Directory.EnumerateFiles(folder))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    public static bool Occupied(int slot) => Slots[slot] is not null;

    public static bool InstallFromFolder(string source, out string error)
    {
        error = "";
        try
        {
            string manifestPath = Path.Combine(source, "plugin.json");
            if (!File.Exists(manifestPath)) { error = Loc.T("所选文件夹缺少 plugin.json。", "The selected folder has no plugin.json."); return false; }
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), JsonOptions);
            if (manifest is null || !ValidId(manifest.Id) || manifest.ApiVersion < 1 || manifest.ApiVersion > ApiVersion ||
                string.IsNullOrWhiteSpace(manifest.Entry) || Path.IsPathRooted(manifest.Entry) || manifest.Entry.Contains("..") ||
                !string.Equals(Path.GetExtension(manifest.Entry), ".dll", StringComparison.OrdinalIgnoreCase))
            { error = Loc.T("插件清单无效或 API 版本不兼容。", "The plugin manifest is invalid or its API version is incompatible."); return false; }
            if (HasReparsePoints(source)) { error = Loc.T("插件目录包含符号链接或联接点。", "The plugin folder contains a symbolic link or junction."); return false; }
            string target = Path.Combine(Folder, manifest.Id);
            if (Directory.Exists(target)) { error = Loc.T("同 ID 插件已安装，请先卸载旧版本。", "A plugin with this ID is already installed. Uninstall it before replacing it."); return false; }
            Directory.CreateDirectory(Folder);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { error = Loc.T("插件包包含目录链接，已拒绝安装。", "The plugin package contains a directory link and was rejected."); return false; }
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { error = Loc.T("插件包包含文件链接，已拒绝安装。", "The plugin package contains a file link and was rejected."); return false; }
            Directory.CreateDirectory(target);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { error = ex.Message.Split('\n')[0]; return false; }
    }

    public static bool Uninstall(int slot, bool deleteData, out string error)
    {
        error = "";
        if (slot < 0 || slot >= Slots.Length || Slots[slot] is null || _sources[slot] is not { } file) return false;
        try
        {
            string? folder = Path.GetDirectoryName(file);
            if (folder is not null && string.Equals(Path.GetDirectoryName(folder), Folder, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(folder, recursive: true);
            else if (File.Exists(file)) File.Delete(file);
            if (deleteData && _ids[slot] is { } id)
            {
                string data = Path.Combine(AppPaths.Data, "extension-data", id);
                if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
            }
            Scan();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { error = ex.Message.Split('\n')[0]; return false; }
    }

    public static int OccupiedCount => Slots.Count(slot => slot is not null);

    public static Action<string, string, ExtensionSettingValue>? WriteValue;
    public static Action? WriteSlots;
    public static bool Alive(int slot, System.Windows.FrameworkElement view) => true;
    public static void Enter(int slot, bool forward) { }
    public static void PanelInUse(bool inUse) { }
    public static void SetPanelScale(double scale) { }
    public static IReadOnlyList<ExtensionSetting> Declared(int slot) => Array.Empty<ExtensionSetting>();
    public static IReadOnlyList<string> Notes(int slot) => Array.Empty<string>();
    public static IReadOnlyDictionary<string, ExtensionSettingValue> ValuesFor(string id) => Settings?.ExtensionValues(id) ?? new Dictionary<string, ExtensionSettingValue>();
    public static DeckSettings? Settings { get; private set; }
    public static void Write(string id, string key, ExtensionSettingValue value) => WriteValue?.Invoke(id, key, value);
    public static IslandPluginContext Context(int slot)
    {
        string id = _ids[slot] ?? "unknown";
        string folder = Path.Combine(AppPaths.Data, "extension-data", id);
        Directory.CreateDirectory(folder);
        return new IslandPluginContext(id, folder, Colors,
            (plugin, key) => ReadPluginValue(plugin, key),
            (plugin, key, value) => WritePluginValue(plugin, key, value),
            action => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (action == "expand") foreach (var deck in ((App)System.Windows.Application.Current).Decks) deck.SetExpanded(true);
            }),
            kind => Subscribe(id, kind));
    }
    private static void Subscribe(string id, string kind)
    {
        if (kind.Length is < 1 or > 64 || kind.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')) return;
        if (!_subscriptions.TryGetValue(id, out var kinds)) _subscriptions[id] = kinds = new(StringComparer.OrdinalIgnoreCase);
        kinds.Add(kind);
    }
    public static System.Windows.FrameworkElement? SettingsView(int slot) => Slots[slot]?.CreateSettingsView(Context(slot));
    public static System.Windows.FrameworkElement? CompactView(int slot) => Slots[slot]?.CreateCompactView(Context(slot));
    public static void CompactDemand(int slot, bool shown)
    {
        if (slot >= 0 && slot < Slots.Length && Slots[slot] is PluginPageAdapter page) page.SetCompactDemand(shown);
    }
    public static IReadOnlyList<IslandCommand> Commands(int slot) => Slots[slot]?.Commands ?? Array.Empty<IslandCommand>();
    public static IReadOnlyList<(string Title, IslandCommand Command)> AllCommands()
        => Slots.Where(extension => extension is not null).SelectMany(extension => extension!.Commands
            .Select(command => (Loc.IsEnglish ? command.TitleEn : command.TitleZh, command))).ToList();
    public static void RunCommand(IslandCommand command)
    {
        try { command.Execute(); }
        catch (Exception ex) { ExtensionLog.Write("plugin command failed: " + ex.Message.Split('\n')[0]); }
    }
    private static string DataPath(string id, string key)
    {
        if (key.Length is < 1 or > 100 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')) throw new ArgumentException("Invalid plugin data key", nameof(key));
        var folder = Path.Combine(AppPaths.Data, "extension-data", id);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, key + ".json");
    }
    private static string? ReadPluginValue(string id, string key)
    {
        try { var path = DataPath(id, key); return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    private static void WritePluginValue(string id, string key, string value)
    {
        try { File.WriteAllText(DataPath(id, key), value.Length > 64 * 1024 ? value[..(64 * 1024)] : value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
    public static System.Windows.Media.Geometry SafeGlyph(string glyph)
    {
        try { return System.Windows.Media.Geometry.Parse(glyph); }
        catch { return System.Windows.Media.Geometry.Parse("M 4,4 H 20 V 20 H 4 Z"); }
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        public PluginLoadContext(string pluginPath) : base("BrimDeck.Island:" + Path.GetFileName(pluginPath), isCollectible: true)
            => _resolver = new AssemblyDependencyResolver(pluginPath);
        protected override Assembly? Load(AssemblyName name)
        {
            if (string.Equals(name.Name, typeof(IIslandExtension).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)) return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }

    private sealed class PluginRuntime(IIslandExtension plugin)
    {
        private readonly HashSet<string> _demandedPages = new(StringComparer.OrdinalIgnoreCase);
        private bool _demand;
        private bool _disposed;
        public IIslandExtension Plugin { get; } = plugin;
        public void SetDemand(string pageId, bool shown)
        {
            if (shown) _demandedPages.Add(pageId); else _demandedPages.Remove(pageId);
            bool next = _demandedPages.Count > 0;
            if (_demand == next) return;
            _demand = next;
            try { Plugin.SetDemand(next); } catch { }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Plugin.SetDemand(false); } catch { }
            try { Plugin.Dispose(); } catch { }
        }
    }

    private sealed class PluginPageAdapter(PluginRuntime runtime, IslandPage page, string id) : IIslandExtension
    {
        public string Id => id;
        public string TitleZh => page.TitleZh;
        public string TitleEn => page.TitleEn;
        public string Glyph => page.Glyph;
        public string Version => runtime.Plugin.Version;
        public string Author => runtime.Plugin.Author;
        public string Description => runtime.Plugin.Description;
        public IReadOnlyList<string> RequestedPermissions => runtime.Plugin.RequestedPermissions;
        public IReadOnlyList<IslandCommand> Commands => runtime.Plugin.Commands;
        public System.Windows.FrameworkElement CreateView(IslandColors colors) => page.CreateView(new IslandPluginContext(id, Path.Combine(AppPaths.Data, "extension-data", id), Colors,
            ReadPluginValue, WritePluginValue, _ => { }, _ => { }), colors);
        public System.Windows.FrameworkElement CreateView(IslandPluginContext context, IslandColors colors) => page.CreateView(context, colors);
        public System.Windows.FrameworkElement? CreateSettingsView(IslandPluginContext context) => runtime.Plugin.CreateSettingsView(context);
        public System.Windows.FrameworkElement? CreateCompactView(IslandPluginContext context) => runtime.Plugin.CreateCompactView(context);
        public void SetDemand(bool shown) => runtime.SetDemand(id, shown);
        public void SetCompactDemand(bool shown) => runtime.SetDemand("compact:" + id, shown);
        public void OnThemeChanged(IslandColors colors) { try { runtime.Plugin.OnThemeChanged(colors); } catch { } }
        public void OnHostEvent(IslandHostEvent hostEvent) { try { runtime.Plugin.OnHostEvent(hostEvent); } catch { } }
        public void OnDataChanged(IslandDataSnapshot snapshot) { try { runtime.Plugin.OnDataChanged(snapshot); } catch { } }
        public void Dispose() => runtime.Dispose();
    }

    // The demand a page hands to its extension, never letting a plugin's exception escape into the host.
    public static void Notify(int slot, bool shown)
    {
        if (slot < 0 || slot >= Slots.Length) return;
        try { Slots[slot]?.SetDemand(shown); }
        catch (Exception) { }
    }

    public static string Title(int slot)
    {
        try
        {
            return Slots[slot] is { } extension ? (Loc.IsEnglish ? extension.TitleEn : extension.TitleZh) : "";
        }
        catch (Exception) { return _ids[slot] ?? ""; }
    }

    // The colors an extension draws with: its monitor palette page's scheme on the theme's text and surface colors.
    // The app sets the palette whenever the theme or the choice changes, before any view is created.
    public static IslandColors Colors { get; private set; } = new("#4FD17F", "#F0B54A", "#FF6B5E", "#F1F1F4", "#C5C5CB", "#8E8E93", "#2A2A30", "#1D1D21");
    public static void SetPalette(DeckSettings settings)
    {
        Settings = settings;
        var monitor = MonitorThemeColors.For(settings);
        Colors = new(PanelTheme.Current.IsDark ? "#4FD17F" : "#1C9A50", monitor.Warning, monitor.Critical,
            UI.Primary, UI.Secondary, UI.Tertiary, UI.Track, UI.Fill);
        foreach (var extension in Slots) if (extension is not null) try { extension.OnThemeChanged(Colors); } catch { }
    }

    public static void PublishEvent(string name, IReadOnlyDictionary<string, string>? values = null)
    {
        var item = new IslandHostEvent(name, values ?? new Dictionary<string, string>());
        foreach (var extension in Slots) if (extension is not null) try { extension.OnHostEvent(item); } catch { }
    }
    public static void PublishData(IslandDataSnapshot snapshot)
    {
        for (int slot = 0; slot < Slots.Length; slot++)
            if (Slots[slot] is { } extension && _ids[slot] is { } id && _subscriptions.ContainsKey(id))
                try { extension.OnDataChanged(snapshot); } catch { }
    }
    public static void PublishMonitor(MonitorSnapshot snapshot)
    {
        var numbers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (snapshot.Cpu.TryGet(out var cpu)) numbers["cpu.usage"] = cpu.Usage;
        if (snapshot.Memory.TryGet(out var memory)) { numbers["memory.usedBytes"] = memory.Used; numbers["memory.totalBytes"] = memory.Total; }
        if (snapshot.Network.TryGet(out var network)) { numbers["network.downloadBytesPerSecond"] = network.DownBytesPerSecond; numbers["network.uploadBytesPerSecond"] = network.UpBytesPerSecond; }
        if (snapshot.Gpu.TryGet(out var gpu)) numbers["gpu.usage"] = gpu.Usage;
        PublishData(new IslandDataSnapshot(snapshot.Time, numbers, new Dictionary<string, string>()));
    }
}
