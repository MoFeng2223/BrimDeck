using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck.Native;

internal static class ClaudeDesktopReader
{
    public static ClaudeDesktopState Read(DataLocations locations)
    {
        var profiles = locations.ClaudeDesktopProfiles();
        bool failed = false;
        foreach (var profile in profiles)
        {
            try
            {
                var configPath = Path.Combine(profile, "config.json");
                if (!File.Exists(configPath)) continue;
                using var config = BrimDeck.Core.SharedFile.Parse(configPath);
                var root = config.RootElement;
                // V2 is authoritative, including cleared entries after sign-out.
                var encrypted = root.TryGetProperty("oauth:tokenCacheV2", out var v2)
                    ? v2.Text() : root.Get("oauth:tokenCache").Text();
                if (encrypted.Length == 0) continue;
                using var state = BrimDeck.Core.SharedFile.Parse(Path.Combine(profile, "Local State"));
                var wrappedKey = Convert.FromBase64String(state.RootElement.Get("os_crypt").Get("encrypted_key").Text());
                if (!wrappedKey.AsSpan().StartsWith("DPAPI"u8)) throw new CryptographicException();
                var key = ProtectedData.Unprotect(wrappedKey.AsSpan(5).ToArray(), null, DataProtectionScope.CurrentUser);
                byte[]? plain = null;
                try
                {
                    var data = Convert.FromBase64String(encrypted);
                    if (data.Length < 31 || !data.AsSpan().StartsWith("v10"u8)) throw new CryptographicException();
                    plain = new byte[data.Length - 31];
                    using var aes = new AesGcm(key, 16);
                    aes.Decrypt(data.AsSpan(3, 12), data.AsSpan(15, plain.Length), data.AsSpan(data.Length - 16), plain);
                    using var cache = JsonDocument.Parse(plain);
                    var credentials = ClaudeDesktopCache.Parse(cache.RootElement, root.Get("lastKnownAccountUuid").Text(), DateTimeOffset.UtcNow);
                    if (credentials.Count > 0) return new(true, credentials);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                    if (plain is not null) CryptographicOperations.ZeroMemory(plain);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException)
            { failed = true; }
        }
        return new(profiles.Count > 0, [], failed);
    }
}
