namespace BrimDeck.Core;

public sealed class ModuleSettings
{
    public bool Enabled { get; set; } = true;
    public List<string> EnabledModules { get; set; } = [];
    public Dictionary<string, string> Approvals { get; set; } = [];
    public Dictionary<string, string> BundledPackages { get; set; } = [];
    public ModuleSettings Copy() => new() { Enabled = Enabled, EnabledModules = [.. EnabledModules ?? []], Approvals = new(Approvals ?? []), BundledPackages = new(BundledPackages ?? []) };
    public void Normalize()
    {
        EnabledModules = (EnabledModules ?? []).Where(ValidId).Distinct(StringComparer.Ordinal).ToList();
        Approvals = (Approvals ?? []).Where(pair => ValidId(pair.Key) && pair.Value is { Length: 64 } && pair.Value.All(Uri.IsHexDigit)).ToDictionary(pair => pair.Key, pair => pair.Value);
        BundledPackages = (BundledPackages ?? []).Where(pair => ValidId(pair.Key) && pair.Value is { Length: 64 } && pair.Value.All(Uri.IsHexDigit)).ToDictionary(pair => pair.Key, pair => pair.Value);
    }
    private static bool ValidId(string? id) => id is { Length: > 0 and <= 80 } && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
