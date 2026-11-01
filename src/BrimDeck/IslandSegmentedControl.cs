// WPF translation of Edge-Drop SegmentedControl.tsx (767b6d63), Apache-2.0.
// Equal slots, a preview thumb during capture, commit on release, cancel on lost capture; roving arrow/Home/End focus.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BrimDeck;

internal sealed class IslandSegmentedControl : Grid
{
    internal Button[] Buttons { get; } = [];
    private readonly Border _thumb;
    private readonly TranslateTransform _shift = new();
    private int _selected, _preview = -1;
    private const double Slot = 48;
    internal IslandSegmentedControl(string[] labels)
    {
        Height = 24; Width = labels.Length * Slot;
        var track = new Border(); track.SetResourceReference(Border.BackgroundProperty, "PanelSwitchFill"); track.SetResourceReference(Border.CornerRadiusProperty, "PanelR12"); Children.Add(track);
        _thumb = new Border { Width = Slot, Height = 24, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, RenderTransform = _shift };
        _thumb.SetResourceReference(Border.BackgroundProperty, "PanelFillSelected"); _thumb.SetResourceReference(Border.CornerRadiusProperty, "PanelR12"); Children.Add(_thumb);
        Buttons = labels.Select((label, i) =>
        {
            var button = new Button { Content = label, Width = Slot, Height = 24, Margin = new Thickness(i * Slot, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, FontSize = 10.5, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), IsTabStop = i == 0 };
            button.SetResourceReference(StyleProperty, "PanelIconButton");
            button.PreviewKeyDown += (_, e) =>
            {
                int next = e.Key switch { Key.Right or Key.Down => Math.Min(labels.Length - 1, i + 1), Key.Left or Key.Up => Math.Max(0, i - 1), Key.Home => 0, Key.End => labels.Length - 1, _ => -1 };
                if (next < 0) return; e.Handled = true; Choose(next); Buttons[next].Focus();
            };
            Children.Add(button); return button;
        }).ToArray();
        PreviewMouseLeftButtonDown += (_, e) => { _preview = At(e.GetPosition(this).X); e.Handled = true; CaptureMouse(); Draw(_preview); };
        PreviewMouseMove += (_, e) => { if (_preview < 0) return; int next = At(e.GetPosition(this).X); if (next != _preview) { _preview = next; Draw(next); } e.Handled = true; };
        PreviewMouseLeftButtonUp += (_, e) => { if (_preview < 0) return; int next = _preview; _preview = -1; ReleaseMouseCapture(); e.Handled = true; Choose(next); Draw(_selected); };
        LostMouseCapture += (_, _) => { _preview = -1; Draw(_selected); };
    }
    private int At(double x) => Math.Clamp((int)(x / Slot), 0, Buttons.Length - 1);
    private void Choose(int index) { if (index != _selected) Buttons[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    internal void Select(int index)
    {
        _selected = index;
        for (int i = 0; i < Buttons.Length; i++) Buttons[i].IsTabStop = i == index;
        if (_preview < 0) Draw(index);
    }
    private void Draw(int index)
    {
        double from = _shift.X, to = index * Slot; _shift.BeginAnimation(TranslateTransform.XProperty, null); _shift.X = to;
        if (Soft.For(this) && IsLoaded) _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(Soft.Choose)) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
    }
}
