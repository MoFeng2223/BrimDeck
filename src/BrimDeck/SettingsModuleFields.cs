using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    // Text fields build through ModuleUi.TextRow now; the color picker stays host-side for its palette access.
    private FrameworkElement ModuleColorField(ModuleSetting field, string current, Func<object, bool> write)
    {
        string title = Loc.T(field.Label, field.LabelEn);
        var button = new Button { Style = (Style)FindResource("SettingsColorButton"), Foreground = UI.Brush(current) };
        AutomationProperties.SetName(button, title); AutomationProperties.SetHelpText(button, current);
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        void Rebuild() => popup.Child = ColorPicker(current, false, color =>
        {
            popup.IsOpen = false;
            if (!ModuleSettingsSchema.Color(color)) return;
            if (!write(color)) return;
            current = color; button.Foreground = UI.Brush(color); AutomationProperties.SetHelpText(button, color); Rebuild();
        }, allowDefault: false);
        Rebuild(); TogglePopup(button, popup);
        return Row(title, button);
    }
}
