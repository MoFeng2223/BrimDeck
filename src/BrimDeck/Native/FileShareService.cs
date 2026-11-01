using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using BrimDeck.Core;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace BrimDeck.Native;

// Shares the original paths through Windows' Share Sheet. No FileTray entry, copied payload or temporary file is created.
internal sealed class FileShareService : IDisposable
{
    private static readonly Guid DataTransferManagerIid = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
    internal const string TrayOriginFormat = "BrimDeck.FileTray.Origin";
    private readonly Func<IntPtr> _window;
    private DataTransferManager? _manager;
    private IDataTransferManagerInterop? _interop;
    private TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? _requested;
    private string[] _paths = [];
    private string _title = "";
    private bool _disposed;

    internal FileShareService(Func<IntPtr> window)
    {
        _window = window;
    }

    internal bool TryShare(IEnumerable<string> paths, string title, out string problem)
    {
        problem = "";
        if (_disposed)
        {
            problem = Loc.T("分享服务已关闭", "The share service is closed");
            return false;
        }
        string[] normalized = NormalizePaths(paths);
        if (normalized.Length == 0)
        {
            problem = Loc.T("源文件不存在或无法访问", "The source file is missing or inaccessible");
            return false;
        }
        IntPtr hwnd = _window();
        if (hwnd == IntPtr.Zero)
        {
            problem = Loc.T("暂时无法打开系统分享", "The system share UI is not available yet");
            return false;
        }
        try
        {
            EnsureManager(hwnd);
            _paths = normalized;
            _title = string.IsNullOrWhiteSpace(title) ? Loc.T("分享文件", "Share files") : title.Trim();
            _interop!.ShowShareUIForWindow(hwnd);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            _paths = []; _title = "";
            problem = Loc.T("无法打开系统分享，请稍后重试", "The system share UI could not be opened; try again later");
            return false;
        }
    }

    internal static bool IsExternalFileDrop(IDataObject data, bool ownDrag)
    {
        try { return !ownDrag && data.GetDataPresent(DataFormats.FileDrop, false) && !data.GetDataPresent(TrayOriginFormat, false); }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or NotSupportedException) { return false; }
    }

    internal static string[] NormalizePaths(IEnumerable<string>? paths)
    {
        if (paths is null) return [];
        var result = new List<string>();
        foreach (string path in paths)
        {
            // Never send a partial selection when one of the originals has disappeared or cannot be resolved.
            if (string.IsNullOrWhiteSpace(path)) return [];
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return []; }
            if (!File.Exists(full) && !Directory.Exists(full)) return [];
            if (!result.Contains(full, StringComparer.OrdinalIgnoreCase)) result.Add(full);
        }
        return [.. result];
    }

    private void EnsureManager(IntPtr hwnd)
    {
        if (_manager is not null && _interop is not null) return;
        _interop = DataTransferManager.As<IDataTransferManagerInterop>();
        Guid iid = DataTransferManagerIid;
        IntPtr abi = _interop.GetForWindow(hwnd, ref iid);
        if (abi == IntPtr.Zero) throw new COMException("DataTransferManager.GetForWindow returned null");
        try { _manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(abi); }
        finally { Marshal.Release(abi); }
        _requested = DataRequested;
        _manager.DataRequested += _requested;
    }

    // Build the package only from original StorageItems. Read-only access and Copy never authorize the recipient to move them.
    internal static async Task PopulateAsync(DataPackage data, IReadOnlyList<string> paths, string title)
    {
        if (paths.Count == 0) throw new FileNotFoundException("No source files to share");
        var items = new List<IStorageItem>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path)) items.Add(await StorageFolder.GetFolderFromPathAsync(path));
            else items.Add(await StorageFile.GetFileFromPathAsync(path));
        }
        data.Properties.Title = title;
        data.SetStorageItems(items, readOnly: true);
        data.RequestedOperation = DataPackageOperation.Copy;
    }

    private async void DataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        // Another share request must not replace the payload or title halfway through an asynchronous file lookup.
        string[] paths = _paths; string title = _title;
        _paths = []; _title = "";
        try
        {
            await PopulateAsync(args.Request.Data, paths, title);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            args.Request.FailWithDisplayText(Loc.T("源文件已不存在或无法访问，未分享任何文件。", "A source file is missing or inaccessible. No files were shared."));
        }
        finally { deferral.Complete(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_manager is not null && _requested is not null) _manager.DataRequested -= _requested;
        _requested = null; _manager = null; _interop = null; _paths = [];
    }

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }
}
