namespace BrimDeck.Core;

/// <summary>Edge-Drop 767b6d63: Panel.top, Header.horizontal and item.css horizontal geometry, in unscaled DIP.</summary>
public static class ClipboardDockLayout
{
    public const int Version = 1;
    public const double Width = 1080, Height = 210, HeaderHeight = 40;
    public const double CardWidth = 210, CardHeight = 146, CardGap = 10, CardRadius = 18;
    public const double ListInset = 14, ListTop = 6, ListBottom = 8, SearchWidth = 300, SearchHeight = 30;
    public const double ScreenInset = 30, MinWidth = 440;
    public static double ReferenceWidth(double workWidth, double scale = 1)
        => Math.Max(240, Math.Min(Width, (workWidth - ScreenInset * 2) / Math.Max(1, scale)));

    // Stored dimensions must change too: changing only property defaults leaves the old installed layout intact.
    public static void Migrate(DeckSettings settings)
    {
        if (settings.ClipboardLayoutVersion >= Version) return;
        settings.ClipboardPreviousLayout ??= new ClipboardLayoutBackup
        {
            Width = settings.ClipboardWidth, Height = settings.ClipboardHeight, GridView = settings.Clipboard.GridView,
        };
        Reset(settings);
        settings.ClipboardLayoutVersion = Version;
    }
    public static void Reset(DeckSettings settings)
    {
        settings.ClipboardWidth = Width; settings.ClipboardHeight = Height;
        settings.ClipboardReferenceSize = true; settings.Clipboard.GridView = false;
    }
    public static void RestorePrevious(DeckSettings settings)
    {
        if (settings.ClipboardPreviousLayout is not { } previous) return;
        settings.ClipboardWidth = Math.Clamp(previous.Width, MinWidth, DeckPages.MaxWidth);
        settings.ClipboardHeight = Math.Clamp(previous.Height, Height, DeckPages.MaxHeight);
        settings.Clipboard.GridView = previous.GridView; settings.ClipboardReferenceSize = false;
    }
}

public sealed class ClipboardLayoutBackup
{
    public double Width { get; set; }
    public double Height { get; set; }
    public bool GridView { get; set; }
}
