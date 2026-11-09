namespace BrimDeck.Core;

// How much BrimDeck keeps in memory between uses (DeckSettings.MemoryMode). Saver gives back as much as it can soon after use and
// does less work while the panel is collapsed, saving processor time as well, without slowing what is on screen while it is used;
// Balanced gives back what it no longer uses without trimming what it still needs; Smooth keeps readings, captures and caches longer
// and prepares the pages' code at start, so the panel and its pages open without waiting, and still lets go of what has not been used
// for a long while.
public enum MemoryMode { Saver, Balanced, Smooth }

// What a mode does. App.Memory.cs, the samplers, the media sessions and the panels read it.
public sealed record MemoryPlan
{
    // The clean-up runs once every panel has been collapsed this long, and at most this often.
    public TimeSpan TrimAfter { get; init; }
    public TimeSpan TrimEvery { get; init; }
    // The clean-up hands the room the heap no longer uses back to Windows (an aggressive collection) rather than only compacting it.
    public bool ReturnMemory { get; init; }
    // The clean-up also takes the process's pages out of its working set. What is taken out comes back on the next use, page by
    // page, which can make the next opening stutter.
    public bool TrimWorkingSet { get; init; }
    // How long the system readings and the devices' batteries keep being read after their page closes, so a page opened again within
    // it shows current figures at once.
    public TimeSpan ReadingsLinger { get; init; }
    // While the readings linger, what the monitor page asked for besides the usage (temperatures, drives, power, frame rate, the
    // graphics adapters' counters) stays asked for too, so it is not primed again when the page opens. The adapters' counters are
    // the costliest part of a reading.
    public bool LingerExtras { get; init; }
    // The process list's buffer and the programs' icons are kept while the readings pause instead of being read again.
    public bool KeepCaches { get; init; }
    // How long the equalizer's audio capture keeps running after nothing shows the bars (a song paused, the panel opened on another
    // page). Starting it again waits up to two seconds for the system, with flat bars meanwhile.
    public TimeSpan MediaLinger { get; init; }
    // How many recent songs' downloaded covers are kept, so switching back to one shows its cover without downloading it again.
    public int RecentCovers { get; init; }
    // The longest pause between checks of the pointer while it is far from the collapsed surface. The pause shrinks as the pointer
    // nears, so the panel opens as promptly either way; a longer one checks less often while the pointer is far away.
    public int PointerFarMilliseconds { get; init; }
    // No full collection runs while a panel is open, unless memory runs low (GCLatencyMode.LowLatency).
    public bool QuietWhileOpen { get; init; }
    // The system readings' counters, and the code that builds the pages, are prepared at start, so the first opening does not wait.
    public bool WarmAtStart { get; init; }
    // How often the collapsed row's system readings are taken while no monitor page is open; null keeps the interval chosen in the
    // settings (DeckSettings.MonitorInterval). An open monitor page always takes the chosen interval.
    public int? CollapsedSeconds { get; init; }

    // The interval the system readings are taken at, in seconds.
    public int ReadingSeconds(int chosen, bool pageOpen) => pageOpen || CollapsedSeconds is not { } collapsed ? chosen : Math.Max(chosen, collapsed);

    public static readonly MemoryPlan Saver = new()
    {
        TrimAfter = TimeSpan.FromSeconds(45), TrimEvery = TimeSpan.FromMinutes(5), ReturnMemory = true, TrimWorkingSet = true,
        ReadingsLinger = TimeSpan.FromSeconds(3), LingerExtras = false, KeepCaches = false, MediaLinger = TimeSpan.Zero, RecentCovers = 4,
        PointerFarMilliseconds = 320, QuietWhileOpen = false, WarmAtStart = false, CollapsedSeconds = 2
    };
    public static readonly MemoryPlan Balanced = new()
    {
        TrimAfter = TimeSpan.FromMinutes(3), TrimEvery = TimeSpan.FromMinutes(15), ReturnMemory = true, TrimWorkingSet = false,
        ReadingsLinger = TimeSpan.FromSeconds(10), LingerExtras = true, KeepCaches = false, MediaLinger = TimeSpan.FromSeconds(5), RecentCovers = 16,
        PointerFarMilliseconds = 160, QuietWhileOpen = false, WarmAtStart = false
    };
    public static readonly MemoryPlan Smooth = new()
    {
        TrimAfter = TimeSpan.FromMinutes(10), TrimEvery = TimeSpan.FromMinutes(30), ReturnMemory = false, TrimWorkingSet = false,
        ReadingsLinger = TimeSpan.FromMinutes(1), LingerExtras = true, KeepCaches = true, MediaLinger = TimeSpan.FromSeconds(30), RecentCovers = 32,
        PointerFarMilliseconds = 160, QuietWhileOpen = true, WarmAtStart = true
    };

    public static MemoryPlan For(MemoryMode mode) => mode switch { MemoryMode.Saver => Saver, MemoryMode.Smooth => Smooth, _ => Balanced };
}
