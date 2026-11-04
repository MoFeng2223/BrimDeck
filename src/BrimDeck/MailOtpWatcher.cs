using System.Windows;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// Listens to the configured QQ mailbox for verification codes and hands hits to the active deck. Never writes mail bodies to disk.
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
        // Restart so address/secret changes reconnect immediately instead of waiting out a prior IDLE/poll.
        Stop(clearSeen: false);
        Start();
    }

    public void Stop() => Stop(clearSeen: true);

    private void Stop(bool clearSeen)
    {
        var cts = Interlocked.Exchange(ref _loop, null);
        cts?.Cancel();
        cts?.Dispose();
        if (clearSeen)
        {
            lock (_gate) { _seen.Clear(); _seeded = false; }
        }
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
        while (!cancellation.IsCancellationRequested)
        {
            var settings = _app.Settings;
            if (!settings.MailOtpEnabled || settings.MailOtpAddress.Length == 0) return;
            var secret = _app.Secrets.Read(MailOtp.SecretId);
            if (secret.Length == 0)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(MailOtp.PollSeconds), cancellation).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                await _mailbox.ListenAsync(settings.MailOtpAddress, secret, HandleHitsAsync, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch
            {
                // Reconnect after a short gap (IDLE drop, network blip, auth hiccup).
            }

            try { await Task.Delay(TimeSpan.FromSeconds(MailOtp.PollSeconds), cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task HandleHitsAsync(IReadOnlyList<MailOtpHit> hits, CancellationToken cancellation)
    {
        if (hits.Count == 0) return;
        // Serialize UI hand-off so overlapping IDLE wakeups do not race the seen set.
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            MailOtpHit? newest = null;
            lock (_gate)
            {
                // First successful batch only remembers ids so an old inbox code is not flashed on enable.
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
            }, DispatcherPriority.Normal);
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
}
