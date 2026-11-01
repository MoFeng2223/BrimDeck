using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

internal sealed partial class InputMethodController
{
    private readonly InputMethodChanges _changes = new();
    private bool _observing;
    internal event Action<InputMethodTarget, InputMethodInfo>? Notified;

    // Reuses the verified target-thread read and the app's 250ms watch timer. This mode
    // never creates a keyboard shortcut service or requests profile activation.
    private async Task ObserveInputMethodAsync()
    {
        if (_observing) return;
        var target = TargetInputMethod.Foreground();
        if (!_canPresent(target)) { _changes.DropPending(); return; }
        if (_retryTarget == target && DateTime.UtcNow < _retryAt) return;
        _observing = true;
        int generation = _generation;
        try
        {
            var read = await Native(() => TargetInputMethod.Read(target));
            if (_disposed || generation != _generation || _settings.Mode != InputMethodMode.Notify
                || !TargetInputMethod.IsCurrent(target) || !_canPresent(target)) return;
            if (!read.Confirmed || read.ActiveId is null)
            {
                RetryNotificationTarget(target); return;
            }
            TargetProblem = null;
            string? changed = _changes.Observe(read.ActiveId, Environment.TickCount64, TargetInputMethod.SwitchModifiersHeld());
            if (changed is null) return;
            var profile = _installed.FirstOrDefault(p => string.Equals(p.Id, changed, StringComparison.OrdinalIgnoreCase));
            if (profile is null) _refreshAt = DateTime.MinValue;
            Notified?.Invoke(target, new(changed, profile?.Name ?? Loc.T("输入法已切换", "Input method changed")));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            if (!_disposed && generation == _generation) RetryNotificationTarget(target);
        }
        finally { _observing = false; }
    }

    private void RetryNotificationTarget(InputMethodTarget target)
    {
        _changes.DropPending(); _retryTarget = target; _retryAt = DateTime.UtcNow.AddSeconds(5);
        TargetProblem = Loc.T("上一个窗口暂时无法读取输入法，已跳过通知。", "The previous window's input method could not be read. Its notification was skipped.");
    }
}
