namespace BrimDeck.Modules;

// A stable, user-facing action a module offers. Registered like any other contribution, so the contribution id is the
// command id ("sample-reading-progress:open-page"): a command cannot address another module's contribution and keeps its id across restarts.
// One implementation serves every entry point — a global shortcut, the tray menu and a page button invoke the same command.
//
// Requires names capabilities the command needs at run time. The host answers CapabilityMissing instead of running the
// command when this host does not offer one of them, so a command written for a newer host degrades into a clear result
// rather than into a failure the user has to interpret.
public sealed record ModuleCommand(string Name, string NameEn)
{
    public string? Description { get; init; }
    public string? DescriptionEn { get; init; }
    // The parameters the command accepts, for documentation and for callers that pass them. Null or empty means none.
    public string[]? Parameters { get; init; }
    public string[]? Requires { get; init; }
}

// The outcomes are told apart on purpose: a command still running is not a failure, a refusal is not a fault, and a missing
// capability is not a bug in the module.
public enum ModuleCommandResultKind { Completed, Busy, Rejected, Cancelled, CapabilityMissing, Failed }

// Message and MessageEn explain Busy, Rejected, CapabilityMissing and Failed to the user; a completed or cancelled result
// usually carries none.
public sealed record ModuleCommandResult(ModuleCommandResultKind Kind, string? Message = null, string? MessageEn = null)
{
    public static ModuleCommandResult Completed { get; } = new(ModuleCommandResultKind.Completed);
    public static ModuleCommandResult Busy { get; } = new(ModuleCommandResultKind.Busy);
    public static ModuleCommandResult Cancelled { get; } = new(ModuleCommandResultKind.Cancelled);
    public static ModuleCommandResult Rejected(string message, string? messageEn = null) => new(ModuleCommandResultKind.Rejected, message, messageEn);
    public static ModuleCommandResult Missing(string message, string? messageEn = null) => new(ModuleCommandResultKind.CapabilityMissing, message, messageEn);
    public static ModuleCommandResult Failed(string message, string? messageEn = null) => new(ModuleCommandResultKind.Failed, message, messageEn);
}

public interface IModuleCommand
{
    ModuleCommand Descriptor { get; }
    // False means the command cannot act right now; invoking it yields Rejected so the user learns why nothing happened.
    bool CanExecute { get; }
    Task<ModuleCommandResult> ExecuteAsync(IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellation);
}

// Where an invocation came from. Only User invocations may ask the host for attention, such as opening a page: a command
// run from a background timer or event must not take focus away from what the user is doing.
public enum ModuleCommandOrigin { User, Host }

// The commands of the calling module. The service is bound to one activation, so neither List nor Invoke can reach another
// module's commands: a module's shortcut, tray entry and page button all run its own implementation through here.
public interface IModuleCommands
{
    IReadOnlyList<ModuleCommand> List();
    Task<ModuleCommandResult> InvokeAsync(string commandId, IReadOnlyDictionary<string, string>? parameters = null,
        ModuleCommandOrigin origin = ModuleCommandOrigin.User, CancellationToken cancellation = default);
    // Shows a page this module registered, for a command that means "take me there". The page id must belong to the caller;
    // the host opens it only for a user-originated call, so a background timer cannot pull the panel open on its own. The
    // rest of the host actions (expand, media, accent) arrive with the host-control capability in 5c.
    bool ShowOwnPage(string pageId, bool userInitiated = true);
}
