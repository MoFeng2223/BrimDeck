using BrimDeck.Core;

internal static class MailOtpTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("Mail OTP: extracts verification-adjacent 6-digit code from subject",
            MailOtp.Extract("您的验证码是 482913，5 分钟内有效", null) == "482913");
        check("Mail OTP: extracts English verification code from body",
            MailOtp.Extract("Hello", "Your verification code is 917255. Do not share it.") == "917255");
        check("Mail OTP: prefers 6-digit among bare numbers",
            MailOtp.Extract("Order 12345 code follows", "Use 884422 within 10 minutes. Ref 99.") == "884422");
        check("Mail OTP: accepts 4-8 digit lengths near keywords",
            MailOtp.Extract("动态密码 1234", null) == "1234"
            && MailOtp.Extract("otp: 99887766", null) == "99887766");
        check("Mail OTP: returns null when no digit group fits",
            MailOtp.Extract("Hello world", "no codes here") is null
            && MailOtp.Extract(null, null) is null);

        check("Mail OTP: shortens display name to 8 chars",
            MailOtp.ShortFrom("腾讯安全中心超长名称专用 <noreply@qq.com>") == "腾讯安全中心超长"
            && MailOtp.ShortFrom("短名 <a@b.c>") == "短名");
        check("Mail OTP: uses mailbox local-part when angle-only",
            MailOtp.ShortFrom("<service@mail.example>") == "service");
        check("Mail OTP: empty from falls back to Mail label",
            MailOtp.ShortFrom("") == Loc.T("邮件", "Mail"));

        check("Mail OTP: display seconds snap to 8/10/12",
            MailOtp.FitSeconds(10) == 10 && MailOtp.FitSeconds(9) == MailOtp.DefaultSeconds
            && MailOtp.SecondChoices.SequenceEqual([8, 10, 12]));

        var settings = new DeckSettings { MailOtpEnabled = true, MailOtpAutoCopy = true, MailOtpSeconds = 99, MailOtpAddress = "  name@qq.com  " };
        settings.Normalize();
        check("Mail OTP: settings normalize address and seconds",
            settings.MailOtpAddress == "name@qq.com" && settings.MailOtpSeconds == MailOtp.DefaultSeconds
            && settings.MailOtpEnabled && settings.MailOtpAutoCopy);
        var defaults = new DeckSettings(); defaults.Normalize();
        check("Mail OTP: defaults leave feature off without auto-copy",
            !defaults.MailOtpEnabled && !defaults.MailOtpAutoCopy && defaults.MailOtpSeconds == MailOtp.DefaultSeconds
            && defaults.MailOtpAddress == "");
    }
}
