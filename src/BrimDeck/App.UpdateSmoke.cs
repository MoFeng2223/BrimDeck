using System.IO;
using System.Windows;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task VerifyAppUpdatesAsync(string output, List<string> checks)
    {
        void Check(string name, bool value) => checks.Add((value ? "PASS " : "FAIL ") + name);
        var original = Updates; var originalSettings = Settings.Copy();
        _settingsWindow?.Close();
        var fake = new UpdateSmokeBackend();
        Updates = new AppUpdates(fake, Path.Combine(Store.DirectoryPath, "update-test"));
        try
        {
            foreach (var theme in new[] { SettingsTheme.Light, SettingsTheme.Dark })
            {
                var settings = Settings.Copy(); settings.Theme = theme; UpdateSettings(settings);
                OpenSettings(); var window = _settingsWindow!; window.ShowPage(7); await Task.Delay(80);
                await Updates.CheckAsync(true);
                await Task.Delay(60);
                Capture(window.RootVisual, Path.Combine(output, $"updates-{theme.ToString().ToLowerInvariant()}-available.png"));
                // Drive the owned modal using WPF's dispatcher, without interacting with other applications.
                var closed = new TaskCompletionSource();
                _ = Dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        await Task.Delay(100);
                        if (window.UpdateDialog is { } dialog)
                        {
                            Check($"{theme} update dialog is owned and never topmost", dialog.Owner == window && !dialog.Topmost);
                            Capture((FrameworkElement)dialog.Content, Path.Combine(output, $"updates-{theme.ToString().ToLowerInvariant()}-dialog.png"));
                            dialog.Close();
                        }
                        else Check($"{theme} update dialog opens", false);
                    }
                    finally { closed.SetResult(); }
                });
                window.Activate(); await window.CheckUpdatesOnOpenAsync(); await closed.Task;
                Check($"{theme} dismissal keeps badge and suppresses repeat prompt", Updates.HasUpdate && !Updates.ShouldPrompt(true, true));
                fake.DownloadGate = new TaskCompletionSource();
                var download = Updates.DownloadAsync(); fake.Report?.Invoke(42); await Task.Delay(80);
                Capture(window.RootVisual, Path.Combine(output, $"updates-{theme.ToString().ToLowerInvariant()}-downloading.png"));
                Updates.CancelDownload(); await download;
                fake.FailDownload = true; await Updates.DownloadAsync(); await Task.Delay(50);
                Capture(window.RootVisual, Path.Combine(output, $"updates-{theme.ToString().ToLowerInvariant()}-error.png"));
                Check($"{theme} failed download offers retry", Updates.Stage == AppUpdateStage.Available && Updates.Error is not null);
                fake.FailDownload = false; fake.DownloadGate = null; await Updates.DownloadAsync(); await Task.Delay(60);
                Capture(window.RootVisual, Path.Combine(output, $"updates-{theme.ToString().ToLowerInvariant()}-ready.png"));
                Check($"{theme} prepared update waits for explicit install", fake.Installs == 0 && Updates.Stage == AppUpdateStage.Ready);
                window.Close();
                Updates = new AppUpdates(fake, Path.Combine(Store.DirectoryPath, "update-test-" + theme));
            }
            // A slow response arriving after settings close must not open a window.
            fake.CheckGate = new TaskCompletionSource<AppRelease?>();
            OpenSettings(); var closing = _settingsWindow!;
            var checkTask = closing.CheckUpdatesOnOpenAsync(); await Task.Delay(50); closing.Close();
            fake.CheckGate.SetResult(fake.Release); await checkTask;
            Check("a late check cannot reopen closed settings or show an update dialog", closing.UpdateDialog is null && _settingsWindow is null);
            fake.CheckGate = null;
            OpenSettings(); var inactive = _settingsWindow!; inactive.WindowState = WindowState.Minimized;
            await inactive.CheckUpdatesOnOpenAsync();
            Check("minimized settings do not show update prompt", inactive.UpdateDialog is null);
            inactive.Close();
            Updates = new AppUpdates(fake, Path.Combine(Store.DirectoryPath, "update-focus-test"));
            fake.CheckGate = new TaskCompletionSource<AppRelease?>();
            OpenSettings(); var background = _settingsWindow!;
            var backgroundCheck = background.CheckUpdatesOnOpenAsync(); await Task.Delay(30);
            var focusWindow = new Window { Width = 160, Height = 80, Title = "Update focus test", ShowInTaskbar = false };
            focusWindow.Show(); focusWindow.Activate(); await Task.Delay(30);
            fake.CheckGate.SetResult(fake.Release); await backgroundCheck;
            Check("a response received after settings lose focus does not prompt", !background.IsActive && background.UpdateDialog is null);
            focusWindow.Close(); background.Close();
        }
        finally { _settingsWindow?.Close(); Updates = original; UpdateSettings(originalSettings); }
    }

    private sealed class UpdateSmokeBackend : IAppUpdateBackend
    {
        public string CurrentVersion => "0.1.0";
        public bool CanInstall => true;
        public AppRelease? PendingRelease => null;
        public AppRelease Release { get; } = new("0.2.0", "优化设置中的文字层级与控件间距。\n\n新增应用内更新，支持下载进度、取消下载与安装后重启。\n现有外观和应用配置将保留。", 85 * 1048576);
        public TaskCompletionSource<AppRelease?>? CheckGate { get; set; }
        public TaskCompletionSource? DownloadGate { get; set; }
        public bool FailDownload { get; set; }
        public Action<int>? Report { get; private set; }
        public int Installs { get; private set; }
        public Task<AppRelease?> CheckAsync(CancellationToken cancellation) => CheckGate?.Task ?? Task.FromResult<AppRelease?>(Release);
        public async Task DownloadAsync(AppRelease release, Action<int> progress, CancellationToken cancellation)
        {
            Report = progress;
            if (FailDownload) throw new IOException("Test download failure");
            if (DownloadGate is { } gate) await gate.Task.WaitAsync(cancellation);
            progress(100);
        }
        public void PrepareRestart() => Installs++;
    }
}
