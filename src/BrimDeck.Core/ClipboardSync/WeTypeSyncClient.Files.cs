using System.Text;

namespace BrimDeck.Core.ClipboardSync;

// A device in the paired group, as the group request lists it.
public sealed record WeTypePeer(ulong Uin, string Name, uint Platform, bool SupportsFileTransfer);

// A pending file invitation addressed to this device (Group field 7).
public sealed record WeTypeFileInvite(string Code, ulong SenderUin, string SenderName, uint Platform, long SendTime, ulong Capabilities);

// The service's answer to command 77: the code the other device uses to join, and how long it stays valid.
public sealed record WeTypeFileOffer(string Code, TimeSpan Validity);

// The other device's side of a file rendezvous (command 74/75).
public sealed record WeTypeFilePeerInfo(ulong Uin, byte[] CaPem, string LinkJson, string? GrpcJson, ulong Capabilities, bool Ready);

// The paired device removed this transfer, or refused it.
public sealed class WeTypeFileCanceledException(string message) : IOException(message);

// File rendezvous commands of the WeType control channel, checked against wx-ime-sdk's wetype.proto.
public sealed partial class WeTypeSyncClient
{
    // The group request's filter bits: the bound list, and a pending transfer invitation.
    private const uint FilterBoundList = 1;
    private const uint FilterPendingInvite = 64;
    // FileRangeAck: the peer acknowledges byte ranges, which this transport requires.
    private const ulong FileRangeAck = 2;

    // Read from the clipboard's group request and handed to the worker once.
    private WeTypeFileInvite? _pendingInvite;

    public string DeviceName => identity.DeviceName;

    public WeTypeFileInvite? TakePendingInvite()
    {
        var invite = _pendingInvite;
        _pendingInvite = null;
        return invite;
    }

    // Invitations come with the clipboard's group request; an unknown sender is ignored rather than breaking the sync.
    private WeTypeFileInvite? ReadInvite(WeTypeWire group)
    {
        if (!group.Has(7)) return null;
        var invite = group.GetMessage(7);
        string code = invite.GetText(1);
        ulong sender = invite.GetNumber(3);
        if (code.Length is 0 or > 256 || !code.All(c => c is >= '!' and <= '~')) return null;
        if (sender == 0 || sender == identity.Uin || !_members.Contains(sender)) return null;
        return new WeTypeFileInvite(code, sender, invite.GetText(2), (uint)invite.GetNumber(5), unchecked((long)invite.GetNumber(4)), invite.GetNumber(6));
    }

    public async Task<IReadOnlyList<WeTypePeer>> ListPeersAsync(CancellationToken ct)
    {
        if (_key is null) await InitializeAsync(ct);
        var group = await GroupAsync(ct);
        return [.. group.Messages(2).Select(ReadPeer).Where(peer => peer.Uin != 0 && peer.Uin != identity.Uin)];
    }

    // Read after ConnectAsync: the group request returns the invitation alongside the devices.
    public async Task<WeTypeFileInvite?> PendingFileInviteAsync(CancellationToken ct)
    {
        if (_key is null) throw new InvalidOperationException("Connect first.");
        var group = await GroupAsync(ct, filter: FilterBoundList | FilterPendingInvite);
        return ReadInvite(group);
    }

    // Command 77 asks the paired device to accept a file; the other device joins with the returned code.
    public async Task<WeTypeFileOffer> InviteFileAsync(ulong peerUin, string caPem, string linkJson, CancellationToken ct)
    {
        if (_key is null) await InitializeAsync(ct);
        var body = new WeTypeWire().Number(1, peerUin).Text(2, caPem).Text(3, linkJson).Text(4, Name)
            .Number(5, FileRangeAck);
        Trace?.Invoke("file invite: uin=" + peerUin + " ca=" + caPem.Length + "B link=" + linkJson.Length + "B");
        var response = await ApiAsync(77, body, ct);
        Success(response);
        string code = response.GetText(2);
        ulong validity = response.GetNumber(4);
        if (code.Length is 0 or > 256) throw new InvalidDataException("Missing transfer code.");
        // The service's validity is in milliseconds; a longer one is not trusted.
        long milliseconds = validity == 0 ? 60_000 : (long)Math.Min(validity, 30UL * 60 * 1000);
        return new WeTypeFileOffer(code, TimeSpan.FromMilliseconds(milliseconds));
    }

    // Command 75, polled until the joined device publishes its LAN endpoints.
    public async Task<WeTypeFilePeerInfo?> PollFilePeerAsync(string code, ulong expected, CancellationToken ct)
    {
        if (_key is null) await InitializeAsync(ct);
        var response = await ApiAsync(75, new WeTypeWire().Text(1, code), ct);
        Success(response);
        if (!response.Has(2)) return null;
        return ReadFilePeer(response.GetMessage(2), expected);
    }

    // Command 74: joins the invitation and publishes this device's LAN endpoint and TLS certificate.
    public async Task<WeTypeFilePeerInfo> JoinFileAsync(string code, string caPem, string linkJson, string grpcJson, CancellationToken ct)
    {
        if (_key is null) await InitializeAsync(ct);
        var body = new WeTypeWire().Text(1, code).Text(2, caPem).Text(3, linkJson).Text(4, grpcJson)
            .Text(5, "{}").Number(6, 0).Number(7, FileRangeAck);
        Trace?.Invoke("file join: ca=" + caPem.Length + "B link=" + linkJson.Length + "B grpc=" + grpcJson.Length + "B");
        var response = await ApiAsync(74, body, ct);
        Success(response);
        if (!response.Has(2)) throw new InvalidDataException("Missing file peer.");
        return ReadFilePeer(response.GetMessage(2), 0);
    }

    private static WeTypePeer ReadPeer(WeTypeWire device)
    {
        var info = device.GetMessage(2);
        return new WeTypePeer(device.GetNumber(1), info.GetText(1), (uint)info.GetNumber(2), info.GetNumber(5) != 0);
    }

    private static WeTypeFilePeerInfo ReadFilePeer(WeTypeWire peer, ulong expected)
    {
        ulong uin = peer.GetNumber(1);
        if (uin == 0 || expected != 0 && uin != expected) throw new InvalidDataException("File rendezvous peer mismatch.");
        uint state = (uint)peer.GetNumber(11);
        if (state is 1 or 4) throw new WeTypeFileCanceledException("The other device canceled the transfer.");
        if (state == 2) throw new WeTypeFileCanceledException("The other device refused the transfer.");
        byte[] ca = peer.GetBytes(2);
        if (ca.Length is 0 or > 16 * 1024) throw new InvalidDataException("Missing peer certificate.");
        byte[] link = peer.GetBytes(3);
        if (link.Length is 0 or > 64 * 1024) throw new InvalidDataException("Missing peer address.");
        byte[] grpc = peer.Has(6) ? peer.GetBytes(6) : [];
        if (grpc.Length > 64 * 1024) throw new InvalidDataException("Peer endpoints too large.");
        return new WeTypeFilePeerInfo(uin, ca, Encoding.UTF8.GetString(link), grpc.Length == 0 ? null : Encoding.UTF8.GetString(grpc),
            peer.GetNumber(13), peer.GetNumber(5) != 0);
    }
}
