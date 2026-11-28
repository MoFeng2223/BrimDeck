using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BrimDeck.Native;

// Keeps WPF's renderer (wpfgfx_cor3.dll) from loading d3d9.dll, so WPF falls back to its software rasterizer and the process
// holds no Direct3D 9 runtime. NVIDIA Advanced Optimus refuses to switch the laptop panel to the discrete GPU while a DirectX 9
// application runs. WPF's own software mode (RenderOptions, DisableHWAcceleration) still loads d3d9.dll, and the switch stays
// blocked with it (issue 408).
// wpfgfx loads the module with LoadLibrary("d3d9.dll") (CD3DModuleLoaderInternal::LoadD3DModule in dotnet/wpf) and treats a
// failure as "no Direct3D": every render target then runs in software. The block replaces wpfgfx's imports of LoadLibraryW
// and LoadLibraryExW; every other module, and every other library wpfgfx loads, is untouched.
internal static unsafe class Direct3D9Block
{
    private static nint _loadLibraryW, _loadLibraryExW;

    public static int Refused { get; private set; }

    // Must run before WPF starts rendering (before the first window). Returns false when wpfgfx could not be found or patched;
    // WPF then renders as before.
    public static bool Install()
    {
        try
        {
            var folder = Path.GetDirectoryName(typeof(System.Windows.Media.Visual).Assembly.Location);
            if (string.IsNullOrEmpty(folder)) return false;
            var module = NativeLibrary.Load(Path.Combine(folder, "wpfgfx_cor3.dll"));
            return Patch(module);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or IOException) { return false; }
    }

    private static bool Patch(nint module)
    {
        var image = (byte*)module;
        if (*(ushort*)image != 0x5A4D) return false;
        var header = image + *(int*)(image + 0x3C);
        if (*(uint*)header != 0x4550) return false;
        var optional = header + 24;
        if (*(ushort*)optional != 0x20B) return false; // PE32+ only: BrimDeck is x64.
        var importRva = *(uint*)(optional + 112 + 8);
        if (importRva == 0) return false;
        var patched = 0;
        for (var entry = image + importRva; *(uint*)(entry + 12) != 0; entry += 20)
        {
            var library = Marshal.PtrToStringAnsi((nint)(image + *(uint*)(entry + 12)));
            if (!string.Equals(library, "KERNEL32.dll", StringComparison.OrdinalIgnoreCase)) continue;
            var names = (ulong*)(image + (*(uint*)entry != 0 ? *(uint*)entry : *(uint*)(entry + 16)));
            var slots = (nint*)(image + *(uint*)(entry + 16));
            for (var i = 0; names[i] != 0; i++)
            {
                if ((names[i] >> 63) != 0) continue;
                var name = Marshal.PtrToStringAnsi((nint)(image + (uint)names[i] + 2));
                nint replacement;
                if (name == "LoadLibraryW") { _loadLibraryW = slots[i]; replacement = (nint)(delegate* unmanaged[Stdcall]<char*, nint>)&LoadW; }
                else if (name == "LoadLibraryExW") { _loadLibraryExW = slots[i]; replacement = (nint)(delegate* unmanaged[Stdcall]<char*, nint, uint, nint>)&LoadExW; }
                else continue;
                if (!VirtualProtect((nint)(slots + i), (nuint)sizeof(nint), PageReadWrite, out var old)) continue;
                slots[i] = replacement;
                VirtualProtect((nint)(slots + i), (nuint)sizeof(nint), old, out _);
                patched++;
            }
        }
        return patched > 0;
    }

    private static bool IsDirect3D9(char* path)
    {
        if (path == null) return false;
        var name = Path.GetFileName(new string(path));
        return name.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("d3d9", StringComparison.OrdinalIgnoreCase);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint LoadW(char* path)
    {
        if (IsDirect3D9(path)) { Refused++; SetLastError(ErrorModNotFound); return 0; }
        return ((delegate* unmanaged[Stdcall]<char*, nint>)_loadLibraryW)(path);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint LoadExW(char* path, nint file, uint flags)
    {
        if (IsDirect3D9(path)) { Refused++; SetLastError(ErrorModNotFound); return 0; }
        return ((delegate* unmanaged[Stdcall]<char*, nint, uint, nint>)_loadLibraryExW)(path, file, flags);
    }

    private const uint PageReadWrite = 0x04, ErrorModNotFound = 126;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint old);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint error);
}
