using System.Windows.Threading;
using BrimDeck.Core.Agents;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

namespace BrimDeck;

// The coding agents' sessions as their hooks report them over the notices' pipe (brim hook, type "agent"). The collapsed row
// shows them (CompactPart.Notices); their approvals and questions come as notices that wait for an answer or only tell
// (NoticeCenter), and are closed here once the session moved on. Kept in memory only.
//
// What no hook tells is read from the session's transcript, where the tool keeps one in Claude Code's form (AgentTranscripts):
// a turn the user stopped. Only the sessions that work or wait are read, a few seconds apart, and only what was added since
// the session last said something new.
internal sealed class AgentCenter
{
    private readonly AgentSessions _sessions = new();
    private readonly NoticeCenter _notices;
    private readonly DispatcherTimer _age = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(3) };
    // Where each session's transcript was read up to, by the session's key.
    private readonly Dictionary<string, TranscriptTail> _tails = new(StringComparer.Ordinal);
    private bool _reading;

    public event Action? Changed;

    public AgentCenter(NoticeCenter notices)
    {
        _notices = notices;
        notices.AgentReport = Report;
        // Answered on the island: the session works again at once, before its next hook says so. "Use the terminal" hands the
        // ask back to the tool, which then asks in its own window: the session still waits.
        notices.Answered += (notice, key) => { if (key != AgentHook.Terminal) Moved(notice); };
        // The hook went away before an answer here: the tool ended it, having been answered in its own window.
        notices.Abandoned += Moved;
        _age.Tick += (_, _) => { if (_sessions.Age(DateTimeOffset.UtcNow)) Changed?.Invoke(); UpdateTimers(); };
        _watch.Tick += async (_, _) => await ReadTranscriptsAsync();
    }

    public IReadOnlyList<AgentSession> All => _sessions.All;
    public IReadOnlyList<AgentSession> Active => _sessions.Active(DateTimeOffset.UtcNow);

    private static bool Waits(AgentSession? session) => session?.State is AgentState.Approval or AgentState.Question;
    private static bool Busy(AgentSession? session) => session?.State is AgentState.Working or AgentState.Approval or AgentState.Question;

    private void Moved(Notice notice)
    {
        if (AgentKeys.Ask(notice.ReplaceKey) is var (harness, session) && _sessions.Answered(harness, session, DateTimeOffset.UtcNow)) Changed?.Invoke();
    }

    private NoticeMessage Report(NoticeMessage message)
    {
        string harness = (message.AppId ?? "").Trim(), id = (message.Session ?? "").Trim();
        var before = _sessions.Find(harness, id);
        if (_sessions.Apply(message, DateTimeOffset.UtcNow)) Changed?.Invoke();
        var session = _sessions.Find(harness, id);
        // The session no longer waits for the user (it ran the tool, took a new prompt, finished, ended): the notice of its
        // approval or question leaves the island, and a hook that still waits for an answer here is let go.
        if (!Waits(session)) _notices.CloseAsk(AgentKeys.For(harness, id));
        // A new prompt, or work after a rest: what the transcript said before is of an earlier turn.
        if (session is null) _tails.Remove(harness + "\u0001" + id);
        else if (message.Title is { Length: > 0 } && session.State == AgentState.Working || !Busy(before) || !_tails.ContainsKey(session.Key)) Follow(session);
        UpdateTimers();
        return NoticeProtocol.Reply(NoticeResults.Accepted, null, message.EventId);
    }

    // Starts reading the session's transcript from its end. Only a file on a local drive is read: a path inside a WSL
    // distribution is none of Windows', and a network path is never opened on a sender's word.
    private void Follow(AgentSession session)
    {
        _tails.Remove(session.Key);
        if (session.Transcript is not { Length: > 3 } file || !char.IsAsciiLetter(file[0]) || file[1] != ':' || file[2] is not ('\\' or '/')) return;
        long length = AgentTranscripts.Length(file);
        if (length >= 0) _tails[session.Key] = new TranscriptTail(file, length);
    }

    private async Task ReadTranscriptsAsync()
    {
        if (_reading) return;
        _reading = true;
        try
        {
            var reads = _sessions.All.Where(Busy).Select(session => (Session: session, Tail: _tails.GetValueOrDefault(session.Key))).Where(read => read.Tail is not null).ToList();
            if (reads.Count == 0) return;
            var stopped = await Task.Run(() => reads.Where(read => Stopped(read.Tail!)).ToList());
            bool changed = false;
            foreach (var (session, tail) in stopped)
            {
                // Read anew since (a new prompt came meanwhile): what was read is of the turn before.
                if (!ReferenceEquals(_tails.GetValueOrDefault(session.Key), tail) || !_sessions.Interrupted(session.Harness, session.Id, DateTimeOffset.UtcNow)) continue;
                _notices.CloseAsk(AgentKeys.For(session.Harness, session.Id));
                changed = true;
            }
            if (changed) Changed?.Invoke();
        }
        finally { _reading = false; UpdateTimers(); }
    }

    // Whether what was added to the transcript says that the user stopped the turn.
    private static bool Stopped(TranscriptTail tail)
    {
        bool stopped = false;
        foreach (var line in tail.Read())
        {
            if (!line.Contains(AgentTranscripts.InterruptMark, StringComparison.Ordinal)) continue;
            using var record = AgentTranscripts.Record(line);
            stopped |= record is not null && AgentTranscripts.Interrupted(record.RootElement);
        }
        return stopped;
    }

    // The timers run only while there is a session to age, and one whose transcript is read.
    private void UpdateTimers()
    {
        var all = _sessions.All;
        if (all.Count > 0 && !_age.IsEnabled) _age.Start(); else if (all.Count == 0) _age.Stop();
        foreach (var key in _tails.Keys.Where(key => all.All(session => session.Key != key)).ToList()) _tails.Remove(key);
        bool watch = all.Any(session => Busy(session) && _tails.ContainsKey(session.Key));
        if (watch && !_watch.IsEnabled) _watch.Start(); else if (!watch) _watch.Stop();
    }

    public void Clear() { _sessions.Clear(); _tails.Clear(); UpdateTimers(); Changed?.Invoke(); }
}
