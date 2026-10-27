using System.Text.Json;

namespace BrimDeck.Modules;

internal static class ModuleManifestReader
{
    internal static void Unlinked(string path, bool allowMissing = false)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked module paths are not supported."); }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
        }
    }
    internal static JsonDocument Open(string file)
    {
        Unlinked(file);
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 1024 * 1024) throw new InvalidDataException("Manifest exceeds 1 MiB.");
        var document = JsonDocument.Parse(input);
        if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new InvalidDataException("module.json must be an object."); }
        return document;
    }
    internal static ModuleManifest Read(string file) { using var document = Open(file); return Deserialize(document.RootElement); }
    internal static ModuleManifest Deserialize(JsonElement root)
    {
        void Keys(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) return;
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
                if (!keys.Add(property.Name)) throw new ModuleApiException("duplicate-manifest-field", "Duplicate manifest field: " + property.Name);
        }
        Keys(root);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("requires", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("host", StringComparison.OrdinalIgnoreCase)) Keys(property.Value);
            if (property.Name.Equals("dependencies", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
                foreach (var dependency in property.Value.EnumerateArray()) Keys(dependency);
        }
        return root.Deserialize<ModuleManifest>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing manifest.");
    }
}
