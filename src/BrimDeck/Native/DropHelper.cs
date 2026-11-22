using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Files dragged in while BrimDeck runs as administrator. The shell runs without those rights, and Windows keeps its drag from
// reaching a window with more (OLE finds the window, but the drop target is never asked), so the drag ends on a no-entry
// pointer. At its start BrimDeck has the shell start this same program with the shell's rights (ShellLaunch) with Switch; that copy,
// the helper, keeps a nearly clear window of its own hidden. While a button is held over a panel that takes files and the press did
// not begin on BrimDeck, the panel has the window laid over the part that takes drops (MainWindow.DropHelper.cs). The shell's drag
// reaches that window; the helper passes what happens (enter, move, leave, drop, with the paths and the point) to BrimDeck, which
// raises it on its own window through WPF's drop target as if the files were over it (DropRelay.Forward), and answers with what
// the panel would do, which the helper gives back to the drag. After a drop or once the button is up, the window is hidden again.
//
// BrimDeck accepts only a helper that is this same program; the helper accepts only the BrimDeck whose process id it was given.
// It ends when the pipe does.
internal sealed class DropHelper : IDisposable
{
    public const string Switch = "--drop-helper";
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Dispatcher _dispatcher;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private string? _shown;
    private int _effect = -1;
    private bool _disposed;

    // The helper's window, once it has said which it is; zero while there is none.
    public IntPtr Window { get; private set; }
    public bool Ready => Window != IntPtr.Zero && !_disposed;
    // The panel the window was last laid over; its window receives the drag.
    public object? Owner { get; private set; }
    // A drag event from the helper, on the UI thread: enter, over, leave or drop, then its fields.
    public event Action<string[]>? Message;

    private DropHelper(Dispatcher dispatcher) => _dispatcher = dispatcher;

    // Started only when files from the shell cannot reach this process.
    public static DropHelper? StartIfNeeded(Dispatcher dispatcher)
    {
        if (!HelperRights.AboveShell()) return null;
        var helper = new DropHelper(dispatcher);
        _ = helper.StartAsync();
        return helper;
    }

    private async Task StartAsync()
    {
        var name = HelperRights.PipeName("Drop");
        if (!ShellLaunch.Start(Environment.ProcessPath!, $"{Switch} {name} {Environment.ProcessId}", AppContext.BaseDirectory)) return;
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(15000);
            if (HelperRights.ServerOf(pipe) is not { } server || !HelperRights.IsThisProgram(server) || _disposed) { pipe.Dispose(); return; }
        }
        catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException)
        {
            pipe.Dispose();
            return;
        }
        _pipe = pipe;
        _writer = new StreamWriter(pipe, Utf8, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var parts = HelperLine.Parse(line);
                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (_disposed) return;
                    if (parts is ["window", var value] && long.TryParse(value, out long handle)) { Window = new IntPtr(handle); WindowsHost.RelayWindow = Window; }
                    else
                    {
                        // The helper hides its window itself after a drop.
                        if (parts is ["drop", ..]) _shown = "hide";
                        Message?.Invoke(parts);
                    }
                });
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        _ = _dispatcher.BeginInvoke(() => { if (WindowsHost.RelayWindow == Window) WindowsHost.RelayWindow = IntPtr.Zero; Window = IntPtr.Zero; });
    }

    // Lays the window over an area on screen in physical pixels, for the panel that owns it.
    public void Show(object owner, Rect area)
    {
        Owner = owner;
        Send(HelperLine.Make("show", ((int)Math.Floor(area.X)).ToString(), ((int)Math.Floor(area.Y)).ToString(),
            ((int)Math.Ceiling(area.Width)).ToString(), ((int)Math.Ceiling(area.Height)).ToString()));
    }

    public void Hide() => Send("hide");

    // What the panel would do with the files (DragDropEffects), for the helper to give back to the drag.
    public void Effect(int effect)
    {
        if (effect == _effect) return;
        _effect = effect;
        Send(HelperLine.Make("effect", effect.ToString()), always: true);
    }

    private void Send(string line, bool always = false)
    {
        if (!Ready || _writer is null || !always && line == _shown) return;
        if (!always) _shown = line;
        try { _writer.WriteLine(line); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (WindowsHost.RelayWindow == Window) WindowsHost.RelayWindow = IntPtr.Zero;
        Window = IntPtr.Zero;
        _writer?.Dispose(); _pipe?.Dispose();
    }

    // ---- The helper ----
    public static int Run(string name, string parent)
    {
        if (!uint.TryParse(parent, out uint parentId)) return 1;
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try { if (!pipe.WaitForConnectionAsync().Wait(TimeSpan.FromSeconds(15))) return 1; }
        catch (AggregateException) { return 1; }
        if (HelperRights.ClientOf(pipe) != parentId) return 1;
        return new Relay(pipe).Run();
    }

    // The helper's window and its drop target, on this STA thread with a message loop of its own.
    private sealed class Relay
    {
        private const int WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_MOUSEACTIVATE = 0x0021, WM_APP = 0x8000, MA_NOACTIVATE = 3;
        private const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        private readonly PipeStream _pipe;
        private readonly StreamWriter _writer;
        private readonly ConcurrentQueue<string[]> _orders = new();
        private readonly WndProc _proc;
        private readonly Target _target;
        private IntPtr _window;
        private volatile int _effect = 1; // DROPEFFECT_COPY until BrimDeck says otherwise
        private bool _closed;

        public Relay(PipeStream pipe)
        {
            _pipe = pipe;
            _writer = new StreamWriter(pipe, Utf8, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            _proc = Proc;
            _target = new Target(this);
        }

        public int Run()
        {
            if (OleInitialize(IntPtr.Zero) < 0) return 1;
            var instance = GetModuleHandle(null);
            var type = new WNDCLASSEX { Size = Marshal.SizeOf<WNDCLASSEX>(), Proc = Marshal.GetFunctionPointerForDelegate(_proc), Instance = instance, ClassName = "BrimDeckDropRelay" };
            if (RegisterClassEx(ref type) == 0) return 1;
            _window = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST, "BrimDeckDropRelay", "BrimDeck", WS_POPUP,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_window == IntPtr.Zero) return 1;
            // Nearly clear: a fully clear layered window lets the pointer, and so the drag, through to what lies beneath.
            SetLayeredWindowAttributes(_window, 0, 1, 2); // LWA_ALPHA
            if (RegisterDragDrop(_window, _target) != 0) return 1;
            if (!Write(HelperLine.Make("window", _window.ToInt64().ToString()))) return 0;
            _ = ReadAsync();
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
            return 0;
        }

        private async Task ReadAsync()
        {
            using var reader = new StreamReader(_pipe, Utf8, false, 1024, leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync() is { } line) { _orders.Enqueue(HelperLine.Parse(line)); PostMessage(_window, WM_APP, IntPtr.Zero, IntPtr.Zero); }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            PostMessage(_window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        private IntPtr Proc(IntPtr window, int message, IntPtr w, IntPtr l)
        {
            switch (message)
            {
                case WM_APP:
                    while (_orders.TryDequeue(out var order)) Obey(order);
                    return IntPtr.Zero;
                case WM_MOUSEACTIVATE:
                    return new IntPtr(MA_NOACTIVATE);
                case WM_CLOSE:
                    if (!_closed) { _closed = true; RevokeDragDrop(window); DestroyWindow(window); }
                    return IntPtr.Zero;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            return DefWindowProc(window, message, w, l);
        }

        private void Obey(string[] order)
        {
            switch (order)
            {
                case ["show", var x, var y, var width, var height] when int.TryParse(x, out int left) && int.TryParse(y, out int top)
                    && int.TryParse(width, out int w) && int.TryParse(height, out int h) && w > 0 && h > 0:
                    SetWindowPos(_window, new IntPtr(-1), left, top, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW); // HWND_TOPMOST
                    break;
                case ["hide"]:
                    ShowWindow(_window, 0);
                    break;
                case ["effect", var value] when int.TryParse(value, out int effect):
                    _effect = effect;
                    break;
            }
        }

        private bool Write(string line)
        {
            try { _writer.WriteLine(line); return true; }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { PostMessage(_window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); return false; }
        }

        // The drag as OLE gives it to the window, passed on to BrimDeck. Only a drag with files is passed on and accepted.
        private sealed class Target(Relay relay) : IOleDropTarget
        {
            private bool _files;

            public int DragEnter(object data, int keys, long point, ref int effect)
            {
                var paths = Paths(data);
                _files = paths.Length > 0;
                if (!_files) { effect = 0; return 0; }
                effect = Allowed(effect, 1);
                relay.Write(Event("enter", keys, point, paths));
                return 0;
            }

            public int DragOver(int keys, long point, ref int effect)
            {
                if (!_files) { effect = 0; return 0; }
                effect = Allowed(effect, relay._effect);
                relay.Write(Event("over", keys, point, []));
                return 0;
            }

            public int DragLeave()
            {
                if (_files) relay.Write("leave");
                _files = false;
                return 0;
            }

            public int Drop(object data, int keys, long point, ref int effect)
            {
                var paths = Paths(data);
                ShowWindow(relay._window, 0);
                if (!_files || paths.Length == 0) { _files = false; effect = 0; return 0; }
                _files = false;
                effect = Allowed(effect, relay._effect);
                relay.Write(Event("drop", keys, point, paths));
                return 0;
            }

            private static int Allowed(int allowed, int wanted) => (allowed & wanted) != 0 ? wanted & allowed : 0;

            private static string Event(string word, int keys, long point, string[] paths)
            {
                var (x, y) = HelperLine.Split(point);
                return HelperLine.Make(word, [keys.ToString(), x.ToString(), y.ToString(), .. paths]);
            }

            private static string[] Paths(object data)
            {
                try { return DroppedFiles.Paths(new DataObject(data)).Where(path => path.AsSpan().IndexOfAny('\t', '\r', '\n') < 0).ToArray(); }
                catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException) { return []; }
            }
        }

        private delegate IntPtr WndProc(IntPtr window, int message, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int Size; public uint Style; public IntPtr Proc; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background;
            public string? MenuName; public string ClassName; public IntPtr SmallIcon;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr Window; public int Message; public IntPtr W, L; public uint Time; public int X, Y; }
        [DllImport("ole32")] private static extern int OleInitialize(IntPtr reserved);
        [DllImport("ole32")] private static extern int RegisterDragDrop(IntPtr window, IOleDropTarget target);
        [DllImport("ole32")] private static extern int RevokeDragDrop(IntPtr window);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")] private static extern ushort RegisterClassEx(ref WNDCLASSEX type);
        [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr window, int message, IntPtr w, IntPtr l);
        [DllImport("user32")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32")] private static extern void PostQuitMessage(int code);
        [DllImport("user32", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr window, int message, IntPtr w, IntPtr l);
        [DllImport("user32", EntryPoint = "GetMessageW")] private static extern int GetMessage(out MSG message, IntPtr window, uint first, uint last);
        [DllImport("user32")] private static extern bool TranslateMessage(ref MSG message);
        [DllImport("user32", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref MSG message);
        [DllImport("user32")] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);
        [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32")] private static extern bool ShowWindow(IntPtr window, int command);
    }

    // OLE's IDropTarget. The point is a POINTL passed by value: x in the low half, y in the high (HelperLine.Split).
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleDropTarget
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] object data, int keys, long point, ref int effect);
        [PreserveSig] int DragOver(int keys, long point, ref int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] object data, int keys, long point, ref int effect);
    }
}
