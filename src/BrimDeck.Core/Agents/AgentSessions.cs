using BrimDeck.Protocol;

namespace BrimDeck.Core.Agents;

// What a coding agent's session is doing, as its hooks report it (NoticeMessage of type "agent").
public enum AgentState { Started, Working, Approval, Question, Done, Idle, Ended }

// One session of a coding agent. Harness is the tool's id (AgentHarnessIds), Name the name it is shown by; Project the folder
// it works in (its last part); Prompt what it was last asked; Message what it said last.
public sealed record AgentSession(string Harness, string Name, string Id, AgentState State, string Project, string Prompt, string Message,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt)
{
    public string Key => Harness + "\u0001" + Id;
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
        var session = new AgentSession(harness, Text(message.AppName, before?.Name ?? harness), id, state,
            Text(message.Context, before?.Project ?? ""), Text(message.Title, before?.Prompt ?? ""), fresh ? Text(message.Body, "") : Text(message.Body, before?.Message ?? ""),
            before?.StartedAt ?? now, now);
        _sessions[key] = session;
        if (_sessions.Count > Limit)
            foreach (var old in _sessions.Values.OrderBy(s => s.UpdatedAt).Take(_sessions.Count - Limit).ToList()) _sessions.Remove(old.Key);
        return before is null || before with { UpdatedAt = now } != session;
    }

    // A session whose waiting the user answered on the island: it works again until its next report says otherwise.
    public bool Answered(string harness, string id, DateTimeOffset now)
    {
        string key = harness + "\u0001" + id;
        if (!_sessions.TryGetValue(key, out var session) || session.State is not (AgentState.Approval or AgentState.Question)) return false;
        _sessions[key] = session with { State = AgentState.Working, UpdatedAt = now };
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
    private const string Prefix = "agent:";

    public static string For(string harness, string session) => BrimDeck.Protocol.AgentKeysWire.For(harness, session);

    public static (string Harness, string Session)? Ask(string? replaceKey)
    {
        if (replaceKey is null || !replaceKey.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        int split = replaceKey.IndexOf(':', Prefix.Length);
        return split > Prefix.Length && split < replaceKey.Length - 1 ? (replaceKey[Prefix.Length..split], replaceKey[(split + 1)..]) : null;
    }
}
