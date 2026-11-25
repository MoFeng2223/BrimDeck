using System.Collections.Concurrent;

namespace BrimDeck.Core.Notices;

// Which notices the notice page shows together as one conversation. Only a chat application's notices are: their title is the
// chat's name, so the same title is the same chat. Elsewhere the same title says nothing of the kind (a mail application's title
// is the sender, and each mail is its own), so every other notice stands alone.
public static class NoticeConversations
{
    // Whether an application is a chat application, as its key and name tell (NoticeGlyphs.App), kept per application since the
    // page asks for every notice it lists.
    private static readonly ConcurrentDictionary<(string Key, string Name, NoticeOrigin Origin), bool> Chats = new();

    public static bool IsChat(string appKey, string appName, NoticeOrigin origin) =>
        Chats.GetOrAdd((appKey, appName, origin), app => NoticeGlyphs.App(app.Key, app.Name, app.Origin) == NoticeGlyph.Chat);

    // The key of a notice's conversation: the application and the title for a chat application's notice with a title, else the
    // notice's own id.
    public static string Key(Notice notice) =>
        notice.Title.Length > 0 && IsChat(notice.AppKey, notice.AppName, notice.Origin) ? notice.AppKey + "\u0001" + notice.Title : "\u0001" + notice.Id;
}
