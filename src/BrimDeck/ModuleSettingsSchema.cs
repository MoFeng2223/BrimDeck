using System.Globalization;
using BrimDeck.Modules;

namespace BrimDeck;

internal sealed record ModuleSettingValue(ModuleSetting Definition, object Value);

internal static class ModuleSettingsSchema
{
    // Stop calling further getters once their combined time exceeds the budget. This cannot interrupt
    // an individual synchronous getter or a blocked Fields/options enumerator on the UI thread.
    private const long ReadBudgetMilliseconds = 250;
    internal static IReadOnlyList<ModuleSettingValue> Read(ModuleHost host, Contribution<IModuleSettings> entry)
    {
        var fields = host.Invoke(entry.ModuleId, "settings schema", () => entry.Value.Fields.Take(129).ToArray()) ?? [];
        var result = new List<ModuleSettingValue>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Warn(string source, string code, string message) => host.Diagnose(entry.ModuleId, source, code, message);
        if (fields.Length > 128) Warn(entry.Id, "settings-field-limit", "Only the first 128 settings fields are displayed.");
        var budget = System.Diagnostics.Stopwatch.StartNew();
        int index = 0;
        foreach (var field in fields.Take(128))
        {
            if (budget.ElapsedMilliseconds > ReadBudgetMilliseconds)
            {
                Warn(entry.Id, "settings-read-slow", "Reading these settings takes too long; the remaining fields are hidden this time.");
                break;
            }
            string source = entry.Id + ":" + index++;
            if (field is null || string.IsNullOrWhiteSpace(field.Key) || field.Key.Length > 80 || field.Key.Any(char.IsControl))
            { Warn(source, "invalid-setting-key", "A field needs a non-empty key of at most 80 characters."); continue; }
            source = entry.Id + ":" + field.Key;
            if (!seen.Add(field.Key)) { Warn(source, "duplicate-setting-key", "Duplicate field key; the later field is ignored."); continue; }
            if (string.IsNullOrWhiteSpace(field.Label) || field.Label.Length > 160 || !Enum.IsDefined(field.Kind))
            { Warn(source, "invalid-setting-definition", "A field needs a valid type and a label of at most 160 characters."); continue; }
            try
            {
                if (field.Group is null || field.Description is null || field.Placeholder is null || field.Group.Length > 80 || field.Description.Length > 1000 || field.Placeholder.Length > 256
                    || field.LabelEn?.Length > 160 || field.GroupEn?.Length > 80 || field.DescriptionEn?.Length > 1000 || field.PlaceholderEn?.Length > 256)
                    throw new ArgumentException("Invalid group, description or placeholder length.");
                var definition = field with { LabelEn = string.IsNullOrWhiteSpace(field.LabelEn) ? field.Label : field.LabelEn,
                    GroupEn = string.IsNullOrWhiteSpace(field.GroupEn) ? field.Group : field.GroupEn,
                    DescriptionEn = string.IsNullOrWhiteSpace(field.DescriptionEn) ? field.Description : field.DescriptionEn,
                    PlaceholderEn = string.IsNullOrWhiteSpace(field.PlaceholderEn) ? field.Placeholder : field.PlaceholderEn };
                if (field.Kind == ModuleSettingKind.Number && (!double.IsFinite(field.Minimum) || !double.IsFinite(field.Maximum)
                    || field.Minimum >= field.Maximum || !double.IsFinite(field.Step) || field.Step <= 0)) throw new ArgumentException("Invalid numeric range or step.");
                if (field.Kind == ModuleSettingKind.Text && field.MaximumLength is < 1 or > 8192) throw new ArgumentException("Text length must be between 1 and 8192.");
                if (field.Kind == ModuleSettingKind.Choice)
                {
                    if (field.Options is null) throw new ArgumentException("A choice needs options.");
                    var options = host.Invoke(entry.ModuleId, "settings options", () => field.Options.Take(129).ToArray());
                    if (options is not { Length: > 0 and <= 128 } || options.Any(o => o is null || string.IsNullOrWhiteSpace(o.Label) || o.Label.Length > 160 || o.LabelEn?.Length > 160)
                        || options.Select(o => o.Value).Distinct().Count() != options.Length) throw new ArgumentException("A choice needs 1–128 uniquely valued options with labels.");
                    definition = definition with { Options = Array.AsReadOnly(options.Select(o => o with { LabelEn = string.IsNullOrWhiteSpace(o.LabelEn) ? o.Label : o.LabelEn }).ToArray()) };
                }
                // A stored value that drifted outside its declaration is shown coerced, never dropped: dropping
                // it would remove the only control that could fix it. Definitions stay strict; values stay editable.
                object? value = host.Invoke(entry.ModuleId, "read setting", () => entry.Value.Read(field.Key));
                object valid = field.Kind switch
                {
                    ModuleSettingKind.Toggle => value is bool flag && flag,
                    ModuleSettingKind.Number => CoerceNumber(value, definition, source, Warn),
                    ModuleSettingKind.Choice => CoerceChoice(value, definition, source, Warn),
                    ModuleSettingKind.Text => CoerceText(value, definition, source, Warn),
                    ModuleSettingKind.Color => CoerceColor(value, source, Warn),
                    _ => throw new ArgumentException("Unsupported setting type.")
                };
                result.Add(new(definition, valid));
            }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
            { Warn(source, "invalid-setting-field", error.Message); }
        }
        return result;
    }
    private static double CoerceNumber(object? value, ModuleSetting field, string source, Action<string, string, string> warn)
    {
        bool numeric = value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
        double number = numeric ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
        if (double.IsFinite(number) && number >= field.Minimum && number <= field.Maximum) return number;
        warn(source, "coerced-setting-value", "The stored number is missing or outside the declared range; the nearest allowed value is shown.");
        return double.IsFinite(number) ? Math.Clamp(number, field.Minimum, field.Maximum) : field.Minimum;
    }
    private static int CoerceChoice(object? value, ModuleSetting field, string source, Action<string, string, string> warn)
    {
        if (value is int choice && field.Options.Any(o => o.Value == choice)) return choice;
        warn(source, "coerced-setting-value", "The stored choice is no longer offered; the first option is shown.");
        return field.Options[0].Value;
    }
    private static string CoerceText(object? value, ModuleSetting field, string source, Action<string, string, string> warn)
    {
        if (value is not string text) { warn(source, "coerced-setting-value", "The stored value is not text; an empty value is shown."); return ""; }
        if (text.Length <= field.MaximumLength) return text;
        warn(source, "coerced-setting-value", "The stored text is longer than the field allows; it is shown truncated.");
        return text[..field.MaximumLength];
    }
    private static string CoerceColor(object? value, string source, Action<string, string, string> warn)
    {
        if (value is string color && Color(color)) return color.ToUpperInvariant();
        warn(source, "coerced-setting-value", "The stored colour is not #RRGGBB; white is shown until a colour is chosen.");
        return "#FFFFFF";
    }
    internal static bool Color(string? value) => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).ToArray().All(Uri.IsHexDigit);
}
