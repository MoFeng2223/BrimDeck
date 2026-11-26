using System.Runtime.InteropServices;
using System.Text;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// The Win32 side of NoticeAppWindows: reads windows and brings one forward. It never moves the mouse, sends input to a
// window or reads what is in one (only the window's style, owner, whether it has a title, class name and process), and
// nothing it reads waits for another program's answer.
// Foreground: the island is a non-activating window, so the click that opens a notice's application leaves the foreground with
// another program. Windows lets the process that handled the last input event set the foreground, and TryFocus runs inside that
// click (LaunchAsync reaches it before its first await), so SetForegroundWindow is tried plainly first. When Windows refuses, the
// documented workaround is used once: this thread is attached to the foreground window's input queue for the duration of one
// BringWindowToTop and SetForegroundWindow call and detached again at once. The result is read back from GetForegroundWindow, so
// a refusal is reported (the launch fallback then runs) instead of assumed to have worked.
internal sealed class AppWindowSystem : IAppWindowSystem
{
    public IReadOnlyList<nint> TopLevelWindows()
    {
        var list = new List<nint>();
        EnumWindows((handle, _) => { list.Add(handle); return true; }, 0);
        return list;
    }

    public AppWindowInfo? Inspect(nint handle)
    {
        if (!IsWindow(handle)) return null;
        GetWindowThreadProcessId(handle, out uint pid);
        if (pid == 0) return null;
        long ex = (long)GetWindowLongPtr(handle, GwlExStyle);
        DwmGetWindowAttribute(handle, DwmCloaked, out int cloaked, sizeof(int));
        var name = new StringBuilder(64); GetClassName(handle, name, name.Capacity);
        return new AppWindowInfo((int)pid, IsWindowVisible(handle), cloaked != 0, (ex & WsExToolWindow) != 0, (ex & WsExAppWindow) != 0,
            GetWindow(handle, GwOwner) != 0, HasTitle(handle), IsIconic(handle),
            name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    // Whether the window has a title, read from what Windows keeps for it. GetWindowTextLength asks the window itself and waits
    // for its answer: a window of a suspended app never answers (the Settings app's, 2026-11-26), and the click that opened a
    // notice's application hung there on the UI thread until Windows closed BrimDeck.
    private static bool HasTitle(nint handle) => InternalGetWindowText(handle, new StringBuilder(2), 2) > 0;

    public string? ImagePath(int pid) => AppIdentity.ImagePath(pid);

    public bool Foreground(nint handle, bool restore)
    {
        if (restore) ShowWindow(handle, SwRestore);
        if (SetForegroundWindow(handle) && GetForegroundWindow() == handle) return true;
        nint front = GetForegroundWindow();
        uint frontThread = front == 0 ? 0 : GetWindowThreadProcessId(front, out _);
        uint mine = GetCurrentThreadId();
        if (frontThread != 0 && frontThread != mine && AttachThreadInput(mine, frontThread, true))
        {
            try { BringWindowToTop(handle); SetForegroundWindow(handle); }
            finally { AttachThreadInput(mine, frontThread, false); }
        }
        return GetForegroundWindow() == handle;
    }

    private const int GwlExStyle = -20, SwRestore = 9, DwmCloaked = 14;
    private const uint GwOwner = 4;
    private const long WsExToolWindow = 0x80, WsExAppWindow = 0x40000;
    private delegate bool EnumWindowsProc(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(nint handle, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
}
