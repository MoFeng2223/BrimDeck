using System.Runtime.InteropServices;

namespace BrimDeck.Native;

// The running processes from one Toolhelp snapshot: their ids, parents and executable file names, without opening any of them.
// Far cheaper than Process.GetProcessesByName, which reads every process's threads too, so a player that is not running costs
// little to look for.
internal static class ProcessSnapshot
{
    // Each process's parent and executable file name.
    public static Dictionary<int, (int Parent, string File)> Read()
    {
        var processes = new Dictionary<int, (int, string)>();
        Walk(entry => { processes[entry.th32ProcessID] = (entry.th32ParentProcessID, entry.szExeFile); return true; });
        return processes;
    }

    // Whether a process of this executable ("cloudmusic.exe") runs.
    public static bool Any(string file) => Any(name => string.Equals(name, file, StringComparison.OrdinalIgnoreCase));
    // Whether a process runs whose executable's file name matches.
    public static bool Any(Func<string, bool> match)
    {
        bool found = false;
        Walk(entry => !(found = match(entry.szExeFile)));
        return found;
    }

    // Calls each until it returns false.
    private static void Walk(Func<PROCESSENTRY32W, bool> each)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == new IntPtr(-1)) return;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more && each(entry); more = Process32NextW(snapshot, ref entry)) { }
        }
        finally { CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public int dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
