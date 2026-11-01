using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// Coordinates a single selection session. Presentation is supplied through events;
// all native work is serialized away from the UI and the keyboard callback.
internal sealed partial class InputMethodController : IDisposable
{
    private readonly Action<Action> _post;
    private readonly Func<InputMethodTarget, bool> _canPresent;
    private readonly SemaphoreSlim _native = new(1);
    private InputMethodSettings _settings = new();
    private IReadOnlyList<InstalledInputMethod> _installed = [];
    private InputMethodShortcut? _shortcut;
    private InputMethodTarget? _armed;
    private InputMethodTarget _target;
    private InputMethodCycle? _cycle;
    private CancellationTokenSource _round = new();
    private long _session = -1;
    private int _generation, _taps;
    private bool _released, _disposed, _preflighting, _refreshing;
    private int _recording;
    private bool _conflict;
    private string _signature = "";
    private DateTime _refreshAt;
    private InputMethodTarget? _retryTarget;
    private DateTime _retryAt;
    internal IReadOnlyList<InputMethodInfo> Installed => _installed.Where(p => p.Enabled).Select(p => new InputMethodInfo(p.Id, p.Name)).ToArray();
    internal string? Problem { get; private set; }
    internal string? TargetProblem { get; private set; }
    internal string Shortcut => _settings.Shortcut;
    internal event Action? CatalogChanged;
    internal event Action<InputMethodTarget>? Started;
    internal event Action<InputMethodTarget, InputMethodCycle>? Selecting;
    internal event Action? Released;
    internal event Action<string, bool>? Finished;
    internal event Action? ResetPresentation;

    internal InputMethodController(Action<Action> post, Func<InputMethodTarget, bool> canPresent)
    { _post = post; _canPresent = canPresent; }

    internal void Configure(InputMethodSettings settings, bool conflict = false)
    {
        if (_disposed) return;
        string signature = JsonSerializer.Serialize(settings) + ":" + conflict;
        if (_signature == signature) return;
        _signature = signature; _settings = JsonSerializer.Deserialize<InputMethodSettings>(JsonSerializer.Serialize(settings))!; _conflict = conflict;
        TargetProblem = null; _retryTarget = null;
        Cancel(); _shortcut?.Dispose(); _shortcut = null; _armed = null; Problem = null;
        _changes.Reset(); ResetPresentation?.Invoke();
        if (_settings.Enabled && !TargetInputMethod.Available)
            Problem = Loc.T("输入法组件缺失，请重新解压完整版本。", "The input-method component is missing. Extract the complete app again.");
        UpdateShortcut();
        _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (_disposed || _refreshing) return;
        _refreshing = true;
        try
        {
            var installed = await Native(() => { using var profiles = new InputMethodProfiles(); return profiles.Enumerate(); });
            if (_disposed) return;
            _installed = installed; _refreshAt = DateTime.UtcNow.AddSeconds(30);
            UpdateShortcut(); CatalogChanged?.Invoke();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or System.IO.IOException)
        { _refreshAt = DateTime.UtcNow.AddSeconds(5); Problem = Loc.T("暂时无法读取输入法，请刷新后重试。", "Input methods could not be read. Refresh to try again."); CatalogChanged?.Invoke(); }
        finally { _refreshing = false; }
    }

    private void UpdateShortcut()
    {
        bool valid = InputMethodGesture.TryParse(_settings.Shortcut, out var binding, out _);
        bool control = _settings.Enabled && _settings.Mode == InputMethodMode.Control;
        bool enabled = control && !_conflict && _recording == 0 && valid && TargetInputMethod.Available && _settings.Participants(Installed).Count > 0;
        if (control && _conflict) Problem = Loc.T("输入法快捷键与隐藏/显示或展开快捷键重复，请重新录制。", "The input-method shortcut conflicts with hide/show or expand. Record another shortcut.");
        if (control && !valid) Problem = Loc.T("请录制有效的输入法快捷键。", "Record a valid input-method shortcut.");
        if (!enabled) { if (_shortcut is not null) Suspend(); _shortcut?.Dispose(); _shortcut = null; _armed = null; return; }
        if (_shortcut is not null) return;
        InputMethodShortcut? service = null;
        service = new(binding, e => _post(() => { if (ReferenceEquals(_shortcut, service)) Handle(e); }),
            () => _post(() => { if (ReferenceEquals(_shortcut, service)) Tick(); }));
        _shortcut = service;
        if (!_shortcut.Running) Problem = Loc.T("快捷键接管未启动，请关闭并重新启用模块。", "The shortcut could not start. Turn the module off and on to retry.");
        Tick();
    }

    internal InputMethodShortcut RecordShortcut(Action<InputMethodRecordingDecision> changed)
    {
        _recording++; Suspend(); _shortcut?.Dispose(); _shortcut = null;
        return new(decision => _post(() => changed(decision)));
    }

    internal void EndRecording()
    {
        if (_recording > 0) _recording--;
        if (_recording == 0 && !_disposed) { UpdateShortcut(); Tick(); }
    }

    // Called by the app's timer. A new foreground window is checked once, before interception.
    internal async void Tick()
    {
        if (_disposed || _recording > 0 || !_settings.Enabled || !TargetInputMethod.Available) return;
        if (DateTime.UtcNow >= _refreshAt) _ = RefreshAsync();
        if (_settings.Mode == InputMethodMode.Notify) { await ObserveInputMethodAsync(); return; }
        if (_shortcut?.Running != true) return;
        var target = TargetInputMethod.Foreground();
        if (!_canPresent(target)) { _shortcut.Arm(null); _armed = null; if (!_released) Cancel(); return; }
        if (_armed == target) return;
        _shortcut.Arm(null); _armed = null;
        if (_retryTarget == target && DateTime.UtcNow < _retryAt) return;
        if (_preflighting) return;
        _preflighting = true;
        var shortcut = _shortcut; int generation = _generation;
        try
        {
            var read = await Native(() => TargetInputMethod.Read(target));
            if (_disposed || generation != _generation || shortcut != _shortcut) return;
            if (_recording == 0 && read.ReachedTarget && TargetInputMethod.IsCurrent(target) && _canPresent(target))
            { _armed = target; _shortcut?.Arm(target); TargetProblem = null; }
            else
            {
                _retryTarget = target; _retryAt = DateTime.UtcNow.AddSeconds(5);
                TargetProblem = Loc.T("上一个窗口无法接管（可能为 32 位、管理员或受保护窗口），已交由系统处理。",
                    "The previous window could not be controlled (32-bit, elevated or protected). Windows handles its shortcut.");
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        finally { _preflighting = false; }
    }

    private void Handle(InputMethodKeyEvent e)
    {
        if (_disposed || !_settings.Enabled || _settings.Mode != InputMethodMode.Control) return;
        if (e.Decision.Next)
        {
            if (_session != e.Session)
            {
                Cancel(); _session = e.Session; _target = e.Target; _taps = 1; _released = false;
                Started?.Invoke(_target);
                _ = BeginAsync(_generation, _round.Token);
            }
            else
            {
                _taps++;
                if (_cycle is not null) { _cycle.Next(); Selecting?.Invoke(_target, _cycle); }
            }
        }
        if (e.Session != _session) return;
        if (e.Decision.Cancel) { Cancel(); return; }
        if (e.Decision.Commit && !_released)
        {
            _released = true; Released?.Invoke();
            if (_cycle is not null) _ = ActivateAsync(_generation, _round.Token);
        }
    }

    private async Task BeginAsync(int generation, CancellationToken cancellation)
    {
        try
        {
            var target = _target;
            var read = await Native(() => TargetInputMethod.Read(target), cancellation);
            if (!Current(generation, target)) return;
            if (!read.ReachedTarget) { Fail(); return; }
            _cycle = new(_settings.Participants(Installed), read.ActiveId);
            for (int i = 0; i < _taps; i++) _cycle.Next();
            if (_cycle.Selected is null) { Fail(); return; }
            if (_released) _ = ActivateAsync(generation, cancellation);
            else Selecting?.Invoke(target, _cycle);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { if (generation == _generation) Fail(); }
    }

    private async Task ActivateAsync(int generation, CancellationToken cancellation)
    {
        var chosen = _cycle?.Commit();
        if (chosen is null) return;
        var profile = _installed.FirstOrDefault(p => string.Equals(p.Id, chosen.Id, StringComparison.OrdinalIgnoreCase));
        if (profile is null) { Fail(); return; }
        var target = _target;
        try
        {
            var result = await Native(() => TargetInputMethod.Activate(target, profile), cancellation);
            for (int i = 0; i < 8 && !result.Confirmed && result.Error >= 0 && Current(generation, target); i++)
            {
                await Task.Delay(75, cancellation);
                result = await Native(() => TargetInputMethod.Read(target), cancellation);
                result = result with { Confirmed = result.Confirmed && string.Equals(result.ActiveId, chosen.Id, StringComparison.OrdinalIgnoreCase) };
            }
            if (!Current(generation, target)) return;
            Finished?.Invoke(result.Confirmed ? chosen.Name : Loc.T("切换未完成", "Could not switch"), result.Confirmed);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { if (generation == _generation) Fail(); }
    }

    private bool Current(int generation, InputMethodTarget target)
        => !_disposed && _settings.Mode == InputMethodMode.Control && generation == _generation && TargetInputMethod.IsCurrent(target) && _canPresent(target);

    private void Fail()
    {
        _released = true; Released?.Invoke(); Finished?.Invoke(Loc.T("切换未完成", "Could not switch"), false);
    }

    internal void Cancel()
    {
        _generation++; _round.Cancel(); _round.Dispose(); _round = new();
        _cycle?.Cancel(); _cycle = null; _session = -1; _released = true; Released?.Invoke();
    }

    internal void Suspend()
    { _shortcut?.Arm(null); _armed = null; _changes.Reset(); Cancel(); }

    private async Task<T> Native<T>(Func<T> action, CancellationToken cancellation = default)
    {
        await _native.WaitAsync(cancellation);
        try { cancellation.ThrowIfCancellationRequested(); return await Task.Run(action, cancellation); }
        finally { _native.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Cancel(); _shortcut?.Dispose(); _shortcut = null;
    }
}
