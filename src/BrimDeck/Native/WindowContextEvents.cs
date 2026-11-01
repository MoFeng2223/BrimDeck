using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BrimDeck.Native;

// One OS event source for all performance islands. No mouse hook and no periodic fallback timer.
internal static class WindowContextEvents
{
    private static readonly Dictionary<Action, Dispatcher> Subscribers = [];
    private static readonly List<nint> Hooks = [];
    private static readonly WinEvent Callback = OnEvent;
    private static int _queued;
    private static Dispatcher? _dispatcher;
    internal static int SubscriberCount => Subscribers.Count;
    internal static IDisposable Subscribe(Dispatcher dispatcher, Action refresh)
    {
        Subscribers.Add(refresh, dispatcher);
        if (Subscribers.Count == 1)
        {
            _dispatcher = dispatcher;
            foreach (var range in new[] { (3u, 3u), (0x16u, 0x17u), (0x8002u, 0x800Bu) })
            {
                var hook = SetWinEventHook(range.Item1, range.Item2, 0, Callback, 0, 0, 0);
                if (hook != 0) Hooks.Add(hook);
            }
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionSwitch += SessionChanged;
        }
        return new Subscription(refresh);
    }
    private static void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (kind >= 0x8000)
        {
            if (objectId != 0 || childId != 0 || window == 0) return;
            GetWindowThreadProcessId(window, out uint process);
            if (process == Environment.ProcessId) return;
            if (kind == 0x800B && window != GetForegroundWindow()) return;
        }
        Schedule();
    }
    private static void DisplayChanged(object? sender, EventArgs args) => Schedule();
    private static void PowerChanged(object sender, PowerModeChangedEventArgs args) => Schedule();
    private static void SessionChanged(object sender, SessionSwitchEventArgs args) => Schedule();
    private static void Schedule()
    {
        // SystemEvents can arrive off-thread; only the WPF dispatcher enumerates consumer ownership.
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _queued, 1) != 0) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _queued, 0);
            foreach (var (refresh, target) in Subscribers.ToArray())
                if (!target.HasShutdownStarted) refresh();
        });
    }
    private sealed class Subscription(Action refresh) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; Subscribers.Remove(refresh);
            if (Subscribers.Count != 0) return;
            _dispatcher = null;
            foreach (var hook in Hooks) UnhookWinEvent(hook);
            Hooks.Clear();
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            SystemEvents.PowerModeChanged -= PowerChanged;
            SystemEvents.SessionSwitch -= SessionChanged;
        }
    }
    private delegate void WinEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
