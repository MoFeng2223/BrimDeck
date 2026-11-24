namespace BrimDeck.Core.Notices;

// A notice's sound (NoticeStyle.Sound). Level 1 has none. Level 2 plays a short one once as its notice shows. Level 3 plays a
// loop for as long as its notice stays, the end joining the start without a seam. Either is a built-in sound (NoticeSoundSynth
// makes it as it is needed: no audio file ships with BrimDeck) or a file the user imported into the data folder's "sounds".
public enum NoticeSoundKind { Short, Loop }

public sealed record NoticeSoundInfo(string Id, NoticeSoundKind Kind, string Chinese, string English)
{
    public string Name => Loc.T(Chinese, English);
}

public static class NoticeSounds
{
    // NoticeStyle.Sound: null is not chosen (the level's default), None plays nothing, a built-in sound's id, or FilePrefix and
    // the name of a file in the sounds folder.
    public const string None = "", FilePrefix = "file:";
    public const string DefaultShort = "drop", DefaultLoop = "marimba";
    // The folder of imported files, inside the data folder.
    public const string Folder = "sounds";
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public static readonly IReadOnlyList<string> FileExtensions = [".wav", ".mp3", ".m4a", ".aac", ".flac", ".wma"];
    // What plays once ends after this long at the latest: an imported song chosen for level 2, and any sound tried in the settings.
    public static readonly TimeSpan OnceLimit = TimeSpan.FromSeconds(10);
    public const int DefaultVolume = 70;

    public static readonly IReadOnlyList<NoticeSoundInfo> BuiltIn =
    [
        new("drop", NoticeSoundKind.Short, "水滴", "Drop"),
        new("ding", NoticeSoundKind.Short, "叮", "Ding"),
        new("duo", NoticeSoundKind.Short, "双音", "Two notes"),
        new("arp", NoticeSoundKind.Short, "琶音", "Arpeggio"),
        new("knock", NoticeSoundKind.Short, "轻敲", "Knock"),
        new("chord", NoticeSoundKind.Short, "和弦", "Chord"),
        new("marimba", NoticeSoundKind.Loop, "木琴", "Marimba"),
        new("chimes", NoticeSoundKind.Loop, "钟琴", "Chimes"),
        new("morning", NoticeSoundKind.Loop, "晨曲", "Morning"),
        new("phone", NoticeSoundKind.Loop, "电话铃", "Phone"),
        new("beeps", NoticeSoundKind.Loop, "嘀嘀", "Beeps")
    ];

    // The kind of sound a level plays; null for a level that plays none.
    public static NoticeSoundKind? KindOf(NoticeLevel level) => level switch
    {
        NoticeLevel.Glow or NoticeLevel.Lively => NoticeSoundKind.Short, NoticeLevel.Persistent => NoticeSoundKind.Loop, _ => null
    };

    public static string Default(NoticeSoundKind kind) => kind == NoticeSoundKind.Loop ? DefaultLoop : DefaultShort;

    // The sound a style names, as a level plays it: None, a built-in sound of the level's kind, or an imported file. A name
    // that is neither (a sound of an older or a newer version, a file name that is not one) is the level's default.
    public static string Of(string? sound, NoticeLevel level)
    {
        if (KindOf(level) is not { } kind) return None;
        if (sound is null) return Default(kind);
        sound = sound.Trim();
        if (sound.Length == 0) return None;
        if (FileName(sound) is not null) return sound;
        return BuiltIn.Any(known => known.Id == sound && known.Kind == kind) ? sound : Default(kind);
    }

    // The sound of what an island starts to show. A notice shown beyond the rate limit plays as a plain one, so it has none; a
    // quota alert and a track change have none. windows: Windows applications' notices play it too (they mostly bring their own
    // sound, so this is off unless the user asks).
    public static string For(AttentionItem item, bool windows)
    {
        if (!item.IsNotice || item.Subdued) return None;
        if (!windows && item.Payload is Notice { Origin: NoticeOrigin.Windows }) return None;
        return Of(item.Style?.Sound, item.Level);
    }

    // The imported file a sound names; null for any other sound.
    public static string? FileName(string sound)
        => sound.StartsWith(FilePrefix, StringComparison.Ordinal) && SafeFileName(sound[FilePrefix.Length..]) ? sound[FilePrefix.Length..] : null;

    public static string ForFile(string name) => FilePrefix + name;

    // A plain file name with one of the extensions: never a path, so a sound can only name a file inside the sounds folder.
    public static bool SafeFileName(string name)
        => name.Length is > 0 and <= 120 && name.IndexOfAny(['/', '\\', ':']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && name[0] != '.' && name.Trim() == name && FileExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    // The name an imported file gets in the sounds folder: its own, numbered when the folder has one of that name already.
    public static string ImportName(string source, IEnumerable<string> existing)
    {
        string extension = Path.GetExtension(source).ToLowerInvariant(), stem = Path.GetFileNameWithoutExtension(source).Trim().TrimStart('.');
        foreach (char bad in Path.GetInvalidFileNameChars().Concat(['/', '\\', ':'])) stem = stem.Replace(bad, '_');
        if (stem.Length == 0) stem = "sound";
        if (stem.Length > 80) stem = stem[..80].Trim();
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string name = stem + extension;
        for (int number = 2; taken.Contains(name); number++) name = $"{stem} ({number}){extension}";
        return name;
    }

    // The files of the sounds folder that can be chosen, by name.
    public static IReadOnlyList<string> Files(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return [];
            return Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().Where(SafeFileName).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // The slider's 0 to 100 as the factor the samples are multiplied with: half the slider is a quarter of the amplitude.
    public static double Gain(int volume) { double part = Math.Clamp(volume, 0, 100) / 100.0; return part * part; }
}

// Keeps short sounds apart: a notice plays its sound once however many islands show it, sounds follow each other no closer
// than Spacing, and none plays over a loop.
public sealed class NoticeSoundPace
{
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Remembered = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, DateTimeOffset> _played = new(StringComparer.Ordinal);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    // Whether the sound of the notice named by key may play now; counted as played when it may.
    public bool Admit(string key, bool looping, DateTimeOffset now)
    {
        foreach (var old in _played.Where(played => now - played.Value >= Remembered).Select(played => played.Key).ToList()) _played.Remove(old);
        if (looping || _played.ContainsKey(key) || now - _last < Spacing) return false;
        _played[key] = now; _last = now;
        return true;
    }
}
