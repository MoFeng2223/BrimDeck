using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using BrimDeck.Core;
using BrimDeck.Core.Mail;

namespace BrimDeck;

// The mailboxes the user connected ("邮箱" settings page): one IMAP connection each (MailWatch) while dynamic notices are on and
// the mailbox is switched on, and each new message handed to the notices as a notice of BrimDeck's own (MailNotices). What signs
// a mailbox in is read from secrets.dat when it connects and kept nowhere else.
internal sealed class MailCenter : IDisposable
{
    private sealed class Entry(MailAccount account, MailWatch watch)
    {
        public MailAccount Account { get; } = account;
        public MailWatch Watch { get; } = watch;
        public MailStatus Status { get; set; } = new(MailState.Connecting);
    }

    private readonly App _app;
    private readonly Dictionary<Guid, Entry> _entries = [];
    // Microsoft access tokens by mailbox, used until shortly before they expire.
    private readonly Dictionary<Guid, MicrosoftTokens> _tokens = [];
    private readonly Lazy<HttpClient> _http = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
    private bool _disposed;

    // A mailbox's state changed; raised on the UI thread.
    public event Action? Changed;

    public MailCenter(App app)
    {
        _app = app;
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
    }

    // The mailbox's connection as it stands; null while it is not connected at all (switched off, or notices are off).
    public MailStatus? Status(Guid id) => _entries.TryGetValue(id, out var entry) ? entry.Status : null;

    // Starts, stops and restarts connections to match the settings. Called at start-up and after every settings change, after
    // the notices have applied theirs.
    public void ApplySettings()
    {
        if (_disposed) return;
        var wanted = _app.Notices.Enabled ? _app.Settings.MailAccounts.Where(account => account.Enabled).ToDictionary(account => account.Id) : [];
        bool changed = false;
        foreach (var id in _entries.Keys.ToList())
        {
            if (wanted.TryGetValue(id, out var account) && account.Signature == _entries[id].Account.Signature) continue;
            _entries[id].Watch.Dispose(); _entries.Remove(id); changed = true;
            lock (_tokens) _tokens.Remove(id);
        }
        foreach (var account in wanted.Values)
        {
            if (_entries.ContainsKey(account.Id) || account.Endpoint() is not { } endpoint) continue;
            var kept = account.Copy();
            Entry? entry = null;
            var watch = new MailWatch(endpoint, stop => SecretAsync(kept, stop),
                message => _app.Dispatcher.BeginInvoke(() => Arrived(entry!, message)),
                status => _app.Dispatcher.BeginInvoke(() => { if (IsCurrent(entry!)) { entry!.Status = status; Changed?.Invoke(); } }));
            _entries[account.Id] = entry = new Entry(kept, watch);
            watch.Start(); changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    private bool IsCurrent(Entry entry) => !_disposed && _entries.TryGetValue(entry.Account.Id, out var current) && ReferenceEquals(current, entry);

    private void Arrived(Entry entry, MailMessage message)
    {
        if (!IsCurrent(entry)) return;
        _app.Notices.Post(MailNotices.Notice(entry.Account, message, DateTimeOffset.UtcNow));
    }

    // Tries a mailbox the server refused once more, at the user's request.
    public void Retry(Guid id) { if (_entries.TryGetValue(id, out var entry)) entry.Watch.Nudge(); }

    // What signs the mailbox in, read as the connection is made. A code or password as saved; for Microsoft, an access token
    // made from the saved refresh token (and the newer refresh token saved in its place when Microsoft gives one).
    private async Task<MailSecret> SecretAsync(MailAccount account, CancellationToken stop)
    {
        string saved;
        try { saved = _app.Secrets.Read(account.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException or FormatException)
        { throw new MailSecretException(ex.Message); }
        if (saved.Length == 0) throw new MailSecretException("");
        if (MailProviders.Find(account.Provider).SignIn != MailSignIn.Microsoft) return new(saved);
        MicrosoftTokens? tokens;
        lock (_tokens) _tokens.TryGetValue(account.Id, out tokens);
        if (tokens is null || tokens.Expires - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(5))
        {
            tokens = await MicrosoftMail.RefreshAsync(_http.Value, saved, stop).ConfigureAwait(false);
            lock (_tokens) _tokens[account.Id] = tokens;
            if (tokens.Refresh != saved)
                try { _app.Secrets.Write(account.Id, tokens.Refresh); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException) { NoticeCenter.Log(ex); }
        }
        return new(null, tokens.Access);
    }

    // Signs in to a Microsoft account in the browser for the window that adds a mailbox; null when the user did not allow it.
    public Task<MicrosoftTokens?> SignInMicrosoftAsync(string address, Func<string, bool> open, CancellationToken stop)
        => MicrosoftMail.SignInAsync(_http.Value, address, open, stop);

    // After sleep the connections are dead; they are made again at once instead of after a timeout.
    private void PowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        _app.Dispatcher.BeginInvoke(() => { foreach (var entry in _entries.Values) entry.Watch.Nudge(refused: false); });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged;
        foreach (var entry in _entries.Values) entry.Watch.Dispose();
        _entries.Clear();
        if (_http.IsValueCreated) _http.Value.Dispose();
    }
}
