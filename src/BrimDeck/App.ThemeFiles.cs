using System.IO;
using System.Windows.Threading;

namespace BrimDeck;

public partial class App
{
    private FileSystemWatcher? _themeWatcher;
    private DispatcherTimer? _themeReload;
    private string _themeWatchStamp = "";

    // 主题文件监听归应用所有；设置页关闭后，浮动置物架仍能跟随自定义主题重载。
    private void InitializeThemeFiles()
    {
        if (_themeWatcher is not null || Exiting) return;
        try
        {
            Directory.CreateDirectory(PanelTheme.Folder);
            if (_themeReload is null)
            {
                _themeWatchStamp = ThemeFolderStamp();
                _themeReload = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
                _themeReload.Tick += (_, _) =>
                {
                    _themeReload.Stop();
                    if (Exiting) return;
                    string stamp = ThemeFolderStamp();
                    if (stamp != _themeWatchStamp)
                    {
                        _themeWatchStamp = stamp;
                        ApplyPanelTheme(reload: true);
                        _settingsWindow?.PanelThemeChanged(onlyThemePage: true);
                    }
                    if (_themeWatcher is null) InitializeThemeFiles();
                };
            }
            void Restart() { if (Exiting) return; _themeReload.Stop(); _themeReload.Start(); }
            var watcher = new FileSystemWatcher(PanelTheme.Folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void Changed(object? _, FileSystemEventArgs e)
            {
                if (!e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && e.ChangeType != WatcherChangeTypes.Deleted && e.ChangeType != WatcherChangeTypes.Renamed) return;
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Restart);
            }
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += Changed;
            watcher.Error += (_, _) =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_themeWatcher, watcher)) return;
                    _themeWatcher = null; watcher.Dispose(); _themeWatchStamp = "?";
                    Restart();
                });
            };
            watcher.EnableRaisingEvents = true;
            _themeWatcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { _themeWatcher = null; }
    }

    internal static string ThemeFolderStamp()
    {
        try
        {
            var folder = new DirectoryInfo(PanelTheme.Folder);
            if (!folder.Exists) return "";
            return string.Join("|", folder.EnumerateFiles("*.json").Concat(folder.EnumerateDirectories().Select(dir => new FileInfo(Path.Combine(dir.FullName, "theme.json"))).Where(file => file.Exists))
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).Select(file => $"{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Guid.NewGuid().ToString(); }
    }

    internal void AcknowledgeThemeFiles() => _themeWatchStamp = ThemeFolderStamp();

    private void DisposeThemeFiles()
    {
        _themeWatcher?.Dispose(); _themeWatcher = null;
        _themeReload?.Stop();
    }
}
