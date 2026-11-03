using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BrimDeck.Modules;

// Host-private directory identity, captured before destructive maintenance is queued.
// File ID plus volume and creation time distinguish a recreated same-name directory.
internal static class ModuleDirectoryIdentity
{
    internal static string? Read(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Module file maintenance requires Windows.");
        string full = Path.GetFullPath(path);
        string native = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        using var handle = Open(native, 0x80, 7, 0, 3, 0x02000000 | 0x00200000, 0);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastPInvokeError(); if (code is 2 or 3) return null;
            throw new IOException("Cannot identify module directory: " + new Win32Exception(code).Message);
        }
        if (!Info(handle, out var info)) throw new IOException("Cannot identify module directory: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0) throw new IOException("Expected an unlinked module directory.");
        return $"win1-{info.Volume:X8}-{info.IndexHigh:X8}{info.IndexLow:X8}-{info.CreatedHigh:X8}{info.CreatedLow:X8}";
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        internal uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WrittenLow, WrittenHigh;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle Open(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Info(SafeFileHandle file, out FileInfo info);
}
