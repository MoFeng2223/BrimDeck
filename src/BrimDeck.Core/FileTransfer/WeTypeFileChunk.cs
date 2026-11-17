using BrimDeck.Core.ClipboardSync;

namespace BrimDeck.Core.FileTransfer;

// One transfer's progress, for the panel and the sync log.
public readonly record struct WeTypeFileProgress(long Transferred, long Total, string Name)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Transferred / Total, 0, 1);
}

public sealed record WeTypeReceivedFile(string Id, string Path, long Bytes, string Name);

public sealed record WeTypeFileReceipt(string Id, long Bytes);

// flurry.FileInfo and flurry.FileChunk, the messages of the gRPC file stream (see wx-ime-sdk's flurry.proto).
public sealed class WeTypeFileInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public ulong Size { get; init; }

    public WeTypeWire ToWire() => new WeTypeWire().Text(1, Id).Text(2, Name).Number(3, Size);

    public static WeTypeFileInfo Read(WeTypeWire wire) => new() { Id = wire.GetText(1), Name = wire.GetText(2), Size = wire.GetNumber(3) };
}

public sealed class WeTypeFileChunk
{
    // 1 metadata, 2 data, 3 the sender cancels, 4 the receiver cancels, 5 an acknowledgement,
    // 100 the stream's first message, 200 a native business message.
    public uint? Kind { get; init; }
    public WeTypeFileInfo? File { get; init; }
    public byte[]? Data { get; init; }
    public ulong? Start { get; init; }
    public ulong? End { get; init; }
    public byte[]? Business { get; init; }
    public ulong? Sequence { get; init; }
    public ulong? Field7 { get; init; }
    public byte[]? ControlId { get; init; }

    public byte[] Encode()
    {
        var wire = new WeTypeWire();
        if (Kind is { } kind)
        {
            var action = new WeTypeWire().Number(1, kind);
            if (File is { } file) action.Message(2, file.ToWire());
            wire.Message(1, action);
        }
        if (Data is not null) wire.Bytes(2, Data);
        if (Start is { } start) wire.Number(3, start);
        if (End is { } end) wire.Number(4, end);
        if (Business is not null) wire.Bytes(5, Business);
        if (Sequence is { } sequence) wire.Number(6, sequence);
        if (Field7 is { } field7) wire.Number(7, field7);
        if (ControlId is not null) wire.Bytes(8, ControlId);
        return wire.Encode();
    }

    public static WeTypeFileChunk Decode(byte[] bytes)
    {
        var wire = WeTypeWire.Parse(bytes);
        WeTypeFileChunk chunk = new()
        {
            Data = wire.Has(2) ? wire.GetBytes(2) : null,
            Start = wire.Has(3) ? wire.GetNumber(3) : null,
            End = wire.Has(4) ? wire.GetNumber(4) : null,
            Business = wire.Has(5) ? wire.GetBytes(5) : null,
            Sequence = wire.Has(6) ? wire.GetNumber(6) : null,
            Field7 = wire.Has(7) ? wire.GetNumber(7) : null,
            ControlId = wire.Has(8) ? wire.GetBytes(8) : null,
        };
        if (wire.Has(1))
        {
            var action = wire.GetMessage(1);
            chunk = new WeTypeFileChunk
            {
                Kind = action.Has(1) ? (uint)action.GetNumber(1) : null,
                File = action.Has(2) ? WeTypeFileInfo.Read(action.GetMessage(2)) : null,
                Data = chunk.Data, Start = chunk.Start, End = chunk.End, Business = chunk.Business,
                Sequence = chunk.Sequence, Field7 = chunk.Field7, ControlId = chunk.ControlId,
            };
        }
        return chunk;
    }
}

// FileControlEnvelope carried in a kind-200 message: 1001 is the peer's disconnect notice.
public static class WeTypeFileControl
{
    public static bool IsDisconnect(byte[] payload)
    {
        try { return WeTypeWire.Parse(payload).GetNumber(2) == 1001; }
        catch (InvalidDataException) { return false; }
    }
}
