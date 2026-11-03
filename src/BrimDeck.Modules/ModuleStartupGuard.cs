using System.Security.Cryptography;
using System.Text.Json;

namespace BrimDeck.Modules;

internal sealed record ModuleStartupRecovery(bool Required, IReadOnlyList<string> Modules, bool Unreadable);

// Host-owned crash journal. Reading it never loads an assembly or changes saved module preferences.
internal sealed class ModuleStartupGuard
{
    private sealed record Marker(int Version, Guid Run, DateTimeOffset StartedAt, string[] Modules);
    private sealed record Dismissed(string Identity);
    private readonly string _marker, _dismissed;
    private readonly TimeProvider _clock;
    private string? _previousIdentity, _activeIdentity;
    private long _lastHeartbeat;
    private int _heartbeats;
    private bool _firstFrame;
    internal ModuleStartupRecovery Recovery { get; }
    internal string? Error { get; private set; }
    internal bool Armed => _activeIdentity is not null;

    internal ModuleStartupGuard(string directory, TimeProvider? clock = null)
    {
        _marker = Path.Combine(directory, "module-startup.json");
        _dismissed = Path.Combine(directory, "module-startup-dismissed.json");
        _clock = clock ?? TimeProvider.System;
        try
        {
            if (!File.Exists(_marker)) { Recovery = new(false, [], false); return; }
            byte[] bytes = ReadBounded(_marker);
            _previousIdentity = Identity(bytes);
            try
            {
                if (File.Exists(_dismissed) && JsonSerializer.Deserialize<Dismissed>(ReadBounded(_dismissed))?.Identity == _previousIdentity)
                { Recovery = new(false, [], false); return; }
            }
            catch (Exception error) when (FileError(error) || error is JsonException) { /* A bad dismissal cannot hide a pending start. */ }
            var marker = JsonSerializer.Deserialize<Marker>(bytes);
            bool valid = marker is { Version: 1 } && marker.Run != Guid.Empty && marker.Modules is not null;
            Recovery = new(true, valid ? Array.AsReadOnly(marker!.Modules!.Where(ModuleIds.Valid).Take(128).ToArray()) : [], !valid);
        }
        catch (Exception error) when (FileError(error) || error is JsonException)
        {
            // Corruption is a recovery reason, not permission to start code silently.
            Recovery = new(true, [], true); Error = error.Message;
        }
    }
    internal void FirstFramePresented() => _firstFrame = true;
    internal void Heartbeat()
    { _lastHeartbeat = _clock.GetTimestamp(); if (Armed) _heartbeats++; }

    internal bool AcknowledgeRecovery()
    {
        try
        {
            if (_previousIdentity is not null) return Retire(_previousIdentity);
            if (Recovery.Required) { Error = "Could not identify the previous journal; it was left untouched."; return false; }
            Error = null; return true;
        }
        catch (Exception error) when (FileError(error)) { Error = error.Message; return false; }
    }
    internal bool Begin(IEnumerable<string> moduleIds)
    {
        if (Armed) return true;
        try
        {
            var ids = moduleIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(128).ToArray();
            if (ids.Length == 0) return true;
            if (ids.Any(id => !ModuleIds.Valid(id))) throw new ArgumentException("Invalid module id in startup journal.");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Marker(1, Guid.NewGuid(), _clock.GetUtcNow(), ids));
            WriteAtomic(_marker, bytes);
            _activeIdentity = Identity(bytes); _heartbeats = 0; _lastHeartbeat = _clock.GetTimestamp(); Error = null;
            return true;
        }
        catch (Exception error) when (FileError(error)) { Error = error.Message; return false; }
    }
    internal bool TryFinish(bool activationPending)
    {
        if (!Armed || activationPending || !_firstFrame || _heartbeats < 2 || !RecentlyResponsive()) return false;
        if (!Retire(_activeIdentity!)) return false;
        _activeIdentity = null; return true;
    }
    internal bool NormalExit()
    {
        if (!Armed) return true;
        if (!RecentlyResponsive()) return false;
        if (!Retire(_activeIdentity!)) return false;
        _activeIdentity = null; return true;
    }
    private bool RecentlyResponsive() => _clock.GetElapsedTime(_lastHeartbeat, _clock.GetTimestamp()) <= TimeSpan.FromSeconds(3);
    private bool Retire(string identity)
    {
        try
        {
            if (!File.Exists(_marker)) { Error = null; return true; }
            if (Identity(ReadBounded(_marker)) != identity) { Error = "Startup journal belongs to another run."; return false; }
            try { File.Delete(_marker); }
            catch (Exception error) when (FileError(error))
            {
                // Bind the dismissal to these exact bytes. A later journal must never be dismissed by an old run.
                WriteAtomic(_dismissed, JsonSerializer.SerializeToUtf8Bytes(new Dismissed(identity)));
            }
            Error = null; return true;
        }
        catch (Exception error) when (FileError(error)) { Error = error.Message; return false; }
    }
    private static string Identity(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 65536) throw new InvalidDataException("Startup journal exceeds 64 KiB.");
        byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool FileError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException;
}
