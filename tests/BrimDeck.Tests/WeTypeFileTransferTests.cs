using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BrimDeck.Core.FileTransfer;

static class WeTypeFileTransferTests
{
    public static void Run(Action<string, bool> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<string, bool> check)
    {
        // Every optional field of a chunk must survive the protobuf round trip, including an empty data block.
        var info = new WeTypeFileInfo { Id = new string('a', 64), Name = "报告.pdf", Size = 3 };
        var chunk = new WeTypeFileChunk
        {
            Kind = 2, File = info, Data = [1, 2, 3], Start = 5, End = 8, Sequence = 9, Field7 = 0,
            Business = [4, 5], ControlId = [6, 7],
        };
        var back = WeTypeFileChunk.Decode(chunk.Encode());
        check("file chunk round trip", back.Kind == 2 && back.File is { } read && read.Id == info.Id && read.Name == info.Name && read.Size == 3
            && back.Data!.SequenceEqual(new byte[] { 1, 2, 3 }) && back.Start == 5 && back.End == 8 && back.Sequence == 9 && back.Field7 == 0
            && back.Business!.SequenceEqual(new byte[] { 4, 5 }) && back.ControlId!.SequenceEqual(new byte[] { 6, 7 }));
        check("file chunk keeps an empty data block", WeTypeFileChunk.Decode(new WeTypeFileChunk { Kind = 2, Data = [] }.Encode()).Data is { Length: 0 });
        check("file control recognises a disconnect", WeTypeFileControl.IsDisconnect(new BrimDeck.Core.ClipboardSync.WeTypeWire().Number(2, 1001).Encode())
            && !WeTypeFileControl.IsDisconnect(new BrimDeck.Core.ClipboardSync.WeTypeWire().Number(2, 7).Encode()));

        string folder = Path.Combine(Path.GetTempPath(), "BrimDeck-FileTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // The file identifier is the BLAKE3 hash from the official test vector.
            string small = Path.Combine(folder, "abc.txt");
            await File.WriteAllTextAsync(small, "abc");
            check("file identifier matches the BLAKE3 vector",
                await WeTypeFileSender.HashAsync(small, default) == "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85");

            using var serverIdentity = new WeTypeFileIdentity();
            using var clientIdentity = new WeTypeFileIdentity();
            using var serverCa = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(serverIdentity.CaPem));
            using var otherCa = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(clientIdentity.CaPem));
            check("file identity issues both leaves", WeTypeFileIdentity.Validate(serverIdentity.ServerCertificate, serverCa)
                && WeTypeFileIdentity.Validate(serverIdentity.ClientCertificate, serverCa));
            check("file identity trusts only its own CA", !WeTypeFileIdentity.Validate(clientIdentity.ClientCertificate, serverCa)
                && !WeTypeFileIdentity.Validate(serverIdentity.ServerCertificate, otherCa));

            string received = Path.Combine(folder, "received");
            Directory.CreateDirectory(received);

            // A zero-byte file still sends its one empty range, and completes after that range is acknowledged.
            string empty = Path.Combine(folder, "empty.txt");
            await File.WriteAllBytesAsync(empty, []);
            var emptyServer = new WeTypeFileReceiveServer(serverIdentity, received, null, null) { PeerCa = otherCa };
            int emptyPort = FreePort();
            await using (var emptyApp = await emptyServer.StartAsync(emptyPort, default))
            {
                using var emptyChannel = WeTypeFileGrpc.Connect(clientIdentity, serverCa, serverIdentity.ServerName, IPAddress.Loopback, emptyPort);
                await using var emptyReceipt = await WeTypeFileSender.UploadAsync(emptyChannel, await WeTypeFileSender.HashAsync(empty, default), "empty.txt", 0, empty, null, default);
                var emptyFile = await emptyServer.Completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await emptyReceipt.CloseAsync();
                await emptyApp.StopAsync();
                check("empty file transfer completes", emptyFile.Bytes == 0 && new FileInfo(emptyFile.Path).Length == 0);
            }

            // Three blocks, so the receiver has to acknowledge each range before the next is sent.
            byte[] content = new byte[WeTypeFileGrpc.Block * 2 + 12345];
            new Random(7).NextBytes(content);
            string source = Path.Combine(folder, "sample.bin");
            await File.WriteAllBytesAsync(source, content);

            int connections = 0;
            var server = new WeTypeFileReceiveServer(serverIdentity, received, null, line => { if (line.StartsWith("已接受局域网连接：")) Interlocked.Increment(ref connections); }) { PeerCa = otherCa };
            int port = FreePort();
            await using var app = await server.StartAsync(port, default);
            using var channel = WeTypeFileGrpc.Connect(clientIdentity, serverCa, serverIdentity.ServerName, IPAddress.Loopback, port);
            string id = await WeTypeFileSender.HashAsync(source, default);
            var reverse = new TaskCompletionSource<WeTypeReceivedFile>(TaskCreationOptions.RunContinuationsAsynchronously);
            var receipt = await WeTypeFileSender.UploadBatchAsync(channel,
                [(id, "sample.bin", (ulong)content.Length, source)], (ulong)content.Length,
                null, null, null, Path.Combine(folder, "reverse"), null, file => reverse.TrySetResult(file), null, default);
            await Task.WhenAll(server.Session!.SendAsync([small], null, null, null, default),
                receipt.SendAsync([small], null, null, null, default)).WaitAsync(TimeSpan.FromSeconds(20));
            var reverseFile = await reverse.Task.WaitAsync(TimeSpan.FromSeconds(20));
            check("same session transfers in both directions concurrently", await File.ReadAllTextAsync(reverseFile.Path) == "abc" && receipt.IsOpen);
            var saved = await server.Completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            check("file session stays open after first batch", receipt.IsOpen);
            await receipt.SendAsync([small, source], null, null, null, default);
            check("file session sends a second multi-file batch", receipt.IsOpen && receipt.Bytes == content.Length + 3);
            await receipt.SendAsync([small], null, null, null, default);
            check("file session sends a third batch without reconnecting", receipt.IsOpen && receipt.Bytes == 3);
            check("multiple batches share one duplex stream", connections == 1);
            await receipt.CloseAsync();

            await receipt.CloseAsync();
            check("file session closes idempotently", !receipt.IsOpen);
            check("file close waits for matching peer acknowledgement", receipt.DisconnectAcknowledged);
            bool closedRejected = false;
            try { await receipt.SendAsync([small], null, null, null, default); }
            catch (IOException) { closedRejected = true; }
            check("closed file session rejects later sends", closedRejected);
            await app.StopAsync();
            check("file transfer acknowledges every range", saved.Bytes == content.Length);
            check("file transfer writes the same bytes", File.ReadAllBytes(saved.Path).SequenceEqual(content));
            check("file transfer keeps the name and identifier", Path.GetFileName(saved.Path) == "sample.bin" && saved.Id == id);
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

