using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// Off: not switched on. NoDingTalk: DingTalk is not installed or has never been signed in to on this PC. NoKey: the account's
// own id or salt could not be found, so its store cannot be read. Unreadable: the store is there but did not open (DingTalk
// changed how it keeps it, or a newer Windows has no SQLite). Reading: new messages are being read.
internal enum DingTalkState { Off, NoDingTalk, NoKey, Unreadable, Reading }

// DingTalk's messages, read from its own store (DingTalkStore) rather than from the popup it draws, so that a group's
// messages are told with their words even though DingTalk draws no popup for them. Windows says when the store's
// write-ahead log changes; each change is read once, a moment later, on a worker thread. Nothing is polled while DingTalk is
// quiet, nothing of DingTalk's is written, and only the newest message of each conversation is read, never the chat history.
internal sealed class DingTalkMessages : IDisposable
{
    // The log is written a moment before the message is there to read; the read is held this long so one read covers it.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(120);
    // A safety net in case Windows misses a change while DingTalk is running; nothing is read when nothing changed.
    private static readonly TimeSpan Sweep = TimeSpan.FromSeconds(30);
    private readonly Dispatcher _dispatcher;
    private readonly Action<NoticeInput> _received;
    private readonly Action _changed;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _reading = new(1, 1);
    private FileSystemWatcher? _watch;
    private string _folder = "", _database = "", _accountId = "";
    private byte[] _key = [];
    // What has been told, per conversation: one mark for all of them would drop a message in one chat whose time is behind
    // the newest in another.
    private readonly Dictionary<string, long> _told = new(StringComparer.Ordinal);
    private bool _started, _disposed;

    public DingTalkState State { get; private set; } = DingTalkState.NoDingTalk;

    public DingTalkMessages(Dispatcher dispatcher, Action<NoticeInput> received, Action changed)
    {
        _dispatcher = dispatcher; _received = received; _changed = changed;
        _ = LoopAsync();
    }

    // Whether the words of DingTalk's messages are being read now, so that the flash of its taskbar button says nothing.
    public bool Covering => State == DingTalkState.Reading;

    private async Task LoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await ReadAsync();
                await Task.Delay(Sweep, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    // The account's folder, its key and its store; found again whenever DingTalk is signed in to or switched.
    private bool Find()
    {
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DingTalk");
            if (!Directory.Exists(root)) { State = DingTalkState.NoDingTalk; return false; }
            var folder = new DirectoryInfo(root).GetDirectories("*_v3").OrderByDescending(entry => entry.LastWriteTimeUtc).FirstOrDefault();
            string? database = folder is null ? null : Path.Combine(folder.FullName, "DBFiles", "dingtalk.db");
            if (folder is null || database is null || !File.Exists(database)) { State = DingTalkState.NoDingTalk; return false; }
            if (folder.FullName == _folder && _key.Length > 0) return true;
            string config = Path.Combine(folder.FullName, "user_config");
            string? salt = File.Exists(config) ? DingTalkStore.Salt(File.ReadAllText(config)) : null;
            string? accountId = AccountId(root);
            if (salt is null || accountId is null) { State = DingTalkState.NoKey; return false; }
            _folder = folder.FullName; _database = database; _accountId = accountId;
            CryptographicOperations.ZeroMemory(_key);
            _key = DingTalkStore.Key(accountId, salt);
            _told.Clear(); _started = false;
            Watch();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { State = DingTalkState.NoDingTalk; return false; }
    }

    // The account's own id, from the newest of DingTalk's own logs that names it (the folder is named after a hash instead).
    private static string? AccountId(string root)
    {
        var logs = new DirectoryInfo(Path.Combine(root, "log"));
        if (!logs.Exists) return null;
        foreach (var log in logs.GetFiles().Where(file => file.Name.StartsWith("cef_debug.log", StringComparison.OrdinalIgnoreCase) || file.Name.StartsWith("gaea.log", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(file => file.LastWriteTimeUtc).Take(12))
        {
            try
            {
                using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (DingTalkStore.AccountId(new StreamReader(stream).ReadToEnd()) is { } found) return found;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private void Watch()
    {
        _watch?.Dispose();
        try
        {
            _watch = new FileSystemWatcher(Path.GetDirectoryName(_database)!, "dingtalk.db*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
            _watch.Changed += (_, _) => Wake();
            _watch.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { _watch = null; }
    }

    private void Wake()
    {
        if (_disposed) return;
        _ = Task.Delay(Settle).ContinueWith(_ => ReadAsync(), TaskScheduler.Default);
    }

    // One reading, off the UI thread; what it found is handed over on the UI thread. The first reading takes what is there
    // now as already told, so that starting up does not bring up every conversation's last message.
    private async Task ReadAsync()
    {
        if (_disposed || !await _reading.WaitAsync(0, CancellationToken.None)) return;
        try
        {
            var before = State;
            var (state, rows) = await Task.Run(Step, _stop.Token);
            if (_disposed) return;
            State = state;
            if (rows is not null)
            {
                var told = new List<NoticeInput>();
                foreach (var row in rows)
                {
                    long mark = _told.GetValueOrDefault(row.Cid);
                    if (row.At <= mark) continue;
                    _told[row.Cid] = row.At;
                    // What was there when the reading began is where it starts from, not something to tell.
                    if (_started && DingTalkStore.Notice(row, mark) is { } input) told.Add(input);
                }
                _started = true;
                if (told.Count > 0)
                    await _dispatcher.InvokeAsync(() => { foreach (var input in told.OrderBy(input => input.CreatedAt)) _received(input); });
            }
            if (state != before) await _dispatcher.InvokeAsync(_changed);
        }
        catch (OperationCanceledException) { }
        finally { _reading.Release(); }
    }

    private (DingTalkState, List<DingTalkRow>?) Step()
    {
        if (!Find()) return (State, null);
        byte[] log = [];
        try
        {
            string logFile = _database + "-wal";
            if (File.Exists(logFile))
            {
                using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes, 0, bytes.Length);
                log = DingTalkStore.DecryptLog(bytes, _key);
            }
            if (DingTalkVfs.Query(_database, _key, log, DingTalkStore.Query) is not { } rows) return (DingTalkState.Unreadable, null);
            var parsed = new List<DingTalkRow>();
            foreach (var row in rows) if (DingTalkStore.Parse(row, _accountId) is { } one) parsed.Add(one);
            return (DingTalkState.Reading, parsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or OutOfMemoryException) { return (DingTalkState.Unreadable, null); }
        finally { CryptographicOperations.ZeroMemory(log); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _watch?.Dispose(); _watch = null;
        CryptographicOperations.ZeroMemory(_key); _key = [];
        State = DingTalkState.Off;
    }
}
