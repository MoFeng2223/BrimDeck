namespace BrimDeck.Core;

/// <summary>Island-only shelf layout reuses the translated clipboard Dock geometry; floating shelves are unchanged.</summary>
public static class ShelfDockLayout
{
    public const int Version = 1;
    public static void Migrate(DeckSettings settings)
    {
        if (settings.TrayLayoutVersion >= Version) return;
        settings.TrayPreviousLayout ??= new ShelfLayoutBackup { Width = settings.TrayWidth, Height = settings.TrayHeight, GridView = settings.Tray.GridView };
        Reset(settings); settings.TrayLayoutVersion = Version;
    }
    public static void Reset(DeckSettings settings)
    {
        settings.TrayWidth = ClipboardDockLayout.Width; settings.TrayHeight = ClipboardDockLayout.Height;
        settings.TrayReferenceSize = true; settings.Tray.GridView = false;
    }
    public static void RestorePrevious(DeckSettings settings)
    {
        if (settings.TrayPreviousLayout is not { } previous) return;
        settings.TrayWidth = Math.Clamp(previous.Width, ClipboardDockLayout.MinWidth, DeckPages.MaxWidth);
        settings.TrayHeight = Math.Clamp(previous.Height, ClipboardDockLayout.Height, DeckPages.MaxHeight);
        settings.Tray.GridView = previous.GridView; settings.TrayReferenceSize = false;
    }
}

public sealed class ShelfLayoutBackup
{
    public double Width { get; set; }
    public double Height { get; set; }
    public bool GridView { get; set; }
}
