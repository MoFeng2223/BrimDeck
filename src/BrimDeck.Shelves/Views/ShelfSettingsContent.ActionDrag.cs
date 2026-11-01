using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DropShelf.Localization;
using DropShelf.ViewModels;

namespace DropShelf.Views;

public partial class ShelfSettingsContent
{
    // 原版快速操作换位、替换和删除区算法，仅将窗口容器改为宿主视口。
    private ActionCardViewModel? _pressedCard, _pendingDelete;
    private Point _pressPoint;
    private bool _draggingCard, _overDeleteZone;
    private ActionSlotViewModel? _dropTarget;
    private const double LockedSink = 28;

    private void OnActionCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<ButtonBase>(source) is not null) return;
        _pressedCard = FindAncestor<FrameworkElement>(source, element => element.DataContext is ActionCardViewModel)?.DataContext as ActionCardViewModel;
        _pressPoint = e.GetPosition(DragLayer);
    }

    private void OnActionCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedCard is not { } card || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(DragLayer);
        if (!_draggingCard)
        {
            if (Math.Abs(position.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            BeginCardDrag(card);
        }
        var zoneTop = DragLayer.ActualHeight - DeleteZone.ActualHeight;
        var y = card.IsCustom || position.Y <= zoneTop ? position.Y : zoneTop + LockedSink * (1 - Math.Exp(-(position.Y - zoneTop) / LockedSink));
        DragGhostShift.X = position.X + 12; DragGhostShift.Y = y + 8;
        SetOverDeleteZone(position.Y >= zoneTop);
        SetDropTarget(_overDeleteZone ? null : HitTestSlot(e.GetPosition(ActionSlotsList)), card);
    }

    private void OnActionCardMouseUp(object sender, MouseButtonEventArgs e)
    {
        var card = _pressedCard; var target = _dropTarget;
        bool dragged = _draggingCard, overZone = _overDeleteZone;
        EndCardDrag();
        if (!dragged || card is null) return;
        if (overZone && card.IsCustom) ConfirmDeleteCustomAction(card);
        else if (overZone) BounceBackGhost();
        else if (target is not null) ViewModel.AssignSlot(target.Index, card.Action.Id);
    }

    private void BounceBackGhost()
    {
        if (!ShelfMotion.Enabled) return;
        DragGhost.Visibility = Visibility.Visible;
        var top = DragLayer.ActualHeight - DeleteZone.ActualHeight - DragGhost.ActualHeight;
        DragGhostShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Math.Min(DragGhostShift.Y, top), TimeSpan.FromMilliseconds(320))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } });
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(160) };
        fade.Completed += (_, _) => { if (!_draggingCard) { DragGhost.Visibility = Visibility.Collapsed; ClearGhostAnimations(); } };
        DragGhost.BeginAnimation(OpacityProperty, fade);
    }

    private void ClearGhostAnimations()
    {
        DragGhostShift.BeginAnimation(TranslateTransform.YProperty, null); DragGhost.BeginAnimation(OpacityProperty, null);
    }

    private void ConfirmDeleteCustomAction(ActionCardViewModel card)
    {
        _pendingDelete = card; ConfirmMessage.Text = Strings.ConfirmDeleteAction(card.Title);
        ConfirmOverlay.Visibility = Visibility.Visible; SectionTabs.IsEnabled = false; ModalChanged?.Invoke(true);
        EnterContent?.Invoke(ConfirmOverlay);
        ConfirmDialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion(0.96, 1));
        ConfirmDialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion(0.96, 1));
        ConfirmCancelButton.Focus();
    }

    private void OnConfirmDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_pendingDelete?.Action.Custom is { } custom) ViewModel.DeleteCustomAction(custom);
        CloseConfirm();
    }
    private void OnConfirmCancelClick(object sender, RoutedEventArgs e) => CloseConfirm();
    private void CloseConfirm()
    {
        if (_pendingDelete is null) return;
        _pendingDelete = null; ConfirmOverlay.BeginAnimation(OpacityProperty, null);
        ConfirmOverlay.Visibility = Visibility.Collapsed; SectionTabs.IsEnabled = true; ModalChanged?.Invoke(false);
    }
    private void OnActionCardLostCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ActionsPage)) EndCardDrag();
    }
    private void BeginCardDrag(ActionCardViewModel card)
    {
        _draggingCard = true; card.IsDragSource = true;
        DragGhostGlyph.Text = card.Glyph; DragGhostText.Text = card.Title;
        ClearGhostAnimations(); DragGhost.Visibility = Visibility.Visible;
        var brush = (Brush)FindResource(card.IsCustom ? "DeleteBrush" : "LockedBrush");
        DeleteGlow.Fill = DeleteHint.Background = brush;
        DeleteHintGlyph.Text = (string)FindResource(card.IsCustom ? "Glyph.Delete" : "Glyph.Lock");
        DeleteHintText.Text = card.IsCustom ? Strings.ReleaseToDelete : Strings.BuiltInCannotDelete;
        Fade(DeleteZone, 1); Mouse.Capture(ActionsPage, CaptureMode.SubTree);
    }
    private void EndCardDrag()
    {
        if (_pressedCard is { } card) card.IsDragSource = false;
        SetDropTarget(null, null); SetOverDeleteZone(false); Fade(DeleteZone, 0);
        _pressedCard = null; DragGhost.Visibility = Visibility.Collapsed;
        if (!_draggingCard) return;
        _draggingCard = false;
        if (Mouse.Captured == ActionsPage) ActionsPage.ReleaseMouseCapture();
    }
    private void SetOverDeleteZone(bool value)
    {
        if (value == _overDeleteZone) return;
        _overDeleteZone = value; Fade(DeleteGlow, value ? 0.8 : 0.35); Fade(DeleteHint, value ? 1 : 0);
    }
    private static void Fade(UIElement element, double to)
    {
        element.BeginAnimation(OpacityProperty, null);
        if (!ShelfMotion.Enabled) { element.Opacity = to; return; }
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(150)) { EasingFunction = HostedResources.MotionEase });
    }
    private void SetDropTarget(ActionSlotViewModel? target, ActionCardViewModel? card)
    {
        if (ReferenceEquals(target, card)) target = null;
        if (ReferenceEquals(target, _dropTarget)) return;
        if (_dropTarget is not null) _dropTarget.DropHint = null;
        _dropTarget = target;
        if (target is not null) target.DropHint = card is ActionSlotViewModel ? Strings.DropHintSwap : Strings.DropHintReplace;
    }
    private ActionSlotViewModel? HitTestSlot(Point point) => ActionSlotsList.IsVisible && ActionSlotsList.InputHitTest(point) is DependencyObject hit
        ? FindAncestor<FrameworkElement>(hit, element => element.DataContext is ActionSlotViewModel)?.DataContext as ActionSlotViewModel : null;
}
