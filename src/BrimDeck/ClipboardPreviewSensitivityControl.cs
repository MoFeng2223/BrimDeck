// Settings.renderHoverDelayControl / setting-slider-wrap from Edge-Drop 767b6d63 (Apache-2.0).
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

internal static class ClipboardPreviewSensitivityControl
{
    internal static FrameworkElement Create(int value, Action<int, bool> changed)
    {
        var root = new Grid { Margin = new Thickness(0, 12, 0, 16) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new DockPanel();
        var badge = new TextBlock { FontSize = 12, FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        badge.SetResourceReference(TextBlock.ForegroundProperty, "SettingsTextSecondary"); DockPanel.SetDock(badge, Dock.Right); heading.Children.Add(badge);
        var title = new TextBlock { Text = Loc.T("预览灵敏度", "Preview sensitivity"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "SettingsTextPrimary"); heading.Children.Add(title); root.Children.Add(heading);
        var description = new TextBlock { Text = Loc.T("向右更灵敏，仅影响首次展开；连续划过时预览立即跟随新卡片。", "Move right for a shorter first-hover delay; an open preview follows new cards immediately."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        description.SetResourceReference(TextBlock.ForegroundProperty, "SettingsTextSecondary"); Grid.SetRow(description, 1); root.Children.Add(description);
        var slider = new EdgeDropWakeSlider { Value = Math.Clamp(value, 0, 100) }; Grid.SetRow(slider, 2); root.Children.Add(slider);
        AutomationProperties.SetAutomationId(slider, "clipboard-preview-sensitivity"); AutomationProperties.SetName(slider, title.Text);
        AutomationProperties.SetAutomationId(badge, "clipboard-preview-delay");
        var labels = new Grid { Margin = new Thickness(2, 9, 2, 0) };
        labels.ColumnDefinitions.Add(new()); labels.ColumnDefinitions.Add(new()); labels.ColumnDefinitions.Add(new());
        var presets = new List<(int Value, Button Button)>();
        foreach (var (preset, caption) in new[] { (0, Loc.T("0% · 慢", "0% · Slow")), (50, "50%"), (100, Loc.T("100% · 立即", "100% · Instant")) })
        {
            var button = new Button { Content = caption, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 10.5, Padding = new Thickness(4, 1, 4, 1),
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalAlignment = preset == 0 ? HorizontalAlignment.Left : preset == 100 ? HorizontalAlignment.Right : HorizontalAlignment.Center };
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(FrameworkElement.MarginProperty, button.Padding); border.AppendChild(presenter);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            button.SetResourceReference(Control.FocusVisualStyleProperty, "SettingsFocus");
            button.Click += (_, _) => slider.Value = preset;
            Grid.SetColumn(button, presets.Count); labels.Children.Add(button); presets.Add((preset, button));
            AutomationProperties.SetAutomationId(button, "clipboard-preview-preset-" + preset); AutomationProperties.SetName(button, caption);
        }
        void Display()
        {
            int sensitivity = (int)slider.Value, delay = (100 - sensitivity) * 10;
            badge.Text = sensitivity.ToString(CultureInfo.InvariantCulture) + "% · " + (delay == 0 ? Loc.T("立即", "Instant") : delay.ToString(CultureInfo.InvariantCulture) + " ms");
            AutomationProperties.SetHelpText(slider, description.Text + " " + badge.Text);
            foreach (var (preset, button) in presets)
            {
                button.SetResourceReference(Control.ForegroundProperty, preset == sensitivity ? "SettingsTextPrimary" : "SettingsTextSecondary");
                button.FontWeight = preset == sensitivity ? FontWeights.Bold : FontWeights.Medium;
            }
        }
        Display(); slider.ValueChanged += (_, _) => { Display(); changed((int)slider.Value, false); };
        slider.ValueCommitted += current => changed((int)current, true);
        Grid.SetRow(labels, 3); root.Children.Add(labels);
        AutomationProperties.SetAutomationId(root, "clipboard-preview-sensitivity-control"); return root;
    }
}
