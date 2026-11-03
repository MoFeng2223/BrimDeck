using System.Windows;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// Polls the configured QQ mailbox for verification codes and hands hits to the active deck. Never writes mail bodies to disk.
internal sealed class MailOtpWatcher
{
    private readonly App _app;
    private readonly IMailOtpMailbox _mailbox;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationTokenSource? _loop;
    private int _running;
    private bool _seeded;

    public MailOtpWatcher(App app, IMailOtpMailbox? mailbox = null)
    {
        _app = app;
        _mailbox = mailbox ?? new QqImapMailOtp();
    }

    public void Apply(DeckSettings settings)
    {
        bool on = settings.MailOtpEnabled && settings.MailOtpAddress.Length > 0;
        if (!on) { Stop(); return; }
        Start();
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _loop, null);
        cts?.Cancel();
        cts?.Dispose();
        lock (_gate) { _seen.Clear(); _seeded = false; }
    }

    private void Start()
    {
        if (_loop is not null) return;
        var cts = new CancellationTokenSource();
        _loop = cts;
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    private async Task LoopAsync(CancellationToken cancellation)
    {
        // Stagger the first poll slightly so startup is not blocked.
        try { await Task.Delay(2500, cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        while (!cancellation.IsCancellationRequested)
        {
            try { await PollOnce(cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch { /* next round */ }
            try { await Task.Delay(TimeSpan.FromSeconds(45), cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollOnce(CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var settings = _app.Settings;
            if (!settings.MailOtpEnabled || settings.MailOtpAddress.Length == 0) return;
            var secret = _app.Secrets.Read(MailOtp.SecretId);
            if (secret.Length == 0) return;
            IReadOnlyList<MailOtpHit> hits;
            try { hits = await _mailbox.RecentAsync(settings.MailOtpAddress, secret, cancellation).ConfigureAwait(false); }
            finally { /* secret string is ephemeral; GC will reclaim */ }
            if (hits.Count == 0) return;
            MailOtpHit? newest = null;
            lock (_gate)
            {
                // First successful poll only remembers ids so an old inbox code is not flashed on enable.
                if (!_seeded)
                {
                    foreach (var hit in hits) _seen.Add(hit.MessageId);
                    _seeded = true;
                    return;
                }
                foreach (var hit in hits.OrderByDescending(h => h.At))
                {
                    if (!_seen.Add(hit.MessageId)) continue;
                    newest = hit;
                    break;
                }
                // Cap remembered ids.
                if (_seen.Count > 200)
                {
                    _seen.Clear();
                    foreach (var hit in hits) _seen.Add(hit.MessageId);
                }
            }
            if (newest is null) return;
            var toast = newest;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var deck in _app.Decks) deck.ShowMailOtp(toast);
            }, DispatcherPriority.Background);
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
}
