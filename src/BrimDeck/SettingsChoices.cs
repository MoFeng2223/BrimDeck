using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private FrameworkElement ChoiceRow<T>(string title, T selected, (T Value, string Label)[] options, Action<T> change, bool enabled = true, double width = 292) where T : notnull
    {
        var strip = new UniformGrid { Columns = options.Length, Width = width, IsEnabled = enabled };
        foreach (var option in options)
        {
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = option.Label, GroupName = title, IsChecked = EqualityComparer<T>.Default.Equals(option.Value, selected) };
            AutomationProperties.SetName(choice, title + " " + option.Label); choice.Checked += (_, _) => change(option.Value); strip.Children.Add(choice);
        }
        var row = Row(title, SegmentBorder(strip)); row.IsEnabled = enabled; return row;
    }
}
