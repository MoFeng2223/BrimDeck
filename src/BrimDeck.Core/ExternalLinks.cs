namespace BrimDeck.Core;

// A plugin page never browses inside the island: the folder it ships in, on the origin the host gave it, is all it may
// load. What it may do is hand a link to the desktop browser, and this is the one place that decides whether a piece of
// text from a page is a web address at all. It sits here, beside the ceilings it applies, because the answer is the
// plugin author's interface — and a browser process is not needed to find out whether an answer is wrong.
public static class ExternalLinks
{
    // The address to give the browser, or the reason this text is not one. Only an absolute http/https URL with a host
    // passes: javascript:, data:, file:, mailto: and any custom protocol are each a way to start something other than a
    // browser page, and the shell would obey them without asking. A relative path is not a refusal but a question — the
    // host answers it inside the plugin folder, where the page's own files are.
    public static (string? Url, string? Reason) Accept(string? text)
    {
        var url = (text ?? "").Trim();
        if (url.Length == 0) return (null, Loc.T("链接为空", "the link is empty"));
        if (url.Length > Bridge.MaximumUrlChars) return (null, Loc.T($"链接超过 {Bridge.MaximumUrlChars} 个字符", $"the link is over {Bridge.MaximumUrlChars} characters"));
        // One space is where a single address becomes two arguments; a URL worth opening never carries one.
        if (url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return (null, Loc.T("链接含空格或控制字符", "the link contains a space or a control character"));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return (null, Loc.T("链接不是完整地址（需要 https:// 开头）", "the link is not a full address — it needs a scheme such as https://"));
        if (parsed.Scheme is not ("http" or "https")) return (null, Loc.T("只打开 http/https 链接，收到的是 " + parsed.Scheme, "only http and https links open; this one is " + parsed.Scheme));
        if (parsed.Host.Length == 0) return (null, Loc.T("链接没有主机名", "the link has no host name"));
        return (parsed.AbsoluteUri, null);
    }
}

// How often one plugin may bring up the desktop browser with no click behind the request. A clicked link is user-
// initiated and counted by nobody; an API call can come from a loop, and a page in a loop fills a desktop with tabs in
// seconds. The caller brings the clock, so the ceiling is checkable without waiting for it.
public sealed class LinkBudget(int maximum, TimeSpan window)
{
    private readonly Queue<DateTime> _opened = new();

    public bool Allow(DateTime at)
    {
        while (_opened.Count > 0 && at - _opened.Peek() > window) _opened.Dequeue();
        if (_opened.Count >= maximum) return false;
        _opened.Enqueue(at);
        return true;
    }
}
