namespace BrimDeck.Core.Mail;

// How a mailbox is signed in to. Code: an authorization code or app password the user makes on the provider's website and types
// here. Microsoft: signing in to the Microsoft account in the browser (Outlook.com and Microsoft 365 take nothing else).
public enum MailSignIn { Code, Microsoft }

// A mail service BrimDeck knows the IMAP server of. Host is its server; with HostByDomain the server is "imap." and the address'
// domain (NetEase and Sina have one per domain), and Host is the one for a domain not listed. Domains are the address endings
// that belong to it. WebMail is opened by a mail notice's button; CodePage is where its code is made (the website itself where
// no page leads there). LocalUser: the sign-in name is the part of the address before "@" (iCloud).
public sealed record MailProvider(string Id, string NameZh, string NameEn, string Host, MailSignIn SignIn, string[] Domains, string WebMail, string CodePage,
    bool HostByDomain = false, bool LocalUser = false)
{
    public string Name => Loc.T(NameZh, NameEn);
}

// The mail services offered when a mailbox is added, and what the window that adds one says for each. Hosts were read from the
// providers' help pages and answered on port 993 (2026-11-24).
public static class MailProviders
{
    // Every mailbox is reached by IMAP over TLS on this port.
    public const int Port = 993;
    public const int MaxAccounts = 8;
    public const string Custom = "imap", Qq = "qq", NetEase = "netease", Exmail = "exmail", ICloud = "icloud", Gmail = "gmail", Sina = "sina", Outlook = "outlook";

    public static IReadOnlyList<MailProvider> All { get; } =
    [
        new(Qq, "QQ 邮箱", "QQ Mail", "imap.qq.com", MailSignIn.Code, ["qq.com", "foxmail.com", "vip.qq.com"], "https://wx.mail.qq.com/", "https://wx.mail.qq.com/account"),
        new(NetEase, "网易邮箱", "NetEase Mail", "imap.163.com", MailSignIn.Code, ["163.com", "126.com", "yeah.net"], "https://mail.163.com/", "https://mail.163.com/", HostByDomain: true),
        new(Exmail, "腾讯企业邮", "Tencent Exmail", "imap.exmail.qq.com", MailSignIn.Code, [], "https://exmail.qq.com/", "https://exmail.qq.com/"),
        new(Sina, "新浪邮箱", "Sina Mail", "imap.sina.com", MailSignIn.Code, ["sina.com", "sina.cn", "vip.sina.com", "vip.sina.cn"], "https://mail.sina.com.cn/", "https://mail.sina.com.cn/", HostByDomain: true),
        new(ICloud, "iCloud 邮件", "iCloud Mail", "imap.mail.me.com", MailSignIn.Code, ["icloud.com", "me.com", "mac.com"], "https://www.icloud.com/mail", "https://account.apple.com/", LocalUser: true),
        new(Gmail, "Gmail", "Gmail", "imap.gmail.com", MailSignIn.Code, ["gmail.com", "googlemail.com"], "https://mail.google.com/", "https://myaccount.google.com/apppasswords"),
        new(Outlook, "Outlook", "Outlook", "outlook.office365.com", MailSignIn.Microsoft, ["outlook.com", "hotmail.com", "live.com", "msn.com"], "https://outlook.live.com/mail/", ""),
        new(Custom, "其他邮箱", "Other mailbox", "", MailSignIn.Code, [], "", "")
    ];

    // The provider of that id; the mailbox with its own server for an id not known.
    public static MailProvider Find(string? id) => All.FirstOrDefault(provider => provider.Id == id) ?? All[^1];

    // The provider an address belongs to by its ending, if a listed one.
    public static MailProvider? ForAddress(string address)
        => Domain(address) is { } domain ? All.FirstOrDefault(provider => provider.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase)) : null;

    // The part after "@", in lower case; null for what is not an address.
    public static string? Domain(string address)
    {
        address = address.Trim();
        int at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1 && address.IndexOf(' ') < 0 && Host(address[(at + 1)..]) is { } domain ? domain : null;
    }

    // The provider's server for that address.
    public static string HostFor(MailProvider provider, string address)
        => provider.HostByDomain && Domain(address) is { } domain && provider.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase) ? "imap." + domain : provider.Host;

    // The name the provider's server signs that address in with.
    public static string UserFor(MailProvider provider, string address)
        => provider.LocalUser && address.Trim().LastIndexOf('@') is > 0 and var at ? address.Trim()[..at] : address.Trim();

    // A server's name as typed, in lower case: letters, digits, "-" and "." only, no port, no path. Null for anything else.
    public static string? Host(string? text)
    {
        string host = (text ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        return host.Length is > 0 and <= 253 && Uri.CheckHostName(host) == UriHostNameType.Dns && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') ? host : null;
    }

    // What the provider calls the secret a mail program signs in with.
    public static string SecretName(MailProvider provider) => provider.Id switch
    {
        ICloud => Loc.T("App 专用密码", "App-specific password"),
        Gmail => Loc.T("应用专用密码", "App password"),
        Exmail => Loc.T("密码或客户端专用密码", "Password or client password"),
        Custom => Loc.T("密码或授权码", "Password or authorization code"),
        _ => Loc.T("授权码", "Authorization code")
    };

    // What to do on the provider's website before signing in here, in the website's own words.
    public static string[] Steps(MailProvider provider) => provider.Id switch
    {
        Qq =>
        [
            Loc.T("在 QQ 邮箱网页版进入“设置 › 账号与安全”。", "On the QQ Mail website, open Settings › Account & Security (设置 › 账号与安全)."),
            Loc.T("开启 IMAP/SMTP 服务，按页面提示验证身份后生成授权码。", "Turn on the IMAP/SMTP service, verify as the page asks, and generate an authorization code (授权码)."),
            Loc.T("把 16 位授权码填在下面。", "Enter the 16-character code below.")
        ],
        NetEase =>
        [
            Loc.T("在网易邮箱网页版点击页面上方的“设置”，选择“POP3/SMTP/IMAP”。", "On the NetEase Mail website, choose 设置 at the top, then POP3/SMTP/IMAP."),
            Loc.T("开启“IMAP/SMTP 服务”，完成短信验证后页面显示授权码；已经开启的，点“新增授权密码”。", "Turn on the IMAP/SMTP service; after the text-message check the page shows the code. If it is already on, choose 新增授权密码."),
            Loc.T("授权码只显示一次，把它填在下面。", "The code is shown once. Enter it below.")
        ],
        Exmail =>
        [
            Loc.T("在企业邮箱网页版进入“设置 › 收发信设置”，开启 IMAP 服务。", "On the Exmail website, open 设置 › 收发信设置 and turn on the IMAP service."),
            Loc.T("开启了“安全登录”的账号：在“设置 › 邮箱绑定”的“客户端专用密码”处点“生成新密码”，填这个密码。其他账号填邮箱密码。",
                "With 安全登录 on: under 设置 › 邮箱绑定, choose 生成新密码 beside 客户端专用密码 and enter that password. Otherwise enter the mailbox password.")
        ],
        Sina =>
        [
            Loc.T("在新浪邮箱网页版进入“设置”，找到“客户端pop/imap/smtp”。", "On the Sina Mail website, open 设置 and find 客户端pop/imap/smtp."),
            Loc.T("开启授权码和 IMAP4/SMTP 服务，完成短信验证后得到授权码。", "Turn on both the authorization code and the IMAP4/SMTP service; the text-message check gives you the code."),
            Loc.T("授权码只显示一次，把它填在下面。", "The code is shown once. Enter it below.")
        ],
        ICloud =>
        [
            Loc.T("打开 account.apple.com 并登录。Apple 账户需要已开启双重认证。", "Sign in at account.apple.com. The Apple Account needs two-factor authentication."),
            Loc.T("在“登录和安全”中选择“App 专用密码”，生成一个。", "Under Sign-In and Security, choose App-Specific Passwords and generate one."),
            Loc.T("把生成的密码填在下面。", "Enter the generated password below.")
        ],
        Gmail =>
        [
            Loc.T("Google 账号需要已开启两步验证。", "The Google Account needs 2-Step Verification."),
            Loc.T("打开“应用专用密码”页面，创建一个，得到 16 位密码。", "Open the App passwords page and create one; it has 16 characters."),
            Loc.T("把这个密码填在下面。工作或学校账号可能没有这一项。", "Enter it below. Work or school accounts may not offer app passwords.")
        ],
        Outlook =>
        [
            Loc.T("在 Outlook 网页版的“设置 › 邮件 › 转发和 IMAP”中打开“允许设备和应用使用 IMAP”。", "On the Outlook website, under Settings › Mail › Forwarding and IMAP, turn on Let devices and apps use IMAP."),
            Loc.T("填写邮箱地址，点“在浏览器中登录”，在打开的页面登录 Microsoft 账户并同意授权。", "Enter the address, choose Sign in in the browser, then sign in to the Microsoft account on the page that opens and allow access.")
        ],
        _ =>
        [
            Loc.T("在邮箱网页版的设置中开启 IMAP 服务，需要授权码的一并生成。", "Turn on IMAP in the mailbox's web settings; where the service asks for an authorization code, generate one."),
            Loc.T("IMAP 服务器填主机名，例如 imap.example.com。只支持 993 端口的加密连接。", "Enter the IMAP server (for example imap.example.com). Only encrypted connections on port 993 are supported.")
        ]
    };
}
