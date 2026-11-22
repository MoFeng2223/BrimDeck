using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BrimDeck;

// Keeps Windows' own banners in line with each listed application's banner switch (NoticeAppRule.HideBanner, NoticeBanners).
// While any applies, Windows' notification settings are watched, so a banner the user turns on again in Windows Settings is
// noticed at once and reported (BannerState.Released), and NoticeCenter turns that application's switch off. The registry and the
// ledger are worked on a worker thread, one pass at a time; the dispatcher only takes a snapshot of the settings and shows the
// result. Update runs after each settings or source change, ReleaseAll on exit, which gives the banners back while BrimDeck is not
// running.
internal sealed class NoticeBannerKeeper : IDisposable
{
    private readonly Func<DeckSettings> _settings;
    private readonly Dispatcher _dispatcher;
    private readonly string _ledgerPath;
    private readonly Func<bool> _windowsReadable;
    private readonly IBannerRegistry _registry;
    private readonly string[] _own;
    private readonly bool _watch;
    private readonly Action<Exception> _failed;
    // One pass at a time; a request while one runs is taken up once it ends (_again). _work guards only _busy, _again and
    // _running and is held briefly, also on the UI thread; _pass serializes the registry and ledger work of a pass and of
    // ReleaseAll, so the UI thread never waits on it except at exit.
    private readonly object _work = new();
    private readonly object _pass = new();
    private Task _running = Task.CompletedTask;
    private bool _busy, _again;
    private bool _released;
    private SettingsWatch? _watcher;

    // registry is Windows' notification settings (NoticeCenter passes the real registry); own are BrimDeck's own ids; watch follows
    // changes in the registry.
    public NoticeBannerKeeper(Func<DeckSettings> settings, Dispatcher dispatcher, string ledgerPath, Func<bool> windowsReadable,
        IBannerRegistry registry, IEnumerable<string> own, Action<Exception> failed, bool watch = true)
    {
        _settings = settings; _dispatcher = dispatcher; _ledgerPath = ledgerPath; _windowsReadable = windowsReadable;
        _registry = registry; _own = [.. own]; _failed = failed; _watch = watch;
    }

    // Each application's state after the last pass, by application id.
    public IReadOnlyDictionary<string, BannerState> States { get; private set; } = new Dictionary<string, BannerState>();
    // Neither the ledger nor its backup could be read: nothing was changed, and banners turned off before may still be off.
    public bool LedgerUnreadable { get; private set; }
    // Some application's switch applies (it is on, notices are on, and Windows lets BrimDeck read notifications).
    public bool Holding { get; private set; }
    public event Action? Changed;

    public void Update()
    {
        if (_released) return;
        var s = _settings();
        Holding = s.NoticesEnabled && _windowsReadable() && s.NoticeApps.Any(rule => rule.HideBanner);
        Watch(Holding);
        Request();
    }

    // On exit: every banner BrimDeck turned off goes back to Windows, after a pass still running has ended. Released stays while
    // the switch is on, so a banner the user turned on again is still left alone at the next start.
    public void ReleaseAll()
    {
        _released = true; Holding = false;
        Watch(false);
        Task running;
        lock (_work) running = _running;
        try { running.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        lock (_pass)
        {
            if (BannerLedger.Load(_ledgerPath) is not { } ledger || ledger.Records.Count == 0) return;
            NoticeBanners.Reconcile(new HashSet<string>(), false, ledger, _registry, () => ledger.Save(_ledgerPath));
        }
    }

    public void Dispose() => Watch(false);

    // Takes a snapshot of what the pass needs and runs it on a worker thread, or marks it to run again after the one under way.
    private void Request()
    {
        if (_released) return;
        lock (_work)
        {
            if (_busy) { _again = true; return; }
            _busy = true;
        }
        var settings = _settings().Copy();
        bool readable = _windowsReadable();
        Task pass = Task.Run(() => Pass(settings, readable));
        lock (_work) _running = pass;
    }

    private void Pass(DeckSettings settings, bool readable)
    {
        IReadOnlyDictionary<string, BannerState> states = new Dictionary<string, BannerState>();
        bool unreadable = false;
        try
        {
            lock (_pass)
            {
                if (!_released)
                {
                    if (BannerLedger.Load(_ledgerPath) is not { } ledger) unreadable = true;
                    else
                    {
                        // A banner turned on again outside BrimDeck is not kept in the ledger's Released (switchOn false): its
                        // application's switch goes off instead, so switching it on again turns the banner off again.
                        var wanted = NoticeBanners.Wanted(settings, readable, _own);
                        if (wanted.Count > 0 || ledger.Records.Count > 0 || ledger.Released.Count > 0)
                            states = NoticeBanners.Reconcile(wanted, false, ledger, _registry, () => ledger.Save(_ledgerPath));
                    }
                }
            }
        }
        catch (Exception ex) { _failed(ex); }
        _dispatcher.BeginInvoke(() =>
        {
            States = states; LedgerUnreadable = unreadable;
            bool again;
            lock (_work) { again = _again; _again = false; _busy = false; }
            Changed?.Invoke();
            if (again) Request();
        });
    }

    // Watches Windows' notification settings while the switch applies; a change (the user's in Windows Settings, or BrimDeck's
    // own, which then finds nothing to do) asks for a pass a moment later.
    private void Watch(bool on)
    {
        if (!_watch) return;
        if (on && _watcher is null) _watcher = SettingsWatch.Start(() => _dispatcher.BeginInvoke(() => { if (Holding) Request(); }));
        else if (!on && _watcher is not null) { _watcher.Dispose(); _watcher = null; }
    }

    private sealed class SettingsWatch : IDisposable
    {
        private const int ChangeName = 0x1, ChangeLastSet = 0x4;
        // Several values written together (Windows Settings writes a few) are answered once.
        private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);
        private readonly RegistryKey _key;
        private readonly AutoResetEvent _changed = new(false);
        private readonly ManualResetEvent _stop = new(false);
        private readonly Action _changedHandler;

        private SettingsWatch(RegistryKey key, Action changed) { _key = key; _changedHandler = changed; }

        public static SettingsWatch? Start(Action changed)
        {
            RegistryKey? key;
            try { key = Registry.CurrentUser.OpenSubKey(NoticeBanners.SettingsKey); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
            if (key is null) return null;
            var watch = new SettingsWatch(key, changed);
            new Thread(watch.Run) { IsBackground = true, Name = "BrimDeck banner settings" }.Start();
            return watch;
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    if (RegNotifyChangeKeyValue(_key.Handle, true, ChangeName | ChangeLastSet, _changed.SafeWaitHandle, true) != 0) return;
                    if (WaitHandle.WaitAny([_stop, _changed]) == 0) return;
                    // Settled: later changes within the moment are covered by the same pass.
                    if (_stop.WaitOne(Settle)) return;
                    _changedHandler();
                }
            }
            catch (ObjectDisposedException) { }
            finally { _key.Dispose(); _changed.Dispose(); _stop.Dispose(); }
        }

        public void Dispose() { try { _stop.Set(); } catch (ObjectDisposedException) { } }

        [DllImport("advapi32.dll")]
        private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubtree, int filter, SafeWaitHandle evt, bool asynchronous);
    }
}
