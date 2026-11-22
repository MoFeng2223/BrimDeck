using System.IO;
using System.Windows.Threading;
using BrimDeck.Core.Agenda;

namespace BrimDeck;

// The planner's data for the whole application: every panel draws from this one AgendaBook and changes it here. It reads
// agenda.json from the data folder (next to settings.json) in the background at the start, saves a second after the last
// change, and writes what is left at exit. A file that cannot be read for now (in use, no permission) is read again every
// half minute, and nothing is written until it has been: what is changed meanwhile joins what it holds. Runs on the UI thread.
internal sealed class AgendaCenter : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1), RetryDelay = TimeSpan.FromSeconds(30);
    private readonly AgendaStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _save, _retry;
    // The writes in the background, one after another; each one's text was taken when it was queued.
    private Task _writing = Task.CompletedTask;
    // _loaded: the file was read (or there was none, or it was moved aside), so it may be written. _dirty: changes not written yet.
    private bool _loaded, _loading, _merging, _dirty, _disposed;

    public AgendaBook Book { get; } = new();
    // How the last reading went; null until the first one has finished.
    public AgendaReadState? ReadState { get; private set; }
    // The name the unreadable file was given when it was moved aside (ReadState Recovered).
    public string? KeptAs { get; private set; }
    // The last write failed; it is tried again.
    public bool SaveFailed { get; private set; }
    public string FilePath => _store.FilePath;
    // Raised after the data changed or was read, and after the reading or saving state changed.
    public event Action? Changed;

    // "查看" from elsewhere (the notices page): the planner page shows Day, switched to View, with ItemId marked. The request
    // waits in PendingReveal until a page takes it (TakeReveal), so a page opened afterwards still finds it.
    public AgendaReveal? PendingReveal { get; private set; }
    public event Action<AgendaReveal>? RevealRequested;

    public AgendaCenter(string dataFolder, Dispatcher dispatcher)
    {
        _store = new AgendaStore(dataFolder);
        _dispatcher = dispatcher;
        _save = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = SaveDelay };
        _save.Tick += (_, _) => { _save.Stop(); _save.Interval = SaveDelay; Save(); };
        _retry = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = RetryDelay };
        _retry.Tick += (_, _) => { _retry.Stop(); Load(); };
        Book.Changed += BookChanged;
        Load();
    }

    // from: whatever asks, such as the panel whose notices page asked, so a page can tell a request meant for its own panel.
    public void Reveal(DateOnly day, AgendaView view, string? itemId = null, object? from = null)
    {
        var reveal = new AgendaReveal(day, view, itemId, from);
        PendingReveal = reveal;
        RevealRequested?.Invoke(reveal);
    }

    public AgendaReveal? TakeReveal()
    {
        var reveal = PendingReveal;
        PendingReveal = null;
        return reveal;
    }

    private void BookChanged()
    {
        if (!_merging) { _dirty = true; QueueSave(); }
        Changed?.Invoke();
    }

    private void QueueSave()
    {
        if (!_loaded || _disposed || _save.IsEnabled) return;
        _save.Start();
    }

    private async void Load()
    {
        if (_loading || _loaded || _disposed) return;
        _loading = true;
        AgendaRead read;
        // Anything else going wrong (a path the system refuses) is treated as a file that cannot be reached now.
        try { read = await Task.Run(() => _store.Read(DateTime.Now)); }
        catch (Exception) { read = new(AgendaReadState.Unavailable, new()); }
        finally { _loading = false; }
        if (_disposed) return;
        ReadState = read.State;
        if (read.KeptAs is not null) KeptAs = read.KeptAs;
        if (read.Writable)
        {
            _loaded = true;
            _merging = true;
            try { Book.MergeLoaded(read.Data); }
            finally { _merging = false; }
            // Changed before the file could be read: what was changed is written now, together with what the file held.
            if (_dirty) QueueSave();
        }
        else _retry.Start();
        Changed?.Invoke();
    }

    private void Save()
    {
        if (!_loaded || !_dirty || _disposed) return;
        _dirty = false;
        var text = AgendaStore.Serialize(Book.Snapshot());
        _writing = WriteAfter(_writing, text);
    }

    // Runs without the UI thread, so Flush can wait for it there.
    private async Task WriteAfter(Task previous, string text)
    {
        try { await previous.ConfigureAwait(false); } catch (Exception) { }
        try
        {
            await Task.Run(() => _store.Write(text)).ConfigureAwait(false);
            _ = _dispatcher.BeginInvoke(() => { if (SaveFailed) { SaveFailed = false; Changed?.Invoke(); } });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                // Written again, with whatever changed since, after a while.
                _dirty = true; SaveFailed = true;
                _save.Stop(); _save.Interval = RetryDelay; _save.Start();
                Changed?.Invoke();
            });
        }
    }

    // On exit: the writes under way finish, and what is not written yet is written now.
    public void Flush()
    {
        if (!_loaded) return;
        _save.Stop();
        try { _writing.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        if (!_dirty) return;
        _dirty = false;
        try { _store.Write(Book.Snapshot()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _dirty = true; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        _disposed = true;
        _save.Stop(); _retry.Stop();
        Book.Changed -= BookChanged;
    }
}
