using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace BrimDeck.Native;

// Files dragged over a window that only shows another window of this app (a lens of TaskbarMirror) go to that other window.
// OLE gives a drag to the window under the pointer, found without asking the window (a window answering HTTRANSPARENT is passed
// over only for the mouse), and a window that takes no drops ends the drag there. The lens is registered as a drop target with
// WPF's own drop target for the window it shows: that target takes the pointer's place on screen, finds the element of its
// window there, and raises the drag events on it as if the files were over that window itself. The lens lies over the window it
// shows at the same place, so the element found is the one the user sees.
//
// WPF's drop target is internal to it and is made by name; where it is not found, the lens takes no drops, as before.
internal static class DropRelay
{
    private static readonly ConstructorInfo? Constructor = typeof(DragDrop).Assembly.GetType("System.Windows.OleDropTarget")
        ?.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, [typeof(IntPtr)]);
    private static readonly Guid DropTargetId = new("00000122-0000-0000-C000-000000000046"); // IID_IDropTarget

    // WPF's drop target for the window; null where it is not found.
    internal static object? Make(IntPtr target) => Constructor?.Invoke([target]);

    // Makes drops on window go to target; false where WPF's drop target could not be made or registered. Called on the UI thread,
    // where WPF has set up OLE.
    public static bool Register(IntPtr window, IntPtr target)
    {
        if (window == IntPtr.Zero || target == IntPtr.Zero) return false;
        IntPtr unknown = IntPtr.Zero, drop = IntPtr.Zero;
        try
        {
            if (Make(target) is not { } made) return false;
            unknown = Marshal.GetIUnknownForObject(made);
            if (Marshal.QueryInterface(unknown, in DropTargetId, out drop) != 0) return false;
            // OLE keeps a reference of its own until the registration is revoked.
            return RegisterDragDrop(window, drop) == 0;
        }
        catch (Exception error) when (error is TargetInvocationException or MemberAccessException or NotSupportedException or InvalidCastException or COMException)
        {
            return false;
        }
        finally
        {
            if (drop != IntPtr.Zero) Marshal.Release(drop);
            if (unknown != IntPtr.Zero) Marshal.Release(unknown);
        }
    }

    // Before the window is destroyed or shows another window.
    public static void Revoke(IntPtr window)
    {
        if (window != IntPtr.Zero) RevokeDragDrop(window);
    }

    // A drag that reached another process's window (DropHelper) raised on this window as OLE would raise it here: WPF's drop target
    // for the window is called directly with the point on screen and the data, and returns what the element there would do.
    internal sealed class Forward
    {
        private readonly object _target;
        private readonly System.Reflection.MethodInfo _enter, _over, _leave, _drop;

        private Forward(object target, Type calls)
        {
            _target = target;
            _enter = calls.GetMethod("OleDragEnter")!; _over = calls.GetMethod("OleDragOver")!;
            _leave = calls.GetMethod("OleDragLeave")!; _drop = calls.GetMethod("OleDrop")!;
        }

        // Null where WPF's drop target or one of its calls is not found.
        public static Forward? For(IntPtr window)
        {
            if (Make(window) is not { } made || made.GetType().GetInterfaces().FirstOrDefault(type => type.GUID == DropTargetId) is not { } calls) return null;
            return calls.GetMethod("OleDragEnter") is null || calls.GetMethod("OleDragOver") is null || calls.GetMethod("OleDragLeave") is null
                || calls.GetMethod("OleDrop") is null ? null : new Forward(made, calls);
        }

        public int Enter(IDataObject data, int keys, long point, int allowed) => Call(_enter, [data, keys, point, allowed], 3);
        public int Over(int keys, long point, int allowed) => Call(_over, [keys, point, allowed], 2);
        public void Leave() => Call(_leave, [], -1);
        public int Drop(IDataObject data, int keys, long point, int allowed) => Call(_drop, [data, keys, point, allowed], 3);

        private int Call(System.Reflection.MethodInfo method, object[] arguments, int effect)
        {
            try
            {
                method.Invoke(_target, arguments);
                return effect >= 0 && arguments[effect] is int answer ? answer : 0;
            }
            catch (Exception error) when (error is TargetInvocationException or MemberAccessException or ArgumentException) { return 0; }
        }
    }

    [DllImport("ole32")] private static extern int RegisterDragDrop(IntPtr window, IntPtr target);
    [DllImport("ole32")] private static extern int RevokeDragDrop(IntPtr window);
}
