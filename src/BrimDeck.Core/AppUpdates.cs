namespace BrimDeck.Core;

public enum AppUpdateStage { Idle, Checking, Available, Downloading, Ready, Installing }
public sealed record AppRelease(string Version, string Notes, long Size);

public interface IAppUpdateBackend
{
    string CurrentVersion { get; }
    bool CanInstall { get; }
    AppRelease? PendingRelease { get; }
    Task<AppRelease?> CheckAsync(CancellationToken cancellation);
    Task DownloadAsync(AppRelease release, Action<int> progress, CancellationToken cancellation);
    void PrepareRestart();
}

// All commands originate on the UI thread. The backend may report progress from a worker thread.
public sealed class AppUpdates
{
    private readonly IAppUpdateBackend _backend;
    private readonly string _promptFile;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private string? _promptedVersion;
    private Task? _check;
    private CancellationTokenSource? _download;
    public event Action? Changed;
    public AppUpdateStage Stage { get; private set; }
    public AppRelease? Release { get; private set; }
    public string CurrentVersion => _backend.CurrentVersion;
    public bool CanInstall => _backend.CanInstall;
    public bool HasUpdate => Release is not null;
    public bool IsBusy => Stage is AppUpdateStage.Checking or AppUpdateStage.Downloading or AppUpdateStage.Installing;
    public int Progress { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }

    public AppUpdates(IAppUpdateBackend backend, string dataDirectory, Func<DateTimeOffset>? clock = null)
    {
        _backend = backend; _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _promptFile = Path.Combine(dataDirectory, "update-prompt.txt");
        try { if (File.Exists(_promptFile)) _promptedVersion = File.ReadAllText(_promptFile).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Release = backend.PendingRelease;
        Stage = Release is null ? AppUpdateStage.Idle : AppUpdateStage.Ready;
    }

    public Task CheckAsync(bool manual, CancellationToken cancellation = default)
    {
        if (_check is { IsCompleted: false }) return _check;
        if (Stage is AppUpdateStage.Downloading or AppUpdateStage.Ready or AppUpdateStage.Installing) return Task.CompletedTask;
        if (!manual && LastChecked is { } last && _clock() - last < TimeSpan.FromMinutes(30)) return Task.CompletedTask;
        return _check = CheckCoreAsync(cancellation);
    }

    private async Task CheckCoreAsync(CancellationToken cancellation)
    {
        Stage = AppUpdateStage.Checking; Error = null; Notify();
        try
        {
            var release = await _backend.CheckAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            Release = release; LastChecked = _clock();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { Error = Loc.T("暂时无法检查更新，请检查网络后重试。", "Updates cannot be checked right now. Check the network and try again."); }
        finally { Stage = HasUpdate ? AppUpdateStage.Available : AppUpdateStage.Idle; Notify(); }
    }

    public bool ShouldPrompt(bool settingsVisible, bool settingsActive) => settingsVisible && settingsActive &&
        Stage is AppUpdateStage.Available or AppUpdateStage.Ready && Release?.Version != _promptedVersion;

    // Persist when a dialog is actually shown, so cancel/close and subsequent app launches do not repeat it.
    public void MarkPromptShown()
    {
        if (Release is null) return;
        _promptedVersion = Release.Version;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_promptFile)!);
            File.WriteAllText(_promptFile + ".tmp", _promptedVersion);
            File.Move(_promptFile + ".tmp", _promptFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async Task DownloadAsync(CancellationToken cancellation = default)
    {
        if (IsBusy || Release is null || !CanInstall || Stage == AppUpdateStage.Ready) return;
        var release = Release;
        using var download = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _download = download; Stage = AppUpdateStage.Downloading; Progress = 0; Error = null; Notify();
        try
        {
            await _backend.DownloadAsync(release, value =>
            {
                void Report() { if (Stage != AppUpdateStage.Downloading || _download != download) return; Progress = Math.Clamp(value, 0, 100); Notify(); }
                if (_context is null) Report(); else _context.Post(_ => Report(), null);
            }, download.Token);
            download.Token.ThrowIfCancellationRequested();
            Progress = 100; Stage = AppUpdateStage.Ready;
        }
        catch (OperationCanceledException) when (download.IsCancellationRequested) { Stage = AppUpdateStage.Available; }
        catch (Exception) { Stage = AppUpdateStage.Available; Error = Loc.T("更新包下载或校验失败，请重试。", "The update package could not be downloaded or verified. Try again."); }
        finally { _download = null; Notify(); }
    }

    public void CancelDownload() => _download?.Cancel();

    // The app saves its settings first, then launches the updater, then exits through its normal shutdown path.
    public bool PrepareRestart(Action saveSettings)
    {
        if (Stage != AppUpdateStage.Ready || !CanInstall) return false;
        Stage = AppUpdateStage.Installing; Error = null; Notify();
        try { saveSettings(); _backend.PrepareRestart(); return true; }
        catch (Exception) { Stage = AppUpdateStage.Ready; Error = Loc.T("暂时无法安装更新，请重试。", "The update cannot be installed right now. Try again."); Notify(); return false; }
    }

    private void Notify() => Changed?.Invoke();
}
