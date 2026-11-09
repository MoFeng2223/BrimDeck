using BrimDeck.Modules;
using Loc = BrimDeck.ModuleLocalization;

namespace BrimDeck.Thrm;

// "Open the cooler page", reachable from a global shortcut, the tray menu and a page button. It runs through the command
// channel so there is one implementation, and it needs the commands service (commands.invoke) to ask the host for its page.
internal sealed class ThermCommands(IModuleContext context) : IModuleCommand
{
    public ModuleCommand Descriptor { get; } = new("打开散热器页", "Open cooler page")
    {
        Description = "展开面板并切到散热器的温度与风扇页。",
        DescriptionEn = "Expand the panel and switch to the cooler's temperature and fan page.",
        Requires = [ModuleApi.CommandsInvoke]
    };

    // The page is always there while the module is running, so the command can always act.
    public bool CanExecute => true;

    public Task<ModuleCommandResult> ExecuteAsync(IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // userInitiated: only a real click or key press reaches here (the host passes User for both); a background caller
        // would be refused rather than pull the panel open.
        return Task.FromResult(context.GetService<IModuleCommands>().ShowOwnPage(ThermModule.PageId)
            ? ModuleCommandResult.Completed
            : ModuleCommandResult.Rejected(Loc.T("散热器页现在打不开。", "The cooler page cannot be opened right now.")));
    }
}
