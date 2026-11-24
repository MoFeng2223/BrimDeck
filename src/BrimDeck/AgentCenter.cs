using System.Windows.Threading;
using BrimDeck.Core.Agents;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

namespace BrimDeck;

// The coding agents' sessions as their hooks report them over the notices' pipe (brim hook, type "agent"). The collapsed row
// shows them (CompactPart.Agents); their approvals and questions come as notices that wait for an answer (NoticeCenter). Kept in
// memory only.
internal sealed class AgentCenter
{
    private readonly AgentSessions _sessions = new();
    private readonly DispatcherTimer _age = new() { Interval = TimeSpan.FromMinutes(1) };

    public event Action? Changed;

    public AgentCenter(NoticeCenter notices)
    {
        notices.AgentReport = Report;
        // Answered on the island: the session works again at once, before its next hook says so.
        notices.Answered += notice =>
        {
            if (AgentKeys.Ask(notice.ReplaceKey) is var (harness, session) && _sessions.Answered(harness, session, DateTimeOffset.UtcNow)) Changed?.Invoke();
        };
        _age.Tick += (_, _) => { if (_sessions.Age(DateTimeOffset.UtcNow)) Changed?.Invoke(); UpdateTimer(); };
    }

    public IReadOnlyList<AgentSession> All => _sessions.All;
    public IReadOnlyList<AgentSession> Active => _sessions.Active(DateTimeOffset.UtcNow);

    private NoticeMessage Report(NoticeMessage message)
    {
        if (_sessions.Apply(message, DateTimeOffset.UtcNow)) Changed?.Invoke();
        UpdateTimer();
        return NoticeProtocol.Reply(NoticeResults.Accepted, null, message.EventId);
    }

    // The timer runs only while there is a session to age.
    private void UpdateTimer()
    {
        bool any = _sessions.All.Count > 0;
        if (any && !_age.IsEnabled) _age.Start(); else if (!any) _age.Stop();
    }

    public void Clear() { _sessions.Clear(); UpdateTimer(); Changed?.Invoke(); }
}
