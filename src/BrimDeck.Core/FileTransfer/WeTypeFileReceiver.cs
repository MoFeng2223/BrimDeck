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
    public WeTypeFileSender.SentFile? Session => _server.Session;
    public Action<WeTypeFileSender.SentFile>? SessionOpened { get => _server.SessionOpened; set => _server.SessionOpened = value; }
    public Action<WeTypeReceivedFile>? FileReceived { get => _server.FileReceived; set => _server.FileReceived = value; }

    public static async Task<WeTypeFileReceiver> StartAsync(WeTypeSyncClient control, WeTypeFileInvite invite, string deviceName,
        string downloadFolder, Action<WeTypeFileProgress>? progress, Action<string>? trace, Action<string>? connected,
        Action<string>? disconnected, CancellationToken ct)
    {
        var lan = WeTypeFileNetwork.LanAddresses();
        if (lan.Count == 0) throw new InvalidDataException("No LAN address to offer.");
        trace?.Invoke("本机地址：" + string.Join(", ", lan));
        var identity = new WeTypeFileIdentity();
        var server = new WeTypeFileReceiveServer(identity, downloadFolder, progress, trace, connected, disconnected);
        server.PeerUin = invite.SenderUin;
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
        if (_server.Session is { } session) await session.CloseAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _server.PeerCa?.Dispose();
        _identity.Dispose();
    }
}

internal sealed class WeTypeFileReceiveServer(WeTypeFileIdentity identity, string downloadFolder, Action<WeTypeFileProgress>? progress,
    Action<string>? trace, Action<string>? connected = null, Action<string>? disconnected = null)
{
    public WeTypeFileIdentity Identity { get; } = identity;
    public string DownloadFolder { get; } = downloadFolder;
    public Action<WeTypeFileProgress>? Progress { get; } = progress;
    public Action<string>? Trace { get; } = trace;
    // Raised the instant the peer opens the file stream: this is "the phone started connecting".
    public Action<string>? Connected { get; } = connected;
    // Raised when the peer actively ends the stream (kind-200 command=1001): the phone sent its disconnect.
    public Action<string>? Disconnected { get; } = disconnected;
    public ulong PeerUin;
    public WeTypeFileSender.SentFile? Session;
    public Action<WeTypeFileSender.SentFile>? SessionOpened;
    public Action<WeTypeReceivedFile>? FileReceived;
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
        if (!await requestStream.MoveNext(context.CancellationToken) || requestStream.Current.Kind != 100)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Missing stream initialization."));
        if (server.Session is { IsOpen: true })
            throw new RpcException(new Status(StatusCode.AlreadyExists, "A session is already connected."));
        await using var session = new WeTypeFileSender.SentFile(requestStream, responseStream,
            server.DownloadFolder, server.Progress, file =>
            {
                server.Completed.TrySetResult(file);
                server.FileReceived?.Invoke(file);
            }, server.Trace);
        session.PeerUin = server.PeerUin;
        server.Session = session;
        server.Trace?.Invoke("已接受局域网连接：" + context.Peer);
        server.Connected?.Invoke(context.Peer);
        server.SessionOpened?.Invoke(session);
        try { await session.Completion; }
        finally { server.Session = null; server.Disconnected?.Invoke(context.Peer); }
    }
}

