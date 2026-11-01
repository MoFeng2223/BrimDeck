using System.ComponentModel;
using System.IO;
using DropShelf.Services;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

internal sealed record ClipboardStorageUsage(int Count, long ContentBytes, long? HistoryBytes, long? PendingBytes, long? CacheBytes);

internal sealed class ClipboardService : IDisposable
{
    private readonly ClipboardHistory _history = new();
    internal ClipboardHistory History { get { EnsureLoaded(); return _history; } }
    internal bool Loaded => _loaded;
    internal int WriteCount { get; private set; }
    internal int CleanCount { get; private set; }
    private bool _loaded, _loading, _dirty, _stopping;
    private ClipboardSettings? _applied;
    private readonly DispatcherTimer _saveDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Func<ClipboardSettings> _settings;
    private readonly Dispatcher _dispatcher;
    private readonly string _defaultDirectory;
    private ClipboardHistoryStore _store;
    private bool _changingStorage, _writePending, _validStoragePath = true;
    private readonly DispatcherTimer _clean = new() { Interval = TimeSpan.FromMinutes(1) };
    private ClipboardListener? _listener;
    private Task _writing = Task.CompletedTask;
    private bool _disposed, _paused, _failedWrite;
    private string _storageError = "", _captureError = "";
    internal event Action? StatusChanged;
    internal string Status => _storageError.Length > 0 ? _storageError : _captureError;
    internal bool Paused => _paused;
    internal bool Listening => _listener is not null;
    internal string StorageDirectory => _validStoragePath ? _store.DirectoryPath : _settings().StorageDirectory;
    internal bool ChangingStorage => _changingStorage;

    internal ClipboardService(string directory, Func<ClipboardSettings> settings, Dispatcher dispatcher)
    {
        _settings = settings; _dispatcher = dispatcher; _defaultDirectory = directory;
        try { _store = new(ClipboardHistoryStore.ResolveDirectory(settings().StorageDirectory, directory)); }
        catch (Exception error) when (ClipboardHistoryStore.IsStorageError(error))
        {
            // An invalid custom path must not silently load or overwrite a different history at the default location.
            _store = new(directory); _validStoragePath = false; _storageError = Loc.T("数据路径无效，请在剪贴板设置中重新选择。", "The data path is invalid. Choose another directory in clipboard settings.");
        }
        _history.Changed += Changed; _history.CommitRequested += CommitRequested;
        _clean.Tick += ExpirationDue;
        _saveDelay.Tick += SaveDue;
        ApplySettings();
    }
    internal void ApplySettings()
    {
        if (_disposed || _stopping) return;
        var settings = _settings();
        if (!settings.Enabled) { _listener?.Dispose(); _listener = null; }
        else EnsureLoaded();
        if (settings.Enabled && _listener is null)
        {
            try
            {
                _listener = new ClipboardListener(_settings, (content, name, path) =>
                {
                    _captureError = "";
                    if (History.Add(content, name, path, DateTimeOffset.UtcNow, _settings()) is null)
                        _captureError = Loc.T("记录未保存：内容无效、过大或收藏与归档已占满上限。", "Not saved: invalid/oversized content or protected entries fill the history limit.");
                    StatusChanged?.Invoke();
                }, () => { _captureError = Loc.T("剪贴板被其他程序占用，这次复制未能记录。", "Another app held the clipboard; this copy could not be recorded."); StatusChanged?.Invoke(); });
                _listener.Paused = _paused; _captureError = "";
            }
            catch (Win32Exception) { _captureError = Loc.T("无法注册系统剪贴板监听，请关闭后重新启用。", "System clipboard monitoring could not be registered. Turn it off and on again."); }
        }
        if (_loaded && (_applied is null || settings.RetentionDays != _applied.RetentionDays || settings.MaximumItems != _applied.MaximumItems)) Clean(null, EventArgs.Empty);
        // A presentation-only setting never serializes history or registers a second listener.
        if (_applied is not null && settings.PersistHistory != _applied.PersistHistory)
        {
            if (settings.PersistHistory) EnsureLoaded();
            QueueWrite();
            if (!settings.PersistHistory) CommitRequested();
        }
        _applied = settings.Copy();
        ScheduleExpiration(); StatusChanged?.Invoke();
    }
    internal void TogglePause()
    {
        _paused = !_paused;
        if (_listener is not null) _listener.Paused = _paused;
        StatusChanged?.Invoke();
    }
    internal void EnsureLoaded()
    {
        if (_loaded || _disposed) return;
        _loaded = true; _loading = true;
        try
        {
            if (_storageError.Length == 0 && _settings().PersistHistory)
                _history.Restore(_store.Load(), _settings(), DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (ClipboardHistoryStore.IsStorageError(error))
        { _storageError = Loc.T("无法读取本地剪贴板历史，原文件未被修改。", "Local clipboard history could not be read; the original file is unchanged."); }
        finally { _loading = false; ScheduleExpiration(); }
    }
    private void ScheduleExpiration()
    {
        _clean.Stop();
        if (!_loaded || _disposed || _stopping || _history.NextExpiration(_settings()) is not { } next) return;
        // WPF limits an interval to Int32 milliseconds; longer retention waits in bounded one-shot segments.
        _clean.Interval = TimeSpan.FromMilliseconds(Math.Clamp((next - DateTimeOffset.UtcNow).TotalMilliseconds, 1, int.MaxValue));
        _clean.Start();
    }
    private void ExpirationDue(object? sender, EventArgs e)
    {
        _clean.Stop();
        if (_history.NextExpiration(_settings()) is { } next && next > DateTimeOffset.UtcNow) { ScheduleExpiration(); return; }
        Clean(sender, e);
    }
    private void Clean(object? sender, EventArgs e)
    {
        _clean.Stop();
        if (_disposed || _stopping || !_loaded) return;
        CleanCount++; _history.Clean(_settings(), DateTimeOffset.UtcNow); ScheduleExpiration();
    }
    private void Changed()
    {
        if (_loading || _disposed) return;
        ScheduleExpiration(); QueueWrite();
    }
    private void QueueWrite()
    {
        if (_changingStorage) { _writePending = true; return; }
        if (!_validStoragePath || _disposed) return;
        _dirty = true;
        _saveDelay.Stop();
        if (!_stopping) _saveDelay.Start();
    }
    private async void CommitRequested() => await FlushAsync();
    private void SaveDue(object? sender, EventArgs e) { _saveDelay.Stop(); StartWrite(); }
    private void StartWrite()
    {
        if (!_dirty || !_writing.IsCompleted || _changingStorage || !_validStoragePath) return;
        _dirty = false; _failedWrite = false;
        bool persist = _settings().PersistHistory;
        if (persist && !_loaded) return;
        var snapshot = persist ? _history.Snapshot() : null;
        var store = _store;
        _writing = Task.Run(() =>
        {
            try { if (snapshot is null) store.Delete(); else store.Save(snapshot); return ""; }
            catch (Exception error) when (ClipboardHistoryStore.IsStorageError(error))
            { return Loc.T("本地历史写入失败，当前记录仅保留在内存。", "Local history could not be saved; current entries remain in memory only."); }
        }).ContinueWith(task =>
        {
            _storageError = task.Result; _failedWrite = task.Result.Length > 0;
            WriteCount++;
            if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                StatusChanged?.Invoke();
                if (_dirty && !_stopping) { _saveDelay.Stop(); _saveDelay.Start(); }
            });
        }, TaskScheduler.Default);
    }
    internal async Task<bool> FlushAsync()
    {
        _saveDelay.Stop();
        await _writing;
        if (_failedWrite) _dirty = true;
        do
        {
            StartWrite(); await _writing;
            if (_failedWrite) { _dirty = true; return false; }
        } while (_dirty && _validStoragePath && !_changingStorage);
        return _storageError.Length == 0;
    }
    internal void OpenedOrResumed() { EnsureLoaded(); Clean(null, EventArgs.Empty); }
    internal void SuspendCapture()
    { _stopping = true; _listener?.Dispose(); _listener = null; _clean.Stop(); _saveDelay.Stop(); }
    internal void ResumeCapture() { _stopping = false; ApplySettings(); }
    internal async Task<ClipboardStorageUsage> MeasureStorageAsync()
    {
        int count = History.Items.Count; long content = History.Items.Sum(item => item.Content.ByteCount);
        var store = _store; await FlushAsync();
        return await Task.Run(() =>
        {
            long? history = null, pending = null, cache = null;
            try { if (_validStoragePath) { var size = store.Measure(); history = size.HistoryBytes; pending = size.PendingBytes; } }
            catch (Exception error) when (ClipboardHistoryStore.IsStorageError(error)) { }
            try { cache = VirtualFileDropService.MeasureCacheBytes(); }
            catch (Exception error) when (ClipboardHistoryStore.IsStorageError(error)) { }
            return new ClipboardStorageUsage(count, content, history, pending, cache);
        });
    }

    internal async Task<(bool Succeeded, string Message)> ChangeStorageDirectoryAsync(string requested, Func<string, Task> commitSettings)
    {
        if (_disposed || _changingStorage) return (false, Loc.T("数据路径正在切换，请稍后重试。", "The data path is changing. Try again shortly."));
        ClipboardHistoryStore target;
        string preference;
        try
        {
            preference = string.IsNullOrWhiteSpace(requested) ? "" : ClipboardHistoryStore.ResolveDirectory(requested, _defaultDirectory);
            target = new(ClipboardHistoryStore.ResolveDirectory(preference, _defaultDirectory));
        }
        catch (Exception error) when (ClipboardHistoryStore.IsStorageError(error))
        { return (false, Loc.T("请选择有效的绝对目录路径。", "Choose a valid absolute directory path.")); }
        EnsureLoaded();
        await FlushAsync();
        if (_storageError.Length > 0) return (false, _storageError);
        var previous = _store;
        bool previousValid = _validStoragePath;
        bool same = previousValid && string.Equals(previous.DirectoryPath, target.DirectoryPath, StringComparison.OrdinalIgnoreCase);
        bool prepared = false, committed = false;
        _changingStorage = true; _writePending = false;
        try
        {
            // Finish old writes, then suspend new writes until the encrypted copy and its settings pointer agree.
            await _writing;
            if (_disposed) return (false, Loc.T("应用正在退出，路径未更改。", "The application is exiting; the path was not changed."));
            if (!same)
            {
                var snapshot = _settings().PersistHistory ? History.Snapshot() : null;
                await Task.Run(() =>
                {
                    if (File.Exists(target.FilePath) || File.Exists(target.FilePath + ".tmp")) throw new IOException("Target already contains clipboard history.");
                    Directory.CreateDirectory(target.DirectoryPath);
                    if (snapshot is not null) target.Save(snapshot, overwrite: false);
                    else
                    {
                        // Validate write access even in memory-only mode; no history is written or imported.
                        using var probe = new FileStream(target.FilePath + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                    }
                });
                prepared = snapshot is not null;
            }
            if (_disposed) return (false, Loc.T("应用正在退出，路径未更改。", "The application is exiting; the path was not changed."));
            // The callback durably saves the pointer before the old encrypted file is removed.
            await commitSettings(preference);
            _store = target; committed = true; _validStoragePath = true; _storageError = "";
            if (!same && previousValid)
            {
                try { await Task.Run(previous.Delete); }
                catch (Exception error) when (ClipboardHistoryStore.IsStorageError(error))
                { return (true, Loc.T("路径已切换，但旧目录中的加密历史无法删除，请检查旧目录权限。", "The path changed, but the old encrypted history could not be removed. Check the old directory permissions.")); }
            }
            return (true, Loc.T("数据存储路径已更新。", "The data storage path was updated."));
        }
        catch (Exception error) when (ClipboardHistoryStore.IsStorageError(error))
        { return (false, Loc.T("路径未更改：目标已有历史、无法写入或设置保存失败。原历史保持不变。", "Path unchanged: the target already contains history, is not writable, or settings could not be saved. Original history is unchanged.")); }
        finally
        {
            if (!committed && prepared)
            {
                try { await Task.Run(target.Delete); }
                catch (Exception error) when (ClipboardHistoryStore.IsStorageError(error)) { }
            }
            _changingStorage = false;
            if (!_disposed && (committed || _writePending)) QueueWrite();
            _writePending = false; StatusChanged?.Invoke();
        }
    }

    internal bool Copy(ClipboardEntry entry)
    {
        try
        {
            Clipboard.SetDataObject(ClipboardListener.ToDataObject(entry.Content), copy: true);
            _listener?.IgnoreCurrentWrite(); _captureError = ""; History.RecordUse(entry.Id, DateTimeOffset.UtcNow); StatusChanged?.Invoke(); return true;
        }
        catch (Exception error) when (error is ExternalException or System.IO.IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            _captureError = error is System.IO.FileNotFoundException
                ? Loc.T("原文件已移动或删除，未复制任何文件。", "An original file was moved or deleted; no files were copied.")
                : Loc.T("无法填充系统剪贴板，请稍后重试。", "The system clipboard could not be filled. Try again shortly.");
            StatusChanged?.Invoke(); return false;
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        SuspendCapture();
        // App shutdown flushes before reaching this path. Synchronous callers still retain the latest snapshot.
        // The App-owned asynchronous barrier is responsible for durability; Dispose never blocks the UI dispatcher.
        StartWrite();
        _disposed = true; _clean.Tick -= ExpirationDue; _saveDelay.Tick -= SaveDue; _history.Changed -= Changed; _history.CommitRequested -= CommitRequested;
    }
}
