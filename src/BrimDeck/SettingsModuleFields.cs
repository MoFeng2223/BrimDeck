using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private FrameworkElement ModuleTextField(ModuleSetting field, string current, Func<object, bool> write)
    {
        string title = Loc.T(field.Label, field.LabelEn), placeholder = Loc.T(field.Placeholder, field.PlaceholderEn);
        var box = new TextBox { Style = (Style)FindResource("SettingsField"), Text = current, MaxLength = field.MaximumLength,
            Width = 260, Height = 28, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(7, 2, 7, 2) };
        AutomationProperties.SetName(box, title); AutomationProperties.SetHelpText(box, placeholder);
        var holder = new Grid(); holder.Children.Add(box);
        var hint = TextLine(placeholder, 12, TextTertiary); hint.Margin = new Thickness(8, 0, 8, 0); hint.VerticalAlignment = VerticalAlignment.Center; hint.IsHitTestVisible = false; holder.Children.Add(hint);
        void UpdateHint() => hint.Visibility = box.Text.Length == 0 && !box.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
        void Commit()
        {
            if (box.Text == current) return;
            if (write(box.Text)) current = box.Text;
        }
        box.TextChanged += (_, _) => UpdateHint();
        box.GotKeyboardFocus += (_, _) => UpdateHint(); box.LostKeyboardFocus += (_, _) => { Commit(); UpdateHint(); };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { box.Text = current; box.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
        };
        UpdateHint();
        return Row(title, holder);
    }
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
