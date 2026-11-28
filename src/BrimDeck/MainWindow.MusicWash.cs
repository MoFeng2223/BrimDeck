using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;
using MediaState = BrimDeck.Core.MediaState;

namespace BrimDeck;

public partial class MainWindow
{
    // Music backdrop for expanded page or collapsed notch (MusicWashMode). Two cover layers crossfade on track change.
    private Grid? _musicWash;
    private (FrameworkElement Layer, Image Image) _musicWashA, _musicWashB;
    private Image? _musicWashFront;
    private System.Windows.Shapes.Rectangle? _musicWashGlow;
    private System.Windows.Shapes.Rectangle? _musicWashScrim;
    private BitmapSource? _musicWashCover;
    private Color? _musicWashColor;
    private Color? _musicWashScrimColor;
    private bool _musicWashCompact;
    // The song the staged cover and glow came from. A cover reloaded for the same song keeps them meanwhile; another song
    // without a cover drops them.
    private MediaTrack? _musicWashTrack;
    private void DropStaleMusicWash()
    {
        if (_mediaCover is { } && _mediaTrack is { } playing) { _musicWashTrack = playing; return; }
        if (_mediaCover is not null || _mediaTrack is not { } track || _musicWashTrack is not { } staged) return;
        if (staged.Id == track.Id && SameMediaSong(staged, track)) return;
        _musicWashTrack = null; _musicWashCover = null; _musicWashColor = null; _musicWashFront = null;
        foreach (var (layer, image) in new[] { _musicWashA, _musicWashB })
        {
            Soft.Glide(layer, UIElement.OpacityProperty, 0, false);
            image.Source = null;
        }
        if (_musicWashGlow is not null) _musicWashGlow.Fill = null;
    }

    // Mounted on IslandContent so the blur stays composed while the expanded page is hidden. Sized to Island (not
    // IslandContent): ExpandedContent stays Hidden when collapsed and would otherwise stretch the wash to the open page.
    private void EnsureMusicWash()
    {
        if (_musicWash is not null) return;
        _musicWash = new Grid
        {
            IsHitTestVisible = false,
            Opacity = 0,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        IslandContent.Children.Insert(0, _musicWash);
        Island.SizeChanged += (_, _) => FitMusicWash();
        _musicWashA = CreateMusicWashCoverLayer();
        _musicWashB = CreateMusicWashCoverLayer();
        _musicWash.Children.Add(_musicWashA.Layer);
        _musicWash.Children.Add(_musicWashB.Layer);
        _musicWashScrim = new System.Windows.Shapes.Rectangle { IsHitTestVisible = false };
        _musicWash.Children.Add(_musicWashScrim);
        _musicWashGlow = new System.Windows.Shapes.Rectangle { IsHitTestVisible = false };
        _musicWash.Children.Add(_musicWashGlow);
        FitMusicWash();
    }
    private void FitMusicWash()
    {
        if (_musicWash is null) return;
        double w = Island.ActualWidth, h = Island.ActualHeight;
        if (w <= 0 || h <= 0) return;
        // Size to the island and clip to its drawn outline. After collapse SettledClip switches the island's layout clip to a
        // rectangle so scrolling text stays cheap; without this the wash would spill into the corners a moment later.
        if (_musicWash.Width != w) _musicWash.Width = w;
        if (_musicWash.Height != h) _musicWash.Height = h;
        var clip = Island.SurfaceClip();
        if (!ReferenceEquals(_musicWash.Clip, clip)) _musicWash.Clip = clip.IsEmpty() ? null : clip;
    }
    // 64×64 blurred cover scaled from its centre to fill the panel.
    private static (FrameworkElement Layer, Image Image) CreateMusicWashCoverLayer()
    {
        const double sample = 64;
        var scale = new ScaleTransform(1, 1);
        var image = new Image
        {
            Width = sample,
            Height = sample,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(.5, .5),
            RenderTransform = scale,
            Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 20,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            },
        };
        var layer = new Grid
        {
            Opacity = 0,
            IsHitTestVisible = false,
            ClipToBounds = true,
            CacheMode = new BitmapCache { EnableClearType = false },
        };
        layer.Children.Add(image);
        layer.SizeChanged += (_, _) =>
        {
            double w = layer.ActualWidth, h = layer.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double s = Math.Max(w / sample, h / sample);
            scale.ScaleX = scale.ScaleY = s;
        };
        return (layer, image);
    }
    private void DressMusicWashScrim()
    {
        if (_musicWashScrim is null) return;
        var surface = Look.ColorOf("surface");
        surface.A = (byte)(Look.Current.IsDark ? 150 : 170);
        if (_musicWashScrimColor == surface) return;
        _musicWashScrimColor = surface;
        var brush = new SolidColorBrush(surface); brush.Freeze();
        _musicWashScrim.Fill = brush;
    }
    // Expanded music page uses MusicBackdrop; collapsed notch/capsule uses MusicCompactBackdrop.
    private MusicBackdrop MusicWashMode => _preview || Look.HighContrast ? MusicBackdrop.None
        : _expanded ? (IsMusicPage ? Settings.MusicBackdrop : MusicBackdrop.None)
        : Settings.MusicCompactBackdrop;
    private bool MusicWashWanted
    {
        get
        {
            var mode = MusicWashMode;
            if (mode == MusicBackdrop.None) return false;
            if (_mediaTrack is not { State: not MediaState.Stopped }) return false;
            return mode == MusicBackdrop.Cover
                ? _mediaCover is not null || _musicWashCover is not null
                : _coverGlow is not null || _musicWashColor is not null;
        }
    }
    // Stages cover/glow and layer visibility. crossfadeCover: image-to-image when a cover is already up.
    private void WarmMusicWash(bool crossfadeCover = false)
    {
        EnsureMusicWash();
        DropStaleMusicWash();
        FitMusicWash();
        if (_mediaCover is { } cover && !ReferenceEquals(_musicWashCover, cover))
            ShowMusicWashCover(cover, crossfadeCover);
        bool compact = !_expanded;
        if (_coverGlow is { } glow && (_musicWashColor != glow || _musicWashCompact != compact))
        {
            _musicWashGlow!.Fill = MusicVisuals.CoverGlow(glow, Look.Current.IsDark, compact);
            _musicWashColor = glow;
            _musicWashCompact = compact;
        }
        DressMusicWashScrim();
        var mode = MusicWashMode;
        bool showCover = mode == MusicBackdrop.Cover && _musicWashCover is not null;
        bool showGlow = mode == MusicBackdrop.Glow && _musicWashColor is not null;
        var coverVis = showCover ? Visibility.Visible : Visibility.Collapsed;
        _musicWashA.Layer.Visibility = coverVis;
        _musicWashB.Layer.Visibility = coverVis;
        _musicWashScrim!.Visibility = coverVis;
        _musicWashGlow!.Visibility = showGlow ? Visibility.Visible : Visibility.Collapsed;
        // One-frame hair of opacity so the blur cache is built while collapsed.
        if (showCover && !MusicWashWanted && _musicWash!.Opacity < .01 && _musicWashFront is { Opacity: > 0 })
        {
            _musicWash.Opacity = .01;
            Dispatcher.BeginInvoke(() =>
            {
                if (_musicWash is not null && !MusicWashWanted) _musicWash.Opacity = 0;
            }, DispatcherPriority.Render);
        }
    }
    private void ShowMusicWashCover(BitmapSource cover, bool crossfade)
    {
        var front = _musicWashFront is not null && ReferenceEquals(_musicWashFront, _musicWashA.Image) ? _musicWashA : _musicWashB;
        var back = ReferenceEquals(front.Image, _musicWashA.Image) ? _musicWashB : _musicWashA;
        bool hasFront = _musicWashFront is not null && front.Layer.Opacity > .05;
        back.Image.Source = cover;
        _musicWashCover = cover;
        if (!(crossfade && hasFront && Animates))
        {
            Soft.Glide(front.Layer, UIElement.OpacityProperty, 0, false);
            Soft.Glide(back.Layer, UIElement.OpacityProperty, 1, false);
            _musicWashFront = back.Image;
            return;
        }
        Soft.Glide(back.Layer, UIElement.OpacityProperty, 1, true, Soft.Short, .02);
        Soft.Glide(front.Layer, UIElement.OpacityProperty, 0, true, Soft.Short, .02);
        _musicWashFront = back.Image;
    }
    private void SyncMusicWash()
    {
        EnsureMusicWash();
        DropStaleMusicWash();
        var mode = MusicWashMode;
        bool pendingCover = mode == MusicBackdrop.Cover && _mediaCover is { } cover && !ReferenceEquals(_musicWashCover, cover);
        bool pendingGlow = mode == MusicBackdrop.Glow && _coverGlow is { } glow && _musicWashColor != glow;
        if (!MusicWashWanted)
        {
            WarmMusicWash();
            Soft.Glide(_musicWash!, UIElement.OpacityProperty, 0, Animates, Soft.Short, .02);
            return;
        }
        if (pendingCover)
        {
            WarmMusicWash(crossfadeCover: _musicWash!.Opacity > .05);
            Soft.Glide(_musicWash!, UIElement.OpacityProperty, 1, Animates, Soft.Short, .02);
            return;
        }
        if (pendingGlow && _musicWash!.Opacity > .05 && Animates)
        {
            var fadeOut = Soft.Fade(_musicWash.Opacity, 0, Soft.Quick, Soft.Follow);
            fadeOut.FillBehavior = FillBehavior.Stop;
            fadeOut.Completed += (_, _) =>
            {
                Soft.Glide(_musicWash, UIElement.OpacityProperty, 0, false);
                WarmMusicWash();
                Soft.Glide(_musicWash, UIElement.OpacityProperty, 1, Animates, Soft.Short, .02);
            };
            _musicWash.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            return;
        }
        WarmMusicWash();
        Soft.Glide(_musicWash!, UIElement.OpacityProperty, 1, Animates, Soft.Short, .02);
    }
}
