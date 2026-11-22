using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace BrimDeck.Native;

// What BrimDeck's helper processes need to know of rights and of each other. Windows keeps a program with fewer rights from
// sending input or window messages to one with more (User Interface Privilege Isolation): an ordinary BrimDeck cannot paste into a
// program run as administrator (PasteHelper), and files dragged from the shell cannot reach a BrimDeck run as administrator
// (DropHelper). Each helper runs with the rights the other side lacks and talks to BrimDeck through a named pipe; both ends check
// that the other end is the BrimDeck process they expect.
internal static class HelperRights
{
    private const int Administrator = 0x3000; // SECURITY_MANDATORY_HIGH_RID

    // The integrity level of a process (0x2000 ordinary, 0x3000 administrator), or -1 when it cannot be read.
    public static int Level(uint pid)
    {
        if (pid == Environment.ProcessId) return PasteInput.IntegrityLevel(GetCurrentProcess(), out _);
        IntPtr process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return -1;
        try { return PasteInput.IntegrityLevel(process, out _); }
        finally { CloseHandle(process); }
    }

    public static bool Elevated => Level((uint)Environment.ProcessId) >= Administrator;

    // Whether files dragged from the shell are kept from this process: it runs with more rights than the desktop's shell. Where
    // the shell itself runs as administrator (UAC turned off), drags reach BrimDeck as they are.
    public static bool AboveShell()
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero) return false;
        GetWindowThreadProcessId(shell, out uint pid);
        int own = Level((uint)Environment.ProcessId), theirs = Level(pid);
        return own >= 0 && theirs >= 0 && own > theirs;
    }

    // Whether the process runs this same executable: a helper and BrimDeck are one program started two ways.
    public static bool IsThisProgram(uint pid)
    {
        IntPtr process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            var path = new StringBuilder(32768);
            int length = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length)
                && string.Equals(Path.GetFullPath(path.ToString(0, length)), Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(process); }
    }

    public static uint? ServerOf(PipeStream pipe) => GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) ? pid : null;
    public static uint? ClientOf(PipeStream pipe) => GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) ? pid : null;

    // A pipe name no other program can guess beforehand.
    public static string PipeName(string kind) => "BrimDeck." + kind + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");

    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
    [DllImport("user32")] private static extern IntPtr GetShellWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}

// Starts a program with the rights of the desktop's shell rather than this process's, as Raymond Chen describes in "How can I launch
// an unelevated process from my elevated process and vice versa?": the shell's own desktop view is asked to run it, so it is the
// shell that starts it. A program started directly by a process run as administrator would run as administrator too.
internal static class ShellLaunch
{
    public static bool Start(string file, string arguments, string directory)
    {
        try
        {
            var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!)!;
            object location = 0, root = null!; // CSIDL_DESKTOP; no root
            var desktop = windows.FindWindowSW(ref location, ref root, 8, out _, 1); // SWC_DESKTOP, SWFO_NEEDDISPATCH
            if (desktop is not IServiceProvider services) return false;
            Guid topBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837"), browserId = typeof(IShellBrowser).GUID; // SID_STopLevelBrowser
            services.QueryService(ref topBrowser, ref browserId, out object found);
            if (found is not IShellBrowser browser || browser.QueryActiveShellView() is not { } view) return false;
            Guid dispatch = new("00020400-0000-0000-C000-000000000046"); // IID_IDispatch
            view.GetItemObject(0, ref dispatch, out object folderView); // SVGIO_BACKGROUND
            var application = folderView.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, folderView, null);
            if (application is null) return false;
            application.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, application, [file, arguments, directory, "open", 0]);
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or TargetInvocationException or MissingMethodException or ArgumentException)
        {
            return false;
        }
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        // Count, Item, _NewEnum, Register, RegisterPending, Revoke, OnNavigate, OnActivated: never called, kept for their places.
        void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object location, [In, MarshalAs(UnmanagedType.Struct)] ref object root, int kind, out int window, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        void QueryService(ref Guid service, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out object found);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        // IOleWindow's two, then InsertMenusSB to SendControlMsg: never called, kept for their places.
        void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9(); void _10(); void _11();
        IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        // IOleWindow's two, then TranslateAccelerator to SelectItem: never called, kept for their places.
        void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9(); void _10(); void _11();
        void GetItemObject(uint item, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out object found);
    }
}
