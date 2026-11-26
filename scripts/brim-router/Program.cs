using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BrimRouter;

// brim.exe for a fixed folder: finds the BrimDeck.exe running in this Windows session, runs the brim.exe beside it with this
// program's own command line, unchanged, and exits with its exit code. Standard input, output and error are inherited, so a
// hook's JSON on standard input and brim's replies pass through as they are. With several BrimDeck instances (which BrimDeck
// itself does not allow) the one started last is used. When none runs, it exits at once with code 3, as brim.exe does when
// BrimDeck is not running. "brim --router-target" prints the brim.exe it would run.
internal static partial class Program
{
    private const int NotRunning = 3;

    private static int Main()
    {
        string own = Environment.ProcessPath ?? "";
        string? target = RunningBrim(own);
        string arguments = ArgumentsOf(Marshal.PtrToStringUni(GetCommandLineW()) ?? "");

        if (arguments.Trim() == "--router-target")
        {
            Console.WriteLine(target ?? "(BrimDeck is not running)");
            return target is null ? NotRunning : 0;
        }
        if (target is null)
        {
            Console.Error.WriteLine("brim: BrimDeck is not running, or the running BrimDeck has no brim.exe in its folder.");
            return NotRunning;
        }

        // Ctrl+C reaches brim.exe through the shared console; this program waits for its exit code instead of ending first.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        using var child = Process.Start(new ProcessStartInfo(target, arguments) { UseShellExecute = false })!;
        child.WaitForExit();
        return child.ExitCode;
    }

    // The command line after the program's name, as Windows reads that name: up to the first space or tab outside quotes.
    internal static string ArgumentsOf(string commandLine)
    {
        int at = 0;
        bool quoted = false;
        for (; at < commandLine.Length; at++)
        {
            char c = commandLine[at];
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is ' ' or '\t') break;
        }
        return commandLine[at..].TrimStart(' ', '\t');
    }

    // brim.exe beside the BrimDeck.exe of this session started last, or null.
    private static string? RunningBrim(string own)
    {
        int session = Process.GetCurrentProcess().SessionId;
        string? best = null;
        long bestStart = long.MinValue;
        foreach (var process in Process.GetProcessesByName("BrimDeck"))
        {
            using (process)
            {
                if (process.SessionId != session) continue;
                if (ImageAndStart(process.Id) is not (string image, long start)) continue;
                string brim = Path.Combine(Path.GetDirectoryName(image)!, "brim.exe");
                if (!File.Exists(brim) || string.Equals(Path.GetFullPath(brim), Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase)) continue;
                if (start > bestStart) { best = brim; bestStart = start; }
            }
        }
        return best;
    }

    // The process's program file and creation time, read with the least access Windows grants (an elevated BrimDeck too).
    private static unsafe (string, long)? ImageAndStart(int id)
    {
        nint handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)id);
        if (handle == 0) return null;
        try
        {
            char* buffer = stackalloc char[1024];
            uint length = 1024;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref length)) return null;
            if (!GetProcessTimes(handle, out long created, out _, out _, out _)) return null;
            return (new string(buffer, 0, (int)length), created);
        }
        finally { CloseHandle(handle); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCommandLineW();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
