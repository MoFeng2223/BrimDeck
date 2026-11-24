using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core.Mail;

// What a Microsoft sign-in gives: a short-lived access token for IMAP, and the refresh token that gets the next one. Only the
// refresh token is saved (encrypted, as an authorization code is).
public sealed record MicrosoftTokens(string Access, string Refresh, DateTimeOffset Expires);

// Signing in to Outlook.com, Hotmail and Microsoft 365 mailboxes, which take neither a password nor an app password over IMAP:
// the user signs in on Microsoft's page in the default browser, and the page hands a one-time code back to a local address
// (OAuth 2.0 authorization code flow with PKCE, as a public client). The access token then signs in to IMAP (XOAUTH2).
public static class MicrosoftMail
{
    // BrimDeck's application (client) id in Microsoft Entra: a public client for "accounts in any organizational directory and
    // personal Microsoft accounts", with the redirect address http://localhost under "Mobile and desktop applications". Empty
    // until the maintainer has registered it; the Outlook sign-in is then not offered.
    public const string ClientId = "";
    public static bool Available => ClientId.Length > 0;

    private const string Authority = "https://login.microsoftonline.com/common/oauth2/v2.0";
    public const string Scope = "https://outlook.office.com/IMAP.AccessAsUser.All offline_access";

    public static string Verifier() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // The page the browser opens. address is offered as the account to sign in with.
    public static string AuthorizeUrl(string clientId, string redirect, string state, string challenge, string address)
        => Authority + "/authorize?" + Form(("client_id", clientId), ("response_type", "code"), ("redirect_uri", redirect), ("response_mode", "query"), ("scope", Scope),
            ("state", state), ("code_challenge", challenge), ("code_challenge_method", "S256"), ("login_hint", address), ("prompt", "select_account"));

    private static string Form(params (string Key, string Value)[] fields) => string.Join("&", fields.Select(field => field.Key + "=" + Uri.EscapeDataString(field.Value)));

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
        return new(access, refresh, now.AddSeconds(seconds));
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
    public static async Task<MicrosoftTokens?> SignInAsync(HttpClient http, string address, Func<string, bool> open, CancellationToken stop)
    {
        if (!Available) throw new InvalidOperationException("No client id.");
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
            if (query is null || !query.TryGetValue("state", out var returned) || returned != state) { await ReplyAsync(browser, false, stop).ConfigureAwait(false); continue; }
            bool allowed = query.TryGetValue("code", out var code) && code.Length > 0;
            await ReplyAsync(browser, allowed, stop).ConfigureAwait(false);
            if (!allowed) return null;
            return await TokenAsync(http, null, stop, ("client_id", ClientId), ("grant_type", "authorization_code"), ("code", code!), ("redirect_uri", redirect),
                ("code_verifier", verifier), ("scope", Scope)).ConfigureAwait(false);
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

    private static async Task ReplyAsync(TcpClient browser, bool done, CancellationToken stop)
    {
        string text = done ? Loc.T("登录完成，可以关闭这个页面并回到 BrimDeck。", "Signed in. You can close this page and go back to BrimDeck.")
            : Loc.T("登录没有完成，请回到 BrimDeck 重试。", "The sign-in was not completed. Go back to BrimDeck and try again.");
        byte[] body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>BrimDeck</title><p style=\"font:16px system-ui;margin:48px\">{text}</p>");
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        var stream = browser.GetStream();
        await stream.WriteAsync(head, stop).ConfigureAwait(false);
        await stream.WriteAsync(body, stop).ConfigureAwait(false);
        await stream.FlushAsync(stop).ConfigureAwait(false);
    }
}
