namespace BrimDeck.Core.Notices;

// Which of a notice's buttons show on the collapsed island. BrimDeck decides this by how the panel opens, not by the notice's
// level, and there is no setting for it (dev-docs/brim-cli.md): where the pointer resting on the island opens the panel at once,
// a button there could not be reached before the panel covers it, and the notices page has the buttons anyway.
public static class NoticeActions
{
    // With the panel opening on hover, the delay from which the island shows buttons, in milliseconds: time enough to reach one.
    public const int HoverDelayForButtons = 500;
    // Buttons the island shows at most; the notices page shows them all.
    public const int IslandLimit = 2;
    // The room the words keep beside the buttons, in DIPs before the island's scale: with less, a button goes. Enough for a few
    // characters, which scroll: the island keeps its size, and on a narrow one the button is what must stay in reach.
    public const double WordsRoom = 48;
    // A button's label at most, in DIPs before the island's scale; a longer one ends in an ellipsis.
    public const double LabelRoom = 96;

    // Whether the island shows buttons at all: the panel opens on a click, or on hover after a delay long enough; never on the
    // indicator, which has no room, nor for a notice that only takes a part's place.
    public static bool OnIsland(bool clickToExpand, int openDelayMs, IslandForm form, bool inPart)
        => form != IslandForm.Line && !inPart && (clickToExpand || openDelayMs >= HoverDelayForButtons);

    // Sender: one of the notice's own (Action). CopyCode and OpenApp: BrimDeck's own, for a verification code it recognized and
    // for a source application it can open.
    public enum ButtonKind { Sender, CopyCode, OpenApp }

    // Short: a shorter label for where the whole one does not fit (a narrow island); null when there is none.
    public sealed record Button(ButtonKind Kind, string Label, NoticeAction? Action = null, bool Primary = false, string? Short = null);

    // The buttons for the island, in order: the sender's main button, its others, copying a verification code, opening the
    // source application; at most limit. sender: the notice's buttons that can still do something (NoticeCenter.Buttons).
    public static IReadOnlyList<Button> Pick(IReadOnlyList<NoticeAction> sender, bool hasCode, bool canOpen, int limit = IslandLimit)
    {
        var buttons = new List<Button>();
        foreach (var action in sender.OrderByDescending(action => action.Primary)) buttons.Add(new(ButtonKind.Sender, action.Label, action, action.Primary));
        if (hasCode) buttons.Add(new(ButtonKind.CopyCode, Loc.T("复制验证码", "Copy code"), Short: Loc.T("复制", "Copy")));
        if (canOpen) buttons.Add(new(ButtonKind.OpenApp, Loc.T("打开", "Open")));
        // Without a main button of the sender's, the first one is drawn as the main one.
        if (buttons.Count > 0 && !buttons[0].Primary) buttons[0] = buttons[0] with { Primary = true };
        return buttons.Count > limit ? buttons.GetRange(0, Math.Max(0, limit)) : buttons;
    }

    // How many of the buttons show, by their widths: all while the words keep their room, else the first alone, else none.
    public static int Fit(IReadOnlyList<double> widths, double gap, double available, double wordsRoom)
    {
        if (widths.Count == 0) return 0;
        double all = widths.Sum() + gap * widths.Count;
        if (available - all >= wordsRoom) return widths.Count;
        return available - widths[0] - gap >= wordsRoom ? 1 : 0;
    }
}

// The backlight of a notice that stays until handled, when the notice itself says or implies one: the sender's own color, else
// one for a warning, an error or a success. Null leaves it to the level's style (NoticeStyle.Color).
public static class NoticeTones
{
    // The swatches of the settings (NoticeStyle.Swatches) that read as a warning, an error and a success.
    public const string Warning = "#F0A35E", Error = "#EF6A6A", Success = "#5DC98C";

    public static string? Glow(Notice notice) => notice.Glow ?? notice.Category switch
    {
        NoticeCategory.Warning => Warning, NoticeCategory.Error => Error, NoticeCategory.Success => Success, _ => null
    };
}
