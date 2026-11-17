using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Blake3.Managed;
using BrimDeck.Core.ClipboardSync;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BrimDeck.Core.FileTransfer;

// Accepts one transfer addressed to this device: publish a LAN endpoint and TLS identity, join the invitation,
// then receive the file the paired device sends over the authenticated stream.
public sealed class WeTypeFileReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly WeTypeFileReceiveServer _server;
    private readonly WeTypeFileIdentity _identity;

    private WeTypeFileReceiver(WebApplication app, WeTypeFileReceiveServer server, WeTypeFileIdentity identity, int port)
    {
        _app = app; _server = server; _identity = identity; Port = port;
    }

    public Task<WeTypeReceivedFile> Completed => _server.Completed.Task;

    public int Port { get; }

    public static async Task<WeTypeFileReceiver> StartAsync(WeTypeSyncClient control, WeTypeFileInvite invite, string deviceName,
        string downloadFolder, Action<WeTypeFileProgress>? progress, Action<string>? trace, CancellationToken ct)
    {
        var lan = WeTypeFileNetwork.LanAddresses();
        if (lan.Count == 0) throw new InvalidDataException("No LAN address to offer.");
        trace?.Invoke("本机地址：" + string.Join(", ", lan));
        var identity = new WeTypeFileIdentity();
        var server = new WeTypeFileReceiveServer(identity, downloadFolder, progress, trace);
        WebApplication? app = null;
        try
        {
            int port = FreePort();
            app = await server.StartAsync(port, ct);
            trace?.Invoke("监听端口：0.0.0.0:" + port);
            string link = WeTypeFileNetwork.LinkJson(lan, deviceName, preferredClient: false);
            string grpc = WeTypeFileNetwork.GrpcJson(identity.ServerName, deviceName, lan, port);
            trace?.Invoke("加入邀请：" + (invite.Code.Length <= 6 ? invite.Code : invite.Code[..6] + "…"));
            var peer = await control.JoinFileAsync(invite.Code, Encoding.ASCII.GetString(identity.CaPem), link, grpc, ct);
            trace?.Invoke("对端 Uin：" + peer.Uin + "，地址：" + peer.LinkJson);
            if ((peer.Capabilities & 2) == 0) throw new InvalidDataException("The other device cannot acknowledge ranges.");
            server.PeerCa = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(peer.CaPem));
            return new WeTypeFileReceiver(app, server, identity, port);
        }
        catch (Exception error)
        {
            trace?.Invoke("加入失败：" + error.GetType().Name + ": " + error.Message);
            if (app is not null) { try { await app.StopAsync(); } catch (Exception stop) when (stop is not OutOfMemoryException) { } await app.DisposeAsync(); }
            identity.Dispose();
            throw;
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _server.PeerCa?.Dispose();
        _identity.Dispose();
    }
}

internal sealed class WeTypeFileReceiveServer(WeTypeFileIdentity identity, string downloadFolder, Action<WeTypeFileProgress>? progress,
    Action<string>? trace)
{
    public WeTypeFileIdentity Identity { get; } = identity;
    public string DownloadFolder { get; } = downloadFolder;
    public Action<WeTypeFileProgress>? Progress { get; } = progress;
    public Action<string>? Trace { get; } = trace;
    public volatile X509Certificate2? PeerCa;
    public TaskCompletionSource<WeTypeReceivedFile> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<WebApplication> StartAsync(int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Any, port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = Identity.ServerCertificate;
                    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    https.ClientCertificateValidation = (certificate, _, _) =>
                    {
                        bool valid = certificate is X509Certificate2 peer && WeTypeFileIdentity.Validate(peer, PeerCa);
                        Trace?.Invoke("TLS 客户端证书 " + (valid ? "验证通过" : "验证失败") + "：" + certificate?.Subject);
                        return valid;
                    };
                });
            });
        });
        builder.Services.AddGrpc(options =>
        {
            options.MaxReceiveMessageSize = WeTypeFileGrpc.MessageLimit;
            options.MaxSendMessageSize = WeTypeFileGrpc.MessageLimit;
        });
        builder.Services.AddSingleton(this);
        var app = builder.Build();
        app.MapGrpcService<WeTypeFileReceiveService>();
        await app.StartAsync(ct);
        return app;
    }
}

internal sealed class WeTypeFileReceiveService(WeTypeFileReceiveServer server) : WeTypeFileGrpc.Base
{
    public override async Task TransferFiles(IAsyncStreamReader<WeTypeFileChunk> requestStream,
        IServerStreamWriter<WeTypeFileChunk> responseStream, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        Incoming? incoming = null;
        server.Trace?.Invoke("已接受局域网连接：" + context.Peer);
        try
        {
            bool initialized = false;
            while (await requestStream.MoveNext(ct))
            {
                var chunk = requestStream.Current;
                if (!initialized)
                {
                    if (chunk.Kind != 100) throw new RpcException(new Status(StatusCode.InvalidArgument, "Missing stream initialization."));
                    initialized = true;
                    continue;
                }
                switch (chunk.Kind)
                {
                    case 1:
                        incoming?.Dispose();
                        incoming = Incoming.Create(server, chunk.File);
                        break;
                    case 2:
                        // Some peers attach the file metadata to the first data block instead of sending a
                        // separate metadata message, so initialize the receiver from that block when needed.
                        if (incoming is null)
                        {
                            if (chunk.File is null) throw new RpcException(new Status(StatusCode.InvalidArgument, "Data before metadata."));
                            incoming = Incoming.Create(server, chunk.File);
                        }
                        var (start, end, complete) = await incoming.AppendAsync(chunk, ct);
                        if (complete)
                        {
                            var file = incoming.Commit();
                            incoming = null;
                            server.Trace?.Invoke("文件接收完成：" + file.Name + "，" + file.Bytes + " 字节，" + file.Path);
                            // The range is acknowledged only after the content matched its identifier.
                            await responseStream.WriteAsync(new WeTypeFileChunk { Kind = 5, File = new WeTypeFileInfo { Id = file.Id }, Start = start, End = end });
                            server.Completed.TrySetResult(file);
                        }
                        else
                        {
                            await responseStream.WriteAsync(new WeTypeFileChunk { Kind = 5, File = new WeTypeFileInfo { Id = incoming.Id }, Start = start, End = end });
                        }
                        break;
                    case 3:
                        incoming?.Dispose(); incoming = null;
                        break;
                    case 200:
                        // Native business messages are acknowledged by echoing their identifier.
                        if (chunk.Business is not { } business || chunk.ControlId is not { } controlId ||
                            business.Length > 64 * 1024 || controlId.Length > 1024)
                            throw new InvalidDataException("Invalid control message.");
                        if (WeTypeFileControl.IsDisconnect(business))
                        {
                            await responseStream.WriteAsync(new WeTypeFileChunk { Kind = 5, ControlId = controlId });
                            return;
                        }
                        await responseStream.WriteAsync(new WeTypeFileChunk { Kind = 5, ControlId = controlId });
                        break;
                    default:
                        break;
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            server.Trace?.Invoke("传输失败：" + error.GetType().Name + ": " + error.Message);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        finally
        {
            incoming?.Dispose();
        }
    }

    // One file on the stream: its metadata, validated on arrival, and the private temporary file it is written to.
    private sealed class Incoming : IDisposable
    {
        private readonly string _folder, _temporary, _id, _name;
        private readonly ulong _size;
        private readonly FileStream _stream;
        private Hasher _hash;
        private readonly Action<WeTypeFileProgress>? _progress;
        private ulong _offset;
        private bool _committed;

        private Incoming(string folder, string temporary, string id, string name, ulong size, FileStream stream, Hasher hash,
            Action<WeTypeFileProgress>? progress)
        {
            _folder = folder; _temporary = temporary; _id = id; _name = name; _size = size; _stream = stream;
            _hash = hash; _progress = progress;
        }

        public string Id => _id;

        public static Incoming Create(WeTypeFileReceiveServer server, WeTypeFileInfo? info)
        {
            if (info is null || !ValidId(info.Id) || !WeTypeFileSender.ValidName(info.Name) || info.Size > (ulong)WeTypeFileSender.MaxBytes)
                throw new InvalidDataException("Invalid file metadata.");
            string folder = Path.Combine(server.DownloadFolder, ".incoming");
            Directory.CreateDirectory(folder);
            server.Trace?.Invoke("收到文件头：" + info.Name + "，" + info.Size + " 字节");
            string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".part");
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, WeTypeFileGrpc.Block,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new Incoming(folder, temporary, info.Id, info.Name, info.Size, stream, Hasher.New(), server.Progress);
        }

        public async Task<(ulong Start, ulong End, bool Complete)> AppendAsync(WeTypeFileChunk chunk, CancellationToken ct)
        {
            if (chunk.File is null || chunk.File.Id != _id || chunk.File.Name != _name || chunk.File.Size != _size ||
                chunk.Data is null || chunk.Start is not { } start || chunk.End is not { } end)
                throw new InvalidDataException("File metadata changed.");
            if (chunk.Data.Length > WeTypeFileGrpc.Block || start != _offset || end < start ||
                (ulong)chunk.Data.Length != end - start || end > _size)
                throw new InvalidDataException("Invalid file range.");
            await _stream.WriteAsync(chunk.Data, ct);
            await _stream.FlushAsync(ct);
            _hash.Update(chunk.Data);
            _offset = end;
            _progress?.Invoke(new((long)_offset, (long)_size, _name));
            return (start, end, end == _size);
        }

        // The temporary file is durable and matches the identifier before it is given its name.
        public WeTypeReceivedFile Commit()
        {
            _stream.Flush(true);
            _stream.Dispose();
            string digest = Convert.ToHexString(_hash.Finalize().AsSpan()).ToLowerInvariant();
            if (digest != _id) { TryDelete(); throw new InvalidDataException("The file does not match its identifier."); }
            Directory.CreateDirectory(_folder);
            string target = Unique(_folder, _name);
            File.Move(_temporary, target);
            _committed = true;
            return new WeTypeReceivedFile(_id, target, (long)_offset, _name);
        }

        public void Dispose()
        {
            try { _hash.Dispose(); } catch (Exception error) when (error is not OutOfMemoryException) { }
            if (_committed) return;
            try { _stream.Dispose(); } catch (Exception error) when (error is not OutOfMemoryException) { }
            TryDelete();
        }

        private void TryDelete()
        {
            try { File.Delete(_temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        private static bool ValidId(string id) => id.Length == 64 && id.All(Uri.IsHexDigit);

        private static string Unique(string folder, string name)
        {
            string clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
            if (clean.Length == 0) clean = "file";
            string target = Path.Combine(folder, clean);
            string stem = Path.GetFileNameWithoutExtension(clean), extension = Path.GetExtension(clean);
            for (int n = 1; File.Exists(target); n++) target = Path.Combine(folder, stem + " (" + n + ")" + extension);
            return target;
        }
    }
}