using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Core.Agents;

namespace BrimDeck;

// The collapsed row's unread notices (MainWindow.NoticeStrip.cs) and coding agents (MainWindow.AgentStrip.cs) as the row places
// them: each in a place of its own, or, merged (DeckSettings.NoticeAgentsMerged), the agents just before the notices in the
// notices' place.
//
// Both are fixed parts, and a surface is never narrower than its fixed parts (CompactMusicLayout.RequiredWidth). With auto width
// the surface takes what they ask for. With a width of its own it keeps that width: they give up what does not fit, one step at a
// time (level), rather than widening it.
//   0: everything.
//   1: the agents say nothing in words; the state stays in the front icon's ring and badge.
//   2: one icon each: the agents' front session and the newest application with the total. Merged, only the one that most
//      needs the user: a waiting session, else the notices.
// A surface too short even for that widens as it does for any other fixed part.
public partial class MainWindow
{
    private const int AttentionLevels = 3;

    private bool AttentionMerged => Settings.NoticeAgentsMerged && Settings.ShowsNotices(EffectiveStyle) && Settings.ShowsAgents(EffectiveStyle);

    // The notices' and the agents' parts at a level; merged, both in the first, the second null.
    private (FrameworkElement? Notices, FrameworkElement? Agents) CompactAttention(double scale, int level)
    {
        bool merged = AttentionMerged;
        var agents = AgentStrip(scale, words: level == 0, single: level >= 2, merged: merged);
        var notices = NoticeStrip(scale, single: level >= 2);
        if (!merged || agents is null) return (notices, agents);
        if (notices is null) return (agents, null);
        if (level >= 2)
        {
            bool waits = AgentStacks.Of(AgentStripContent()) is { } stack && AgentStacks.Waits(stack.Front);
            if (waits) { _compactNotices = null; return (agents, null); }
            _compactAgents = null; return (notices, null);
        }
        double s = CompactMusicLayout.SafeScale(scale), gap = CompactMusicLayout.Whole(6 * s);
        var both = new Grid { Width = agents.Width + gap + notices.Width, Height = Math.Max(agents.Height, notices.Height), Background = System.Windows.Media.Brushes.Transparent };
        agents.HorizontalAlignment = HorizontalAlignment.Left; agents.VerticalAlignment = VerticalAlignment.Center;
        notices.HorizontalAlignment = HorizontalAlignment.Left; notices.VerticalAlignment = VerticalAlignment.Center; notices.Margin = new Thickness(agents.Width + gap, 0, 0, 0);
        both.Children.Add(agents); both.Children.Add(notices);
        return (both, null);
    }

    // The content width the surface has at the width it was given, before its content asks for more.
    private double GivenContentWidth()
        => CompactContentWidth(CollapsedGeometry.Resolve(Settings, EffectiveStyle, MonitorBoundsDip, WorkDip, _pixelsPerDip, null, 0, _scale));
}
