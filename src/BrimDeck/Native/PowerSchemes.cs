using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using BrimDeck.Core;

namespace BrimDeck.Native;

public sealed record PowerPlanInfo(Guid Id, string Name, string Description, bool IsActive);

public sealed record PowerPlanSwitchResult(bool Success, string? Error, string? HardwareMode);

// Enumerates and switches Windows power schemes through powrprof.dll without spawning powercfg.exe.
// On ASUS laptops exposing ASUS System Control Interface (\\.\ATKACPI), also syncs ThrottleThermalPolicy
// so hardware fan curves and TDP limits follow Silent / Performance / Turbo switches.
public static class PowerSchemes
{
    private const uint AccessScheme = 16;
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;

    private static readonly Guid PowerSaverGuid = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid BalancedGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformanceGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UltimatePerformanceGuid = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    // Windows 10/11 power mode overlay GUIDs (Best Power Efficiency / Balanced / Best Performance).
    private static readonly Guid OverlayEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid OverlayBalanced = Guid.Empty;
    private static readonly Guid OverlayPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

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
        return FilterDisplayPlans(list);
    }

    // On ASUS systems with Armoury Crate's standard Silent / Performance / Turbo plans, show those three in order
    // (hiding duplicate Windows Balanced and PD-only variants). Non-ASUS systems keep all enumerated plans.
    private static IReadOnlyList<PowerPlanInfo> FilterDisplayPlans(List<PowerPlanInfo> plans)
    {
        if (!HasAsusThermalInterface()) return plans;
        var silent = plans.FirstOrDefault(p => p.Name.Equals("Silent", StringComparison.OrdinalIgnoreCase));
        var performance = plans.FirstOrDefault(p => p.Name.Equals("Performance", StringComparison.OrdinalIgnoreCase));
        var turbo = plans.FirstOrDefault(p => p.Name.Equals("Turbo", StringComparison.OrdinalIgnoreCase));
        if (silent is not null && performance is not null && turbo is not null)
            return [silent, performance, turbo];
        return plans;
    }

    private static bool HasAsusThermalInterface()
    {
        const uint GenericReadWrite = 0xC0000000;
        const uint FileShareReadWrite = 3;
        const uint OpenExisting = 3;
        const uint FileAttributeNormal = 0x80;
        IntPtr handle = CreateFileW(@"\\.\ATKACPI", GenericReadWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        CloseHandle(handle);
        return true;
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

    public static PowerPlanSwitchResult SetActive(PowerPlanInfo plan)
    {
        var (ok, error) = SetActive(plan.Id);
        if (!ok) return new(false, error, null);

        var tier = ClassifyTier(plan);
        TryApplyOverlay(tier);
        string? hw = TryApplyAsusThermalPolicy(tier);
        return new(true, null, hw);
    }

    // 0 = Balanced/Performance, 1 = Turbo, 2 = Silent (matches ASUS ThrottleThermalPolicy 0x00120075).
    private static int? ClassifyTier(PowerPlanInfo plan)
    {
        if (plan.Id == PowerSaverGuid || Matches(plan.Name, "silent", "saver", "eco", "quiet", "节能", "静音"))
            return 2;
        if (plan.Id == UltimatePerformanceGuid || Matches(plan.Name, "turbo", "ultimate", "增强", "狂暴", "野兽", "卓越"))
            return 1;
        if (plan.Id == BalancedGuid || plan.Id == HighPerformanceGuid || Matches(plan.Name, "performance", "balanced", "平衡", "性能"))
            return 0;
        return null;
    }

    private static void TryApplyOverlay(int? tier)
    {
        if (tier is null) return;
        try
        {
            Guid overlay = tier.Value switch
            {
                2 => OverlayEfficiency,
                1 => OverlayPerformance,
                _ => OverlayBalanced
            };
            PowerSetActiveOverlayScheme(overlay);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
    }

    private static string? TryApplyAsusThermalPolicy(int? tier)
    {
        if (tier is null) return null;
        const uint GenericReadWrite = 0xC0000000;
        const uint FileShareReadWrite = 3;
        const uint OpenExisting = 3;
        const uint FileAttributeNormal = 0x80;
        const uint IoctlAtkAcpi = 0x0022240C;
        const uint ThrottleThermalPolicy = 0x00120075;

        IntPtr handle = CreateFileW(@"\\.\ATKACPI", GenericReadWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
        try
        {
            byte[] input = new byte[16];
            Encoding.ASCII.GetBytes("DEVS", 0, 4, input, 0);
            BitConverter.GetBytes(8).CopyTo(input, 4);
            BitConverter.GetBytes(ThrottleThermalPolicy).CopyTo(input, 8);
            BitConverter.GetBytes((uint)tier.Value).CopyTo(input, 12);
            byte[] output = new byte[16];
            if (!DeviceIoControl(handle, IoctlAtkAcpi, input, (uint)input.Length, output, (uint)output.Length, out _, IntPtr.Zero))
                return null;
            if (BitConverter.ToInt32(output, 0) != 1) return null;
            return tier.Value switch
            {
                2 => Loc.T("已联动华硕散热模式：静音", "ASUS thermal mode: Silent"),
                1 => Loc.T("已联动华硕散热模式：增强", "ASUS thermal mode: Turbo"),
                _ => Loc.T("已联动华硕散热模式：性能/均衡", "ASUS thermal mode: Performance")
            };
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool Matches(string text, params string[] keywords)
    {
        foreach (var kw in keywords)
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDescription(IntPtr rootPowerKey, ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, byte[] lpInBuffer, uint nInBufferSize,
        byte[] lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
