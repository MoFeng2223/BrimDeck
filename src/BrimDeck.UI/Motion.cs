using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;
namespace BrimDeck;

// Redraws the elements whose values are easing, while any is, with the display. Otherwise it is unhooked and the panel costs
// nothing between readings.
internal static class Motion
{
    private static readonly List<UIElement> Moving = [];
    private static bool _hooked;
    // Whether hover and press feedback eases; off with the app's animations. Readings say so with each update (motion).
    public static bool Enabled { get; set; } = true;
    private static TimeSpan _last;
    private static double _period = 1000 / 60.0;
    private static int _every = 1;
    private static readonly Dictionary<UIElement, Func<double, bool>> Steps = [];

    // step(now) redraws the element and returns whether it still moves.
    public static void Start(UIElement element, Func<double, bool> step)
    {
        Steps[element] = step;
        if (!Moving.Contains(element)) Moving.Add(element);
        if (_hooked) return;
        _hooked = true; _last = TimeSpan.Zero; (_period, _every) = Pacing(); CompositionTarget.Rendering += Tick;
    }

    // The display's refresh period in milliseconds, from the desktop compositor, and how many of its frames make one of ours.
    // DWM_TIMING_INFO is packed to the byte and 292 bytes long; only its refresh rate is read (a ratio, after the size).
    private static (double Period, int Every) Pacing()
    {
        const int Size = 292;
        double hertz = 0;
        var info = Marshal.AllocHGlobal(Size);
        try
        {
            for (int i = 0; i < Size; i += 4) Marshal.WriteInt32(info, i, 0);
            Marshal.WriteInt32(info, 0, Size);
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, info) == 0)
            {
                uint numerator = (uint)Marshal.ReadInt32(info, 4), denominator = (uint)Marshal.ReadInt32(info, 8);
                if (denominator > 0) hertz = numerator / (double)denominator;
            }
        }
        finally { Marshal.FreeHGlobal(info); }
        if (!(hertz >= 24 && hertz <= 500)) hertz = 60;
        return (1000 / hertz, hertz > 180 ? 2 : 1);
    }
    [DllImport("dwmapi")] private static extern int DwmGetCompositionTimingInfo(IntPtr window, IntPtr info);

    private static void Tick(object? sender, EventArgs e)
    {
        // Every display frame, or every other one above 180 Hz. Drawing only every other frame at 144 Hz looked lighter, but a WPF
        // frame with nothing to draw lets the render loop fall out of step, and the next frame then came a refresh late: three
        // steps in ten were uneven, against one in sixteen when every frame draws. What a frame draws is kept small instead
        // (DrawingCache), so following the display costs little. The rhythm counts from the display's own refresh period.
        if (e is RenderingEventArgs args)
        {
            var time = args.RenderingTime;
            if (_last != TimeSpan.Zero && (time - _last).TotalMilliseconds < _every * _period - _period / 2) return;
            _last = time;
        }
        double now = Eased.Now;
        for (int i = Moving.Count - 1; i >= 0; i--)
        {
            var element = Moving[i];
            if (!Steps.TryGetValue(element, out var step) || !step(now)) { Moving.RemoveAt(i); Steps.Remove(element); }
        }
        if (Moving.Count > 0) return;
        _hooked = false; CompositionTarget.Rendering -= Tick;
    }
}
