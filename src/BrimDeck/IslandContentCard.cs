// WPF translation of Edge-Drop ClipboardItem / item.css (767b6d63), Apache-2.0.
// The content/footer/actions layers follow upstream; palette, focus and motion belong to the host.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BrimDeck;

internal class IslandContentCard : Border
{
    protected readonly Grid Layers = new();
    protected readonly ContentControl Body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Focusable = false };
    protected readonly Grid Footer = new() { Margin = new Thickness(12, 1, 12, 6), Height = 18 };
    protected readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal };
    protected readonly Button Pin;
    private readonly Border _pill;
    private ListBoxItem? _row;
    private bool _pinned;
    internal bool ActionsVisible => _pill.Visibility == Visibility.Visible;
    internal UIElement? PreviewBody => Body.Content as UIElement;

    protected IslandContentCard()
    {
        SetResourceReference(BackgroundProperty, "PanelFill");
        SetResourceReference(BorderBrushProperty, "PanelPopupBorder");
        BorderThickness = new Thickness(1);
        SetResourceReference(CornerRadiusProperty, "PanelR12");
        SnapsToDevicePixels = true;
        Layers.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        Layers.RowDefinitions.Add(new() { Height = new GridLength(24) });
        Layers.Children.Add(Body); Grid.SetRow(Footer, 1); Layers.Children.Add(Footer);
        _pill = new Border { Child = Actions, Padding = new Thickness(2), Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden, BorderThickness = new Thickness(1) };
        _pill.SetResourceReference(BackgroundProperty, "PanelPopup"); _pill.SetResourceReference(BorderBrushProperty, "PanelPopupBorder"); _pill.SetResourceReference(CornerRadiusProperty, "PanelR12");
        Layers.Children.Add(_pill);
        Pin = ActionButton("☆", "", () => OnPin());
        Pin.HorizontalAlignment = HorizontalAlignment.Left; Pin.VerticalAlignment = VerticalAlignment.Top; Pin.Margin = new Thickness(4);
        Layers.Children.Add(Pin);
        Child = Layers;
        SizeChanged += (_, _) => { UpdateClip(); Footer.Visibility = ActualHeight < 64 ? Visibility.Collapsed : Visibility.Visible; };
        MouseEnter += (_, _) => UpdateActions(); MouseLeave += (_, _) => UpdateActions();
        IsKeyboardFocusWithinChanged += (_, _) => UpdateActions();
        Loaded += (_, _) =>
        {
            _row = Ancestor<ListBoxItem>(this);
            if (_row is not null) { _row.GotKeyboardFocus += RowFocus; _row.LostKeyboardFocus += RowFocus; }
            UpdateActions();
        };
        Unloaded += (_, _) => { if (_row is not null) { _row.GotKeyboardFocus -= RowFocus; _row.LostKeyboardFocus -= RowFocus; } _row = null; };
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Pixel themes change the corners without changing the card's measured size.
        if (e.Property == CornerRadiusProperty) UpdateClip();
    }
    private void UpdateClip() => Clip = new RectangleGeometry(new Rect(RenderSize), CornerRadius.TopLeft, CornerRadius.TopLeft);
    protected virtual void OnPin() { }
    protected void SetPinned(bool value, string name)
    {
        _pinned = value; Pin.Content = value ? "★" : "☆"; Pin.ToolTip = name; AutomationProperties.SetName(Pin, name); UpdateActions();
    }
    private void RowFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateActions();
    private void UpdateActions()
    {
        bool show = IsMouseOver || IsKeyboardFocusWithin || _row?.IsKeyboardFocusWithin == true;
        bool entering = show && _pill.Visibility != Visibility.Visible;
        _pill.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        _pill.IsHitTestVisible = show;
        Pin.Visibility = show || _pinned ? Visibility.Visible : Visibility.Hidden;
        Pin.Opacity = show || _pinned ? 1 : 0;
        Pin.IsHitTestVisible = show || _pinned;
        if (entering && IsLoaded) Soft.Enter(_pill);
    }
    protected Button AddAction(object content, string name, Action action)
    {
        var button = ActionButton(content, name, action); Actions.Children.Add(button); return button;
    }
    internal static Button ActionButton(object content, string name, Action action)
    {
        var button = new Button { Content = content, Width = 22, Height = 22, Padding = new Thickness(2), FontSize = 12, ToolTip = name };
        button.SetResourceReference(FrameworkElement.StyleProperty, "PanelIconButton");
        AutomationProperties.SetName(button, name);
        // A card action never bubbles into double-click reuse or a native drag press.
        button.Click += (_, e) => { e.Handled = true; action(); };
        return button;
    }
    private static T? Ancestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(node); parent is not null; parent = VisualTreeHelper.GetParent(parent)) if (parent is T value) return value;
        return null;
    }
}
