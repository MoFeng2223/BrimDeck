using System.IO;
using System.Runtime.InteropServices;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck.Native;

// brim.exe, BrimDeck's command line, beside BrimDeck.exe, and whether a terminal finds it: whether BrimDeck's folder is in PATH.
// The installer puts it there (installer/BrimDeck.iss); where it could not, or for a portable copy, the settings say so and
// offer to add it for the current user, which is all this does to PATH.
internal static class BrimCommand
{
    private const string UserKey = "Environment", MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", Value = "Path";

    public static string Folder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    public static string File => Path.Combine(AppContext.BaseDirectory, "brim.exe");
    public static bool Installed => System.IO.File.Exists(File);

    // Whether the user's or the machine's PATH names BrimDeck's folder, as the registry has them now (a terminal opened
    // earlier still has the PATH of then). False when neither can be read.
    public static bool OnPath()
    {
        try { return CommandPath.Contains(Read(Registry.CurrentUser, UserKey), Folder) || CommandPath.Contains(Read(Registry.LocalMachine, MachineKey), Folder); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    // Adds BrimDeck's folder to the current user's PATH and tells running programs (Explorer, so new terminals) to read their
    // environment again. Null when it worked, else why not.
    public static string? AddForUser()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UserKey, writable: true);
            string? current = key.GetValue(Value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            string changed = CommandPath.With(current, Folder);
            if (changed != (current ?? "")) key.SetValue(Value, changed, RegistryValueKind.ExpandString);
            // Read back: a value that did not take (a policy, a redirected registry) is a failure the user should hear of.
            if (!CommandPath.Contains(key.GetValue(Value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, Folder)) return Loc.T("写入后读回的 PATH 中没有这一项。", "PATH did not keep the entry.");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ex.Message; }
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 3000, out _);
        return null;
    }

    private static string? Read(RegistryKey root, string name)
    {
        using var key = root.OpenSubKey(name);
        return key?.GetValue(Value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    // HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG: a window that does not answer is passed over.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
