namespace BrimDeck.Core;

/// <summary>Island-only compact shelf dimensions, independent of the clipboard; floating shelves are unchanged.</summary>
public static class ShelfDockLayout
{
    public const int Version = 2;
    public const double Width = 240, Height = 120;
    public static void Migrate(DeckSettings settings)
    {
        if (settings.TrayLayoutVersion >= Version) return;
        if (settings.TrayLayoutVersion < 1)
        {
            settings.TrayPreviousLayout ??= new ShelfLayoutBackup { Width = settings.TrayWidth, Height = settings.TrayHeight, GridView = settings.Tray.GridView };
            Reset(settings);
        }
        else if (settings.TrayReferenceSize)
        {
            settings.TrayWidth = Width; settings.TrayHeight = Height;
        }
        settings.TrayLayoutVersion = Version;
    }
    public static void Reset(DeckSettings settings)
    {
        settings.TrayWidth = Width; settings.TrayHeight = Height;
        settings.TrayReferenceSize = true; settings.Tray.GridView = false;
    }
    public static void RestorePrevious(DeckSettings settings)
    {
        if (settings.TrayPreviousLayout is not { } previous) return;
        settings.TrayWidth = Math.Clamp(previous.Width, Width, DeckPages.MaxWidth);
        settings.TrayHeight = Math.Clamp(previous.Height, Height, DeckPages.MaxHeight);
        settings.Tray.GridView = previous.GridView; settings.TrayReferenceSize = false;
    }
}

public sealed class ShelfLayoutBackup
{
    public double Width { get; set; }
    public double Height { get; set; }
    public bool GridView { get; set; }
}
