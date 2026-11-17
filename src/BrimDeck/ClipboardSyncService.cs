using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;
using BrimDeck.Core.FileTransfer;
using BrimDeck.Native;

namespace BrimDeck;

internal sealed class ClipboardSyncAccounts
{
    public string DeviceName { get; set; } = "BrimDeck " + Environment.MachineName;
    public WeTypeIdentity WeType { get; set; } = new();
    public DoubaoIdentity? Doubao { get; set; }
    public LanClipboardPeer? Lan { get; set; }
    // The device files last went to, so the next drop goes there again instead of asking a second time.
    public ulong? FilePeerUin { get; set; }
    public string DoubaoDeviceId { get; set; } = (1_000_000_000_000_000L + RandomNumberGenerator.GetInt32(1_000_000_000) * 1_000_000L + RandomNumberGenerator.GetInt32(1_000_000)).ToString();
}

// How a connection stands, for the colour of its status: not syncing, syncing, or needing the user's attention.
internal enum SyncLevel { Idle, Active, Attention }

internal sealed partial class ClipboardSyncService : IDisposable
{
    private readonly App _app;
    private readonly string _file;
    private ClipboardWatch? _watch;
    private readonly Dictionary<bool, CancellationTokenSource> _workers = [];
    private readonly Dictionary<bool, ClipCopy> _pending = [];
    private readonly Queue<(string Hash, DateTime Time)> _received = [];
    private readonly Dictionary<bool, (string Text, SyncLevel Level)> _status = [];
    // What was last sent to each service, as a hash: its own application on this computer may hand the same thing back.
    private readonly Dictionary<bool, string> _lastSent = [];
    // Transfer invitations already taken, so one sent again before it is accepted starts nothing twice.
    private readonly ConcurrentDictionary<string, DateTime> _fileInvites = new();
    // Any file invitation the service already holds when we start is a stale leftover from before this run; only the
    // invitations that appear after startup are ones a phone is really sending now. UNIX seconds of process start.
    private readonly long _startedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly ConcurrentQueue<Func<WeTypeSyncClient, Task>> _weTypeJobs = new();
    private readonly object _openSentFileGate = new();
    private WeTypeFileSender.SentFile? _openSentFile;
    private WeTypeFileIdentity? _openFileIdentity;
    private Task _fileClosing = Task.CompletedTask;
    public string FileConnectionStatus { get; private set; } = "未连接";
    public string ReceiveConnectionStatus { get; private set; } = "未连接";
    public string FileConnectionDisplay => _openSentFile is { IsOpen: true }
        ? _fileSending != 0 ? "已连接 · 正在传输" : "已连接"
        : _openSentFile is not null ? "对端连接已断开" : FileConnectionStatus;

    // Whether a file session to a phone is open right now; the Send button and Ctrl+drag work only then.
    public bool FileTransferConnected => _openSentFile is { IsOpen: true };
    // A connect is in flight but no session is open yet: a second click is a re-click of the same attempt, not a new one.
    public bool FileTransferConnecting => _fileSending != 0 && _openSentFile is not { IsOpen: true };

    private int _fileSending;
    private CancellationTokenSource? _fileConnectCts;

    // Cancels a connection that is still waiting (e.g. the phone has not confirmed yet). Used when switching devices so a
    // pending attempt stops instead of finishing against the previous device.
    public void CancelConnectingFile()
    {
        try { _fileConnectCts?.Cancel(); } catch { }
    }
    private IReadOnlyList<WeTypePeer> _cachedWeTypePeers = [];
    private bool _disposed;
    // Cancelled when this computer's network addresses change: a connection waiting to retry tries again at once.
    private CancellationTokenSource _wake = new();
    public string Folder { get; }
    public ClipboardSyncAccounts Accounts { get; private set; } = new();
    // Whether Doubao lets this device sync, as last read; null before the first reading.
    public DoubaoDeviceState? DoubaoCapability { get; private set; }
    public event Action? Changed;

    public ClipboardSyncService(App app, string folder)
    {
        _app = app; Folder = Path.Combine(folder, "clipboard-sync"); _file = Path.Combine(Folder, "accounts.dat");
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += NetworkChanged;
        try
        {
            if (File.Exists(_file))
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_file), null, DataProtectionScope.CurrentUser);
                try { Accounts = JsonSerializer.Deserialize<ClipboardSyncAccounts>(plain) ?? new(); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
        }
        // Unreadable connection details leave every service as not connected. The file is set aside rather than written
        // over by the next save, so that it is not lost to a fault that may pass.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            Log("connection details could not be read: " + Describe(ex));
            try { File.Copy(_file, _file + ".unreadable", true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var plain = JsonSerializer.SerializeToUtf8Bytes(Accounts);
        try
        {
            File.WriteAllBytes(_file + ".tmp", ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
            File.Move(_file + ".tmp", _file, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    // The names WeType shows: another device may have renamed this one, or joined or left. The worker reads them itself while it runs.
    public async Task RefreshWeTypeNamesAsync()
    {
        if (!Connected(false) || _workers.ContainsKey(false)) return;
        using var client = new WeTypeSyncClient(Accounts.WeType, Save);
        await client.RefreshNamesAsync(_app.Lifetime.Token);
    }
    public bool CanRename => !Connected(false) && !Connected(true);
    public void RenameDevice(string name)
    {
        name = name.Trim();
        if (!CanRename || name.Length is 0 or > 48 || name.Any(char.IsControl)) throw new ArgumentException("Invalid device name.");
        Accounts.DeviceName = name; Accounts.WeType.DeviceName = name;
        Save(); Changed?.Invoke();
    }
    // A laptop back on a network, or awake again: the wait between retries, up to a minute by then, is cut short.
    private void NetworkChanged(object? sender, EventArgs e) => _app.Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        var waiting = _wake;
        _wake = new();
        waiting.Cancel();
    });
    public bool Connected(bool doubao) => doubao ? Accounts.Doubao is not null : Accounts.WeType.Paired;
    public string Status(bool doubao) => _status.TryGetValue(doubao, out var status) ? status.Text : "";
    public SyncLevel Level(bool doubao) => _status.TryGetValue(doubao, out var status) ? status.Level : SyncLevel.Idle;
    private void Report(bool doubao, string message, SyncLevel level)
    {
        if (_disposed) return;
        if (Status(doubao) != message) Log((doubao ? "doubao" : "wetype") + " status [" + level + "] " + message);
        _status[doubao] = (message, level);
        Changed?.Invoke();
    }
    // A live console (from app.exe --console) shows the flow in real time; a plain WPF window has none.
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();

    // What happened and when, without any copied content: sizes, names of devices and applications, and the services' error
    // codes. Kept in the sync folder to tell afterwards why something was not synced; the older half goes at 256 KB.
    internal void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string file = Path.Combine(Folder, "events.log");
            if (new FileInfo(file) is { Exists: true, Length: > 256 * 1024 })
            {
                var lines = File.ReadAllLines(file);
                File.WriteAllLines(file, lines[(lines.Length / 2)..]);
            }
            File.AppendAllText(file, DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + " " + line + Environment.NewLine);
            // Mirror to the console/debugger too, so the wetype flow can be watched live without opening the file.
            string stamp = DateTime.Now.ToString("HH:mm:ss.fff") + " " + line;
            System.Diagnostics.Trace.WriteLine(stamp);
            if (Console.IsOutputRedirected || GetConsoleWindow() != IntPtr.Zero) // a real terminal (app.exe > out.txt / app.exe --console)
            {
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (Exception) { } // keep non-ASCII readable on the terminal
                Console.WriteLine(stamp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message + (ex.InnerException is { } inner ? " <- " + inner.GetType().Name + ": " + inner.Message : "");
    public async Task PairWeTypeAsync(string code, CancellationToken ct)
    {
        if (_workers.ContainsKey(false)) throw new InvalidOperationException("Disconnect before pairing again.");
        Accounts.WeType.DeviceName = Accounts.DeviceName;
        using var client = new WeTypeSyncClient(Accounts.WeType, Save) { Trace = Log };
        await client.PairAsync(code, ct);
        Changed?.Invoke();
    }
    // Signing in and being allowed to sync are separate: the account's devices each have a switch of their own.
    public async Task ConnectDoubaoAsync(DoubaoIdentity identity, CancellationToken ct)
    {
        using var client = new DoubaoSyncClient(identity);
        client.Trace = Log;
        // As the official client does on signing in; after an earlier sign-out it lists this device again.
        try { await client.ReportSignInAsync(true, ct); }
        catch (Exception ex) when (ex is DoubaoRequestException or InvalidDataException or System.Net.Http.HttpRequestException) { Log("doubao sign-in notice failed: " + Describe(ex)); }
        var capability = await client.ReadCurrentDeviceAsync(ct);
        Log("doubao signed in: sync_enabled=" + capability.SyncState + " is_support_copy=" + capability.SupportsCopy);
        // Signing in here is asking to sync: this device's switch, off for a new device, is turned on once. Turned off
        // later on another device, it stays off.
        if (!capability.CanSync && capability.SupportsCopy)
        {
            try
            {
                capability = await client.EnableAsync(ct);
                Log("doubao switch on: sync_enabled=" + capability.SyncState);
            }
            catch (Exception ex) when (ex is DoubaoRequestException or InvalidDataException or System.Net.Http.HttpRequestException) { Log("doubao switch on failed: " + Describe(ex)); }
        }
        ct.ThrowIfCancellationRequested();
        Accounts.Doubao = identity; Save();
        DoubaoCapability = capability;
        Changed?.Invoke();
    }
    // The account's other devices, for taking one off its list: Doubao's own applications have no way to, and a device whose
    // sign-in was lost (BrimDeck before a reinstall) cannot sign out itself. They are not told apart by name, which the user
    // chooses: all are listed, and the user picks.
    public async Task<IReadOnlyList<DoubaoDevice>> OtherDoubaoDevicesAsync(CancellationToken ct)
    {
        if (Accounts.Doubao is not { } identity) return [];
        using var client = new DoubaoSyncClient(identity) { Trace = Log };
        return [.. (await client.ListDevicesAsync(ct)).Where(device => !device.Current && device.Id != identity.DeviceId)];
    }
    // True once the account no longer lists the device.
    public async Task<bool> RemoveDoubaoDeviceAsync(DoubaoDevice device, CancellationToken ct)
    {
        if (Accounts.Doubao is not { } identity) return false;
        using var client = new DoubaoSyncClient(identity) { Trace = Log };
        try { await client.RemoveDeviceAsync(device, ct); }
        catch (Exception ex) when (ex is DoubaoRequestException or InvalidDataException or System.Net.Http.HttpRequestException)
        { Log("doubao device not removed: \"" + device.Name + "\" " + Describe(ex)); return false; }
        bool gone = (await client.ListDevicesAsync(ct)).All(listed => listed.Id != device.Id);
        Log("doubao device \"" + device.Name + "\" " + (gone ? "removed" : "still listed after the removal"));
        return gone;
    }
    // Signs out of Doubao. The service is told first, as the official client tells it when its account is cleared, so that
    // the account's other devices stop listing this one. The sign-in kept here goes whether or not that reached the service.
    public async Task SignOutDoubaoAsync()
    {
        if (Accounts.Doubao is { } identity)
        {
            Stop(true);
            try
            {
                using var client = new DoubaoSyncClient(identity) { Trace = Log };
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.ReportSignInAsync(false, limit.Token);
                // Nothing is asked of the service after this: asking for the account's devices names this one again.
                Log("doubao told of the sign-out");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Log("doubao sign-out notice failed: " + Describe(ex)); }
        }
        Disconnect(true);
    }
    public void Disconnect(bool doubao)
    {
        Stop(doubao);
        if (doubao)
        {
            Accounts.Doubao = null;
            DoubaoCapability = null;
        }
        else Accounts.WeType = new() { DeviceName = Accounts.DeviceName };
        _status.Remove(doubao);
        Save(); Changed?.Invoke();
    }
    public void Apply()
    {
        if (_disposed) return;
        ApplyLan();
        foreach (bool doubao in new[] { false, true })
        {
            bool enabled = doubao ? _app.Settings.DoubaoClipboardSync : _app.Settings.WeTypeClipboardSync;
            if (enabled && Connected(doubao) && !_workers.ContainsKey(doubao))
            {
                var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
                _workers[doubao] = stop; _ = RunAsync(doubao, stop);
            }
            else if ((!enabled || !Connected(doubao)) && _workers.ContainsKey(doubao)) Stop(doubao);
        }
        if ((_workers.Count > 0 || _lanStop is not null) && _watch is null)
        {
            try { _watch = new ClipboardWatch(Copied, name => ClipboardExclusions.Excludes(_app.Settings.ClipboardExcluded, name)); }
            catch (System.ComponentModel.Win32Exception)
            {
                foreach (bool doubao in _workers.Keys) Report(doubao, Loc.T("无法读取剪贴板", "Cannot read the clipboard"), SyncLevel.Attention);
            }
        }
        else ReleaseIdleWatcher();
    }
    private void ReleaseIdleWatcher()
    {
        if (_workers.Count != 0 || _lanStop is not null) return;
        _watch?.Dispose();
        _watch = null;
    }
    private void Stop(bool doubao)
    {
        if (_workers.Remove(doubao, out var source)) source.Cancel();
        _pending.Remove(doubao);
        _status.Remove(doubao);
        _lastSent.Remove(doubao);
    }
    // Which services a copy goes to. Not to the one whose own application put it on the clipboard: an input method installed
    // here and syncing by itself writes there what it received, and sends by itself what is copied here. And not what was
    // last sent to a service, which its application would hand back in the same way. Either would go round without end.
    private void Queue(ClipCopy copy, string hash)
    {
        foreach (bool doubao in _workers.Keys)
        {
            bool own = copy.SourcePath.Contains(doubao ? "\\DoubaoIME\\" : "\\WeType\\", StringComparison.OrdinalIgnoreCase);
            if (own || _lastSent.TryGetValue(doubao, out var sent) && sent == hash)
            {
                Log((doubao ? "doubao" : "wetype") + " not sent: " + (own ? "written by its own application" : "the same as last sent"));
                continue;
            }
            _pending[doubao] = copy;
        }
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private void Copied(ClipCopy copy)
    {
        while (_received.TryPeek(out var item) && item.Time < DateTime.UtcNow.AddMinutes(-2)) _received.Dequeue();
        Log("copy " + (copy.Png is { } picture ? "image " + copy.PixelWidth + "x" + copy.PixelHeight + " " + picture.Length + " bytes" : "text " + copy.Text.Length + " chars")
            + " from \"" + copy.Source + "\" -> " + string.Join(",", _workers.Keys.Select(doubao => doubao ? "doubao" : "wetype").Append(_lanOutgoing is null ? "" : "lan").Where(name => name.Length > 0)));
        if (copy.Png is { } png)
        {
            string hash = HashImage(png);
            if (_received.Any(item => item.Hash == hash)) return;
            Queue(copy, hash);
            if (_lanImages && _lanOutgoing is { } images && LanPng.Fits(copy.PixelWidth, copy.PixelHeight, png.Length)) images.Writer.TryWrite(copy);
            return;
        }
        if (copy.Text.Length == 0 || LanClipboardLink.ContainsInvitation(copy.Text)) return;
        string textHash = Hash(copy.Text);
        if (_received.Any(item => item.Hash == textHash)) return;
        if (Encoding.UTF8.GetByteCount(copy.Text) > SyncTransport.MaxTextBytes) return;
        Queue(copy, textHash);
        _lanOutgoing?.Writer.TryWrite(copy);
    }
    private void AcceptText(string text, string source, string html = "")
    {
        _app.Shelf.AcceptRemoteText(text, source, html.Length > 0 ? ClipboardHtml.Wrap(html) : "");
        _watch?.IgnoreCurrent();
        RememberReceived(Hash(text));
    }
    private bool AcceptImage(LanPng png, BitmapSource image, string source)
    {
        if (!_app.Shelf.AcceptRemoteImage(png, image, source)) return false;
        _watch?.IgnoreCurrent();
        RememberReceived(HashImage(png.Bytes));
        return true;
    }
    private void RememberReceived(string hash)
    {
        _received.Enqueue((hash, DateTime.UtcNow));
        while (_received.Count > 64) _received.Dequeue();
    }
    private static string HashImage(byte[] png) => "PNG:" + Convert.ToHexString(SHA256.HashData(png));
    private static string At(string what) => what + " · " + DateTime.Now.ToString("HH:mm:ss");

    private async Task RunAsync(bool doubao, CancellationTokenSource stop)
    {
        using IClipboardSyncClient client = doubao ? new DoubaoSyncClient(Accounts.Doubao!) { Trace = Log } : new WeTypeSyncClient(Accounts.WeType, Save) { Trace = Log };
        string source = doubao ? Loc.T("豆包输入法", "Doubao IME") : Loc.T("微信输入法", "WeType");
        bool initialized = false, sending = false; string? lastId = null, lastSignature = null; int failures = 0;
        static string Signature(RemoteClip clip) => clip.Image is { } bytes ? HashImage(bytes) : Hash(clip.Text);
        Report(doubao, Loc.T("正在连接…", "Connecting…"), SyncLevel.Idle);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                int wait = 3;
                try
                {
                    if (!initialized)
                    {
                        if (client is DoubaoSyncClient account)
                        {
                            var capability = await account.ReadCurrentDeviceAsync(stop.Token);
                            stop.Token.ThrowIfCancellationRequested();
                            if (DoubaoCapability != capability) Log("doubao device: sync_enabled=" + capability.SyncState + " is_support_copy=" + capability.SupportsCopy);
                            DoubaoCapability = capability;
                            if (!capability.CanSync) throw new DoubaoDeviceUnavailableException(capability.SyncState, capability.SupportsCopy);
                        }
                        else await client.ConnectAsync(stop.Token);
                        // What was on the service before connecting is not pasted here: only later copies are.
                        lastId = await client.PeekAsync(stop.Token);
                        stop.Token.ThrowIfCancellationRequested();
                        initialized = true; failures = 0;
                        Report(doubao, Loc.T("已连接", "Connected"), SyncLevel.Active);
                    }
                    // File work runs on this worker's one control session. A second client with the same account made
                    // the server move the session key and the phone kept the previous device busy, so every send raced
                    // with a 400 and the next invitation was refused.
                    if (!doubao && client is WeTypeSyncClient weTypeSession)
                    {
                        while (_weTypeJobs.TryDequeue(out var job)) await job(weTypeSession);
                    }
                    if (_pending.Remove(doubao, out var copy))
                    {
                        sending = true;
                        await SendAsync(client, doubao, copy, stop.Token);
                        sending = false;
                    }
                    uint sequence = ClipboardWatch.GetClipboardSequenceNumber();
                    var remote = await client.ReceiveAsync(lastId, stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                    if (client is WeTypeSyncClient weTypeCache) _cachedWeTypePeers = weTypeCache.LastPeers;
                    // The same thing again from the same service is already on the clipboard.
                    if (remote is not null && Signature(remote) is var signature && signature == lastSignature) { lastId = remote.Id; remote = null; }
                    else if (remote is not null) lastSignature = Signature(remote);
                    if (remote is not null)
                    {
                        Log((doubao ? "doubao" : "wetype") + " received " + (remote.Image is { } bytes ? "image " + bytes.Length + " bytes" : "text " + remote.Text.Length + " chars") + " id=" + remote.Id);
                        lastId = remote.Id;
                        // Another device's copy is now the latest there: what was sent before may be sent again.
                        _lastSent.Remove(doubao);
                        await AcceptAsync(remote, source, doubao, sequence, stop.Token);
                    }
                    if (!doubao && client is WeTypeSyncClient weType && weType.TakePendingInvite() is { } invite)
                    {
                        // The service keeps every unconsumed invitation (per the protocol) and hands it back on each
                        // group request. Anything sent before this run started is a stale leftover from a phone that will
                        // never connect now, so drop it instead of opening a receiver and waiting vainly for 90 s.
                        if (invite.SendTime < _startedUtc)
                        {
                            Log("wetype file: pending invite from before startup dropped (stale). sendTime=" + invite.SendTime
                                + " started=" + _startedUtc + " code=" + (invite.Code.Length <= 6 ? invite.Code : invite.Code[..6] + "…"));
                            continue;
                        }
                        // An invitation is handled once. Retrying after the phone cancels it only produced 400s and
                        // broke the clipboard connection while the installed WeType accepted the same file.
                        foreach (var stale in _fileInvites.Where(pair => pair.Value < DateTime.UtcNow).Select(pair => pair.Key).ToList())
                            _fileInvites.TryRemove(stale, out _);
                        bool handled = _fileInvites.ContainsKey(invite.Code);
                        Log("wetype file: pending invite read -> code=" + (invite.Code.Length <= 6 ? invite.Code : invite.Code[..6] + "…")
                            + " senderUin=" + invite.SenderUin + " sender=" + (invite.SenderName.Length > 0 ? invite.SenderName : "?")
                            + " platform=" + invite.Platform + " alreadyHandled=" + handled);
                        if (handled)
                        {
                            Log("wetype file: pending invite ignored (processed within 10 min).");
                        }
                        else if (_fileInvites.TryAdd(invite.Code, DateTime.UtcNow.AddMinutes(10)))
                        {
                            _ = ReceiveWeTypeFileAsync(invite, weType, stop.Token);
                        }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (DoubaoDeviceUnavailableException ex)
                {
                    // The switch is on the account: once another device turns this one on, syncing starts by itself.
                    DoubaoCapability = new(ex.SyncState, ex.SupportsCopy); initialized = false; wait = 15;
                    Report(true, Loc.T("此设备的互传开关未开启", "Sync is turned off for this device"), SyncLevel.Attention);
                }
                catch (WeTypeUnpairedException)
                {
                    initialized = false; wait = 30;
                    if (Accounts.WeType.PeerNames.Count > 0)
                    {
                        Accounts.WeType.PeerNames = [];
                        try { Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
                    }
                    Report(false, Loc.T("已被其他设备解除关联", "Removed by another device"), SyncLevel.Attention);
                }
                catch (DoubaoRequestException ex)
                {
                    initialized = false; wait = 30;
                    Report(true, Loc.T("豆包拒绝了请求（错误码 ", "Doubao refused the request (code ") + ex.Code + Loc.T("）", ")"), SyncLevel.Attention);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log((doubao ? "doubao" : "wetype") + (sending ? " send failed: " : " failed: ") + Describe(ex));
                    failures = Math.Min(failures + 1, 5); initialized = false; wait = Math.Min(60, 3 * (1 << failures));
                    Report(doubao, sending ? Loc.T("发送失败，请重新复制", "Not sent. Copy again to retry")
                        : Loc.T("连接中断，正在重试…", "Connection lost. Retrying…"), SyncLevel.Attention);
                }
                sending = false;
                using var pause = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, _wake.Token);
                try { await Task.Delay(TimeSpan.FromSeconds(wait), pause.Token); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    // The addresses change several times as a network comes up; the retry follows the last of them.
                    failures = 0;
                    await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_workers.TryGetValue(doubao, out var current) && ReferenceEquals(current, stop))
            {
                _workers.Remove(doubao);
                _pending.Remove(doubao);
                ReleaseIdleWatcher();
            }
            stop.Dispose();
        }
    }

    private async Task SendAsync(IClipboardSyncClient client, bool doubao, ClipCopy copy, CancellationToken ct)
    {
        if (copy.Png is not { } png)
        {
            // Doubao carries the formatting too; a formatted copy it refuses goes as plain text.
            bool rich = false;
            if (client is DoubaoSyncClient account && copy.Html.Length > 0 && ClipboardHtml.Fragment(copy.Html) is { Length: > 0 and <= DoubaoSyncClient.MaxHtmlChars } fragment)
            {
                try { await account.SendRichAsync(copy.Text, fragment, ct); rich = true; }
                catch (Exception ex) when (ex is DoubaoRequestException or InvalidDataException) { Log("doubao formatted text refused: " + Describe(ex)); }
            }
            if (!rich) await client.SendAsync(copy.Text, ct);
            _lastSent[doubao] = Hash(copy.Text);
            Report(doubao, At(Loc.T("已发送文字", "Text sent")), SyncLevel.Active);
            return;
        }
        if (!client.SupportsImages) return;
        try
        {
            int limit = doubao ? DoubaoSyncClient.MaxImageBytes : SyncTransport.MaxImageBytes;
            var picture = await Task.Run(() => ShelfService.EncodeForSync(png, limit), ct) ?? throw new SyncImageTooLargeException();
            await client.SendImageAsync(picture, copy.PixelWidth, copy.PixelHeight, ct);
            _lastSent[doubao] = HashImage(png);
            Report(doubao, At(Loc.T("已发送图片", "Image sent")), SyncLevel.Active);
        }
        catch (SyncImageTooLargeException ex)
        {
            // The service's own threshold, when it named one, tells which pictures would go through.
            string size = ex.Limit > 0 ? Loc.T($"（上限 {ex.Limit / 1024} KB）", $" (limit {ex.Limit / 1024} KB)") : "";
            Report(doubao, Loc.T("图片过大，未发送", "Image too large to send") + size, SyncLevel.Active);
        }
    }

    private async Task AcceptAsync(RemoteClip remote, string source, bool doubao, uint sequence, CancellationToken ct)
    {
        try
        {
            if (remote.Image is { } encoded)
            {
                var (png, image) = await Task.Run(() => ShelfService.DecodeRemotePicture(encoded), ct);
                ct.ThrowIfCancellationRequested();
                // A copy made here while the picture was on its way stays on the clipboard.
                if (sequence != ClipboardWatch.GetClipboardSequenceNumber() || _pending.ContainsKey(doubao) || !AcceptImage(png, image, source)) return;
                Report(doubao, At(Loc.T("已接收图片", "Image received")), SyncLevel.Active);
            }
            else
            {
                if (sequence != ClipboardWatch.GetClipboardSequenceNumber() || _pending.ContainsKey(doubao)) return;
                AcceptText(remote.Text, source, remote.Html);
                Report(doubao, At(Loc.T("已接收文字", "Text received")), SyncLevel.Active);
            }
        }
        // The clipboard is held by another application, or the picture cannot be read: this one copy is skipped.
        catch (Exception ex) when (ex is ExternalException or IOException or InvalidDataException or FormatException or NotSupportedException
            or ArgumentException or InvalidOperationException or OverflowException) { Log((doubao ? "doubao" : "wetype") + " not accepted: " + Describe(ex)); }
    }

    // Progress of an incoming file, for the transfer card on the clipboard page; raised from the sync worker.
    public event Action<FileTransferUpdate>? IncomingFileTransfer;

    // Sends files, or folders as a zip, to a paired WeType device. A folder is archived beside the temporary folder and
    // removed afterwards; the other device receives one ordinary file.
    public Task SendWeTypeFilesAsync(IReadOnlyList<string> paths) => SendWeTypeFilesAsync(paths, null, null);

    // Where files went last time; null before the first send. A new drop reuses it while it is still paired.
    public ulong? LastFilePeer
    {
        get => Accounts.FilePeerUin;
        set
        {
            if (Accounts.FilePeerUin == value) return;
            Accounts.FilePeerUin = value;
            Save();
        }
    }

    public async Task SendWeTypeFilesAsync(IReadOnlyList<string> paths, ulong? peerUin, Action<FileTransferUpdate>? update)
    {
        if (paths.Count == 0) return;
        if (!Connected(false))
        {
            Report(false, Loc.T("未关联微信输入法", "WeType is not linked"), SyncLevel.Attention);
            update?.Invoke(new FileTransferUpdate(FileTransferPhase.Failed, Loc.T("无法发送", "Cannot send"),
                Loc.T("未关联微信输入法", "WeType is not linked"), 0, ""));
            return;
        }
        if (Interlocked.Exchange(ref _fileSending, 1) != 0)
        {
            Log("wetype file: a send is already running");
            update?.Invoke(new FileTransferUpdate(FileTransferPhase.Failed, Loc.T("发送失败", "Send failed"),
                Loc.T("已有发送任务正在进行", "A send is already running"), 0, ""));
            return;
        }
        var ct = _app.Lifetime.Token;
        try
        {
            // Archiving, hashing and waiting for acknowledgements stay off the interface thread.
            EnsureWeTypeWorker();
            await RunWeTypeJobAsync(async control =>
            {
                await SendWeTypeFilesCoreAsync(paths, peerUin, update, control, ct);
                return true;
            }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log("wetype file send failed: " + Describe(ex));
            Report(false, Loc.T("文件发送失败：", "File send failed: ") + ex.Message, SyncLevel.Attention);
        }
        finally { Interlocked.Exchange(ref _fileSending, 0); }
    }

    // Connects to the chosen device: opens the file session (the phone confirms) without sending anything. Sending is a
    // separate event, opened by the Send button or a Ctrl+drag on an already-open session.
    public async Task OpenWeTypeFileTransferAsync(ulong? peerUin, Action<FileTransferUpdate>? update)
    {
        // The moment Connect is pressed, surfaced in the debug console so a "no reaction" is locatable at once.
        Log("wetype connect: requested, uin=" + (peerUin?.ToString() ?? "auto"));
        if (!Connected(false))
        {
            update?.Invoke(new FileTransferUpdate(FileTransferPhase.Failed, Loc.T("无法连接", "Cannot connect"),
                Loc.T("未关联微信输入法", "WeType is not linked"), 0, ""));
            return;
        }
        if (Interlocked.Exchange(ref _fileSending, 1) != 0)
        {
            update?.Invoke(new FileTransferUpdate(FileTransferPhase.Failed, Loc.T("连接失败", "Connect failed"),
                Loc.T("已有传输任务正在进行", "A transfer is already running"), 0, ""));
            return;
        }
        CancelConnectingFile();
        var connectCts = new CancellationTokenSource();
        _fileConnectCts = connectCts;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token, connectCts.Token);
        var ct = linked.Token;
        try
        {
            EnsureWeTypeWorker();
            await RunWeTypeJobAsync(async control =>
            {
                await OpenWeTypeFileCoreAsync(peerUin, update, control, ct);
                return true;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            // A switch of device cancelled the pending attempt. If no session was built yet, go back to idle so the next
            // Connect starts clean; otherwise the already-open session stays and is closed by the caller.
            if (_openSentFile is null) FileConnectionStatus = "未连接";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log("wetype file connect failed: " + Describe(ex));
            update?.Invoke(new FileTransferUpdate(FileTransferPhase.Failed, Loc.T("连接失败", "Connect failed"), ex.Message, 0, ""));
        }
        finally { Interlocked.Exchange(ref _fileSending, 0); }
    }

    private async Task OpenWeTypeFileCoreAsync(ulong? peerUin, Action<FileTransferUpdate>? update,
        WeTypeSyncClient control, CancellationToken ct)
    {
        var debug = new StringBuilder();
        void Send(FileTransferPhase phase, string title, string detail, double fraction) =>
            update?.Invoke(new FileTransferUpdate(phase, title, detail, fraction, debug.ToString().TrimEnd()));
        try
        {
            await _fileClosing;
            FileConnectionStatus = "正在连接";
            Send(FileTransferPhase.Preparing, Loc.T("正在连接", "Connecting"), Loc.T("正在建立文件会话…", "Opening a file session…"), 0);
            var peers = await control.ListPeersAsync(ct);
            debug.AppendLine("设备列表：" + string.Join("；", peers.Select(item => item.Name + "（Uin " + item.Uin + "）")));
            var peer = peerUin is { } chosen
                ? peers.FirstOrDefault(item => item.Uin == chosen) ?? throw new InvalidDataException("The chosen device is gone.")
                : peers.FirstOrDefault(item => item.Platform == 1) ?? peers.FirstOrDefault()
                  ?? throw new InvalidDataException("No paired WeType device.");
            debug.AppendLine("目标：" + peer.Name + "（Uin " + peer.Uin + "）");
            if (_openSentFile is { } previous && (!previous.IsOpen || previous.PeerUin != peer.Uin)) await CloseOpenFileTransferAsync();
            Send(FileTransferPhase.Waiting, Loc.T("等待对方确认", "Waiting"), Loc.T($"目标 {peer.Name}，请在对方设备上确认连接", $"Target {peer.Name}; confirm the connection on the phone"), 0);
            var identity = new WeTypeFileIdentity();
            try
            {
                string source = peer.Name;
                string receiveFolder = Path.Combine(Folder, "incoming");
                Directory.CreateDirectory(receiveFolder);
                var sent = await WeTypeFileSender.OpenAsync(control, identity, peer.Uin, line => { debug.AppendLine(line); Log("wetype connect: " + line); },
                    receiveFolder,
                    // The same session also receives files the phone pushes back; reflect them on the transfer board.
                    receiveProgress: progress =>
                    {
                        var progressSnapshot = progress;
                        _ = _app.Dispatcher.BeginInvoke(() =>
                        {
                            FileConnectionStatus = Loc.T("已连接 · 接收中 " + source, "Connected · receiving " + source);
                            Report(false, Loc.T($"正在接收 · {progressSnapshot.Name} {progressSnapshot.Fraction:P0}",
                                $"Receiving · {progressSnapshot.Name} {progressSnapshot.Fraction:P0}"), SyncLevel.Active);
                            Send(FileTransferPhase.Receiving, Loc.T("正在接收文件", "Receiving") + " · " + source,
                                progressSnapshot.Name + " · " + Size(progressSnapshot.Transferred) + " / " + Size(progressSnapshot.Total),
                                progressSnapshot.Fraction);
                        });
                    },
                    receiveCompleted: file => HandleFileSessionReceived(file, source),
                    receiveTrace: line => { debug.AppendLine(line); Log("wetype file: " + line); },
                    ct);
                lock (_openSentFileGate)
                {
                    _openSentFile = sent; _openFileIdentity = identity;
                    FileConnectionStatus = "已连接";
                }
            }
            catch { identity.Dispose(); throw; }
            LastFilePeer = peer.Uin;
            Send(FileTransferPhase.Waiting, Loc.T("已连接", "Connected"), Loc.T($"已连接 {peer.Name}，可以发送文件", $"Connected to {peer.Name}; ready to send"), 0);
        }
        catch (Exception error)
        {
            debug.AppendLine("失败：" + Describe(error));
            Log("wetype connect failed: " + Describe(error));
            string detail = error.Message.Contains("No file endpoints")
                ? Loc.T("手机没有公布局域网地址。请让手机和电脑连接同一个 Wi-Fi，再重试。", "The phone exposed no LAN address. Connect both to the same Wi-Fi and retry.")
                : error.Message;
            Send(FileTransferPhase.Failed, Loc.T("连接失败", "Connect failed"), detail, 0);
            throw;
        }
    }

    // A file the phone pushes back over the connected (two-way) session: commit it to the board like any receive.
    private void HandleFileSessionReceived(WeTypeReceivedFile file, string source)
    {
        _ = _app.Dispatcher.BeginInvoke(() =>
        {
            Report(false, At(Loc.T($"已接收 {file.Name}", $"Received {file.Name}")), SyncLevel.Active);
            FileConnectionStatus = Loc.T("已连接 · 已接收文件", "Connected · received");
            _app.Shelf.AddFiles([file.Path], null);
            IncomingFileTransfer?.Invoke(new FileTransferUpdate(FileTransferPhase.Completed,
                Loc.T("接收完成", "Received"), file.Name + " · " + Size(file.Bytes), 1, ""));
            Log("wetype file session received: \"" + file.Name + "\" " + file.Bytes + " bytes from " + source);
        });
    }

    // The paired devices the sync worker last read; the transfer view opens with these instead of waiting for its own.
    public IReadOnlyList<WeTypePeer> CachedWeTypePeers => _cachedWeTypePeers;

    // The devices in the paired group, for choosing where a file goes.
    public Task<IReadOnlyList<WeTypePeer>> WeTypePeersAsync(CancellationToken ct)
    {
        if (!Connected(false)) return Task.FromResult<IReadOnlyList<WeTypePeer>>([]);
        EnsureWeTypeWorker();
        return RunWeTypeJobAsync(control => control.ListPeersAsync(ct), ct);
    }

    // One control session for the worker and every file operation: the service binds a session key to the account, so
    // two clients made it invalidate the other's key and left the phone seeing a busy device.
    private Task<T> RunWeTypeJobAsync<T>(Func<WeTypeSyncClient, Task<T>> job, CancellationToken ct)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => done.TrySetCanceled(ct));
        _weTypeJobs.Enqueue(async control =>
        {
            try { done.TrySetResult(await job(control)); }
            catch (OperationCanceledException error) { done.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { done.TrySetException(error); }
        });
        try { _wake.Cancel(); }
        catch (ObjectDisposedException) { }
        return done.Task;
    }

    // A previous session close (await sent.CloseAsync) can hang. Connections/sends await _fileClosing; cap that bound so a
    // stale close can't make Connect feel dead forever — proceed after a bound and let the close finish in the background.
    private async Task WaitClosedAsync(CancellationToken ct)
    {
        if (_fileClosing.IsCompleted) return;
        try { await _fileClosing.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { Log("wetype file: previous close not finishing; continuing"); }
    }

    private void EnsureWeTypeWorker()
    {
        if (_workers.ContainsKey(false) || !Connected(false)) return;
        var stop = new CancellationTokenSource();
        _workers[false] = stop;
        _ = RunAsync(false, stop);
    }

    private async Task SendWeTypeFilesCoreAsync(IReadOnlyList<string> paths, ulong? peerUin, Action<FileTransferUpdate>? update,
        WeTypeSyncClient control, CancellationToken ct)
    {
        var debug = new StringBuilder();
        void Send(FileTransferPhase phase, string title, string detail, double fraction, int? fileIndex = null) =>
            update?.Invoke(new FileTransferUpdate(phase, title, detail, fraction, debug.ToString().TrimEnd(), fileIndex));
        try
        {
            await _fileClosing;
            FileConnectionStatus = "正在连接";
            Send(FileTransferPhase.Preparing, Loc.T("准备发送", "Preparing"), Loc.T("正在连接微信输入法…", "Connecting to WeType…"), 0);
            var peers = await control.ListPeersAsync(ct);
            debug.AppendLine("设备列表：" + string.Join("；", peers.Select(item => item.Name + "（Uin " + item.Uin + "，platform " + item.Platform + "，文件 " + (item.SupportsFileTransfer ? "支持" : "未知") + "）")));
            var peer = peerUin is { } chosen
                ? peers.FirstOrDefault(item => item.Uin == chosen) ?? throw new InvalidDataException("The chosen device is gone.")
                : peers.FirstOrDefault(item => item.Platform == 1 && item.SupportsFileTransfer)
                  ?? peers.FirstOrDefault(item => item.Platform == 1)
                  ?? peers.FirstOrDefault(item => item.SupportsFileTransfer)
                  ?? peers.FirstOrDefault() ?? throw new InvalidDataException("No paired WeType device.");
            debug.AppendLine("目标：" + peer.Name + "（Uin " + peer.Uin + "，platform " + peer.Platform + "）");
            Send(FileTransferPhase.Waiting, Loc.T("等待对方确认", "Waiting"), Loc.T($"目标 {peer.Name}，请在对方设备上确认接收", $"Target {peer.Name}; confirm on the other device"), 0);
            // Reuse the connected peer; switching devices or a closed stream needs a new invitation.
            if (_openSentFile is { } previous && (!previous.IsOpen || previous.PeerUin != peer.Uin)) await CloseOpenFileTransferAsync();
            // Prepare every item first (folders become zips), then send them all in one session.
            var items = new List<string>();
            var cleanups = new List<string>();
            try
            {
                for (int index = 0; index < paths.Count; index++)
                {
                    if (!Directory.Exists(paths[index])) { items.Add(paths[index]); continue; }
                    Send(FileTransferPhase.Preparing, Loc.T("正在打包文件夹", "Archiving"), new DirectoryInfo(paths[index]).Name, 0);
                    (string path, string folder) = ArchiveFolder(paths[index]);
                    items.Add(path);
                    cleanups.Add(folder);
                }
                async Task<WeTypeFileSender.SentFile> SendBatch(Action<WeTypeFileProgress> progress,
                    Action<int, string, long, long> fileDone, Action<string> trace)
                {
                    // Sending is only uploading: the session was opened by the Connect event, so never auto-connect here.
                    if (_openSentFile is { IsOpen: true } existing && existing.PeerUin == peer.Uin)
                    {
                        trace("复用已连接设备，无需再次邀请。");
                        await existing.SendAsync(items, progress, fileDone, trace, ct);
                        return existing;
                    }
                    throw new InvalidOperationException(Loc.T("尚未建立会话，请先点击“连接”。", "Open the session with Connect first."));
                }
                var sent = await SendBatch(progress =>
                {
                    double fraction = progress.Total > 0 ? progress.Transferred / (double)progress.Total : 1;
                    Report(false, Loc.T($"正在发送到 {peer.Name} · {progress.Name} {fraction:P0}",
                        $"Sending to {peer.Name} · {progress.Name} {fraction:P0}"), SyncLevel.Active);
                    Send(FileTransferPhase.Sending, Loc.T("正在发送到 ", "Sending to ") + peer.Name,
                        progress.Name + " · " + Size(progress.Transferred) + " / " + Size(progress.Total), fraction);
                }, (int index, string name, long transferred, long total) =>
                {
                    double fraction = total > 0 ? transferred / (double)total : 1;
                    Send(FileTransferPhase.Sending, Loc.T("发送完成", "Sent") + " · " + name,
                        Size(transferred) + " / " + Size(total), fraction, index);
                    debug.AppendLine("已发送 " + (index + 1) + "/" + items.Count + "：" + name + "，" + Size(transferred) + " / " + Size(total));
                    Log("wetype file sent: \"" + name + "\"");
                }, line => { debug.AppendLine(line); Log("wetype file: " + line); });
                // The whole batch is one session: it stays open instead of closing, so the phone keeps its receive slot
                // until the idle timeout, an explicit close, a peer disconnect, or program exit.
                await HoldSentFileAsync(sent);
                // A device that took a file is the one a later drop goes to without asking again.
                LastFilePeer = peer.Uin;
                debug.AppendLine("已发送 " + items.Count + " 个文件，共 " + sent.Bytes + " 字节");
                Report(false, At(Loc.T($"已发送 {items.Count} 个文件", $"Sent {items.Count} file(s)")), SyncLevel.Active);
            }
            finally
            {
                foreach (var folder in cleanups)
                {
                    try { Directory.Delete(folder, true); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            Send(FileTransferPhase.Completed, Loc.T("全部发送完成", "All sent"),
                Loc.T($"已发送 {paths.Count} 个文件到 {peer.Name}", $"Sent {paths.Count} file(s) to {peer.Name}"), 1);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            debug.AppendLine("失败：" + Describe(error));
            string detail = error.Message.Contains("No file endpoints")
                ? Loc.T("手机没有公布局域网地址（返回里只有 awdl/wfd）。请让手机和电脑连接同一个 Wi-Fi，再重新发送。",
                    "The phone exposed only awdl/wfd and no LAN address. Connect both devices to the same Wi-Fi and retry.")
                : error.Message;
            Send(FileTransferPhase.Failed, Loc.T("发送失败", "Send failed"), detail, 0);
            throw;
        }
    }

    // Keep the completed batch connected so a later drop reuses the same session; the session closes only on an explicit
    // close, a peer disconnect, or program exit (never on an idle timer).
    private Task HoldSentFileAsync(WeTypeFileSender.SentFile sent)
    {
        lock (_openSentFileGate)
        {
            _openSentFile = sent;
            FileConnectionStatus = "已连接";
        }
        return Task.CompletedTask;
    }

    public Task CloseOpenFileTransferAsync([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        lock (_openSentFileGate)
        {
            if (_openSentFile is not { } sent) return _fileClosing;
            Log("wetype file: local session close requested by " + caller);
            var identity = _openFileIdentity;
            _openSentFile = null; _openFileIdentity = null;
            FileConnectionStatus = "正在断开 · 等待对端确认";
            return _fileClosing = CloseFileSessionAsync(sent, identity);
        }
    }

    private async Task CloseFileSessionAsync(WeTypeFileSender.SentFile sent, WeTypeFileIdentity? identity)
    {
        try
        {
            await sent.CloseAsync();
            // 断开只显示「已断开」；对端是否确认不再单独提示。
            FileConnectionStatus = "已断开";
            Log("wetype file: " + FileConnectionStatus);
        }
        finally { identity?.Dispose(); }
    }
    private static string Size(long bytes) => bytes >= 1 << 30 ? (bytes / (double)(1 << 30)).ToString("0.0") + " GiB"
        : bytes >= 1 << 20 ? (bytes / (double)(1 << 20)).ToString("0.0") + " MiB"
        : bytes >= 1 << 10 ? (bytes / (double)(1 << 10)).ToString("0.0") + " KiB" : bytes + " B";

    private static (string Zip, string Folder) ArchiveFolder(string directory)
    {
        string folder = Path.Combine(Path.GetTempPath(), "BrimDeck-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string name = new DirectoryInfo(directory).Name;
        if (name.Length == 0) name = "folder";
        string zip = Path.Combine(folder, name + ".zip");
        try
        {
            ZipFile.CreateFromDirectory(directory, zip, CompressionLevel.Fastest, true);
            if (new FileInfo(zip).Length > WeTypeFileSender.MaxBytes)
                throw new InvalidDataException("The folder is larger than 1 GiB.");
            return (zip, folder);
        }
        catch
        {
            try { Directory.Delete(folder, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // Accepts one file invitation in the background: joining is the acceptance, and the file goes to the file transfer
    // column when it has arrived and matched its identifier.
    private async Task ReceiveWeTypeFileAsync(WeTypeFileInvite invite, WeTypeSyncClient control, CancellationToken ct)
    {
        string source = invite.SenderName.Length > 0 ? invite.SenderName : Loc.T("另一台设备", "Another device");
        var debug = new StringBuilder();
        debug.AppendLine("来自：" + source + "（Uin " + invite.SenderUin + "，platform " + invite.Platform + "）");
        debug.AppendLine("邀请码：" + (invite.Code.Length <= 6 ? invite.Code : invite.Code[..6] + "…"));
        // The board is only brought forward when the phone really connects; a stale invitation the service keeps
        // (one never consumed) must not open the board again on every launch.
        bool didExpand = false;
        void Send(FileTransferPhase phase, string title, string detail, double fraction) =>
            IncomingFileTransfer?.Invoke(new FileTransferUpdate(phase, title, detail, fraction, debug.ToString().TrimEnd(), null, didExpand));
        try
        {
            Log("wetype file invite: \"" + source + "\" uin=" + invite.SenderUin + " platform=" + invite.Platform);
            Send(FileTransferPhase.Receiving, Loc.T("正在接收文件", "Receiving"), Loc.T("来自 " + source, "From " + source), 0);
            string folder = Path.Combine(Folder, "incoming");
            Directory.CreateDirectory(folder);
            Report(false, Loc.T($"正在接收来自 {source} 的文件…", $"Receiving a file from {source}…"), SyncLevel.Active);
            // The phone may open the stream and then end it without sending a file (e.g. it abandoned the transfer).
            // When the peer sends its disconnect notice the callback below wins Task.WhenAny and we stop right away.
            var endedByPeer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var receiver = await WeTypeFileReceiver.StartAsync(control, invite, Accounts.WeType.DeviceName, folder, progress =>
            {
                Report(false, Loc.T($"正在接收 · {progress.Name} {progress.Fraction:P0}",
                    $"Receiving · {progress.Name} {progress.Fraction:P0}"), SyncLevel.Active);
                Send(FileTransferPhase.Receiving, Loc.T("正在接收文件", "Receiving"),
                    progress.Name + " · " + Size(progress.Transferred) + " / " + Size(progress.Total), progress.Fraction);
            }, line => { debug.AppendLine(line); Log("wetype file: " + line); },
            // The phone opened the file stream: now (and only now) bring the board forward and light the link.
            _ =>
            {
                ReceiveConnectionStatus = Loc.T("已连接 · " + source, "Connected · " + source);
                didExpand = true; Send(FileTransferPhase.Receiving, Loc.T("手机已连接 · " + source, "Phone connected · " + source),
                Loc.T("正在等待文件数据…", "Waiting for the file data…"), 0);
                Log("wetype file receive: phone CONNECTED (TransferFiles entered), opening board. sender=" + source + " uin=" + invite.SenderUin);
            },
            // The phone actively ended the stream (kind-200 command=1001): reflect that on the board and stop waiting.
            _ =>
            {
                ReceiveConnectionStatus = Loc.T("已断开 · 手机结束传输", "Disconnected · phone ended");
                Send(FileTransferPhase.Completed, Loc.T("已断开", "Disconnected"),
                    Loc.T("手机结束了这次传输。", "The phone ended the transfer."), 1);
                Log("wetype file receive: peer disconnected (command=1001). sender=" + source);
                endedByPeer.TrySetResult(true);
            }, ct);
            debug.AppendLine("监听端口：" + receiver.Port);
            Log("wetype file receive: listening on port " + receiver.Port + " (joined invitation " + (invite.Code.Length <= 6 ? invite.Code : invite.Code[..6] + "…") + "), waiting for phone to connect");
            Send(FileTransferPhase.Receiving, Loc.T("等待手机连接", "Waiting"), Loc.T("端口 " + receiver.Port + "，请确认手机与电脑在同一局域网", "Port " + receiver.Port + "; both devices must share a LAN"), 0);
            void Adopt(WeTypeFileSender.SentFile session)
            {

                lock (_openSentFileGate) { _openSentFile = session; FileConnectionStatus = "已连接"; }
                LastFilePeer = invite.SenderUin;
            }
            receiver.FileReceived = file => HandleFileSessionReceived(file, source);
            receiver.SessionOpened = Adopt;
            if (receiver.Session is { } active) Adopt(active);
            await endedByPeer.Task.WaitAsync(ct);        }
        catch (WeTypeFileCanceledException canceled)
        {
            // The installed WeType on this PC accepted the invitation first, so the phone dropped this join. That is not
            // an error for the person, and the file is already on the clipboard device they normally use.
            Log("wetype file invite handled by another device: \"" + source + "\" " + canceled.Message);
            ReceiveConnectionStatus = Loc.T("已断开 · 已接收", "Disconnected · received");
            Report(false, At(Loc.T("文件已由本机微信输入法接收", "Received by the installed WeType")), SyncLevel.Active);
            Send(FileTransferPhase.Completed, Loc.T("已由本机微信输入法接收", "Handled by the installed WeType"),
                Loc.T("另一台客户端先接受了这次传输，BrimDeck 没有重复接收。", "Another client accepted first; BrimDeck did not duplicate it."), 1);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            debug.AppendLine("失败：" + Describe(ex));
            Log("wetype file receive failed: " + Describe(ex));
            ReceiveConnectionStatus = Loc.T("已断开 · 接收失败", "Disconnected · failed");
            Report(false, Loc.T("文件接收失败：", "File receive failed: ") + ex.Message, SyncLevel.Attention);
            Send(FileTransferPhase.Failed, Loc.T("接收失败", "Receive failed"), ex.Message, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= NetworkChanged;
        _ = CloseOpenFileTransferAsync();
        StopLan();
        foreach (var source in _workers.Values) source.Cancel();
        _workers.Clear(); _pending.Clear(); _watch?.Dispose(); _watch = null;
    }
}

public enum FileTransferPhase { Preparing, Waiting, Sending, Receiving, Completed, Failed }

// One step of a file transfer, for the transfer card: what is happening, how far it has come, and the protocol
// details that tell afterwards which part did not work. FileIndex is which file of the batch a Sending/Completed step
// refers to, so one row can turn green as soon as it is done; Fraction always covers the whole batch.
public sealed record FileTransferUpdate(FileTransferPhase Phase, string Title, string Detail, double Fraction, string Debug, int? FileIndex = null, bool Expand = true);


