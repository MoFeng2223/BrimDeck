using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    // Player / lyrics column split (MusicLayout.DefaultInfoShare). Session only; the drag grip writes it.
    private double _musicInfoShare = MusicLayout.DefaultInfoShare;
    private FrameworkElement? _musicSplitInfo, _musicSplitLyrics;
    // Beside the cover the titles take what the cover leaves of the information column (MusicColumns), so a drag resizes them.
    private FrameworkElement? _musicSplitTitles;
    private double _musicSplitTitlesTrim;
    // The cover sizes the page was built with; a drag that ends with other sizes builds the page again.
    private (double Above, double Beside) _musicSplitCover;
    private Border? _musicSplitGrip;
    private double _musicSplitBody, _musicSplitBarsClear;
    private bool _musicSplitDrag;
    private double _musicSplitStartX, _musicSplitStartShare;

    private FrameworkElement? _lyricCurrHost, _lyricNextHost;
    private TranslateTransform? _lyricNextShift;
    private ScaleTransform? _lyricCurrScale;
    private SolidColorBrush? _lyricCurrBrush, _lyricNextBrush;
    private string _lyricShown = "";
    private string _lyricPendingCurr = "", _lyricPendingNext = "";
    private bool _lyricAdvancing;
    private double _lyricDimLine, _lyricCurLine, _lyricCurHeight;
    private Color _lyricDimColor, _lyricCurColor;

    private FrameworkElement LyricHost(FrameworkElement child, out TranslateTransform shift, out ScaleTransform scale)
    {
        scale = new ScaleTransform(1, 1);
        shift = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(shift);
        return new Grid
        {
            Height = child.Height,
            ClipToBounds = false,
            Children = { child },
            RenderTransform = group,
            RenderTransformOrigin = new Point(0, 0),
        };
    }

    // Size the current host to the wrapped text (1..wrapLines) so the next line sits tight underneath.
    private void FitLyricCurrentHost()
    {
        if (_lyricCurrent is null || _lyricCurrHost is null || _lyricCurLine <= 0) return;
        double width = _lyricCurrHost.ActualWidth;
        if (width <= 0) width = _lyricCurrent.ActualWidth;
        if (width <= 0) return;
        double measured = _lyricCurrent.MeasureWrapHeight(width);
        double height = measured > 0
            ? Math.Clamp(Math.Ceiling(measured), _lyricCurLine, _lyricCurHeight)
            : _lyricCurLine;
        if (Math.Abs(_lyricCurrHost.Height - height) < .5) return;
        _lyricCurrent.Height = height;
        _lyricCurrHost.Height = height;
    }

    private TextBlock LyricNeighbor(SolidColorBrush brush, double size, double lineHeight, TextAlignment align)
    {
        var block = new TextBlock
        {
            FontSize = size,
            Foreground = brush,
            Height = lineHeight,
            LineHeight = lineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = align,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetClearTypeHint(block, ClearTypeHint.Enabled);
        return block;
    }

    // Current (wraps 2–3 rows) with the next line under it when `showNext` (the room holds it). No previous line.
    private FrameworkElement BuildTimedLyrics(MusicMetrics c, double currentFont, double currentLine,
        int wrapLines, bool showNext, TextAlignment align, Visibility visibility)
    {
        _lyricAdvancing = false;
        wrapLines = Math.Clamp(Math.Max(2, wrapLines), 2, 3);
        _lyricDimLine = c.LyricLine;
        _lyricCurLine = currentLine;
        _lyricCurHeight = currentLine * wrapLines;
        _lyricDimColor = ((SolidColorBrush)UI.Brush(Look.Secondary)).Color;
        _lyricCurColor = (LyricInk as SolidColorBrush)?.Color ?? ((SolidColorBrush)UI.Brush(Look.Primary)).Color;
        _lyricCurrBrush = new SolidColorBrush(_lyricCurColor);
        _lyricNextBrush = new SolidColorBrush(_lyricDimColor);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _lyricNext = null;
        _lyricCurrHost = _lyricNextHost = null;
        _lyricNextShift = null;

        _lyricCurrent = new MusicMarquee
        {
            FontSize = currentFont,
            Height = currentLine,
            FontWeight = FontWeights.SemiBold,
            CenterWhenFits = align == TextAlignment.Center,
            Wrap = true,
            WrapLines = wrapLines,
            ScrollAllowed = false,
            Foreground = _lyricCurrBrush,
            Backdrop = Look.MarqueeBackdrop,
            InkFade = Look.InkFade,
        };
        _lyricCurrHost = LyricHost(_lyricCurrent, out _, out _lyricCurrScale);
        _lyricCurrHost.SizeChanged += (_, _) => { if (!_lyricAdvancing) FitLyricCurrentHost(); };
        stack.Children.Add(_lyricCurrHost);

        if (showNext)
        {
            _lyricNext = LyricNeighbor(_lyricNextBrush, c.Lyric, c.LyricLine, align);
            _lyricNextHost = LyricHost(_lyricNext, out _lyricNextShift, out _);
            stack.Children.Add(_lyricNextHost);
        }

        var view = new Border
        {
            Height = _lyricCurHeight + (showNext ? _lyricDimLine : 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = visibility,
            ClipToBounds = true,
            Child = new Grid { Children = { stack } },
        };
        AutomationProperties.SetName(view, Loc.T("歌词", "Lyrics"));
        _lyricShown = "";
        return view;
    }

    // Karaoke while the line holds; on a change, current fades and next rises into place.
    private void UpdateTimedLyrics(string current, string next, IReadOnlyList<LyricWord>? words, LyricSweep sweep)
    {
        if (_lyricCurrent is null) return;
        if (_lyricAdvancing)
        {
            _lyricPendingCurr = current;
            _lyricPendingNext = next;
            return;
        }

        bool changed = current != _lyricShown;
        bool advance = changed && _lyricShown.Length > 0 && current.Length > 0 && Animates
            && _lyricNextShift is not null && _lyricCurrHost is not null;

        if (!changed)
        {
            if (_lyricNext is not null) _lyricNext.Text = next;
            _lyricCurrent.Karaoke(words, Animated(sweep));
            return;
        }

        if (!advance)
        {
            ApplyLyricTexts(current, next);
            _lyricShown = current;
            ResetLyricMotion();
            _lyricCurrent.Karaoke(words, Animated(sweep));
            return;
        }

        BeginLyricHandoff(current, next);
    }

    private void ApplyLyricTexts(string current, string next)
    {
        _lyricCurrent!.Text = current;
        if (_lyricNext is not null) _lyricNext.Text = next;
        FitLyricCurrentHost();
    }

    private void BeginLyricHandoff(string current, string next)
    {
        _lyricAdvancing = true;
        _lyricPendingCurr = current;
        _lyricPendingNext = next;
        if (_lyricNext is not null)
        {
            _lyricNext.Text = current;
            _lyricNext.FontWeight = FontWeights.SemiBold;
        }
        _lyricCurrent!.Karaoke(null, default);

        int ms = Soft.Short;
        double leave = -Math.Max(_lyricCurLine, _lyricCurrHost?.Height ?? _lyricCurHeight);

        if (_lyricCurrHost is not null)
            Soft.Glide(_lyricCurrHost, UIElement.OpacityProperty, 0, true, Soft.Quick);
        Soft.Glide(_lyricCurrBrush!, _lyricDimColor, true, Soft.Quick);
        Soft.Glide(_lyricNextBrush!, _lyricCurColor, true, ms);

        var shift = _lyricNextShift!;
        SetImmediate(shift, TranslateTransform.YProperty, leave);
        var anim = new DoubleAnimation(0, leave, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = Soft.Out,
            FillBehavior = FillBehavior.Stop,
        };
        anim.Completed += (_, _) => FinishLyricHandoff();
        shift.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void FinishLyricHandoff()
    {
        if (!_lyricAdvancing || _lyricCurrent is null) { _lyricAdvancing = false; return; }
        _lyricAdvancing = false;
        ApplyLyricTexts(_lyricPendingCurr, _lyricPendingNext);
        _lyricShown = _lyricPendingCurr;
        ResetLyricMotion();
        if (!Animates) return;
        if (_lyricCurrScale is not null)
        {
            _lyricCurrScale.ScaleX = _lyricCurrScale.ScaleY = Soft.Shrunk;
            Soft.Glide(_lyricCurrScale, ScaleTransform.ScaleXProperty, 1, true, Soft.Quick);
            Soft.Glide(_lyricCurrScale, ScaleTransform.ScaleYProperty, 1, true, Soft.Quick);
        }
        if (_lyricNextHost is not null && _lyricPendingNext.Length > 0)
        {
            _lyricNextHost.Opacity = 0;
            Soft.Glide(_lyricNextHost, UIElement.OpacityProperty, 1, true, Soft.Quick);
        }
    }

    private void ResetLyricMotion()
    {
        if (_lyricNextShift is not null) SetImmediate(_lyricNextShift, TranslateTransform.YProperty, 0d);
        if (_lyricCurrScale is not null)
        {
            SetImmediate(_lyricCurrScale, ScaleTransform.ScaleXProperty, 1d);
            SetImmediate(_lyricCurrScale, ScaleTransform.ScaleYProperty, 1d);
        }
        if (_lyricCurrHost is not null) SetImmediate(_lyricCurrHost, UIElement.OpacityProperty, 1d);
        if (_lyricNextHost is not null) SetImmediate(_lyricNextHost, UIElement.OpacityProperty, 1d);
        if (_lyricCurrBrush is not null)
        {
            Soft.Glide(_lyricCurrBrush, _lyricCurColor, false);
            if (_lyricCurrent is not null) _lyricCurrent.Foreground = _lyricCurrBrush;
        }
        if (_lyricNextBrush is not null) Soft.Glide(_lyricNextBrush, _lyricDimColor, false);
        if (_lyricNext is not null)
        {
            _lyricNext.FontWeight = FontWeights.Normal;
            _lyricNext.Foreground = _lyricNextBrush;
        }
    }

    private FrameworkElement MusicLyricsSplitGrip(MusicLayout layout, MusicColumns columns, double inset, bool cover)
    {
        var line = new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(1),
            Background = UI.Brush(Look.Hairline),
            IsHitTestVisible = false,
        };
        var grip = new Border
        {
            Width = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(columns.InfoWidth + columns.DividerGap - 6.5, inset, 0, inset),
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeWE,
            Child = line,
        };
        AutomationProperties.SetName(grip, Loc.T("调整歌词栏宽度", "Resize lyrics column"));
        void Paint(bool lit)
        {
            line.Width = _musicSplitDrag || lit ? 2 : 1;
            line.Background = UI.Brush(_musicSplitDrag ? Look.Accent : lit ? Look.Track : Look.Hairline);
        }
        grip.MouseEnter += (_, _) => Paint(true);
        grip.MouseLeave += (_, _) => { if (!_musicSplitDrag) Paint(false); };
        grip.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragPress = null;
            _musicSplitDrag = true;
            _musicSplitStartX = e.GetPosition(grip.Parent as IInputElement ?? grip).X;
            _musicSplitStartShare = _musicInfoShare;
            _musicSplitBody = layout.BodyWidth;
            Paint(true);
            grip.CaptureMouse();
            e.Handled = true;
        };
        grip.MouseMove += (_, e) =>
        {
            if (!_musicSplitDrag || e.LeftButton != MouseButtonState.Pressed) return;
            double x = e.GetPosition(grip.Parent as IInputElement ?? grip).X;
            double share = MusicLayout.ClampInfoShare(_musicSplitStartShare + (x - _musicSplitStartX) / Math.Max(1, _musicSplitBody));
            if (Math.Abs(share - _musicInfoShare) < .002) return;
            _musicInfoShare = share;
            ApplyMusicLyricsSplit(layout.Columns(true, cover, _musicInfoShare), inset);
            e.Handled = true;
        };
        grip.MouseLeftButtonUp += (_, e) =>
        {
            if (!_musicSplitDrag) return;
            _musicSplitDrag = false;
            grip.ReleaseMouseCapture();
            Paint(grip.IsMouseOver);
            e.Handled = true;
        };
        grip.LostMouseCapture += (_, _) =>
        {
            _musicSplitDrag = false;
            Paint(grip.IsMouseOver);
            // The cover above the titles, or beside them, follows the column's width: once the drag ends, a split that gives it
            // another size or place builds the page again.
            Dispatcher.BeginInvoke(() =>
            {
                if (_musicSplitGrip != grip || !IsMusicPage) return;
                var settled = layout.Columns(true, cover, _musicInfoShare);
                if ((settled.Cover, settled.SideCover) == _musicSplitCover) return;
                _musicRenderKey = ""; RenderMusic();
            });
        };
        _musicSplitGrip = grip;
        return grip;
    }

    private void ApplyMusicLyricsSplit(MusicColumns columns, double inset)
    {
        if (_musicSplitInfo is { } info) info.Width = columns.InfoWidth;
        if (_musicSplitTitles is { } titles) titles.Width = Math.Max(0, columns.InfoWidth - _musicSplitTitlesTrim);
        if (_musicSplitLyrics is { } lyrics)
        {
            lyrics.Width = Math.Max(0, columns.LyricsWidth - _musicSplitBarsClear);
            lyrics.Margin = new Thickness(columns.LyricsX, 0, 0, 0);
        }
        if (_musicSplitGrip is { } grip)
            grip.Margin = new Thickness(columns.InfoWidth + columns.DividerGap - 6.5, inset, 0, inset);
    }
}
