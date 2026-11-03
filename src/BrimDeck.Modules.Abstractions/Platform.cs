namespace BrimDeck.Modules;

// Optional extension of the original entry contract: older modules keep using IModuleContext.
public interface IModulePlatform : IModuleContext
{
    IModuleRegistries Registries { get; }
    IModuleEvents Events { get; }
    IModuleServices Services { get; }
}

public static class ModuleContextExtensions
{
    public static IModulePlatform Platform(this IModuleContext context) => context as IModulePlatform
        ?? throw new ModuleApiException("platform-unavailable", "This module requires BrimDeck module contract 1.5.");
}

public interface IModuleRegistries
{
    IModuleRegistration Register<T>(string id, T contribution) where T : class;
}

public interface IModuleRegistration : IDisposable
{
    string Id { get; }
}

public interface IModuleServices { T Get<T>() where T : class; }

public enum ModuleEventPriority { Highest = 0, High = 1, Normal = 2, Low = 3, Lowest = 4 }

public interface IModuleEvents
{
    IDisposable On<T>(Action<T> listener, ModuleEventPriority priority = ModuleEventPriority.Normal);
    void Publish<T>(T message);
}

// Host-originated hooks use a one-way cancellation flag. A later handler cannot undo a cancellation.
public abstract class ModuleOpeningEvent(string contributionId) : IModuleHostEvent
{
    public string ContributionId { get; } = contributionId;
    public bool Cancelled { get; private set; }
    public void Cancel() => Cancelled = true;
}

public sealed class ModulePageOpening(string contributionId) : ModuleOpeningEvent(contributionId);
public sealed class ModuleActivityOpening(string contributionId) : ModuleOpeningEvent(contributionId);

// Scoped by the host: a module can request a refresh, never supply another module's identity.
public interface IModulePresentation
{
    void Invalidate();
}
