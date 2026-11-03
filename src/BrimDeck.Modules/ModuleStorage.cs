using System.Text.Json;

namespace BrimDeck.Modules;

public sealed class ModuleStorage(string directory) : IModuleStorage, IModuleStructuredStorage
{
    // Shared across generations: an old writer cannot race the new instance's temporary file.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory = Path.GetFullPath(directory);
    private static void Unlinked(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ModuleApiException("storage-path-invalid", "Linked module storage paths are not supported."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    private string FileFor(string key) { ModuleIds.Require(key); string file = Path.Combine(_directory, key + ".json"); Unlinked(file); return file; }
    public T? Read<T>(string key)
    {
        var result = ReadResult<T>(key);
        if (result.Status == ModuleReadStatus.Corrupt) throw new ModuleApiException("storage-corrupt", "Invalid module data: " + key);
        return result.Value;
    }
    public ModuleReadResult<T> ReadResult<T>(string key)
    {
        string file = FileFor(key);
        lock (Gates.GetOrAdd(file, _ => new()))
        {
            Unlinked(file);
            if (!File.Exists(file)) return new(ModuleReadStatus.Missing, default);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 1024 * 1024) return new(ModuleReadStatus.Corrupt, default, "storage-too-large");
            try { return new(ModuleReadStatus.Value, JsonSerializer.Deserialize<T>(input)); }
            catch (JsonException) { return new(ModuleReadStatus.Corrupt, default, "invalid-json"); }
        }
    }
    public void Write<T>(string key, T value)
    {
        string file = FileFor(key);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 1024 * 1024) throw new IOException("Module data exceeds 1 MiB per snapshot.");
        lock (Gates.GetOrAdd(file, _ => new()))
        {
            Unlinked(file); Directory.CreateDirectory(_directory); Unlinked(file);
            string temporary = Path.Combine(_directory, ".write-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
                Unlinked(file); File.Move(temporary, file, true);
            }
            finally { Unlinked(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
