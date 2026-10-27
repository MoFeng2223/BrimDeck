namespace BrimDeck.Modules;

public enum ModuleCapabilityKind { Intrinsic, Service, Contribution }

// Strings describe UI contracts without introducing a WPF dependency into the core SDK.
public sealed record ModuleCapability(string Id, ModuleCapabilityKind Kind, string Name, string NameEn,
    int IntroducedMinor, string ContractAssembly, string ContractType, string? Permission)
{
    public string Environment { get; init; } = "windows-desktop";
    public string ResourceLifetime { get; init; } = "activation";
    public bool Experimental { get; init; } = true;
}

internal static class ModuleCapabilities
{
    internal static IReadOnlyList<ModuleCapability> Create() => Array.AsReadOnly(new[]
    {
        Core<IModuleHostInfo>(ModuleApi.HostInfo, ModuleCapabilityKind.Intrinsic, "宿主信息", "Host information", 1, null) with { Environment = "any" },
        Core<IModuleUsage>(ModuleApi.UsageRead, ModuleCapabilityKind.Service, "读取用量", "Read usage", 1, ModuleApi.UsageRead),
        Core<IModuleMedia>(ModuleApi.MediaRead, ModuleCapabilityKind.Service, "读取正在播放", "Read now playing", 1, ModuleApi.MediaRead),
        Core<IModuleSystem>(ModuleApi.SystemRead, ModuleCapabilityKind.Service, "读取系统监控", "Read system metrics", 1, ModuleApi.SystemRead),
        Core<IModuleNotifications>(ModuleApi.NotificationsShow, ModuleCapabilityKind.Service, "显示岛内提醒", "Show island notices", 1, ModuleApi.NotificationsShow),
        Core<IModulePresentation>(ModuleApi.UiPresentation, ModuleCapabilityKind.Service, "刷新模组界面", "Refresh module presentation", 5, null),
        Ui(ModuleApi.UiPages, "小程序页面", "Mini-app pages", "BrimDeck.ModulePage"),
        Ui(ModuleApi.UiActivities, "持续活动", "Persistent activities", "BrimDeck.IModuleActivity"),
        Ui(ModuleApi.UiWidgets, "组件", "Widgets", "BrimDeck.ModuleWidget"),
        Ui(ModuleApi.UiSettings, "设置", "Settings", "BrimDeck.IModuleSettings"),
        Core<IModuleStorage>(ModuleApi.StorageData, ModuleCapabilityKind.Intrinsic, "模组数据", "Module data", 0, null) with { Environment = "any" }
    });
    private static ModuleCapability Core<T>(string id, ModuleCapabilityKind kind, string name, string nameEn, int minor, string? permission)
        => new(id, kind, name, nameEn, minor, typeof(T).Assembly.GetName().Name!, typeof(T).FullName!, permission);
    private static ModuleCapability Ui(string id, string name, string nameEn, string contract)
        => new(id, ModuleCapabilityKind.Contribution, name, nameEn, 0, "BrimDeck.UI", contract, null);
}
