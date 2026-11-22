namespace BrimDeck.Core.Notices;

// What the island plays for a notice: nothing (it only shows the words), the indicator's one brightening, the backlight
// alone (the indicator: its breathing bloom), or the backlight with the liquid.
public enum NoticeEffect { Still, Brighten, Glow, Liquid }

// How strongly a notice shows on the island (NoticeLevel), from the user's rule, what the notice is and what its sender asked.
public static class NoticeLevels
{
    // In order: the user's rule for the application says "只进通知页" (Silent); a call, a ringing alarm, a countdown that ended or an
    // event's reminder stays until handled (level 3); the level the user chose for the application; the level the sender asked
    // for; the default level of the settings (fallback: Plain, Glow or Persistent). A rule's "不提示" never gets here: those notices
    // are not received (NoticeHub). Persistent is only for a notice still at its source whose time was not stopped: with nothing
    // left to handle it shows once, as level 2 does.
    public static NoticeLevel Resolve(Notice notice, NoticeAppRule? rule, NoticeLevel fallback, DateTimeOffset now)
    {
        var level = Decide(notice, rule, fallback, now);
        return level == NoticeLevel.Persistent && (!notice.SourcePresent || notice.Timing is { Stopped: true }) ? NoticeLevel.Glow : level;
    }

    private static NoticeLevel Decide(Notice notice, NoticeAppRule? rule, NoticeLevel fallback, DateTimeOffset now)
    {
        if (rule?.Level == NoticeLevel.Silent) return NoticeLevel.Silent;
        // One whose sender waits for an answer (Notice.Awaited: an approval) stays as a call does.
        if (notice.Call || notice.Awaited || NoticeClock.Alerting(notice.Timing, now)) return NoticeLevel.Persistent;
        if (Chosen(rule?.Level ?? NoticeLevel.Auto) is { } chosen) return chosen;
        if (notice.Level == NoticeLevel.Silent) return NoticeLevel.Silent;
        return Chosen(notice.Level) ?? Default(fallback);
    }

    // A level the user or a sender may choose for a notice, as one of the three; null for Auto (and Silent, decided apart).
    private static NoticeLevel? Chosen(NoticeLevel level) => level switch
    {
        NoticeLevel.Plain => NoticeLevel.Plain, NoticeLevel.Glow or NoticeLevel.Lively => NoticeLevel.Glow, NoticeLevel.Persistent => NoticeLevel.Persistent, _ => null
    };

    // The setting's value as it may be used: Plain, Glow or Persistent (the older Lively is Glow).
    public static NoticeLevel Default(NoticeLevel level) => Chosen(level) ?? NoticeLevel.Glow;

    // What the island plays. subdued: the notice shows beyond the rate limit, so it shows as a plain one. liquid: the style of a
    // notice's level asks for the liquid (NoticeStyle.Liquid); null for an item without a style (a quota alert, a track change),
    // where Lively plays the liquid and Glow the backlight alone. The indicator has no room for words: a plain notice lights its
    // line once, without the bloom.
    public static NoticeEffect Effect(NoticeLevel level, bool subdued, IslandForm form, bool? liquid = null) => (subdued ? NoticeLevel.Plain : level) switch
    {
        NoticeLevel.Glow or NoticeLevel.Lively or NoticeLevel.Persistent when liquid is { } chosen => chosen ? NoticeEffect.Liquid : NoticeEffect.Glow,
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
