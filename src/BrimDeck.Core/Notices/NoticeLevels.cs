namespace BrimDeck.Core.Notices;

// What the island plays for a notice: nothing (it only shows the words), the indicator's one brightening, the backlight
// alone (the indicator: its breathing bloom), the backlight with the liquid, or the liquid alone.
public enum NoticeEffect { Still, Brighten, Glow, Liquid, LiquidOnly }

// How strongly a notice shows on the island (NoticeLevel), from what the notice is and what its sender asked. The levels are
// fixed: the user chooses no level, only whether an application's notices show on the island at all (NoticeAppRule.Level Silent).
public static class NoticeLevels
{
    // In order: the user's rule for the application says "岛上提示" is off (Silent); a call, a ringing alarm, a countdown that ended,
    // an event's reminder or an approval stays until handled (level 3); the level the sender asked for (BrimDeck's own features,
    // the command line, a plug-in); else level 2. A rule's "接收" off never gets here: those notices are not received (NoticeHub).
    // Persistent is only for a notice still at its source whose time was not stopped: with nothing left to handle it shows once,
    // as level 2 does.
    public static NoticeLevel Resolve(Notice notice, NoticeAppRule? rule, DateTimeOffset now)
    {
        var level = Decide(notice, rule, now);
        return level == NoticeLevel.Persistent && (!notice.SourcePresent || notice.Timing is { Stopped: true }) ? NoticeLevel.Glow : level;
    }

    private static NoticeLevel Decide(Notice notice, NoticeAppRule? rule, DateTimeOffset now)
    {
        if (rule?.Level == NoticeLevel.Silent) return NoticeLevel.Silent;
        // One whose sender waits for an answer (Notice.Awaited: an approval) stays as a call does.
        if (notice.Call || notice.Awaited || NoticeClock.Alerting(notice.Timing, now)) return NoticeLevel.Persistent;
        if (notice.Level == NoticeLevel.Silent) return NoticeLevel.Silent;
        return notice.Level switch
        {
            NoticeLevel.Plain => NoticeLevel.Plain, NoticeLevel.Persistent => NoticeLevel.Persistent, _ => NoticeLevel.Glow
        };
    }

    // What the island plays. subdued: the notice shows beyond the rate limit, so it shows as a plain one. liquid: the style of a
    // notice's level asks for the liquid (NoticeStyle.Liquid); null for an item without a style (a quota alert, a track change),
    // where Lively plays the liquid and Glow the backlight alone. light: the style asks for the backlight (NoticeStyle.Light);
    // false with the liquid plays the liquid alone. The indicator has no room for words: a plain notice lights its line once,
    // without the bloom.
    public static NoticeEffect Effect(NoticeLevel level, bool subdued, IslandForm form, bool? liquid = null, bool light = true) => (subdued ? NoticeLevel.Plain : level) switch
    {
        NoticeLevel.Glow or NoticeLevel.Lively or NoticeLevel.Persistent when liquid is { } chosen => !chosen ? NoticeEffect.Glow : light ? NoticeEffect.Liquid : NoticeEffect.LiquidOnly,
        NoticeLevel.Lively or NoticeLevel.Persistent => NoticeEffect.Liquid,
        NoticeLevel.Glow => NoticeEffect.Glow,
        NoticeLevel.Plain when form == IslandForm.Line => NoticeEffect.Brighten,
        _ => NoticeEffect.Still
    };

    // How long the effect plays, in seconds, without the liquid (whose model knows its own length).
    public static double EffectLength(NoticeEffect effect, IslandForm form) => effect switch
    {
        NoticeEffect.Brighten => IslandLiquid.Breath,
        NoticeEffect.Glow => form == IslandForm.Line ? 2 * IslandLiquid.Breath : IslandLiquid.GlowLength,
        _ => 0
    };
}

// Where a notice's icon and words sit in the island (NoticeAlign).
public static class NoticeAligns
{
    // How far the group moves right from the left of the room it has, in whole DIPs: none of what is left over, half of it or
    // all. A group that fills the room or is longer stays at the left, where its words start scrolling.
    public static double Shift(double room, double content, NoticeAlign align)
        => content >= room ? 0 : Math.Floor((room - content) * (align switch { NoticeAlign.Left => 0, NoticeAlign.Center => .5, _ => 1 }));
}
