using System.Windows;
using System.Windows.Interop;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// Files dragged in while BrimDeck runs as administrator arrive through the drop helper (DropHelper): while a button is held over the
// part of this panel that takes drops, and the press did not begin on this panel, the helper's window is laid over that part; the
// drag that reaches it is raised here as if it had reached this window (DropRelay.Forward), so the panel opens, lights up and takes
// the files as it does for any drag.
public partial class MainWindow
{
    // A press that began on this window: what it drags is the panel's own (a file dragged out, a row moved), never files coming in.
    private bool _ownPress;
    private DropRelay.Forward? _forward;
    private bool _forwardTried;
    // The files of the drag the helper is passing on, from its enter to its leave or drop.
    private DataObject? _helperDrag;

    private void InitializeDropHelper() => PreviewMouseDown += (_, _) => _ownPress = true;

    // Whether this panel takes files at all: the clipboard page is shown and the file transfer is part of it.
    private bool TakesDrops => DeckPages.Shown(Settings, DeckPage.Clipboard) && Settings.ShelfShown != ShelfShown.Clipboard;

    // From TrackPointer: lays the helper's window over the part that takes drops while files may be on their way, and takes it away
    // once they cannot be.
    private void TrackDropHelper(Point cursor, bool tracking = true)
    {
        if (_app.DropHelper is not { Ready: true } helper) return;
        bool held = PasteInput.KeyDown(0x01) || PasteInput.KeyDown(0x02);
        if (!held) _ownPress = false;
        var zone = tracking ? ScreenBounds(HoverZone) : Rect.Empty;
        bool wanted = tracking && held && !_ownPress && !_draggingOut && !_moveArmed && !ClickThrough && TakesDrops && !zone.IsEmpty && zone.Contains(cursor);
        if (wanted) helper.Show(this, zone);
        else if (ReferenceEquals(helper.Owner, this)) helper.Hide();
    }

    // A drag event the helper passed on: enter, over, leave or drop, with the key state, the point on screen and, on entering and
    // dropping, the paths.
    internal void HelperDrag(string[] message)
    {
        if (_app.DropHelper is not { } helper) return;
        if (!_forwardTried)
        {
            _forwardTried = true;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) _forward = DropRelay.Forward.For(handle);
        }
        if (_forward is not { } forward) return;
        const int copy = (int)DragDropEffects.Copy;
        switch (message)
        {
            case ["enter", var keys, var x, var y, .. var paths] when Read(keys, x, y, out int state, out long point) && paths.Length > 0:
                _helperDrag = new DataObject(DataFormats.FileDrop, paths);
                helper.Effect(forward.Enter(_helperDrag, state, point, copy));
                break;
            case ["over", var keys, var x, var y] when _helperDrag is not null && Read(keys, x, y, out int state, out long point):
                helper.Effect(forward.Over(state, point, copy));
                break;
            case ["leave"] when _helperDrag is not null:
                _helperDrag = null;
                forward.Leave();
                break;
            case ["drop", var keys, var x, var y, .. var paths] when Read(keys, x, y, out int state, out long point) && paths.Length > 0:
                _helperDrag = null;
                forward.Drop(new DataObject(DataFormats.FileDrop, paths), state, point, copy);
                break;
        }
    }

    private static bool Read(string keys, string x, string y, out int state, out long point)
    {
        point = 0;
        if (!int.TryParse(keys, out state) || !int.TryParse(x, out int left) || !int.TryParse(y, out int top)) return false;
        point = HelperLine.Point(left, top);
        return true;
    }
}
