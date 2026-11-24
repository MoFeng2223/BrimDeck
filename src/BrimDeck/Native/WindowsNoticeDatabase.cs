using System.IO;

namespace BrimDeck.Native;

// Watches the files of Windows' notification database for the current user (%LOCALAPPDATA%\Microsoft\Windows\Notifications,
// wpndatabase.db and its write-ahead log wpndatabase.db-wal) and, from each write that starts a burst, asks for a probe every
// ProbeEvery for ProbeFor (WindowsNoticeLoop.DatabaseChanged says why). Windows writes the files within 50 ms of a toast and every
// 50 ms or so while it shows it; the listener returned the toast 190 to 460 ms after it was shown (measured 2026-11-25), so the
// window covers that from the first write, and a later write starts another. The shared-memory file (wpndatabase.db-shm) is left
// out: reading the database touches it, and BrimDeck reads it for pictures (WindowsToastPicture). Nothing is read here; the files
// only say that something changed. Probe is called on a worker thread, never after Dispose.
internal sealed class WindowsNoticeDatabase : IDisposable
{
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromMilliseconds(40), ProbeFor = TimeSpan.FromMilliseconds(520);
    private readonly FileSystemWatcher _watcher;
    private readonly Action _probe;
    private int _burst;
    private volatile bool _closed;

    private WindowsNoticeDatabase(string folder, Action probe)
    {
        _probe = probe;
        _watcher = new FileSystemWatcher(folder, "wpndatabase.db*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size, IncludeSubdirectories = false };
        _watcher.Changed += Changed;
    }

    // The watch, or null when the folder is missing or cannot be watched; the push notification log and the readings still work.
    public static WindowsNoticeDatabase? Start(Action probe)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");
            if (!Directory.Exists(folder)) return null;
            var watch = new WindowsNoticeDatabase(folder, probe);
            watch._watcher.EnableRaisingEvents = true;
            return watch;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException) { return null; }
    }

    private void Changed(object sender, FileSystemEventArgs e)
    {
        if (_closed || !(e.Name is { } name && (name.Equals("wpndatabase.db", StringComparison.OrdinalIgnoreCase) || name.Equals("wpndatabase.db-wal", StringComparison.OrdinalIgnoreCase)))) return;
        if (Interlocked.Exchange(ref _burst, 1) == 0) _ = Burst();
    }

    private async Task Burst()
    {
        try
        {
            var end = DateTime.UtcNow + ProbeFor;
            while (!_closed && DateTime.UtcNow < end)
            {
                _probe();
                await Task.Delay(ProbeEvery).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _burst, 0); }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _watcher.Changed -= Changed;
        try { _watcher.EnableRaisingEvents = false; } catch (ObjectDisposedException) { }
        _watcher.Dispose();
    }
}
