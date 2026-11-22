using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace BrimDeck.Core.Agenda;

// How reading agenda.json went. Empty: there is no file yet. Loaded: read. Recovered: the file could not be understood; it was
// renamed (KeptAs) and the planner starts empty. Unavailable (the file is in use, the drive does not answer) and Denied (no
// permission): nothing was read, and nothing may be written until a later reading succeeds.
public enum AgendaReadState { Empty, Loaded, Recovered, Unavailable, Denied }

public sealed record AgendaRead(AgendaReadState State, AgendaData Data, string? KeptAs = null)
{
    // The file may be written over: it was read, or there was none (left), or it was moved aside.
    public bool Writable => State is AgendaReadState.Empty or AgendaReadState.Loaded or AgendaReadState.Recovered;
}

// agenda.json in BrimDeck's data folder: plain UTF-8 JSON, not encrypted, since it is personal data to keep for years and
// Windows' per-account encryption cannot be read back after Windows is reinstalled. "format" is the version of the layout.
// Reading keeps fields it does not know and writes them back; an item that does not make sense is dropped alone.
public sealed class AgendaStore(string directory)
{
    public const string FileName = "agenda.json";
    public const int Format = 1;
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, FileName);

    private static readonly JsonSerializerOptions Written = new() { WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    // now names a file moved aside ("agenda.broken-20261122-142000.json").
    public AgendaRead Read(DateTime now)
    {
        string text;
        try
        {
            if (!File.Exists(FilePath)) return new(AgendaReadState.Empty, new());
            text = File.ReadAllText(FilePath);
        }
        catch (UnauthorizedAccessException) { return new(AgendaReadState.Denied, new()); }
        catch (IOException) { return new(AgendaReadState.Unavailable, new()); }
        AgendaData data;
        try { data = Parse(text); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            try { return new(AgendaReadState.Recovered, new(), KeepBroken(now)); }
            catch (UnauthorizedAccessException) { return new(AgendaReadState.Denied, new()); }
            catch (IOException) { return new(AgendaReadState.Unavailable, new()); }
        }
        return new(AgendaReadState.Loaded, data);
    }

    // Renames the unreadable file beside itself and returns the new name.
    private string KeepBroken(DateTime now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var name = $"agenda.broken-{stamp}.json";
        for (int i = 2; File.Exists(Path.Combine(DirectoryPath, name)); i++) name = $"agenda.broken-{stamp}-{i}.json";
        File.Move(FilePath, Path.Combine(DirectoryPath, name));
        return name;
    }

    // Writes the whole file through a temporary file, so a write cut short leaves the last one whole. Throws IOException or
    // UnauthorizedAccessException when it cannot.
    public void Write(AgendaData data) => Write(Serialize(data));
    public void Write(string text)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, FilePath, true);
    }

    // ---- The layout ----

    public static string Serialize(AgendaData data)
    {
        var root = data.Extra?.DeepClone().AsObject() ?? [];
        root["format"] = Format;
        root["events"] = new JsonArray([.. data.Events.Select(Node)]);
        root["todos"] = new JsonArray([.. data.Todos.Select(Node)]);
        var notes = new JsonObject();
        foreach (var (day, text) in data.Notes.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).OrderBy(pair => pair.Key)) notes[AgendaTime.Text(day)] = text;
        root["notes"] = notes;
        return root.ToJsonString(Written);
    }

    // Throws JsonException when the text is not a JSON object.
    public static AgendaData Parse(string text)
    {
        var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
            ?? throw new JsonException("agenda.json is not an object");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var data = new AgendaData { Extra = Rest(root, "format", "events", "todos", "notes") };
        foreach (var node in root["events"] as JsonArray ?? [])
            if (node is JsonObject item && Event(item) is { } read && ids.Add(read.Id)) data.Events.Add(read);
        foreach (var node in root["todos"] as JsonArray ?? [])
            if (node is JsonObject item && Todo(item) is { } read && ids.Add(read.Id)) data.Todos.Add(read);
        if (root["notes"] is JsonObject notes)
            foreach (var (key, value) in notes)
                if (AgendaTime.ParseDay(key) is { } day && Text(value) is { } note && !string.IsNullOrWhiteSpace(note)) data.Notes[day] = note;
        return data;
    }

    private static readonly string[] EventKeys = ["id", "title", "start", "end", "allDay", "detail", "source", "created"];
    private static readonly string[] TodoKeys = ["id", "text", "day", "due", "done", "doneAt", "source", "created"];
    private static readonly string[] SourceKeys = ["notice", "app", "appName", "sender", "icon"];

    private static JsonObject Node(AgendaEvent item)
    {
        var node = item.Extra?.DeepClone().AsObject() ?? [];
        node["id"] = item.Id; node["title"] = item.Title;
        node["start"] = item.AllDay ? AgendaTime.Text(item.FirstDay) : AgendaTime.Text(item.Start);
        node["end"] = item.AllDay ? AgendaTime.Text(DateOnly.FromDateTime(item.End)) : AgendaTime.Text(item.End);
        node["allDay"] = item.AllDay; node["detail"] = item.Detail; node["source"] = Node(item.Source); node["created"] = AgendaTime.Text(item.Created);
        return node;
    }

    private static JsonObject Node(AgendaTodo item)
    {
        var node = item.Extra?.DeepClone().AsObject() ?? [];
        node["id"] = item.Id; node["text"] = item.Text;
        node["day"] = item.Day is { } day ? AgendaTime.Text(day) : null;
        node["due"] = item.Due is { } due ? AgendaTime.Text(due) : null;
        node["done"] = item.Done; node["doneAt"] = item.DoneAt is { } at ? AgendaTime.Text(at) : null;
        node["source"] = Node(item.Source); node["created"] = AgendaTime.Text(item.Created);
        return node;
    }

    private static JsonObject? Node(AgendaSource? source)
    {
        if (source is null) return null;
        var node = source.Extra?.DeepClone().AsObject() ?? [];
        node["notice"] = source.NoticeId; node["app"] = source.AppKey; node["appName"] = source.AppName;
        node["sender"] = source.Sender; node["icon"] = source.Icon;
        return node;
    }

    // An event that makes sense, put in shape (AgendaBook.Shape), or null. A missing id is made up; a missing creation time
    // takes the start's.
    private static AgendaEvent? Event(JsonObject node)
    {
        if (Text(node["title"]) is not { } title || AgendaTime.Parse(Text(node["start"])) is not { } start) return null;
        bool allDay = node["allDay"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        var end = AgendaTime.Parse(Text(node["end"]));
        if (!allDay && end is null) return null;
        var created = AgendaTime.Parse(Text(node["created"])) ?? start;
        var item = AgendaBook.Shape(Id(node), title, start, end, allDay, Text(node["detail"]), Source(node["source"]), created);
        return item is null ? null : item with { Extra = Rest(node, EventKeys) };
    }

    private static AgendaTodo? Todo(JsonObject node)
    {
        if (Text(node["text"]) is not { } text || string.IsNullOrWhiteSpace(text)) return null;
        bool done = node["done"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        // A day that is there but cannot be read makes the to-do unreadable, rather than moving it to "长期".
        if (!Day(node, "day", out var day) || !Day(node, "due", out var due)) return null;
        var created = AgendaTime.Parse(Text(node["created"])) ?? DateTime.MinValue;
        var doneAt = done ? AgendaTime.Parse(Text(node["doneAt"])) ?? created : (DateTime?)null;
        return new AgendaTodo
        {
            Id = Id(node), Text = text.Trim(), Day = day, Due = due, Done = done, DoneAt = doneAt is { } at ? AgendaTime.Second(at) : null,
            Source = Source(node["source"]), Created = AgendaTime.Second(created), Extra = Rest(node, TodoKeys)
        };
    }

    private static bool Day(JsonObject node, string key, out DateOnly? day)
    {
        day = null;
        if (node[key] is null) return true;
        day = AgendaTime.ParseDay(Text(node[key]));
        return day is not null;
    }

    private static AgendaSource? Source(JsonNode? node)
    {
        if (node is not JsonObject source || Text(source["notice"]) is not { Length: > 0 } notice) return null;
        return new AgendaSource
        {
            NoticeId = notice, AppKey = Text(source["app"]) ?? "", AppName = Text(source["appName"]) ?? "",
            Sender = Text(source["sender"]), Icon = Text(source["icon"]), Extra = Rest(source, SourceKeys)
        };
    }

    private static string Id(JsonObject node) => Text(node["id"]) is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N");
    private static string? Text(JsonNode? node) => node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // The fields of node other than known ones, or null when there are none.
    private static JsonObject? Rest(JsonObject node, params string[] known)
    {
        var rest = node.Where(pair => !known.Contains(pair.Key)).ToList();
        return rest.Count == 0 ? null : new JsonObject(rest.Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
    }
}
