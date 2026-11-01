namespace BrimDeck.Core;

public sealed record InputMethodInfo(string Id, string Name, bool Available = true);

// Zero preserves the behavior of configurations saved before modes were introduced.
public enum InputMethodMode { Control, Notify }

public sealed class InputMethodChoice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class InputMethodSettings
{
    public bool Enabled { get; set; }
    public InputMethodMode Mode { get; set; } = InputMethodMode.Control;
    public string Shortcut { get; set; } = "Win+Space";
    public List<InputMethodChoice> Profiles { get; set; } = [];

    public void Normalize()
    {
        if (!Enum.IsDefined(Mode)) Mode = InputMethodMode.Control;
        Shortcut = InputMethodBinding.Normalize(Shortcut);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Profiles = (Profiles ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id) && p.Id.Length <= 256 && seen.Add(p.Id))
            .Take(128).ToList();
        foreach (var profile in Profiles)
        {
            profile.Id = profile.Id.ToLowerInvariant();
            profile.Name = (profile.Name ?? "").Trim();
            if (profile.Name.Length > 128) profile.Name = profile.Name[..128];
        }
    }

    // A saved but unavailable profile stays in settings; it does not enter this session's cycle.
    public IReadOnlyList<InputMethodInfo> Participants(IEnumerable<InputMethodInfo> installed)
    {
        var available = installed.Where(p => p.Available).DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        return Profiles.Where(p => p.Enabled && available.ContainsKey(p.Id)).Select(p => available[p.Id]).ToArray();
    }
}

// One immutable participant snapshot, with exactly one commit. It has no Windows or UI dependencies.
public sealed class InputMethodCycle
{
    public IReadOnlyList<InputMethodInfo> Items { get; }
    public string? CurrentId { get; }
    public int SelectedIndex { get; private set; }
    public bool Finished { get; private set; }
    public InputMethodInfo? Selected => SelectedIndex >= 0 ? Items[SelectedIndex] : null;

    public InputMethodCycle(IEnumerable<InputMethodInfo> items, string? currentId)
    {
        Items = Array.AsReadOnly(items.Where(p => p.Available).DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToArray());
        CurrentId = currentId;
        SelectedIndex = Items.ToList().FindIndex(p => string.Equals(p.Id, currentId, StringComparison.OrdinalIgnoreCase));
    }

    public InputMethodInfo? Next()
    {
        if (Finished || Items.Count == 0) return null;
        SelectedIndex = (SelectedIndex + 1) % Items.Count;
        return Selected;
    }

    public InputMethodInfo? Commit()
    {
        if (Finished) return null;
        Finished = true;
        return Selected;
    }

    public void Cancel() => Finished = true;
}
