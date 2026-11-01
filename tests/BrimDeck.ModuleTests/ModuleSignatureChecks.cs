using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrimDeck.Modules;

internal static partial class Program
{
    private static void ModuleSignatureChecks()
    {
        string Root() { string root = Path.Combine(_root, "signature-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
        string Package(string root, string version = "1.0.0")
        {
            string dir = Path.Combine(root, "mods", "signed"); Directory.CreateDirectory(dir);
            var manifest = new ModuleManifest { Id = "signed", Name = "signed", Version = version, Assembly = "Example.dll", EntryType = "Example.Entry" };
            File.WriteAllText(Path.Combine(dir, "module.json"), JsonSerializer.Serialize(manifest));
            File.WriteAllText(Path.Combine(dir, "Example.dll"), "fixture");
            return dir;
        }
        void Sign(string dir, ModuleManifest manifest, ECDsa key, string keyId, string channel, string? version = null)
        {
            string digest = "sha256:" + ModuleSignatureVerifier.Digest(dir)!;
            string signedAt = "2026-10-26T00:00:00Z", versionText = version ?? manifest.Version;
            byte[] message = Encoding.UTF8.GetBytes(string.Join('\n', "brimdeck-signature/1", "modId=" + manifest.Id,
                "modVersion=" + versionText, "digest=" + digest, "keyId=" + keyId, "signedAt=" + signedAt) + "\n");
            var file = new
            {
                alg = "ES256", keyId, channel, modId = manifest.Id, modVersion = versionText, digest, signedAt,
                signature = Convert.ToBase64String(key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            };
            File.WriteAllText(Path.Combine(dir, ModuleSignatureVerifier.FileName), JsonSerializer.Serialize(file));
        }
        ModuleManifest Manifest(string version = "1.0.0") => new() { Id = "signed", Name = "signed", Version = version, Assembly = "Example.dll", EntryType = "Example.Entry" };

        using var release = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var beta = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string releaseKey = Convert.ToBase64String(release.ExportSubjectPublicKeyInfo());
        string betaKey = Convert.ToBase64String(beta.ExportSubjectPublicKeyInfo());
        var keys = new[]
        {
            new ModuleSigningKey("brimdeck-ci-2026-1", ModuleSignatureChannel.Release, releaseKey),
            new ModuleSigningKey("brimdeck-beta-2026-1", ModuleSignatureChannel.Beta, betaKey),
            new ModuleSigningKey("retired", ModuleSignatureChannel.Release, releaseKey, Revoked: true),
        };

        {
            string root = Root(); string dir = Package(root); Sign(dir, Manifest(), release, "brimdeck-ci-2026-1", "release");
            var result = ModuleSignatureVerifier.Verify(Manifest(), dir, keys);
            Check("a package signed by a trusted release key verifies", result.Status == ModuleSignatureStatus.Verified && result.KeyId == "brimdeck-ci-2026-1" && result.Digest is not null);
        }
        {
            string root = Root(); string dir = Package(root); Sign(dir, Manifest(), beta, "brimdeck-beta-2026-1", "beta");
            Check("a beta key reports the beta channel", ModuleSignatureVerifier.Verify(Manifest(), dir, keys).Status == ModuleSignatureStatus.Beta);
            string signatureFile = Path.Combine(dir, ModuleSignatureVerifier.FileName);
            string original = File.ReadAllText(signatureFile);
            File.WriteAllText(signatureFile, original.Replace("\"channel\":\"beta\"", "\"channel\":\"release\""));
            Check("beta signature cannot promote itself by editing unsigned channel metadata", ModuleSignatureVerifier.Verify(Manifest(), dir, keys).Status == ModuleSignatureStatus.Invalid);
            File.WriteAllText(signatureFile, original.Replace("\"channel\":\"beta\"", "\"channel\":\"preview\""));
            Check("unknown signature channels are rejected", ModuleSignatureVerifier.Verify(Manifest(), dir, keys).Status == ModuleSignatureStatus.Invalid);
        }
        {
            string root = Root(); string dir = Package(root); Sign(dir, Manifest(), release, "brimdeck-ci-2026-1", "release");
            File.WriteAllText(Path.Combine(dir, "Example.dll"), "tampered");
            Check("changing a file after signing fails the digest", ModuleSignatureVerifier.Verify(Manifest(), dir, keys).Reason == "digest-mismatch");
        }
        {
            string root = Root(); string dir = Package(root, "1.1.0"); Sign(dir, Manifest("1.1.0"), release, "brimdeck-ci-2026-1", "release", version: "1.0.0");
            Check("a signature for another version is refused", ModuleSignatureVerifier.Verify(Manifest("1.1.0"), dir, keys).Reason == "mod-mismatch");
        }
        {
            string root = Root(); string dir = Package(root); Sign(dir, Manifest(), release, "nobody", "release");
            Check("an unknown key is refused", ModuleSignatureVerifier.Verify(Manifest(), dir, keys).Reason == "unknown-key");
        }
        {
            string root = Root(); string dir = Package(root); Sign(dir, Manifest(), release, "retired", "release");
            Check("a revoked key is refused", ModuleSignatureVerifier.Verify(Manifest(), dir, keys).Reason == "revoked-key");
        }
        {
            string root = Root(); string dir = Package(root); Sign(dir, Manifest(), release, "brimdeck-ci-2026-1", "release");
            Check("a revoked module version is refused",
                ModuleSignatureVerifier.Verify(Manifest(), dir, keys, [new ModuleRevocation("signed", "1.0.0")]).Reason == "revoked-mod");
        }
        {
            string root = Root(); string dir = Package(root);
            Check("a package without a signature reads as unsigned", ModuleSignatureVerifier.Verify(Manifest(), dir, keys).Status == ModuleSignatureStatus.Unsigned);
        }
        {
            string root = Root(); string dir = Package(root); Sign(dir, Manifest(), release, "brimdeck-ci-2026-1", "release");
            File.WriteAllText(Path.Combine(dir, ".DS_Store"), "junk");
            Check("OS files the shell drops in do not break a signature", ModuleSignatureVerifier.Verify(Manifest(), dir, keys).Status == ModuleSignatureStatus.Verified);
        }
        Check("the shipped key list is empty today", ModuleSignatureVerifier.Trusted.Count == 0 && ModuleSignatureVerifier.Revoked.Count == 0);
    }
}
