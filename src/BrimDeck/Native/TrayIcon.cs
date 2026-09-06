using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace BrimDeck.Native;

public sealed class TrayIcon : IDisposable
{
    private const int Callback = 0x8001;
    private readonly HwndSource _source;
    private readonly Action _settings;
    private readonly Action _exit;
    private NOTIFYICONDATA _data;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    public TrayIcon(Window owner, Action settings, Action exit)
    {
        _settings = settings; _exit = exit;
        var hwnd = new WindowInteropHelper(owner).Handle;
        _source = HwndSource.FromHwnd(hwnd); _source.AddHook(Hook);
        var path = Environment.ProcessPath!;
        var icon = ExtractIcon(IntPtr.Zero, path, 0);
        if (icon == IntPtr.Zero || icon == new IntPtr(1)) icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        _data = new NOTIFYICONDATA { Size = Marshal.SizeOf<NOTIFYICONDATA>(), Window = hwnd, Id = 1, Flags = 1 | 2 | 4, CallbackMessage = Callback,
            Icon = icon, Tip = "BrimDeck · 右键打开设置", Info = "", InfoTitle = "" };
        Shell_NotifyIcon(0, ref _data);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreated) Shell_NotifyIcon(0, ref _data);
        if (message != Callback) return IntPtr.Zero;
        var mouseMessage = lParam.ToInt32() & 0xffff;
        if (mouseMessage == 0x203) _settings();
        if (mouseMessage == 0x205)
        {
            SetForegroundWindow(hwnd);
            var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
            var settings = new MenuItem { Header = "设置…" }; settings.Click += (_, _) => _settings(); menu.Items.Add(settings);
            menu.Items.Add(new Separator());
            var exit = new MenuItem { Header = "退出 BrimDeck" }; exit.Click += (_, _) => _exit(); menu.Items.Add(exit);
            menu.IsOpen = true;
        }
        return IntPtr.Zero;
    }
    public void Dispose() { Shell_NotifyIcon(2, ref _data); _source.RemoveHook(Hook); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NOTIFYICONDATA
    {
        public int Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid GuidItem; public IntPtr BalloonIcon;
    }
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern IntPtr ExtractIcon(IntPtr instance, string file, uint index);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
}
