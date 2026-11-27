using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace BrimDeck.Core.Mail;

// Where a mailbox is reached. Tls is off only for the tests' local server; every real mailbox is IMAP over TLS (MailProviders.Port).
public sealed record MailEndpoint(string Host, int Port, string UserName, bool Tls = true);

// What signs the user in: the authorization code or app password as typed, or an OAuth access token (Microsoft).
public sealed record MailSecret(string? Password, string? AccessToken = null);

// The saved code or sign-in is missing or can no longer be used: the user has to give it again.
public sealed class MailSecretException(string message) : Exception(message);

// A message new to the inbox, as much of it as a notice shows.
public sealed record MailMessage(uint UidValidity, uint Uid, string FromName, string FromAddress, string Subject, string Preview, DateTimeOffset Date);

// Connecting: signing in. Watching: signed in, new mail is noticed. Retrying: the server could not be reached or dropped the
// connection, and another attempt follows by itself. Refused: the server turned the sign-in down; nothing is tried again until
// the user gives the code again or asks to (several wrong attempts can get an account locked).
public enum MailState { Connecting, Watching, Retrying, Refused }
public sealed record MailStatus(MailState State, string Detail = "");

// How often a watch looks and how long it waits between attempts; the tests shorten them.
public sealed record MailPace
{
    // IDLE is ended and started again this often, with a NOOP between: some servers (QQ, 163) do not always push new mail while idle.
    public TimeSpan Idle { get; init; } = TimeSpan.FromSeconds(60);
    // A server without IDLE is asked this often.
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public IReadOnlyList<TimeSpan> Retry { get; init; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5)];
}

// Watches one mailbox's inbox over IMAP and reports the mail that arrives while it watches. The inbox is opened read-only and
// message bodies are only peeked at, so nothing is marked read. Mail already there at the first connection is not reported;
// after a dropped connection, what arrived meanwhile is. One connection at a time; a broken one is made again after a pause that
// grows with each failure.
public sealed class MailWatch(MailEndpoint endpoint, Func<CancellationToken, Task<MailSecret>> secret, Action<MailMessage> arrived, Action<MailStatus> status, MailPace? pace = null) : IDisposable
{
    // After a long time without a connection, only the latest few are reported.
    public const int MaxAtOnce = 20;
    // The preview kept of a message's text.
    public const int MaxPreview = 300;

    // .NET has GB2312, GBK, Big5, Shift_JIS and the other code pages only once they are registered, and MimeKit looks for them
    // once, the first time it decodes anything: registered later, a subject or a text in GB2312 (Chinese mail from Outlook and
    // Exchange) is read as Latin-1 for as long as the app runs. So they are registered before the first connection is made.
    static MailWatch() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    private readonly MailPace _pace = pace ?? new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _round;
    private Task? _run;
    // The inbox's UIDVALIDITY and the first UID not seen yet; 0 until the first connection has read them.
    private uint _validity, _next;
    private volatile bool _refused;

    public void Start() { lock (_gate) _run ??= Task.Run(() => RunAsync(_stop.Token)); }

    // Connects again now: when the user asks to retry, or after the PC wakes (refused false: a mailbox the server refused is
    // not tried again by itself).
    public void Nudge(bool refused = true) { lock (_gate) { if (_refused && !refused) return; try { _round?.Cancel(); } catch (ObjectDisposedException) { } } }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        int failures = 0;
        while (!stop.IsCancellationRequested)
        {
            using var round = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (_gate) _round = round;
            bool refused = false;
            _refused = false;
            try
            {
                status(new(MailState.Connecting));
                await SessionAsync(() => failures = 0, round.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            // Nudged: connect again at once.
            catch (OperationCanceledException) when (round.IsCancellationRequested) { failures = 0; continue; }
            catch (Exception ex) when (ex is AuthenticationException or MailSecretException) { _refused = refused = true; status(new(MailState.Refused, Detail(ex))); }
            catch (Exception ex) when (Transient(ex)) { status(new(MailState.Retrying, Detail(ex))); }
            try
            {
                // Refused: wait for the user (Nudge) or the end.
                if (refused) await Task.Delay(Timeout.InfiniteTimeSpan, round.Token).ConfigureAwait(false);
                else await Task.Delay(_pace.Retry[Math.Min(failures++, _pace.Retry.Count - 1)], round.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { failures = 0; }
            catch (OperationCanceledException) { break; }
            finally { lock (_gate) _round = null; }
        }
    }

    // What breaks a connection without saying anything about the account: the network, the server, a timeout.
    private static bool Transient(Exception ex) => ex is IOException or SocketException or ImapProtocolException or ImapCommandException or TimeoutException
        or ServiceNotConnectedException or ServiceNotAuthenticatedException or SslHandshakeException or OperationCanceledException or InvalidOperationException or FolderNotFoundException;

    // The server's own words where it gave any, on one line.
    private static string Detail(Exception ex)
    {
        string text = (ex is ImapCommandException command && command.ResponseText is { Length: > 0 } said ? said : ex.Message).ReplaceLineEndings(" ").Trim();
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    // Signs in and opens the inbox without changing anything in it. Throws AuthenticationException when the server refuses the
    // user name or the code.
    private static async Task<IMailFolder> OpenAsync(ImapClient client, MailEndpoint endpoint, MailSecret secret, MailPace pace, CancellationToken stop)
    {
        client.Timeout = (int)pace.Timeout.TotalMilliseconds;
        // As the app's HTTP requests: the certificate's chain and name are checked, its revocation list is not fetched (a list that
        // cannot be reached would fail the connection).
        client.CheckCertificateRevocation = false;
        await client.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Tls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None, stop).ConfigureAwait(false);
        if (secret.AccessToken is { Length: > 0 } token) await client.AuthenticateAsync(new SaslMechanismOAuth2(endpoint.UserName, token), stop).ConfigureAwait(false);
        else
        {
            client.AuthenticationMechanisms.Remove("XOAUTH2"); client.AuthenticationMechanisms.Remove("OAUTHBEARER");
            await client.AuthenticateAsync(endpoint.UserName, secret.Password ?? "", stop).ConfigureAwait(false);
        }
        // NetEase's mailboxes (163, 126) refuse to open the inbox ("Unsafe Login") unless the client has named itself.
        if (client.Capabilities.Contains(ImapCapability.Id))
            await client.IdentifyAsync(new ImapImplementation { Name = "BrimDeck", Version = ApplicationVersion.Current, Vendor = "BrimDeck" }, stop).ConfigureAwait(false);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, stop).ConfigureAwait(false);
        return inbox;
    }

    // Whether the mailbox can be signed in to and its inbox opened, for the window that adds it. Null when it can; else what
    // went wrong, and whether the server refused the sign-in (the code is wrong, or IMAP is switched off for the account).
    public static async Task<(bool Refused, string Detail)?> CheckAsync(MailEndpoint endpoint, MailSecret secret, CancellationToken stop, MailPace? pace = null)
    {
        using var client = new ImapClient();
        try
        {
            await OpenAsync(client, endpoint, secret, pace ?? new(), stop).ConfigureAwait(false);
            await client.DisconnectAsync(true, stop).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
        catch (AuthenticationException ex) { return (true, Detail(ex)); }
        catch (Exception ex) when (Transient(ex) || ex is ArgumentException) { return (false, Detail(ex)); }
    }

    private async Task SessionAsync(Action watching, CancellationToken stop)
    {
        using var client = new ImapClient();
        var inbox = await OpenAsync(client, endpoint, await secret(stop).ConfigureAwait(false), _pace, stop).ConfigureAwait(false);
        try
        {
            uint validity = inbox.UidValidity;
            if (_next == 0 || validity != _validity)
            {
                // The first look at this inbox (or the server numbered it anew): what is there now is not new.
                _validity = validity;
                _next = inbox.UidNext is { } next ? next.Id : await HighestAsync(inbox, stop).ConfigureAwait(false) + 1;
            }
            else await ReportNewAsync(inbox, stop).ConfigureAwait(false);
            watching(); status(new(MailState.Watching));

            bool changed = false;
            CancellationTokenSource? idle = null;
            // Raised on this session's own thread while it idles or reads a reply.
            inbox.CountChanged += (_, _) => { changed = true; try { idle?.Cancel(); } catch (ObjectDisposedException) { } };
            while (true)
            {
                stop.ThrowIfCancellationRequested();
                if (changed) { changed = false; await ReportNewAsync(inbox, stop).ConfigureAwait(false); continue; }
                if (client.Capabilities.Contains(ImapCapability.Idle))
                {
                    using var done = new CancellationTokenSource(_pace.Idle);
                    idle = done;
                    try { await client.IdleAsync(done.Token, stop).ConfigureAwait(false); }
                    finally { idle = null; }
                }
                else await Task.Delay(_pace.Poll, stop).ConfigureAwait(false);
                if (!changed) await client.NoOpAsync(stop).ConfigureAwait(false);
            }
        }
        finally
        {
            // A goodbye when the connection still stands; a broken one is simply dropped.
            if (client.IsConnected && !stop.IsCancellationRequested)
                try { using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await client.DisconnectAsync(true, bye.Token).ConfigureAwait(false); }
                catch (Exception ex) when (Transient(ex)) { }
        }
    }

    // The highest UID in the inbox, for a server that does not say which comes next; 0 for an empty inbox.
    private static async Task<uint> HighestAsync(IMailFolder inbox, CancellationToken stop)
    {
        if (inbox.Count == 0) return 0;
        var last = await inbox.FetchAsync(inbox.Count - 1, inbox.Count - 1, new FetchRequest(MessageSummaryItems.UniqueId), stop).ConfigureAwait(false);
        return last.Count > 0 ? last[^1].UniqueId.Id : 0;
    }

    // Reports the messages numbered from _next on that are unread, oldest first, and moves _next past them.
    private async Task ReportNewAsync(IMailFolder inbox, CancellationToken stop)
    {
        // "n:*" always names the highest message, also when it is below n; the search's answer is filtered for that.
        var found = await inbox.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId(_next), UniqueId.MaxValue)), stop).ConfigureAwait(false);
        var fresh = found.Where(uid => uid.Id >= _next).OrderBy(uid => uid.Id).ToList();
        if (fresh.Count == 0) return;
        uint next = fresh[^1].Id + 1;
        if (fresh.Count > MaxAtOnce) fresh = fresh.GetRange(fresh.Count - MaxAtOnce, MaxAtOnce);
        const MessageSummaryItems basic = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.Flags;
        IList<IMessageSummary> summaries;
        try { summaries = await inbox.FetchAsync(fresh, new FetchRequest(basic | MessageSummaryItems.PreviewText), stop).ConfigureAwait(false); }
        // A message whose text the server cannot hand out in part is still reported, without its preview.
        catch (ImapCommandException) { summaries = await inbox.FetchAsync(fresh, new FetchRequest(basic), stop).ConfigureAwait(false); }
        _next = next;
        foreach (var summary in summaries.OrderBy(s => s.UniqueId.Id))
        {
            // Read already elsewhere, or a draft or a copy filed here as read: nothing to tell.
            if (summary.Flags is { } flags && flags.HasFlag(MessageFlags.Seen)) continue;
            var from = summary.Envelope?.From?.Mailboxes.FirstOrDefault();
            arrived(new MailMessage(_validity, summary.UniqueId.Id, from?.Name?.Trim() ?? "", from?.Address?.Trim() ?? "", summary.Envelope?.Subject?.Trim() ?? "",
                Preview(summary.PreviewText), summary.InternalDate ?? summary.Envelope?.Date ?? DateTimeOffset.UtcNow));
        }
    }

    // A message's text on one run of lines: blank lines and runs of spaces closed up, cut to MaxPreview.
    public static string Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.ReplaceLineEndings("\n").Split('\n').Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Where(line => line.Length > 0);
        string joined = string.Join('\n', lines);
        return joined.Length > MaxPreview ? joined[..MaxPreview].TrimEnd() + "…" : joined;
    }
}
