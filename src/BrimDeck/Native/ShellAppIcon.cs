using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrimDeck.Native;

// The icon Windows shows for an application in its Apps folder (the Start menu's list), found by the application's user model
// id, or the icon of a program file. Desktop applications that send notifications through a Start menu shortcut (QQ, WeChat's installer, most Win32 programs)
// give no logo through the notification itself; the shell still knows their icon from the shortcut or the program. Read on a
// thread of its own in the single-threaded apartment the shell's objects expect; returned as PNG bytes.
internal static class ShellAppIcon
{
    public static Task<byte[]?> PngAsync(string appUserModelId, int size = 128)
        => string.IsNullOrWhiteSpace(appUserModelId) ? Task.FromResult<byte[]?>(null) : ReadAsync(@"shell:AppsFolder\" + appUserModelId, size);

    // The icon of a program file; the program is not run (its icon is read from its resources, the file loaded as data). The
    // caller checks the path (NoticeIcons). The shell's image is not taken first: for a program whose large icon it has not
    // cached, it gives the 32 pixel icon in the middle of a framed square of the size asked for, which stays small on a plate
    // (seen with the Qoder and CodeAgent CLIs, 2026-11-28). It is still taken for a program without an icon of its own.
    public static Task<byte[]?> FilePngAsync(string path, int size = 128) => ReadAsync(path, size, program: true);

    private static Task<byte[]?> ReadAsync(string parsingName, int size, bool program = false)
    {
        var done = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult((program ? FromResources(parsingName, size) : null) ?? Read(parsingName, size)); }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or IOException or NotSupportedException) { done.SetResult(null); }
        }) { IsBackground = true, Name = "BrimDeck app icon" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static byte[]? Read(string parsingName, int size)
    {
        if (parsingName.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase) && TargetMissing(parsingName)) return null;
        var factoryId = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref factoryId, out var factory) != 0 || factory is null) return null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, IconOnly | BiggerSizeOk, out bitmap) != 0 || bitmap == IntPtr.Zero) return null;
            if (Pixels(bitmap) is not { } source) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            Marshal.ReleaseComObject(factory);
        }
    }

    // Whether the Start menu entry is a shortcut whose program is gone (an application uninstalled without removing its
    // entry): the shell then gives the default program icon, which is no icon of the application's (seen with Cursor's
    // entry left behind, 2026-11-28). Only a target on a local drive is looked at; a packaged application has none.
    private static bool TargetMissing(string parsingName)
    {
        var itemId = typeof(IShellItem2).GUID;
        if (SHCreateItemFromParsingName2(parsingName, IntPtr.Zero, ref itemId, out var item) != 0 || item is null) return false;
        try
        {
            var key = new PropertyKey { Format = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), Id = 2 };
            if (item.GetString(ref key, out var target) != 0 || target is not { Length: > 3 }) return false;
            bool local = char.IsAsciiLetter(target[0]) && target[1] == ':' && target[2] == '\\';
            return local && !File.Exists(target) && !Directory.Exists(target);
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    // The program's first icon at the size asked for, scaled by Windows from its nearest picture; null without one.
    private static byte[]? FromResources(string path, int size)
    {
        var icons = new IntPtr[1];
        if (PrivateExtractIcons(path, 0, size, size, icons, null, 1, 0) is 0 or uint.MaxValue || icons[0] == IntPtr.Zero) return null;
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally { DestroyIcon(icons[0]); }
    }

    // The shell gives a 32-bit section with premultiplied alpha, usually bottom-up; its rows are copied as they are, flipped if needed.
    private static BitmapSource? Pixels(IntPtr bitmap)
    {
        var section = new DibSection();
        if (GetObject(bitmap, Marshal.SizeOf<DibSection>(), ref section) == 0) return null;
        int width = section.Width, height = Math.Abs(section.Height);
        if (width <= 0 || height <= 0 || section.BitsPerPixel != 32 || section.Bits == IntPtr.Zero) return null;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        bool bottomUp = section.HeaderHeight > 0;
        for (int row = 0; row < height; row++)
            Marshal.Copy(section.Bits + (bottomUp ? height - 1 - row : row) * stride, pixels, row * stride, stride);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private const int BiggerSizeOk = 0x1, IconOnly = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    // BITMAP followed by the BITMAPINFOHEADER of a DIBSECTION; only what is read here is named.
    [StructLayout(LayoutKind.Sequential)]
    private struct DibSection
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPerPixel;
        public IntPtr Bits;
        public int HeaderSize, HeaderWidth, HeaderHeight;
        public ushort HeaderPlanes, HeaderBitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        public int Mask0, Mask1, Mask2;
        public IntPtr Section;
        public int Offset;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    // IShellItem's five methods, then IShellItem2's up to GetString; only GetString is called, the others hold their places.
    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        void BindToHandler(); void GetParent(); void GetDisplayName(); void GetAttributes(); void Compare();
        void GetPropertyStore(); void GetPropertyStoreWithCreateObject(); void GetPropertyStoreForKeys(); void GetPropertyDescriptionList();
        void Update(); void GetProperty(); void GetCLSID(); void GetFileTime(); void GetInt32();
        [PreserveSig] int GetString(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string? value);
    }

    [DllImport("shell32", CharSet = CharSet.Unicode, PreserveSig = true, EntryPoint = "SHCreateItemFromParsingName")]
    private static extern int SHCreateItemFromParsingName2(string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem2? item);

    [DllImport("shell32", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32")] private static extern int GetObject(IntPtr handle, int size, ref DibSection section);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int width, int height, [Out] IntPtr[] icons, [Out] uint[]? ids, uint count, uint flags);

    [DllImport("user32")] private static extern bool DestroyIcon(IntPtr icon);
}
