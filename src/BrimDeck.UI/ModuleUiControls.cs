using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BrimDeck;

public static partial class ModuleUi
{
    public static Button Button(ModuleViewContext context, string label, Action clicked)
    {
        var button = new Button { Content = label, FontFamily = PanelFont, Padding = new Thickness(9, 4, 9, 4), Style = context.Owner.TryFindResource("PanelTextButton") as Style };
        button.SetResourceReference(Control.ForegroundProperty, "PanelPrimary"); AutomationProperties.SetName(button, label);
        button.Click += (_, e) => { if (!context.Lifetime.IsCancellationRequested && !context.Appearance.Preview) clicked(); e.Handled = true; }; return button;
    }
    public static Grid Ring(double percent, double size = 24, double thickness = 2)
    {
        var ring = UI.Ring(double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0, UI.Accent, Math.Clamp(size, 8, 256), Math.Clamp(thickness, .5, 8));
        for (int i = 0; i < ring.Children.Count; i++)
            if (ring.Children[i] is Shape shape) shape.SetResourceReference(Shape.StrokeProperty, i == 0 ? "PanelRingTrack" : "PanelAccent");
        return ring;
    }
    public static ProgressBar Progress(double percent)
    {
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0, Height = 3 };
        progress.SetResourceReference(Control.ForegroundProperty, "PanelAccent"); progress.SetResourceReference(Control.BackgroundProperty, "PanelTrack"); return progress;
    }
    public static Border Row(string title, string value)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(Centered(title, 12, Secondary)); var number = Text(value); Grid.SetColumn(number, 1); row.Children.Add(number);
        return new Border { Padding = new Thickness(8, 5, 8, 5), Child = row };
    }
    public static Border ValueCard(string title, string value)
    {
        var contents = new StackPanel(); contents.Children.Add(Centered(title, 11, Secondary)); contents.Children.Add(Text(value, 22));
        var card = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(10), Child = contents };
        card.SetResourceReference(Border.BackgroundProperty, "PanelFill"); return card;
    }
}
