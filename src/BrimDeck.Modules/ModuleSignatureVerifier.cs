using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Modules;

// Signatures prove who published these bytes and that they were not changed since. They are not a permission
// boundary: a signed module still runs in-process with the same reach as any other module.
public static class ModuleSignatureVerifier
{
    public const string FileName = "BRIMDECK.SIG";
    // Shipped with the host. Empty until keys are published, so every package reads as unsigned for now.
    public static IReadOnlyList<ModuleSigningKey> Trusted { get; } = [];
    public static IReadOnlyList<ModuleRevocation> Revoked { get; } = [];

    private sealed record SigFile(string Alg, string KeyId, string Channel, string ModId, string ModVersion, string Digest, string SignedAt, string Signature);

    internal static ModuleSignature Verify(ModuleManifest manifest, string directory,
        IReadOnlyList<ModuleSigningKey>? keys = null, IReadOnlyList<ModuleRevocation>? revoked = null)
    {
        keys ??= Trusted; revoked ??= Revoked;
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return new(ModuleSignatureStatus.Unsigned, ModuleSignatureChannel.Release, null, null, null, null);
        SigFile? file;
        try
        {
            ModuleManifestReader.Unlinked(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 65536) return Invalid("malformed", null);
            file = JsonSerializer.Deserialize<SigFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return Invalid("malformed", null);
        }
        if (file is null) return Invalid("malformed", null);
        // ES256: ECDSA P-256 with SHA-256; keys are base64 SubjectPublicKeyInfo. (Ed25519 is not available on this runtime yet.)
        if (!string.Equals(file.Alg, "ES256", StringComparison.OrdinalIgnoreCase)) return Invalid("unverifiable", file.KeyId);
        ModuleSigningKey? key = null;
        foreach (var candidate in keys) if (candidate.Id == file.KeyId) { key = candidate; break; }
        if (key is null) return Invalid("unknown-key", file.KeyId);
        if (key.Revoked) return Invalid("revoked-key", file.KeyId);
        // Channel authority belongs to the host's trusted key, never to editable package metadata.
        string channel = key.Channel == ModuleSignatureChannel.Beta ? "beta" : "release";
        if (!string.Equals(file.Channel, channel, StringComparison.OrdinalIgnoreCase)) return Invalid("channel-mismatch", file.KeyId);
        foreach (var entry in revoked)
            if (entry.Id == file.ModId && (entry.Version == "*" || entry.Version == file.ModVersion)) return Invalid("revoked-mod", file.KeyId);
        if (file.ModId != manifest.Id || file.ModVersion != manifest.Version) return Invalid("mod-mismatch", file.KeyId);
        string? digest = Digest(directory);
        if (digest is null) return Invalid("unverifiable", file.KeyId);
        if (!string.Equals(file.Digest, "sha256:" + digest, StringComparison.OrdinalIgnoreCase)) return Invalid("digest-mismatch", file.KeyId);
        // The signed message is fixed, so changing the version or the digest invalidates a signature.
        byte[] message = Encoding.UTF8.GetBytes(string.Join('\n', "brimdeck-signature/1", "modId=" + file.ModId,
            "modVersion=" + file.ModVersion, "digest=" + file.Digest, "keyId=" + file.KeyId, "signedAt=" + file.SignedAt) + "\n");
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out _);
            if (!ecdsa.VerifyData(message, Convert.FromBase64String(file.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                return Invalid("bad-signature", file.KeyId);
        }
        catch (Exception error) when (error is FormatException or CryptographicException or ArgumentException or PlatformNotSupportedException)
        {
            return Invalid("bad-signature", file.KeyId);
        }
        bool beta = key.Channel == ModuleSignatureChannel.Beta;
        return new(beta ? ModuleSignatureStatus.Beta : ModuleSignatureStatus.Verified,
            beta ? ModuleSignatureChannel.Beta : ModuleSignatureChannel.Release, file.KeyId, file.SignedAt, digest, null);
    }

    private static ModuleSignature Invalid(string reason, string? keyId)
        => new(ModuleSignatureStatus.Invalid, ModuleSignatureChannel.Release, keyId, null, null, reason);

    // Signature digest v1, separate from the one that binds enablement approvals: one line per file
    // ("<sha256>  <relative path>") sorted by code point, then hashed again. Files the OS drops into a folder
    // are excluded so browsing a package cannot break its signature. Links are refused.
    internal static string? Digest(string directory)
    {
        try
        {
            var lines = new List<string>();
            long total = 0;
            foreach (string file in ModuleCatalog.Files(directory))
            {
                string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (string.Equals(relative, FileName, StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileName(file);
                if (string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                ModuleManifestReader.Unlinked(file);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                total += stream.Length;
                if (total > 64L * 1024 * 1024) return null;
                lines.Add(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() + "  " + relative.Normalize(NormalizationForm.FormC));
            }
            lines.Sort(StringComparer.Ordinal);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"))).ToLowerInvariant();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
