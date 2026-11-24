using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.Agents;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// The collapsed row's coding agents (CompactPart.Agents): a mark for each session that works, waits for the user or finished a
// short while ago (the tool's first letter, with a dot in the state's color), and the state of the one in front in words.
// Nothing shows without such a session. Pointed at, the panel opens on the notices page, where approvals and questions are
// answered.
public partial class MainWindow
{
    // At most this many sessions' marks; the words name the first.
    private const int AgentStripMarks = 3;
    private FrameworkElement? _compactAgents;
    private string _agentStripShown = "";
    private bool _agentsSubscribed;

    private IReadOnlyList<AgentSession> AgentStripContent()
        => _app.Agents is { } agents && Settings.ShowsAgents(EffectiveStyle) ? agents.Active : [];

    private static string AgentStripKey(IReadOnlyList<AgentSession> sessions) => string.Join('|', sessions.Take(AgentStripMarks).Select(s => s.Key + "/" + s.State)) + "#" + sessions.Count;

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
    }

    // A session changed: the collapsed row is built again when the badge would show otherwise. While the panel is open nothing
    // is built; closing it builds the row.
    private void AgentStripChanged()
    {
        if (_expanded) return;
        if (AgentStripKey(AgentStripContent()) != _agentStripShown) RenderCompact();
    }

    // The state's color: waiting for the user in the caution color, working in the accent, finished in the success color.
    internal static string AgentStateColor(AgentState state) => state switch
    {
        AgentState.Approval or AgentState.Question => PanelTheme.Hex("caution"),
        AgentState.Working or AgentState.Started => PanelTheme.Hex("accent"),
        AgentState.Done => PanelTheme.Hex("success"),
        _ => UI.Tertiary
    };

    internal static string AgentStateName(AgentState state) => state switch
    {
        AgentState.Approval => Loc.T("待审批", "Approval"),
        AgentState.Question => Loc.T("待回答", "Question"),
        AgentState.Working => Loc.T("运行中", "Working"),
        AgentState.Started => Loc.T("已启动", "Started"),
        AgentState.Done => Loc.T("已完成", "Done"),
        AgentState.Idle => Loc.T("空闲", "Idle"),
        _ => Loc.T("已结束", "Ended")
    };

    private FrameworkElement? AgentStrip(double scale)
    {
        var sessions = AgentStripContent();
        _agentStripShown = AgentStripKey(sessions); _compactAgents = null;
        if (sessions.Count == 0) return null;
        double s = CompactMusicLayout.SafeScale(scale), mark = CompactMusicLayout.Whole(14 * s), height = CompactMusicLayout.Whole(16 * s);
        double gap = CompactMusicLayout.Whole(4 * s), between = CompactMusicLayout.Whole(2 * s);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var badge = new Grid { Height = height, Background = Brushes.Transparent };
        double left = 0;
        foreach (var session in sessions.Take(AgentStripMarks))
        {
            var plate = AgentMark(session, mark);
            plate.HorizontalAlignment = HorizontalAlignment.Left; plate.VerticalAlignment = VerticalAlignment.Center; plate.IsHitTestVisible = false;
            plate.Margin = new Thickness(left, 0, 0, 0);
            badge.Children.Add(plate);
            left += mark + between;
        }
        var first = sessions[0];
        var words = new TextBlock
        {
            Text = AgentStateName(first.State), FontFamily = UI.PanelFont, FontSize = CompactMusicLayout.Half(11.5 * s), FontWeight = FontWeights.SemiBold,
            Foreground = UI.Brush(first.State is AgentState.Approval or AgentState.Question ? AgentStateColor(first.State) : Look.Primary),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap
        };
        words.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double start = DevicePixels.Up(left - between + gap, dpi);
        words.Margin = new Thickness(start, 0, 0, 0);
        badge.Children.Add(words);
        badge.Width = DevicePixels.Up(start + words.DesiredSize.Width, dpi);
        string tip = string.Join("\n", sessions.Select(session => session.Name + (session.Project.Length > 0 ? " · " + session.Project : "") + " · " + AgentStateName(session.State)));
        badge.ToolTip = tip; AutomationProperties.SetName(badge, tip.Replace("\n", Loc.T("；", "; ")));
        _compactAgents = badge;
        return badge;
    }

    // The tool's first letter on a plate in its color, with a dot in the state's color at its lower right.
    private FrameworkElement AgentMark(AgentSession session, double size)
    {
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(NoticeLetter.Plate(session.Name, size, PanelTheme.Readable(AgentColor(session.Harness))));
        double dot = Math.Max(4, Math.Round(size * .42));
        grid.Children.Add(new Ellipse
        {
            Width = dot, Height = dot, Fill = UI.Brush(AgentStateColor(session.State)), Stroke = UI.Brush(Look.Hex("surface")), StrokeThickness = Math.Max(1, dot * .22),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -dot * .25, -dot * .25)
        });
        return grid;
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
