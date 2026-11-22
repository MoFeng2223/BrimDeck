using BrimDeck.Protocol;

namespace BrimDeck.Core.Notices;

// The notices as "brim notify list" gives them, and which ones a sender may list or remove. The pipe and the WSL link admit
// any program of the user, so without the user's leave (DeckSettings.NoticeCommandAccess) only the notices of the command line
// and programs are reached: an application's notification (a chat's words) is not handed to whatever asks for it.
public static class NoticeEntries
{
    public static bool Reachable(Notice notice, bool all) => all || notice.Origin == NoticeOrigin.Cli;

    // In the order of "list" (NoticeProtocol.ListOrder). source: only the notices of that name, when given. codes: the user
    // hides verification codes (NoticeCodes), so the words go out with them hidden as the notices page shows them. shown and
    // waiting say whether a notice is on the island and whether its sender waits for an answer.
    public static List<NoticeEntry> Of(IEnumerable<Notice> notices, bool all, string? source, bool codes, Func<Notice, bool> shown, Func<Notice, bool> waiting)
    {
        string? name = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        var entries = notices.Where(n => Reachable(n, all) && (name is null || string.Equals(n.AppName, name, StringComparison.OrdinalIgnoreCase)))
            .Select(n => Entry(n, codes, shown(n), waiting(n))).ToList();
        entries.Sort(NoticeProtocol.ListOrder);
        return entries;
    }

    private static NoticeEntry Entry(Notice notice, bool codes, bool shown, bool waiting)
    {
        var words = NoticeCodes.Words(notice, codes);
        return new()
        {
            Id = notice.Id, Origin = Origin(notice), Source = notice.AppName, Distro = notice.Distro, Key = notice.ReplaceKey,
            Title = words.Title, Body = words.Body, Context = notice.Context.Length > 0 ? notice.Context : null,
            Category = notice.Category.ToString().ToLowerInvariant(), Kind = notice.TimeKind == NoticeTimeKind.None ? null : notice.TimeKind.ToString().ToLowerInvariant(),
            ReceivedAt = notice.ReceivedAt, Unread = !notice.Viewed, Shown = shown, Waiting = waiting, Closed = !notice.SourcePresent
        };
    }

    private static string Origin(Notice notice) => notice.Origin switch
    {
        NoticeOrigin.Windows when notice.AppKey.StartsWith("flash:", StringComparison.Ordinal) => "taskbar",
        NoticeOrigin.Internal => "brimdeck",
        var origin => origin.ToString().ToLowerInvariant()
    };
}
