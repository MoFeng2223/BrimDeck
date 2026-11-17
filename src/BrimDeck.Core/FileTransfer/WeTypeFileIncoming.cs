using Blake3.Managed;

namespace BrimDeck.Core.FileTransfer;

// One file being received on a stream: its metadata validated on arrival, written to a private temporary file, and
// given its real name only once the content matches its identifier. Both roles of a file session use it: the pure
// receiver server (a phone-initiated stream) and the client-side reader of a two-way conversation.
internal sealed class WeTypeFileIncoming : IDisposable
{
    private readonly string _folder, _temporary, _id, _name;
    private readonly ulong _size;
    private readonly FileStream _stream;
    private Hasher _hash;
    private readonly Action<WeTypeFileProgress>? _progress;
    private ulong _offset;
    private bool _committed;

    private WeTypeFileIncoming(string folder, string temporary, string id, string name, ulong size,
        FileStream stream, Hasher hash, Action<WeTypeFileProgress>? progress)
    {
        _folder = folder; _temporary = temporary; _id = id; _name = name; _size = size; _stream = stream;
        _hash = hash; _progress = progress;
    }

    public string Id => _id;

    public static WeTypeFileIncoming Create(string downloadFolder, WeTypeFileInfo? info,
        Action<WeTypeFileProgress>? progress, Action<string>? trace)
    {
        if (info is null || !ValidId(info.Id) || !WeTypeFileSender.ValidName(info.Name) || info.Size > (ulong)WeTypeFileSender.MaxBytes)
            throw new InvalidDataException("Invalid file metadata.");
        string folder = Path.Combine(downloadFolder, ".incoming");
        Directory.CreateDirectory(folder);
        trace?.Invoke("收到文件头：" + info.Name + "，" + info.Size + " 字节");
        string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".part");
        var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, WeTypeFileGrpc.Block,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new WeTypeFileIncoming(folder, temporary, info.Id, info.Name, info.Size, stream, Hasher.New(), progress);
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