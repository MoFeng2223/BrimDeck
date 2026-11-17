using System.Net;
using Blake3.Managed;
using BrimDeck.Core.ClipboardSync;

namespace BrimDeck.Core.FileTransfer;

// Sends one file to a paired device: invite it over the control channel, wait for its LAN endpoint, then upload
// over the authenticated stream. The receiver acknowledges every range before the next one is sent.
public static class WeTypeFileSender
{
    public const long MaxBytes = 1024L * 1024 * 1024;

    public static async Task<WeTypeFileReceipt> SendAsync(WeTypeSyncClient control, WeTypeFileIdentity identity,
        ulong peerUin, string path, Action<WeTypeFileProgress>? progress, Action<string>? trace, CancellationToken ct)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > MaxBytes) throw new InvalidDataException("A file of at most 1 GiB is required.");
        string name = file.Name;
        if (!ValidName(name)) throw new InvalidDataException("The file name cannot be sent.");
        ulong size = (ulong)file.Length;
        string id = await HashAsync(path, ct);
        trace?.Invoke("文件：" + name + "，" + size + " 字节，BLAKE3 " + id[..8] + "…");
        var lan = WeTypeFileNetwork.LanAddresses();
        if (lan.Count == 0) throw new InvalidDataException("No LAN address to offer.");
        trace?.Invoke("本机地址：" + string.Join(", ", lan));
        var started = System.Diagnostics.Stopwatch.StartNew();

        progress?.Invoke(new(0, (long)size, name));
        var offer = await control.InviteFileAsync(peerUin, System.Text.Encoding.ASCII.GetString(identity.CaPem),
            WeTypeFileNetwork.LinkJson(lan, control.DeviceName, preferredClient: true), ct);
        trace?.Invoke("邀请码：" + Mask(offer.Code) + "，有效期 " + offer.Validity.TotalSeconds.ToString("0") + " 秒");

        // The other device joins with the code and publishes where it listens; the addresses can appear a moment later,
        // and some clients only publish them after the offer has been accepted on the phone.
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

        Exception? last = null;
        foreach (var (address, port) in endpoints)
        {
            try
            {
                using var channel = WeTypeFileGrpc.Connect(identity, authority, serverName ?? "", address, port);
                var receipt = await UploadAsync(channel, id, name, size, path, progress, ct);
                trace?.Invoke("局域网连接：" + address + ":" + port + "，耗时 " + started.ElapsedMilliseconds + " 毫秒");
                return receipt;
            }
            catch (Exception error) when (error is not OperationCanceledException && error is not WeTypeFileCanceledException)
            {
                trace?.Invoke("端点 " + address + ":" + port + " 失败：" + error.GetType().Name + ": " + error.Message);
                last = error;
            }
        }
        throw new IOException("No endpoint accepted the transfer.", last);
    }

    private static string Mask(string code) => code.Length <= 6 ? code : code[..6] + "…";

    internal static async Task<WeTypeFileReceipt> UploadAsync(Grpc.Net.Client.GrpcChannel channel, string id, string name, ulong size,
        string path, Action<WeTypeFileProgress>? progress, CancellationToken ct)
    {
        var info = new WeTypeFileInfo { Id = id, Name = name, Size = size };
        using var call = channel.CreateCallInvoker().AsyncDuplexStreamingCall(WeTypeFileGrpc.TransferFilesMethod, null,
            new Grpc.Core.CallOptions(cancellationToken: ct));
        await call.RequestStream.WriteAsync(new WeTypeFileChunk { Kind = 100 });
        await call.RequestStream.WriteAsync(new WeTypeFileChunk { Kind = 1, File = info, Sequence = 0 });

        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, WeTypeFileGrpc.Block,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[WeTypeFileGrpc.Block];
        ulong offset = 0, sequence = 1;
        bool first = true;
        do
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0 && size > 0) throw new IOException("The file became shorter.");
            ulong end = offset + (ulong)read;
            await call.RequestStream.WriteAsync(new WeTypeFileChunk
            {
                Kind = 2, File = info, Data = read == buffer.Length ? buffer : buffer[..read],
                Start = offset, End = end, Sequence = sequence++,
            });
            await WaitForRangeAsync(call, id, offset, end, ct);
            offset = end;
            first = false;
            progress?.Invoke(new((long)offset, (long)size, name));
        } while (offset < size || first);

        await call.RequestStream.CompleteAsync();
        return new WeTypeFileReceipt(id, (long)offset);
    }

    // The stream also carries the other side's native control messages; they are acknowledged and skipped.
    private static async Task WaitForRangeAsync(Grpc.Core.AsyncDuplexStreamingCall<WeTypeFileChunk, WeTypeFileChunk> call,
        string id, ulong start, ulong end, CancellationToken ct)
    {
        while (await call.ResponseStream.MoveNext(ct))
        {
            var message = call.ResponseStream.Current;
            switch (message.Kind)
            {
                case 5 when message.File is null:
                    continue;
                case 5:
                    if (message.File.Id != id || message.Start != start || message.End != end)
                        throw new InvalidDataException("Unexpected acknowledgement.");
                    return;
                case 4:
                    throw new WeTypeFileCanceledException("The other device canceled the transfer.");
                case 200:
                    if (message.Business is { } business && WeTypeFileControl.IsDisconnect(business))
                        throw new IOException("The other device disconnected.");
                    await call.RequestStream.WriteAsync(new WeTypeFileChunk { Kind = 5, ControlId = message.ControlId });
                    continue;
                default:
                    continue;
            }
        }
        throw new IOException("The stream closed before the range was acknowledged.");
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
