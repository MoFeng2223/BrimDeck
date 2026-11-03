namespace BrimDeck.Timers;

// Uses the content area beneath the shared panel header. Size steps change the arrangement,
// as on the usage/music/monitor pages; ordinary ticks never change a card's dimensions.
public sealed record MiniAppsLayout(double Width, double Height, double Tabs, double Gap,
    bool SideBySide, double HeroWidth, double HeroHeight, double DetailWidth, double DetailHeight)
{
    public bool Slim => HeroHeight < 54 && !SideBySide;
    public int FieldColumns => SideBySide && DetailWidth >= 600 ? 4 : 2;
    public bool FullFields => DetailHeight >= (FieldColumns == 4 ? 54 : 106);
    public bool CountdownFields => DetailHeight >= 54;
    public int LapRows => Math.Clamp((int)((DetailHeight - 32) / 22), 1, 10);
    public static MiniAppsLayout For(double width, double height)
    {
        width = double.IsFinite(width) ? Math.Max(360, width) : 480;
        height = double.IsFinite(height) ? Math.Max(90, height) : 266;
        double tabs = height < 126 ? 24 : 28, gap = height < 170 ? 4 : 8;
        double body = height - tabs - gap;
        bool side = width >= 680;
        double heroWidth = side ? Math.Clamp(width * .32, 240, 330) : width;
        // Grow the hero only once the full editor grid still fits underneath it.
        // Increasing height must never replace visible editors with compact summaries.
        double heroHeight = side ? body : body >= 186 ? 72 : 36;
        return new(width, height, tabs, gap, side, heroWidth, heroHeight,
            side ? width - heroWidth - gap : width, side ? body : body - heroHeight - gap);
    }
    public static int PageCount(int count, int rows) => Math.Max(1, (Math.Max(0, count) + Math.Max(1, rows) - 1) / Math.Max(1, rows));
}
