using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private DeckSettings? _pageSettings;
    private DeckPageDirectory? _pages;
    private object? _pageOrder;
    private long _pageRegistryRevision = -1;
    internal DeckPageDirectory PageDirectory(DeckSettings settings)
    {
        if (ReferenceEquals(settings, _pageSettings) && ReferenceEquals(settings.Pages.Order, _pageOrder) && Modules.Registry.Revision == _pageRegistryRevision && _pages is not null) return _pages;
        _pageSettings = settings; _pageOrder = settings.Pages.Order; _pageRegistryRevision = Modules.Registry.Revision;
        return _pages = new(settings, Modules.Registry.List<ModulePage>()
        .OrderBy(e => e.Value.Order).ThenBy(e => e.Id, StringComparer.Ordinal).Select(entry => new DeckPageDefinition(entry.Id, entry.Value.Title, entry.Value.TitleEn)
        {
            Owner = entry.ModuleId, IconPath = entry.Value.IconPath,
            Width = entry.Value.Size?.Width ?? settings.MiniApps.Width, Height = entry.Value.Size?.Height ?? settings.MiniApps.Height,
            MinWidth = entry.Value.Size?.MinWidth ?? DeckPages.MinWidth, MaxWidth = entry.Value.Size?.MaxWidth ?? DeckPages.MaxWidth,
            MinHeight = entry.Value.Size?.MinHeight ?? DeckPages.MinHeight, MaxHeight = entry.Value.Size?.MaxHeight ?? DeckPages.MaxHeight
        }));
    }
}
