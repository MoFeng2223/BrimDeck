using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.Agents;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// The collapsed row's live notices (CompactPart.Notices): the unread notices (MainWindow.NoticeStrip.cs) and the coding agents'
// sessions (MainWindow.AgentStrip.cs, DeckSettings.NoticeStripAgents), in one place. Apart (the default) they are two groups side
// by side, the agents first, each with its own words; merged (DeckSettings.NoticeAgentsMerged) the sessions' and the
// applications' icons stack in one group, the one that most needs the user in front, with one word or number after it.
//
// The part is a fixed part, and a surface is never narrower than its fixed parts (CompactMusicLayout.RequiredWidth). With auto
// width the surface takes what it asks for. With a width of its own it keeps that width: the part gives up what does not fit, one
// step at a time (level), rather than widening it.
//   0: everything.
//   1: the agents say nothing in words; their state stays in the front icon's ring and badge. The unread total stays.
//   2: one icon for each group (merged: the front icon), with the unread total.
// A surface too short even for that widens as it does for any other fixed part.
public partial class MainWindow
{
    private const int AttentionLevels = 3;

    private FrameworkElement? CompactAttention(double scale, int level)
    {
        if (Settings.NoticeAgentsMerged) return MergedAttention(scale, level);
        var agents = AgentStrip(scale, words: level == 0, single: level >= 2);
        var notices = NoticeStrip(scale, single: level >= 2);
        if (agents is null || notices is null) return agents ?? notices;
        double s = CompactMusicLayout.SafeScale(scale), gap = CompactMusicLayout.Whole(8 * s);
        var both = new Grid { Width = agents.Width + gap + notices.Width, Height = Math.Max(agents.Height, notices.Height), Background = Brushes.Transparent };
        agents.HorizontalAlignment = HorizontalAlignment.Left; agents.VerticalAlignment = VerticalAlignment.Center;
        notices.HorizontalAlignment = HorizontalAlignment.Left; notices.VerticalAlignment = VerticalAlignment.Center; notices.Margin = new Thickness(agents.Width + gap, 0, 0, 0);
        both.Children.Add(agents); both.Children.Add(notices);
        return both;
    }

    // One stack of the sessions and the applications, most urgent first: the sessions waiting for the user, an unread call, the
    // applications with unread notices (newest first), the working sessions, the finished ones. Its words: what the waiting
    // sessions wait for, then the unread total; without either, what the agents are doing.
    private FrameworkElement? MergedAttention(double scale, int level)
    {
        var sessions = AgentStripContent();
        var (count, apps) = NoticeStripContent();
        _agentStripShown = AgentStripKey(sessions); _noticeStripShown = NoticeStripKey(count, apps);
        _compactAgents = null; _compactNotices = null; _agentElapsed = null;
        var stack = AgentStacks.Of(sessions);
        if (stack is null && count == 0) { StopAgentClock(); return null; }
        double s = CompactMusicLayout.SafeScale(scale), dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var order = AgentStacks.Order(sessions);
        // Each session's icon in its own state's ring; the applications' icons plain.
        var items = new List<Func<(FrameworkElement, double)>>();
        void Session(AgentSession session) => items.Add(() => (CompactStacks.State(AgentIcon(session, s), session.State, s, Look, Animates), CompactStacks.RingPad(s)));
        foreach (var session in order.Where(AgentStacks.Waits)) Session(session);
        if (count > 0)
        {
            if (apps.Count == 0) items.Add(() => (CompactStacks.Plate(CompactStacks.Diameter(s), s, Look, NoticeBadges.Glyph(NoticeGlyph.Bell, Math.Round(CompactStacks.Diameter(s) * .5), Look.ColorOf("textPrimary"))), 0));
            foreach (var notice in apps) items.Add(() => (NoticeStripIcon(notice, s), 0));
        }
        foreach (var session in order.Where(session => !AgentStacks.Waits(session))) Session(session);
        // Built front first, so that the front's icon is read before the others; laid out front last, at the right.
        var icons = items.Take(level >= 2 ? 1 : AgentStacks.MaxShown).Select(make => make()).Reverse().ToList();
        var stacked = CompactStacks.Stack(icons, s);
        // The words: a waiting session's (with level 0), then the total; else, with nothing unread, the agents' doing.
        var words = new TextBlock { FontFamily = UI.PanelFont, FontSize = CompactMusicLayout.Half(11.5 * s), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        Typography.SetNumeralAlignment(words, FontNumeralAlignment.Tabular);
        string? reserve = null;
        bool waits = stack is { Label: AgentLabel.Approval or AgentLabel.Question or AgentLabel.Waiting };
        if (waits && level == 0) words.Inlines.Add(new Run(AgentWords(stack!)) { Foreground = UI.Brush(Look.Hex("caution")) });
        if (count > 0) words.Inlines.Add(new Run((words.Inlines.Count > 0 ? "  " : "") + (count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture))) { Foreground = UI.Brush(Look.Primary) });
        else if (!waits && stack is not null && level == 0)
        {
            words.Inlines.Add(new Run(AgentWords(stack)) { Foreground = UI.Brush(Look.Secondary) });
            if (stack.Label == AgentLabel.Elapsed)
            {
                _agentElapsed = words; _agentSince = stack.Front.Since;
                reserve = _agentElapsedPattern = AgentStacks.ElapsedPattern(DateTimeOffset.UtcNow - _agentSince);
            }
        }
        if (_agentElapsed is null) StopAgentClock(); else StartAgentClock();
        var badge = CompactStacks.Row(stacked, words.Inlines.Count > 0 ? words : null, s, dpi, reserve);
        var lines = new List<string>();
        if (count > 0) lines.Add(Loc.T($"{count} 条未读通知", Loc.Count(count, "unread notice", "unread notices")) + (apps.Count > 0 ? Loc.T("：", ": ") + string.Join(Loc.T("、", ", "), apps.Select(notice => notice.AppName)) : ""));
        lines.AddRange(sessions.Select(session => session.Name + (session.Project.Length > 0 ? " · " + session.Project : "") + " · " + AgentStateName(session.State)));
        string tip = string.Join("\n", lines);
        badge.ToolTip = tip; AutomationProperties.SetName(badge, tip.Replace("\n", Loc.T("；", "; ")));
        _compactNotices = badge;
        return badge;
    }

    // What the agents are doing in words (AgentStack.Label).
    private static string AgentWords(AgentStack stack) => stack.Label switch
    {
        AgentLabel.Approval => Loc.T("待批准", "Approval"),
        AgentLabel.Question => Loc.T("待回答", "Question"),
        AgentLabel.Waiting => Loc.T($"{stack.Count} 个待处理", $"{stack.Count} waiting"),
        AgentLabel.Working => Loc.T($"{stack.Count} 个运行中", $"{stack.Count} working"),
        AgentLabel.Elapsed => AgentStacks.Elapsed(DateTimeOffset.UtcNow - stack.Front.Since),
        _ => Loc.T("已完成", "Done")
    };

    // The content width the surface has at the width it was given, before its content asks for more.
    private double GivenContentWidth()
        => CompactContentWidth(CollapsedGeometry.Resolve(Settings, EffectiveStyle, MonitorBoundsDip, WorkDip, _pixelsPerDip, null, 0, _scale));
}
