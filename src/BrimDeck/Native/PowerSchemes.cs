using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BrimDeck.Native;

public sealed record PowerPlanInfo(Guid Id, string Name, string Description, bool IsActive);

// Enumerates and switches Windows power schemes through powrprof.dll without spawning powercfg.exe.
public static class PowerSchemes
{
    private const uint AccessScheme = 16;
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;

    public static IReadOnlyList<PowerPlanInfo> List()
    {
        var active = GetActive();
        var list = new List<PowerPlanInfo>();
        uint guidSize = (uint)Marshal.SizeOf<Guid>();
        IntPtr buffer = Marshal.AllocHGlobal((int)guidSize);
        try
        {
            for (uint index = 0; index < 128; index++)
            {
                uint size = guidSize;
                uint status = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, buffer, ref size);
                if (status == ErrorNoMoreItems) break;
                if (status != ErrorSuccess) break;
                var id = Marshal.PtrToStructure<Guid>(buffer);
                string name = ReadString(id, description: false);
                if (string.IsNullOrWhiteSpace(name)) name = id.ToString("B");
                string desc = ReadString(id, description: true);
                list.Add(new PowerPlanInfo(id, name.Trim(), desc.Trim(), active == id));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    public static Guid? GetActive()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) != ErrorSuccess || ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    public static (bool Success, string? Error) SetActive(Guid id)
    {
        uint status = PowerSetActiveScheme(IntPtr.Zero, ref id);
        if (status == ErrorSuccess) return (true, null);
        return (false, new Win32Exception((int)status).Message);
    }

    private static string ReadString(Guid id, bool description)
    {
        uint size = 0;
        uint status = description
            ? PowerReadDescription(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size)
            : PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if ((status != ErrorSuccess && status != ErrorMoreData) || size < 2) return "";
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = description
                ? PowerReadDescription(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size)
                : PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size);
            if (status != ErrorSuccess) return "";
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        uint accessFlags, uint index, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDescription(IntPtr rootPowerKey, ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
