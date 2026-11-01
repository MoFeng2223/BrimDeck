namespace BrimDeck.Core;

// Values are persisted; Full preserves installations created before module modes existed.
public enum ModuleMode { Full, Performance, Off }

/// <summary>Presentation and service policy, independent of capture consent and shelf placement.</summary>
public readonly record struct ModuleRuntimePolicy(ModuleMode Mode)
{
    public bool Running => Mode != ModuleMode.Off;
    public bool Reduced => Mode == ModuleMode.Performance;
    public bool Automatic => Mode == ModuleMode.Full;
    public int CardPixels => Reduced ? 256 : 400;
    public int PreviewPixels => Reduced ? 768 : 1000;
    public int ThumbnailPixels => Reduced ? 128 : 512;
    public const long ReducedCacheBytes = 16 * 1024 * 1024;
    public static ModuleRuntimePolicy For(ModuleMode mode) => new(Enum.IsDefined(mode) ? mode : ModuleMode.Full);
    public static ModuleMode ForPage(DeckSettings settings, DeckPage page) => page switch
    {
        DeckPage.Clipboard => settings.Clipboard.Mode,
        DeckPage.FileTray => settings.Tray.Mode,
        _ => ModuleMode.Full
    };
}
