using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BrimDeck.Core;
using BrimDeck.Core.Mail;
using BrimDeck.Core.Notices;

// The mailboxes: the listed mail services and what an address makes of them, a new message as a notice, the Microsoft sign-in's
// parts, and the watch itself against a small IMAP server on this PC (what it reports, what it leaves alone, how it comes back).
internal static class MailTests
{
    public static async Task Run(Action<string, bool> check)
    {
        Providers(check);
        Notices(check);
        Microsoft(check);
        await Watching(check);
        await Failures(check);
    }

    private static void Providers(Action<string, bool> check)
    {
        check("Mail: an address names its service by its ending", MailProviders.ForAddress("someone@QQ.com")?.Id == MailProviders.Qq && MailProviders.ForAddress("a@foxmail.com")?.Id == MailProviders.Qq
            && MailProviders.ForAddress("a@126.com")?.Id == MailProviders.NetEase && MailProviders.ForAddress("a@hotmail.com")?.Id == MailProviders.Outlook && MailProviders.ForAddress("a@example.com") is null);
        check("Mail: what is not an address has no domain", MailProviders.Domain("someone") is null && MailProviders.Domain("@qq.com") is null && MailProviders.Domain("a b@qq.com") is null && MailProviders.Domain("a@qq.com") == "qq.com");
        var netease = MailProviders.Find(MailProviders.NetEase);
        check("Mail: NetEase and Sina have one server per domain", MailProviders.HostFor(netease, "a@126.com") == "imap.126.com" && MailProviders.HostFor(netease, "a@yeah.net") == "imap.yeah.net"
            && MailProviders.HostFor(netease, "a@163.com") == "imap.163.com" && MailProviders.HostFor(MailProviders.Find(MailProviders.Sina), "a@vip.sina.com") == "imap.vip.sina.com"
            && MailProviders.HostFor(MailProviders.Find(MailProviders.Qq), "a@foxmail.com") == "imap.qq.com");
        check("Mail: iCloud signs in with the part before the at sign, the others with the address",
            MailProviders.UserFor(MailProviders.Find(MailProviders.ICloud), "john@icloud.com") == "john" && MailProviders.UserFor(MailProviders.Find(MailProviders.Gmail), " john@gmail.com ") == "john@gmail.com");
        check("Mail: a server is a host name only", MailProviders.Host(" IMAP.Example.com ") == "imap.example.com" && MailProviders.Host("imap.example.com:993") is null
            && MailProviders.Host("https://imap.example.com") is null && MailProviders.Host("imap example") is null && MailProviders.Host("") is null);
        check("Mail: every service has its steps and the name of its secret", MailProviders.All.All(p => MailProviders.Steps(p).Length >= 2 && MailProviders.SecretName(p).Length > 0));
        check("Mail: every listed service but the user's own has a server and a website", MailProviders.All.Where(p => p.Id != MailProviders.Custom).All(p => MailProviders.Host(p.Host) == p.Host && p.WebMail.StartsWith("https://")));

        var account = new MailAccount { Provider = MailProviders.ICloud, Address = "john@icloud.com" };
        check("Mail: a listed mailbox is reached on its service's server over TLS", account.Endpoint() is { Host: "imap.mail.me.com", Port: 993, UserName: "john", Tls: true });
        account.UserName = "john@icloud.com";
        check("Mail: a sign-in name that was kept is used as it is", account.Endpoint()?.UserName == "john@icloud.com");
        check("Mail: a mailbox with its own server needs the server", new MailAccount { Provider = MailProviders.Custom, Address = "a@example.com" }.Endpoint() is null
            && new MailAccount { Provider = MailProviders.Custom, Address = "a@example.com", Host = "imap.example.com" }.Endpoint() is { Host: "imap.example.com", UserName: "a@example.com" });
        check("Mail: a mailbox with its own server signs in with the user name given, else the address",
            new MailAccount { Provider = MailProviders.Custom, Address = "a@example.com", Host = "imap.example.com", UserName = " e1234567 " }.Endpoint()?.UserName == "e1234567"
            && new MailAccount { Provider = MailProviders.Custom, Address = "a@example.com", Host = "imap.example.com", UserName = "  " }.Endpoint()?.UserName == "a@example.com");

        var settings = new DeckSettings();
        var id = Guid.NewGuid();
        settings.MailAccounts = [new() { Id = id, Provider = "qq", Address = " a@qq.com " }, new() { Id = id, Provider = "qq", Address = "b@qq.com" }, new() { Provider = "later-service", Address = "c@example.com", Host = "IMAP.example.com" },
            new() { Provider = "qq", Address = "" }];
        settings.Normalize();
        check("Mail: saved mailboxes are cleaned: one per id, an address each, an unknown service as the mailbox's own server",
            settings.MailAccounts.Count == 2 && settings.MailAccounts[0].Address == "a@qq.com" && settings.MailAccounts[1] is { Provider: MailProviders.Custom, Host: "imap.example.com" });
        settings.MailAccounts = [.. Enumerable.Range(0, 20).Select(i => new MailAccount { Provider = "qq", Address = $"a{i}@qq.com" })];
        settings.Normalize();
        check("Mail: any number of mailboxes is kept", settings.MailAccounts.Count == 20);
        var copy = settings.Copy();
        copy.MailAccounts[0].Address = "changed@qq.com";
        check("Mail: a copy of the settings has its own mailboxes", settings.MailAccounts[0].Address == "a0@qq.com");
        check("Mail: the mailboxes belong to the whole app, not to a profile", !Profiles.IsProfileKey(nameof(DeckSettings.MailAccounts)));
        var read = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(settings))!;
        check("Mail: the mailboxes are saved with the settings and no secret with them", read.MailAccounts.Count == 20 && read.MailAccounts[0].Id == settings.MailAccounts[0].Id
            && !JsonSerializer.Serialize(settings.MailAccounts[0]).Contains("assword", StringComparison.OrdinalIgnoreCase));
        check("Mail: a new code gives the mailbox another signature", new MailAccount { Address = "a@qq.com", Secret = Guid.NewGuid() }.Signature != new MailAccount { Address = "a@qq.com", Secret = Guid.NewGuid() }.Signature);
    }

    private static void Notices(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var account = new MailAccount { Provider = MailProviders.Qq, Address = "me@qq.com" };
        var notice = MailNotices.Notice(account, new MailMessage(7, 42, "GitHub", "noreply@github.com", "Your code", "Verification code: 482913", now.AddSeconds(-20)), now);
        check("Mail notice: the sender is the title, the subject and the text the body", notice is { Title: "GitHub", Body: "Your code\nVerification code: 482913", AppName: "QQ 邮箱" or "QQ Mail", Context: "me@qq.com" });
        check("Mail notice: it is BrimDeck's own, of the mail kind, under the mailbox's key", notice.Origin == NoticeOrigin.Internal && notice.Timing?.Kind == NoticeTimeKind.Mail
            && notice.AppKey == account.AppKey && notice.AppKey.StartsWith(NoticeKeys.InternalPrefix) && notice.InstanceKey == $"mail:{account.Id:N}:7:42");
        check("Mail notice: its button opens the service's website", notice.Actions is [{ Kind: NoticeActionKind.Open, Value: "https://wx.mail.qq.com/" }] && !notice.Missed);
        check("Mail notice: the code in it is found as in any notice", VerificationCode.Find(notice.Title, notice.Body) is not null);
        var bare = MailNotices.Notice(new MailAccount { Provider = MailProviders.Custom, Address = "me@example.com", Host = "imap.example.com" }, new MailMessage(1, 1, "", "a@b.c", "", "", now.AddHours(-2)), now);
        check("Mail notice: without a name the address stands for the sender, and an empty subject is said", bare.Title == "a@b.c" && bare.Body.Length > 0 && bare.Actions.Count == 0);
        check("Mail notice: mail that is old when it is learned of is only listed", bare.Missed);

        var hub = new NoticeHub(_ => null, _ => true);
        var outcome = hub.Receive(notice, now);
        check("Mail notice: the hub takes it and shows it", outcome is { Receipt: NoticeReceipt.Accepted, Present: true } && outcome.Notice?.TimeKind == NoticeTimeKind.Mail);
        check("Mail notice: the same message twice is one notice", hub.Receive(notice, now).Receipt != NoticeReceipt.Accepted && hub.Notices.Count == 1);
        check("Mail notice: late mail is listed without showing", hub.Receive(bare, now) is { Receipt: NoticeReceipt.Accepted, Present: false });

        check("Mail preview: blank lines and runs of spaces close up", MailWatch.Preview("  Hello   there \r\n\r\n\r\n  second\tline  ") == "Hello there\nsecond line" && MailWatch.Preview(null) == "" && MailWatch.Preview(" \n ") == "");
        check("Mail preview: a long text is cut", MailWatch.Preview(new string('a', 1000)).Length == MailWatch.MaxPreview + 1);
    }

    private static void Microsoft(Action<string, bool> check)
    {
        // RFC 7636, appendix B.
        check("Microsoft sign-in: the challenge is the verifier's SHA-256 in base64url", MicrosoftMail.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
        check("Microsoft sign-in: a verifier is 43 url-safe characters", MicrosoftMail.Verifier() is { Length: 43 } verifier && verifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        string url = MicrosoftMail.AuthorizeUrl("client-1", "http://localhost:5000", "st", "ch", "me@outlook.com");
        check("Microsoft sign-in: the page asks for IMAP and a refresh token, with PKCE, back to this PC",
            url.StartsWith("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?") && url.Contains("client_id=client-1") && url.Contains("response_type=code")
            && url.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A5000") && url.Contains("scope=openid%20email%20profile%20https%3A%2F%2Foutlook.office.com%2FIMAP.AccessAsUser.All%20offline_access")
            && url.Contains("code_challenge=ch") && url.Contains("code_challenge_method=S256") && url.Contains("state=st") && url.Contains("login_hint=me%40outlook.com"));
        check("Microsoft sign-in: adding a mailbox offers no account on Microsoft's page",
            !MicrosoftMail.AuthorizeUrl("client-1", "http://localhost:5000", "st", "ch", null).Contains("login_hint"));
        static string IdToken(string claims) => "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";
        string ours = MicrosoftMail.ClientId;
        check("Microsoft sign-in: the mailbox is the account signed in to, by its email, else its sign-in name when that is an address",
            MicrosoftMail.Address(IdToken($$"""{"aud":"{{ours}}","email":"real@outlook.com","preferred_username":"+8613800000000"}"""), ours) == "real@outlook.com"
            && MicrosoftMail.Address(IdToken($$"""{"aud":"{{ours}}","preferred_username":"me@contoso.com"}"""), ours) == "me@contoso.com"
            && MicrosoftMail.Address(IdToken($$"""{"aud":"{{ours}}","preferred_username":"+8613800000000"}"""), ours) is null);
        check("Microsoft sign-in: an ID token for another app or a broken one gives no mailbox",
            MicrosoftMail.Address(IdToken("""{"aud":"other","email":"real@outlook.com"}"""), ours) is null && MicrosoftMail.Address("not-a-token", ours) is null
            && MicrosoftMail.Address("a.!!!.b", ours) is null);
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var tokens = MicrosoftMail.ReadTokens("""{"access_token":"at","refresh_token":"rt","expires_in":3599}""", null, now);
        check("Microsoft sign-in: the tokens are read with their end", tokens == new MicrosoftTokens("at", "rt", now.AddSeconds(3599)));
        check("Microsoft sign-in: a sign-in's tokens carry the mailbox of the account",
            MicrosoftMail.ReadTokens($$"""{"access_token":"at","refresh_token":"rt","expires_in":3599,"id_token":"{{IdToken($$"""{"aud":"{{ours}}","email":"real@outlook.com"}""")}}"}""", null, now).Address == "real@outlook.com");
        check("Microsoft sign-in: a refresh that gives no new refresh token keeps the old one", MicrosoftMail.ReadTokens("""{"access_token":"at2","expires_in":60}""", "rt", now).Refresh == "rt");
        bool refused = false, broken = false;
        try { MicrosoftMail.ReadTokens("""{"error":"invalid_grant","error_description":"AADSTS70000: expired"}""", "rt", now); } catch (MailSecretException ex) { refused = ex.Message.Contains("expired"); }
        try { MicrosoftMail.ReadTokens("""{"access_token":""}""", null, now); } catch (InvalidDataException) { broken = true; }
        check("Microsoft sign-in: a refused refresh asks for a new sign-in, an answer without tokens is an error", refused && broken);
        check("Microsoft sign-in: the code and the state are read from the request the browser makes",
            MicrosoftMail.Query("GET /?code=M.C5%2Babc&state=st HTTP/1.1\r\nHost: localhost\r\n\r\n") is { } query && query["code"] == "M.C5+abc" && query["state"] == "st"
            && MicrosoftMail.Query("GET /favicon.ico HTTP/1.1\r\n\r\n") is null && MicrosoftMail.Query("POST /?code=1 HTTP/1.1\r\n\r\n") is null);
        check("Microsoft sign-in: the page the browser comes back to says how it went and clears the code from the address bar",
            MicrosoftMail.Page(true) is var done && done.Contains("m9 12 2 2 4-4") && done.Contains("history.replaceState") && done.Contains("prefers-color-scheme:dark")
            && MicrosoftMail.Page(false) is var failed && failed.Contains("m15 9-6 6") && !failed.Contains("m9 12 2 2 4-4"));
    }

    private static readonly MailPace Fast = new() { Idle = TimeSpan.FromMilliseconds(300), Poll = TimeSpan.FromMilliseconds(150), Timeout = TimeSpan.FromSeconds(5),
        Retry = [TimeSpan.FromMilliseconds(100)] };

    private static async Task<bool> Until(Func<bool> done, int milliseconds = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end) { if (done()) return true; await Task.Delay(20); }
        return done();
    }

    private sealed class Seen
    {
        public List<MailMessage> Mail { get; } = [];
        public List<MailStatus> States { get; } = [];
        public MailWatch Watch(FakeImap server, string password = "secret", MailPace? pace = null)
            => new(new MailEndpoint("127.0.0.1", server.Port, "me@example.com", Tls: false), _ => Task.FromResult(new MailSecret(password)),
                message => { lock (Mail) Mail.Add(message); }, status => { lock (States) States.Add(status); }, pace ?? Fast);
        public int Count { get { lock (Mail) return Mail.Count; } }
        public MailState? State { get { lock (States) return States.Count > 0 ? States[^1].State : null; } }
        public bool Was(MailState state) { lock (States) return States.Any(s => s.State == state); }
    }

    private static async Task Watching(Action<string, bool> check)
    {
        using (var server = new FakeImap { RequiresId = true })
        {
            server.Add("Old One", "old@example.com", "Before", "was here already");
            server.Add("Old Two", "old@example.com", "Before too", "was here already");
            var seen = new Seen();
            using var watch = seen.Watch(server);
            watch.Start();
            check("Mail watch: it signs in and watches", await Until(() => seen.State == MailState.Watching));
            check("Mail watch: the client names itself before opening the inbox (NetEase refuses otherwise)", server.Log.Any(line => line.StartsWith("ID ", StringComparison.Ordinal)));
            await Task.Delay(500);
            check("Mail watch: mail already in the inbox is not reported", seen.Count == 0);

            server.Add("张三", "zhang@example.com", "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("你的验证码")) + "?=", "验证码：482913，10 分钟内有效。", push: true);
            check("Mail watch: mail pushed while it idles is reported at once", await Until(() => seen.Count == 1, 3000));
            MailMessage first; lock (seen.Mail) first = seen.Mail[0];
            check("Mail watch: the sender, the decoded subject and the start of the text come with it",
                first is { FromName: "张三", FromAddress: "zhang@example.com", Subject: "你的验证码", UidValidity: FakeImap.Validity, Uid: 3 } && first.Preview.Contains("482913"));

            server.Add("Quiet", "quiet@example.com", "No push", "the server said nothing", push: false);
            check("Mail watch: mail a server does not push is found by the next look", await Until(() => seen.Count == 2));
            server.Add("Read", "read@example.com", "Read elsewhere", "already read", push: true, seen: true);
            server.Add("After", "after@example.com", "Unread", "new", push: true);
            check("Mail watch: mail already read is passed over", await Until(() => seen.Count == 3) && seen.Mail[^1].FromName == "After");

            server.Drop();
            server.Add("Meanwhile", "m@example.com", "While away", "arrived with no connection");
            check("Mail watch: a dropped connection is made again and what arrived meanwhile is reported", await Until(() => seen.Count == 4) && seen.Mail[^1].FromName == "Meanwhile" && seen.Was(MailState.Retrying));
            check("Mail watch: it watches again after coming back", await Until(() => seen.State == MailState.Watching));
            watch.Dispose();
            await Task.Delay(200);
            check("Mail watch: the inbox is only examined, bodies only peeked at, nothing stored",
                server.Log.Any(line => line.StartsWith("EXAMINE", StringComparison.Ordinal)) && !server.Log.Any(line => line.StartsWith("SELECT", StringComparison.Ordinal) || line.StartsWith("STORE", StringComparison.Ordinal) || line.Contains("UID STORE"))
                && server.Log.Where(line => line.Contains("BODY", StringComparison.Ordinal)).All(line => !Regex.IsMatch(line, @"BODY\[")));
            check("Mail watch: bodies were asked for in part", server.Log.Any(line => line.Contains("BODY.PEEK[", StringComparison.Ordinal)));
        }

        using (var server = new FakeImap { Idle = false })
        {
            var seen = new Seen();
            using var watch = seen.Watch(server);
            watch.Start();
            await Until(() => seen.State == MailState.Watching);
            server.Add("Polled", "p@example.com", "No idle", "found by asking");
            check("Mail watch: a server without IDLE is asked in turns", await Until(() => seen.Count == 1) && !server.Log.Contains("IDLE"));
        }

        using (var server = new FakeImap())
        {
            var seen = new Seen();
            using var watch = seen.Watch(server);
            watch.Start();
            await Until(() => seen.State == MailState.Watching);
            for (int i = 0; i < MailWatch.MaxAtOnce + 15; i++) server.Add("Sender " + i, "s@example.com", "Mail " + i, "text");
            server.Push();
            check("Mail watch: of a flood only the latest are reported", await Until(() => seen.Count >= MailWatch.MaxAtOnce) && await Task.Delay(400).ContinueWith(_ => true)
                && seen.Count == MailWatch.MaxAtOnce && seen.Mail[^1].Subject == "Mail " + (MailWatch.MaxAtOnce + 14));
        }
    }

    private static async Task Failures(Action<string, bool> check)
    {
        using (var server = new FakeImap())
        {
            var seen = new Seen();
            using var watch = seen.Watch(server, password: "wrong");
            watch.Start();
            check("Mail watch: a refused sign-in is said", await Until(() => seen.State == MailState.Refused));
            await Task.Delay(600);
            check("Mail watch: after a refusal nothing is tried again by itself", server.Log.Count(line => line.StartsWith("LOGIN", StringComparison.Ordinal)) == 1);
            watch.Nudge(refused: false);
            await Task.Delay(300);
            check("Mail watch: waking the PC does not try a refused mailbox", server.Log.Count(line => line.StartsWith("LOGIN", StringComparison.Ordinal)) == 1);
            watch.Nudge();
            check("Mail watch: the user's retry tries once more", await Until(() => server.Log.Count(line => line.StartsWith("LOGIN", StringComparison.Ordinal)) == 2));
        }

        using (var server = new FakeImap { RequiresId = true })
        {
            var endpoint = new MailEndpoint("127.0.0.1", server.Port, "me@example.com", Tls: false);
            check("Mail check: a mailbox that lets the user in passes", await MailWatch.CheckAsync(endpoint, new MailSecret("secret"), CancellationToken.None, Fast) is null);
            check("Mail check: a wrong code is a refusal, with the server's words", await MailWatch.CheckAsync(endpoint, new MailSecret("nope"), CancellationToken.None, Fast) is { Refused: true, Detail.Length: > 0 });
            int port = server.Port;
            server.Dispose();
            check("Mail check: a server that does not answer is not a refusal", await MailWatch.CheckAsync(endpoint with { Port = port }, new MailSecret("secret"), CancellationToken.None, Fast) is { Refused: false });
        }

        {
            var seen = new Seen();
            using var missing = new MailWatch(new MailEndpoint("127.0.0.1", 9, "me@example.com", Tls: false), _ => throw new MailSecretException("gone"), _ => { }, status => { lock (seen.States) seen.States.Add(status); }, Fast);
            missing.Start();
            check("Mail watch: a mailbox whose saved code is gone waits for the user", await Until(() => seen.State is MailState.Refused or MailState.Retrying) );
        }
    }

    // A small IMAP server on this PC, without TLS: one inbox, the commands MailKit sends to watch it, and a log of each command
    // (without its tag) to see what the client did.
    private sealed class FakeImap : IDisposable
    {
        public const uint Validity = 7;
        private sealed record Message(uint Uid, string FromName, string FromAddress, string Subject, string Text, bool Seen);

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Message> _messages = [];
        private readonly object _gate = new();
        private TcpClient? _current;
        // Completed by each Push; _pushed is the count an idling client is to be told of.
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pushed;
        private uint _nextUid = 1;

        public bool Idle { get; init; } = true;
        // As NetEase: the inbox opens only for a client that has sent ID.
        public bool RequiresId { get; init; }
        public int Port { get; }
        public List<string> Log { get; } = [];

        public FakeImap()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        public void Add(string fromName, string fromAddress, string subject, string text, bool push = false, bool seen = false)
        {
            lock (_gate) _messages.Add(new(_nextUid++, fromName, fromAddress, subject, text, seen));
            if (push) Push();
        }

        // Tells an idling client that the count changed.
        public void Push()
        {
            TaskCompletionSource signal;
            lock (_gate) { _pushed = _messages.Count; signal = _signal; _signal = new(TaskCreationOptions.RunContinuationsAsynchronously); }
            signal.TrySetResult();
        }

        // Cuts the connection, as a network that went away does.
        public void Drop() { lock (_gate) { _current?.Close(); _current = null; } }

        public void Dispose() { _stop.Cancel(); _listener.Stop(); Drop(); }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    lock (_gate) _current = client;
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        private void Note(string command) { lock (Log) Log.Add(command); }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                async Task Send(string text) => await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
                bool identified = false, authenticated = false;
                int told = 0;
                await Send("* OK IMAP4rev1 ready\r\n");
                while (await reader.ReadLineAsync() is { } line)
                {
                    int space = line.IndexOf(' ');
                    if (space < 0) continue;
                    string tag = line[..space], command = line[(space + 1)..];
                    Note(command);
                    string verb = command.Split(' ')[0].ToUpperInvariant();
                    if (verb == "UID") verb = "UID " + command.Split(' ')[1].ToUpperInvariant();
                    List<Message> messages; lock (_gate) messages = [.. _messages];
                    switch (verb)
                    {
                        case "CAPABILITY": await Send($"* CAPABILITY IMAP4rev1 ID{(Idle ? " IDLE" : "")}\r\n{tag} OK done\r\n"); break;
                        case "LOGIN":
                            if (command.Contains("secret", StringComparison.Ordinal)) { authenticated = true; await Send($"{tag} OK signed in\r\n"); }
                            else await Send($"{tag} NO [AUTHENTICATIONFAILED] Login fail. The code is incorrect.\r\n");
                            break;
                        case "ID": identified = true; await Send($"* ID (\"name\" \"fake\")\r\n{tag} OK done\r\n"); break;
                        case "LIST":
                            await Send((command.Contains("INBOX", StringComparison.OrdinalIgnoreCase) ? "* LIST () \"/\" INBOX\r\n" : "* LIST (\\Noselect) \"/\" \"\"\r\n") + $"{tag} OK done\r\n");
                            break;
                        case "EXAMINE" or "SELECT":
                            if (!authenticated) { await Send($"{tag} NO not signed in\r\n"); break; }
                            if (RequiresId && !identified) { await Send($"{tag} NO SELECT Unsafe Login. Please contact kefu@188.com for help\r\n"); break; }
                            uint next; lock (_gate) next = _nextUid;
                            told = messages.Count;
                            await Send($"* {messages.Count} EXISTS\r\n* 0 RECENT\r\n* FLAGS (\\Seen)\r\n* OK [UIDVALIDITY {Validity}] valid\r\n* OK [UIDNEXT {next}] next\r\n{tag} OK [READ-ONLY] opened\r\n");
                            break;
                        case "UID SEARCH":
                        {
                            var range = Regex.Match(command, @"UID (\d+):\*");
                            uint from = range.Success ? uint.Parse(range.Groups[1].Value) : 1;
                            // "n:*" names the highest message even when it is below n.
                            var found = messages.Where(m => m.Uid >= from).Select(m => m.Uid).ToList();
                            if (found.Count == 0 && messages.Count > 0) found.Add(messages[^1].Uid);
                            await Send($"* SEARCH {string.Join(' ', found)}\r\n{tag} OK done\r\n");
                            break;
                        }
                        case "UID FETCH" or "FETCH":
                        {
                            var parts = command.Split(' ');
                            string set = parts[verb == "FETCH" ? 1 : 2];
                            var reply = new StringBuilder();
                            foreach (var (message, index) in messages.Select((m, i) => (m, i)))
                            {
                                uint number = verb == "FETCH" ? (uint)index + 1 : message.Uid;
                                if (!InSet(set, number, verb == "FETCH" ? (uint)messages.Count : messages[^1].Uid)) continue;
                                var items = new List<string> { $"UID {message.Uid}" };
                                if (command.Contains("FLAGS")) items.Add($"FLAGS ({(message.Seen ? "\\Seen" : "")})");
                                if (command.Contains("INTERNALDATE")) items.Add($"INTERNALDATE \"{DateTimeOffset.UtcNow:dd-MMM-yyyy HH:mm:ss} +0000\"");
                                if (command.Contains("ENVELOPE"))
                                {
                                    string name = "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(message.FromName)) + "?=";
                                    string[] address = message.FromAddress.Split('@');
                                    string from = $"((\"{name}\" NIL \"{address[0]}\" \"{address[1]}\"))";
                                    items.Add($"ENVELOPE (\"{DateTimeOffset.UtcNow:r}\" \"{message.Subject}\" {from} NIL NIL ((NIL NIL \"me\" \"example.com\")) NIL NIL NIL \"<{message.Uid}@example.com>\")");
                                }
                                byte[] text = Encoding.UTF8.GetBytes(message.Text);
                                if (command.Contains("BODYSTRUCTURE")) items.Add($"BODYSTRUCTURE (\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"8BIT\" {text.Length} 1)");
                                reply.Append($"* {index + 1} FETCH ({string.Join(' ', items)}");
                                var body = Regex.Match(command, @"BODY\.PEEK\[([^\]]*)\]<(\d+)\.(\d+)>");
                                if (body.Success) reply.Append($" BODY[{body.Groups[1].Value}]<{body.Groups[2].Value}> {{{text.Length}}}\r\n").Append(message.Text);
                                reply.Append(")\r\n");
                            }
                            await Send(reply + $"{tag} OK done\r\n");
                            break;
                        }
                        case "NOOP":
                            if (messages.Count != told) { told = messages.Count; await Send($"* {told} EXISTS\r\n"); }
                            await Send($"{tag} OK done\r\n");
                            break;
                        case "IDLE":
                        {
                            await Send("+ idling\r\n");
                            var done = reader.ReadLineAsync();
                            while (true)
                            {
                                Task signal; int pushed;
                                lock (_gate) { signal = _signal.Task; pushed = _pushed; }
                                // Only what was pushed is said while idle; the rest waits for the client to ask (NOOP).
                                if (pushed > told) { told = pushed; await Send($"* {told} EXISTS\r\n"); }
                                if (await Task.WhenAny(done, signal) == done) break;
                            }
                            if (await done is null) return;
                            await Send($"{tag} OK idle ended\r\n");
                            break;
                        }
                        case "LOGOUT": await Send($"* BYE\r\n{tag} OK bye\r\n"); return;
                        default: await Send($"{tag} BAD unknown\r\n"); break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
            finally { client.Close(); }
        }

        // "3", "3:5", "3:*", "3,5,7:9".
        private static bool InSet(string set, uint number, uint highest) => set.Split(',').Any(part =>
        {
            var ends = part.Split(':');
            uint low = ends[0] == "*" ? highest : uint.Parse(ends[0]), high = ends.Length == 1 ? low : ends[1] == "*" ? highest : uint.Parse(ends[1]);
            return number >= Math.Min(low, high) && number <= Math.Max(low, high);
        });
    }
}
