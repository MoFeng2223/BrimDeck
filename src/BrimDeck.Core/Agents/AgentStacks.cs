namespace BrimDeck.Core.Agents;

// What the collapsed row's coding agents say in words (AgentStack.Label).
public enum AgentLabel
{
    // One session waits for an approval, or for an answer to its question.
    Approval, Question,
    // More than one session waits for the user; Count says how many.
    Waiting,
    // One session works: the time since its task began (AgentSession.Since).
    Elapsed,
    // More than one session works; Count says how many.
    Working,
    // Only finished sessions are left.
    Done
}

// The collapsed row's coding agents: one round icon per session, at most MaxShown, stacked from left to right with the session
// that most needs the user at the right, on top. Front is that session, the only one whose state shows (its ring and badge);
// Label says in words what the sessions are doing, Count for the labels that count.
public sealed record AgentStack(IReadOnlyList<AgentSession> Shown, AgentSession Front, AgentLabel Label, int Count);

public static class AgentStacks
{
    public const int MaxShown = 3;

    // active: the sessions worth showing (AgentSessions.Active). Null when there are none.
    // The sessions are ordered by how much they need the user: waiting first (the one that has waited longest first), then
    // working, then finished (the latest first). A tool's sessions stand together, the tools in the order of their most
    // urgent session, so that the icons show how many each tool has.
    public static AgentStack? Of(IReadOnlyList<AgentSession> active)
    {
        if (active.Count == 0) return null;
        var urgent = active.OrderBy(s => AgentSessions.Rank(s.State)).ThenBy(s => Waits(s) ? s.UpdatedAt.UtcTicks : -s.UpdatedAt.UtcTicks).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();
        var order = urgent.GroupBy(s => s.Harness, StringComparer.Ordinal).SelectMany(tool => tool).ToList();
        var front = order[0];
        // Left to right: the least urgent of those shown first, the front at the right.
        var shown = order.Take(MaxShown).Reverse().ToList();
        int waiting = active.Count(Waits), working = active.Count(s => s.State == AgentState.Working);
        var (label, count) = waiting > 1 ? (AgentLabel.Waiting, waiting)
            : waiting == 1 ? (front.State == AgentState.Question ? AgentLabel.Question : AgentLabel.Approval, 1)
            : working > 1 ? (AgentLabel.Working, working)
            : working == 1 ? (AgentLabel.Elapsed, 1)
            : (AgentLabel.Done, active.Count);
        return new(shown, front, label, count);
    }

    public static bool Waits(AgentSession session) => session.State is AgentState.Approval or AgentState.Question;

    // The time since a task began as the row shows it: m:ss under an hour, h:mm:ss from then on.
    public static string Elapsed(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";
    }

    // The widest text Elapsed gives for times of the same number of digits, so the row keeps one width while the time counts.
    public static string ElapsedPattern(TimeSpan time) => new([.. Elapsed(time).Select(c => char.IsDigit(c) ? '0' : c)]);
}
