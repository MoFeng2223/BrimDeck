using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BrimDeck.Modules;

namespace BrimDeck.InspectionFixture;

public sealed class Probe : IModule
{
    [ModuleInitializer] public static void Initialize() => Touch("initializer");
    public Probe() => Touch("constructor");
    public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken) { Touch("activation"); return ValueTask.CompletedTask; }
    private static void Touch(string stage) { if (Environment.GetEnvironmentVariable("BRIMDECK_CHECK_SENTINEL") is { Length: > 0 } path) File.AppendAllText(path, stage); }
    public static Type ForbiddenDesktopDependency() => typeof(BrimDeck.App);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentProcessId();
}
public sealed class NoDefault(string value) : IModule
{
    public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public override string ToString() => value;
}
public abstract class GenericBase<T> : IModule
{
    public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
public sealed class GenericEntry : GenericBase<int> { }
internal sealed class Hidden
{
    public sealed class Nested : IModule
    {
        public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
