using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Protocol;

// One message of the notice protocol. The same shape travels in both directions and over every link: a sender's notice to
// BrimDeck, BrimDeck's reply, the WSL agent's status reports, and BrimDeck's requests to the agent (an action the user chose,
// a notice the user dismissed). Fields that do not apply to a type stay null and are left out of the JSON.
public sealed class NoticeMessage
{
    public int Version { get; set; } = NoticeProtocol.Version;
    public string Type { get; set; } = NoticeTypes.Notify;
    // A sender's id for this one message: sending it again with the same id is recognized as a retry.
    public string? EventId { get; set; }
    // A stable key chosen by the sender: a later notice with the same key updates the earlier one instead of adding another
    // (a task's progress, then its result).
    public string? ReplaceId { get; set; }
    // The id the source itself gave the notice (a D-Bus notification id), so that later updates and closes find it.
    public string? NativeId { get; set; }
    // Set by the WSL agent: "cli" for brim-notify, "dbus" for a desktop notification. BrimDeck takes the link a message
    // arrives on as its origin; this only tells the two kinds of WSL notices apart.
    public string? Origin { get; set; }
    public string? AppName { get; set; }
    public string? AppId { get; set; }
    public string? Distro { get; set; }
    public string? Session { get; set; }
    // An icon name or a local file path, never a remote address; nothing is downloaded.
    public string? Icon { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    // message, task, success, warning, error or info.
    public string? Category { get; set; }
    // low, normal or high: a hint. Only the user's rule for the source can raise a notice above normal.
    public string? Priority { get; set; }
    // How strongly the notice shows on the island: silent (only listed), plain (words only), glow (the backlight flashes),
    // lively (the backlight and the liquid ripples) or persistent (lively, and it stays until it is handled or closed: a
    // stop with the same ReplaceId, or a later notice with that id and another level). The user's level for the source comes
    // first. A value BrimDeck does not know reads as not given, as does none.
    public string? Level { get; set; }
    // Where the icon and the words sit in the island: left, center or right. Not given, the user's setting decides.
    public string? Align { get; set; }
    // 0 to 1 while a task runs.
    public double? Progress { get; set; }
    // The result of "brim-notify run": BrimDeck words the notice in its own language from these.
    public int? ExitCode { get; set; }
    public long? DurationMs { get; set; }
    public string? Command { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    // How long the message had waited since CreatedAt when it was sent, by the sender's own clock (see Stamp). BrimDeck
    // judges a late notice by it, since a distribution's clock may differ from Windows'.
    public long? AgeMs { get; set; }
    // Time-kind notices (see NoticeTimes): alarm, stopwatch, countdown, mail or event. At is a time and InMs a duration from
    // when BrimDeck receives the message (a countdown's end, an event's start); Until an event's end; LeadMs how long before
    // an event's start BrimDeck reminds. Stop ends the stopwatch, countdown or event sent before with the same ReplaceId, or
    // closes the plain notice sent with it (which ends a persistent one on the island).
    public string? Kind { get; set; }
    public string? At { get; set; }
    public long? InMs { get; set; }
    public string? Until { get; set; }
    public long? LeadMs { get; set; }
    public bool? Stop { get; set; }
    // A desktop notification marked "transient": shown, but not saved with the recent notices (I1).
    public bool? Transient { get; set; }
    public List<NoticeActionInfo>? Actions { get; set; }
    // Type "action": the chosen action's key.
    public string? ActionKey { get; set; }
    // Type "status": the agent's state (see AgentStates) and a technical detail for diagnostics.
    public string? State { get; set; }
    public string? Detail { get; set; }
    // Type "close": why the source closed the notice (the freedesktop reason codes).
    public int? Reason { get; set; }
    // Type "reply": the result for the message just received. Type "result": how a notice that was waited for ended
    // (NoticeWaits), with ActionKey for a chosen button.
    public string? Result { get; set; }
    // Where a plain notice shows (NoticeSlots): the whole island, or the place of one part of the collapsed content.
    public string? Slot { get; set; }
    // "#RRGGBB": the backlight's color for a notice that stays until handled.
    public string? Glow { get; set; }
    // A second line beside the source's name: a project, a branch, a session.
    public string? Context { get; set; }
    // A picture of who or what the notice is from, shown in the icon's place; a local file, as Icon is.
    public string? Picture { get; set; }
    // True: no icon is shown.
    public bool? HideIcon { get; set; }
    // False: the notice only shows on the island and is not listed among the recent notices.
    public bool? History { get; set; }
    // The sender keeps its connection and waits for how the notice ends: a button chosen, dismissed, or run out of time.
    public bool? Wait { get; set; }
    // How long a notice that stays until handled may stay, and with it how long the sender waits.
    public long? TimeoutMs { get; set; }
    // In a reply: BrimDeck will send a "result" on the same connection. A BrimDeck that does not know Wait leaves it out.
    public bool? Waiting { get; set; }
    // Type "remove", and a close (Stop): the notice by BrimDeck's own id, as "list" gives it, instead of by ReplaceId.
    public string? NoticeId { get; set; }
    // Type "list": where the page starts, the Cursor of the reply before; in the reply, where the next page starts, null after
    // the last page.
    public string? Cursor { get; set; }
    // The reply to "list": one page of the notices, newest first, and which notices BrimDeck lists (NoticeScopes).
    public List<NoticeEntry>? Notices { get; set; }
    public string? Scope { get; set; }
    // Questions the sender asks and waits for (an AI agent's multiple-choice question): the user picks options or, where a
    // question allows it, types an answer of their own. Only with Wait.
    public List<NoticeQuestion>? Questions { get; set; }
    // A command or a path the notice is about (what an approval would run), shown as code; never run.
    public string? Code { get; set; }
    // Type "result": the answers to Questions, one per question answered.
    public List<NoticeAnswer>? Answers { get; set; }
    // Type "agent": a coding agent's session (AgentWire). AppName names the tool, AppId its id (AgentHarnessIds), Session the
    // session, State its state (AgentSessionStates), Context the project, Title what it was asked, Body what it said last.
}

// One question of NoticeMessage.Questions.
public sealed class NoticeQuestion
{
    public string Id { get; set; } = "";
    // A short label over the question (a chip: "Auth method").
    public string? Header { get; set; }
    public string Question { get; set; } = "";
    public bool? MultiSelect { get; set; }
    // The user may type an answer instead of picking an option.
    public bool? AllowOther { get; set; }
    public List<NoticeOption>? Options { get; set; }
}

public sealed class NoticeOption
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Description { get; set; }
}

// The answer to one question: the keys of the options picked, and the words typed (AllowOther).
public sealed class NoticeAnswer
{
    public string Id { get; set; } = "";
    public List<string>? Keys { get; set; }
    public string? Text { get; set; }
}

// The states of a coding agent's session (NoticeMessage.State of type "agent").
public static class AgentSessionStates
{
    // Started: the session began. Working: it works on a prompt. Approval: it waits for a permission. Question: it asks the
    // user a question. Done: it finished its turn. Idle: it waits for the next prompt. Ended: the session closed.
    public const string Started = "started", Working = "working", Approval = "approval", Question = "question", Done = "done", Idle = "idle", Ended = "ended";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Started, Working, Approval, Question, Done, Idle, Ended };
}

// One notice as "list" gives it: what a script needs to find it and to close or remove it by Id.
public sealed class NoticeEntry
{
    public string Id { get; set; } = "";
    // windows (an application's notification, read from Windows' notification center), taskbar (a chat whose taskbar button
    // flashes), wsl (a WSL desktop notification), cli (brim, brim-notify, a program), brimdeck (BrimDeck's own) or test.
    public string Origin { get; set; } = "";
    // The name the notice comes from: an application's, or the sender's -s.
    public string Source { get; set; } = "";
    public string? Distro { get; set; }
    // The sender's -k, which close and remove take as well.
    public string? Key { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Context { get; set; }
    public string Category { get; set; } = "";
    // alarm, stopwatch, countdown, mail or event; null for a plain notice.
    public string? Kind { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    // Not yet seen on the notices page.
    public bool Unread { get; set; }
    // On the island now.
    public bool Shown { get; set; }
    // Its sender waits for an answer (--wait or --action).
    public bool Waiting { get; set; }
    // Its source no longer has it: closed by its sender, cleared from Windows' notification center, a timer stopped.
    public bool Closed { get; set; }
}

// Which notices "list" gives and "remove" reaches (NoticeMessage.Scope): those of the command line and programs only, or,
// once the user allows it in the settings, those of every source.
public static class NoticeScopes
{
    public const string Cli = "cli", All = "all";
}

// What brim notify list --json prints: every page put together.
public sealed class NoticeListing
{
    public string Scope { get; set; } = NoticeScopes.Cli;
    public List<NoticeEntry> Notices { get; set; } = [];
}

public sealed class NoticeActionInfo
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    // reply (the key goes back to the sender; the default), copy (Value is put on the clipboard) or open (Value is a web
    // address, a local file or "app:<application id>").
    public string? Type { get; set; }
    public string? Value { get; set; }
    // primary: the main button.
    public string? Style { get; set; }
}

public static class NoticeActionTypes
{
    public const string Reply = "reply", Copy = "copy", Open = "open";
    public const string Primary = "primary";
}

// How a notice that was waited for ended (NoticeMessage.Result of a "result").
public static class NoticeWaits
{
    // Action: a button was chosen (ActionKey). Dismissed: the user closed or deleted the notice, or its sender closed it.
    // Expired: it ran out of time. Replaced: the same key was sent again with a wait of its own.
    public const string Action = "action", Dismissed = "dismissed", Expired = "expired", Replaced = "replaced";
}

// Where a plain notice shows on the collapsed island.
public static class NoticeSlots
{
    public const string Island = "island", Usage = "usage", Music = "music", Monitor = "monitor", Clock = "clock", Transfer = "transfer", Devices = "devices";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Island, Usage, Music, Monitor, Clock, Transfer, Devices };
}

public static class NoticeTypes
{
    public const string Notify = "notify", Close = "close", Status = "status", Action = "action", Dismissed = "dismissed", Reply = "reply";
    // BrimDeck to a sender that waits (NoticeMessage.Wait): how its notice ended.
    public const string Result = "result";
    // A sender asks for the notices (a page at a time), or for one to be removed from the notices page.
    public const string List = "list", Remove = "remove";
    // A coding agent's hook reports its session's state (no notice of its own).
    public const string Agent = "agent";
}

public static class NoticeResults
{
    // Accepted: a new notice. Updated: it changed one shown before. Duplicate: a retry of a message already received.
    // Filtered: received, but the user's rules keep it out (a muted application, notices switched off).
    // Rejected: malformed or over the limits; Reason says why.
    public const string Accepted = "accepted", Updated = "updated", Duplicate = "duplicate", Filtered = "filtered", Rejected = "rejected";
}

public static class AgentStates
{
    // Server: the agent is the session's notification service. Monitor: another service shows notices and the agent watches
    // them. MonitorDenied: another service runs and the bus does not let the agent watch; brim-notify still works.
    // NoBus: no session bus was found. Error: something else failed; Detail says what.
    public const string Server = "server", Monitor = "monitor", MonitorDenied = "monitor-denied", NoBus = "no-bus", Error = "error";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NoticeMessage))]
[JsonSerializable(typeof(AgentBridgeConfig))]
[JsonSerializable(typeof(NoticeListing))]
[JsonSerializable(typeof(string))]
public sealed partial class NoticeJson : JsonSerializerContext;

// Written by BrimDeck into a WSL distribution next to the agent, so that brim-notify can reach Windows through the
// command-line sender while the agent is not running.
public sealed class AgentBridgeConfig
{
    public string? Helper { get; set; }
    public string? Version { get; set; }
}

public static class NoticeProtocol
{
    public const int Version = 1;
    // One frame's payload at most; a longer frame is refused and the connection closed.
    public const int MaxFrame = 64 * 1024;
    public const int MaxTitle = 256, MaxBody = 4000, MaxActions = 8, MaxField = 256, MaxActionValue = 2048;
    // An agent's question: questions in one notice, options in one question, the words of an option, of a typed answer, of a code block.
    public const int MaxQuestions = 4, MaxOptions = 12, MaxOptionText = 512, MaxAnswerText = 4000, MaxCode = 4000;
    // How long a sender may ask a notice to stay (and wait for it) at most, and at least.
    public const long MaxTimeoutMs = 30L * 60 * 1000, MinTimeoutMs = 1000;
    // A run's duration at most (about 100 years): larger values cannot be turned into a time span.
    public const long MaxDurationMs = 36500L * 24 * 60 * 60 * 1000;

    // The pipe is per user and per logon session: one user's senders never reach another session's BrimDeck.
    // The user's SID is hashed only to keep the name short and free of characters a pipe name cannot hold.
    public static string PipeName(int sessionId, string userSid)
        => $"BrimDeck.Notices.{sessionId}.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid)))[..16]}";

    public static byte[] Serialize(NoticeMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, NoticeJson.Default.NoticeMessage);
    public static NoticeMessage? Deserialize(ReadOnlySpan<byte> json)
    {
        try { return JsonSerializer.Deserialize(json, NoticeJson.Default.NoticeMessage); }
        catch (JsonException) { return null; }
    }
    // Gives a message about to be sent its creation time (now, unless it has one) and its age by the same clock. Only the
    // sender stamps a message: a relay on another machine's clock (BrimDeck.Notify.exe --stdio for WSL) passes it on as it is.
    public static void Stamp(NoticeMessage message, DateTimeOffset now)
    {
        message.CreatedAt ??= now;
        message.AgeMs = Math.Clamp((long)(now - message.CreatedAt.Value).TotalMilliseconds, 0, MaxDurationMs);
    }

    public static NoticeMessage Reply(string result, string? reason = null, string? id = null)
        => new() { Type = NoticeTypes.Reply, Result = result, Detail = reason, EventId = id };

    // What one page of a "list" reply may hold, below a frame so that the WSL agent can pass it on as it is.
    public const int PageBudget = 48 * 1024;

    // The reply to "list": the notices after cursor that fit in a page, with the cursor of the next page. entries are in the
    // order ListOrder gives (newest first); a cursor names the last notice a page held by its time and id, so a page still
    // starts in the right place when notices were added or removed meanwhile. A cursor that cannot be read starts over.
    public static NoticeMessage ListPage(IReadOnlyList<NoticeEntry> entries, string? cursor, string scope, string? id)
    {
        int start = 0;
        if (ReadCursor(cursor) is var (ticks, after))
            while (start < entries.Count && ListOrder(entries[start], ticks, after) <= 0) start++;
        var page = new List<NoticeEntry>();
        int size = 512;
        for (int i = start; i < entries.Count; i++)
        {
            int length = JsonSerializer.SerializeToUtf8Bytes(entries[i], NoticeJson.Default.NoticeEntry).Length + 1;
            if (page.Count > 0 && size + length > PageBudget) break;
            page.Add(entries[i]);
            size += length;
        }
        bool more = start + page.Count < entries.Count;
        string? next = more ? page[^1].ReceivedAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + ":" + page[^1].Id : null;
        return new() { Type = NoticeTypes.Reply, Result = NoticeResults.Accepted, EventId = id, Scope = scope, Notices = page, Cursor = next };
    }

    // The order of "list": newest first, and by id among notices received at the same moment.
    public static int ListOrder(NoticeEntry a, NoticeEntry b) => ListOrder(a, b.ReceivedAt.UtcTicks, b.Id);

    private static int ListOrder(NoticeEntry entry, long ticks, string id)
    {
        int byTime = ticks.CompareTo(entry.ReceivedAt.UtcTicks);
        return byTime != 0 ? byTime : string.CompareOrdinal(entry.Id, id);
    }

    private static (long Ticks, string Id)? ReadCursor(string? cursor)
    {
        int at = cursor?.IndexOf(':') ?? -1;
        return at > 0 && long.TryParse(cursor.AsSpan(0, at), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ? (ticks, cursor![(at + 1)..]) : null;
    }

    // BrimDeck's request to a WSL agent about one of its desktop notices (type Action or Dismissed), addressed by both the
    // session and the id of the notice's "wsl:<distribution>:<session>:<id>" key. The agent acts only on its current run's
    // session, so an earlier run's notice never acts on the new run's notice with the same id (C-L5). Null when the key names
    // no notice id.
    public static NoticeMessage? ToAgent(string type, string instanceKey, string? actionKey = null)
    {
        var parts = instanceKey.Split(':');
        if (parts is not ["wsl", _, .., var session, var id] || !uint.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)) return null;
        return new() { Type = type, Session = session.Length > 0 ? session : null, NativeId = id, ActionKey = actionKey };
    }

    // Checks a message received from outside BrimDeck and trims its texts to the limits. Returns null when it can be used,
    // or the reason it is refused.
    public static string? Validate(NoticeMessage message)
    {
        if (message.Version < 1 || message.Version > Version) return "unsupported-version";
        if (message.Type is not (NoticeTypes.Notify or NoticeTypes.Close or NoticeTypes.Status or NoticeTypes.Action or NoticeTypes.Dismissed or NoticeTypes.Reply or NoticeTypes.Result
            or NoticeTypes.List or NoticeTypes.Remove or NoticeTypes.Agent))
            return "unknown-type";
        if (message.Type == NoticeTypes.Agent && (string.IsNullOrWhiteSpace(message.AppId) || string.IsNullOrWhiteSpace(message.Session) || message.State is null || !AgentSessionStates.All.Contains(message.State)))
            return "bad-agent";
        if (message.Type == NoticeTypes.Remove && string.IsNullOrWhiteSpace(message.NoticeId) && string.IsNullOrWhiteSpace(message.ReplaceId)) return "no-target";
        // A time-kind notice may come without text: BrimDeck names it by its kind ("Timer", "Stopwatch").
        if (message.Type == NoticeTypes.Notify && string.IsNullOrWhiteSpace(message.Title) && string.IsNullOrWhiteSpace(message.Body) && message.ExitCode is null
            && message.Kind is not (NoticeTimes.Countdown or NoticeTimes.Stopwatch or NoticeTimes.Event) && message.Stop != true)
            return "empty";
        if ((message.Kind?.Length ?? 0) > NoticeTimes.MaxText || (message.At?.Length ?? 0) > NoticeTimes.MaxText || (message.Until?.Length ?? 0) > NoticeTimes.MaxText)
            return "bad-time";
        if (NoticeTimes.Check(message, DateTimeOffset.UtcNow, TimeZoneInfo.Local) is { } timeProblem) return timeProblem;
        if (message.Progress is { } progress && !(double.IsFinite(progress) && progress >= 0 && progress <= 1)) return "bad-progress";
        if (message.DurationMs is < 0 or > MaxDurationMs) return "bad-duration";
        if (message.AgeMs is < 0 or > MaxDurationMs) return "bad-age";
        if (message.TimeoutMs is < MinTimeoutMs or > MaxTimeoutMs) return "bad-timeout";
        ClipTexts(message);
        return null;
    }

    // Cuts a message's texts to the limits, as BrimDeck does with each message it receives. brim-notify cuts them before it
    // sends, so that a long body still fits in a frame and shows as BrimDeck would show it anyway (W7).
    public static void ClipTexts(NoticeMessage message)
    {
        message.Title = Clip(message.Title, MaxTitle);
        message.Body = Clip(message.Body, MaxBody);
        message.EventId = Clip(message.EventId, MaxField);
        message.ReplaceId = Clip(message.ReplaceId, MaxField);
        message.NativeId = Clip(message.NativeId, MaxField);
        message.AppName = Clip(message.AppName, MaxField);
        message.AppId = Clip(message.AppId, MaxField);
        message.Distro = Clip(message.Distro, MaxField);
        message.Session = Clip(message.Session, MaxField);
        message.Icon = Clip(message.Icon, 1024);
        message.Command = Clip(message.Command, MaxField);
        message.Detail = Clip(message.Detail, 1024);
        message.Level = Clip(message.Level, NoticeTimes.MaxText);
        message.Align = Clip(message.Align, NoticeTimes.MaxText);
        message.Slot = Clip(message.Slot, NoticeTimes.MaxText);
        message.Glow = Clip(message.Glow, NoticeTimes.MaxText);
        message.Context = Clip(message.Context, MaxField);
        message.Picture = Clip(message.Picture, 1024);
        message.ActionKey = Clip(message.ActionKey, 64);
        message.NoticeId = Clip(message.NoticeId, MaxField);
        message.Cursor = Clip(message.Cursor, MaxField);
        message.Code = Clip(message.Code, MaxCode);
        if (message.Questions is { } questions)
            message.Questions = questions.Where(q => q is not null && !string.IsNullOrWhiteSpace(q.Question)).Take(MaxQuestions).Select((q, i) => new NoticeQuestion
            {
                Id = Clip(string.IsNullOrWhiteSpace(q.Id) ? i.ToString(CultureInfo.InvariantCulture) : q.Id, 64)!, Header = Clip(q.Header, 64), Question = Clip(q.Question, MaxOptionText)!,
                MultiSelect = q.MultiSelect, AllowOther = q.AllowOther,
                Options = q.Options?.Where(o => o is not null && !string.IsNullOrWhiteSpace(o.Label)).Take(MaxOptions).Select((o, j) => new NoticeOption
                {
                    Key = Clip(string.IsNullOrWhiteSpace(o.Key) ? j.ToString(CultureInfo.InvariantCulture) : o.Key, 64)!, Label = Clip(o.Label, MaxOptionText)!, Description = Clip(o.Description, MaxOptionText)
                }).ToList()
            }).ToList();
        if (message.Answers is { } answers)
            message.Answers = answers.Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Id)).Take(MaxQuestions).Select(a => new NoticeAnswer
            {
                Id = Clip(a.Id, 64)!, Keys = a.Keys?.Where(k => !string.IsNullOrWhiteSpace(k)).Take(MaxOptions).Select(k => Clip(k, 64)!).ToList(), Text = Clip(a.Text, MaxAnswerText)
            }).ToList();
        if (message.Actions is { } actions)
            message.Actions = actions.Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Key) && !string.IsNullOrWhiteSpace(a.Label))
                .Take(MaxActions).Select(a => new NoticeActionInfo
                {
                    Key = Clip(a.Key, 64)!, Label = Clip(a.Label, 64)!, Type = Clip(a.Type, NoticeTimes.MaxText), Value = Clip(a.Value, MaxActionValue), Style = Clip(a.Style, NoticeTimes.MaxText)
                }).ToList();
    }

    // Whether the message fits in one frame.
    public static bool Fits(NoticeMessage message) => Serialize(message).Length <= MaxFrame;

    // Removes control characters other than line breaks and tabs, and the invisible characters Clean drops (the bidi overrides
    // and isolates among them), which could make a text read differently from what it is; line and paragraph separators
    // become a line break. Cuts at the limit without splitting a surrogate pair.
    public static string? Clip(string? text, int limit)
    {
        if (text is null) return null;
        var builder = new StringBuilder(Math.Min(text.Length, limit));
        // Combining marks the output ends with (L1): what was dropped between them does not start the count again.
        int marks = 0;
        for (int i = 0; i < text.Length && builder.Length < limit; i++)
        {
            char c = text[i];
            bool pair = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            if (Unwanted(text, i, marks, out bool breaks)) { if (breaks) { builder.Append('\n'); marks = 0; } }
            else if (char.IsControl(c) && c is not ('\n' or '\t')) { if (c != '\r') { builder.Append(' '); marks = 0; } }
            else { builder.Append(c); if (pair) builder.Append(text[i + 1]); marks = Mark(text, i) ? marks + 1 : 0; }
            if (pair) i++;
        }
        if (builder.Length > limit) builder.Length = limit;
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1])) builder.Length--;
        return builder.ToString();
    }

    // Text as a sender wrote it, without the invisible characters that let it pass for something else (sec-L4, S3): format
    // characters (zero-width spaces, bidi embeddings, overrides and isolates, word joiners, the BOM, stray Unicode tags) and
    // the Hangul fillers that show as nothing are dropped, and a letter keeps at most MaxMarks combining marks, so stacked
    // marks cannot spill over other lines. Kept, because writing needs them (C-L11): the zero-width joiner of emoji
    // sequences, the zero-width non-joiner (Persian, Indic scripts), the left-to-right, right-to-left and Arabic letter marks
    // that set punctuation in right-to-left text, and the tags of a subdivision flag (England, Scotland). Line and paragraph
    // separators, which WPF breaks lines on, become a line break; in a single line they, line breaks and every other
    // control character become a space (a carriage return goes). BrimDeck.Core's NoticeText.Clean is this.
    public static string Clean(string text, bool singleLine = false)
    {
        bool Pair(int index) => char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);
        // Combining marks the output ends with (L1): what was dropped between them does not start the count again.
        int i = 0, marks = 0;
        for (; i < text.Length; i++)
        {
            if (Unwanted(text, i, marks, out _) || singleLine && char.IsControl(text[i])) break;
            marks = Mark(text, i) ? marks + 1 : 0;
            if (Pair(i)) i++;
        }
        if (i == text.Length) return text;
        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, i);
        for (; i < text.Length; i++)
        {
            bool pair = Pair(i);
            if (Unwanted(text, i, marks, out bool breaks)) { if (breaks) { builder.Append(singleLine ? ' ' : '\n'); marks = 0; } }
            else if (singleLine && char.IsControl(text[i])) { if (text[i] != '\r') { builder.Append(' '); marks = 0; } }
            else { builder.Append(text[i]); if (pair) builder.Append(text[i + 1]); marks = Mark(text, i) ? marks + 1 : 0; }
            if (pair) i++;
        }
        return builder.ToString();
    }

    // Combining marks one letter keeps; enough for Vietnamese, Indic and Thai stacks and an emoji keycap.
    public const int MaxMarks = 4;

    // marks: how many combining marks the output kept so far ends with.
    private static bool Unwanted(string text, int index, int marks, out bool breaks)
    {
        var category = char.GetUnicodeCategory(text, index);
        breaks = category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
        if (breaks) return true;
        char c = text[index];
        if (c is '\u115F' or '\u1160' or '\u3164' or '\uFFA0') return true;
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) return marks >= MaxMarks;
        if (category != UnicodeCategory.Format || c is '\u200C' or '\u200D' or '\u200E' or '\u200F' or '\u061C') return false;
        return !(Tag(text, index) && AfterFlag(text, index));
    }

    // A Unicode tag character (U+E0020 to U+E007F); they take a surrogate pair.
    private static bool Tag(string text, int index)
        => char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
            && char.ConvertToUtf32(text[index], text[index + 1]) is >= 0xE0020 and <= 0xE007F;

    // Whether the tag at index follows a black flag (U+1F3F4) through other tags only, as in a subdivision flag; the cancel
    // tag (U+E007F) ends the flag, so a tag after it is stray. A flag's tags are a region's code, a few letters: those beyond
    // MaxFlagTags are stray too, which keeps the look back short (L1) and hides no text behind a flag.
    private static bool AfterFlag(string text, int index)
    {
        int j = index, tags = 0;
        while (tags < MaxFlagTags - 1 && j >= 2 && Tag(text, j - 2) && !(text[j - 2] == '\uDB40' && text[j - 1] == '\uDC7F')) { j -= 2; tags++; }
        return j >= 2 && text[j - 2] == '\uD83C' && text[j - 1] == '\uDFF4';
    }

    // Tags one flag keeps at most: a subdivision's code ("gbsct") with the cancel tag takes six.
    public const int MaxFlagTags = 8;

    private static bool Mark(string text, int index) => char.GetUnicodeCategory(text, index) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
}

// Frames on a stream: a 4-byte little-endian length, then that many bytes of UTF-8 JSON.
public static class NoticeFrames
{
    public static async Task WriteAsync(Stream stream, NoticeMessage message, CancellationToken cancellation = default)
    {
        var payload = NoticeProtocol.Serialize(message);
        if (payload.Length > NoticeProtocol.MaxFrame) throw new InvalidDataException("The message is larger than a frame may be.");
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    // The next frame's payload, or null at the end of the stream. A frame over the limit, or a stream that ends inside a
    // frame, throws InvalidDataException.
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellation = default)
    {
        var header = new byte[4];
        if (!await FillAsync(stream, header, cancellation).ConfigureAwait(false)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > NoticeProtocol.MaxFrame) throw new InvalidDataException("Frame length " + length + " is outside the limit.");
        var payload = new byte[length];
        if (length > 0 && !await FillAsync(stream, payload, cancellation).ConfigureAwait(false)) throw new InvalidDataException("The stream ended inside a frame.");
        return payload;
    }

    public static async Task<NoticeMessage?> ReadMessageAsync(Stream stream, CancellationToken cancellation = default)
    {
        var payload = await ReadAsync(stream, cancellation).ConfigureAwait(false);
        if (payload is null) return null;
        return NoticeProtocol.Deserialize(payload) ?? throw new InvalidDataException("A frame did not hold a notice message.");
    }

    // Hands each message to handle until the stream ends. One that handle throws on is dropped (failed hears why) and the
    // next is read, so a single message from outside cannot end the reader; a broken frame still throws, as the stream can no
    // longer be followed after it.
    public static async Task ReadEachAsync(Stream stream, Action<NoticeMessage> handle, Action<Exception> failed, CancellationToken cancellation = default)
    {
        while (await ReadMessageAsync(stream, cancellation).ConfigureAwait(false) is { } message)
        {
            try { handle(message); }
            catch (Exception ex) { failed(ex); }
        }
    }

    // One message and its reply, within wait. Reply is null when the exchange failed or ran out of time; Written says whether
    // the whole message had gone out by then. Once it has, the receiver may act on it even without answering in time, so the
    // sender must not send it again another way, which would show the notice twice or start a second timer (W3).
    public static async Task<(NoticeMessage? Reply, bool Written)> ExchangeAsync(Stream stream, NoticeMessage message, TimeSpan wait, CancellationToken cancellation = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(wait);
        bool written = false;
        try
        {
            await WriteAsync(stream, message, limit.Token).ConfigureAwait(false);
            written = true;
            return (await ReadMessageAsync(stream, limit.Token).ConfigureAwait(false), true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { return (null, written); }
    }

    // True when the buffer was filled; false when the stream ended before its first byte.
    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellation)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), cancellation).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new InvalidDataException("The stream ended inside a frame.");
            }
            read += n;
        }
        return true;
    }
}
