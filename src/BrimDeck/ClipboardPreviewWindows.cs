using System.Windows;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// App-lifetime presentation registry, not a clipboard store. Every window references the same immutable history entry.
internal sealed class ClipboardPreviewWindows : IDisposable
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, ClipboardPreviewWindows> Owners = new();
    internal static ClipboardPreviewWindows ForOwner(Window owner, ClipboardHistory history, Func<ClipboardSettings> settings, Action<ClipboardEntry, bool> activate)
        => Owners.GetValue(owner, window =>
        {
            var previews = new ClipboardPreviewWindows(history, settings, (item, copyOnly, _) => { activate(item, copyOnly); return true; });
            window.Closed += (_, _) => { previews.Dispose(); Owners.Remove(window); };
            return previews;
        });
    private readonly ClipboardHistory _history;
    private readonly Func<ClipboardSettings> _settings;
    private readonly Func<ClipboardEntry, bool, ClipboardPasteTarget, bool> _activate;
    private readonly Func<ClipboardPasteTarget> _target;
    private readonly Dictionary<string, ClipboardPreviewWindow> _windows = new(StringComparer.Ordinal);
    private bool _disposed;
    internal IReadOnlyCollection<ClipboardPreviewWindow> Windows => _windows.Values;

    internal ClipboardPreviewWindows(ClipboardHistory history, Func<ClipboardSettings> settings, Func<ClipboardEntry, bool, ClipboardPasteTarget, bool> activate, Func<ClipboardPasteTarget>? target = null)
    {
        _history = history; _settings = settings; _activate = activate; _target = target ?? (() => default);
        _history.Changed += Refresh;
    }
    internal void Show(ClipboardEntry entry, Rect card, Rect panel, double scale)
    {
        if (_disposed) return;
        if (_windows.TryGetValue(entry.Id, out var existing) && !existing.IsClosing)
        { existing.UpdateEntry(entry); existing.BringForward(); return; }
        ClipboardPreviewWindow window = null!;
        window = new ClipboardPreviewWindow(_history, entry, _settings, (item, copyOnly) => _activate(item, copyOnly, window.PasteTarget)) { PasteTarget = _target() };
        _windows[entry.Id] = window;
        window.Closed += (_, _) => { if (_windows.TryGetValue(entry.Id, out var current) && ReferenceEquals(current, window)) _windows.Remove(entry.Id); };
        // A real switch to another app updates this window's receiver, not another preview's saved paste target.
        window.Deactivated += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_disposed && !window.IsClosing && ClipboardPaste.IsExternalWindow(ClipboardPaste.Foreground)) window.PasteTarget = ClipboardPaste.Capture(ClipboardPaste.Foreground);
        });
        window.ShowNear(card, panel, scale);
    }
    internal void Refresh()
    {
        if (_disposed) return;
        foreach (var (id, window) in _windows.ToArray())
        {
            if (_history.Items.FirstOrDefault(entry => entry.Id == id) is { } item) window.UpdateEntry(item);
            // Deleted/expired content must not survive in a detached UI after it has left the history owner.
            else window.CloseImmediately();
        }
    }
    internal void RefreshText() { foreach (var window in _windows.Values.ToArray()) if (!window.IsClosing) window.RefreshText(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _history.Changed -= Refresh;
        foreach (var window in _windows.Values.ToArray()) window.CloseImmediately();
        _windows.Clear();
    }
}
