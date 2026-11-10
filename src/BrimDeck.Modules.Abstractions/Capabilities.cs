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
        Core<IModuleHostState>(ModuleApi.HostState, ModuleCapabilityKind.Service, "读取面板状态", "Read panel state", 10, ModuleApi.HostState),
        Core<IModuleHostControl>(ModuleApi.HostControl, ModuleCapabilityKind.Service, "请求宿主操作", "Ask the host to act", 10, ModuleApi.HostControl),
        // Each action of IModuleHostControl is granted apart; a manifest lists only what it uses.
        Core<IModulePageControl>(ModuleHostControlActions.Page, ModuleCapabilityKind.Service, "打开自己的页面", "Open own page", 10, ModuleHostControlActions.Page),
        Core<IModulePanelControl>(ModuleHostControlActions.Expand, ModuleCapabilityKind.Service, "请求展开面板", "Request expanding", 10, ModuleHostControlActions.Expand),
        Core<IModuleMediaControl>(ModuleHostControlActions.Media, ModuleCapabilityKind.Service, "播放或暂停媒体", "Play or pause media", 10, ModuleHostControlActions.Media),
        Core<IModuleAccentControl>(ModuleHostControlActions.Accent, ModuleCapabilityKind.Service, "临时强调色", "Temporary accent", 10, ModuleHostControlActions.Accent),
        Core<IModuleNotifications>(ModuleApi.NotificationsShow, ModuleCapabilityKind.Service, "显示岛内提醒", "Show island notices", 1, ModuleApi.NotificationsShow),
        Core<IModulePresentation>(ModuleApi.UiPresentation, ModuleCapabilityKind.Service, "刷新模组界面", "Refresh module presentation", 5, null),
        Core<IModuleCommands>(ModuleApi.CommandsInvoke, ModuleCapabilityKind.Service, "调用自己的命令", "Invoke own commands", 9, null),
        Core<IModuleCommand>(ModuleApi.UiCommands, ModuleCapabilityKind.Contribution, "插件命令", "Commands", 9, null),
        Ui(ModuleApi.UiPages, "小程序页面", "Mini-app pages", "BrimDeck.ModulePage"),
        Ui(ModuleApi.UiActivities, "持续活动", "Persistent activities", "BrimDeck.IModuleActivity"),
        Ui(ModuleApi.UiWidgets, "组件", "Widgets", "BrimDeck.ModuleWidget"),
        Ui(ModuleApi.UiCompactParts, "收起行部件", "Compact-row parts", "BrimDeck.IModuleCompactPart"),
        Ui(ModuleApi.UiSettings, "设置", "Settings", "BrimDeck.IModuleSettings"),
        Core<IModuleStorage>(ModuleApi.StorageData, ModuleCapabilityKind.Intrinsic, "模组数据", "Module data", 0, null) with { Environment = "any" }
    });
    private static ModuleCapability Core<T>(string id, ModuleCapabilityKind kind, string name, string nameEn, int minor, string? permission)
        => new(id, kind, name, nameEn, minor, typeof(T).Assembly.GetName().Name!, typeof(T).FullName!, permission);
    private static ModuleCapability Ui(string id, string name, string nameEn, string contract)
        => new(id, ModuleCapabilityKind.Contribution, name, nameEn, 0, "BrimDeck.UI", contract, null);
}
