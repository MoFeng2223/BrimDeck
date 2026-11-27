using System.Text;
using System.Text.Json;

namespace BrimDeck.Protocol;

// What the transcript of a Claude Code session (and of the tools built on it: CodeAgent, Qoder) says that no hook does: the
// user refused a tool in the tool's own prompt, or stopped the turn. The file is one JSON record a line. The form is the
// tools' own and promised by nobody, so whatever cannot be read says nothing, and the session is then left to its hooks.
//
// - A call: {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{...}}]}}
// - Its result: {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1",...}]}}; a refusal is a
//   result too (an error with the tool's words for it).
// - A stopped turn: {"type":"user","message":{"content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]}},
//   or "[Request interrupted by user]" when no tool ran.
public static class AgentTranscripts
{
    public const string InterruptMark = "[Request interrupted by user";

    // One line as a record; null when it is none.
    public static JsonDocument? Record(string line)
    {
        if (line.Length < 2 || line[0] != '{') return null;
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
    }

    // The parts of a record's message, when it is of the given type.
    private static IEnumerable<JsonElement> Parts(JsonElement record, string type)
    {
        if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != type) yield break;
        if (!record.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content)) yield break;
        if (content.ValueKind == JsonValueKind.String) { yield return content; yield break; }
        if (content.ValueKind != JsonValueKind.Array) yield break;
        foreach (var part in content.EnumerateArray()) yield return part;
    }

    private static string? Word(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Whether the record says that the user stopped the turn.
    public static bool Interrupted(JsonElement record)
    {
        foreach (var part in Parts(record, "user"))
        {
            string? text = part.ValueKind == JsonValueKind.String ? part.GetString() : Word(part, "type") == "text" ? Word(part, "text") : null;
            if (text is not null && text.StartsWith(InterruptMark, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // Adds the ids of the record's calls of the tool with these arguments (call: AgentHook.Stamp).
    public static void Calls(JsonElement record, string tool, string call, ISet<string> ids)
    {
        foreach (var part in Parts(record, "assistant"))
            if (Word(part, "type") == "tool_use" && Word(part, "name") == tool && Word(part, "id") is { Length: > 0 } id
                && AgentHook.Stamp(tool, part.TryGetProperty("input", out var input) ? input : default) == call)
                ids.Add(id);
    }

    // Whether the record holds the result of one of the calls.
    public static bool Result(JsonElement record, ISet<string> ids)
    {
        if (ids.Count == 0) return false;
        foreach (var part in Parts(record, "user"))
            if (Word(part, "type") == "tool_result" && Word(part, "tool_use_id") is { } id && ids.Contains(id)) return true;
        return false;
    }

    // The file's length, or -1 when it is not there or cannot be read.
    public static long Length(string file)
    {
        try { var info = new FileInfo(file); return info.Exists ? info.Length : -1; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return -1; }
    }

    // The whole lines between two places of the file; a line cut by either end is left out or, at the start, read as it is
    // (it then fails as a record).
    public static List<string> Lines(string file, long from, long to)
    {
        var tail = new TranscriptTail(file, from);
        var lines = new List<string>();
        while (tail.Position < to)
        {
            long before = tail.Position;
            lines.AddRange(tail.Read((int)Math.Min(TranscriptTail.Chunk, to - before)));
            if (tail.Position == before) break;
        }
        return lines;
    }
}

// Reads what is added to a transcript, from a place in it: the whole lines added since the last read. The tool keeps the file
// open and appends to it, so it is opened for a moment each time and shared with every other use.
public sealed class TranscriptTail(string file, long from)
{
    // Read at most at once. A record longer than this (a tool's long output) is passed over.
    public const int Chunk = 1 << 20;

    // Where the next read starts: after the last whole line read.
    public long Position { get; private set; } = Math.Max(0, from);
    // The rest of a line longer than a chunk is skipped up to its end.
    private bool _skipping;

    public List<string> Read(int limit = Chunk)
    {
        var lines = new List<string>();
        byte[] bytes;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // A file that became shorter was written anew: start over from its end.
            if (stream.Length < Position) { Position = stream.Length; _skipping = false; return lines; }
            if (stream.Length == Position) return lines;
            stream.Seek(Position, SeekOrigin.Begin);
            bytes = new byte[(int)Math.Min(Math.Clamp(limit, 1, Chunk), stream.Length - Position)];
            int read = 0;
            while (read < bytes.Length && stream.Read(bytes, read, bytes.Length - read) is > 0 and var n) read += n;
            if (read < bytes.Length) Array.Resize(ref bytes, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return lines; }
        int start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            if (_skipping) _skipping = false;
            else lines.Add(Encoding.UTF8.GetString(bytes, start, i - start).TrimEnd('\r'));
            start = i + 1;
        }
        // No line ended in a full chunk: it is longer than one, and is passed over.
        if (start == 0 && bytes.Length >= Chunk) { _skipping = true; start = bytes.Length; }
        Position += start;
        return lines;
    }
}
