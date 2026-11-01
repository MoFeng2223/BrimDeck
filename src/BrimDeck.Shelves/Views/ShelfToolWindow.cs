// Adapted from DropShelf 2.6.7 ShelfWindow.xaml, ShelfWindow.xaml.cs and ShelfWindow.Entrance.cs (MIT).
// This shares the original tool-window chrome, controls and motion without creating a shelf model or drop target.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using DropShelf.Interop;
using DropShelf.Services;

namespace DropShelf.Views;

public class ShelfToolWindow : Window
{
    private readonly Border _root;
    private readonly Grid _surface;
    private readonly ScaleTransform _surfaceScale = new();
    private readonly TranslateTransform _surfaceOffset = new();
    private readonly ToggleButton _pin;
    private readonly Button _close;
    private readonly TextBlock _heading;
    private readonly ContentControl _body;
    private bool _closing, _closed;

    public ShelfToolWindow()
    {
        if (HostedResources.IsHosted) HostedResources.Attach(this);
        else
        {
            // Standalone UI checks use the original resources too, without initializing hooks or the shelf controller.
            Resources.MergedDictionaries.Add(new() { Source = new Uri("pack://application:,,,/DropShelf;component/Themes/Tokens.xaml") });
            Resources.MergedDictionaries.Add(new() { Source = new Uri("pack://application:,,,/DropShelf;component/Themes/Controls.xaml") });
        }
        Style = (Style)FindResource("AppWindowStyle");
        WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent; MinWidth = 280; MinHeight = 180;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(4), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        _surface = new Grid { RenderTransformOrigin = new Point(.5, .4) };
        _surface.RenderTransform = new TransformGroup { Children = { _surfaceScale, _surfaceOffset } };
        _surface.RowDefinitions.Add(new() { Height = new GridLength(40) }); _surface.RowDefinitions.Add(new());
        var header = new Grid { Margin = new Thickness(4, 4, 4, 0), Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _pin = new ToggleButton { Style = (Style)FindResource("IconToggleButtonStyle"), Width = 28, Height = 30, FontSize = 14, IsChecked = true };
        _pin.Checked += (_, _) => { Topmost = true; RefreshPinGlyph(); }; _pin.Unchecked += (_, _) => { Topmost = false; RefreshPinGlyph(); };
        AutomationProperties.SetAutomationId(_pin, "preview-window-pin"); header.Children.Add(_pin);
        _heading = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), IsHitTestVisible = false };
        _heading.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); Grid.SetColumn(_heading, 1); header.Children.Add(_heading);
        _close = new Button { Style = (Style)FindResource("IconButtonStyle"), Width = 28, Height = 30, FontSize = 11, Content = FindResource("Glyph.Close") };
        _close.Click += (_, _) => CloseAnimated(); AutomationProperties.SetAutomationId(_close, "preview-window-close"); Grid.SetColumn(_close, 2); header.Children.Add(_close);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || WithinButton(e.OriginalSource as DependencyObject)) return;
            FinishMotion(); DragMove(); WindowPlacement.KeepInWorkArea(this); e.Handled = true;
        };
        _surface.Children.Add(header);
        _body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(_body, 1); _surface.Children.Add(_body);
        _root = new Border { Child = _surface }; _root.SetResourceReference(Border.BackgroundProperty, "ShelfSolidBrush"); Content = _root;
        ThemeService.ThemeChanged += ThemeChanged;
        SourceInitialized += (_, _) =>
        {
            WindowEffects.SetToolWindow(this, noActivate: false);
            var handle = WindowEffects.Handle(this);
            var style = NativeMethods.GetWindowLong(handle, -16) & ~(0x00080000 | 0x00020000 | 0x00010000);
            NativeMethods.SetWindowLong(handle, -16, style);
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
            ApplyWindowTheme();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) { CloseAnimated(); e.Handled = true; } };
        RefreshPinGlyph();
    }

    public bool IsClosing => _closing || _closed;
    public Func<bool>? AnimationPolicy { get; set; }
    private bool MotionEnabled => AnimationPolicy?.Invoke() ?? ShelfMotion.Enabled;
    public void RefreshMotionPolicy() { if (!MotionEnabled && !_closing) FinishMotion(); }
    protected object? PreviewContent { get => _body.Content; set => _body.Content = value; }
    protected void SetCaption(string title, string pin, string close)
    {
        Title = _heading.Text = title;
        AutomationProperties.SetName(this, title); AutomationProperties.SetAutomationId(this, "clipboard-preview-window");
        _pin.ToolTip = pin; AutomationProperties.SetName(_pin, pin);
        _close.ToolTip = close; AutomationProperties.SetName(_close, close);
    }
    protected void SetContentScale(double scale) => _surface.LayoutTransform = new ScaleTransform(scale, scale);
    protected static (Rect Work, double Dpi) WorkArea(Point point)
    {
        var (work, dpi) = WindowPlacement.GetMonitor(new POINT((int)Math.Round(point.X), (int)Math.Round(point.Y)));
        return (new Rect(work.Left, work.Top, work.Width, work.Height), dpi);
    }
    protected void ShowAt(Rect pixelBounds, bool activate)
    {
        var center = new POINT((int)Math.Round(pixelBounds.Left + pixelBounds.Width / 2), (int)Math.Round(pixelBounds.Top + pixelBounds.Height / 2));
        var (work, dpi) = WindowPlacement.GetMonitor(center);
        MaxWidth = work.Width / dpi; MaxHeight = work.Height / dpi;
        MinWidth = Math.Min(MinWidth, MaxWidth); MinHeight = Math.Min(MinHeight, MaxHeight);
        Width = Math.Min(pixelBounds.Width / dpi, MaxWidth); Height = Math.Min(pixelBounds.Height / dpi, MaxHeight);
        ShowActivated = activate;
        WindowPlacement.CenterOn(this, center, Width, Height); Show(); WindowPlacement.CenterOn(this, center, Width, Height);
        if (activate) { Activate(); Focus(); }
        AnimateIn();
    }
    public void BringForward(bool activate = true)
    {
        if (IsClosing) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        WindowPlacement.KeepInWorkArea(this);
        if (activate) { Activate(); Focus(); }
    }
    public void CloseAnimated()
    {
        if (IsClosing) return;
        _closing = true; IsHitTestVisible = false;
        if (!MotionEnabled) { Close(); return; }
        FinishMotion();
        var fade = new DoubleAnimation(0, ShelfMotion.CloseDuration) { EasingFunction = HostedResources.MotionEase ?? new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => { if (!_closed) Close(); };
        _surfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(ShelfMotion.CloseScale, ShelfMotion.CloseDuration) { EasingFunction = fade.EasingFunction });
        _surfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(ShelfMotion.CloseScale, ShelfMotion.CloseDuration) { EasingFunction = fade.EasingFunction });
        _surface.BeginAnimation(OpacityProperty, fade);
    }
    public void CloseImmediately() { if (_closed) return; _closing = true; FinishMotion(); Close(); }
    private void AnimateIn()
    {
        FinishMotion();
        if (!MotionEnabled) return;
        var motion = ShelfMotion.For(ShelfEntrance.Plain);
        _surface.CacheMode = new BitmapCache(CacheScale.For(this));
        _surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, motion.FadeDuration));
        var scale = new DoubleAnimation(motion.StartScale, 1, motion.Duration) { EasingFunction = motion.ScaleEase, FillBehavior = FillBehavior.Stop };
        scale.Completed += (_, _) => _surface.CacheMode = null;
        _surfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale); _surfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        _surfaceOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, motion.Duration) { EasingFunction = motion.ScaleEase, FillBehavior = FillBehavior.Stop });
    }
    private void FinishMotion()
    {
        _surface.BeginAnimation(OpacityProperty, null); _surface.Opacity = 1;
        _surfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); _surfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); _surfaceScale.ScaleX = _surfaceScale.ScaleY = 1;
        _surfaceOffset.BeginAnimation(TranslateTransform.YProperty, null); _surfaceOffset.Y = 0; _surface.CacheMode = null;
    }
    private void RefreshPinGlyph() => _pin.Content = FindResource(Topmost ? "Glyph.Pinned" : "Glyph.Pin");
    private void ThemeChanged(object? sender, EventArgs args) { if (new WindowInteropHelper(this).Handle != IntPtr.Zero) ApplyWindowTheme(); }
    private void ApplyWindowTheme()
    {
        WindowEffects.SetRoundedCorners(this, square: HostedResources.SquareCorners?.Invoke() == true);
        WindowEffects.ApplyTheme(this, SystemBackdrop.None, ThemeService.IsDark);
        HostedResources.DressShelf?.Invoke(this, _root);
        if (!MotionEnabled && !_closing) FinishMotion();
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi); ApplyWindowTheme(); WindowPlacement.KeepInWorkArea(this);
    }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true; ThemeService.ThemeChanged -= ThemeChanged; FinishMotion(); _body.Content = null;
        base.OnClosed(e);
    }
    private static bool WithinButton(DependencyObject? node)
    {
        while (node is not null) { if (node is ButtonBase) return true; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node); }
        return false;
    }
}
