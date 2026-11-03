using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Modules;
using BrimDeck.Core;

namespace BrimDeck;

// Temporary modules share the island's geometry tracks and the existing page hand-over.
// The compact frame is latched for the session, so content or names cannot resize it.
public partial class MainWindow
{
    private sealed record TransientPage(FrameworkElement Content, double Width, double Height, bool Closing = false);
    private sealed record TransientNotice(string Text, bool Success)
    {
        public int Duration { get; init; } = 650;
        public ModuleNoticeQueue.Ticket? Module { get; init; }
    }
    private TransientPage? _transient;
    private TransientNotice? _transientNotice, _pendingTransientNotice;
    private CollapsedShape? _transientCompactShape;
    private PageHandover? _transientHandover;
    private PromptTimer? _transientTimer;
    private int _transientVersion;
    private long _noticeAppeared;
    internal bool CanShowModuleNotice => CanPresentInputMethods && !_expanded && _alert is null && _transient is null && _transientNotice is null && _transientCompactShape is null;
    internal void ShowModuleNotice(ModuleNoticeQueue.Ticket ticket)
    {
        if (!CanShowModuleNotice || ticket.Lifetime.IsCancellationRequested) return;
        LatchTransientFrame();
        PresentTransientNotice(new(ticket.Notice.Title, ticket.Notice.Tone != ModuleNoticeTone.Warning) { Duration = ticket.Notice.DurationMilliseconds, Module = ticket });
        SetExpanded(false, transientRequest: true); HoldTransientNotice();
    }
    internal void DismissModuleNotice(string owner, CancellationToken lifetime)
    { if (_transientNotice?.Module is { } ticket && ticket.Owner == owner && ticket.Lifetime == lifetime) RestoreTransientContent(animate: true); }
    private FrameworkElement NoticeElement => _transientNotice?.Module is null ? TransientFeedback : ModuleNoticeContent;
    private void RenderModuleNotice(ModuleNotice notice)
    {
        ModuleNoticeContent.Children.Clear();
        var row = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(20) }); row.ColumnDefinitions.Add(new());
        var mark = UI.Centered(notice.Tone == ModuleNoticeTone.Warning ? "!" : notice.Tone == ModuleNoticeTone.Success ? "✓" : "•", 13, UI.Accent);
        mark.SetResourceReference(TextBlock.ForegroundProperty, "PanelAccent"); row.Children.Add(mark);
        var text = UI.Text(notice.Title + (string.IsNullOrWhiteSpace(notice.Message) ? "" : " · " + notice.Message), 11.5, UI.Primary);
        text.TextTrimming = TextTrimming.CharacterEllipsis; text.VerticalAlignment = VerticalAlignment.Center;
        text.TextWrapping = TextWrapping.NoWrap; text.FontFamily = UI.PanelFont; text.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary");
        Grid.SetColumn(text, 1); row.Children.Add(text); ModuleNoticeContent.Children.Add(row);
        if (notice.ProgressPercent is { } progress)
        {
            var fill = new Grid();
            fill.ColumnDefinitions.Add(new() { Width = new GridLength(progress, GridUnitType.Star) });
            fill.ColumnDefinitions.Add(new() { Width = new GridLength(100 - progress, GridUnitType.Star) });
            var amount = new Border { CornerRadius = new CornerRadius(1) }; amount.SetResourceReference(Border.BackgroundProperty, "PanelAccent"); fill.Children.Add(amount);
            var track = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(1), Child = fill, Opacity = .7 };
            track.SetResourceReference(Border.BackgroundProperty, "PanelTrack"); ModuleNoticeContent.Children.Add(track);
        }
        AutomationProperties.SetName(ModuleNoticeContent, notice.Title + " " + notice.Message);
    }

    private void LatchTransientFrame()
    {
        if (_transientCompactShape is not null) return;
        var normal = ShapeFor(NormalEffectiveStyle);
        // A line opens into the selector and settles directly into a readable result capsule,
        // instead of first shrinking to a line and immediately growing a second time.
        _transientCompactShape = normal.Style == CompactStyle.Line
            ? CollapsedGeometry.Resolve(Settings, CompactStyle.Capsule, MonitorBoundsDip, WorkDip, _pixelsPerDip) : normal;
    }

    private void ShowTransient(FrameworkElement content, double width, double height)
    {
        StopTransientTimer(); _transientHandover?.Drop();
        LatchTransientFrame(); ResetTransientFeedback();
        _pendingTransientNotice = _transientNotice = null;
        _hover.Stop(); _leave.Stop(); _pageSwitchHold.Stop(); _handover.Drop();
        _detailPopup?.Close(); CloseSourceMenu(); _openCentre = null;
        _transient = new(content, width, height);
        TransientContent.Width = width; TransientContent.Height = height;
        TransientContent.Children.Clear(); TransientContent.Children.Add(content);
        TransientContent.Visibility = Visibility.Visible;
        ContentCanvas.Visibility = Visibility.Hidden; Toolbar.Visibility = Visibility.Hidden; ModuleControlHost.Visibility = Visibility.Collapsed;
        ApplySettings(); RenderCompact();
        SetExpanded(true, transientRequest: true);
    }

    private void EndTransient()
    {
        if (_transient is null || _transient.Closing) return;
        _transient = _transient with { Closing = true };
        if (_pendingTransientNotice is { } notice) PresentTransientNotice(notice);
        SetExpanded(false, transientRequest: true);
        int duration = Soft.Enabled ? Settings.AnimationDuration : 0;
        if (duration == 0) FinishTransient();
        else AfterTransient(duration, FinishTransient);
    }

    private void FinishTransient()
    {
        _transient = null; TransientContent.Children.Clear(); TransientContent.Visibility = Visibility.Collapsed;
        ContentCanvas.Visibility = Visibility.Visible; Toolbar.Visibility = Visibility.Visible; ApplyModuleControlsVisibility();
        // The collapse has already placed the surface. Applying all settings here used to
        // cancel those clocks and replace content in a single frame.
        if (_pendingTransientNotice is { } notice)
        {
            _pendingTransientNotice = null;
            if (_transientNotice != notice) PresentTransientNotice(notice);
            HoldTransientNotice();
        }
        else RestoreTransientContent(animate: true);
        UpdateGrips();
    }

    private void ShowTransientNotice(string text, bool success)
    {
        var notice = new TransientNotice(text, success);
        if (_transient is { } page)
        {
            _pendingTransientNotice = notice;
            // Prepare confirmed feedback during the collapse, so the normal compact arrival
            // reveals it. Do not restart the geometry or its completion timer.
            if (page.Closing) PresentTransientNotice(notice);
            return;
        }
        LatchTransientFrame(); PresentTransientNotice(notice);
        SetExpanded(false, transientRequest: true);
        HoldTransientNotice();
    }

    private void PresentTransientNotice(TransientNotice notice)
    {
        var leaving = _transient is null ? PageHandover.Capture(Compact) : null;
        _transientHandover ??= new(TransientLeavingContent);
        _transientHandover.Drop();
        _transientNotice = notice; _noticeAppeared = Environment.TickCount64;
        if (notice.Module is { } ticket) RenderModuleNotice(ticket.Notice);
        AutomationProperties.SetName(TransientFeedback, notice.Text);
        RenderCompact();
        if (leaving is not null) _transientHandover.Send(leaving, new Thickness(0), NoticeElement, Handover.Morph);
        else PageHandover.Appear(NoticeElement);
    }

    private void HoldTransientNotice()
    {
        int arrival = Soft.Enabled ? (int)Math.Max(0, Soft.Short - (Environment.TickCount64 - _noticeAppeared)) : 0;
        AfterTransient(arrival + (_transientNotice?.Duration ?? 650), () => RestoreTransientContent(animate: true));
    }

    private void RestoreTransientContent(bool animate)
    {
        StopTransientTimer();
        var leaving = animate ? PageHandover.Capture(Compact) : null;
        _transientHandover ??= new(TransientLeavingContent);
        _transientHandover.Drop();
        _pendingTransientNotice = _transientNotice = null;
        ResetTransientFeedback(); RenderCompact();
        var arriving = _alert is not null ? CompactAlert : ModuleActivityPresented ? (FrameworkElement)ModuleCompact : CompactRings;
        if (leaving is not null && Soft.Enabled) _transientHandover.Send(leaving, new Thickness(0), arriving, Handover.Morph);
        else if (animate) PageHandover.Appear(arriving);
        void ReleaseFrame()
        {
            _transientCompactShape = null;
            UpdateCollapsedGeometry(); RenderCompact();
            SetExpanded(_expanded, transientRequest: true); UpdateGrips();
        }
        if (animate && Soft.Enabled) AfterTransient(Soft.Short, ReleaseFrame);
        else ReleaseFrame();
    }

    private void ResetTransientFeedback()
    {
        SetImmediate(ModuleNoticeContent, OpacityProperty, 1d); ModuleNoticeContent.Children.Clear(); ModuleNoticeContent.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(ModuleNoticeContent, "");
        SetImmediate(TransientFeedback, OpacityProperty, 1d);
        TransientFeedback.Text = ""; TransientFeedback.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(TransientFeedback, "");
    }

    private void ClearTransientPresentation()
    {
        bool hadPresentation = _transient is not null || _transientNotice is not null || _transientCompactShape is not null;
        StopTransientTimer(); _transientHandover?.Drop();
        _pendingTransientNotice = _transientNotice = null; _transientCompactShape = null;
        ResetTransientFeedback();
        _transient = null; TransientContent.Children.Clear(); TransientContent.Visibility = Visibility.Collapsed;
        ContentCanvas.Visibility = Visibility.Visible; Toolbar.Visibility = Visibility.Visible; ApplyModuleControlsVisibility();
        RenderCompact();
        if (hadPresentation) SetExpanded(false, transientRequest: true);
    }

    private void StopTransientTimer() { _transientVersion++; _transientTimer?.Stop(); _transientTimer = null; }
    private void AfterTransient(int milliseconds, Action action)
    {
        StopTransientTimer(); int version = _transientVersion;
        _transientTimer = new PromptTimer(Dispatcher, () =>
        {
            if (version != _transientVersion) return;
            _transientTimer?.Stop(); _transientTimer = null; action();
        }) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)) };
        _transientTimer.Start();
    }
}
