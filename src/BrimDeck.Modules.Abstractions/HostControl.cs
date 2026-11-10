namespace BrimDeck.Modules;

// What a module may ask the host to do, one narrow action at a time. Each action is a capability of its own
// (host.control.page, host.control.expand, host.control.media, host.control.accent) with its own interface, so a manifest
// grants exactly what the module uses and nothing else; IModuleHostControl hands the granted ones out. No action gives a
// module a window, a settings object or the panel itself.
public static class ModuleHostControlActions
{
    public const string Page = "host.control.page", Expand = "host.control.expand", Media = "host.control.media", Accent = "host.control.accent";
}

public enum ModuleHostResultKind { Completed, Rejected, CapabilityMissing, Cancelled, Failed }

// The outcome of a host action. Reason is a stable code when the host refused ("user-initiated-only", "no-player",
// "not-playing", "panel-hidden"), so a module can tell a policy refusal from a failure.
public sealed record ModuleHostResult(ModuleHostResultKind Kind, string? Reason = null)
{
    public bool Ok => Kind == ModuleHostResultKind.Completed;
    public static ModuleHostResult Completed { get; } = new(ModuleHostResultKind.Completed);
    public static ModuleHostResult Rejected(string reason) => new(ModuleHostResultKind.Rejected, reason);
    public static ModuleHostResult Missing(string reason) => new(ModuleHostResultKind.CapabilityMissing, reason);
    public static ModuleHostResult Failed(string reason) => new(ModuleHostResultKind.Failed, reason);
    public static ModuleHostResult Cancelled { get; } = new(ModuleHostResultKind.Cancelled);
}

// host.control.page: shows a page this module registered, and only that.
public interface IModulePageControl
{
    Task<ModuleHostResult> OpenOwnPageAsync(string pageId, CancellationToken cancellation = default);
}
// host.control.expand: asks the panel to open. Honored only in answer to a user action; a module calling it from a timer,
// a background event or a state callback is refused with "user-initiated-only", so no automation takes the screen in a loop.
public interface IModulePanelControl
{
    Task<ModuleHostResult> RequestExpandAsync(ModuleCommandOrigin origin, CancellationToken cancellation = default);
}
// host.control.media: pauses or resumes the player the panel shows, when that player offers it.
public interface IModuleMediaControl
{
    Task<ModuleHostResult> ToggleMediaPlayPauseAsync(CancellationToken cancellation = default);
}
// host.control.accent: a temporary accent color, held until the override is disposed, the module stops, or the user
// changes the theme. A background module may tint the panel but never change the user's saved theme.
public interface IModuleAccentControl
{
    ModuleAccentResult RequestAccentOverride(string color);
}
// The four above in one service, as a module holds it; asking for what the manifest does not declare throws
// "capability-not-declared" rather than returning a refusal.
public interface IModuleHostControl
{
    IModulePageControl Pages { get; }
    IModulePanelControl Panel { get; }
    IModuleMediaControl Media { get; }
    IModuleAccentControl Accent { get; }
}

// The temporary accent: Dispose gives the color back. Requests are a stack, the newest in front; the user's own theme
// change clears every request at once (PanelTheme). Disposing twice does nothing.
public interface ModuleAccentOverride : IDisposable
{
    string Color { get; }
}
public sealed record ModuleAccentResult(ModuleHostResultKind Kind, ModuleAccentOverride? Override = null, string? Reason = null);
