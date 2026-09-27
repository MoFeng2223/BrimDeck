namespace BrimDeck.Core;

public static class ScreenPolicy
{
    public static ScreenContext Classify(bool exclusive, bool coversMonitor, bool hasCaption, bool maximized)
    {
        if (exclusive) return ScreenContext.Exclusive;
        if (coversMonitor && !hasCaption) return ScreenContext.Borderless;
        if (maximized) return ScreenContext.Maximized;
        return coversMonitor ? ScreenContext.Borderless : ScreenContext.Desktop;
    }
}
