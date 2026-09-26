using BrimDeck.Core;

internal static class AppUpdateTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var time = DateTimeOffset.UtcNow;
        var backend = new UpdateFixture();
        var folder = Path.Combine(root, "updates");
        var service = new AppUpdates(backend, folder, () => time);
        check("updater starts without prompting or making network requests", backend.Checks == 0 && !service.ShouldPrompt(true, true));
        var pendingCheck = new TaskCompletionSource<AppRelease?>(); backend.CheckResult = pendingCheck.Task;
        var first = service.CheckAsync(false); var second = service.CheckAsync(true);
        check("concurrent version checks share one request", ReferenceEquals(first, second) && backend.Checks == 1);
        pendingCheck.SetResult(backend.Release); await first;
        check("update detection never downloads automatically", service.HasUpdate && backend.Downloads == 0 && service.Stage == AppUpdateStage.Available);
        check("closed settings do not prompt", !service.ShouldPrompt(false, true));
        check("inactive settings do not prompt", !service.ShouldPrompt(true, false));
        check("active settings can prompt", service.ShouldPrompt(true, true));
        service.MarkPromptShown();
        check("canceling dialog retains update without another prompt", service.HasUpdate && !service.ShouldPrompt(true, true));
        await service.CheckAsync(false);
        check("automatic checks are throttled", backend.Checks == 1);
        backend.CheckResult = null;
        var reopened = new AppUpdates(backend, folder); await reopened.CheckAsync(true);
        check("dismissed version is remembered after restart", reopened.HasUpdate && !reopened.ShouldPrompt(true, true));
        backend.Release = new("0.3.0", "New release", 200); await reopened.CheckAsync(true);
        check("a newer version can prompt again", reopened.ShouldPrompt(true, true));
        backend.FailCheck = true; await reopened.CheckAsync(true);
        check("network failure preserves the known update", reopened.HasUpdate && reopened.Error is not null);
        backend.FailCheck = false;
        var downloadGate = new TaskCompletionSource(); backend.DownloadGate = downloadGate.Task;
        var downloading = reopened.DownloadAsync(); await reopened.DownloadAsync();
        backend.Report?.Invoke(47);
        check("download progress is reported and duplicate downloads are blocked", reopened.Progress == 47 && backend.Downloads == 1);
        check("cannot install an incomplete download", !reopened.PrepareRestart(() => { }) && backend.Installs == 0);
        reopened.CancelDownload(); await downloading;
        check("download cancellation leaves update available", reopened.Stage == AppUpdateStage.Available && reopened.Error is null);
        backend.DownloadGate = null; backend.FailDownload = true; await reopened.DownloadAsync();
        check("failed download cannot become installable", reopened.Stage == AppUpdateStage.Available && reopened.Error is not null && backend.Installs == 0);
        backend.FailDownload = false; await reopened.DownloadAsync();
        check("verified download waits for explicit restart", reopened.Stage == AppUpdateStage.Ready && backend.Installs == 0);
        var checksBeforeReady = backend.Checks; await reopened.CheckAsync(true);
        check("checking again preserves an already prepared update", backend.Checks == checksBeforeReady && reopened.Stage == AppUpdateStage.Ready);
        check("failed settings save prevents installation", !reopened.PrepareRestart(() => throw new IOException()) && backend.Installs == 0);
        bool saved = false; backend.BeforeInstall = () => check("settings are saved before updater starts", saved);
        check("explicit install starts updater", reopened.PrepareRestart(() => saved = true) && backend.Installs == 1);
        check("repeat install requests are blocked", !reopened.PrepareRestart(() => { }) && backend.Installs == 1);
        backend.PendingRelease = backend.Release;
        var restored = new AppUpdates(backend, Path.Combine(root, "pending"));
        check("downloaded update is restored offline after restart", restored.Stage == AppUpdateStage.Ready && restored.Release == backend.Release);
        backend.PendingRelease = null; backend.Release = null; var empty = new AppUpdates(backend, Path.Combine(root, "empty"));
        await empty.CheckAsync(true);
        check("no newer release displays current version normally", !empty.HasUpdate && empty.LastChecked is not null && empty.Error is null);
        backend.CanInstall = false; backend.Release = new("0.5.0", "", 100); await empty.CheckAsync(true); await empty.DownloadAsync();
        check("unpackaged app can check but cannot overwrite its running executable", empty.HasUpdate && empty.Stage == AppUpdateStage.Available);
        var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await empty.CheckAsync(true, cancelled.Token);
        check("canceled check is quiet and preserves available release", empty.HasUpdate && empty.Error is null);
    }
}

internal sealed class UpdateFixture : IAppUpdateBackend
{
    public string CurrentVersion => "0.1.0";
    public bool CanInstall { get; set; } = true;
    public AppRelease? PendingRelease { get; set; }
    public AppRelease? Release { get; set; } = new("0.2.0", "Update notes", 100);
    public Task<AppRelease?>? CheckResult { get; set; }
    public Task? DownloadGate { get; set; }
    public bool FailCheck { get; set; }
    public bool FailDownload { get; set; }
    public int Checks { get; private set; }
    public int Downloads { get; private set; }
    public int Installs { get; private set; }
    public Action<int>? Report { get; private set; }
    public Action? BeforeInstall { get; set; }
    public Task<AppRelease?> CheckAsync(CancellationToken cancellation)
    {
        Checks++;
        if (FailCheck) throw new HttpRequestException();
        return CheckResult ?? Task.FromResult(Release);
    }
    public async Task DownloadAsync(AppRelease release, Action<int> progress, CancellationToken cancellation)
    {
        Downloads++; Report = progress;
        if (FailDownload) throw new IOException("Checksum mismatch");
        if (DownloadGate is not null) await DownloadGate.WaitAsync(cancellation);
        progress(100);
    }
    public void PrepareRestart() { BeforeInstall?.Invoke(); Installs++; }
}
