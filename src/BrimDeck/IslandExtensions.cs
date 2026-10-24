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
// A scan is a full hot reload: a DLL that changed on disk is loaded fresh and its extension replaced in place, a DLL
// taken away has its slot retired, and what did not change is kept as it is. The slots are wired into DeckPages before
// the settings are read, so a page's own switch and size apply from the first open.
internal static class IslandExtensions
{
    public static readonly IIslandExtension?[] Slots = new IIslandExtension?[4];
    private static readonly AssemblyLoadContext?[] _contexts = new AssemblyLoadContext?[4];
    private static readonly string?[] _sources = new string?[4];
    private static readonly (DateTime Written, long Size)[] _stamps = new (DateTime, long)[4];

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "extensions");
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

        // The slots whose DLL is on disk unchanged are kept; everything else is loaded fresh.
        var kept = new bool[Slots.Length];
        foreach (var file in disk)
        {
            (DateTime Written, long Size) stamp;
            try { var info = new FileInfo(file); stamp = (info.LastWriteTimeUtc, info.Length); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failures.Add((Path.GetFileName(file), ex.Message.Split('\n')[0]));
                continue;
            }
            for (int slot = 0; slot < Slots.Length; slot++)
            {
                if (kept[slot] || Slots[slot] is null) continue;
                if (string.Equals(_sources[slot], Path.GetFileName(file), StringComparison.OrdinalIgnoreCase) && _stamps[slot] == stamp)
                {
                    kept[slot] = true;
                    break;
                }
            }
        }

        // Load the fresh DLLs, each into its own collectible context so a replaced one can be let go.
        var fresh = new List<(IIslandExtension Extension, AssemblyLoadContext Context, string File, (DateTime, long) Stamp, int FromFile)>();
        var perFile = new List<(AssemblyLoadContext Context, int Placed)>();
        foreach (var file in disk)
        {
            (DateTime Written, long Size) stamp;
            try { var info = new FileInfo(file); stamp = (info.LastWriteTimeUtc, info.Length); }
            catch { continue; }
            bool unchanged = false;
            for (int slot = 0; slot < Slots.Length; slot++)
                if (kept[slot] && string.Equals(_sources[slot], Path.GetFileName(file), StringComparison.OrdinalIgnoreCase) && _stamps[slot] == stamp)
                { unchanged = true; break; }
            if (unchanged) continue;
            // Everything in here runs third-party code — the assembly's types and the extension's own constructor and Id
            // getter included — so every exception is caught: one bad DLL is a row on the plugins page, never a failed
            // startup.
            AssemblyLoadContext? context = null;
            bool listed = false;
            try
            {
                context = new AssemblyLoadContext("BrimDeck.Island:" + Path.GetFileName(file), isCollectible: true);
                var assembly = context.LoadFromAssemblyPath(file);
                int fromFile = perFile.Count;
                perFile.Add((context, 0));
                listed = true;
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsAbstract || !typeof(IIslandExtension).IsAssignableFrom(type)) continue;
                    if (type.GetConstructor(Type.EmptyTypes) is null) continue;
                    var extension = (IIslandExtension)Activator.CreateInstance(type)!;
                    var id = extension.Id;
                    if (id is null || id.Trim().Length == 0) continue;
                    fresh.Add((extension, context, Path.GetFileName(file), stamp, fromFile));
                }
            }
            catch (Exception ex)
            {
                if (context is not null && !listed) context.Unload();
                Failures.Add((Path.GetFileName(file), ex.Message.Split('\n')[0]));
            }
        }

        // Place: the kept slots stay where they were; the fresh extensions fill the rest in file order. A repeated id
        // is skipped, and what does not fit a slot is noted by its file.
        var slots = new IIslandExtension?[4];
        var contexts = new AssemblyLoadContext?[4];
        var sources = new string?[4];
        var stamps = new (DateTime, long)[4];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int slot = 0; slot < 4; slot++)
            if (kept[slot])
            {
                slots[slot] = Slots[slot]; contexts[slot] = _contexts[slot]; sources[slot] = _sources[slot]; stamps[slot] = _stamps[slot];
                ids.Add(Slots[slot]!.Id);
            }
        int at = 0;
        foreach (var item in fresh)
        {
            if (!ids.Add(item.Extension.Id)) continue;
            while (at < 4 && slots[at] is not null) at++;
            if (at >= 4)
            {
                Failures.Add((item.File, Loc.T("槽位已满", "All four slots are filled")));
                continue;
            }
            slots[at] = item.Extension; contexts[at] = item.Context; sources[at] = item.File; stamps[at] = item.Stamp;
            perFile[item.FromFile] = (perFile[item.FromFile].Context, perFile[item.FromFile].Placed + 1);
        }
        // A context nothing was placed from is let go at once; so is the context of every slot that did not survive.
        for (int i = 0; i < perFile.Count; i++)
            if (perFile[i].Placed == 0) perFile[i].Context.Unload();

        var changed = new List<int>();
        for (int slot = 0; slot < 4; slot++)
        {
            if (ReferenceEquals(Slots[slot], slots[slot])) continue;
            Slots[slot]?.SetDemand(false);
            changed.Add(slot);
        }
        for (int slot = 0; slot < 4; slot++)
        {
            if (!ReferenceEquals(_contexts[slot], contexts[slot]) && _contexts[slot] is { } retired) retired.Unload();
            Slots[slot] = slots[slot]; _contexts[slot] = contexts[slot]; _sources[slot] = sources[slot]; _stamps[slot] = stamps[slot];
        }

        DeckPages.SlotOccupied = slot => Slots[slot] is not null;
        DeckPages.SlotId = slot => Slots[slot]?.Id;
        if (changed.Count > 0) ExtensionsChanged?.Invoke(changed);
    }

    public static bool Occupied(int slot) => Slots[slot] is not null;

    public static int OccupiedCount => Slots.Count(slot => slot is not null);

    public static string Title(int slot) => Slots[slot] is { } extension
        ? Loc.IsEnglish ? extension.TitleEn : extension.TitleZh
        : "";

    // The colors an extension draws with: its monitor palette page's scheme on the theme's text and surface colors.
    // The app sets the palette whenever the theme or the choice changes, before any view is created.
    public static IslandColors Colors { get; private set; } = new("#4FD17F", "#F0B54A", "#FF6B5E", "#F1F1F4", "#C5C5CB", "#8E8E93", "#2A2A30", "#1D1D21");
    public static void SetPalette(MonitorPalette palette) => Colors = With(MonitorThemeColors.For(palette));
    private static IslandColors With(MonitorColors colors) => new(colors.Normal, colors.Warning, colors.Critical, UI.Primary, UI.Secondary, UI.Tertiary, UI.Track, UI.Fill);
}
