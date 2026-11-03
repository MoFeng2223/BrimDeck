using System.Text;
using BrimDeck.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace BrimDeck;

// Read-only QQ IMAP (imap.qq.com): recent inbox messages only; bodies are scanned in memory and discarded.
internal sealed class QqImapMailOtp : IMailOtpMailbox
{
    private const int Recent = 12;
    private const int BodyChars = 4000;

    public async Task<IReadOnlyList<MailOtpHit>> RecentAsync(string address, string authorizationCode, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(authorizationCode)) return [];
        using var client = new ImapClient();
        client.Timeout = 20_000;
        await client.ConnectAsync(MailOtp.DefaultHost, MailOtp.DefaultPort, SecureSocketOptions.SslOnConnect, cancellation).ConfigureAwait(false);
        try
        {
            await client.AuthenticateAsync(address.Trim(), authorizationCode, cancellation).ConfigureAwait(false);
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, cancellation).ConfigureAwait(false);
            if (inbox.Count == 0) return [];
            var uids = await inbox.SearchAsync(SearchQuery.All, cancellation).ConfigureAwait(false);
            if (uids.Count == 0) return [];
            // Newest UIDs are at the end of SearchQuery.All for a typical INBOX.
            IList<UniqueId> take = uids.Count <= Recent ? uids : uids.Skip(uids.Count - Recent).ToList();
            var hits = new List<MailOtpHit>();
            foreach (var uid in take.Reverse())
            {
                cancellation.ThrowIfCancellationRequested();
                var message = await inbox.GetMessageAsync(uid, cancellation).ConfigureAwait(false);
                try
                {
                    var subject = message.Subject ?? "";
                    var body = PeekText(message);
                    var code = MailOtp.Extract(subject, body);
                    if (code is null) continue;
                    var from = message.From?.Mailboxes.FirstOrDefault()?.ToString()
                        ?? message.From?.ToString()
                        ?? "";
                    var id = message.MessageId is { Length: > 0 } mid ? mid : $"uid:{uid}";
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
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(true, cancellation).ConfigureAwait(false);
        }
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
