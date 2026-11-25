namespace BrimDeck.Core.Notices;

// How a notice of one level looks on the collapsed island: the "提示方式" settings page has one for levels 2 and 3
// (NoticeLevelStyles), and an application may have its own (NoticeAppRule.Styles), which then comes first. Level 1 is BrimDeck's
// own (its features, the command line, plug-ins) and looks the same always (NoticeLevelStyles.Text).
// Icon: the application's icon beside the words. Seconds: how long levels 1 and 2 stay, one of SecondsSteps; level 3 stays until
// handled, at most DeckSettings.NoticeStaySeconds. Liquid: levels 2 and 3 play the liquid as well as the backlight. Color: the
// backlight of levels 2 and 3, "" white (the island's own light), AppColor the application's color, else "#RRGGBB". The settings
// page offers it for level 3 only; an application's own looks offer it for level 2 as well. Sound: what levels 2 and 3 play
// (NoticeSounds): null the level's own default, "" nothing, a built-in sound's id, or "file:" and an imported file's name.
public sealed class NoticeStyle
{
    public const string AppColor = "app";
    public const double DefaultSeconds = 3;
    // The times a level-2 notice may stay, in seconds.
    public static readonly double[] SecondsSteps = [.5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
    // The level-3 backlights offered next to white and the application's color.
    public static readonly string[] Swatches = ["#6E9CF0", "#5DC98C", "#F0A35E", "#EF6A6A", "#B58CF0"];

    public bool Icon { get; set; } = true;
    public double Seconds { get; set; } = DefaultSeconds;
    public bool Liquid { get; set; }
    public string Color { get; set; } = "";
    public string? Sound { get; set; }

    public NoticeStyle Copy() => (NoticeStyle)MemberwiseClone();

    // The step nearest to seconds (a time saved before the steps, or out of range, takes the nearest one).
    public static double Step(double seconds)
        => double.IsFinite(seconds) ? SecondsSteps.MinBy(step => Math.Abs(step - seconds)) : DefaultSeconds;

    internal void Normalize()
    {
        Seconds = Step(Seconds);
        var color = (Color ?? "").Trim();
        Color = color == AppColor ? AppColor : AppPresets.NormalizeColor(color);
    }
}

// The three levels' looks: 文字 (Plain), 闪动 (Glow) and 常驻 (Persistent). Text is fixed, neither saved nor offered in the
// settings. Call: an incoming call's look, which stays as level 3 does; only an application's own looks have one (a chat's
// "来电"), null shows calls as Stay.
public sealed class NoticeLevelStyles
{
    [System.Text.Json.Serialization.JsonIgnore] public NoticeStyle Text { get; } = new();
    public NoticeStyle Flash { get; set; } = new();
    public NoticeStyle Stay { get; set; } = new() { Liquid = true };
    public NoticeStyle? Call { get; set; }

    // The look of a level; Lively, the older name of level 2, is Flash.
    public NoticeStyle For(NoticeLevel level) => level switch { NoticeLevel.Plain => Text, NoticeLevel.Persistent => Stay, _ => Flash };

    // An incoming call's look: its own while set, else level 3's.
    public NoticeStyle ForCall() => Call ?? Stay;

    public NoticeLevelStyles Copy() => new() { Flash = Flash.Copy(), Stay = Stay.Copy(), Call = Call?.Copy() };

    internal void Normalize()
    {
        Flash ??= new(); Stay ??= new() { Liquid = true };
        Flash.Normalize(); Stay.Normalize(); Call?.Normalize();
    }

    // The look a notice of this level shows with: the application's own while it has one, else the settings'. A call that stays
    // (level 3) shows with the call's look.
    public static NoticeStyle Of(NoticeLevel level, NoticeAppRule? rule, NoticeLevelStyles settings, bool call = false)
    {
        var styles = rule?.Styles ?? settings;
        return call && level == NoticeLevel.Persistent ? styles.ForCall() : styles.For(level);
    }
}
