using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core.Mail;

// What a Microsoft sign-in gives: a short-lived access token for IMAP, and the refresh token that gets the next one. Only the
// refresh token is saved (encrypted, as an authorization code is). Address is the mailbox of the account the user signed in to
// (from the ID token of a sign-in; null on a refresh).
public sealed record MicrosoftTokens(string Access, string Refresh, DateTimeOffset Expires, string? Address = null);

// Signing in to Outlook.com, Hotmail and Microsoft 365 mailboxes, which take neither a password nor an app password over IMAP:
// the user signs in on Microsoft's page in the default browser, and the page hands a one-time code back to a local address
// (OAuth 2.0 authorization code flow with PKCE, as a public client). The access token then signs in to IMAP (XOAUTH2).
public static class MicrosoftMail
{
    // BrimDeck's application (client) id in Microsoft Entra: a public client for "accounts in any organizational directory and
    // personal Microsoft accounts", with the redirect address http://localhost under "Mobile and desktop applications".
    public const string ClientId = "563872d9-3787-4802-8128-0977c6be7530";

    private const string Authority = "https://login.microsoftonline.com/common/oauth2/v2.0";
    public const string Scope = "https://outlook.office.com/IMAP.AccessAsUser.All offline_access";
    // A sign-in also asks who signed in: the user may sign in to any account on Microsoft's page, and the mailbox connected is
    // that account's, not an address typed beforehand.
    public const string SignInScope = "openid email profile " + Scope;

    public static string Verifier() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // The page the browser opens. address, when given (signing a mailbox in again), is offered as the account to sign in with.
    public static string AuthorizeUrl(string clientId, string redirect, string state, string challenge, string? address)
        => Authority + "/authorize?" + Form(("client_id", clientId), ("response_type", "code"), ("redirect_uri", redirect), ("response_mode", "query"), ("scope", SignInScope),
            ("state", state), ("code_challenge", challenge), ("code_challenge_method", "S256"), ("login_hint", address ?? ""), ("prompt", "select_account"));

    private static string Form(params (string Key, string Value)[] fields)
        => string.Join("&", fields.Where(field => field.Value.Length > 0).Select(field => field.Key + "=" + Uri.EscapeDataString(field.Value)));

    // The mailbox of the account an ID token is for: its email claim, else its sign-in name when that is an address (a personal
    // account may sign in with a phone number). Null when neither is, or the token is not BrimDeck's. The token comes straight
    // from Microsoft's token endpoint over TLS and only says which mailbox to open (the mail server checks the access token),
    // so its signature is not checked.
    public static string? Address(string idToken, string clientId)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')));
            var root = claims.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("aud", out var aud) || aud.GetString() != clientId) return null;
            foreach (var name in new[] { "email", "preferred_username" })
                if (root.TryGetProperty(name, out var claim) && claim.ValueKind == JsonValueKind.String && claim.GetString()!.Trim() is var address && MailProviders.Domain(address) is not null)
                    return address;
            return null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    // The token endpoint's answer. An error there (the sign-in was withdrawn, the refresh token expired) is a MailSecretException:
    // the user has to sign in again.
    public static MicrosoftTokens ReadTokens(string json, string? previousRefresh, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Unexpected token response.");
        if (root.TryGetProperty("error", out var error))
            throw new MailSecretException((root.TryGetProperty("error_description", out var said) ? said.GetString() : null) ?? error.GetString() ?? "error");
        string access = root.TryGetProperty("access_token", out var token) ? token.GetString() ?? "" : "";
        // A refresh may keep the refresh token it was made with.
        string refresh = root.TryGetProperty("refresh_token", out var next) && next.GetString() is { Length: > 0 } given ? given : previousRefresh ?? "";
        if (access.Length == 0 || refresh.Length == 0) throw new InvalidDataException("The token response has no token.");
        int seconds = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var value) ? value : 3600;
        string? address = root.TryGetProperty("id_token", out var id) && id.ValueKind == JsonValueKind.String ? Address(id.GetString()!, ClientId) : null;
        return new(access, refresh, now.AddSeconds(seconds), address);
    }

    private static async Task<MicrosoftTokens> TokenAsync(HttpClient http, string? previousRefresh, CancellationToken stop, params (string Key, string Value)[] fields)
    {
        using var content = new StringContent(Form(fields), Encoding.ASCII, "application/x-www-form-urlencoded");
        using var response = await http.PostAsync(Authority + "/token", content, stop).ConfigureAwait(false);
        return ReadTokens(await response.Content.ReadAsStringAsync(stop).ConfigureAwait(false), previousRefresh, DateTimeOffset.UtcNow);
    }

    public static Task<MicrosoftTokens> RefreshAsync(HttpClient http, string refreshToken, CancellationToken stop)
        => TokenAsync(http, refreshToken, stop, ("client_id", ClientId), ("grant_type", "refresh_token"), ("refresh_token", refreshToken), ("scope", Scope));

    // Opens Microsoft's sign-in page in the browser and waits for it to come back to a local address. Null when the browser
    // could not be opened or the user turned the request down; cancelling stop gives the sign-in up.
    public static async Task<MicrosoftTokens?> SignInAsync(HttpClient http, string? address, Func<string, bool> open, CancellationToken stop)
    {
        // Microsoft accepts any port for http://localhost; the listener takes a free one.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string redirect = $"http://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}";
        string verifier = Verifier(), state = Base64Url(RandomNumberGenerator.GetBytes(16));
        if (!open(AuthorizeUrl(ClientId, redirect, state, Challenge(verifier), address))) return null;
        while (true)
        {
            using var browser = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
            var query = await ReadQueryAsync(browser, stop).ConfigureAwait(false);
            // Anything else that reaches the port (a favicon request, another program) is answered and waited past.
            if (query is null || !query.TryGetValue("state", out var returned) || returned != state) { await ReplyAsync(browser, null, stop).ConfigureAwait(false); continue; }
            bool allowed = query.TryGetValue("code", out var code) && code.Length > 0;
            await ReplyAsync(browser, Page(allowed), stop).ConfigureAwait(false);
            if (!allowed) return null;
            return await TokenAsync(http, null, stop, ("client_id", ClientId), ("grant_type", "authorization_code"), ("code", code!), ("redirect_uri", redirect),
                ("code_verifier", verifier), ("scope", SignInScope)).ConfigureAwait(false);
        }
    }

    // The query of the request line "GET /?code=…&state=… HTTP/1.1"; null for anything else.
    private static async Task<Dictionary<string, string>?> ReadQueryAsync(TcpClient browser, CancellationToken stop)
    {
        var buffer = new byte[8192];
        int read = 0;
        var stream = browser.GetStream();
        while (read < buffer.Length)
        {
            int got = await stream.ReadAsync(buffer.AsMemory(read), stop).ConfigureAwait(false);
            if (got == 0) break;
            read += got;
            if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n", StringComparison.Ordinal)) break;
        }
        return Query(Encoding.ASCII.GetString(buffer, 0, read));
    }

    public static Dictionary<string, string>? Query(string request)
    {
        string line = request.Split("\r\n", 2)[0];
        var parts = line.Split(' ');
        if (parts.Length < 2 || parts[0] != "GET") return null;
        int mark = parts[1].IndexOf('?');
        if (mark < 0) return null;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parts[1][(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals > 0) query[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }
        return query;
    }

    // The page the browser shows once Microsoft has sent it back: whether the sign-in went through, in the light or dark scheme
    // the browser asks for. Its script takes the one-time code out of the address bar. The icons are Lucide's circle-check and
    // circle-x.
    public static string Page(bool done)
    {
        string title = done ? Loc.T("登录完成", "Signed in") : Loc.T("登录没有完成", "The sign-in was not completed");
        string text = done ? Loc.T("BrimDeck 正在连接邮箱，可以关闭这个页面。", "BrimDeck is connecting the mailbox. You can close this page.")
            : Loc.T("回到 BrimDeck 重试。", "Go back to BrimDeck and try again.");
        string mark = done ? "<path d=\"m9 12 2 2 4-4\"/>" : "<path d=\"m15 9-6 6\"/><path d=\"m9 9 6 6\"/>";
        return $$$"""
            <!doctype html>
            <html lang="{{{Loc.T("zh-CN", "en")}}}">
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{{title}}} · BrimDeck</title>
            <style>
            :root{color-scheme:light dark;--bg:#f4f4f6;--card:#fff;--text:#1d1d1f;--sub:#6e6e73;--line:#e3e3e8;--mark:{{{(done ? "#1f9d55" : "#d93025")}}}}
            @media (prefers-color-scheme:dark){:root{--bg:#161618;--card:#202023;--text:#f2f2f4;--sub:#9a9aa1;--line:#2e2e33;--mark:{{{(done ? "#3ccf7a" : "#ff6b5f")}}}}}
            html,body{height:100%;margin:0}
            body{display:grid;place-items:center;padding:16px;box-sizing:border-box;background:var(--bg);color:var(--text);font:15px/1.6 "Segoe UI Variable Text","Segoe UI","Microsoft YaHei UI",system-ui,sans-serif}
            main{width:100%;max-width:380px;box-sizing:border-box;padding:36px 32px 28px;background:var(--card);border:1px solid var(--line);border-radius:8px;text-align:center}
            svg{width:44px;height:44px;color:var(--mark)}
            h1{margin:14px 0 4px;font-size:20px;font-weight:600}
            p{margin:0;color:var(--sub)}
            footer{margin-top:28px;font-size:12px;color:var(--sub)}
            </style>
            <main>
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="10"/>{{{mark}}}</svg>
            <h1>{{{title}}}</h1>
            <p>{{{text}}}</p>
            <footer>BrimDeck</footer>
            </main>
            <script>history.replaceState(null, "", "/")</script>
            </html>
            """;
    }

    // Sends the page, or "not found" for a request that is not the sign-in's.
    private static async Task ReplyAsync(TcpClient browser, string? page, CancellationToken stop)
    {
        byte[] body = Encoding.UTF8.GetBytes(page ?? "");
        string status = page is null ? "404 Not Found" : "200 OK";
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        var stream = browser.GetStream();
        await stream.WriteAsync(head, stop).ConfigureAwait(false);
        await stream.WriteAsync(body, stop).ConfigureAwait(false);
        await stream.FlushAsync(stop).ConfigureAwait(false);
    }
}
