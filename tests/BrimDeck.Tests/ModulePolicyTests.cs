using System.Text.Json;
using BrimDeck.Core;

internal static class ModulePolicyTests
{
    internal static void Run(Action<string, bool> check)
    {
        var legacy = JsonSerializer.Deserialize<DeckSettings>("{\"Clipboard\":{\"Enabled\":false},\"Tray\":{\"Enabled\":false}}")!;
        legacy.Normalize();
        check("modules migration: absent modes keep Full without enabling capture or treating legacy Tray.Enabled as Off",
            legacy.Clipboard.Mode == ModuleMode.Full && legacy.Tray.Mode == ModuleMode.Full && !legacy.Clipboard.Enabled);
        foreach (var clipboard in Enum.GetValues<ModuleMode>())
        foreach (var shelf in Enum.GetValues<ModuleMode>())
        foreach (var placement in Enum.GetValues<FileTrayPlacement>())
        {
            var settings = new DeckSettings { ClipboardPage = true };
            settings.Clipboard.Mode = clipboard; settings.Tray.Mode = shelf; settings.Tray.Placement = placement;
            var copy = settings.Copy();
            check($"modules policy {clipboard}/{shelf}/{placement}: ownership and effective policy are independent",
                ModuleRuntimePolicy.ForPage(copy, DeckPage.Clipboard) == clipboard && ModuleRuntimePolicy.ForPage(copy, DeckPage.FileTray) == shelf
                && ModuleRuntimePolicy.For(clipboard).Running == (clipboard != ModuleMode.Off)
                && ModuleRuntimePolicy.For(shelf).Automatic == (shelf == ModuleMode.Full) && !copy.Clipboard.Enabled);
            check($"modules navigation {clipboard}/{shelf}/{placement}: Off cannot be a selectable module page",
                (clipboard != ModuleMode.Off || !DeckPages.Shown(copy, DeckPage.Clipboard))
                && (shelf != ModuleMode.Off || !DeckPages.Shown(copy, DeckPage.FileTray)));
        }
        var original = new DeckSettings { Animations = true };
        original.Clipboard.Mode = ModuleMode.Performance; original.Tray.Mode = ModuleMode.Off;
        original.Clipboard.PreviewSensitivity = 73; original.Clipboard.HeaderMode = ClipboardHeaderMode.Edge;
        var restored = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(original))!; restored.Normalize();
        check("modules storage: modes round-trip without overwriting motion, header, consent or preview preferences",
            restored.Clipboard.Mode == ModuleMode.Performance && restored.Tray.Mode == ModuleMode.Off && restored.Animations
            && restored.Clipboard.HeaderMode == ClipboardHeaderMode.Edge && restored.Clipboard.PreviewSensitivity == 73 && !restored.Clipboard.Enabled);
        original.Clipboard.Mode = (ModuleMode)999; original.Tray.Mode = (ModuleMode)(-1); original.Normalize();
        check("modules normalization: invalid modes preserve compatible Full behavior", original.Clipboard.Mode == ModuleMode.Full && original.Tray.Mode == ModuleMode.Full);
        var policy = ModuleRuntimePolicy.For(ModuleMode.Performance);
        check("modules presentation: reduced budgets affect only regenerable display pixels", policy.CardPixels == 256 && policy.PreviewPixels == 768
            && policy.ThumbnailPixels == 128 && ModuleRuntimePolicy.ReducedCacheBytes == 16 * 1024 * 1024);
    }
}
