using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.Agents;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// The marks an arriving notice shows that are not its application's icon, the same as the collapsed row's (CompactStacks):
// a call that rings, a green phone whose ring spreads out, with the time it has rung at the right; a call that has stopped, the
// phone in the danger color; a coding agent's approval or question, the tool's own icon in the waiting state's ring with its
// badge, and what it waits for at the right.
internal sealed partial class IslandNotice
{
    // Counts a ringing call's time once a second while it shows.
    private DispatcherTimer? _ringClock;
    private TextBlock? _ringTime;

    // The session a coding agent's notice waits for an answer from, and what it waits for; null for any other notice.
    private (string Harness, AgentState State)? AgentAsk(Notice? notice)
        => notice is { Awaited: true } && AgentKeys.Ask(notice.ReplaceKey) is var (harness, _)
            ? (harness, notice.Questions.Count > 0 ? AgentState.Question : AgentState.Approval) : null;

    private FrameworkElement? OwnMark(double size)
    {
        if (_item is not { Kind: AttentionKind.Notice } || Notice is not { } notice) return null;
        var look = PanelLook.Of(_deck);
        double s = _shape.Scale;
        if (notice.Call) return CompactStacks.Call(size, s, look, notice.SourcePresent, Soft.Enabled);
        if (AgentAsk(notice) is not var (harness, state)) return null;
        double diameter = CompactStacks.Diameter(s), whole = diameter + 2 * CompactStacks.RingPad(s);
        System.Windows.Media.Imaging.BitmapSource? icon = null;
        foreach (var iconRef in AgentIcons.For(harness))
        {
            if (Center.Icons.Get(iconRef, out _) is { } image) { icon = image; break; }
            if (Center.Icons.IsLoading(iconRef)) break;
        }
        var face = CompactStacks.Icon(icon, diameter, s, look, notice.AppName, PanelTheme.Readable(look.ColorOf("accent")));
        var ringed = CompactStacks.State(face, state, s, look, Soft.Enabled);
        // The ring reaches past the icon's room; it is placed around the room's middle and left unclipped.
        var room = new Canvas { Width = size, Height = size };
        Canvas.SetLeft(ringed, (size - whole) / 2); Canvas.SetTop(ringed, (size - whole) / 2);
        room.Children.Add(ringed);
        return room;
    }

    // Whether the icon that arrived is the one a coding agent's notice shows.
    private bool OwnMarkIcon(string iconRef) => AgentAsk(Notice) is var (harness, _) && AgentIcons.For(harness).Contains(iconRef, StringComparer.Ordinal);

    // The words at the right of the content, left of the buttons: a ringing call's time (in the success color, counting) or
    // what an agent waits for (in the caution color). Returns the room they take with the gap before them; none when the words
    // would be left less than about four characters.
    private double BuildTrailing(double scale, double left, double available, double gap)
    {
        _ringTime = null;
        _ringClock?.Stop();
        if (_item is not { Kind: AttentionKind.Notice } || Notice is not { } notice || _part is not null) return 0;
        var look = PanelLook.Of(_deck);
        string text, color, widest;
        if (notice.Call && notice.SourcePresent)
        {
            var time = DateTimeOffset.UtcNow - notice.CreatedAt;
            text = AgentStacks.Elapsed(time); widest = AgentStacks.ElapsedPattern(time); color = look.Hex("success");
        }
        else if (AgentAsk(notice) is var (_, state))
        {
            text = widest = state == AgentState.Question ? Loc.T("待回答", "Question") : Loc.T("待批准", "Approval"); color = look.Hex("caution");
        }
        else return 0;
        var words = new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = 12 * scale, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(color), TextAlignment = TextAlignment.Right };
        Typography.SetNumeralAlignment(words, FontNumeralAlignment.Tabular);
        var measure = new TextBlock { Text = widest, FontFamily = words.FontFamily, FontSize = words.FontSize, FontWeight = words.FontWeight };
        Typography.SetNumeralAlignment(measure, FontNumeralAlignment.Tabular);
        measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Math.Ceiling(measure.DesiredSize.Width) + 1;
        if (available - width - gap < 64 * scale) return 0;
        words.Width = width;
        words.Measure(new Size(width, double.PositiveInfinity));
        Canvas.SetLeft(words, left + available - width); Canvas.SetTop(words, (_island.Height - words.DesiredSize.Height) / 2);
        _content.Children.Add(words);
        if (notice.Call && notice.SourcePresent)
        {
            _ringTime = words;
            _ringClock ??= RingClock();
            _ringClock.Start();
        }
        return width + gap;
    }

    private DispatcherTimer RingClock()
    {
        var clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) =>
        {
            if (_leaving || _ringTime is null || Notice is not { Call: true, SourcePresent: true } notice) { clock.Stop(); return; }
            var time = DateTimeOffset.UtcNow - notice.CreatedAt;
            // A time that needs another digit lays the content out again for the wider time.
            if (AgentStacks.ElapsedPattern(time).Length != AgentStacks.ElapsedPattern(time - TimeSpan.FromSeconds(1)).Length) { BuildContent(); return; }
            _ringTime.Text = AgentStacks.Elapsed(time);
        };
        return clock;
    }
}
