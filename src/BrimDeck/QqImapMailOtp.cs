using System.Text;
using BrimDeck.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace BrimDeck;

// Read-only QQ IMAP (imap.qq.com): recent inbox messages only; bodies are scanned in memory and discarded.
// Prefer IMAP IDLE so a new verification mail surfaces within seconds; fall back to short polls when IDLE drops.
internal sealed class QqImapMailOtp : IMailOtpMailbox
{
    private const int Recent = 12;
    private const int BodyChars = 4000;

    public Task<IReadOnlyList<MailOtpHit>> RecentAsync(string address, string authorizationCode, CancellationToken cancellation)
        => WithInboxAsync(address, authorizationCode, FetchRecentAsync, cancellation);

    public async Task ListenAsync(string address, string authorizationCode, Func<IReadOnlyList<MailOtpHit>, CancellationToken, Task> onHits, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(authorizationCode))
        {
            // Keep the outer loop from spinning hot when credentials are incomplete.
            await Task.Delay(TimeSpan.FromSeconds(MailOtp.PollSeconds), cancellation).ConfigureAwait(false);
            return;
        }

        using var client = new ImapClient();
        client.Timeout = 20_000;
        await client.ConnectAsync(MailOtp.DefaultHost, MailOtp.DefaultPort, SecureSocketOptions.SslOnConnect, cancellation).ConfigureAwait(false);
        try
        {
            await client.AuthenticateAsync(address.Trim(), authorizationCode, cancellation).ConfigureAwait(false);
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, cancellation).ConfigureAwait(false);

            // Seed / first batch immediately so enable-and-wait is not gated on the first IDLE heartbeat.
            await onHits(await FetchRecentAsync(inbox, cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false);

            while (!cancellation.IsCancellationRequested)
            {
                if (!client.IsConnected || !inbox.IsOpen)
                    throw new IOException("QQ IMAP connection closed.");

                using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                done.CancelAfter(TimeSpan.FromMinutes(MailOtp.IdleHeartbeatMinutes));

                void OnCountChanged(object? sender, EventArgs e)
                {
                    try { done.Cancel(); }
                    catch (ObjectDisposedException) { /* idle already finished */ }
                }

                inbox.CountChanged += OnCountChanged;
                try
                {
                    // Returns when done is cancelled (new mail, heartbeat, or outer cancel).
                    await client.IdleAsync(done.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    // Heartbeat or CountChanged — fall through and refresh.
                }
                finally
                {
                    inbox.CountChanged -= OnCountChanged;
                }

                if (cancellation.IsCancellationRequested) return;

                // Re-open if the server left the folder after IDLE.
                if (!inbox.IsOpen)
                    await inbox.OpenAsync(FolderAccess.ReadOnly, cancellation).ConfigureAwait(false);

                await onHits(await FetchRecentAsync(inbox, cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false);
            }
        }
        finally
        {
            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false); }
                catch { /* shutting down */ }
            }
        }
    }

    private static async Task<T> WithInboxAsync<T>(string address, string authorizationCode, Func<IMailFolder, CancellationToken, Task<T>> work, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(authorizationCode)) return default!;
        using var client = new ImapClient();
        client.Timeout = 20_000;
        await client.ConnectAsync(MailOtp.DefaultHost, MailOtp.DefaultPort, SecureSocketOptions.SslOnConnect, cancellation).ConfigureAwait(false);
        try
        {
            await client.AuthenticateAsync(address.Trim(), authorizationCode, cancellation).ConfigureAwait(false);
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, cancellation).ConfigureAwait(false);
            return await work(inbox, cancellation).ConfigureAwait(false);
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(true, cancellation).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<MailOtpHit>> FetchRecentAsync(IMailFolder inbox, CancellationToken cancellation)
    {
        if (inbox.Count == 0) return [];
        // Index order is oldest→newest; take the tail instead of SearchQuery.All (slow on large inboxes).
        int start = Math.Max(0, inbox.Count - Recent);
        var hits = new List<MailOtpHit>();
        for (int i = inbox.Count - 1; i >= start; i--)
        {
            cancellation.ThrowIfCancellationRequested();
            var message = await inbox.GetMessageAsync(i, cancellation).ConfigureAwait(false);
            try
            {
                var subject = message.Subject ?? "";
                var body = PeekText(message);
                var code = MailOtp.Extract(subject, body);
                if (code is null) continue;
                var from = message.From?.Mailboxes.FirstOrDefault()?.ToString()
                    ?? message.From?.ToString()
                    ?? "";
                var id = message.MessageId is { Length: > 0 } mid ? mid : $"idx:{i}:{message.Date.UtcTicks}";
                hits.Add(new MailOtpHit(code, MailOtp.ShortFrom(from), message.Date.ToUniversalTime(), id));
            }
            finally
            {
                // Drop references promptly; do not write message content to disk.
                message.Dispose();
            }
        }
        return hits;
    }

    private static string PeekText(MimeMessage message)
    {
        if (message.TextBody is { Length: > 0 } text)
            return text.Length <= BodyChars ? text : text[..BodyChars];
        if (message.HtmlBody is { Length: > 0 } html)
        {
            var plain = StripTags(html);
            return plain.Length <= BodyChars ? plain : plain[..BodyChars];
        }
        return "";
    }

    private static string StripTags(string html)
    {
        var sb = new StringBuilder(Math.Min(html.Length, BodyChars));
        bool hide = false;
        for (int i = 0; i < html.Length && sb.Length < BodyChars; i++)
        {
            char c = html[i];
            if (c == '<') { hide = true; continue; }
            if (c == '>') { hide = false; continue; }
            if (!hide) sb.Append(c);
        }
        return System.Net.WebUtility.HtmlDecode(sb.ToString());
    }
}
