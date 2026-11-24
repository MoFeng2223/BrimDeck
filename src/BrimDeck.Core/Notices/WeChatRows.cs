using System.Text.RegularExpressions;

namespace BrimDeck.Core.Notices;

// One row of desktop WeChat's chat list, as the row's accessible name words it (WeChat 4.2.0.13, read 2026-11-24):
//   <chat>\n[<n>条] <latest message>\n<time>\n消息免打扰\n
// The count, the mute line and "已置顶" are there only when they apply; the count may stand on a line of its own, and a long
// message runs over several lines. A group's message starts with its sender ("小林: 好的"). The row's id is "session_item_"
// and the chat's name, so two chats of one name cannot be told apart. Only the latest message of a chat is ever there.
public sealed record WeChatRow(string Name, int Unread, string Summary, string Time, bool Muted, bool MentionsMe, bool Selected)
{
    // Rows that stand for many accounts or chats at once; nothing is reported for them.
    private static readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        "公众号", "服务号", "订阅号", "订阅号消息", "折叠的聊天", "折叠置顶聊天", "Official Accounts", "Service Accounts", "Subscriptions", "Folded Chats"
    };
    public bool Folder => Folders.Contains(Name);
}

public static partial class WeChatRows
{
    public const string AppKey = "flash:wechat";
    public const string IdPrefix = "session_item_";
    public const string ListId = "session_list";
    public const string InputId = "chat_input_field";
    // The count stops growing here ("[99+条]").
    public const int UnreadCap = 99;
    private static readonly string[] MuteMarks = ["消息免打扰", "Mute Notifications", "Muted"];
    private static readonly string[] PinMarks = ["已置顶", "置顶", "Sticky on Top", "Pinned"];
    private static readonly string[] MentionMarks = ["[有人@我]", "[Mentioned you]", "[You were mentioned]"];

    [GeneratedRegex(@"^\[(\d{1,4})\+?条\]\s*")] private static partial Regex UnreadMark();
    // "12:41", "11/23", "2026/11/23", "昨天", "星期一", "Yesterday", "Monday", each perhaps with a time of day after it.
    [GeneratedRegex(@"^(?:(?:昨天|前天|星期[一二三四五六日天]|Yesterday|(?:Mon|Tues|Wednes|Thurs|Fri|Satur|Sun)day|\d{1,4}/\d{1,2}(?:/\d{1,4})?)(?:\s+\d{1,2}:\d{2})?|\d{1,2}:\d{2}(?:\s?[AaPp][Mm])?)$")]
    private static partial Regex TimeLine();
    [GeneratedRegex(@"^(\d{1,2}):(\d{2})$")] private static partial Regex ClockTime();

    // The row a name and an id say; null for an element that is not a chat's row.
    public static WeChatRow? Parse(string? id, string? name, bool selected)
    {
        if (id is null || !id.StartsWith(IdPrefix, StringComparison.Ordinal) || string.IsNullOrEmpty(name)) return null;
        var lines = name.Replace("\r", "").Split('\n').Select(line => line.Trim()).ToList();
        // The id carries the chat's name whole; the first line is the same name.
        string chat = id[IdPrefix.Length..].Trim() is { Length: > 0 } fromId ? fromId : lines[0];
        if (chat.Length == 0) return null;
        lines.RemoveAt(0);
        bool muted = lines.RemoveAll(line => MuteMarks.Contains(line, StringComparer.OrdinalIgnoreCase)) > 0;
        lines.RemoveAll(line => PinMarks.Contains(line, StringComparer.OrdinalIgnoreCase));
        // The name ends with a line break, and the time is the last line before it (empty for a chat without messages).
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        string time = "";
        if (lines.Count > 0 && TimeLine().IsMatch(lines[^1])) { time = lines[^1]; lines.RemoveAt(lines.Count - 1); }
        string summary = string.Join(' ', lines.Where(line => line.Length > 0));
        // The count and the mention mark lead the message, in either order; the mark stays part of the words.
        string mention = Mention(summary);
        summary = summary[mention.Length..].TrimStart();
        int unread = 0;
        if (UnreadMark().Match(summary) is { Success: true } mark)
        {
            unread = Math.Min(int.Parse(mark.Groups[1].ValueSpan), UnreadCap);
            summary = summary[mark.Length..];
        }
        if (mention.Length == 0) { mention = Mention(summary); summary = summary[mention.Length..].TrimStart(); }
        return new WeChatRow(chat, unread, (mention + " " + summary).Trim(), time, muted, mention.Length > 0, selected);
    }

    private static string Mention(string summary)
        => MentionMarks.FirstOrDefault(text => summary.StartsWith(text, StringComparison.OrdinalIgnoreCase)) is { } found ? summary[..found.Length] : "";

    // Whether a row's time is a time of day within a few minutes of now: a message that has just arrived.
    public static bool JustNow(string time, DateTime now)
    {
        if (ClockTime().Match(time) is not { Success: true } match) return false;
        int minutes = int.Parse(match.Groups[1].ValueSpan) * 60 + int.Parse(match.Groups[2].ValueSpan);
        int gap = Math.Abs(minutes - (now.Hour * 60 + now.Minute));
        return Math.Min(gap, 24 * 60 - gap) <= 2;
    }

    // The chat a notice of this source stands for, or null for any other notice (also the plain "有新消息" of the taskbar flash).
    public static string? ChatOf(Notice? notice)
        => notice is { AppKey: AppKey, Title.Length: > 0 } && notice.InstanceKey.StartsWith(KeyPrefix, StringComparison.Ordinal) ? notice.Title : null;
    public const string KeyPrefix = "wechat:";
}

// What one reading of the list changed: the messages to tell the user about, and the notices (by instance key) whose chat has
// been read in WeChat meanwhile.
public sealed record WeChatChange(IReadOnlyList<NoticeInput> Incoming, IReadOnlyList<string> Read);

// Tells new messages from one reading of WeChat's chat list to the next. The list says only each chat's latest message and how
// many are unread, so a message is new when a chat's unread count grew. The first reading is only remembered. Two more cases:
// a chat first seen with unread messages counts when its time is this minute (it moved up into view with the message), and the
// chat that is open in WeChat, which may keep no count, counts when its latest message changed within Confirm of a flash of
// WeChat's taskbar button (flashed), whichever of the two is seen first. A muted chat tells only of a message that mentions the
// user, a row standing for many accounts tells nothing, and nothing is told while the user is in WeChat. Each message is a
// notice of its own under the chat's name, so the notices page lists a chat's messages together. One thread at a time.
public sealed class WeChatWatch(string appName)
{
    public static readonly TimeSpan Confirm = TimeSpan.FromSeconds(3);
    private const int MaxRemembered = 400;
    private readonly Dictionary<string, (WeChatRow Row, long Order)> _seen = new(StringComparer.Ordinal);
    // The notices told for a chat's unread count, until the count is back at none.
    private readonly Dictionary<string, List<string>> _told = new(StringComparer.Ordinal);
    // The open chat whose latest message changed without a count, waiting for a flash to say it was a message for the user.
    private (string Name, DateTime At)? _waiting;
    private bool _primed;
    private long _order;

    public WeChatChange Observe(IReadOnlyList<WeChatRow> rows, bool flashed, bool inWeChat, DateTime now)
    {
        var incoming = new List<NoticeInput>();
        var read = new List<string>();
        if (_waiting is { } stale && (now - stale.At > Confirm || now < stale.At)) _waiting = null;
        foreach (var row in rows)
        {
            if (row.Folder) continue;
            bool known = _seen.TryGetValue(row.Name, out var last);
            _seen[row.Name] = (row, ++_order);
            // Read in WeChat (here or on the phone): what was told about the chat is done with.
            if (row.Unread == 0 && _told.Remove(row.Name, out var keys)) read.AddRange(keys);
            if (!_primed || inWeChat) continue;
            bool changed = known && (row.Summary != last.Row.Summary || row.Time != last.Row.Time);
            bool grew = known ? row.Unread > last.Row.Unread || row.Unread >= WeChatRows.UnreadCap && changed : row.Unread > 0 && WeChatRows.JustNow(row.Time, now);
            bool open = false;
            if (!grew && row.Selected && row.Unread == 0 && (changed || _waiting?.Name == row.Name))
            {
                open = flashed;
                _waiting = flashed ? null : changed ? (row.Name, now) : _waiting;
            }
            if (!(grew || open) || row.Muted && !row.MentionsMe) continue;
            string key = $"{WeChatRows.KeyPrefix}{now.Ticks}:{incoming.Count}:{row.Name}";
            if (grew)
            {
                if (!_told.TryGetValue(row.Name, out var told)) _told[row.Name] = told = [];
                told.Add(key);
            }
            incoming.Add(new NoticeInput
            {
                Origin = NoticeOrigin.Windows, AppKey = WeChatRows.AppKey, AppName = appName, InstanceKey = key, Title = row.Name,
                Body = row.Summary.Length > 0 ? row.Summary : Loc.T("有新消息", "New message"), Category = NoticeCategory.Message
            });
        }
        _primed = true;
        if (_seen.Count > MaxRemembered)
            foreach (var old in _seen.OrderBy(pair => pair.Value.Order).Take(_seen.Count - MaxRemembered).Select(pair => pair.Key).ToList()) _seen.Remove(old);
        return new(incoming, read);
    }

    // WeChat went away (it was closed or signed out): what is read next is only remembered again.
    public void Forget() { _primed = false; _seen.Clear(); _waiting = null; }
}
