using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.Agents;

namespace BrimDeck;

// The collapsed row's coding agents (CompactPart.Agents): a round icon per session that works, waits for the user or finished a
// short while ago, each tool's own program icon, stacked as the notices' applications are (AgentStacks: at most three, the
// session that most needs the user at the right, on top). That front icon carries its session's state (CompactStacks.State),
// and the words after the stack say what the sessions are doing: what one waits for, how many wait, the time one has worked,
// how many work. Nothing shows without such a session. Pointed at, the panel opens on the notices page, where approvals and
// questions are answered.
public partial class MainWindow
{
    private FrameworkElement? _compactAgents;
    private string _agentStripShown = "";
    private bool _agentsSubscribed;
    // The running time the row shows (AgentLabel.Elapsed), counted from when, and the widest form its width was kept for.
    private TextBlock? _agentElapsed;
    private DateTimeOffset _agentSince;
    private string _agentElapsedPattern = "";
    private DispatcherTimer? _agentClock;

    private IReadOnlyList<AgentSession> AgentStripContent()
        => _app.Agents is { } agents && Settings.ShowsAgents(EffectiveStyle) ? agents.Active : [];

    private static string AgentStripKey(IReadOnlyList<AgentSession> sessions)
        => string.Join('|', sessions.Select(s => s.Key + "/" + s.State + "/" + s.Since.UtcTicks));

    private void SubscribeAgents()
    {
        if (_agentsSubscribed || _app.Agents is null) return;
        _agentsSubscribed = true;
        _app.Agents.Changed += AgentStripChanged;
    }

    private void UnsubscribeAgents()
    {
        if (_agentsSubscribed && _app.Agents is not null) _app.Agents.Changed -= AgentStripChanged;
        _agentsSubscribed = false;
        StopAgentClock();
    }

    // A session changed: the collapsed row is built again when the stack would show otherwise. While the panel is open nothing
    // is built; closing it builds the row.
    private void AgentStripChanged()
    {
        if (_expanded) return;
        if (AgentStripKey(AgentStripContent()) != _agentStripShown) RenderCompact();
    }

    internal static string AgentStateName(AgentState state) => state switch
    {
        AgentState.Approval => Loc.T("待批准", "Approval"),
        AgentState.Question => Loc.T("待回答", "Question"),
        AgentState.Working => Loc.T("运行中", "Working"),
        AgentState.Started => Loc.T("已启动", "Started"),
        AgentState.Done => Loc.T("已完成", "Done"),
        AgentState.Idle => Loc.T("空闲", "Idle"),
        _ => Loc.T("已结束", "Ended")
    };

    // The agents' group of the live notices shown apart (MainWindow.CompactAttention.cs). words: whether it says in words what the
    // sessions are doing. single: only the front icon (CompactAttention's last step).
    private FrameworkElement? AgentStrip(double scale, bool words = true, bool single = false)
    {
        var sessions = AgentStripContent();
        _agentStripShown = AgentStripKey(sessions); _compactAgents = null; _agentElapsed = null;
        if (AgentStacks.Of(sessions) is not { } stack) { StopAgentClock(); return null; }
        double s = CompactMusicLayout.SafeScale(scale), dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // Every session's icon shows its own state: three working sessions turn three rings.
        IReadOnlyList<AgentSession> shown = single ? [stack.Front] : stack.Shown;
        var stacked = CompactStacks.Stack([.. shown.Select(session => (CompactStacks.State(AgentIcon(session, s), session.State, s, Look, Animates), CompactStacks.RingPad(s)))], s);
        TextBlock? text = null;
        string? reserve = null;
        bool waits = stack.Label is AgentLabel.Approval or AgentLabel.Question or AgentLabel.Waiting;
        if (words)
        {
            string said = stack.Label switch
            {
                AgentLabel.Approval => Loc.T("待批准", "Approval"),
                AgentLabel.Question => Loc.T("待回答", "Question"),
                AgentLabel.Waiting => Loc.T($"{stack.Count} 个待处理", $"{stack.Count} waiting"),
                AgentLabel.Working => Loc.T($"{stack.Count} 个运行中", $"{stack.Count} working"),
                AgentLabel.Elapsed => AgentStacks.Elapsed(DateTimeOffset.UtcNow - stack.Front.Since),
                _ => Loc.T("已完成", "Done")
            };
            text = CompactStacks.Words(said, s, waits ? Look.Hex("caution") : Look.Secondary);
            Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            if (stack.Label == AgentLabel.Elapsed)
            {
                _agentElapsed = text; _agentSince = stack.Front.Since;
                reserve = _agentElapsedPattern = AgentStacks.ElapsedPattern(DateTimeOffset.UtcNow - _agentSince);
            }
        }
        if (_agentElapsed is null) StopAgentClock(); else StartAgentClock();
        var badge = CompactStacks.Row(stacked, text, s, dpi, reserve);
        string tip = string.Join("\n", sessions.Select(session => session.Name + (session.Project.Length > 0 ? " · " + session.Project : "") + " · " + AgentStateName(session.State)));
        badge.ToolTip = tip; AutomationProperties.SetName(badge, tip.Replace("\n", Loc.T("；", "; ")));
        _compactAgents = badge;
        return badge;
    }

    // The running time counts once a second; when it needs another digit, the row is built again for the wider time.
    private void StartAgentClock()
    {
        if (_agentClock is null)
        {
            _agentClock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _agentClock.Tick += (_, _) =>
            {
                if (_agentElapsed is not { } shown || _expanded) { StopAgentClock(); return; }
                var time = DateTimeOffset.UtcNow - _agentSince;
                if (AgentStacks.ElapsedPattern(time) != _agentElapsedPattern) { RenderCompact(); return; }
                shown.Text = AgentStacks.Elapsed(time);
            };
        }
        if (!_agentClock.IsEnabled) _agentClock.Start();
    }

    private void StopAgentClock() => _agentClock?.Stop();

    // The tool's own program icon (AgentIcons), round; while it is read or when the tool has none, its first letter in its color.
    private FrameworkElement AgentIcon(AgentSession session, double s)
    {
        double size = CompactStacks.Diameter(s);
        var color = PanelTheme.Readable(AgentColor(session.Harness));
        if (_app.Notices?.Icons is not { } icons || AgentIcons.Ref(session.Harness) is not { } iconRef) return CompactStacks.Icon(null, size, s, Look, session.Name, color);
        // Still being read: the letter for now, the icon once it arrives (NoticeStripIconLoaded).
        _noticeStripIcons.Add(iconRef);
        return CompactStacks.Icon(icons.Get(iconRef, out _), size, s, Look, session.Name, color);
    }

    // The tool's color as the usage page gives it; a tool connected by hand takes a gray.
    private static Color AgentColor(string harness)
    {
        var provider = harness switch
        {
            "claude" => ProviderId.Claude, "codex" => ProviderId.Codex, "cursor" => ProviderId.Cursor, "antigravity" => ProviderId.Antigravity, "codeagent" => ProviderId.CodeAgent,
            "qoder" => ProviderId.Qoder, "qoder-cn" => ProviderId.QoderCn, "workbuddy" => ProviderId.WorkBuddy, "workbuddy-ai" => ProviderId.WorkBuddyAi, "zcode" => ProviderId.ZCode,
            "opencode" => ProviderId.OpenCode, "kimi" => ProviderId.Kimi, "commandcode" => ProviderId.CommandCode, "mimo" => ProviderId.MiMo, "trae" => ProviderId.Trae, "trae-cn" => ProviderId.TraeCn,
            _ => (ProviderId?)null
        };
        try { return (Color)ColorConverter.ConvertFromString(provider is { } id ? AppPresets.ThemeColor(id) : "#8E8E93"); }
        catch (FormatException) { return Colors.Gray; }
    }

    private bool PointerOnAgents() => PointerOnPart(_compactAgents);
}
