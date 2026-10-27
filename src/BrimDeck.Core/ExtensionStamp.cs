using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BrimDeck.Core;

// The fingerprint of a plugin folder, used to decide whether "Reload" has anything to reload.
//
// A timestamp is not an answer to that question. An author who restores a stylesheet from version control, or a build
// step that copies with timestamps preserved, produces a different file with the same length and the same write time —
// and the reload the plugins page offers would do nothing, which is the one thing a plugin author does all day. What a
// page actually runs is content, so content is what is compared.
public static class ExtensionStamp
{
    // Every file's relative path and bytes, in a stable order, hashed together. Null when the folder cannot be read:
    // a scanner that fails must not look like "nothing changed", so the caller gets no answer to reuse rather than a
    // false one.
    public static string? Of(string folder)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(file => (Full: file, Relative: Path.GetRelativePath(folder, file).Replace('\\', '/')))
                .OrderBy(item => item.Relative, StringComparer.OrdinalIgnoreCase);
            foreach (var (full, relative) in files)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(relative));
                hash.AppendData([0]);
                try { hash.AppendData(File.ReadAllBytes(full)); }
                catch (Exception) { hash.AppendData(Encoding.UTF8.GetBytes("?")); }
                hash.AppendData([0]);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception) { return null; }
    }
}
