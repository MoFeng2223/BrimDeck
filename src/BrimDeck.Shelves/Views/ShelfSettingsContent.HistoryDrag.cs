using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DropShelf.Interop;
using DropShelf.Services;
using DropShelf.ViewModels;

namespace DropShelf.Views;

public partial class ShelfSettingsContent
{
    // 保留原版历史卡片拉脱、回位换序和跨窗口拖出恢复；ghost 只是交互辅助，不是管理窗口。
    private const double HistoryReturnReach = 60, DetachedScale = 1.06, TearDistance = 48, HoldFollow = 0.3, ReturnPull = 0.25;
    private HistoryEntryViewModel? _pressedEntry;
    private FrameworkElement? _draggedContainer;
    private Controls.FanPanel? _historyPanel;
    private int _historyIndex;
    private Point _historyPressPoint, _dragStartCursor;
    private Vector _grabOffset;
    private bool _tornOff, _draggingHistory;
    private CardGhostWindow? _ghost;

    private void OnHistoryMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressedEntry = null;
        var source = e.OriginalSource as DependencyObject;
        if (_ghost?.IsBusy == true || FindAncestor<FrameworkElement>(source, element => element.DataContext is HistoryEntryViewModel) is not { DataContext: HistoryEntryViewModel entry }
            || FindAncestor<ButtonBase>(source)?.Command != entry.RestoreCommand) return;
        _pressedEntry = entry; _historyPressPoint = e.GetPosition(this); _ghost ??= new CardGhostWindow();
    }
    private void OnHistoryMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingHistory || _pressedEntry is not { } entry || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _historyPressPoint.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _historyPressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        BeginHistoryDrag(entry);
    }
    private void OnHistoryMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingHistory) { _pressedEntry = null; return; }
        e.Handled = true; EndHistoryDrag(cancel: false);
    }
    private void OnHistoryLostCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, HistoryList)) EndHistoryDrag(cancel: true);
    }
    private void BeginHistoryDrag(HistoryEntryViewModel entry)
    {
        _pressedEntry = null;
        if (_ghost is null || HistoryList.ItemContainerGenerator.ContainerFromItem(entry) is not ContentPresenter container || VisualTreeHelper.GetChildrenCount(container) == 0
            || VisualTreeHelper.GetChild(container, 0) is not FrameworkElement card || PoseOf(container) is not { } start || !Mouse.Capture(HistoryList, CaptureMode.Element)) return;
        _draggingHistory = true; _pressedEntry = entry; _draggedContainer = container;
        _dragStartCursor = CursorPoint(); _grabOffset = _dragStartCursor - start.Center; _tornOff = false;
        _historyPanel = VisualTreeHelper.GetParent(container) as Controls.FanPanel;
        _historyIndex = HistoryList.ItemContainerGenerator.IndexFromContainer(container);
        _historyPanel?.ClearHot();
        (container.ContentTemplate?.FindName("RestoreHint", container) as UIElement)?.BeginAnimation(OpacityProperty, null);
        card.UpdateLayout();
        if (Snapshot(card)?.Image.Source is not { } image) { EndHistoryDrag(cancel: true); return; }
        container.Opacity = 0; _historyPanel?.SetGap(container, _historyIndex);
        _ghost.Layer = () => !_tornOff || HistoryReturnWeight(CursorPoint()) > 0 ? DragLayer : null;
        _ghost.Begin(image, new Size(card.ActualWidth, card.ActualHeight), start, () => HistoryDragPose(start, container));
    }
    private ScreenCardPose HistoryDragPose(ScreenCardPose start, FrameworkElement container)
    {
        if (!NativeMethods.IsButtonPhysicallyDown()) Dispatcher.BeginInvoke(() => EndHistoryDrag(cancel: false));
        var cursor = CursorPoint(); var pulled = cursor - _dragStartCursor;
        if (!_tornOff && pulled.Length / VisualTreeHelper.GetDpi(this).DpiScaleX < TearDistance) return start with { Center = start.Center + pulled * HoldFollow };
        _tornOff = true;
        var free = cursor - _grabOffset; var weight = HistoryReturnWeight(cursor);
        _historyPanel?.SetGap(container, weight > 0 ? _historyPanel.SlotAt(_historyPanel.PointFromScreen(free).X) : -1);
        if (weight <= 0 || PoseOf(container) is not { } slot) return new ScreenCardPose(free, DetachedScale, 0);
        var pull = ReturnPull * weight;
        return new ScreenCardPose(free + (slot.Center - free) * pull, DetachedScale + (slot.Scale - DetachedScale) * weight, slot.Angle * pull);
    }
    private void EndHistoryDrag(bool cancel)
    {
        if (!_draggingHistory) return;
        _draggingHistory = false;
        var entry = _pressedEntry!; var container = _draggedContainer!; var panel = _historyPanel;
        _pressedEntry = null; _draggedContainer = null; _historyPanel = null;
        if (Mouse.Captured == HistoryList) HistoryList.ReleaseMouseCapture();
        if (_ghost is not { IsBusy: true } ghost) { container.Opacity = 1; panel?.SetGap(null, -1); return; }
        var cursor = CursorPoint();
        if (cancel || !_tornOff || HistoryReturnWeight(cursor) > 0)
        {
            var original = _historyIndex;
            var index = !cancel && _tornOff && panel?.Gap is >= 0 and var gap ? gap : original;
            panel?.SetGap(container, index); panel?.UpdateHot(); ghost.Layer = () => DragLayer;
            ghost.Settle(() => PoseOf(container), () =>
            {
                container.Opacity = 1;
                if (index == original) { panel?.SetGap(null, -1); return; }
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => _controller.History.Move(entry.Entry, index));
            });
            return;
        }
        ghost.Layer = () => null; ghost.Vanish(cursor);
        _controller.RestoreAt(entry.Entry, new POINT((int)cursor.X, (int)cursor.Y));
    }
    private double HistoryReturnWeight(Point screen)
    {
        var window = PointFromScreen(screen); var list = HistoryList.PointFromScreen(screen);
        if (window.X < 0 || window.Y < 0 || window.X > ActualWidth || window.Y > ActualHeight || list.X < 0 || list.X > HistoryList.ActualWidth || list.Y < -HistoryReturnReach || list.Y > HistoryList.ActualHeight) return 0;
        var t = Math.Clamp((list.Y + HistoryReturnReach) / HistoryReturnReach, 0.01, 1); return t * t * (3 - 2 * t);
    }
    private static ScreenCardPose? PoseOf(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth < 1 || PresentationSource.FromVisual(element) is null) return null;
        var origin = element.PointToScreen(default); var edge = element.PointToScreen(new Point(element.ActualWidth, 0)) - origin;
        var center = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return new ScreenCardPose(center, edge.Length / (element.ActualWidth * VisualTreeHelper.GetDpi(element).DpiScaleX), Math.Atan2(edge.Y, edge.X) * 180 / Math.PI);
    }
    private static Point CursorPoint() { var cursor = WindowPlacement.GetCursor(); return new Point(cursor.X, cursor.Y); }
}
