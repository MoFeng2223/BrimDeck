using BrimDeck.Core;
using BrimDeck.Modules;

namespace BrimDeck;

// The panel's state as modules see it (ModuleHostStateSnapshot). The snapshot is taken from the main panel only: the other
// chosen displays have a panel each with its own open page, and a change on one of them must not overwrite what a module
// reads about the main one. Every value here is a copy of what the panel shows; no panel, service or settings object is
// handed out.
public partial class App
{
    // The source's own value is the main panel's state; RedactHostState hides another module's page from a reader.
    private readonly HostStateRevisions _hostState = new();
    private void PublishModuleHostState(bool force)
    {
        if (Exiting || Dispatcher.HasShutdownStarted || Deck is null) return;
        if (_hostState.Apply(CollectHostState(), force) is { } published) ModuleHostStateSource.Publish(published);
    }
    private ModuleHostStateSnapshot CollectHostState()
    {
        var deck = Deck;
        return new ModuleHostStateSnapshot(0, DateTimeOffset.UtcNow)
        {
            Expanded = deck.IsExpanded,
            PageId = deck.CurrentPageId,
            PageIsOwn = false,
            ThemeId = PanelTheme.ShownTheme.Id,
            ThemeIsDark = PanelTheme.Current.IsDark,
            ThemeNightLook = PanelTheme.Night,
            HighContrast = PanelTheme.HighContrast,
            ThemeGeneration = PanelTheme.Generation,
            DeviceScale = deck.DeviceScale,
            ContentScale = deck.Scale,
            ScreenContext = deck.CurrentScreenContext.ToString().ToLowerInvariant(),
            DisplayId = deck.Display?.Id,
            DisplayRevision = _displayRevision
        };
    }
    // A module's own page is named in full only to that module: any other reader gets the generic mini-apps page. The module
    // id of a page is the part before its colon ("thrm:main" is thrm's).
    internal static string PageIdForModules(string pageId, string? moduleId)
        => moduleId is { Length: > 0 } && IsOwnPage(pageId, moduleId) ? pageId : pageId.StartsWith('@') ? pageId : DeckPageIds.LegacyMiniApps;
    internal ModuleHostStateSnapshot RedactHostState(ModuleHostStateSnapshot state, string moduleId)
        => IsOwnPage(state.PageId, moduleId) ? state with { PageIsOwn = true }
            : state.PageId.StartsWith('@') ? state with { PageIsOwn = false } : state with { PageId = DeckPageIds.LegacyMiniApps, PageIsOwn = false };
    private static bool IsOwnPage(string pageId, string moduleId)
    {
        int colon = pageId.IndexOf(':');
        return pageId.Length > 0 && pageId[0] != '@' && colon > 0 && pageId[..colon] == moduleId;
    }
    private int _displayRevision;
    // Called after the panels have taken a display change (App.DisplaysChanged): the layout's revision is raised and the
    // state published once, after every panel has its monitor.
    internal void DisplaysSettled() { _displayRevision++; PublishModuleHostState(force: true); }
    // Called by the main panel when what a module can see about it changed; the panel knows when its layout finished.
    internal void DeckStateChanged() => PublishModuleHostState(force: false);
}
// The revisions of the panel state as modules see it: a change publishes once, an unchanged layout publishes nothing, and a
// forced publish (a display change, a theme reload) keeps the revision moving forward rather than repeating the last one.
internal sealed class HostStateRevisions
{
    private ModuleHostStateSnapshot _last = new(0, DateTimeOffset.UtcNow);
    public ModuleHostStateSnapshot? Apply(ModuleHostStateSnapshot state, bool force)
    {
        if (!force && Same(_last, state)) return null;
        _last = state with { Revision = _last.Revision + 1, ObservedAt = DateTimeOffset.UtcNow };
        return _last;
    }
    public ModuleHostStateSnapshot Current => _last;
    private static bool Same(ModuleHostStateSnapshot a, ModuleHostStateSnapshot b)
        => a.Expanded == b.Expanded && a.PageId == b.PageId && a.ThemeId == b.ThemeId && a.ThemeIsDark == b.ThemeIsDark && a.ThemeNightLook == b.ThemeNightLook
        && a.HighContrast == b.HighContrast && a.ThemeGeneration == b.ThemeGeneration && a.DeviceScale.Equals(b.DeviceScale) && a.ContentScale.Equals(b.ContentScale)
        && a.ScreenContext == b.ScreenContext && a.DisplayId == b.DisplayId && a.DisplayRevision == b.DisplayRevision;
}
