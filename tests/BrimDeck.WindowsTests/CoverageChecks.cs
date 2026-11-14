using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BrimDeck.Native;

// WindowsHost.MenuAt: the pointer checks that decide whether hovering opens the panel must yield to a system menu raised
// over the island even while the panel is topmost (WatchOrder's promise, MainWindow.TrackPointer). A real #32768 menu is
// tracked over a real window; the windows that must NOT read as a menu are the control: this process's own narrow popup
// (its own popups must not block the panel), another process's ordinary window (the taskbar), and an empty desktop point.
internal static class CoverageChecks
{
    public static void Run(Action<string, bool> check)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) { Console.WriteLine("SKIP: menu coverage checks need the Win32 menu APIs."); return; }
        var owner = new Window { Width = 100, Height = 40, Left = 300, Top = 300, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        var popup = new Window { Width = 120, Height = 30, Topmost = true, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = 400, Top = 300 };
        IntPtr menu = IntPtr.Zero;
        var thread = new Thread(() => { });
        try
        {
            owner.Show(); popup.Show();
            var ownerHandle = new WindowInteropHelper(owner).Handle;

            // 1. An empty desktop point: no menu anywhere near the pointer.
            var (bounds, _, _) = WindowsHost.PrimaryScreen();
            var far = new Point(bounds.Left + 5, bounds.Bottom - 5);
            check("an empty desktop point is not a menu", !WindowsHost.MenuAt(far));

            // 2. This process's own narrow topmost popup over a point: not a menu while it has no owner (an unowned tool
            // window must not block the panel), and a menu once it is owned by a shown window, the way the shelf's own
            // ContextMenu hosts are owned by the panel (the panel must yield to those even while topmost).
            var over = new Point(460, 315);
            check("this process's own unowned narrow popup is not a menu", !WindowsHost.MenuAt(over));
            var shelfLike = new Window { Width = 167, Height = 257, Topmost = true, ShowInTaskbar = false, ShowActivated = false,
                WindowStyle = WindowStyle.None, Owner = owner, Left = 900, Top = 300 };
            try
            {
                shelfLike.Show();
                check("this process's own narrow popup owned by a shown window is a menu", WindowsHost.MenuAt(new Point(950, 350)));
            }
            finally { shelfLike.Close(); }

            // 3. Another process's ordinary window at the taskbar's middle: not a menu (the topmost recovery keeps working
            // against real windows; only menus are yielded to).
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero && GetWindowRect(tray, out var r))
            {
                var at = new Point((r.Left + r.Right) / 2.0, (r.Top + r.Bottom) / 2.0);
                check("another process's taskbar window is not a menu", !WindowsHost.MenuAt(at));
            }
            else Console.WriteLine("SKIP: no taskbar window to check.");

            // 4. A real system menu (#32768): tracked on its own thread (TrackPopupMenu blocks until the menu ends) over this
            // thread's owner window, then found by scanning the desktop for a visible #32768 window rather than trusting a
            // fixed coordinate — Windows moves a menu that would not fit, and another app's window at the requested point
            // makes the call fail outright, neither of which says anything about MenuAt. Where a menu is really on screen,
            // MenuAt must read it as a menu at its own centre; where none can be held up, the check is skipped.
            menu = CreatePopupMenu();
            AppendMenu(menu, 0, (IntPtr)1, "first"); AppendMenu(menu, 0, (IntPtr)2, "second");
            string outcome = "";
            thread = new Thread(() =>
            {
                uint fore = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                uint self = GetCurrentThreadId();
                bool attached = fore != 0 && fore != self && AttachThreadInput(self, fore, true);
                keybd_event(0x12 /* VK_MENU */, 0, 0, IntPtr.Zero); keybd_event(0x12, 0, 2 /* KEYEVENTF_KEYUP */, IntPtr.Zero);
                bool front = SetForegroundWindow(ownerHandle);
                if (attached) AttachThreadInput(self, fore, false);
                int cmd = front ? TrackPopupMenu(menu, 0x0100 /* TPM_RETURNCMD */, 700, 500, 0, ownerHandle, IntPtr.Zero) : -1;
                outcome = (front ? "tracked" : "no foreground") + ", returned " + cmd;
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            // The owner window belongs to this thread: the menu loop sends it messages and waits for the answers, so the
            // waits pump this thread's queue rather than sleeping.
            int waited = 0;
            RECT menuRect = default;
            bool onScreen = false;
            while (waited < 2000)
            {
                Pump(TimeSpan.FromMilliseconds(50)); waited += 50;
                if ((onScreen = FindMenu(out menuRect))) break;
            }
            if (onScreen)
            {
                var centre = new Point((menuRect.Left + menuRect.Right) / 2.0, (menuRect.Top + menuRect.Bottom) / 2.0);
                check("a system menu on screen reads as a menu at its centre", WindowsHost.MenuAt(centre));
                // WM_CANCELMODE to the menu's own window dismisses it in the tracking loop (EndMenu reaches only its caller).
                var hit = GetAncestor(WindowFromPoint(new POINT { X = (int)centre.X, Y = (int)centre.Y }), 2);
                if (hit != IntPtr.Zero) PostMessage(hit, 0x001F /* WM_CANCELMODE */, IntPtr.Zero, IntPtr.Zero);
            }
            waited = 0;
            while (thread.IsAlive && waited < 3000) { Pump(TimeSpan.FromMilliseconds(50)); waited += 50; }
            if (thread.IsAlive) outcome += "; still tracking";
            else if (menu != IntPtr.Zero) { DestroyMenu(menu); menu = IntPtr.Zero; }
            if (!onScreen) Console.WriteLine("SKIP: no system menu could be held on screen in this session: " + outcome);

            // 5. CoveredAt keeps its own rules: this process's windows never read as covering it (the panel's own popups).
            check("CoveredAt still ignores this process's own windows", !WindowsHost.CoveredAt(owner, over));
        }
        finally
        {
            // Closing the owner dismisses a menu still tracked over it; only then is the menu handle safe to destroy.
            int left = 0;
            while (thread.IsAlive && left < 1000) { Pump(TimeSpan.FromMilliseconds(50)); left += 50; }
            if (menu != IntPtr.Zero && !thread.IsAlive) { DestroyMenu(menu); menu = IntPtr.Zero; }
            popup.Close(); owner.Close();
        }
    }

    // Waits while pumping this thread's messages, as the menu loop of a window owned here sends to it.
    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    // The first visible system menu on the desktop and its rectangle, wherever Windows placed it.
    private static bool FindMenu(out RECT rect)
    {
        var found = false; var hit = default(RECT);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(32); GetClassName(h, sb, 32);
            if (sb.ToString() != "#32768") return true;
            if (!GetWindowRect(h, out hit)) return true;
            found = true; return false;
        }, IntPtr.Zero);
        rect = hit;
        return found;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32", SetLastError = true)] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32")] private static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32")] private static extern void SetLastError(uint error);
    [DllImport("user32")] private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);
    [DllImport("user32")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32")] private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string text);
    [DllImport("user32")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out RECT r);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? title);
    private delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] private static extern int GetClassName(IntPtr window, StringBuilder name, int max);
}
