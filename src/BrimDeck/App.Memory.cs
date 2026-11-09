using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// Gives memory back while BrimDeck sits collapsed, as far as the memory mode asks (DeckSettings.MemoryMode, MemoryPlan).
// Reading the usage logs, building a page and loading covers allocate in bursts; afterwards the garbage collector keeps the room
// it grew into, and Windows keeps the pages in the working set, so an idle panel that had peaked at over 200 MB stayed there. In the
// saver and balanced modes, once every panel has been collapsed and left alone for the mode's wait, the heap is compacted and the room
// it no longer needs handed back to Windows; the saver mode trims the working set as well. What is trimmed comes back page by page on
// the next use, which can make an opening stutter, so only the saver mode trims it. Never while a panel is open or a surface is being
// resized, and at most once per the mode's period. The smooth mode gives nothing back: compacting alone left the figure as it was.
// The smooth mode also keeps full collections from running while a panel is open (GCLatencyMode.LowLatency), unless memory runs low.
public partial class App
{
    private DispatcherTimer? _trim;
    private DateTime _trimmedAt = DateTime.MinValue;
    internal MemoryPlan MemoryPlan => MemoryPlan.For(Settings.MemoryMode);

    // Called after a burst of work (a usage refresh), whenever a panel collapses, when the settings window closes and when the mode
    // changes: the clean-up waits for a quiet stretch after it.
    internal void RequestTrim()
    {
        var plan = MemoryPlan;
        if (Exiting || !plan.TrimWhenIdle) { _trim?.Stop(); return; }
        _trim ??= CreateTrimTimer();
        _trim.Stop();
        var wait = _trimmedAt + plan.TrimEvery - DateTime.UtcNow;
        _trim.Interval = wait > plan.TrimAfter ? wait : plan.TrimAfter;
        _trim.Start();
    }

    private DispatcherTimer CreateTrimTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (Exiting || !MemoryPlan.TrimWhenIdle) return;
            // Someone is using a panel: try again after the next quiet stretch.
            if (Decks.Any(deck => deck.IsExpanded || deck.IsResizing)) { RequestTrim(); return; }
            _trimmedAt = DateTime.UtcNow;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            // Aggressive also returns the memory the heap no longer uses to the system.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            if (MemoryPlan.TrimWorkingSet) SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        };
        return timer;
    }

    // Called whenever a panel opens or closes and when the mode changes.
    internal void UpdateCollectionLatency()
    {
        bool quiet = !Exiting && MemoryPlan.QuietWhileOpen && Decks.Any(deck => deck.IsExpanded);
        var mode = quiet ? GCLatencyMode.LowLatency : GCLatencyMode.Interactive;
        if (GCSettings.LatencyMode != mode) GCSettings.LatencyMode = mode;
    }

    // The mode changed in the settings: the samplers take its linger, and the clean-up and the collector its rules.
    internal void ApplyMemoryMode()
    {
        var plan = MemoryPlan;
        Monitor?.UsePlan(plan);
        Devices?.UsePlan(plan);
        if (Media is not null) UseMediaPlan(Media, plan);
        if (plan.WarmAtStart) { WarmMonitor(); PageWarm.Start(Dispatcher); }
        UpdateCollectionLatency();
        RequestTrim();
    }

    private static void UseMediaPlan(Native.MediaSessions media, MemoryPlan plan) { media.MeterLinger = plan.MediaLinger; media.RecentCoverCount = plan.RecentCovers; }

    // The smooth mode sets up the system readings' counters at start (SystemSampler.Warm), so the first opening of the monitor page
    // does not wait for them.
    private void WarmMonitor()
    {
        if (Exiting || !DeckPages.Shown(Settings, DeckPage.Monitor)) return;
        Monitor ??= CreateMonitor();
        Monitor.Warm();
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
