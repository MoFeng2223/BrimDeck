using System.Net;
using Blake3.Managed;
using BrimDeck.Core.ClipboardSync;

namespace BrimDeck.Core.FileTransfer;

// Opens a reusable file session with a paired device and uploads the first batch
// over one authenticated stream. Later batches use the returned session; each range is acknowledged.
public static class WeTypeFileSender
{
    public const long MaxBytes = 1024L * 1024 * 1024;

    // Opens the file session with the phone—the other machine confirms and the stream opens—without sending any file.
    // Sending is a later, independent event on the same session: the Connect button opens, the Send button uploads.
    public static async Task<SentFile> OpenAsync(WeTypeSyncClient control, WeTypeFileIdentity identity,
        ulong peerUin, Action<string>? trace,
        string? receiveFolder, Action<WeTypeFileProgress>? receiveProgress,
        Action<WeTypeReceivedFile>? receiveCompleted, Action<string>? receiveTrace,
        CancellationToken ct)
    {
        var lan = WeTypeFileNetwork.LanAddresses();
        if (lan.Count == 0) throw new InvalidDataException("No LAN address to offer.");
        trace?.Invoke("本机地址：" + string.Join(", ", lan));

        var offer = await control.InviteFileAsync(peerUin, System.Text.Encoding.ASCII.GetString(identity.CaPem),
            WeTypeFileNetwork.LinkJson(lan, control.DeviceName, preferredClient: true), ct);
        trace?.Invoke("会话邀请码：" + Mask(offer.Code) + "，有效期 " + offer.Validity.TotalSeconds.ToString("0") + " 秒");

        // 等对端(手机)公布本次会话的监听端点。
        var deadline = DateTime.UtcNow + (offer.Validity < TimeSpan.FromSeconds(120) ? offer.Validity : TimeSpan.FromSeconds(120));
        string? lastGrpc = null, serverName = null;
        IReadOnlyList<(IPAddress Address, int Port)>? endpoints = null;
        WeTypeFilePeerInfo? lastPeer = null;
        while (DateTime.UtcNow < deadline)
        {
            lastPeer = await control.PollFilePeerAsync(offer.Code, peerUin, ct);
            if (lastPeer?.GrpcJson is { Length: > 0 } text && text != lastGrpc)
            {
                lastGrpc = text;
                trace?.Invoke("对端连接信息：" + (text.Length <= 600 ? text : text[..600] + "…"));
                if (WeTypeFileNetwork.TryReadGrpc(text, out string certName, out var parsed)) { serverName = certName; endpoints = parsed; break; }
            }
            await Task.Delay(500, ct);
        }
        if (lastPeer is null) throw new TimeoutException("The other device did not join the transfer.");
        if ((lastPeer.Capabilities & 2) == 0) throw new InvalidDataException("The other device cannot acknowledge ranges.");
        if (endpoints is null) throw new InvalidDataException("No file endpoints: " + (lastGrpc ?? "the other device published nothing"));
        if (serverName is not { Length: > 0 }) trace?.Invoke("对端未提供证书名，仅校验其 CA。");
        trace?.Invoke("对端端点：" + string.Join(", ", endpoints.Select(item => item.Address + ":" + item.Port)));
        using var authority = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
            System.Text.Encoding.UTF8.GetString(lastPeer.CaPem));

        foreach (var (address, port) in endpoints)
        {
            var channel = WeTypeFileGrpc.Connect(identity, authority, serverName ?? "", address, port);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await channel.ConnectAsync(timeout.Token);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                channel.Dispose();
                trace?.Invoke("端点连接失败：" + error.Message);
                continue;
            }
            catch { channel.Dispose(); throw; }
            // Once a stream is open, never replay a partly acknowledged batch on another endpoint.
            var sent = await UploadBatchAsync(channel, [], 0, null, null, trace,
                receiveFolder, receiveProgress, receiveCompleted, receiveTrace, ct);
            sent.PeerUin = peerUin;
            return sent;
        }
        throw new IOException("No file endpoints.");
    }
    private static string Mask(string code) => code.Length <= 6 ? code : code[..6] + "…";

    internal static async Task<SentFile> UploadAsync(Grpc.Net.Client.GrpcChannel channel, string id, string name, ulong size,
        string path, Action<WeTypeFileProgress>? progress, CancellationToken ct) =>
        await UploadBatchAsync(channel, [(id, name, size, path)], size, progress, null, null, null, null, null, null, ct);

    internal static async Task<SentFile> UploadBatchAsync(Grpc.Net.Client.GrpcChannel channel,
        IReadOnlyList<(string Id, string Name, ulong Size, string Path)> files, ulong totalSize,
        Action<WeTypeFileProgress>? progress, Action<int, string, long, long>? fileDone, Action<string>? trace,
        string? receiveFolder, Action<WeTypeFileProgress>? receiveProgress,
        Action<WeTypeReceivedFile>? receiveCompleted, Action<string>? receiveTrace,
        CancellationToken ct)
    {
        var session = new SentFile(channel, receiveFolder, receiveProgress, receiveCompleted, receiveTrace);
        try
        {
            await session.WriteAsync(new WeTypeFileChunk { Kind = 100 }, ct);
            await session.UploadAsync(files, totalSize, progress, fileDone, trace, ct);
            return session;
        }
        catch { await session.CloseAsync(); throw; }
    }

    // Owns one duplex stream across files and batches, including reading control traffic while idle.
    public sealed class SentFile : IAsyncDisposable
    {
        private readonly Grpc.Net.Client.GrpcChannel? _channel;
        private readonly Grpc.Core.AsyncDuplexStreamingCall<WeTypeFileChunk, WeTypeFileChunk>? _call;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _writes = new(1), _uploads = new(1);
        private readonly System.Threading.Channels.Channel<WeTypeFileChunk> _acks =
            System.Threading.Channels.Channel.CreateBounded<WeTypeFileChunk>(16);
        private readonly Task _reader;
        private readonly Grpc.Core.IAsyncStreamReader<WeTypeFileChunk> _input;
        private readonly Func<WeTypeFileChunk, Task> _output;
        public Task Completion => _reader;
        private int _closed;
        private readonly TaskCompletionSource _disconnectAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closeDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ulong _disconnectId = (ulong)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
        public bool DisconnectAcknowledged { get; private set; }
        private ulong _sequence;
        public ulong PeerUin { get; internal set; }
        public string Id { get; private set; } = "";
        public long Bytes { get; private set; }
        public bool IsOpen => Volatile.Read(ref _closed) == 0 && !_reader.IsCompleted;

        // The session is two-way: besides uploading, it also receives files the peer sends on the same duplex stream.
        private readonly string? _receiveFolder;
        private readonly Action<WeTypeFileProgress>? _receiveProgress;
        private readonly Action<WeTypeReceivedFile>? _receiveCompleted;
        private readonly Action<string>? _receiveTrace;
        private readonly object _receiveGate = new();
        private WeTypeFileIncoming? _incoming;

        internal SentFile(Grpc.Net.Client.GrpcChannel channel,
            string? receiveFolder, Action<WeTypeFileProgress>? receiveProgress,
            Action<WeTypeReceivedFile>? receiveCompleted, Action<string>? receiveTrace)
        {
            _channel = channel;
            _receiveFolder = receiveFolder;
            _receiveProgress = receiveProgress;
            _receiveCompleted = receiveCompleted;
            _receiveTrace = receiveTrace;
            _call = channel.CreateCallInvoker().AsyncDuplexStreamingCall(WeTypeFileGrpc.TransferFilesMethod, null,
                new Grpc.Core.CallOptions(cancellationToken: _stop.Token));
            _input = _call.ResponseStream;
            _output = chunk => _call.RequestStream.WriteAsync(chunk);
            _reader = ReadAsync();
        }

        internal SentFile(Grpc.Core.IAsyncStreamReader<WeTypeFileChunk> input,
            Grpc.Core.IServerStreamWriter<WeTypeFileChunk> output, string folder,
            Action<WeTypeFileProgress>? progress, Action<WeTypeReceivedFile> completed, Action<string>? trace)
        {
            _input = input; _output = chunk => output.WriteAsync(chunk);
            _receiveFolder = folder; _receiveProgress = progress;
            _receiveCompleted = completed; _receiveTrace = trace;
            _reader = ReadAsync();
        }
        // Braces the current incoming receive so the next file on the stream starts from a clean slate.
        private void DropIncoming()
        {
            WeTypeFileIncoming? current;
            lock (_receiveGate) { current = _incoming; _incoming = null; }
            current?.Dispose();
        }

        // A file the peer sends on this session commits into the download folder and hands over its handle.
        private async Task<(bool Complete, WeTypeReceivedFile? File)> HandlePeerFileAsync(WeTypeFileChunk message, CancellationToken ct)
        {
            WeTypeFileIncoming incoming;
            lock (_receiveGate)
            {
                incoming = _incoming ??= WeTypeFileIncoming.Create(_receiveFolder ?? throw new InvalidDataException("No download folder configured."),
                    message.File, _receiveProgress, _receiveTrace);
            }
            var (start, end, complete) = await incoming.AppendAsync(message, ct);
            if (!complete)
            {
                await WriteAsync(new WeTypeFileChunk { Kind = 5, File = new WeTypeFileInfo { Id = incoming.Id },
                    Start = start, End = end }, ct);
                return (false, null);
            }
            WeTypeReceivedFile file;
            lock (_receiveGate) { if (_incoming != incoming) throw new InvalidDataException("Stale receive."); file = incoming.Commit(); _incoming = null; }
            incoming.Dispose();
            await WriteAsync(new WeTypeFileChunk { Kind = 5, File = new WeTypeFileInfo { Id = file.Id },
                Start = start, End = end }, ct);
            _receiveTrace?.Invoke("文件接收完成：" + file.Name + "，" + file.Bytes + " 字节");
            return (true, file);
        }

        internal async Task WriteAsync(WeTypeFileChunk chunk, CancellationToken ct)
        {
            await _writes.WaitAsync(ct);
            try { await _output(chunk).WaitAsync(ct); }
            finally { _writes.Release(); }
        }

        private async Task ReadAsync()
        {
            Exception? failure = null;
            try
            {
                while (await _input.MoveNext(_stop.Token))
                {
                    var message = _input.Current;
                    switch (message.Kind)
                    {
                        case 1:
                            // The peer starts sending a file to us on this same session.
                            DropIncoming();
                            if (_receiveFolder is null) throw new InvalidDataException("No download folder configured.");
                            lock (_receiveGate)
                            {
                                _incoming = WeTypeFileIncoming.Create(_receiveFolder, message.File, _receiveProgress, _receiveTrace);
                            }
                            break;
                        case 2:
                            {
                                // The peer's file data (metadata may ride the first block): write it and ack the range.
                                var (complete, file) = await HandlePeerFileAsync(message, _stop.Token);
                                if (complete) _receiveCompleted?.Invoke(file!);
                                break;
                            }
                        case 3:
                            // The peer canceled the file it was sending to us.
                            DropIncoming();
                            _receiveTrace?.Invoke("对端取消了发送的文件。");
                            break;
                        case 200:

                            if (message.ControlId is null) throw new InvalidDataException("Missing control acknowledgement ID.");
                            await WriteAsync(new WeTypeFileChunk { Kind = 5, ControlId = message.ControlId }, _stop.Token);
                            if (message.Business is { } business && WeTypeFileControl.IsDisconnect(business)) return;
                            break;
                        case 5 when message.File is null && message.ControlId is { } controlId
                            && controlId.SequenceEqual(WeTypeFileControl.Varint(_disconnectId)):
                            _disconnectAck.TrySetResult();
                            break;
                        case 4:
                            throw new WeTypeFileCanceledException("The other device canceled the transfer.");
                        case 5 when message.File is not null:
                            await _acks.Writer.WriteAsync(message, _stop.Token);
                            break;
                    }
                }
                failure = new IOException("The file session closed.");
            }
            catch (Exception error)
            {
                failure = error;
                _receiveTrace?.Invoke("会话读取停止：" + error + "；本地主动关闭=" + (Volatile.Read(ref _closed) != 0));
            }
            finally { _acks.Writer.TryComplete(failure); DropIncoming(); }
        }

        public async Task SendAsync(IReadOnlyList<string> paths, Action<WeTypeFileProgress>? progress,
            Action<int, string, long, long>? fileDone, Action<string>? trace, CancellationToken ct)
        {
            if (paths.Count == 0) throw new InvalidDataException("No file to send.");
            var files = new List<(string Id, string Name, ulong Size, string Path)>();
            foreach (var path in paths)
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxBytes || !ValidName(info.Name)) throw new InvalidDataException("Invalid file.");
                files.Add((await HashAsync(path, ct), info.Name, (ulong)info.Length, path));
            }
            await UploadAsync(files, (ulong)files.Sum(file => (long)file.Size), progress, fileDone, trace, ct);
        }

        internal async Task UploadAsync(IReadOnlyList<(string Id, string Name, ulong Size, string Path)> files,
            ulong total, Action<WeTypeFileProgress>? progress, Action<int, string, long, long>? fileDone,
            Action<string>? trace, CancellationToken ct)
        {
            await _uploads.WaitAsync(ct);
            try
            {
                if (!IsOpen) throw new IOException("The file session is closed.");
                long done = 0;
                foreach (var (file, index) in files.Select((file, index) => (file, index)))
                {
                    var info = new WeTypeFileInfo { Id = file.Id, Name = file.Name, Size = file.Size };
                    trace?.Invoke("开始发送：" + file.Name + "，" + file.Size + " 字节");
                    await WriteAsync(new WeTypeFileChunk { Kind = 1, File = info, Sequence = _sequence++ }, ct);
                    await using var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        WeTypeFileGrpc.Block, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    byte[] buffer = new byte[WeTypeFileGrpc.Block];
                    ulong offset = 0;
                    do
                    {
                        int limit = (int)Math.Min((ulong)buffer.Length, file.Size - offset);
                        int read = await source.ReadAsync(buffer.AsMemory(0, limit), ct);
                        if (read == 0 && offset < file.Size) throw new IOException("The file became shorter.");
                        ulong end = offset + (ulong)read;
                        await WriteAsync(new WeTypeFileChunk { Kind = 2, File = info, Data = buffer[..read],
                            Start = offset, End = end, Sequence = _sequence++ }, ct);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(60));
                        WeTypeFileChunk ack;
                        try { ack = await _acks.Reader.ReadAsync(timeout.Token); }
                        catch (Exception error)
                        {
                            trace?.Invoke($"确认失败：文件={file.Name}，范围={offset}-{end}，总字节={file.Size}，本地关闭={Volatile.Read(ref _closed) != 0}，取消={ct.IsCancellationRequested}，错误={error}");
                            throw;
                        }
                        if (ack.File?.Id != file.Id || ack.Start != offset || ack.End != end)
                            throw new InvalidDataException("Unexpected acknowledgement.");
                        offset = end;
                        if (offset == file.Size || offset % (16UL * WeTypeFileGrpc.Block) == 0)
                            trace?.Invoke($"确认进度：{file.Name}，{offset}/{file.Size} 字节，UTC={DateTime.UtcNow:O}");
                        progress?.Invoke(new(done + (long)offset, (long)total, file.Name));
                    } while (offset < file.Size);
                    done += (long)file.Size;
                    Id = file.Id;
                    fileDone?.Invoke(index, file.Name, done, (long)total);
                }
                Bytes = done;
                trace?.Invoke("本次发送完成，连接保持，可继续发送文件。");
            }
            catch { await CloseAsync(); throw; }
            finally { _uploads.Release(); }
        }

        public async Task CloseAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) { await _closeDone.Task; return; }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await WriteAsync(new WeTypeFileChunk { Kind = 200, Business = WeTypeFileControl.Disconnect(_disconnectId), Field7 = 0,
                    ControlId = WeTypeFileControl.Varint(_disconnectId) }, timeout.Token);
                await _disconnectAck.Task.WaitAsync(timeout.Token);
                DisconnectAcknowledged = true;
                if (_call is not null) await _call.RequestStream.CompleteAsync().WaitAsync(timeout.Token);
            }
            catch (Exception) { }
            finally
            {
                _stop.Cancel(); _call?.Dispose(); _channel?.Dispose();
                await _reader;
                _closeDone.TrySetResult();
            }
        }
        public ValueTask DisposeAsync() => new(CloseAsync());
    }
    public static bool ValidName(string name) =>
        name.Length is > 0 and <= 240 && System.Text.Encoding.UTF8.GetByteCount(name) <= 240 &&
        name != "." && name != ".." && !name.Any(c => c is '/' or '\\' or ':' or '\0');

    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, WeTypeFileGrpc.Block,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = Hasher.New();
        byte[] buffer = new byte[WeTypeFileGrpc.Block];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) hasher.Update(buffer.AsSpan(0, read));
        return Convert.ToHexString(hasher.Finalize().AsSpan()).ToLowerInvariant();
    }
}

