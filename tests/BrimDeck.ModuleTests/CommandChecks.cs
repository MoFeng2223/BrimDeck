using System.IO;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Modules;

internal static partial class Program
{
    // A command that records that it ran and answers as told.
    private sealed class TestCommand(string name, Func<CancellationToken, Task<ModuleCommandResult>>? run = null) : IModuleCommand
    {
        internal int Runs;
        internal CancellationToken Token;
        public ModuleCommand Descriptor { get; } = new(name, name);
        public bool CanExecute { get; set; } = true;
        public async Task<ModuleCommandResult> ExecuteAsync(IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellation)
        {
            Runs++; Token = cancellation;
            return run is null ? ModuleCommandResult.Completed : await run(cancellation);
        }
    }

    private static async Task CommandChecks()
    {
        // ---- contract shape ----
        Check("command result factories keep the five outcomes apart",
            ModuleCommandResult.Completed.Kind == ModuleCommandResultKind.Completed && ModuleCommandResult.Busy.Kind == ModuleCommandResultKind.Busy
            && ModuleCommandResult.Cancelled.Kind == ModuleCommandResultKind.Cancelled
            && ModuleCommandResult.Rejected("m", "e") is { Kind: ModuleCommandResultKind.Rejected, Message: "m", MessageEn: "e" }
            && ModuleCommandResult.Missing("m") is { Kind: ModuleCommandResultKind.CapabilityMissing, Message: "m" }
            && ModuleCommandResult.Failed("m") is { Kind: ModuleCommandResultKind.Failed, Message: "m" });
        Check("a command descriptor carries an optional description, parameters and requirement list",
            new ModuleCommand("打开", "Open") is { Parameters: null, Requires: null, Description: null }
            && new ModuleCommand("打开", "Open") { Requires = [ModuleApi.CommandsInvoke] } is { Requires.Length: 1 });

        // ---- both capabilities are in the catalog and bound ----
        // Introduced in minor 9; the contract's current minor moves on with later additions.
        Check("the command capabilities are catalogued as a service and a contribution",
            ModuleApi.Describe(ModuleApi.CommandsInvoke) is { Kind: ModuleCapabilityKind.Service, IntroducedMinor: 9 }
            && ModuleApi.Describe(ModuleApi.UiCommands) is { Kind: ModuleCapabilityKind.Contribution, IntroducedMinor: 9 }
            && ModuleApi.Capabilities.Contains(ModuleApi.UiCommands) && ModuleApi.Capabilities.Contains(ModuleApi.CommandsInvoke));
        Check("the contribution kind accepts a registered command and rejects an unnamed one",
            App.ModuleKinds().Any(kind => kind.Contract == typeof(IModuleCommand) && kind.Id == ModuleApi.UiCommands));
        {
            var validate = App.ModuleKinds().First(kind => kind.Contract == typeof(IModuleCommand)).Validate!;
            validate(new TestCommand("a"));
            Throws("a command without any name is refused", () => validate(new TestCommand("")));
        }

        // ---- host side: registration, scoping, retirement ----
        // This host adds the commands service on top of the shared harness, which binds presentation only. The service here
        // repeats the host's scoping rule (only the caller's own commands) so the rule itself is exercised headlessly.
        // Both manifests declare the command capabilities: registering a contribution or taking a service without declaring
        // its capability is refused by the host, which is why these definitions cannot use the shared Def helper.
        static ModuleDefinition CommandDef(string id, Func<IModuleContext, ValueTask> activate, params string[] depends) => new(
            new() { Id = id, Name = id, Version = "1.0.0", Dependencies = depends.Select(x => new ModuleDependency(x)).ToArray(),
                Capabilities = [ModuleApi.UiCommands, ModuleApi.CommandsInvoke] },
            "v1", true, () => new TestModule(activate));
        ModuleHost host = null!;
        host = new ModuleHost(Path.Combine(_root, Guid.NewGuid().ToString("N")), new Scheduler(), [], App.ModuleKinds(),
            [ModuleService.Of<IModulePresentation>(ModuleApi.UiPresentation, scope => new TestPresentation(scope)),
             ModuleService.Of<IModuleCommands>(ModuleApi.CommandsInvoke, scope => new ScopedCommands(() => host, scope))])
        { StartTimeout = TimeSpan.FromMilliseconds(250) };
        using var lifetime = host;
        var own = new TestCommand("own");
        var foreign = new TestCommand("foreign");
        bool scoped = false, listedOnlyOwn = false, commandAdded = false;
        await host.ReconcileAsync([
            CommandDef("alpha", c => { c.Register<IModuleCommand>("do", own); return ValueTask.CompletedTask; }),
            CommandDef("beta", c =>
            {
                // The service lists and invokes the caller's own command only. A registration made during activation is
                // staged until the activation commits, so the caller's own brand-new command is not listed yet — what the
                // list shows at this moment is what a command running later would see.
                c.Register<IModuleCommand>("foreign", foreign);
                var service = c.GetService<IModuleCommands>();
                commandAdded = service.List().Count == 0;
                listedOnlyOwn = service.List().All(command => command.Name != "own");
                scoped = ThrowsAsync(() => service.InvokeAsync("alpha:do"));
                return ValueTask.CompletedTask;
            }, depends: "alpha")
        ]);
        Check("the commands service shows a module only its own commands",
            commandAdded && host.Registry.List<IModuleCommand>().Count == 2 && host.Registry.List<IModuleCommand>().All(entry => entry.Id is "alpha:do" or "beta:foreign"));
        Check("invoking another module's command through the service throws instead of running it",
            scoped && own.Runs == 0 && foreign.Runs == 0);
        Check("the caller's list contains no other module's command", listedOnlyOwn);

        // ---- retirement takes the commands with it ----
        await host.ReconcileAsync([]);
        Check("stopping a module withdraws its commands", host.Registry.List<IModuleCommand>().Count == 0);

        // ---- settings keep a binding for a module that is not there ----
        var settings = new DeckSettings();
        // A recorded combination is saved in the canonical modifier order with the key's own spelling, which is what a
        // recording produces (the modifiers come from flags, the key from the WPF Key enum name).
        settings.SetModuleHotkey("thrm:open-page", "alt+ctrl+T");
        settings.Normalize();
        Check("a command binding survives normalizing while the module is absent",
            settings.ModuleHotkey("thrm:open-page") == "Ctrl+Alt+T");
        var copy = settings.Copy();
        copy.SetModuleHotkey("thrm:open-page", "");
        Check("the settings copy owns its binding map, so editing the copy leaves the original alone",
            settings.ModuleHotkey("thrm:open-page") == "Ctrl+Alt+T" && copy.ModuleHotkey("thrm:open-page") == "");
        var dirty = new DeckSettings();
        dirty.ModuleHotkeys["not a command id"] = "Ctrl+K";
        dirty.ModuleHotkeys["ok:cmd"] = "Ctrl+";     // not a hotkey, so it has no combination to register
        dirty.ModuleHotkeys["ok:other"] = "F9";
        dirty.Normalize();
        Check("normalizing drops impossible command ids and invalid key texts",
            dirty.ModuleHotkeys.Count == 1 && dirty.ModuleHotkey("ok:other") == "F9");
        Check("a binding for a command that is unset reads back empty", new DeckSettings().ModuleHotkey("x:y") == "");

        // ---- a command and a built-in shortcut never share a combination, in either direction ----
        // Both directions are checked through the same primitive the settings rows use, so the two sides cannot drift apart:
        // a row compares the combination being recorded against the other side's saved text, and Overlap is symmetric.
        Check("a command binding to a built-in combination is detected as a clash",
            HotkeyText.Overlap("ctrl+alt+t", HotkeyText.Normalize("Ctrl+Alt+T")));
        Check("an arrow shortcut covers its four arrow keys, so a command on one of them clashes",
            HotkeyText.Overlap("Ctrl+Alt+Left", HotkeyText.Normalize("Ctrl+Alt+Arrows"))
            && HotkeyText.Overlap("Ctrl+Alt+Arrows", HotkeyText.Normalize("Ctrl+Alt+Left")));
        // The clash a built-in row must report does not depend on what that row held before: changing a row from F10 to the
        // combination a command already has is a clash, even though F10 itself clashes with nothing.
        Check("a new recording clashes with a command even when the row's previous text did not",
            !HotkeyText.Overlap("Ctrl+Alt+F10", "Ctrl+Alt+F9") && HotkeyText.Overlap("Ctrl+Alt+F9", "Ctrl+Alt+F9"));
        Check("an unbound command never clashes with anything",
            !HotkeyText.Overlap("Ctrl+Alt+F9", "") && !HotkeyText.Overlap("", "Ctrl+Alt+F9") && !HotkeyText.Same("", ""));
    }

    private static bool ThrowsAsync(Func<Task> action)
    { try { action().GetAwaiter().GetResult(); return false; } catch { return true; } }

    // The same scoping rule as the app's service: only the caller's own commands are listed or reachable.
    private sealed class ScopedCommands(Func<ModuleHost> host, ModuleServiceScope scope) : IModuleCommands
    {
        public IReadOnlyList<ModuleCommand> List()
            => host().Registry.List<IModuleCommand>().Where(entry => entry.ModuleId == scope.ModuleId).Select(entry => entry.Value.Descriptor).ToArray();
        public Task<ModuleCommandResult> InvokeAsync(string commandId, IReadOnlyDictionary<string, string>? parameters = null,
            ModuleCommandOrigin origin = ModuleCommandOrigin.User, CancellationToken cancellation = default)
        {
            if (host().Registry.List<IModuleCommand>().FirstOrDefault(entry => entry.ModuleId == scope.ModuleId && entry.Id == commandId) is not { } entry)
                throw new ModuleApiException("unknown-command", "A module can invoke only its own commands: " + commandId);
            return entry.Value.ExecuteAsync(parameters, cancellation);
        }
        public bool ShowOwnPage(string pageId, bool userInitiated = true) => false;
    }
}
