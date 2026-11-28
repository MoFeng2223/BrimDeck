using BrimDeck.Protocol;

namespace BrimDeck.Core.Agents;

// What a coding agent's session is doing, as its hooks report it (NoticeMessage of type "agent").
public enum AgentState { Started, Working, Approval, Question, Done, Idle, Ended }

// One session of a coding agent. Harness is the tool's id (AgentHarnessIds), Name the name it is shown by; Project the folder
// it works in (its last part); Prompt what it was last asked; Message what it said last. Since: when its current task began,
// the start of the working turn (a wait for the user within it does not start it again); the collapsed row counts from it.
public sealed record AgentSession(string Harness, string Name, string Id, AgentState State, string Project, string Prompt, string Message,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt)
{
    public string Key => Harness + "\u0001" + Id;
    public DateTimeOffset Since { get; init; } = UpdatedAt;
    // While it waits for the user: the mark of the tool call it waits on (NoticeMessage.Call), when its hook gave one.
    public string? Call { get; init; }
    // The file the tool writes the session to, when its hook names one (AgentTranscripts).
    public string? Transcript { get; init; }
}

// The sessions BrimDeck knows of, kept in memory only: a restart forgets them, and the next hook brings a session back.
// A session that reports nothing for a while is dropped: a terminal closed without its session ending says nothing more.
public sealed class AgentSessions
{
    // How long a finished turn shows before the session counts as idle.
    public static readonly TimeSpan DoneFor = TimeSpan.FromMinutes(10);
    // How long a session may be silent before it is dropped: working (a long tool run says nothing between hooks), and the rest.
    public static readonly TimeSpan WorkingSilence = TimeSpan.FromMinutes(30), Silence = TimeSpan.FromHours(2);
    // Sessions kept at most; the oldest go first.
    public const int Limit = 32;

    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);

    // In the order the collapsed row and the panel show them: those waiting for the user first, then working, then finished,
    // and within each the most recent first.
    public IReadOnlyList<AgentSession> All => [.. _sessions.Values.OrderBy(s => Rank(s.State)).ThenByDescending(s => s.UpdatedAt)];

    // The sessions worth showing on the collapsed row: waiting for the user, working, or finished a short while ago.
    public IReadOnlyList<AgentSession> Active(DateTimeOffset now) => [.. All.Where(s => s.State is AgentState.Approval or AgentState.Question or AgentState.Working
        || s.State == AgentState.Done && now - s.UpdatedAt < DoneFor)];

    public static int Rank(AgentState state) => state switch
    {
        AgentState.Approval or AgentState.Question => 0, AgentState.Working => 1, AgentState.Done => 2, AgentState.Started or AgentState.Idle => 3, _ => 4
    };

    public static AgentState? Parse(string? state) => state switch
    {
        AgentSessionStates.Started => AgentState.Started, AgentSessionStates.Working => AgentState.Working, AgentSessionStates.Approval => AgentState.Approval,
        AgentSessionStates.Question => AgentState.Question, AgentSessionStates.Done => AgentState.Done, AgentSessionStates.Idle => AgentState.Idle,
        AgentSessionStates.Ended => AgentState.Ended, _ => null
    };

    public AgentSession? Find(string harness, string id) => _sessions.GetValueOrDefault(harness + "\u0001" + id);

    // Takes one report (validated: NoticeProtocol.Validate). True when what is shown changed.
    public bool Apply(NoticeMessage message, DateTimeOffset now)
    {
        if (message.Type != NoticeTypes.Agent || Parse(message.State) is not { } state || string.IsNullOrWhiteSpace(message.AppId) || string.IsNullOrWhiteSpace(message.Session)) return false;
        string harness = message.AppId.Trim(), id = message.Session.Trim(), key = harness + "\u0001" + id;
        if (state == AgentState.Ended) return _sessions.Remove(key);
        string Text(string? text, string kept) => string.IsNullOrWhiteSpace(text) ? kept : NoticeProtocol.Clean(text, singleLine: true).Trim();
        _sessions.TryGetValue(key, out var before);
        // A new prompt starts the words over; a later report keeps what it does not say.
        bool fresh = state == AgentState.Working && message.Title is { Length: > 0 };
        // The result of another tool call than the one the session waits on (one run beside it, a subagent's, which shares the
        // session's id) does not end the wait: the session is alive, and still waits.
        if (!fresh && state == AgentState.Working && before is { State: AgentState.Approval or AgentState.Question, Call: { Length: > 0 } pending }
            && message.Call is { Length: > 0 } call && call != pending)
        {
            _sessions[key] = before with { UpdatedAt = now };
            return false;
        }
        // A session BrimDeck already knows that says it started goes on as it was: WorkBuddy says so right after each prompt,
        // Qoder when a task is opened again, Claude Code when it compacts in the middle of a turn.
        if (state == AgentState.Started && before is not null)
        {
            if (before.State is AgentState.Working or AgentState.Approval or AgentState.Question) _sessions[key] = before with { UpdatedAt = now };
            return false;
        }
        // A tool that says it is idle right after its turn finished (Claude Code's and Qoder's idle_prompt) leaves the finished
        // turn showing for its time.
        if (state == AgentState.Idle && before?.State == AgentState.Done) return false;
        var session = new AgentSession(harness, Text(message.AppName, before?.Name ?? harness), id, state,
            Text(message.Context, before?.Project ?? ""), Text(message.Title, before?.Prompt ?? ""), fresh ? Text(message.Body, "") : Text(message.Body, before?.Message ?? ""),
            before?.StartedAt ?? now, now)
        {
            // A turn starts with a new prompt, or with work that follows anything but working or waiting for the user.
            Since = before is not null && !fresh && (state is AgentState.Approval or AgentState.Question
                || before.State is AgentState.Working or AgentState.Approval or AgentState.Question) ? before.Since : now,
            Call = state is AgentState.Approval or AgentState.Question && !string.IsNullOrWhiteSpace(message.Call) ? message.Call : null,
            Transcript = string.IsNullOrWhiteSpace(message.Transcript) ? before?.Transcript : message.Transcript
        };
        _sessions[key] = session;
        if (_sessions.Count > Limit)
            foreach (var old in _sessions.Values.OrderBy(s => s.UpdatedAt).Take(_sessions.Count - Limit).ToList()) _sessions.Remove(old.Key);
        return before is null || before with { UpdatedAt = now } != session;
    }

    // A session whose waiting was answered, on the island or (its hook gone) in the tool: it works again until its next report
    // says otherwise.
    public bool Answered(string harness, string id, DateTimeOffset now)
    {
        string key = harness + "\u0001" + id;
        if (!_sessions.TryGetValue(key, out var session) || session.State is not (AgentState.Approval or AgentState.Question)) return false;
        _sessions[key] = session with { State = AgentState.Working, UpdatedAt = now, Call = null };
        return true;
    }

    // A session whose turn the user stopped, as its transcript tells (no hook does): it is idle.
    public bool Interrupted(string harness, string id, DateTimeOffset now)
    {
        string key = harness + "\u0001" + id;
        if (!_sessions.TryGetValue(key, out var session) || session.State is not (AgentState.Working or AgentState.Approval or AgentState.Question)) return false;
        _sessions[key] = session with { State = AgentState.Idle, UpdatedAt = now, Call = null };
        return true;
    }

    // Drops the silent sessions and turns finished ones idle. True when anything changed.
    public bool Age(DateTimeOffset now)
    {
        bool changed = false;
        foreach (var session in _sessions.Values.ToList())
        {
            var silent = now - session.UpdatedAt;
            if (silent > Silence || session.State == AgentState.Working && silent > WorkingSilence) { _sessions.Remove(session.Key); changed = true; }
            else if (session.State == AgentState.Done && silent >= DoneFor) { _sessions[session.Key] = session with { State = AgentState.Idle }; changed = true; }
        }
        return changed;
    }

    public void Clear() => _sessions.Clear();
}

// The key (NoticeMessage.ReplaceId) of the notice an agent's hook sends for an approval or a question, so that BrimDeck knows
// which session an answer on the island belongs to: "agent:<harness>:<session>". One session asks one thing at a time.
public static class AgentKeys
{
    private const string Prefix = "agent:", DonePrefix = "agent-done:";

    public static string For(string harness, string session) => BrimDeck.Protocol.AgentKeysWire.For(harness, session);

    public static (string Harness, string Session)? Ask(string? replaceKey) => Split(replaceKey, Prefix);

    // The tool any notice of an agent's hook is from: an approval or a question (Ask), or a finished turn ("agent-done:<harness>:
    // <session>", AgentKeysWire.Done). Null for every other notice.
    public static string? Harness(string? replaceKey) => (Split(replaceKey, Prefix) ?? Split(replaceKey, DonePrefix))?.Harness;

    private static (string Harness, string Session)? Split(string? replaceKey, string prefix)
    {
        if (replaceKey is null || !replaceKey.StartsWith(prefix, StringComparison.Ordinal)) return null;
        int split = replaceKey.IndexOf(':', prefix.Length);
        return split > prefix.Length && split < replaceKey.Length - 1 ? (replaceKey[prefix.Length..split], replaceKey[(split + 1)..]) : null;
    }
}
