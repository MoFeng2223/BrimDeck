using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BrimDeck;

// Settings-styled rows for module-built UI: the host's rendered module settings pages and a module's own settings view
// (IModuleCustomSettings) build the same rows through these helpers, so both carry the settings window's look. Styles
// and colors resolve from the window's resources (SettingsResources.xaml + SettingsPalette.Apply) through the scope
// element; outside the settings window they fall back to plain controls.
public static partial class ModuleUi
{
    // A toggle on its own 44 DIP line; the label sits inside the switch's content like the host's rows do.
    public static FrameworkElement ToggleRow(FrameworkElement scope, string title, bool value, Action<bool> changed, string? description = null)
    {
        var toggle = new CheckBox { Content = SettingText(title, 13, "SettingsTextPrimary", FontWeights.SemiBold), Style = Style(scope, "SettingsToggle"), IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title);
        toggle.Checked += (_, _) => changed(true); toggle.Unchecked += (_, _) => changed(false);
        return WithDescription(new Border { Child = toggle, Padding = new Thickness(0), Height = 44 }, description);
    }

    // A slider with a framed number box (value, unit, divider, steppers) on one line; the label keeps a fixed column so
    // rows of a page align.
    public static FrameworkElement SliderRow(FrameworkElement scope, string title, double value, double min, double max, double step,
        string? unit, Action<double> changed, string? description = null, bool fractional = false)
    {
        var row = new Grid { Height = 44 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(174) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = SettingText(title, 13, "SettingsTextPrimary", FontWeights.SemiBold); label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        var (slider, number) = Numeric(scope, title, value, min, max, step, unit ?? "", changed, fractional);
        slider.Margin = new Thickness(0, 0, 18, 0); Grid.SetColumn(slider, 1); row.Children.Add(slider);
        number.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(number, 2); row.Children.Add(number);
        return WithDescription(row, description);
    }

    // A segmented choice strip; the chosen option carries the shared highlight (placed statically, without the host's
    // sliding animation).
    public static FrameworkElement ChoiceRow(FrameworkElement scope, string title, int selected, (int Value, string Label)[] options, Action<int> changed, string? description = null)
    {
        var strip = new UniformGrid { Columns = Math.Max(1, options.Length), Width = 292 };
        foreach (var option in options)
        {
            var choice = new RadioButton { Style = Style(scope, "SettingsSegment"), Content = option.Label, GroupName = title, IsChecked = option.Value == selected };
            AutomationProperties.SetName(choice, title + " " + option.Label);
            choice.Checked += (_, _) => changed(option.Value);
            strip.Children.Add(choice);
        }
        return WithDescription(SettingsRow(title, Segment(scope, strip)), description);
    }

    // A text field with a placeholder hint; commit runs on Enter and on losing focus, Escape reverts to the last
    // accepted text. A commit that returns false leaves the field as typed (the host reports the failure itself).
    public static FrameworkElement TextRow(FrameworkElement scope, string title, string value, Func<string, bool> commit,
        string? placeholder = null, int maxLength = 0, string? description = null)
    {
        string current = value ?? "";
        var box = new TextBox { Style = Style(scope, "SettingsField"), Text = current, MaxLength = maxLength, Width = 260, Height = 28,
            VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(7, 2, 7, 2) };
        AutomationProperties.SetName(box, title); AutomationProperties.SetHelpText(box, placeholder ?? "");
        var holder = new Grid(); holder.Children.Add(box);
        TextBlock? hint = null;
        if (!string.IsNullOrEmpty(placeholder))
        {
            hint = SettingText(placeholder, 12, "SettingsTextTertiary"); hint.Margin = new Thickness(8, 0, 8, 0);
            hint.VerticalAlignment = VerticalAlignment.Center; hint.IsHitTestVisible = false; holder.Children.Add(hint);
        }
        void UpdateHint() { if (hint is not null) hint.Visibility = box.Text.Length == 0 && !box.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed; }
        void Accept()
        {
            if (box.Text == current) return;
            if (commit(box.Text)) current = box.Text;
        }
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { box.Text = current; box.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        };
        box.TextChanged += (_, _) => UpdateHint();
        box.GotKeyboardFocus += (_, _) => UpdateHint();
        box.LostKeyboardFocus += (_, _) => { Accept(); UpdateHint(); };
        UpdateHint();
        return WithDescription(SettingsRow(title, holder), description);
    }

    // One line per setting: the label at the left, the control at the right.
    private static Grid SettingsRow(string title, FrameworkElement control)
    {
        var row = new Grid { MinHeight = 44 };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = SettingText(title, 13, "SettingsTextPrimary", FontWeights.SemiBold); label.VerticalAlignment = VerticalAlignment.Center;
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 12, 0, 12) };
        labels.Children.Add(label);
        row.Children.Add(labels);
        control.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(control, 1); row.Children.Add(control);
        return row;
    }

    // The description a row carries reads as a wrapped note under it, the way the host's module settings pages draw one.
    private static FrameworkElement WithDescription(FrameworkElement row, string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return row;
        var panel = new StackPanel();
        panel.Children.Add(row);
        var note = SettingText(description, 11.5, "SettingsTextSecondary"); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, -2, 0, 10);
        panel.Children.Add(note);
        return panel;
    }

    private static Style? Style(FrameworkElement scope, string key) => scope.TryFindResource(key) as Style;

    // A text styled like the settings window's: the color rides a dynamic resource reference, so palette changes recolor
    // built rows in place.
    private static TextBlock SettingText(string text, double size, string resourceKey, FontWeight? weight = null)
    {
        var block = UI.Line(text, size, "#F1F1F4", weight); block.FontFamily = UI.PanelFont;
        block.SetResourceReference(TextBlock.ForegroundProperty, resourceKey);
        return block;
    }

    // The segmented control's highlight: one fill behind the chosen option, placed on check and on layout, without the
    // slide between options the settings window's own rows perform.
    private static Border Segment(FrameworkElement scope, UniformGrid strip)
    {
        var options = strip.Children.OfType<RadioButton>().ToList();
        var highlight = new Border { CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, Visibility = Visibility.Hidden, BorderThickness = new Thickness(1) };
        highlight.SetResourceReference(Border.BackgroundProperty, "SettingsFillSelected");
        highlight.SetResourceReference(Border.BorderBrushProperty, "SettingsInputBorder");
        var host = new Grid(); host.Children.Add(highlight); host.Children.Add(strip);
        void Place()
        {
            var chosen = options.FirstOrDefault(option => option.IsChecked == true);
            if (chosen is null || chosen.ActualWidth <= 0 || !host.IsAncestorOf(chosen)) { highlight.Visibility = Visibility.Hidden; return; }
            var at = chosen.TranslatePoint(new Point(), host);
            highlight.Width = chosen.ActualWidth; highlight.Height = chosen.ActualHeight; highlight.Margin = new Thickness(0, at.Y, 0, 0);
            highlight.Visibility = Visibility.Visible;
        }
        foreach (var option in options) option.Checked += (_, _) => Place();
        host.Loaded += (_, _) => Place(); host.SizeChanged += (_, _) => Place();
        var border = new Border { Child = host, CornerRadius = new CornerRadius(7), Padding = new Thickness(3), Margin = new Thickness(0, 4, 0, 4) };
        border.SetResourceReference(Border.BackgroundProperty, "SettingsInset");
        return border;
    }

    // The slider plus framed number box of the settings window: the value, its unit, a divider and two steppers in one
    // frame; a click on the frame focuses the field, and the frame takes the accent color while the field has the focus.
    private static (Slider Slider, Border Number) Numeric(FrameworkElement scope, string title, double value, double min, double max,
        double tick, string unit, Action<double> changed, bool fractional)
    {
        if (!(tick > 0)) tick = 1;
        string Text(double number) => number.ToString(fractional ? "G15" : "0", CultureInfo.InvariantCulture);
        var slider = new Slider { Style = Style(scope, "SettingsSlider"), Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
            SmallChange = tick, LargeChange = tick * 2, TickFrequency = tick, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        var input = new TextBox { Style = Style(scope, "SettingsNumber"), Width = fractional ? 56 : 40, Text = Text(slider.Value), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, title); AutomationProperties.SetName(input, ModuleLocalization.T(title + "数值", title + " value"));
        var units = SettingText(unit, 10, "SettingsTextTertiary"); units.Margin = new Thickness(4, 0, 6, 0); units.VerticalAlignment = VerticalAlignment.Center;
        var minus = new RepeatButton { Style = Style(scope, "SettingsStepper"), Content = "−" };
        var plus = new RepeatButton { Style = Style(scope, "SettingsStepper"), Content = "+" };
        AutomationProperties.SetName(minus, ModuleLocalization.T("减少" + title, "Decrease " + title)); AutomationProperties.SetName(plus, ModuleLocalization.T("增加" + title, "Increase " + title));
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(input); line.Children.Add(units);
        var divider = new Border { Width = 1, Margin = new Thickness(2, 5, 3, 5) };
        divider.SetResourceReference(Border.BackgroundProperty, "SettingsDivider");
        line.Children.Add(divider); line.Children.Add(minus); line.Children.Add(plus);
        var chip = new Border { Child = line, Height = 30, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(7, 0, 2, 0) };
        chip.SetResourceReference(Border.BackgroundProperty, "SettingsInput");
        chip.SetResourceReference(Border.BorderBrushProperty, "SettingsInputBorder");
        chip.MouseLeftButtonDown += (_, e) => { if (e.Handled) return; input.Focus(); input.SelectAll(); e.Handled = true; };
        input.IsKeyboardFocusWithinChanged += (_, _) =>
        {
            chip.BorderBrush = scope.TryFindResource(input.IsKeyboardFocusWithin ? "SettingsAccent" : "SettingsInputBorder") as Brush;
        };
        void Set(double number)
        {
            double steps = (number - min) / tick;
            double next = fractional ? number : Math.Round(number);
            if (fractional && double.IsFinite(steps)) next = min + Math.Round(steps) * tick;
            next = Math.Clamp(next, min, max);
            if (fractional ? slider.Value == next : Math.Abs(slider.Value - next) < .01) { input.Text = Text(next); return; }
            slider.Value = next;
        }
        slider.ValueChanged += (_, _) => { input.Text = Text(slider.Value); changed(slider.Value); };
        void Accept()
        {
            if (double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) Set(number);
            input.Text = Text(slider.Value);
        }
        minus.Click += (_, _) => Set(slider.Value - tick); plus.Click += (_, _) => Set(slider.Value + tick);
        input.LostKeyboardFocus += (_, _) => Accept();
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } if (e.Key == Key.Escape) { input.Text = Text(slider.Value); e.Handled = true; } };
        return (slider, chip);
    }
}
