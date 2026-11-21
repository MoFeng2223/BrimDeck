using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

public partial class MainWindow
{
    private const int CopyContractMs = 240, CopyReboundMs = 540, CopyFadeMs = 180;
    private readonly DispatcherTimer _copyTransition = new() { Interval = TimeSpan.FromMilliseconds(CopyContractMs) };
    private string? _pendingCopy;
    private double _copyPreviewWidth;
    private bool _copyReturning, _copyRebounding;
    private static readonly IEasingFunction CopySpring = CreateCopySpring();
    private static IEasingFunction CreateCopySpring()
    {
        var curve = new BackEase { Amplitude = .85, EasingMode = EasingMode.EaseOut };
        curve.Freeze();
        return curve;
    }

    private bool CanPreviewCopy => Settings.ClipboardPreview && Settings.ClipboardRecord && Settings.ClipboardPage
        && !_expanded && !_preview && !_retiring && !_poweringOff && !_hiding && IsVisible && !IsSuppressed
        && !DisplayPower.Off && EffectiveStyle != CompactStyle.Line && !_moveArmed && _moveOverlay is null
        && !(_alert is { CopyText: null });

    private void InitializeCopyPreview()
    {
        _app.Shelf.CopyCaptured += PreviewCopy;
        _copyTransition.Tick += (_, _) =>
        {
            var text = _pendingCopy;
            bool returning = _copyReturning;
            CancelCopyTransition();
            if (returning) RestoreAfterCopy();
            else if (text is not null && CanPreviewCopy) PresentCopy(text);
            else if (!_expanded) SetExpanded(false);
        };
        Closed += (_, _) => { CancelCopyTransition(); _app.Shelf.CopyCaptured -= PreviewCopy; };
    }

    private void CancelCopyTransition()
    {
        _copyTransition.Stop();
        _pendingCopy = null;
        _copyReturning = false;
    }

    private void PreviewCopy(ClipEntry entry)
    {
        if (!CanPreviewCopy) return;
        string text = !Settings.ClipboardPreviewContent ? Loc.T("✓ 复制成功", "✓ Copied")
            : entry.Kind == ClipKind.Image
                ? Loc.T($"✓ 已复制：图片 {entry.PixelWidth} × {entry.PixelHeight}", $"✓ Copied: image {entry.PixelWidth} × {entry.PixelHeight}")
                : Loc.T("✓ 已复制：", "✓ Copied: ") + CopyPreviewText.Summarize(entry.Text, Settings.ClipboardPreviewMaxLength);
        // Coalesce rapid copies without restarting the contraction. Never keep the full clipboard in the view.
        if (_copyTransition.IsEnabled) { _pendingCopy = text; _copyReturning = false; return; }
        if (!Animates || _alert?.CopyText is not null) { PresentCopy(text); return; }
        _pendingCopy = text;
        ContractCopy();
    }

    private void EndCopyPreview()
    {
        if (!Animates || !CanPreviewCopy) { RestoreAfterCopy(); return; }
        _copyReturning = true;
        ContractCopy();
    }

    private void RestoreAfterCopy()
    {
        CancelCopyTransition();
        _alert = null;
        _copyRebounding = true;
        try { RenderCompact(); if (!_expanded) SetExpanded(false); }
        finally { _copyRebounding = false; }
    }

    private void ContractCopy()
    {
        _copyTransition.Start();
        Animate(Compact, OpacityProperty, 0, CopyFadeMs, new(0, 1), Soft.InOut);
        // Width and margin share the same curve, keeping the centre still. The usual layout owns the return.
        // Side-docked columns keep their surface and only fade their content.
        if (!ColumnFor(EffectiveStyle))
        {
            Island.SettledClip = false;
            double width = Island.Width;
            var before = _alert;
            double target;
            try
            {
                if (_copyReturning) _alert = null;
                else if (_pendingCopy is { } text)
                {
                    MeasureCopy(text);
                    _alert = new CompactAlertInfo(new AppEntry(), "", "", 0, null, text);
                }
                target = ShapeFor(EffectiveStyle).Surface.Width;
            }
            finally { _alert = before; }
            double shrink = width - Math.Max(32 * Collapsed.Scale, Math.Min(width, target) * .78);
            Animate(Island, WidthProperty, Math.Max(1, width - shrink), CopyContractMs, new(0, 1), Soft.InOut);
            var margin = Island.Margin;
            Island.BeginAnimation(MarginProperty, new ThicknessAnimation(margin,
                new Thickness(margin.Left + shrink / 2, margin.Top, margin.Right, margin.Bottom), TimeSpan.FromMilliseconds(CopyContractMs))
                { EasingFunction = Soft.InOut });
        }
    }

    private void MeasureCopy(string text)
    {
        double scale = CompactMusicLayout.SafeScale(Collapsed.Scale);
        _copyPreviewWidth = CopyPreviewLayout.Measure(Compact, text, scale, Look.Primary);
    }

    private void PresentCopy(string text)
    {
        MeasureCopy(text);
        _copyRebounding = true;
        try
        {
            ShowAlert(new CompactAlertInfo(new AppEntry(), "", "", 0, null, text));
            FitCompactToContent();
        }
        finally { _copyRebounding = false; }
    }
}
