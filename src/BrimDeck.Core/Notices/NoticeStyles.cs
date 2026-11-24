namespace BrimDeck.Core.Notices;

// How a notice of one level looks on the collapsed island: the "提示方式" settings page has one per level (NoticeLevelStyles), and
// an application may have its own (NoticeAppRule.Styles), which then comes first.
// Slot: level 1 only, the collapsed part whose place the notice takes, the rest of the island showing as it was; null for the
// whole island. Icon: the application's icon beside the words. Seconds: how long levels 1 and 2 stay; level 3 stays until
// handled. Liquid: levels 2 and 3 play the liquid as well as the backlight. Color: the backlight of levels 2 and 3, "" white (the
// island's own light), AppColor the application's color, else "#RRGGBB". The settings page offers it for level 3 only; an
// application's own looks offer it for level 2 as well.
public sealed class NoticeStyle
{
    public const string AppColor = "app";
    public const int MinSeconds = 2, MaxSeconds = 30;
    // The level-3 backlights offered next to white and the application's color.
    public static readonly string[] Swatches = ["#6E9CF0", "#5DC98C", "#F0A35E", "#EF6A6A", "#B58CF0"];

    public bool Icon { get; set; } = true;
    public int Seconds { get; set; } = 6;
    public bool Liquid { get; set; }
    public string Color { get; set; } = "";

    public NoticeStyle Copy() => (NoticeStyle)MemberwiseClone();

    internal void Normalize()
    {
        Seconds = Math.Clamp(Seconds, MinSeconds, MaxSeconds);
        var color = (Color ?? "").Trim();
        Color = color == AppColor ? AppColor : AppPresets.NormalizeColor(color);
    }
}

// The three levels' looks: 文字 (Plain), 闪动 (Glow) and 常驻 (Persistent). Call: an incoming call's look, which stays as level 3
// does; only an application's own looks have one (a chat's "来电"), null shows calls as Stay.
public sealed class NoticeLevelStyles
{
    public NoticeStyle Text { get; set; } = new();
    public NoticeStyle Flash { get; set; } = new();
    public NoticeStyle Stay { get; set; } = new() { Liquid = true };
    public NoticeStyle? Call { get; set; }

    // The look of a level; Lively, the older name of level 2, is Flash.
    public NoticeStyle For(NoticeLevel level) => level switch { NoticeLevel.Plain => Text, NoticeLevel.Persistent => Stay, _ => Flash };

    // An incoming call's look: its own while set, else level 3's.
    public NoticeStyle ForCall() => Call ?? Stay;

    public NoticeLevelStyles Copy() => new() { Text = Text.Copy(), Flash = Flash.Copy(), Stay = Stay.Copy(), Call = Call?.Copy() };

    internal void Normalize()
    {
        Text ??= new(); Flash ??= new(); Stay ??= new() { Liquid = true };
        Text.Normalize(); Flash.Normalize(); Stay.Normalize(); Call?.Normalize();
    }

    // The look a notice of this level shows with: the application's own while it has one, else the settings'. A call that stays
    // (level 3) shows with the call's look.
    public static NoticeStyle Of(NoticeLevel level, NoticeAppRule? rule, NoticeLevelStyles settings, bool call = false)
    {
        var styles = rule?.Styles ?? settings;
        return call && level == NoticeLevel.Persistent ? styles.ForCall() : styles.For(level);
    }
}
