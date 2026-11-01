using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck.Native;

// History can contain private text. DPAPI binds the whole file, image payloads included, to the Windows user; there are no
// plaintext thumbnails, excerpts or exported history alongside it. A failed replacement leaves the previous file intact.
internal sealed class ClipboardHistoryStore(string directory)
{
    internal string DirectoryPath { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    internal string FilePath { get; } = Path.Combine(directory, "clipboard-history.bin");

    internal static string ResolveDirectory(string requested, string defaultDirectory)
    {
        string value = string.IsNullOrWhiteSpace(requested) ? defaultDirectory : requested.Trim();
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("Clipboard storage requires an absolute directory.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    internal (long HistoryBytes, long PendingBytes) Measure()
    {
        static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        return (Size(FilePath), Size(FilePath + ".tmp"));
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const long MaximumFileBytes = 48 * 1024 * 1024;

    internal ClipboardSnapshot Load()
    {
        if (!File.Exists(FilePath)) return new();
        if (new FileInfo(FilePath).Length > MaximumFileBytes) throw new InvalidDataException("Clipboard history exceeds the storage limit.");
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<ClipboardSnapshot>(plain, Json) ?? throw new InvalidDataException("Invalid clipboard history."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal void Save(ClipboardSnapshot snapshot, bool overwrite = true)
    {
        Directory.CreateDirectory(directory);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        string temp = FilePath + ".tmp";
        bool ownsTemp = false;
        try
        {
            if (plain.LongLength > MaximumFileBytes) throw new InvalidDataException("Clipboard history exceeds the storage limit.");
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            using (var output = new FileStream(temp, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemp = true; output.Write(encrypted);
            }
            File.Move(temp, FilePath, overwrite);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (ownsTemp && File.Exists(temp)) File.Delete(temp);
        }
    }
    internal void Delete() { if (File.Exists(FilePath)) File.Delete(FilePath); }
    internal static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException or CryptographicException or JsonException or NotSupportedException or ArgumentException;
}
