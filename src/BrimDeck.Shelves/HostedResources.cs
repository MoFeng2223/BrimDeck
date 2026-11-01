using System.Windows;

namespace DropShelf;

/// <summary>把原版隐式样式限制在置物架窗口和控件内，避免改变 BrimDeck 的按钮、滚动条和主题。</summary>
public static class HostedResources
{
    internal static bool IsHosted { get; private set; }
    internal static ResourceDictionary Palette { get; } = new();
    private static ResourceDictionary? _resources;
    internal static Func<bool>? AnimationPreference { get; private set; }
    public static System.Windows.Media.Animation.IEasingFunction? MotionEase { get; set; }
    public static Action<Window, FrameworkElement>? DressShelf { get; set; }
    public static Action<FrameworkElement>? AttachMotion { get; set; }
    public static Func<bool>? SquareCorners { get; set; }

    public static void ConfigureHost(Func<bool>? animations, string cacheDirectory)
    {
        Configure(animations);
        Services.VirtualFileDropService.ConfigureCacheRoot(cacheDirectory);
    }

    public static IDisposable AcquireImaging() => Services.ImagingThread.Acquire();
    public static Task<T> ImageWorkAsync<T>(Func<T> work, CancellationToken cancellation = default) => Services.ImagingThread.RunAsync(work, cancellation);
    public static Func<int>? ThumbnailLimit { get; set; }
    public static bool AutomaticEnabled { get; set; } = true;

    internal static void Configure(Func<bool>? animations)
    {
        IsHosted = true;
        AnimationPreference = animations;
    }

    internal static void Attach(FrameworkElement element)
    {
        if (!IsHosted) return;
        _resources ??= new ResourceDictionary
        {
            MergedDictionaries =
            {
                new() { Source = new Uri("pack://application:,,,/DropShelf;component/Themes/Tokens.xaml") },
                new() { Source = new Uri("pack://application:,,,/DropShelf;component/Themes/Controls.xaml") },
                Palette,
            },
        };
        element.Resources.MergedDictionaries.Add(_resources);
        AttachMotion?.Invoke(element);
    }
}
