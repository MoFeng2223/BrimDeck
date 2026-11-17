using System.Net;
using Blake3.Managed;
using BrimDeck.Core.ClipboardSync;

namespace BrimDeck.Core.FileTransfer;

// Opens a reusable file session with a paired device and uploads the first batch
// over one authenticated stream. Later batches use the returned session; each range is acknowledged.
public static class WeTypeFileSender
{
    public const long MaxBytes = 1024L * 1024 * 1024;

    public static async Task<SentFile> SendAsync(WeTypeSyncClient control, WeTypeFileIdentity identity,
        ulong peerUin, IReadOnlyList<string> paths, Action<WeTypeFileProgress>? progress,
        Action<int, string, long, long>? fileDone, Action<string>? trace, CancellationToken ct)
    {
        if (paths is null || paths.Count == 0) throw new InvalidDataException("No file to send.");
        var infos = new List<(string Id, string Name, ulong Size, string Path)>();
        foreach (var path in paths)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxBytes) throw new InvalidDataException("A file of at most 1 GiB is required.");
            string name = file.Name;
            if (!ValidName(name)) throw new InvalidDataException("The file name cannot be sent.");
            string id = await HashAsync(path, ct);
            infos.Add((id, name, (ulong)file.Length, path));
        }
        ulong totalSize = (ulong)infos.Sum(item => (long)item.Size);
        trace?.Invoke("文件：" + string.Join("、", infos.Select(item => item.Name + " " + item.Size + " B")));
        var lan = WeTypeFileNetwork.LanAddresses();
        if (lan.Count == 0) throw new InvalidDataException("No LAN address to offer.");
        trace?.Invoke("本机地址：" + string.Join(", ", lan));
        var started = System.Diagnostics.Stopwatch.StartNew();

        progress?.Invoke(new(0, (long)totalSize, infos[0].Name));


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
            var sent = await UploadBatchAsync(channel, infos, totalSize, progress, fileDone, trace, ct);
            sent.PeerUin = peerUin;
            return sent;
        }
        throw new IOException("No file endpoints.");
    }
    private static string Mask(string code) => code.Length <= 6 ? code : code[..6] + "…";

    internal static async Task<SentFile> UploadAsync(Grpc.Net.Client.GrpcChannel channel, string id, string name, ulong size,
        string path, Action<WeTypeFileProgress>? progress, CancellationToken ct) =>
        await UploadBatchAsync(channel, [(id, name, size, path)], size, progress, null, null, ct);

    internal static async Task<SentFile> UploadBatchAsync(Grpc.Net.Client.GrpcChannel channel,
        IReadOnlyList<(string Id, string Name, ulong Size, string Path)> files, ulong totalSize,
        Action<WeTypeFileProgress>? progress, Action<int, string, long, long>? fileDone, Action<string>? trace,
        CancellationToken ct)
    {
        var session = new SentFile(channel);
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
        private readonly Grpc.Net.Client.GrpcChannel _channel;
        private readonly Grpc.Core.AsyncDuplexStreamingCall<WeTypeFileChunk, WeTypeFileChunk> _call;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _writes = new(1), _uploads = new(1);
        private readonly System.Threading.Channels.Channel<WeTypeFileChunk> _acks =
            System.Threading.Channels.Channel.CreateBounded<WeTypeFileChunk>(16);
        private readonly Task _reader;
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

        internal SentFile(Grpc.Net.Client.GrpcChannel channel)
        {
            _channel = channel;
            _call = channel.CreateCallInvoker().AsyncDuplexStreamingCall(WeTypeFileGrpc.TransferFilesMethod, null,
                new Grpc.Core.CallOptions(cancellationToken: _stop.Token));
            _reader = ReadAsync();
        }

        internal async Task WriteAsync(WeTypeFileChunk chunk, CancellationToken ct)
        {
            await _writes.WaitAsync(ct);
            try { await _call.RequestStream.WriteAsync(chunk).WaitAsync(ct); }
            finally { _writes.Release(); }
        }

        private async Task ReadAsync()
        {
            Exception? failure = null;
            try
            {
                while (await _call.ResponseStream.MoveNext(_stop.Token))
                {
                    var message = _call.ResponseStream.Current;
                    if (message.Kind == 200)
                    {
                        if (message.Business is { } business && WeTypeFileControl.IsDisconnect(business))
                            throw new IOException("The other device disconnected.");
                        if (message.ControlId is null) throw new InvalidDataException("Missing control acknowledgement ID.");
                        await WriteAsync(new WeTypeFileChunk { Kind = 5, ControlId = message.ControlId }, _stop.Token);
                    }
                    else if (message.Kind == 5 && message.File is null && message.ControlId is { } controlId
                        && controlId.SequenceEqual(WeTypeFileControl.Varint(_disconnectId))) _disconnectAck.TrySetResult();
                    else if (message.Kind == 4) throw new WeTypeFileCanceledException("The other device canceled the transfer.");
                    else if (message.Kind == 5 && message.File is not null) await _acks.Writer.WriteAsync(message, _stop.Token);
                }
                failure = new IOException("The file session closed.");
            }
            catch (Exception error) { failure = error; }
            finally { _acks.Writer.TryComplete(failure); }
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
                        var ack = await _acks.Reader.ReadAsync(timeout.Token);
                        if (ack.File?.Id != file.Id || ack.Start != offset || ack.End != end)
                            throw new InvalidDataException("Unexpected acknowledgement.");
                        offset = end;
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
                await _call.RequestStream.CompleteAsync().WaitAsync(timeout.Token);
            }
            catch (Exception) { }
            finally
            {
                _stop.Cancel(); _call.Dispose(); _channel.Dispose();
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