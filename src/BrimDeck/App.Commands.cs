using BrimDeck.Core;
using BrimDeck.Modules;

namespace BrimDeck;

// The single command system: a module registers IModuleCommand contributions, and a global shortcut, the tray menu and a page
// button all run the same ExecuteAsync. Nothing here caches a command's behaviour, so a command replaced with its module keeps
// its id and the new implementation; a module that is stopped takes its commands with it (ModuleScope owns the registration).
public partial class App
{
    // One invocation at a time per command id. A second call while the first is in flight is Busy, not a queue and not a
    // failure: a shortcut pressed twice must not run the action twice.
    private readonly object _commandGate = new();
    private readonly HashSet<string> _commandsRunning = new(StringComparer.Ordinal);

    // Every command on offer, in module order then registration order. Stopped modules contribute none, since their
    // registrations were released with the activation.
    internal IReadOnlyList<Contribution<IModuleCommand>> ModuleCommands() => Modules.Registry.List<IModuleCommand>();

    // Runs a command by its full contribution id ("thrm:open-page"). The result always describes what happened; the caller
    // decides whether to tell the user.
    internal async Task<ModuleCommandResult> InvokeModuleCommandAsync(string commandId, IReadOnlyDictionary<string, string>? parameters = null,
        ModuleCommandOrigin origin = ModuleCommandOrigin.User, CancellationToken cancellation = default)
    {
        if (Modules.Registry.List<IModuleCommand>().FirstOrDefault(entry => entry.Id == commandId) is not { } entry)
            return ModuleCommandResult.Missing(Loc.T("这个命令已不存在。", "This command no longer exists."));
        return await InvokeModuleCommandAsync(entry, parameters, origin, cancellation);
    }

    private async Task<ModuleCommandResult> InvokeModuleCommandAsync(Contribution<IModuleCommand> entry,
        IReadOnlyDictionary<string, string>? parameters, ModuleCommandOrigin origin, CancellationToken cancellation)
    {
        string commandId = entry.Id;
        // The capability the module needs at run time; a command written for a newer host reports the gap instead of failing.
        if (entry.Value.Descriptor.Requires is { Length: > 0 } required)
            foreach (string capability in required)
                if (!Modules.AvailableCapabilities.Contains(capability))
                    return ModuleCommandResult.Missing(
                        Loc.T($"该命令需要「{ModuleApi.Describe(capability)?.Name ?? capability}」，当前版本不提供。",
                            $"This command needs {ModuleApi.Describe(capability)?.NameEn ?? capability}, which this version does not offer."));
        if (!entry.Value.CanExecute) return ModuleCommandResult.Rejected(Loc.T("这个命令现在不可用。", "This command is not available right now."));
        lock (_commandGate) { if (!_commandsRunning.Add(commandId)) return ModuleCommandResult.Busy; }
        try
        {
            // A command may take time; it runs once, with a token that ends with the module and with the caller.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var moduleLive = Modules.CommandLifetime(entry.ModuleId);
            using var registration = moduleLive.Register(() => { try { linked.Cancel(); } catch (ObjectDisposedException) { } });
            linked.Token.ThrowIfCancellationRequested();
            var result = await entry.Value.ExecuteAsync(parameters, linked.Token)
                ?? ModuleCommandResult.Failed(Loc.T("命令没有返回结果。", "The command returned no result."));
            return result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || Modules.CommandLifetime(entry.ModuleId).IsCancellationRequested)
        { return ModuleCommandResult.Cancelled; }
        catch (ModuleApiException error)
        { return ModuleCommandResult.Failed(Loc.T("命令执行失败：" + error.Message, "The command failed: " + error.Message)); }
        catch (Exception error)
        {
            Modules.Report(entry.ModuleId, "command " + commandId, error);
            return ModuleCommandResult.Failed(Loc.T("插件执行命令时出错。", "The plugin threw while running the command."));
        }
        finally { lock (_commandGate) _commandsRunning.Remove(commandId); }
    }

    // Tells the user why a command did nothing. Completed and cancelled results stay quiet: a cancelled command is the user's
    // own doing, usually because the panel closed.
    private void ReportCommandResult(Contribution<IModuleCommand> entry, ModuleCommandResult result)
    {
        if (result.Kind is ModuleCommandResultKind.Completed or ModuleCommandResultKind.Cancelled) return;
        string message = (result.Message, result.MessageEn) switch
        {
            ({ Length: > 0 } zh, { Length: > 0 } en) => Loc.T(zh, en),
            ({ Length: > 0 } zh, _) => zh,
            (_, { Length: > 0 } en) => en,
            _ => result.Kind switch
            {
                ModuleCommandResultKind.Busy => Loc.T("命令正在执行，请稍后。", "The command is still running."),
                ModuleCommandResultKind.Rejected => Loc.T("这个命令现在不可用。", "This command is not available right now."),
                ModuleCommandResultKind.CapabilityMissing => Loc.T("当前版本不支持该命令。", "This version does not support this command."),
                _ => Loc.T("命令执行失败。", "The command failed.")
            }
        };
        _ = message;
        ShowCommandFailure(entry, message);
    }

    // Shown through the module notice queue, which respects the panel's own rules (hidden, full screen, already showing an
    // alert); the result is also kept so the settings page can explain it in the row.
    private void ShowCommandFailure(Contribution<IModuleCommand> entry, string message)
    {
        if (Exiting || Dispatcher.HasShutdownStarted) return;
        void Show()
        {
            LastCommandProblem = (entry.Id, message);
            PostModuleNotice(entry.ModuleId, Lifetime.Token, new ModuleNotice("command-" + entry.Id, CommandName(entry), message) { Tone = ModuleNoticeTone.Warning });
        }
        if (Dispatcher.CheckAccess()) Show(); else Dispatcher.BeginInvoke(Show);
    }

    // The last command that did nothing and why; the settings page reads it for the row's message.
    internal (string CommandId, string Message)? LastCommandProblem { get; private set; }

    internal static string CommandName(Contribution<IModuleCommand> entry)
        => (entry.Value.Descriptor.Name, entry.Value.Descriptor.NameEn) is ({ Length: > 0 } zh, { Length: > 0 } en) ? Loc.T(zh, en)
            : entry.Value.Descriptor.Name is { Length: > 0 } name ? name : entry.Id;

    // Runs a command from its global shortcut: reports the result and never throws.
    internal async void RunModuleCommand(Contribution<IModuleCommand> entry)
    {
        try
        {
            var result = await InvokeModuleCommandAsync(entry, null, ModuleCommandOrigin.User, Lifetime.Token);
            ReportCommandResult(entry, result);
        }
        catch (Exception error) { Modules.Report(entry.ModuleId, "command " + entry.Id, error); }
    }

    // Opens a page the module owns, and nothing else: the id must be one of that module's registered pages. This is the one
    // host action a command may take for itself in 5a; it is what makes a command like "show the cooler" meaningful. It runs
    // on the UI thread and only while the panel is not holding a transient surface of its own.
    internal bool OpenOwnModulePage(string moduleId, string pageId, bool userInitiated)
    {
        if (!userInitiated) return false;
        if (Modules.Registry.List<ModulePage>().FirstOrDefault(entry => entry.Id == pageId && entry.ModuleId == moduleId) is null) return false;
        if (Deck is null || Exiting || Dispatcher.HasShutdownStarted) return false;
        void Open() => Deck.OpenModulePageFromSettings(pageId);
        if (Dispatcher.CheckAccess()) Open(); else Dispatcher.BeginInvoke(Open);
        return true;
    }

    // The service a module receives: bound to one activation, so it lists and invokes only that module's commands.
    private sealed class CommandsService(App app, ModuleServiceScope scope) : IModuleCommands
    {
        public IReadOnlyList<ModuleCommand> List()
        {
            scope.RequireLive();
            return app.ModuleCommands().Where(entry => entry.ModuleId == scope.ModuleId).Select(entry => entry.Value.Descriptor).ToArray();
        }
        public Task<ModuleCommandResult> InvokeAsync(string commandId, IReadOnlyDictionary<string, string>? parameters = null,
            ModuleCommandOrigin origin = ModuleCommandOrigin.User, CancellationToken cancellation = default)
        {
            scope.RequireLive();
            if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
            ArgumentNullException.ThrowIfNull(commandId);
            if (app.ModuleCommands().FirstOrDefault(entry => entry.ModuleId == scope.ModuleId && entry.Id == commandId) is not { } entry)
                throw new ModuleApiException("unknown-command", "A module can invoke only its own commands: " + commandId);
            return app.InvokeModuleCommandAsync(entry, parameters, origin, cancellation);
        }
        public bool ShowOwnPage(string pageId, bool userInitiated = true)
        {
            scope.RequireLive();
            ArgumentNullException.ThrowIfNull(pageId);
            return scope.Ready && app.OpenOwnModulePage(scope.ModuleId, pageId, userInitiated);
        }
    }
}
