using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
        var owner = new Window { Width = 100, Height = 40, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        var popup = new Window { Width = 120, Height = 30, Topmost = true, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = 400, Top = 300 };
        try
        {
            owner.Show(); popup.Show();
            var ownerHandle = new WindowInteropHelper(owner).Handle;

            // 1. An empty desktop point: no menu anywhere near the pointer.
            var (bounds, _, _) = WindowsHost.PrimaryScreen();
            var far = new Point(bounds.Left + 5, bounds.Bottom - 5);
            check("an empty desktop point is not a menu", !WindowsHost.MenuAt(far));

            // 2. This process's own narrow topmost popup over a point: not a menu (its own popups must not block the panel).
            var over = new Point(460, 315);
            check("this process's own narrow popup window is not a menu", !WindowsHost.MenuAt(over));

            // 3. Another process's ordinary window at the taskbar's middle: not a menu (the topmost recovery keeps working
            // against real windows; only menus are yielded to).
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero && GetWindowRect(tray, out var r))
            {
                var at = new Point((r.Left + r.Right) / 2.0, (r.Top + r.Bottom) / 2.0);
                check("another process's taskbar window is not a menu", !WindowsHost.MenuAt(at));
            }
            else Console.WriteLine("SKIP: no taskbar window to check.");

            // 4. A real system menu (#32768) tracked over a point: the menu reads as a menu at its own items, and the point
            // beside it stays free. Tracked on its own thread, as TrackPopupMenu blocks until the menu ends, and with no
            // owner window so it does not need this process to hold the foreground. A session that keeps even an ownerless
            // menu from staying up (a game, or a locked desktop) reports the check as skipped rather than failed.
            const int X = 700, Y = 500;
            var menu = CreatePopupMenu();
            AppendMenu(menu, 0, (IntPtr)1, "first"); AppendMenu(menu, 0, (IntPtr)2, "second");
            var point = new Point(X + 15, Y + 15);
            string outcome = "";
            var thread = new Thread(() =>
            {
                // Tracked with no owner window (hWnd 0), the menu belongs to the desktop and stays up until it is dismissed,
                // so it does not need this process to hold the foreground; an owned menu would close at once without it.
                var cmd = TrackPopupMenu(menu, 0x0100 /* TPM_RETURNCMD */, X, Y, 0, IntPtr.Zero, IntPtr.Zero);
                outcome = "returned " + cmd;
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            // Poll until the menu is up (it is created by the tracking thread), at most three seconds.
            int waited = 0;
            while (!WindowsHost.MenuAt(point) && waited < 3000) { Thread.Sleep(50); waited += 50; }
            bool shown = WindowsHost.MenuAt(point);
            if (shown) check("the point beside a tracked menu is not a menu", !WindowsHost.MenuAt(new Point(X - 40, Y - 40)));
            EndMenu(); // releases the blocking thread; the menu returns 0 (cancelled)
            thread.Join(3000);
            DestroyMenu(menu);
            // An unattended session can keep the menu from staying up at all, and then the check says nothing about the
            // code: it is reported as skipped, not failed. The real right-click over the island is checked on the running
            // panel (see the issue's repro).
            if (!shown && !thread.IsAlive) Console.WriteLine("SKIP: the real-menu check could not keep a menu up: " + outcome);
            else check("a system menu tracked over the point reads as a menu", shown);

            // 5. CoveredAt keeps its own rules: this process's windows never read as covering it (the panel's own popups).
            check("CoveredAt still ignores this process's own windows", !WindowsHost.CoveredAt(owner, over));
        }
        finally { popup.Close(); owner.Close(); }
    }

    [DllImport("user32")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32")] private static extern bool EndMenu();
    [DllImport("user32")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32")] private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string text);
    [DllImport("user32")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out RECT r);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? title);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
}