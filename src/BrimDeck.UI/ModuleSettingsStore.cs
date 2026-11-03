using System.IO;
using System.Text.Json;
using BrimDeck.Modules;

namespace BrimDeck;

public sealed record ModuleSettingsSnapshot(long Revision, IReadOnlyDictionary<string, object> Values);
public sealed record ModuleSettingsPatch(long ExpectedRevision, IReadOnlyDictionary<string, object> Values);
public enum ModuleSettingsWriteStatus { Saved, Conflict, Invalid }
public sealed record ModuleSettingsWriteResult(ModuleSettingsWriteStatus Status, long Revision, string? Error = null);
public interface IModuleVersionedSettings : IModuleSettings
{
    ModuleSettingsSnapshot GetSnapshot();
    ModuleSettingsWriteResult Apply(ModuleSettingsPatch patch);
}
public interface IModuleCustomSettings : IModuleSettings
{
    IModuleView CreateSettingsView(ModuleViewContext context);
}

// A single validated value owner for forms, programmatic patches, imports and reset.
public sealed class ModuleSettingsStore : IModuleVersionedSettings
{
    private sealed record Saved(int SchemaVersion, long Revision, Dictionary<string, JsonElement> Values);
    private readonly object _gate = new();
    private readonly IModuleStorage _storage;
    private readonly string _key;
    private Saved _saved;
    public string Name { get; }
    public string NameEn { get; }
    public IReadOnlyList<ModuleSetting> Fields { get; }
    public bool Visible => true;
    public ModuleSettingsStore(string name, string nameEn, IReadOnlyList<ModuleSetting> fields, IModuleStorage storage, string key = "settings")
    {
        Name = name; NameEn = nameEn; Fields = Array.AsReadOnly(fields.Select(field => field with { Options = Array.AsReadOnly(field.Options.ToArray()) }).ToArray()); _storage = storage; _key = key;
        if (Fields.Count > 128 || Fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() != Fields.Count) throw new ArgumentException("Settings require at most 128 unique fields.");
        foreach (var field in Fields) _ = Validate(field, field.DefaultValue ?? throw new ArgumentException("Schema fields need a default value: " + field.Key));
        var result = storage.ReadResult<Saved>(key);
        if (result.Status is ModuleReadStatus.Corrupt or ModuleReadStatus.Unsupported) throw new InvalidDataException(result.Error ?? "Settings cannot be read safely.");
        _saved = result.Value ?? new(1, 0, []);
        if (_saved.SchemaVersion != 1 || _saved.Revision < 0 || _saved.Values is null) throw new InvalidDataException("Unsupported settings schema; original data was preserved.");
        foreach (var field in Fields) if (_saved.Values.TryGetValue(field.Key, out var value)) _ = Validate(field, Decode(field, value));
    }
    public object Read(string key)
    {
        lock (_gate)
        {
            var field = Fields.SingleOrDefault(f => f.Key == key) ?? throw new ArgumentException("Unknown setting: " + key);
            return _saved.Values.TryGetValue(key, out var value) ? Validate(field, Decode(field, value)) : Validate(field, field.DefaultValue!);
        }
    }
    public ModuleSettingsSnapshot GetSnapshot()
    { lock (_gate) return new(_saved.Revision, new System.Collections.ObjectModel.ReadOnlyDictionary<string, object>(Fields.ToDictionary(f => f.Key, f => Read(f.Key), StringComparer.Ordinal))); }
    public ModuleSettingsWriteResult Apply(ModuleSettingsPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        lock (_gate)
        {
            if (patch.ExpectedRevision != _saved.Revision) return new(ModuleSettingsWriteStatus.Conflict, _saved.Revision);
            var values = new Dictionary<string, JsonElement>(_saved.Values, StringComparer.Ordinal);
            try
            {
                ArgumentNullException.ThrowIfNull(patch.Values);
                foreach (var pair in patch.Values)
                {
                    var field = Fields.SingleOrDefault(f => f.Key == pair.Key) ?? throw new ArgumentException("Unknown setting: " + pair.Key);
                    values[pair.Key] = JsonSerializer.SerializeToElement(Validate(field, pair.Value));
                }
            }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
            { return new(ModuleSettingsWriteStatus.Invalid, _saved.Revision, error.Message); }
            var next = new Saved(1, checked(_saved.Revision + 1), values);
            _storage.Write(_key, next); _saved = next;
            return new(ModuleSettingsWriteStatus.Saved, next.Revision);
        }
    }
    public void Write(string key, object value)
    {
        lock (_gate)
        {
            var result = Apply(new(_saved.Revision, new Dictionary<string, object> { [key] = value }));
            if (result.Status != ModuleSettingsWriteStatus.Saved) throw new ArgumentException(result.Error ?? result.Status.ToString());
        }
    }
    public void Reset()
    { lock (_gate) { var next = new Saved(1, checked(_saved.Revision + 1), []); _storage.Write(_key, next); _saved = next; } }
    private static object Decode(ModuleSetting field, JsonElement value) => field.Kind switch
    {
        ModuleSettingKind.Toggle => value.GetBoolean(), ModuleSettingKind.Number => value.GetDouble(), ModuleSettingKind.Choice => value.GetInt32(),
        _ => value.GetString() ?? ""
    };
    public static object Validate(ModuleSetting field, object value)
    {
        if (string.IsNullOrWhiteSpace(field.Key) || field.Key.Length > 80 || field.Key.Any(char.IsControl)) throw new ArgumentException("Invalid setting key.");
        return field.Kind switch
        {
            ModuleSettingKind.Toggle when value is bool => value,
            ModuleSettingKind.Number when value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => Number(),
            ModuleSettingKind.Choice when value is int choice && field.Options is { Count: > 0 and <= 128 } && field.Options.Select(o => o.Value).Distinct().Count() == field.Options.Count && field.Options.Any(o => o.Value == choice) => value,
            ModuleSettingKind.Text when value is string text && field.MaximumLength is >= 1 and <= 8192 && text.Length <= field.MaximumLength => value,
            ModuleSettingKind.Color when value is string color && color.Length == 7 && color[0] == '#' && color.AsSpan(1).ToArray().All(Uri.IsHexDigit) => color.ToUpperInvariant(),
            _ => throw new ArgumentException("Invalid setting value: " + field.Key)
        };
        object Number()
        {
            double number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            if (!double.IsFinite(number) || !double.IsFinite(field.Minimum) || !double.IsFinite(field.Maximum) || field.Minimum >= field.Maximum || !double.IsFinite(field.Step) || field.Step <= 0 || number < field.Minimum || number > field.Maximum)
                throw new ArgumentException("Invalid numeric setting: " + field.Key);
            return number;
        }
    }
}
