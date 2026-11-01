using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck.Native;

// A message-only window lives independently of display panels. The notification only schedules a bounded read: the copying
// process commonly still owns the clipboard during WM_CLIPBOARDUPDATE, so it must not be opened from the message hook.
internal sealed class ClipboardListener : IDisposable
{
    private readonly HwndSource _source;
    private readonly DispatcherTimer _read = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly Func<ClipboardSettings> _settings;
    private readonly Action<ClipboardContent, string, string> _captured;
    private readonly Action _unavailable;
    private uint _lastSequence, _writtenSequence;
    private int _attempts;
    private bool _disposed, _paused;
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value; _read.Stop(); _attempts = 0;
            // A pause drops pending reads; resuming starts after the current sequence so a private copy made during the
            // pause cannot be imported by a delayed notification. This inspects metadata only, never the copied payload.
            _lastSequence = GetClipboardSequenceNumber();
        }
    }
    private static readonly uint Exclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint HistoryAllowed = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CloudAllowed = RegisterClipboardFormat("CanUploadToCloudClipboard");

    internal ClipboardListener(Func<ClipboardSettings> settings, Action<ClipboardContent, string, string> captured, Action unavailable)
    {
        _settings = settings; _captured = captured; _unavailable = unavailable;
        _source = new HwndSource(new HwndSourceParameters("BrimDeck.Clipboard") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        _source.AddHook(Hook);
        if (!AddClipboardFormatListener(_source.Handle))
        {
            int error = Marshal.GetLastWin32Error(); _source.RemoveHook(Hook); _source.Dispose(); throw new Win32Exception(error);
        }
        _lastSequence = GetClipboardSequenceNumber();
        _read.Tick += Read;
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x031D && !_disposed && !Paused) { _attempts = 0; _read.Stop(); _read.Start(); }
        return IntPtr.Zero;
    }
    private void Read(object? sender, EventArgs args)
    {
        _read.Stop();
        if (_disposed || Paused || !_settings().Enabled) return;
        uint sequence = GetClipboardSequenceNumber();
        if (sequence == _lastSequence || sequence == _writtenSequence) return;
        try
        {
            // These registered formats are password-manager and Windows history opt-outs; their presence/value wins over
            // every setting, before the payload is read. No clipboard content is ever sent to a logger or network service.
            if (Sensitive()) { _lastSequence = sequence; return; }
            var (name, path) = Identity(GetClipboardOwner());
            if (_settings().Excludes(name)) { _lastSequence = sequence; return; }
            var data = Clipboard.GetDataObject();
            if (data is null) { _lastSequence = sequence; return; }
            ClipboardContent? content = ReadContent(data, _settings(), name);
            // Never attach source metadata to a payload from the following copy.
            if (GetClipboardSequenceNumber() != sequence) { Retry(); return; }
            // Delayed OLE rendering may pump messages while GetData runs. A pause, opt-out or disposal in that interval
            // invalidates this read too, even if recording has already resumed by the time the payload returns.
            if (_disposed || Paused || !_settings().Enabled || sequence == _lastSequence) return;
            _lastSequence = sequence;
            if (content is not null) _captured(content, name, path);
        }
        catch (Exception error) when (error is ExternalException or IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        { Retry(); }
    }
    private void Retry()
    {
        if (++_attempts < 4) _read.Start(); else _unavailable();
    }
    internal void IgnoreCurrentWrite() => _writtenSequence = _lastSequence = GetClipboardSequenceNumber();

    internal static ClipboardContent? ReadContent(IDataObject data, ClipboardSettings settings, string sourceName = "")
    {
        if (data.GetDataPresent(DataFormats.FileDrop, false))
        {
            if (data.GetData(DataFormats.FileDrop, false) is not string[] paths || paths.Length is 0 or > 200) return null;
            // QQ offers a copied chat image as CF_HDROP too. Keep Explorer and multi-file copies as files,
            // but decode the single chat image before its attachment representation can win.
            if (IsChatImageSource(sourceName) && paths.Length == 1 && IsImagePath(paths[0]))
            {
                if (!settings.CaptureImages) return null;
                if ((ReadLocalImage(paths[0], sourceName) ?? ReadImage(data, sourceName)) is { } image) return image with { Files = paths };
            }
            return settings.CaptureFiles ? new ClipboardContent { Kind = ClipboardContentKind.Files, Files = paths } : null;
        }
        if (data.GetDataPresent("image/svg+xml", false) && TextFormat(data, "image/svg+xml") is { Length: > 0 } svg && ClipboardContentTypes.IsSvg(svg))
            return new ClipboardContent { Kind = ClipboardContentKind.Svg, Text = svg };
        if (data.GetDataPresent(DataFormats.Bitmap) || EncodedImageFormats.Any(format => data.GetDataPresent(format, false)))
        {
            if (!settings.CaptureImages) return null;
            if (ReadImage(data, sourceName) is { } image) return image;
        }
        string text = TextFormat(data, DataFormats.UnicodeText) ?? "";
        string html = TextFormat(data, DataFormats.Html) ?? "", rtf = TextFormat(data, DataFormats.Rtf) ?? "";
        if (text.Length == 0 && html.Length > 0) text = ClipboardContentTypes.PlainHtml(html);
        if (text.Length == 0 && rtf.Length > 0) text = PlainRtf(rtf);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var content = ClipboardContent.FromText(text);
        // Keep the original rich formats for recall, but previews only draw inert text, never active HTML or RTF objects.
        if (html.Length > 0 || rtf.Length > 0) content = content with { Kind = content.Kind == ClipboardContentKind.Text ? ClipboardContentKind.RichText : content.Kind, Html = html, Rtf = rtf };
        return content;
    }
    // Encoded formats follow DropShelf DropDataReader; clipboard capture validates and stores bounded pixels in history,
    // rather than calling the shelf importer or creating another attachment cache.
    private static readonly string[] EncodedImageFormats = ["PNG", "image/png", "JFIF", "image/jpeg", "GIF", "image/gif"];
    private static bool IsChatImageSource(string name) => name.Equals("QQ", StringComparison.OrdinalIgnoreCase)
        || name.Equals("QQNT", StringComparison.OrdinalIgnoreCase) || name.Equals("TIM", StringComparison.OrdinalIgnoreCase);
    private static bool IsImagePath(string? path) => Path.GetExtension(path)?.ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".ico" or ".heic" or ".avif";
    private static ClipboardContent? ReadLocalImage(string path, string sourceName)
    {
        try
        {
            // Only local drive paths may be probed. UNC, device paths and mapped network drives must never trigger I/O.
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return null;
            string full = Path.GetFullPath(path), root = Path.GetPathRoot(full)!;
            if (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram)) return null;
            string current = root;
            foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
            }
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > ClipboardHistory.MaxItemBytes) return null;
            return DecodeImage(stream, sourceName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or ExternalException)
        { return null; }
    }
    private static ClipboardContent? ReadImage(IDataObject data, string sourceName)
    {
        foreach (string format in EncodedImageFormats)
        {
            if (!data.GetDataPresent(format, false)) continue;
            object? value = data.GetData(format, false);
            byte[]? bytes = value switch
            {
                byte[] raw when raw.Length is > 0 and <= ClipboardHistory.MaxItemBytes => raw,
                MemoryStream raw when raw.Length is > 0 and <= ClipboardHistory.MaxItemBytes => raw.ToArray(),
                _ => null
            };
            if (bytes is null) continue;
            try
            {
                using var stream = new MemoryStream(bytes, false);
                if (DecodeImage(stream, sourceName) is { } image) return image;
            }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or NotSupportedException or ExternalException) { }
        }
        return data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap ? CaptureImage(bitmap, sourceName) : null;
    }
    private static ClipboardContent? DecodeImage(Stream stream, string sourceName)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        return decoder.Frames.Count > 0 ? CaptureImage(decoder.Frames[0], sourceName) : null;
    }
    private static ClipboardContent? CaptureImage(BitmapSource image, string sourceName)
    {
        if (image.PixelWidth is <= 0 or > 8192 || image.PixelHeight is <= 0 or > 8192 || (long)image.PixelWidth * image.PixelHeight > 16_777_216) return null;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream);
        if (stream.Length > ClipboardHistory.MaxItemBytes) return null;
        return new ClipboardContent { Kind = IsScreenshotSource(sourceName) ? ClipboardContentKind.Screenshot : ClipboardContentKind.Image, Image = stream.ToArray(), PixelWidth = image.PixelWidth, PixelHeight = image.PixelHeight };
    }
    private static bool IsScreenshotSource(string name) => name.Equals("SnippingTool", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ScreenClippingHost", StringComparison.OrdinalIgnoreCase) || name.Equals("ShareX", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Snipaste", StringComparison.OrdinalIgnoreCase);
    private static string? TextFormat(IDataObject data, string format)
    {
        if (!data.GetDataPresent(format, false)) return null;
        object? value = data.GetData(format, false);
        if (value is string text) return text.Length <= 1_000_000 ? text : null;
        if (value is MemoryStream stream && stream.Length <= 1_000_000)
            return System.Text.Encoding.UTF8.GetString(stream.ToArray()).TrimEnd('\0');
        return null;
    }
    private static string PlainRtf(string rtf)
    {
        // Do not instantiate a rich document: embedded objects and external images stay inert at the capture boundary.
        string plain = System.Text.RegularExpressions.Regex.Replace(rtf, @"\\'[0-9a-fA-F]{2}|\\[a-zA-Z]+-?\d* ?|[{}]", "", System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
        return plain.Trim();
    }

    internal static DataObject ToDataObject(ClipboardContent content)
    {
        var data = new DataObject();
        // A history recall must not be collected by Windows cloud/history or by other managers as another private copy.
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        if (content.Kind is ClipboardContentKind.Image or ClipboardContentKind.Screenshot)
        {
            using var stream = new MemoryStream(content.Image, writable: false);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); image.Freeze();
            data.SetImage(image);
            data.SetData("PNG", new MemoryStream(content.Image, writable: false));
        }
        else if (content.Kind == ClipboardContentKind.Files)
        {
            // Refuse partial recalls: missing files must not silently turn a multi-file copy into a different selection.
            if (content.Files.Any(path => !File.Exists(path) && !Directory.Exists(path))) throw new FileNotFoundException("A clipboard file is no longer available.");
            var paths = new StringCollection(); paths.AddRange(content.Files); data.SetFileDropList(paths);
        }
        else
        {
            data.SetText(content.Text, TextDataFormat.UnicodeText);
            if (content.Html.Length > 0 || content.Rtf.Length > 0)
            {
                if (content.Html.Length > 0) data.SetData(DataFormats.Html, content.Html);
                if (content.Rtf.Length > 0) data.SetData(DataFormats.Rtf, content.Rtf);
            }
            if (content.Kind == ClipboardContentKind.Svg) data.SetData("image/svg+xml", content.Text);
        }
        return data;
    }

    private static bool Sensitive()
    {
        if (IsClipboardFormatAvailable(Exclude)) return true;
        if (!OpenClipboard(IntPtr.Zero)) throw new ExternalException("Clipboard is busy.");
        try { return IsZero(HistoryAllowed) || IsZero(CloudAllowed); }
        finally { CloseClipboard(); }
    }
    private static bool IsZero(uint format)
    {
        if (!IsClipboardFormatAvailable(format)) return false;
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero || GlobalSize(handle).ToUInt64() < 4) return true;
        var memory = GlobalLock(handle);
        if (memory == IntPtr.Zero) return true;
        try { return Marshal.ReadInt32(memory) == 0; } finally { GlobalUnlock(handle); }
    }
    private static (string Name, string Path) Identity(IntPtr window)
    {
        try
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid == 0) return ("", "");
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            string name = process.ProcessName, path = "";
            try { path = process.MainModule?.FileName ?? ""; } catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { }
            return (name, path);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException) { return ("", ""); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _read.Stop(); _read.Tick -= Read;
        RemoveClipboardFormatListener(_source.Handle); _source.RemoveHook(Hook); _source.Dispose();
    }
    [DllImport("user32", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32")] private static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32")] private static extern bool CloseClipboard();
    [DllImport("user32")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32")] private static extern bool GlobalUnlock(IntPtr memory);
}
