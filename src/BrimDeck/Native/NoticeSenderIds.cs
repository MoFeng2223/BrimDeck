using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace BrimDeck.Native;

// The application id a program's Windows notices are most likely sent under, for adding an application on the "应用" page
// before it has sent one. A running program has the id the taskbar groups its windows by (AppIdentity: a packaged
// application's own, an id the program chose, or one Windows derives from its Start menu entry); a program that is not
// running has the id of the Start menu entry that starts it. Either may differ from the sender's: a helper program may send
// the notices, or a browser sends each site's under an id of its own. Measured 2026-11-26: Outlook, Chrome, Obsidian and
// programs listed only by their path have the same id in the taskbar and in the Start menu.
internal static class NoticeSenderIds
{
    internal sealed record Sender(string AppId, string Name, string Path);

    // One entry per application id among the programs with a visible window; BrimDeck itself and programs without an id are left out.
    public static IReadOnlyList<Sender> Running()
    {
        var found = new Dictionary<string, Sender>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<uint>();
        uint own = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0) return true;
            GetWindowThreadProcessId(window, out var pid);
            if (pid == 0 || pid == own || !seen.Add(pid)) return true;
            var described = AppIdentity.Describe((int)pid);
            if (described.AppId.Length == 0 || found.ContainsKey(described.AppId)) return true;
            string path = AppIdentity.ImagePath((int)pid);
            string name = AppIdentity.RegisteredName(described.AppId) ?? (described.ProcessName.Length > 0 ? described.ProcessName : described.AppId);
            found[described.AppId] = new Sender(described.AppId, name, path);
            return true;
        }, IntPtr.Zero);
        return found.Values.OrderBy(sender => sender.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // The id of a chosen program: a running process's of that file, else the Start menu entry's that starts it; null when
    // neither is found. Reading the Start menu's list takes about a second (240 entries, measured), so it runs on a thread of
    // its own in the apartment the shell's objects expect.
    public static async Task<Sender?> ForExecutableAsync(string path)
    {
        string full = Path.GetFullPath(path);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(full)))
            using (process)
            {
                if (!string.Equals(AppIdentity.ImagePath(process.Id), full, StringComparison.OrdinalIgnoreCase)) continue;
                var described = AppIdentity.Describe(process.Id);
                if (described.AppId.Length > 0)
                    return new Sender(described.AppId, AppIdentity.RegisteredName(described.AppId) ?? described.ProcessName, full);
            }
        var done = new TaskCompletionSource<Sender?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(FromStartMenu(full)); }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or InvalidOperationException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { done.SetResult(null); }
        }) { IsBackground = true, Name = "BrimDeck notice sender id" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await done.Task;
    }

    private static Sender? FromStartMenu(string full)
    {
        if (Type.GetTypeFromProgID("Shell.Application") is not { } type) return null;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic? folder = shell.NameSpace("shell:AppsFolder");
            if (folder is null) return null;
            foreach (dynamic item in folder.Items())
            {
                if (item.ExtendedProperty("System.Link.TargetParsingPath") is not string target
                    || !string.Equals(target, full, StringComparison.OrdinalIgnoreCase)) continue;
                if (item.Path is string { Length: > 0 } appId) return new Sender(appId, item.Name as string ?? Path.GetFileNameWithoutExtension(full), full);
            }
            return null;
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
