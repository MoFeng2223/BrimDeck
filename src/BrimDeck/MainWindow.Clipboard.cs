using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

public partial class MainWindow
{
    private ClipboardView? _clipboardView;
    private bool _clipboardInput;
    // Text entry and its popups hold the island open; browsing keeps the usual pointer-leave behavior.
    private bool ClipboardHoldsOpen => IsClipboardPage && (_clipboardInput || _clipboardView?.PopupOpen == true);
    private bool _openingClipboard;
    private string _clipboardLanguage = "";

    private void RenderClipboard()
    {
        if (!_expanded) return;
        RefreshStatus();
        PrepareClipboardDock();
        if (_app.ClipboardHistoryService is { } service && _app.ClipboardRunning)
        {
            if (_clipboardView is null) service.OpenedOrResumed();
            RenderClipboardPage(service.History, service.TogglePause, _app.EnableClipboard, () => (Settings.Clipboard.Enabled, service.Paused, service.Status));
        }
    }

    private void PrepareClipboardDock()
    {
        UsageContent.Visibility = MusicContent.Visibility = MonitorContent.Visibility = TrayContent.Visibility = Visibility.Collapsed;
        ClipboardContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = ApplicationPages.Visibility = SyncButton.Visibility = Visibility.Collapsed;
        // The shared page header is separate from the translated clipboard controls and reveals above them.
        PrepareClipboardHeader(instant: !_expanded);
        ClipboardContent.Margin = new Thickness(0);
    }

    private void RenderClipboardPage(ClipboardHistory history, Action togglePause, Action enable, Func<(bool Enabled, bool Paused, string Problem)> status)
    {
        if (_clipboardView is null || !_clipboardView.HasUnsavedDraft && _clipboardLanguage != Loc.Language)
        {
            bool focused = _clipboardInput;
            var query = _clipboardView?.QueryState ?? new ClipboardQuery { Sort = Settings.Clipboard.SortOrder };
            bool applicationsMode = _clipboardView?.ApplicationsMode == true;
            var presentation = _clipboardView?.CapturePresentation();
            _clipboardView?.Dispose(); ClipboardContent.Children.Clear();
            _clipboardView = new ClipboardView(history, () => Settings.Clipboard,
                (entry, copyOnly) => _app.ActivateClipboardEntry(this, entry, copyOnly), _app.OpenClipboardSettings,
                togglePause, enable, status,
                grid => _app.EditClipboardSettings(settings => settings.GridView = grid), () => SetExpanded(false, keyboard: true),
                entry => { SetExpanded(false, keyboard: true); _app.OpenClipboardLibraryFor(entry?.Id); },
                setSort: order => _app.EditClipboardSettings(settings => settings.SortOrder = order),
                setApplicationPinned: _app.SetClipboardApplicationPinned,
                openPreviewWindow: _app.OpenClipboardPreview, setApplicationHidden: _app.SetClipboardApplicationHidden);
            _clipboardView.PageRequested += page => SelectPage(page);
            _clipboardView.AvailablePages = () => DeckPages.Visible(Settings);
            _clipboardView.InputRequested += BeginClipboardInput;
            _clipboardView.InteractionChanged += ClipboardInteractionChanged;
            ClipboardContent.Children.Add(_clipboardView);
            _clipboardLanguage = Loc.Language;
            _clipboardView.ApplicationsMode = applicationsMode;
            _clipboardView.QueryState = query;
            if (presentation is not null) _clipboardView.RestorePresentation(presentation);
            if (focused) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (IsClipboardPage && _expanded) _clipboardView?.RestoreHeaderFocus(); });
        }
        _clipboardView.ApplyLayout();
    }

    internal void ClipboardChanged() { if (IsClipboardPage && _expanded) _clipboardView?.Refresh(); }
    internal void ClipboardPreviewSensitivityChanged() => _clipboardView?.RefreshPreviewSensitivity();

    internal void OpenClipboardPage()
    {
        if (!_app.ClipboardRunning) { _app.OpenClipboardSettings(); return; }
        if (_preview) { _app.EndPreviewFromDeck(); if (_preview) return; }
        _openingClipboard = true;
        try
        {
            SelectPage(DeckPage.Clipboard);
            OpenFromHotkey();
            if (!_expanded || !IsClipboardPage) return;
            BeginClipboardInput();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (_expanded && IsClipboardPage) _clipboardView?.FocusHeader(reset: true); });
        }
        finally { _openingClipboard = false; }
    }

    private void BeginClipboardInput()
    {
        if (_preview || !_expanded || !IsDockPage) return;
        if (!_clipboardInput) _app.CaptureClipboardTarget(preserveInternal: true);
        _clipboardInput = true; _hover.Stop(); _leave.Stop();
        // The non-activating hover overlay borrows focus only for an explicit search or hotkey recall, never on hover.
        WindowsHost.SetOverlayInput(this, true);
        _escapeDismissal?.SetEnabled(false);
        Activate();
    }

    private void EndClipboardInput()
    {
        bool hadInput = _clipboardInput;
        _clipboardInput = false;
        _clipboardView?.ClosePopups(); CloseTrayPopups();
        if (PresentationSource.FromVisual(this) is not null) WindowsHost.SetOverlayInput(this, false);
        if (hadInput && IsKeyboardFocusWithin) Keyboard.ClearFocus();
        if (hadInput && IsActive) _app.RestoreClipboardFocus();
        _escapeDismissal?.SetEnabled(_expanded && !_preview);
    }

    private void ClipboardInteractionChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (!_expanded || !IsDockPage || _clipboardView?.PopupOpen == true || _trayMenu?.IsOpen == true) return;
        if (_clipboardInput && !IsActive)
        {
            EndClipboardInput();
            if (IsTrayPage || Settings.Clipboard.HideOnDeactivate) SetExpanded(false, keyboard: true);
        }
        else if (!_clipboardInput) PointerChanged(_pointerInside);
    });

    private void DismissPanelFromKeyboard()
    {
        if (_moveArmed) { CancelMove(); return; }
        if (_trayDragging) { EndTrayReorder(false); _trayPressed = null; _trayPressCard = null; }
        else if (_expanded && IsTrayPage && HandleTrayShortcut(Key.Escape, ModifierKeys.None, null)) { }
        else if (_expanded && IsClipboardPage && _clipboardView is { } view) view.DismissFromKeyboard();
        else SetExpanded(false, keyboard: true);
    }

    private void PanelKeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
        if (_expanded && IsClipboardPage && _clipboardView?.HandleShortcut(HotkeyGesture.KeyOf(e), Keyboard.Modifiers, e.OriginalSource as DependencyObject) == true)
        { e.Handled = true; return; }
        if (_expanded && IsTrayPage && HandleTrayShortcut(HotkeyGesture.KeyOf(e), Keyboard.Modifiers, e.OriginalSource as DependencyObject)) { e.Handled = true; return; }
        if (e.Key == Key.Escape) { DismissPanelFromKeyboard(); e.Handled = true; }
    }
}
