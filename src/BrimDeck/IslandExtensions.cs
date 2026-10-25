using System.IO;
using System.Reflection;
using System.Runtime.Loader;
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
    public static readonly IIslandExtension?[] Slots = new IIslandExtension?[4];
    private static readonly AssemblyLoadContext?[] _contexts = new AssemblyLoadContext?[4];
    private static readonly string?[] _sources = new string?[4];
    // The id each extension reported when it was created; the host never asks a plugin for it a second time.
    private static readonly string?[] _ids = new string?[4];
    private static readonly (DateTime Written, long Size)[] _stamps = new (DateTime, long)[4];

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "extensions");
    // The copies the contexts are loaded from: a loaded assembly locks its file on Windows, and the plugin folder must
    // stay writable so a new DLL can be dropped over the old one.
    private static string CacheFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "extension-cache");
    public static string Source(int slot) => _sources[slot] ?? "";

    // The DLLs that could not be loaded, by file name with the error's first line. Cleared on each scan.
    public static readonly List<(string File, string Error)> Failures = new();

    // Raised with the slots whose extension was replaced or retired; the app drops their tabs and views and re-renders.
    public static event Action<IReadOnlyList<int>>? ExtensionsChanged;

    public static void Load() => Scan();

    public static void Scan()
    {
        Failures.Clear();
        var disk = new List<string>();
        try
        {
            if (Directory.Exists(Folder))
                disk.AddRange(Directory.EnumerateFiles(Folder, "*.dll").OrderBy(file => file, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failures.Add((Path.GetFileName(Folder), ex.Message.Split('\n')[0]));
        }

        // A file whose stamp still matches every slot it filled is kept whole: all of its extensions stay, not only the
        // first one found. Everything else — a changed file, a new file, a file whose slots did not all survive — is
        // loaded fresh.
        var stamps = new Dictionary<string, (DateTime Written, long Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in disk)
        {
            try { var info = new FileInfo(file); stamps[Path.GetFileName(file)] = (info.LastWriteTimeUtc, info.Length); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Failures.Add((Path.GetFileName(file), ex.Message.Split('\n')[0])); }
        }
        var kept = new bool[Slots.Length];
        for (int slot = 0; slot < Slots.Length; slot++)
        {
            if (Slots[slot] is null || _sources[slot] is not { Length: > 0 } name) continue;
            if (stamps.TryGetValue(name, out var stamp) && stamp == _stamps[slot]) kept[slot] = true;
        }
        // A file is unchanged only when it filled slots and every one of them is kept.
        bool Unchanged(string name)
        {
            bool any = false;
            for (int slot = 0; slot < Slots.Length; slot++)
            {
                if (!string.Equals(_sources[slot], name, StringComparison.OrdinalIgnoreCase)) continue;
                any = true;
                if (Slots[slot] is not null && !kept[slot]) return false;
            }
            return any;
        }

        // Load the fresh DLLs from cached copies, each into its own collectible context so a replaced one can be let go.
        var fresh = new List<(IIslandExtension Extension, string Id, AssemblyLoadContext Context, string File, (DateTime, long) Stamp, int FromFile)>();
        var perFile = new List<(AssemblyLoadContext Context, int Placed)>();
        try { Directory.CreateDirectory(CacheFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Failures.Add(("extension-cache", ex.Message.Split('\n')[0])); }
        foreach (var file in disk)
        {
            string name = Path.GetFileName(file);
            if (!stamps.TryGetValue(name, out var stamp) || Unchanged(name)) continue;
            // Everything in here runs third-party code — the assembly's types and the extensions' own constructors and Id
            // getters included — so every exception is caught per type: one bad extension is a row on the plugins page,
            // never a failed startup.
            AssemblyLoadContext? context = null;
            bool listed = false;
            try
            {
                context = new AssemblyLoadContext("BrimDeck.Island:" + name, isCollectible: true);
                context.Resolving += Resolve;
                var assembly = context.LoadFromAssemblyPath(Shadow(file, name, stamp));
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
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        fresh.Add((extension, id.Trim(), context, name, stamp, fromFile));
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
        var newStamps = new (DateTime, long)[4];
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
            if (!ReferenceEquals(_contexts[slot], contexts[slot]) && _contexts[slot] is { } retired) retired.Unload();
            Slots[slot] = slots[slot]; _contexts[slot] = contexts[slot]; _sources[slot] = sources[slot]; _ids[slot] = ids[slot]; _stamps[slot] = newStamps[slot];
        }

        DeckPages.SlotOccupied = slot => Slots[slot] is not null;
        DeckPages.SlotId = slot => _ids[slot];
        if (changed.Count > 0) ExtensionsChanged?.Invoke(changed);
    }

    // A dependency next to the plugin is loaded from the plugin folder; the contract always comes from the host, so both
    // sides share one IIslandExtension type.
    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (string.Equals(name.Name, typeof(IIslandExtension).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Name is not { Length: > 0 } simple) return null;
        var candidate = Path.Combine(Folder, simple + ".dll");
        return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
    }

    // The cached copy a context loads from, made once per file version.
    private static string Shadow(string file, string name, (DateTime Written, long Size) stamp)
    {
        var shadow = Path.Combine(CacheFolder, $"{name}.{stamp.Written.Ticks}.{stamp.Size}.dll");
        if (!File.Exists(shadow))
        {
            File.Copy(file, shadow, true);
            // Older copies of the same DLL are no longer needed; a loaded one stays locked and is skipped.
            foreach (var old in Directory.EnumerateFiles(CacheFolder, name + ".*.dll"))
                if (!string.Equals(old, shadow, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return shadow;
    }

    public static bool Occupied(int slot) => Slots[slot] is not null;

    public static int OccupiedCount => Slots.Count(slot => slot is not null);

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
    public static void SetPalette(MonitorPalette palette) => Colors = With(MonitorThemeColors.For(palette));
    private static IslandColors With(MonitorColors colors) => new(colors.Normal, colors.Warning, colors.Critical, UI.Primary, UI.Secondary, UI.Tertiary, UI.Track, UI.Fill);
}
