using System.Windows.Media;
using BrimDeck.Modules;

namespace BrimDeck;

internal sealed record ModuleSettingsEntry(Contribution<IModuleSettings> Contribution, ModuleSettingsPlacement Placement, bool Visible, int Order, string? IconPath);

// All navigation paths consume this single projection. One settings contribution produces at most one sidebar item.
internal static class ModuleSettingsIndex
{
    private sealed record Metadata(ModuleSettingsPlacement Placement, int Order, string? IconPath);
    internal static IReadOnlyList<ModuleSettingsEntry> Create(ModuleHost host, bool miniAppsEnabled)
    {
        var widgets = host.Registry.List<ModuleWidget>();
        var result = new List<ModuleSettingsEntry>();
        foreach (var entry in host.Registry.List<IModuleSettings>())
        {
            var linked = widgets.Where(w => w.ModuleId == entry.ModuleId && w.Value.SettingsId == entry.Id).ToArray();
            var metadata = host.Invoke(entry.ModuleId, "settings navigation", () => entry.Value is IModuleSettingsNavigation navigation
                ? new Metadata(navigation.Placement, navigation.Order, navigation.IconPath) : null);
            var declared = metadata?.Placement ?? ModuleSettingsPlacement.Automatic;
            if (!Enum.IsDefined(declared))
            { host.Diagnose(entry.ModuleId, entry.Id, "invalid-settings-placement", "Invalid placement; automatic placement is used."); declared = ModuleSettingsPlacement.Automatic; }
            var placement = declared == ModuleSettingsPlacement.Automatic ? linked.Length == 0 ? ModuleSettingsPlacement.Modules : ModuleSettingsPlacement.MiniApps : declared;
            bool visible = host.Invoke(entry.ModuleId, "settings visibility", () => entry.Value.Visible);
            if (placement == ModuleSettingsPlacement.MiniApps)
                visible &= miniAppsEnabled && (linked.Length == 0 || linked.Any(w => host.Invoke(w.ModuleId, "widget state", w.Value.Enabled)));
            int WidgetOrder(Contribution<ModuleWidget> widget) => widget.Value.ReadOrder is { } read ? host.Invoke(widget.ModuleId, "widget order", read) : widget.Value.Order;
            int order = metadata?.Order ?? (linked.Length > 0 ? linked.Min(WidgetOrder) : 500);
            string? icon = metadata?.IconPath ?? linked.Select(w => w.Value.IconPath).FirstOrDefault(p => p is not null);
            if (icon is not null)
            {
                try { if (icon.Length > 4096) throw new ArgumentException(); _ = Geometry.Parse(icon); }
                catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException)
                { host.Diagnose(entry.ModuleId, entry.Id, "invalid-settings-icon", "Invalid icon; the default icon is used."); icon = null; }
            }
            result.Add(new(entry, placement, visible, order, icon));
        }
        return result.OrderBy(e => e.Contribution.ModuleId, StringComparer.Ordinal).ThenBy(e => e.Order).ThenBy(e => e.Contribution.Id, StringComparer.Ordinal).ToArray();
    }
}
