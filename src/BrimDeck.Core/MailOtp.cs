namespace BrimDeck.Core;

// A verification code taken from a recent mail (subject or body). No body is kept — only the code, a short sender label, and ids.
public sealed record MailOtpHit(string Code, string FromShort, DateTimeOffset At, string MessageId);

// QQ mailbox verification codes for the collapsed island: extract 4–8 digit codes, short sender labels, and settings bounds.
public static class MailOtp
{
    // Well-known id for the authorization code in IProviderSecrets (DPAPI on Windows). Not an account address.
    public static readonly Guid SecretId = Guid.Parse("6f2e9a41-8c5d-4b17-9e3a-1d0c7b8a5f24");

    public const string DefaultHost = "imap.qq.com";
    public const int DefaultPort = 993;
    public const int DefaultSeconds = 10;
    public static readonly int[] SecondChoices = [8, 10, 12];

    // 4–8 digits, not glued to more digits; prefers codes near 验证码/code/otp wording.
    private static readonly System.Text.RegularExpressions.Regex NearCode = new(
        @"(?:验证码|校[验核]码|動態密碼|动态密码|one[-\s]?time|otp|pass\s*code|verification\s*code|security\s*code)[^\d]{0,24}(\d{4,8})(?!\d)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex AnyCode = new(
        @"(?<!\d)(\d{4,8})(?!\d)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string? Extract(string? subject, string? body)
    {
        foreach (var text in new[] { subject, body })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var near = NearCode.Match(text);
            if (near.Success) return near.Groups[1].Value;
        }
        // Prefer 6-digit across subject+body before falling back to any 4–8 digit group.
        string? six = null, any = null;
        foreach (var text in new[] { subject, body })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (System.Text.RegularExpressions.Match match in AnyCode.Matches(text))
            {
                var value = match.Groups[1].Value;
                if (value.Length == 6) { six ??= value; }
                else any ??= value;
            }
        }
        return six ?? any;
    }

    // Sender for the island: display name or mailbox local-part, at most eight CJK / Latin characters.
    public static string ShortFrom(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return Loc.T("邮件", "Mail");
        var text = from.Trim();
        var angle = text.LastIndexOf('<');
        if (angle > 0) text = text[..angle].Trim().Trim('"', '\'');
        else if (angle == 0 && text.EndsWith('>'))
        {
            text = text[1..^1];
            var at = text.IndexOf('@');
            if (at > 0) text = text[..at];
        }
        else
        {
            var at = text.IndexOf('@');
            if (at > 0) text = text[..at];
        }
        text = text.Trim();
        if (text.Length == 0) return Loc.T("邮件", "Mail");
        return text.Length <= 8 ? text : text[..8];
    }

    public static int FitSeconds(int seconds) => SecondChoices.Contains(seconds) ? seconds : DefaultSeconds;
}

// Read-only mailbox poll for recent verification codes. Implementations must not persist message bodies.
public interface IMailOtpMailbox
{
    Task<IReadOnlyList<MailOtpHit>> RecentAsync(string address, string authorizationCode, CancellationToken cancellation);
}
