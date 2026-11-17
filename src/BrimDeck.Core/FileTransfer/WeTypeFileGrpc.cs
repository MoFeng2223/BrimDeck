using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;

namespace BrimDeck.Core.FileTransfer;

// The Flurry file stream both sides speak (wx-ime-sdk's flurry.proto): one duplex gRPC call per connection.
public static class WeTypeFileGrpc
{
    public const int Block = 2 * 1024 * 1024;
    public const int MessageLimit = Block + 4096;
    public const string ServiceName = "flurry.FileService";

    private static readonly Marshaller<WeTypeFileChunk> Marshaller =
        Marshallers.Create(chunk => chunk.Encode(), bytes => WeTypeFileChunk.Decode(bytes));

    public static readonly Method<WeTypeFileChunk, WeTypeFileChunk> TransferFilesMethod = new(
        MethodType.DuplexStreaming, ServiceName, "TransferFiles", Marshaller, Marshaller);

    [BindServiceMethod(typeof(WeTypeFileGrpc), nameof(BindService))]
    public abstract class Base
    {
        public virtual Task TransferFiles(IAsyncStreamReader<WeTypeFileChunk> requestStream,
            IServerStreamWriter<WeTypeFileChunk> responseStream, ServerCallContext context) =>
            Task.FromException(new RpcException(new Status(StatusCode.Unimplemented, "")));
    }

    public static ServerServiceDefinition BindService(Base service) => ServerServiceDefinition.CreateBuilder()
        .AddMethod(TransferFilesMethod, service.TransferFiles)
        .Build();

    public static void BindService(ServiceBinderBase binder, Base service) =>
        binder.AddMethod(TransferFilesMethod, service is null ? null : new DuplexStreamingServerMethod<WeTypeFileChunk, WeTypeFileChunk>(service.TransferFiles));

    // The channel authenticates with this side's client certificate and trusts only the CA the other side sent.
    public static GrpcChannel Connect(WeTypeFileIdentity identity, X509Certificate2 peerCa, string serverName, IPAddress address, int port)
    {
        byte[] authorityBytes = peerCa.RawData;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                // A phone that does not name its certificate is trusted through the CA it sent over the control channel.
                TargetHost = serverName.Length > 0 ? serverName : "wetype.private",
                ClientCertificates = new X509CertificateCollection { identity.ClientCertificate },
                // The platform verifies the name against the IP literal it connected to; the certificate instead names
                // the rendezvous, so the expected name is checked here against the SAN.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    using var authority = X509CertificateLoader.LoadCertificate(authorityBytes);
                    return certificate is X509Certificate2 peer && WeTypeFileIdentity.Validate(peer, authority) &&
                        (serverName.Length == 0 || peer.MatchesHostname(serverName));
                },
            },
        };
        return GrpcChannel.ForAddress($"https://{address}:{port}", new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = MessageLimit,
            MaxSendMessageSize = MessageLimit,
            DisposeHttpClient = true,
        });
    }
}
