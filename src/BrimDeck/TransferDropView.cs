using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// Three file kinds above a two-line drop hint; motion only while files are over the page.
internal sealed class TransferDropView : Grid
{
    private readonly bool _empty;
    private bool _dragging;
    private readonly Border _surface;
    private readonly TextBlock _title, _note;
    private readonly TranslateTransform _fileMotion = new();
    private readonly Rectangle _outline;
    private readonly Brush _accent = new SolidColorBrush(PanelTheme.ColorOf("accent"));

    public TransferDropView(bool empty)
    {
        _empty = empty;
        IsHitTestVisible = false;
        Margin = new Thickness(2);
        _surface = new Border { CornerRadius = new CornerRadius(16), Background = UI.Brush(PanelTheme.Hex("hover")), Opacity = .45 };
        Children.Add(_surface);
        _outline = new Rectangle { RadiusX = 16, RadiusY = 16, StrokeThickness = 1,
            Stroke = UI.Brush(UI.Hairline), Margin = new Thickness(.5) };
        Children.Add(_outline);
        var stack = new StackPanel { Margin = new Thickness(24, 10, 24, 10) };
        var icons = new Canvas { Width = 132, Height = 66,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10),
            RenderTransform = _fileMotion };
        void Icon(string data, string color, double left, double top, double angle, bool primary)
        {
            var glyph = new Canvas { Width = 32, Height = 32 };
            glyph.Children.Add(new Path { Data = Geometry.Parse(data), Stroke = UI.Brush(color),
                StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            var surface = PanelTheme.ColorOf("surface");
            var ink = PanelTheme.ColorOf("textPrimary");
            double mix = primary ? .09 : .045;
            Color face = Color.FromRgb(
                (byte)(surface.R * (1 - mix) + ink.R * mix),
                (byte)(surface.G * (1 - mix) + ink.G * mix),
                (byte)(surface.B * (1 - mix) + ink.B * mix));
            var tile = new Border { Width = primary ? 46 : 40, Height = primary ? 54 : 46,
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                BorderBrush = UI.Brush(primary ? UI.Outline : UI.Hairline),
                Background = new SolidColorBrush(face),
                Child = new Viewbox { Child = glyph, Width = primary ? 28 : 24, Height = primary ? 28 : 24,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform(angle) };
            Canvas.SetLeft(tile, left); Canvas.SetTop(tile, top);
            icons.Children.Add(tile);
        }
        // Side cards first; the central folder overlaps them and owns the visual focus.
        Icon("M 7,4 H 25 Q 28,4 28,7 V 25 Q 28,28 25,28 H 7 Q 4,28 4,25 V 7 Q 4,4 7,4 Z M 4,23 L 12,15 L 18,21 L 22,17 L 28,23 M 23,10 A 2,2 0 1 1 19,10 A 2,2 0 1 1 23,10", "#D5AA7D", 17, 13, -14, false);
        Icon("M 8,3 H 20 L 27,10 V 27 Q 27,29 25,29 H 8 Q 6,29 6,27 V 5 Q 6,3 8,3 Z M 20,3 V 10 H 27 M 11,16 H 22 M 11,21 H 19", "#91AED5", 75, 13, 14, false);
        Icon("M 4,9 V 7 Q 4,5 6,5 H 13 L 16,9 H 26 Q 28,9 28,11 V 25 Q 28,27 26,27 H 6 Q 4,27 4,25 Z M 4,12 H 28", UI.Primary, 43, 3, 0, true);
        stack.Children.Add(icons);
        _title = new TextBlock { FontFamily = UI.PanelFont, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = UI.Brush(UI.Primary), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        _note = new TextBlock { FontFamily = UI.PanelFont, FontSize = 11, Foreground = UI.Brush(UI.Secondary),
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
        stack.Children.Add(_title); stack.Children.Add(_note);
        Children.Add(new Viewbox { Child = stack, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6) });
        IsVisibleChanged += (_, _) => UpdateMotion();
        Unloaded += (_, _) => StopMotion();
        SetDragging(false);
    }

    public void SetDragging(bool dragging)
    {
        bool changed = _dragging != dragging;
        _dragging = dragging;
        Visibility = _empty || dragging ? Visibility.Visible : Visibility.Collapsed;
        _title.Text = dragging ? Loc.T("松手，文件就暂存在这里", "Release to keep files here") : Loc.T("把文件拖到这里，稍后接着用", "Drop files here to pick up later");
        _note.Text = Loc.T("只记录位置，不复制文件；移除不会删除原文件", "Locations only, no copies. Removing keeps the originals.");
        _outline.Stroke = dragging ? _accent : UI.Brush(UI.Tertiary);
        _outline.StrokeDashArray = new DoubleCollection { 5, 4 };
        _outline.Opacity = dragging ? 1 : .5;
        _surface.Background = dragging ? _accent : UI.Brush(PanelTheme.Hex("hover"));
        _surface.Opacity = dragging ? .08 : 0;
        if (changed) UpdateMotion();
    }

    private void UpdateMotion()
    {
        StopMotion();
        if (!_dragging || !IsVisible || !Soft.Enabled) return;
        _fileMotion.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-5, 4, TimeSpan.FromMilliseconds(700))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        _outline.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -9, TimeSpan.FromMilliseconds(1200))
        { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void StopMotion()
    {
        _fileMotion.BeginAnimation(TranslateTransform.YProperty, null);
        _outline.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
    }
}
