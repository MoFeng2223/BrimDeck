using BrimDeck.Core.Notices;

namespace BrimDeck.Core.Mail;

// A mailbox the user connected (DeckSettings.MailAccounts). What signs it in (the authorization code, or Microsoft's refresh
// token) is not here: it is kept encrypted for the Windows account in secrets.dat under Id.
public sealed class MailAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    // A MailProviders id; an id this version does not know reads as the mailbox's own server (MailProviders.Custom).
    public string Provider { get; set; } = "";
    public string Address { get; set; } = "";
    // The IMAP server of a mailbox with its own server (MailProviders.Custom); the listed providers have theirs.
    public string Host { get; set; } = "";
    // The sign-in name where it is not the one the provider takes by rule (MailProviders.UserFor); empty follows the rule.
    public string UserName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    // Changes whenever the saved code or sign-in is replaced, so that the mailbox is connected again with the new one.
    public Guid Secret { get; set; }

    public MailAccount Copy() => (MailAccount)MemberwiseClone();

    // The source its notices come from, and the key of the user's rule for them ("提示等级").
    public string AppKey => NoticeKeys.Internal("mail:" + Id.ToString("N"));

    // Where it is reached and who signs in; null when it names no server (a mailbox with its own server and none given).
    public MailEndpoint? Endpoint()
    {
        var provider = MailProviders.Find(Provider);
        string host = provider.Id == MailProviders.Custom ? Host.Trim() : MailProviders.HostFor(provider, Address);
        string user = UserName.Trim().Length > 0 ? UserName.Trim() : MailProviders.UserFor(provider, Address);
        return host.Length == 0 || user.Length == 0 ? null : new MailEndpoint(host, MailProviders.Port, user);
    }

    // What decides the connection: with another, the mailbox is connected anew.
    public string Signature => $"{Provider}|{Address}|{Host}|{UserName}|{Secret:N}";

    public static List<MailAccount> Normalize(IEnumerable<MailAccount?>? accounts)
    {
        var kept = new List<MailAccount>();
        foreach (var account in accounts ?? [])
        {
            if (account is null || kept.Count >= MailProviders.MaxAccounts) continue;
            account.Provider = MailProviders.Find(account.Provider).Id;
            account.Address = Clean(account.Address, 254); account.Host = MailProviders.Host(account.Host) ?? ""; account.UserName = Clean(account.UserName, 254);
            if (account.Id == Guid.Empty || account.Address.Length == 0 || kept.Any(other => other.Id == account.Id)) continue;
            kept.Add(account);
        }
        return kept;
    }

    private static string Clean(string? text, int max)
    {
        string clean = new([.. (text ?? "").Trim().Where(c => !char.IsControl(c))]);
        return clean.Length > max ? clean[..max] : clean;
    }
}

// A new message as a notice: who it is from as the title, the subject and the start of the text as the body. Messages from one
// sender merge into one conversation on the notices page, as a chat's do. Verification codes are found in it as in any notice.
public static class MailNotices
{
    // Mail older than this when BrimDeck learns of it (the PC slept, the network was away) is listed unread but not shown as arriving.
    public static readonly TimeSpan Late = TimeSpan.FromMinutes(10);

    public static NoticeInput Notice(MailAccount account, MailMessage message, DateTimeOffset now)
    {
        var provider = MailProviders.Find(account.Provider);
        string from = message.FromName.Length > 0 ? message.FromName : message.FromAddress.Length > 0 ? message.FromAddress : Loc.T("未知发件人", "Unknown sender");
        string subject = message.Subject.Length > 0 ? message.Subject : Loc.T("（无主题）", "(No subject)");
        return new()
        {
            Origin = NoticeOrigin.Internal, AppKey = account.AppKey, AppName = provider.Name, Context = account.Address,
            InstanceKey = $"mail:{account.Id:N}:{message.UidValidity}:{message.Uid}",
            Title = from, Body = message.Preview.Length > 0 ? subject + "\n" + message.Preview : subject,
            CreatedAt = message.Date, Timing = new NoticeTiming { Kind = NoticeTimeKind.Mail },
            Missed = now - message.Date > Late,
            Actions = provider.WebMail.Length > 0 ? [new NoticeAction("open-mail", Loc.T("打开邮箱", "Open mailbox"), NoticeActionKind.Open, provider.WebMail)] : []
        };
    }
}
