using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

internal enum WeChatGateResult { Open, NoModule, Unsupported, Denied, Refused }

// Turns Qt's accessibility on in a running desktop WeChat 4.x, so that UI Automation can read its chat list: it writes the
// value 1 to one byte of the WeChat process (WeChatGateScan says which and why), and writes 0 back when the reading is switched
// off or BrimDeck exits. Nothing else of the process is written, no code is injected and no file of WeChat is changed. It is the
// user's choice in the settings, off by default, since WeChat's terms do not allow a program to change it.
//
// Before the byte is written: the instruction that reads it must be in the running process exactly as in the file (so the
// address is not one of another build of the module), and the byte must hold 0. After it: WeChat must hand out its controls
// (treeOpen), or the 0 is written back at once. What was written is recorded in the data folder first (the process's id and
// start time, the address), so that a BrimDeck that ended without putting it back does so when it next starts.
internal static class WeChatGate
{
    private const string FileName = "wechat-gate.json";
    private const uint QueryLimited = 0x1000, VmOperation = 0x8, VmRead = 0x10, VmWrite = 0x20;
    private static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(3);

    private sealed class Written { public int Pid { get; set; } public long Started { get; set; } public uint Check { get; set; } public uint Flag { get; set; } public string Module { get; set; } = ""; }
    private sealed class Scanned { public string Module { get; set; } = ""; public long Size { get; set; } public long Changed { get; set; } public List<uint[]> Sites { get; set; } = []; }
    private sealed class State { public Written? Written { get; set; } public Scanned? Scanned { get; set; } }

    public static WeChatGateResult Open(int pid, Func<bool> treeOpen, string folder)
    {
        if (Module(pid) is not { } module) return WeChatGateResult.NoModule;
        var state = Load(folder);
        var sites = Sites(module.Path, state, folder);
        if (sites.Count == 0) return WeChatGateResult.Unsupported;
        nint process = OpenProcess(QueryLimited | VmOperation | VmRead | VmWrite, false, pid);
        if (process == 0) return WeChatGateResult.Denied;
        try
        {
            foreach (var site in sites)
            {
                if (!Matches(process, module.Base, site) || Flag(process, module.Base, site) is not { } flag) continue;
                // Already on (a screen reader was running, or an earlier BrimDeck wrote it and its record is still there).
                if (flag == 1) { if (treeOpen()) return WeChatGateResult.Open; continue; }
                if (flag != 0 || Started(pid) is not { } started) continue;
                state.Written = new Written { Pid = pid, Started = started, Check = site.Check, Flag = site.Flag, Module = module.Path };
                Save(folder, state);
                if (!Write(process, module.Base, site, 1)) { state.Written = null; Save(folder, state); continue; }
                var wait = Stopwatch.StartNew();
                do { if (treeOpen()) return WeChatGateResult.Open; Thread.Sleep(150); } while (wait.Elapsed < OpenWait);
                Write(process, module.Base, site, 0);
                state.Written = null; Save(folder, state);
            }
            return WeChatGateResult.Refused;
        }
        finally { CloseHandle(process); }
    }

    // Writes 0 back where this or an earlier BrimDeck wrote 1, if that WeChat still runs, and forgets the record.
    public static void Restore(string folder)
    {
        var state = Load(folder);
        if (state.Written is not { } written) return;
        state.Written = null;
        try
        {
            if (Started(written.Pid) != written.Started || Module(written.Pid) is not { } module || !string.Equals(module.Path, written.Module, StringComparison.OrdinalIgnoreCase)) return;
            nint process = OpenProcess(QueryLimited | VmOperation | VmRead | VmWrite, false, written.Pid);
            if (process == 0) return;
            try
            {
                var site = new WeChatGateSite(written.Check, written.Flag);
                if (Matches(process, module.Base, site) && Flag(process, module.Base, site) == 1) Write(process, module.Base, site, 0);
            }
            finally { CloseHandle(process); }
        }
        finally { Save(folder, state); }
    }

    // Whether a record of a written byte is waiting to be put back.
    public static bool Pending(string folder) => Load(folder).Written is not null;

    private static (nint Base, string Path)? Module(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            foreach (ProcessModule module in process.Modules)
                using (module)
                    if (string.Equals(module.ModuleName, "Weixin.dll", StringComparison.OrdinalIgnoreCase) && module.FileName is { Length: > 0 } path) return (module.BaseAddress, path);
        }
        // Gone meanwhile, still starting, or one BrimDeck may not look into (WeChat run as administrator).
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
        return null;
    }

    private static long? Started(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.StartTime.ToFileTimeUtc(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    // The places of the module's file, scanned once for each build of it (the file is about 200 MB) and kept with its size and date.
    private static IReadOnlyList<WeChatGateSite> Sites(string path, State state, string folder)
    {
        try
        {
            var file = new FileInfo(path);
            if (state.Scanned is { } kept && string.Equals(kept.Module, path, StringComparison.OrdinalIgnoreCase) && kept.Size == file.Length && kept.Changed == file.LastWriteTimeUtc.Ticks)
                return [.. kept.Sites.Where(site => site.Length == 2).Select(site => new WeChatGateSite(site[0], site[1]))];
            IReadOnlyList<WeChatGateSite> sites;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan)) sites = WeChatGateScan.Sites(stream);
            state.Scanned = new Scanned { Module = path, Size = file.Length, Changed = file.LastWriteTimeUtc.Ticks, Sites = [.. sites.Select(site => new[] { site.Check, site.Flag })] };
            Save(folder, state);
            return sites;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool Matches(nint process, nint module, WeChatGateSite site)
    {
        var code = new byte[WeChatGateScan.ShapeLength];
        return ReadProcessMemory(process, module + (nint)site.Check, code, code.Length, out nint read) && read == code.Length
            && WeChatGateScan.IsShape(code, out long target) && site.Check + target == site.Flag;
    }

    private static byte? Flag(nint process, nint module, WeChatGateSite site)
    {
        var value = new byte[1];
        return ReadProcessMemory(process, module + (nint)site.Flag, value, 1, out nint read) && read == 1 ? value[0] : null;
    }

    private static bool Write(nint process, nint module, WeChatGateSite site, byte value)
        => WriteProcessMemory(process, module + (nint)site.Flag, [value], 1, out nint written) && written == 1;

    private static State Load(string folder)
    {
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(Path.Combine(folder, FileName))) ?? new State(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new State(); }
    }

    private static void Save(string folder, State state)
    {
        try { File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(state)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nint size, out nint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nint size, out nint written);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
