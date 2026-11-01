using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;
using DropShelf;

namespace BrimDeck;

public partial class MainWindow
{
    internal bool ClipboardHasDraft => _clipboardView?.HasUnsavedDraft == true;
    internal bool ReducedModule => ModuleRuntimePolicy.ForPage(Settings, CurrentPage) == ModuleMode.Performance;
    private bool _shelfStopping;
    internal void PrepareClipboardStop()
    { _clipboardView?.ClosePopups(); if (IsClipboardPage) EndClipboardInput(); }
    internal void ReleaseClipboardView()
    {
        PrepareClipboardStop(); _clipboardView?.Dispose(); _clipboardView = null;
        ClipboardContent.Children.Clear();
    }
    internal void PrepareShelfStop()
    {
        _shelfStopping = true; _trayDropHover.Stop(); CloseTrayPopups();
        if (IsTrayPage) EndClipboardInput();
        if (_trayDragging) EndTrayReorder(false);
    }
    internal void ResumeShelfInput()
    {
        _shelfStopping = false;
        if (!_app.ShelfRunning || Settings.Tray.Placement == FileTrayPlacement.Floating)
        { _fileShareDrop?.Dispose(); _fileShareDrop = null; return; }
        if (_fileShareDrop is not null) return;
        _fileShareDrop = new ShelfIslandDropTarget(this, Island, () => _app.Shelves!.Shelves.ActiveWindow,
            () => _app.ShelfRunning && !_shelfStopping && !_app.ModulesChanging && !IsSuppressed && !_preview && Settings.Tray.Placement != FileTrayPlacement.Floating,
            () => _trayExporting, FileShareHighlight, FileShareProblem, _app.Shelves!.Shelves.Operations);
        _fileShareDrop.ActivityChanged += ShelfActivityChanged;
    }
    private void ShelfActivityChanged() => _app.Shelves?.Shelves.NotifyActivityChanged();
    internal void ReleaseShelfView()
    {
        _fileShareDrop?.Dispose(); _fileShareDrop = null; _trayDropHover.Stop();
        ReleaseTrayViewport(); CloseTrayPopups(); HideTrayNotice();
        TrayContent.Children.Clear(); _trayCards.Clear(); _trayResults.Clear(); _trayTypes.Clear();
        _trayFrame = null; _trayGrid = null; _trayScroll = null; _trayBar = null; _trayGhostLayer = null;
        _trayTypesHost = null; _trayRight = null; _trayCenter = null; _traySearchField = null;
        _traySearchCapsule = null; _traySearch = null; _trayPickerText = null; _traySearchPlaceholder = null; _trayEmpty = null;
        _trayPicker = _traySearchToggle = _traySearchClear = _trayPinned = _trayLayout = _trayActions = _trayMore = _traySettings = null;
        _trayPressed = _trayDragItem = null; _trayPressCard = _trayDragCard = null; _trayGhost = null;
        _trayKey = ""; _trayModelId = ""; _trayArrived = null;
    }
    private ModuleMode? _presentedClipboard, _presentedShelf;
    private bool? _presentedReduced;
    internal void ApplyModulePresentation()
    {
        bool changed = _presentedReduced != ReducedModule || _presentedClipboard != Settings.Clipboard.Mode || _presentedShelf != Settings.Tray.Mode;
        _presentedReduced = ReducedModule; _presentedClipboard = Settings.Clipboard.Mode; _presentedShelf = Settings.Tray.Mode;
        Soft.SetReduced(ClipboardContent, Settings.Clipboard.Mode == ModuleMode.Performance);
        Soft.SetReduced(TrayContent, Settings.Tray.Mode == ModuleMode.Performance);
        Soft.SetReduced(Island, ReducedModule);
        _clipboardView?.ApplyPerformanceMode();
        if (ReducedModule && changed)
        {
            StopSpring(); Soft.StopTree(Island); EndThemeFade();
            _handover?.Drop(); _hover.Stop(); _pointerTrack.Stop();
        }
        Soft.SetReduced(ShadowHost, ReducedModule);
        ShadowHost.Visibility = ReducedModule ? Visibility.Collapsed : Visibility.Visible;
        if (ReducedModule) { _shadowSettle?.Stop(); ShadowHost.CacheMode = null; }
        UpdatePointerTracking();
        _host.EventDriven = ReducedModule;
        if (changed && _app.ShelfRunning)
            foreach (var shelf in _app.Shelves!.Shelves.OpenShelves) shelf.ApplySettings(_app.Shelves.Settings);
        ResumeShelfInput();
    }
}
