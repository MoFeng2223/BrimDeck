namespace BrimDeck.Modules;

public enum ModuleSignatureStatus { Unsigned, Verified, Beta, Invalid }
public enum ModuleSignatureChannel { Release, Beta }

// A trusted signing key. Rotating or revoking one needs a new host release; there is no network lookup.
public sealed record ModuleSigningKey(string Id, ModuleSignatureChannel Channel, string PublicKey, bool Revoked = false);
public sealed record ModuleRevocation(string Id, string Version);

public sealed record ModuleSignature(
    ModuleSignatureStatus Status, ModuleSignatureChannel Channel, string? KeyId, string? SignedAt, string? Digest, string? Reason);
