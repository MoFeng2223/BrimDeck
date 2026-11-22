namespace BrimDeck.Core.Notices;

// How a notice of one level looks on the collapsed island: the "提示方式" settings page has one per level (NoticeLevelStyles), and
// an application may have its own (NoticeAppRule.Styles), which then comes first.
// Slot: level 1 only, the collapsed part whose place the notice takes, the rest of the island showing as it was; null for the
// whole island. Icon: the application's icon beside the words. Seconds: how long levels 1 and 2 stay; level 3 stays until
// handled. Liquid: levels 2 and 3 play the liquid as well as the backlight. Color: level 3's backlight, "" white (the island's own
// light), AppColor the application's color, else "#RRGGBB".
public sealed class NoticeStyle
{
    public const string AppColor = "app";
    public const int MinSeconds = 2, MaxSeconds = 30;
    // The level-3 backlights offered next to white and the application's color.
    public static readonly string[] Swatches = ["#6E9CF0", "#5DC98C", "#F0A35E", "#EF6A6A", "#B58CF0"];

    public CompactPart? Slot { get; set; }
    public bool Icon { get; set; } = true;
    public int Seconds { get; set; } = 6;
    public bool Liquid { get; set; }
    public string Color { get; set; } = "";

    public NoticeStyle Copy() => (NoticeStyle)MemberwiseClone();

    internal void Normalize()
    {
        if (Slot is { } slot && !Enum.IsDefined(slot)) Slot = null;
        Seconds = Math.Clamp(Seconds, MinSeconds, MaxSeconds);
        var color = (Color ?? "").Trim();
        Color = color == AppColor ? AppColor : AppPresets.NormalizeColor(color);
    }
}

// The three levels' looks: 文字 (Plain), 闪动 (Glow) and 常驻 (Persistent).
public sealed class NoticeLevelStyles
{
    public NoticeStyle Text { get; set; } = new();
    public NoticeStyle Flash { get; set; } = new();
    public NoticeStyle Stay { get; set; } = new() { Liquid = true };

    // The look of a level; Lively, the older name of level 2, is Flash.
    public NoticeStyle For(NoticeLevel level) => level switch { NoticeLevel.Plain => Text, NoticeLevel.Persistent => Stay, _ => Flash };

    public NoticeLevelStyles Copy() => new() { Text = Text.Copy(), Flash = Flash.Copy(), Stay = Stay.Copy() };

    internal void Normalize()
    {
        Text ??= new(); Flash ??= new(); Stay ??= new() { Liquid = true };
        Text.Normalize(); Flash.Normalize(); Stay.Normalize();
    }

    // The look a notice of this level shows with: the application's own while it has one, else the settings'.
    public static NoticeStyle Of(NoticeLevel level, NoticeAppRule? rule, NoticeLevelStyles settings) => (rule?.Styles ?? settings).For(level);
}
