using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Core;

public sealed class MiniAppsSettings
{
    public bool Enabled { get; set; } = true;
    public double Width { get; set; } = 520;
    public double Height { get; set; } = 320;
    [JsonExtensionData] public Dictionary<string, JsonElement>? LegacyValues { get; set; }
    public MiniAppsSettings Copy() => new() { Enabled = Enabled, Width = Width, Height = Height,
        LegacyValues = LegacyValues?.ToDictionary(p => p.Key, p => p.Value.Clone()) };
    public void Normalize()
    {
        Width = double.IsFinite(Width) ? Math.Clamp(Width, DeckPages.MinWidth, DeckPages.MaxWidth) : 520;
        Height = double.IsFinite(Height) ? Math.Clamp(Height, DeckPages.MinHeight, DeckPages.MaxHeight) : 320;
    }
}
