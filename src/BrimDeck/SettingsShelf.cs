using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BrimDeck.Core;
using DropShelf.Services;
using DropShelf.Views;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private ContentControl? _shelfSlot;
    private ShelfSettingsContent? _shelfContent;
    private bool _shelfRecording;

    private ShelfSettingsContent CreateShelfContent()
    {
        var control = new ShelfSettingsContent(_app.Shelves!) { EnterContent = element => Soft.Enter(element) };
        control.RecordingChanged += recording =>
        {
            if (_shelfRecording == recording) return;
            _shelfRecording = recording;
            if (recording) _app.SuspendHotkeys(); else _app.ResumeHotkeys();
        };
        control.ModalChanged += modal =>
        {
            // 原版编辑器占用本页视口时，避免位置切换或页面重建丢掉未保存的编辑。
            _navigation.IsEnabled = !modal;
            _content.IsEnabled = !modal;
        };
        control.OpenSection("Shelf");
        return control;
    }

    public void ShowShelfSection(string section)
    {
        ShowPage(TrayPage);
        _shelfContent?.OpenSection(section);
    }

    private ResourceDictionary ShelfSettingsPalette()
    {
        var resources = new ResourceDictionary();
        void Brush(string key, string color) => resources[key] = UI.Brush(color);
        foreach (var (key, color) in ThemeService.BuildPalette(_palette.Dark, AccentPalette.FromColor((Color)ColorConverter.ConvertFromString(_palette.Accent))))
        { var brush = new SolidColorBrush(color); brush.Freeze(); resources[key] = brush; }
        foreach (var key in new[] { "WindowBackgroundBrush", "ShelfSolidBrush", "StackEdgeBrush", "SheetGlowBrush" }) Brush(key, Surface);
        foreach (var key in new[] { "CardBrush", "CardHoverBrush", "LayerBrush", "ShelfTileBrush" }) Brush(key, _palette.Inset);
        foreach (var key in new[] { "TextPrimaryBrush", "AccentTextBrush" }) Brush(key, TextPrimary);
        Brush("TextSecondaryBrush", TextSecondary); Brush("TextTertiaryBrush", TextTertiary);
        foreach (var key in new[] { "CardBorderBrush", "ControlBorderBrush", "ShelfTileBorderBrush", "FlyoutBorderBrush" }) Brush(key, _palette.Border);
        Brush("DividerBrush", Divider); Brush("FlyoutBrush", _palette.Popup); Brush("ControlBrush", _palette.Input);
        resources["SheetBrush"] = UI.Brush(Surface);
        // Reuse the host's styles rather than introducing a second settings-control language.
        foreach (var type in new[] { typeof(Button), typeof(TextBox), typeof(Slider), typeof(ScrollBar), typeof(ScrollViewer) })
            if (TryFindResource(type) is Style style) resources[type] = style;
        if (TryFindResource("SettingsButton") is Style button) resources[typeof(Button)] = button;
        foreach (var (key, target) in new (string, object)[] { ("SettingsField", typeof(TextBox)), ("SettingsSlider", typeof(Slider)), ("SettingsToggle", "ToggleSwitchStyle"), ("SettingsPageScrollViewer", typeof(ScrollViewer)) })
            if (TryFindResource(key) is Style style) resources[target] = style;
        resources["TooltipBrush"] = UI.Brush(_palette.Popup); resources["TooltipBorderBrush"] = UI.Brush(_palette.Border);
        return resources;
    }

    private void ResetShelfOptions()
    {
        if (_app.Shelves is not { } controller) return;
        var defaults = SettingsService.Normalize(new AppSettings());
        controller.UpdateSettings(settings =>
        {
            settings.AutoOpenTrigger = defaults.AutoOpenTrigger; settings.ShakeSensitivity = defaults.ShakeSensitivity;
            settings.EdgeDwell = defaults.EdgeDwell; settings.BlockedApps = [];
            settings.Hotkey = defaults.Hotkey; settings.QuickShelfSize = defaults.QuickShelfSize;
            settings.DragOutMode = defaults.DragOutMode; settings.CloseWhenEmpty = defaults.CloseWhenEmpty;
            settings.QuickActionLayout = defaults.QuickActionLayout; settings.QuickActionSlots = [.. defaults.QuickActionSlots!];
            // 自定义操作是用户内容；恢复行为和布局默认值不删除其定义。
        });
    }
}
