using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private bool IsDockPage => IsClipboardPage || IsTrayPage;
    private ClipboardHeaderMode DockHeaderMode => ReducedModule ? ClipboardHeaderMode.Always : IsTrayPage ? Settings.Tray.HeaderMode : Settings.Clipboard.HeaderMode;
    private const double ClipboardHeaderHeight = 36;
    private bool _clipboardHeaderRevealed, _clipboardHeaderConfigured, _clipboardHeaderEdgeArmed;
    private double _clipboardHeaderTarget;
    private DeckPage? _dockHeaderPage;
    private readonly DispatcherTimer _clipboardHeaderLeave = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly TranslateTransform _clipboardHeaderShift = new();
    private static readonly DependencyProperty ClipboardHeaderProgressProperty = DependencyProperty.Register("ClipboardHeaderProgress", typeof(double), typeof(MainWindow),
        new PropertyMetadata(0d, (owner, args) => ((MainWindow)owner).PlaceClipboardHeader((double)args.NewValue)));

    private void InitializeClipboardHeader()
    {
        _clipboardHeaderLeave.Tick += (_, _) =>
        {
            _clipboardHeaderLeave.Stop();
            if (Toolbar.IsMouseOver || Toolbar.IsKeyboardFocusWithin || _clipboardView?.PopupOpen == true || _trayMenu?.IsOpen == true || SettingsButton.ContextMenu?.IsOpen == true) return;
            SetClipboardHeaderRevealed(false);
        };
        Closed += (_, _) => { _clipboardHeaderLeave.Stop(); BeginAnimation(ClipboardHeaderProgressProperty, null); };
        AutomationProperties.SetAutomationId(Toolbar, "island-shared-header");
    }
    private void PrepareClipboardHeader(bool instant)
    {
        if (!IsDockPage)
        {
            if (!_clipboardHeaderConfigured) return;
            _clipboardHeaderLeave.Stop(); _clipboardHeaderRevealed = false; _clipboardHeaderEdgeArmed = false; _clipboardHeaderTarget = 0;
            BeginAnimation(ClipboardHeaderProgressProperty, null); SetValue(ClipboardHeaderProgressProperty, 0d);
            _clipboardHeaderConfigured = false;
            Toolbar.RenderTransform = Transform.Identity; Toolbar.Opacity = 1; Toolbar.IsHitTestVisible = true;
            return;
        }
        if (!_expanded || _dockHeaderPage != CurrentPage) { _clipboardHeaderRevealed = false; _clipboardHeaderEdgeArmed = false; _clipboardHeaderLeave.Stop(); }
        _dockHeaderPage = CurrentPage;
        bool shown = DockHeaderMode == ClipboardHeaderMode.Always || _clipboardHeaderRevealed;
        double target = shown ? ClipboardHeaderHeight : 0;
        bool changed = !_clipboardHeaderConfigured || target != _clipboardHeaderTarget;
        _clipboardHeaderConfigured = true; _clipboardHeaderTarget = target;
        double progress = (double)GetValue(ClipboardHeaderProgressProperty);
        ApplyHeader(PanelHeader.For(ClipboardHeaderHeight, ClipboardDockLayout.ListInset));
        ClockLabel.Visibility = Visibility.Visible;
        Toolbar.RenderTransform = _clipboardHeaderShift; Toolbar.IsHitTestVisible = shown;
        // Reapply geometry without restarting a reveal whenever status, language or theme refreshes the same page.
        PlaceClipboardHeader(progress);
        if (!changed && !instant) return;
        int duration = instant || !Soft.For(Island) ? 0 : PanelMotion.Reshape;
        Animate(this, ClipboardHeaderProgressProperty, target, duration, new Track(0, 1), Soft.InOut);
    }
    private void PlaceClipboardHeader(double height)
    {
        if (!_clipboardHeaderConfigured) return;
        height = Math.Clamp(height, 0, ClipboardHeaderHeight);
        ToolbarRow.Height = new GridLength(height);
        _clipboardHeaderShift.Y = height - ClipboardHeaderHeight;
        Toolbar.Opacity = height / ClipboardHeaderHeight;
        Toolbar.Visibility = height > .01 || _clipboardHeaderTarget > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SetClipboardHeaderRevealed(bool revealed)
    {
        if (!_expanded || !IsDockPage || DockHeaderMode != ClipboardHeaderMode.Edge || _clipboardHeaderRevealed == revealed) return;
        _clipboardHeaderRevealed = revealed; _clipboardHeaderLeave.Stop();
        // This is an in-place reshape of the existing island, never a second popup or a rebuilt clipboard view.
        SetExpanded(true);
    }
    private void TrackClipboardHeader(Point screenPoint)
    {
        if (!_expanded || !IsDockPage || DockHeaderMode != ClipboardHeaderMode.Edge || Resizing || _clipboardView?.PopupOpen == true || _trayMenu?.IsOpen == true) return;
        var island = ScreenBounds(Island);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        double screenTop = Stage.PointToScreen(new Point()).Y;
        if (AtClipboardHeaderEdge(screenPoint, island, screenTop, dpi))
        {
            _clipboardHeaderLeave.Stop();
            // The hover that opened the island must leave the edge before it can reveal a second level.
            if (_clipboardHeaderEdgeArmed) SetClipboardHeaderRevealed(true);
            return;
        }
        _clipboardHeaderEdgeArmed = true;
        if (!_clipboardHeaderRevealed) return;
        if (ClipboardHeaderBridge(screenPoint) || ScreenBounds(Toolbar).Contains(screenPoint) || Toolbar.IsKeyboardFocusWithin)
            _clipboardHeaderLeave.Stop();
        else if (!_clipboardHeaderLeave.IsEnabled) _clipboardHeaderLeave.Start();
    }
    internal static bool AtClipboardHeaderEdge(Point pointer, Rect island, double screenTop, double dpi) =>
        pointer.X >= island.Left && pointer.X <= island.Right && pointer.Y >= screenTop && pointer.Y <= screenTop + 6 * dpi;

    private bool ClipboardHeaderBridge(Point point)
    {
        if (!_expanded || !IsDockPage || DockHeaderMode != ClipboardHeaderMode.Edge || PresentationSource.FromVisual(Stage) is null) return false;
        var panel = ScreenBounds(Island); double top = Stage.PointToScreen(new Point()).Y;
        // The transparent gap above a lowered capsule remains click-through, but crossing it must not close the island.
        return point.X >= panel.Left && point.X <= panel.Right && point.Y >= top && point.Y <= panel.Top;
    }
}
