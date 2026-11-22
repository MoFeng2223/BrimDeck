using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// One panel's arriving notices, shown on the island itself. The island keeps its size and place: it moves once like a viscous
// liquid (IslandLiquid: the capsule flings bubbles off its ends, the notch's edge ripples, the indicator beads and breathes) and
// shows the notice's icon and words inside its outline, scrolling them when they are longer than it. While the notice shows, the
// pointer resting on the island opens the panel on the notices page with this notice in front; there is no card of its own.
//
// How much of that plays follows the notice's level (NoticeLevels.Effect): a plain one only shows its words (the indicator, which
// has none, lights up once), a glowing one flashes the backlight without moving the outline (the indicator breathes), a lively
// one moves as well, and one that stays until handled plays the lively motion again every few seconds.
//
// The window lies over the island with room around it for the bubbles and the backlight, and is shown only while a notice is.
// While it shows, the island underneath is hidden (MainWindow.SetIslandCovered) and drawn here in the same outline and material.
// A level-1 notice whose sender asks for a collapsed part's place (Notice.Slot) leaves the island as it is and draws over that
// part alone, in the island's material, with the words inside it. It never takes focus.
internal sealed class IslandNotice : Window
{
    private static readonly Color LineFlash = Color.FromRgb(0xF5, 0xF5, 0xF7), GlowColor = Color.FromRgb(0xF0, 0xF3, 0xF8);
    // The backlight's own color (white), as the settings show it for level 3.
    internal static Color DefaultLight => GlowColor;
    // The prototype's pace and intensity (the user's call, 2026-10-30: faster looked worse than the prototype).
    private const double Pace = 1, Fury = 5;
    // The words leave quickly and arrive a little slower: the island's own content and the notice's take turns, by opacity alone.
    internal const int FadeOut = 150, FadeIn = 220;
    private readonly MainWindow _deck;
    private readonly App _app;
    private readonly Canvas _stage = new() { Background = null };
    private readonly Layer _shadow = new(), _shade = new(), _glowWide = new(), _glowCore = new(), _bloomWide = new(), _bloomMiddle = new(), _bloomSmall = new(), _body = new(), _parts = new();
    private readonly Border _hit = new() { Background = Brushes.Transparent, Cursor = Cursors.Hand };
    private readonly Canvas _content = new() { IsHitTestVisible = false, ClipToBounds = true };
    // The notice's buttons (BuildButtons), inside the pointer's area: the pointer on one is still on the notice.
    private readonly Canvas _buttons = new();
    private readonly PromptTimer _hoverOpen, _replay;
    private AttentionItem? _item;
    private CollapsedShape _shape = null!;
    private NoticeLook _look = NoticeLook.Current();
    private Rect _stageRect, _island;
    // In a part's place: the part's area in the stage, within _island; null while the notice takes the whole island.
    private Rect? _part;
    // The backlight's color of the shown item (NoticeStyle.Color for level 3), white otherwise.
    private Color _light = GlowColor;
    private bool _leaving, _arrivedUnderPointer, _held;
    private int _generation;

    // The motion: its model, what of it plays and for how long, when it started, the factor from its design size to DIPs, and the
    // shapes it draws each frame.
    private IslandLiquid? _liquid;
    private NoticeEffect _effect;
    private double _start, _t, _k, _length;
    private readonly List<IslandLiquid.Circle> _circles = [];
    private readonly List<IslandLiquid.Neck> _necks = [];
    private Geometry? _notchGlowCore, _notchGlowWide;

    public IslandNotice(MainWindow deck, App app)
    {
        _deck = deck; _app = app;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowActivated = false; ShowInTaskbar = false; Title = "BrimDeck notices"; UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Content = _stage;
        // Back to front: the shadow, the shade and over it the backlight (or the indicator's bloom) behind the island, the island with
        // the light along its edge, the beads that trail its ripples, the content and the area that takes the pointer.
        foreach (var layer in new[] { _shadow, _shade, _glowWide, _glowCore, _bloomWide, _bloomMiddle, _bloomSmall, _body, _parts })
        {
            // Nothing is drawn before the island has been laid out (Layout sets _shape): Prepare shows the window once, far off screen,
            // before any notice, and the layers' first render then asked for the island's form (a NullReferenceException on every start
            // that left the window shown).
            layer.Ready = () => _shape is not null;
            _stage.Children.Add(layer);
        }
        _stage.Children.Add(_content); _stage.Children.Add(_hit);
        _hit.Child = _buttons;
        _shadow.Draw = DrawShadow; _shade.Draw = DrawShade; _shade.Opacity = 0; _glowWide.Draw = d => DrawGlow(d, wide: true); _glowCore.Draw = d => DrawGlow(d, wide: false);
        _bloomWide.Draw = d => DrawBloom(d, 3, 4, .55); _bloomMiddle.Draw = d => DrawBloom(d, 1.5, 1.5, .6); _bloomSmall.Draw = d => DrawBloom(d, .5, 0, .8);
        _body.Draw = DrawBody; _parts.Draw = DrawParts;
        RenderOptions.SetClearTypeHint(_content, ClearTypeHint.Enabled);
        AutomationProperties.SetName(_hit, Loc.T("灵动提示", "Live notice"));
        _hoverOpen = new(Dispatcher, () => { _hoverOpen!.Stop(); if (_item is not null && !_leaving && _hit.IsMouseOver) OpenPage(byKey: false); });
        // A notice that stays until handled plays its motion again while it shows.
        _replay = new(Dispatcher, () => { _replay!.Stop(); if (_item is { Persistent: true } && !_leaving && IsVisible) StartMotion(); });
        _hit.MouseEnter += (_, _) => PointerEntered();
        _hit.MouseLeave += (_, _) => PointerLeft();
        _hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_item is not null && !_leaving) Activate(byKey: false); };
        _hit.MouseRightButtonUp += (_, e) => e.Handled = true;
        SourceInitialized += (_, _) => WindowsHost.ConfigureOverlay(this, _app.Settings.WindowCapture);
        _app.Notices.Icons.Loaded += IconLoaded;
        Closed += (_, _) => { StopMotion(); _hoverOpen.Stop(); _replay.Stop(); _app.Notices.Icons.Loaded -= IconLoaded; _item = null; };
        DpiChanged += (_, _) => { if (_item is not null && !_leaving) Dress(); };
    }

    // The window's handle, the theme's materials and the fonts, made beforehand while nothing moves; nothing is shown.
    internal void Prepare()
    {
        new WindowInteropHelper(this).EnsureHandle();
        Dress();
        if (IsVisible || _item is not null) return;
        double left = Left, top = Top, width = Width, height = Height, opacity = Opacity;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000; Width = 16; Height = 16; Opacity = 0;
        try { Show(); UpdateLayout(); Hide(); }
        finally
        {
            // A warm-up that failed half way must not leave the window shown (Covering would then hold the island's hover back).
            if (IsVisible && _item is null) Hide();
            Left = left; Top = top; Width = width; Height = height; Opacity = opacity;
        }
    }

    // True while a notice covers the island: the panel then leaves hovering to this window.
    public bool Covering => _item is not null || IsVisible && !_leaving;
    public AttentionItem? Item => _item;
    private NoticeCenter Center => _app.Notices;
    private DeckSettings Settings => _app.Settings;
    private Notice? Notice => _item?.Payload as Notice;
    private IslandForm Form => _shape.Style switch { CompactStyle.Notch => IslandForm.Notch, CompactStyle.Capsule => IslandForm.Capsule, _ => IslandForm.Line };

    // ----- Showing, updating and ending -----

    public void Present(AttentionItem item)
    {
        bool part = PartOf(item) is not null;
        bool continuing = IsVisible && !_leaving && _item is not null && part == (_part is not null);
        // Another notice in place that takes the island where the one shown took a part, or the other way round: the shown one goes
        // at once and the new one starts as a new one.
        if (IsVisible && !continuing && _item is not null && !_leaving) { StopMotion(); _content.Children.Clear(); _deck.SetIslandCovered(false); }
        _item = item; _leaving = false;
        int generation = ++_generation;
        // Another notice in place: the words that show leave first. A new one: the island's own words leave first, unless the notice
        // only takes a part's place, which it covers.
        if (continuing) _content.BeginAnimation(OpacityProperty, Fade(0, Soft.Enabled ? FadeOut : 0, () => { if (generation == _generation && _item is not null) Begin(true, generation); }));
        else if (part) Begin(false, generation);
        else _deck.FadeIslandContent(() => { if (generation == _generation && _item is not null) Begin(false, generation); });
    }

    // Where a level-1 notice whose sender asks for a part shows (Notice.Slot): the part's area on the monitor, or null for the
    // whole island (another level, no part asked for or the island asked for, the indicator, or a part not on the island now).
    // The settings offer no place: notices of every level take the whole island unless their sender asks.
    // The notice's application has its own looks (NoticeAppRule.Styles): they decide over what its message asks for.
    private bool OwnLooks(AttentionItem? item) => item?.Payload is Notice notice && Settings.NoticeRule(notice.AppKey)?.Styles is not null;

    private Rect? PartOf(AttentionItem? item)
    {
        if (item is not { Level: NoticeLevel.Plain }) return null;
        var slot = (item.Payload as Notice)?.Slot is { } asked ? NoticeKeys.Part(asked) : null;
        return slot is { } part ? _deck.CompactPartBounds(part) : null;
    }

    private void Begin(bool continuing, int generation)
    {
        if (!continuing)
        {
            Layout();
            Dress();
            _stage.Opacity = 1;
            Show();
            // Shown on a monitor with another scale, Windows has just given the window that scale; it is placed again in it.
            Place();
            Dress();
            NoticeNative.BringToTop(new WindowInteropHelper(this).Handle);
            // The two windows reach the screen on frames of their own: the island underneath is hidden only once this window has
            // drawn the same island over it, so no frame shows neither. In a part's place the island stays.
            if (_part is null) AfterFrames(HandFrames, () => { if (generation == _generation && _item is not null && _part is null) _deck.SetIslandCovered(true); });
        }
        _arrivedUnderPointer = (_part ?? _island).Contains(CursorInStage());
        NameHit();
        BuildContent();
        _content.BeginAnimation(OpacityProperty, null);
        _content.Opacity = 0;
        _content.BeginAnimation(OpacityProperty, Fade(1, Soft.Enabled ? FadeIn : 0));
        // A window just shown draws its first frame late; started now, the motion would lose its first flash to that frame.
        if (continuing) StartMotion();
        else Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (generation == _generation && _item is not null) StartMotion(); });
        Announce();
    }

    // The shown notice changed in place (a task's progress, another message of its conversation): its words change, the island
    // stays. One that now stays until handled starts playing again; one that no longer does plays no more.
    public void Update(AttentionItem item)
    {
        if (_item is null) return;
        // Its look changed so that it now takes the island instead of a part, or the other way round: shown again as a new one.
        if ((PartOf(item) is not null) != (_part is not null)) { Present(item); return; }
        _item = item;
        NameHit();
        BuildContent();
        if (!item.Persistent) _replay.Stop();
        else if (!_replay.IsEnabled && _liquid is null && IsVisible && !_leaving) StartMotion();
    }

    // The item ended: the words leave, the island's own content lands again and the window hides.
    public void End(AttentionItem item)
    {
        if (_item is null || !ReferenceEquals(_item, item) && _item.Key != item.Key) return;
        _item = null; _leaving = true;
        _hoverOpen.Stop(); _replay.Stop(); Release();
        int generation = ++_generation;
        double fade = Soft.Enabled ? FadeOut : 0;
        if (fade <= 0) { Finish(reveal: true); return; }
        _content.BeginAnimation(OpacityProperty, Fade(0, fade, () => { if (generation == _generation && _item is null) Finish(reveal: true); }));
    }

    // The panel opens over the island: a notice on its way out goes at once.
    public void Abort()
    {
        if (_item is not null || !IsVisible) return;
        _generation++;
        Finish();
    }

    // The panel opens while a notice shows (the pointer rested on it, the hotkey): the notice goes in the same frame and the panel
    // grows from the island as usual, which has kept its outline.
    public void HandOver()
    {
        // Still waiting for the island's own words to fade (FadeIslandContent): they come back at once and the notice never shows.
        if (!IsVisible) { if (_item is not null) { _item = null; _generation++; _deck.SetIslandCovered(false); } return; }
        _item = null;
        _hoverOpen.Stop(); _replay.Stop(); Release();
        _generation++;
        Finish();
    }

    // The panel is hidden, or BrimDeck exits: a notice that still shows goes at once.
    public void Vanish() { if (IsVisible) HandOver(); }

    // The theme or the island changed while a notice shows: it is laid out and drawn again for the island as it is now.
    public void Refresh()
    {
        if (!IsVisible) return;
        if (_item is null) { Finish(reveal: true); return; }
        StopMotion();
        Layout();
        Dress();
        BuildContent();
        _body.InvalidateVisual(); _shadow.InvalidateVisual();
    }

    public void IslandChanged()
    {
        if (IsVisible && _deck.Collapsed != _shape) Refresh();
    }

    // The notices hotkey with a notice on the island: the panel opens on the notices page and takes the keyboard.
    public void EnterKeyboard() { if (_item is not null) Activate(byKey: true); }

    // reveal: the notice ran out and the island takes over in place. The island shows again first and this window, drawing the same
    // island, leaves only once the island is on screen, a few frames later: the two windows reach the screen on frames of their own,
    // and leaving at once showed a frame with neither, a flash. When the panel opens instead, this window leaves at once, as the
    // panel grows from the island.
    private void Finish(bool reveal = false)
    {
        StopMotion();
        _leaving = false;
        _deck.SetIslandCovered(false, reveal);
        _content.Children.Clear();
        if (!reveal) { Empty(); return; }
        int generation = _generation;
        AfterFrames(HandFrames, () => { if (generation == _generation && _item is null) Empty(); });
    }

    // A layered window keeps its last picture while hidden and shows it for a frame when shown again: emptied first, then hidden.
    private void Empty()
    {
        _stage.Opacity = 0;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () => { if (_item is null && !_leaving) Hide(); });
    }

    // How many display frames one window waits for the other to have drawn the island when they hand it over.
    private const int HandFrames = 3;

    private static void AfterFrames(int frames, Action done)
    {
        TimeSpan last = default;
        int seen = 0;
        EventHandler? step = null;
        step = (_, e) =>
        {
            // WPF may raise Rendering more than once for one frame.
            if (e is RenderingEventArgs args) { if (args.RenderingTime == last) return; last = args.RenderingTime; }
            if (++seen < frames) return;
            CompositionTarget.Rendering -= step;
            done();
        };
        CompositionTarget.Rendering += step;
    }

    // A click or the hotkey opens the notices page with the notice in front.
    private void Activate(bool byKey) => OpenPage(byKey, acted: true);

    private void NameHit()
    {
        if (Track is { } track) { AutomationProperties.SetName(_hit, Loc.T("正在播放：", "Now playing: ") + track.Title); return; }
        AutomationProperties.SetName(_hit, Loc.T("灵动提示", "Live notice"));
    }

    // acted: a click or the hotkey, rather than the pointer resting on the notice. With the notices page switched off there is
    // nothing to open; a notice that stays until handled then ends with the click, which would otherwise do nothing.
    private void OpenPage(bool byKey, bool acted = false)
    {
        // A track change's title is not a notice: the panel opens as it does from the island itself.
        if (Track is not null) { _deck.OpenFromHotkey(byKey: byKey); return; }
        if (acted && _item is { Persistent: true } && !DeckPages.Shown(Settings, DeckPage.Notices)) { Center.Dismiss(_deck, reportToSource: false); return; }
        string? id = Notice?.Id;
        _deck.OpenNoticesPage(byKey, id);
    }

    // ----- Pointer -----

    private void PointerEntered()
    {
        if (_item is null || _leaving) return;
        Hold();
        // A notice that arrives under a resting pointer does not open the panel by itself: the pointer leaves and comes back, or clicks.
        RestHover();
    }

    // The pointer rests on the notice (not on one of its buttons): the panel opens after its delay.
    private void RestHover()
    {
        // With the panel set to open on a click (ClickToExpand), resting on the notice does not open it either.
        if (_item is null || _leaving || _arrivedUnderPointer || Settings.ClickToExpand) return;
        _hoverOpen.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Settings.OpenDelay));
        _hoverOpen.Start();
    }

    private void PointerLeft()
    {
        _hoverOpen.Stop();
        _arrivedUnderPointer = false;
        Release();
    }

    // While the pointer is on it, the notice does not run out.
    private void Hold() { if (!_held) { _held = true; Center.Hold(_deck, true); } }
    private void Release() { if (_held) { _held = false; Center.Hold(_deck, false); } }

    private Point CursorInStage()
    {
        var (bounds, _, scale) = WindowsHost.MonitorScreen(_deck.HotkeyMonitor) ?? WindowsHost.PrimaryScreen();
        var cursor = WindowsHost.Cursor();
        return new Point((cursor.X - bounds.Left) / scale - _stageRect.X, (cursor.Y - bounds.Top) / scale - _stageRect.Y);
    }

    // ----- Geometry -----

    // The window holds the island with room around it for the bubbles and the backlight, and covers the island exactly.
    private void Layout()
    {
        _shape = _deck.Collapsed;
        var surface = _shape.Surface;
        var island = new Rect(surface.X, surface.Y, Math.Max(1, surface.Width), Math.Max(1, surface.Height));
        _k = Form switch { IslandForm.Capsule => island.Height / IslandLiquid.DesignHeight, IslandForm.Notch => Math.Max(.5, _shape.BottomRadius / CollapsedLimits.NotchCorner), _ => Math.Max(.5, _deck.Scale) };
        double side = Form == IslandForm.Line ? 40 : 28, below = Form == IslandForm.Line ? 32 : 26;
        var stage = new Rect(island.X - side * _k, island.Y - 26 * _k, island.Width + 2 * side * _k, island.Height + (26 + below) * _k);
        var monitor = _deck.MonitorBoundsDip;
        stage.Intersect(new Rect(0, 0, Math.Max(1, monitor.Width), Math.Max(1, monitor.Height)));
        if (stage.IsEmpty) stage = island;
        _stageRect = stage;
        _island = new Rect(island.X - stage.X, island.Y - stage.Y, island.Width, island.Height);
        Place();
        foreach (var layer in new[] { _shadow, _shade, _glowWide, _glowCore, _bloomWide, _bloomMiddle, _bloomSmall, _body, _parts }) { layer.Width = stage.Width; layer.Height = stage.Height; }
        _part = PartOf(_item) is { } part ? new Rect(part.X - stage.X, part.Y - stage.Y, part.Width, part.Height) : null;
        // The pointer's area: the island (a part's place), and for the thin indicator the band round it the island's own hover area takes.
        var hit = _part ?? (Form == IslandForm.Line ? new Rect(_island.X - 12, 0, _island.Width + 24, _island.Bottom + 10) : _island);
        Canvas.SetLeft(_hit, hit.X); Canvas.SetTop(_hit, hit.Y); _hit.Width = hit.Width; _hit.Height = hit.Height;
        // The content lies over the whole island and is clipped to its outline, and in a part's place to the part as well.
        Canvas.SetLeft(_content, _island.X); Canvas.SetTop(_content, _island.Y); _content.Width = _island.Width; _content.Height = _island.Height;
        var outline = IslandBorder.BuildOutline(_island.Width, _island.Height, _shape.Shoulder, new CornerRadius(_shape.TopRadius, _shape.TopRadius, _shape.BottomRadius, _shape.BottomRadius));
        _content.Clip = _part is { } area ? Geometry.Combine(outline, new RectangleGeometry(new Rect(area.X - _island.X, area.Y - _island.Y, area.Width, area.Height)), GeometryCombineMode.Intersect, null) : outline;
        _notchGlowCore = _notchGlowWide = null;
        _rest = _frameBody = _frameInner = _frameEdge = null; _edgePens = null;
        _shade.InvalidateVisual();
    }

    private void Place()
    {
        var (bounds, _, scale) = WindowsHost.MonitorScreen(_deck.HotkeyMonitor) ?? WindowsHost.PrimaryScreen();
        Width = _stageRect.Width; Height = _stageRect.Height;
        int x = (int)Math.Round(bounds.Left + _stageRect.X * scale), y = (int)Math.Round(bounds.Top + _stageRect.Y * scale);
        if (PresentationSource.FromVisual(this) is not null && VisualTreeHelper.GetDpi(this).DpiScaleX is var current && Math.Abs(current - scale) > .001)
            WindowsHost.MoveTo(this, x, y);
        double dpi = PresentationSource.FromVisual(this) is null ? scale : VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = x / dpi; Top = y / dpi;
    }

    private void Dress()
    {
        double dpi = PresentationSource.FromVisual(this) is null ? (WindowsHost.MonitorScreen(_deck.HotkeyMonitor) ?? WindowsHost.PrimaryScreen()).Scale : VisualTreeHelper.GetDpi(this).DpiScaleX;
        _look = NoticeLook.Current(dpi);
        _fill = null; _rim = null; _edgePens = null;
        // The island's shadow, from a copy of its body behind it; a see-through surface would show that copy, so it has none.
        _shadow.Effect = _look.ShadowOpacity <= 0 || _look.HighContrast || _look.Surface.A < 255 ? null : new DropShadowEffect
        {
            Color = _look.Shadow, BlurRadius = Math.Max(0, _look.ShadowBlur), ShadowDepth = _look.ShadowDepth, Direction = 270,
            Opacity = _look.ShadowOpacity, RenderingBias = RenderingBias.Performance
        };
        // The backlight's wide layer and the indicator's bloom are the prototype's blurred copies (σ 3.2; 1.6, 4 and 9), as kernel radii of 3σ.
        _glowWide.Effect = new BlurEffect { Radius = 3.2 * 3 * _k, RenderingBias = RenderingBias.Performance };
        // The shade under the backlight is softer (σ 5.5; the indicator's, under its bloom, 7).
        _shade.Effect = new BlurEffect { Radius = (_shape is not null && Form == IslandForm.Line ? 7 : 5.5) * 3 * _k, RenderingBias = RenderingBias.Performance };
        _shade.InvalidateVisual();
        _bloomSmall.Effect = new BlurEffect { Radius = 1.6 * 3 * _k, RenderingBias = RenderingBias.Performance };
        _bloomMiddle.Effect = new BlurEffect { Radius = 4 * 3 * _k, RenderingBias = RenderingBias.Performance };
        _bloomWide.Effect = new BlurEffect { Radius = 9 * 3 * _k, RenderingBias = RenderingBias.Performance };
        _body.InvalidateVisual(); _shadow.InvalidateVisual();
    }

    // ----- Motion -----

    // With animations on and outside high contrast; without, the island keeps still and only shows the words.
    private bool MotionAllowed => Soft.Enabled && !_look.HighContrast;

    // What the shown item plays on this island (NoticeLevels.Effect): a notice that shows beyond the rate limit plays as a plain one.
    private NoticeEffect Played => _item is { } item ? NoticeLevels.Effect(item.Level, item.Subdued, Form, item.Style?.Liquid) : NoticeEffect.Still;

    // The backlight's color: level 3's chosen one (white, the application's or a swatch), white for every other.
    private Color Light()
    {
        if (_item is not { Level: NoticeLevel.Persistent }) return GlowColor;
        // The notice's own (its sender's color, or the one of a warning, an error or a success) comes before the level's style.
        if (!OwnLooks(_item) && Notice is { } own && NoticeTones.Glow(own) is { } tone && ColorConverter.ConvertFromString(tone) is Color toned) return toned;
        if (_item is not { Style.Color: { Length: > 0 } chosen }) return GlowColor;
        if (chosen == NoticeStyle.AppColor)
            return Notice is { } notice ? _look.AppColor(Center.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey))) : GlowColor;
        return ColorConverter.ConvertFromString(chosen) is Color color ? color : GlowColor;
    }

    private void StartMotion()
    {
        StopMotion();
        _replay.Stop();
        var effect = Played;
        if (!MotionAllowed || effect == NoticeEffect.Still) { RedrawAll(); return; }
        var form = Form;
        double width = _island.Width / _k, height = _island.Height / _k;
        // Without the liquid the model keeps the island's shape and only times the backlight (the indicator: its breathing).
        bool liquid = effect == NoticeEffect.Liquid;
        _liquid = form switch
        {
            IslandForm.Notch => new IslandLiquid(form, width, height, liquid: liquid, fury: Fury, corner: _shape.BottomRadius / _k, shoulder: _shape.Shoulder / _k),
            _ => new IslandLiquid(form, width, height, liquid: liquid, fury: Fury)
        };
        _effect = effect;
        _light = Light(); _edgePens = null;
        _length = liquid ? _liquid.Total : NoticeLevels.EffectLength(effect, form) + .05;
        _start = Eased.Now; _t = 0;
        Motion.Start(_body, Step);
        if (_item is { Persistent: true }) { _replay.Interval = AttentionCoordinator.PersistentReplay; _replay.Start(); }
    }

    // One display frame of the motion; false once it has played.
    private bool Step(double now)
    {
        if (_liquid is null) return false;
        _t = Math.Max(0, now - _start) * Pace;
        if (_t >= _length) { StopMotion(); return false; }
        RedrawAll();
        return true;
    }

    private void StopMotion()
    {
        if (_liquid is null) return;
        _liquid = null;
        RedrawAll();
    }

    private void RedrawAll()
    {
        foreach (var layer in new[] { _shadow, _glowWide, _glowCore, _bloomWide, _bloomMiddle, _bloomSmall, _body, _parts }) layer.InvalidateVisual();
        Shade();
    }

    // How strong the shade is at its darkest, under the backlight and under the indicator's bloom.
    private const double ShadeMost = .55, LineShadeMost = .65;

    // The shade's strength at this frame. It keeps its drawing (DrawShade) and only changes its opacity: it rises and falls with the
    // light, and is as strong as the light is bright, since a white light is lost on a light desktop while a deep color shows there
    // by itself and would only be dulled.
    private void Shade()
    {
        if (_shape is null || _liquid is null || _part is not null) { _shade.Opacity = 0; return; }
        bool line = Form == IslandForm.Line;
        double light = line ? (_effect == NoticeEffect.Brighten ? 0 : IslandLiquid.BreathAt(_t)) : IslandLiquid.GlowAt(_t);
        _shade.Opacity = light * (line ? LineShadeMost : ShadeMost) * new ThemeColor(255, _light.R, _light.G, _light.B).Luminance;
    }

    // ----- Drawing -----

    private Brush? _fill, _rim;

    private Brush Fill => _fill ??= Freeze(new SolidColorBrush(_look.Surface));

    // The island at rest, and the body and the line of the light along its edge for the frame they were last made for, kept until
    // the island or the frame changes.
    private Geometry? _rest, _frameBody, _frameInner, _frameEdge;
    private IslandLiquid? _frameLiquid;
    private double _frameTime;
    private double[] _outs = [];

    // The island at rest, exactly as IslandBorder draws it.
    private Geometry RestOutline()
    {
        if (_rest is not null) return _rest;
        if (Form == IslandForm.Line)
        {
            double r = _island.Height / 2;
            return _rest = Freeze(new RectangleGeometry(_island, r, r));
        }
        var outline = IslandBorder.BuildOutline(_island.Width, _island.Height, _shape.Shoulder, new CornerRadius(_shape.TopRadius, _shape.TopRadius, _shape.BottomRadius, _shape.BottomRadius)).Clone();
        outline.Transform = new TranslateTransform(_island.X, _island.Y);
        return _rest = Freeze(outline);
    }

    // The island's body at this frame: its outline pushed out by the ripples (the notch's top edge stays against the screen).
    // Without the liquid it is the island at rest, to the pixel.
    private Geometry BodyOutline()
    {
        if (_liquid is not { Liquid: true } || Form == IslandForm.Line) return RestOutline();
        Frame();
        return _frameBody!;
    }

    // How far inside the island's edge its light begins. The strip left between them keeps the island's own color, so the light
    // has the island on both sides of it and shows as a line of light on any desktop; right at the edge, a white light met a
    // light desktop and only made the edge look soft.
    private const double EdgeInset = 1.25;

    // The frame's outline, made once for the layers that draw it: the body; the same outline EdgeInset further in, along which
    // the edge lights up; and for the notch that line left open, without its top edge, which lies against the screen.
    private void Frame()
    {
        if (_liquid is not { } liquid || _frameBody is not null && ReferenceEquals(_frameLiquid, liquid) && (_frameTime == _t || !liquid.Liquid)) return;
        var outline = liquid.Outline;
        if (_outs.Length < outline.Count) _outs = new double[outline.Count];
        liquid.EdgeOuts(_t, _outs);
        bool notch = Form == IslandForm.Notch;
        StreamGeometry body = new(), inner = new() { FillRule = FillRule.Nonzero }, edge = new();
        using (StreamGeometryContext g = body.Open(), n = inner.Open(), e = edge.Open())
        {
            if (notch) { g.BeginFigure(new Point(_island.X, _island.Y), true, true); n.BeginFigure(new Point(_island.X, _island.Y), true, true); }
            for (int i = 0; i < outline.Count; i++)
            {
                var p = outline[i];
                double o = _outs[i], d = o - EdgeInset;
                Point point = new(_island.X + (p.X + p.Nx * o) * _k, _island.Y + (p.Y + p.Ny * o) * _k), within = new(_island.X + (p.X + p.Nx * d) * _k, _island.Y + (p.Y + p.Ny * d) * _k);
                if (i == 0 && !notch) { g.BeginFigure(point, true, true); n.BeginFigure(within, true, true); }
                else { g.LineTo(point, true, false); n.LineTo(within, true, false); }
                if (!notch) continue;
                if (i == 0) e.BeginFigure(within, false, false); else e.LineTo(within, true, false);
            }
        }
        body.Freeze(); inner.Freeze(); edge.Freeze();
        // Without the liquid the body is the island at rest, to the pixel; only the light's line is taken from the model.
        _frameBody = liquid.Liquid ? body : RestOutline();
        _frameInner = inner; _frameEdge = notch ? edge : inner; _frameLiquid = liquid; _frameTime = _t;
    }

    private void DrawShadow(DrawingContext drawing)
    {
        if (_shadow.Effect is null || Form == IslandForm.Line || _part is not null) return;
        drawing.DrawGeometry(Fill, null, BodyOutline());
    }

    private void DrawBody(DrawingContext drawing)
    {
        if (Form == IslandForm.Line) { DrawLine(drawing); return; }
        var body = BodyOutline();
        // In a part's place only that part of the island is drawn, over the part the island shows there.
        if (_part is { } part) drawing.PushClip(new RectangleGeometry(part));
        drawing.DrawGeometry(Fill, null, body);
        if (_look.Gradient is { } gradient) drawing.DrawGeometry(gradient, null, body);
        if (_look.Pattern is { } pattern) drawing.DrawGeometry(pattern, null, body);
        if (_look.RimWidth > 0)
        {
            // Clipped to the body, the outer half of the stroke goes, so the pen is twice the rim's width (as IslandBorder's).
            _rim ??= Freeze(_look.Rim.Clone());
            drawing.PushClip(body);
            drawing.DrawGeometry(null, new Pen(_rim, _look.RimWidth * 2), body);
            drawing.Pop();
        }
        DrawEdgeLight(drawing, body);
        if (_part is not null) drawing.Pop();
    }

    // The shade behind the island, under the backlight: the island's own shape at rest, a little larger, in black, blurred by its
    // layer. On a light desktop the white backlight cannot be seen; there the shade is what shows, and the backlight reads as light
    // against it. On a dark desktop the shade is lost and the backlight shows as before. Drawn once; Shade sets how strong it is.
    private void DrawShade(DrawingContext drawing)
    {
        if (_part is not null) return;
        if (Form == IslandForm.Line)
        {
            // The indicator's: its shape grown by 4 and moved down by 3, under its bloom.
            double grow = 4 * _k, half = _island.Height / 2 + grow;
            drawing.DrawRoundedRectangle(Brushes.Black, null, new Rect(_island.X - grow, _island.Y - grow + 3 * _k, _island.Width + 2 * grow, 2 * half), half, half);
            return;
        }
        // Grown by 2: the fill, and a stroke of that width round it. The notch's is not carried on above the screen's edge, so it
        // thins out towards the shoulders as the backlight does.
        drawing.DrawGeometry(Brushes.Black, new Pen(Brushes.Black, 4 * _k) { LineJoin = PenLineJoin.Round }, RestOutline());
    }

    // The light along the island's edge, on its own surface: strongest where it begins (EdgeInset from the edge) and gone 6 DIPs
    // further in, EdgeMost · exp(−(d / 2.2)^1.3) at the distance d from there, as strokes of growing width whose strengths add up to that.
    private const double EdgeMost = .9;
    private static readonly (double Depth, double Alpha)[] EdgeSteps = MakeEdgeSteps();
    private Pen[]? _edgePens;

    private static (double Depth, double Alpha)[] MakeEdgeSteps()
    {
        double[] depths = [6, 5, 4.1, 3.3, 2.6, 2, 1.5, 1, .6];
        var steps = new (double, double)[depths.Length];
        double outer = 0;
        for (int i = 0; i < depths.Length; i++)
        {
            double target = EdgeMost * Math.Exp(-Math.Pow(depths[i] / 2.2, 1.3));
            steps[i] = (depths[i], 1 - (1 - target) / (1 - outer));
            outer = target;
        }
        return steps;
    }

    // The light's color on the surface and how strong it is drawn. The backlight's white is the theme's own text color there, which
    // always stands out from the surface; on a light surface that is a dark line, drawn weaker so that it does not read as a border.
    // A chosen color (level 3) is drawn as the theme draws colors chosen outside it.
    private (Color Color, double Strength) EdgeLight()
    {
        if (_light != GlowColor) return (_look.AppColor(_light), 1);
        var text = ColorConverter.ConvertFromString(_look.Primary) is Color primary ? Color.FromRgb(primary.R, primary.G, primary.B) : GlowColor;
        bool darker = new ThemeColor(255, text.R, text.G, text.B).Luminance < new ThemeColor(255, _look.Surface.R, _look.Surface.G, _look.Surface.B).Luminance;
        return (text, darker ? .55 : 1);
    }

    // The light along the edge, flashing with the backlight and following the liquid's outline. It is drawn on the island, which
    // BrimDeck colors itself, so it shows whatever the desktop behind is; under the content, which stays as it is.
    private void DrawEdgeLight(DrawingContext drawing, Geometry body)
    {
        if (_liquid is null || _part is not null || Form == IslandForm.Line) return;
        double g = IslandLiquid.GlowAt(_t);
        if (g <= .004) return;
        var (color, strength) = EdgeLight();
        bool notch = Form == IslandForm.Notch;
        if (_edgePens is null)
        {
            // Brightest over the middle and dimmer towards the ends, as the backlight.
            (double, double)[] stops = notch ? [(0, .6), (.06, .72), (.28, 1), (.72, 1), (.94, .72), (1, .6)] : [(0, .6), (.12, .78), (.32, 1), (.68, 1), (.88, .78), (1, .6)];
            _edgePens = [.. EdgeSteps.Select(step => Freeze(new Pen(Fade(_island.X, _island.Right, stops, color, step.Alpha), 2 * step.Depth * _k) { LineJoin = PenLineJoin.Round }))];
        }
        Frame();
        drawing.PushClip(body);
        drawing.PushClip(_frameInner);
        drawing.PushOpacity(g * strength);
        // The notch's fades out up its shoulders to nothing where they meet the screen.
        if (notch) drawing.PushOpacityMask(Freeze(new LinearGradientBrush(Colors.Transparent, Colors.Black, new Point(0, _island.Y + _k), new Point(0, _island.Y + Math.Max(2 * _k, _shape.Shoulder * 7 / 16)))
            { MappingMode = BrushMappingMode.Absolute }));
        foreach (var pen in _edgePens) drawing.DrawGeometry(null, pen, _frameEdge);
        if (notch) drawing.Pop();
        drawing.Pop();
        drawing.Pop();
        drawing.Pop();
    }

    // The beads that trail some ripples, in the island's own color.
    private void DrawParts(DrawingContext drawing)
    {
        if (_liquid is not { Liquid: true } liquid || Form == IslandForm.Line) return;
        liquid.Beads(_t, _circles);
        if (_circles.Count == 0) return;
        var parts = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var g = parts.Open())
            foreach (var c in _circles) Circle(g, _island.X + c.X * _k, _island.Y + c.Y * _k, c.R * _k);
        parts.Freeze();
        drawing.DrawGeometry(Fill, null, parts);
    }

    // How bright the indicator is at this frame: two breaths, or the one of a plain notice.
    private double LineLight => _effect == NoticeEffect.Brighten ? IslandLiquid.FlashAt(_t) : IslandLiquid.BreathAt(_t);

    // The indicator: its thread with round ends, the droplets leaving them, in the indicator's color brightening as it breathes.
    private void DrawLine(DrawingContext drawing)
    {
        var color = PanelTheme.ColorOf("indicator");
        if (_liquid is not { } liquid) { drawing.DrawGeometry(Freeze(new SolidColorBrush(color)), null, RestOutline()); return; }
        double b = LineLight * .85;
        var flash = _light == GlowColor ? LineFlash : _light;
        var lit = Color.FromArgb((byte)Math.Round(color.A + (flash.A - color.A) * b), (byte)Math.Round(color.R + (flash.R - color.R) * b),
            (byte)Math.Round(color.G + (flash.G - color.G) * b), (byte)Math.Round(color.B + (flash.B - color.B) * b));
        double cx = _island.X + _island.Width / 2, cy = _island.Y + _island.Height / 2, h0 = liquid.Height / 2;
        double left = -liquid.LineEnd(_t, -1) + h0, right = liquid.LineEnd(_t, 1) - h0;
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var g = geometry.Open())
        {
            int n = Math.Max(4, (int)Math.Ceiling((right - left) / .5));
            // Bottom left to right, then top right to left: the same turning as the circles, so where they overlap the fill adds up.
            g.BeginFigure(new Point(cx + left * _k, cy + liquid.LineHalf(left, _t) * _k), true, true);
            for (int i = 1; i <= n; i++) { double x = left + (right - left) * i / n; g.LineTo(new Point(cx + x * _k, cy + liquid.LineHalf(x, _t) * _k), true, false); }
            for (int i = n; i >= 0; i--) { double x = left + (right - left) * i / n; g.LineTo(new Point(cx + x * _k, cy - liquid.LineHalf(x, _t) * _k), true, false); }
            Circle(g, cx + left * _k, cy, liquid.LineHalf(left, _t) * _k);
            Circle(g, cx + right * _k, cy, liquid.LineHalf(right, _t) * _k);
            liquid.LineDrops(_t, _circles, _necks);
            foreach (var c in _circles) Circle(g, cx + c.X * _k, cy + c.Y * _k, c.R * _k);
            foreach (var neck in _necks) NeckFigure(g, neck, cx, cy);
        }
        geometry.Freeze();
        drawing.DrawGeometry(Freeze(new SolidColorBrush(lit)), null, geometry);
    }

    // The capsule's and the notch's backlight: behind the island, so the liquid shows dark against it. wide: the blurred outer layer.
    private void DrawGlow(DrawingContext drawing, bool wide)
    {
        if (_liquid is not { } liquid || Form == IslandForm.Line) return;
        double g = IslandLiquid.GlowAt(_t);
        if (g <= .004) return;
        Geometry band;
        LinearGradientBrush brush;
        if (Form == IslandForm.Capsule)
        {
            double cx = _island.X + _island.Width / 2, cy = _island.Y + _island.Height / 2;
            double left = -liquid.Width / 2, right = liquid.Width / 2;
            var (outer, inner) = liquid.CapsuleRing(left, right, wide ? 10 : 3.4);
            var ring = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var c = ring.Open()) { Polygon(c, outer, cx, cy); Polygon(c, inner, cx, cy); }
            ring.Freeze();
            band = ring;
            brush = Fade(cx + left * _k, cx + right * _k, [(0, .6), (.12, .78), (.32, 1), (.68, 1), (.88, .78), (1, .6)]);
        }
        else
        {
            ref var cached = ref wide ? ref _notchGlowWide : ref _notchGlowCore;
            if (cached is null)
            {
                var shape = new StreamGeometry();
                using (var c = shape.Open()) Polygon(c, liquid.NotchBand(wide ? 12 : 3.8), _island.X, _island.Y);
                shape.Freeze();
                cached = shape;
            }
            band = cached;
            brush = Fade(_island.X, _island.Right, [(0, .6), (.06, .72), (.28, 1), (.72, 1), (.94, .72), (1, .6)]);
        }
        drawing.PushOpacity(g);
        drawing.DrawGeometry(brush, null, band);
        drawing.Pop();
    }

    // The indicator's bloom: its own shape grown by grow and moved down by drop, blurred (the layer's effect), breathing twice.
    // A plain notice has none: the line alone lights up.
    private void DrawBloom(DrawingContext drawing, double grow, double drop, double opacity)
    {
        if (_liquid is not { } liquid || Form != IslandForm.Line || _effect == NoticeEffect.Brighten) return;
        double b = IslandLiquid.BreathAt(_t);
        if (b <= .004) return;
        double cx = _island.X + _island.Width / 2, cy = _island.Y + _island.Height / 2;
        double half = (liquid.Height / 2 + grow) * _k, left = cx - (liquid.LineEnd(_t, -1) + grow) * _k, right = cx + (liquid.LineEnd(_t, 1) + grow) * _k;
        drawing.PushOpacity(b * opacity);
        drawing.DrawRoundedRectangle(Freeze(new SolidColorBrush(_light)), null, new Rect(left, cy + drop * _k - half, Math.Max(0, right - left), 2 * half), half, half);
        drawing.Pop();
    }

    private LinearGradientBrush Fade(double from, double to, (double Offset, double Alpha)[] stops) => Fade(from, to, stops, _light, 1);

    private static LinearGradientBrush Fade(double from, double to, (double Offset, double Alpha)[] stops, Color color, double strength)
    {
        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(from, 0), EndPoint = new Point(Math.Max(from + 1, to), 0) };
        foreach (var (offset, alpha) in stops) brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(alpha * strength * 255), color.R, color.G, color.B), offset));
        brush.Freeze();
        return brush;
    }

    private void Polygon(StreamGeometryContext g, List<LiquidPoint> points, double ox, double oy)
    {
        if (points.Count < 3) return;
        g.BeginFigure(new Point(ox + points[0].X * _k, oy + points[0].Y * _k), true, true);
        for (int i = 1; i < points.Count; i++) g.LineTo(new Point(ox + points[i].X * _k, oy + points[i].Y * _k), true, false);
    }

    private static void Circle(StreamGeometryContext g, double x, double y, double r)
    {
        if (!(r > 0)) return;
        // Two half arcs, turning the same way as the bodies, so overlaps add up instead of cancelling.
        g.BeginFigure(new Point(x - r, y), true, true);
        g.ArcTo(new Point(x + r, y), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
        g.ArcTo(new Point(x - r, y), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
    }

    private void NeckFigure(StreamGeometryContext g, IslandLiquid.Neck n, double ox, double oy)
    {
        Point P(LiquidPoint p) => new(ox + p.X * _k, oy + p.Y * _k);
        g.BeginFigure(P(n.P1), true, true);
        g.BezierTo(P(n.H1), P(n.H3), P(n.P3), true, false);
        g.LineTo(P(n.P4), true, false);
        g.BezierTo(P(n.H4), P(n.H2), P(n.P2), true, false);
    }

    private static T Freeze<T>(T freezable) where T : Freezable { if (freezable.CanFreeze) freezable.Freeze(); return freezable; }

    // ----- Content -----

    // The song of a track change shown on the island (MainWindow.AttentionStarted); null for a notice.
    private TrackNoticeContent? Track => _item is { Kind: AttentionKind.Track } ? _item.Payload as TrackNoticeContent : null;

    // spoken: the words for a screen reader, in which a verification code is not dots but a note that it is hidden.
    private (string Title, string Summary) Texts(bool spoken = false)
    {
        if (_item is { Kind: AttentionKind.Summary, Payload: int count })
            return (NoticeText.Count(count), Loc.T("在 BrimDeck 安静期间收到", "Arrived while BrimDeck kept quiet"));
        if (Track is { } track) return (track.Title, track.Artist);
        if (Notice is not { } notice) return ("", "");
        var (title, summary) = NoticeText.Presented(notice, !Settings.NoticeCodes ? CodeMask.Off : spoken ? CodeMask.Spoken : CodeMask.Dots);
        if (NoticeClock.Live(notice.Timing, DateTimeOffset.UtcNow, TimeZoneInfo.Local) is { } live) summary = live;
        if (title.Length == 0) title = notice.AppName;
        if (_item is { Count: > 1 } item) summary = (summary.Length > 0 ? summary + " · " : "") + NoticeText.Count(item.Count);
        return (title, summary);
    }

    private TextBlock? _announced;

    // Where the icon and the words sit when they fit: the sender's choice for this notice, else in the middle.
    private NoticeAlign Align => Notice?.Align ?? NoticeAlign.Center;

    // The icon and the words, laid out inside the island's content area at the island's scale: together at the left, in the middle
    // or at the right of it (Align) when they fit. Words longer than the area start at the left and scroll through once they have
    // been read from the start, and start again; without animations they end in an ellipsis.
    private void BuildContent()
    {
        _content.Children.Clear();
        _buttons.Children.Clear();
        _announced = null;
        if (_item is null || Form == IslandForm.Line) return;
        double scale = _shape.Scale, inset = _shape.Inset, available = Math.Max(0, _island.Width - 2 * inset);
        // In a part's place the words have the part's width, from its left; a part narrower than the icon and a few words leaves
        // the icon out.
        double origin = 0;
        if (_part is { } part)
        {
            inset = Math.Round(4 * scale);
            origin = part.X - _island.X;
            available = Math.Max(0, part.Width - 2 * inset);
        }
        double plate = Math.Round(20 * scale), gap = Math.Round(8 * scale);
        // The buttons take their room at the right; the words have what is left.
        available = Math.Max(0, available - BuildButtons(scale, inset, available));
        bool withIcon = _item.Style?.Icon != false && (OwnLooks(_item) || Notice?.HideIcon != true) && available >= plate + gap + 48 * scale;
        if (!withIcon) plate = gap = 0;
        var (title, summary) = Texts();
        var icon = withIcon ? Plate(plate) : new Canvas();
        Canvas.SetTop(icon, (_island.Height - plate) / 2);
        _content.Children.Add(icon);
        var line = new TextBlock { FontFamily = UI.PanelFont, FontSize = 12.5 * scale, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        line.Inlines.Add(new Run(title) { FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(_look.Primary) });
        if (summary.Length > 0) line.Inlines.Add(new Run("  " + summary) { Foreground = UI.Brush(_look.Secondary), FontSize = 12 * scale });
        Directed(line, title + summary);
        var (said, saidSummary) = Settings.NoticeCodes ? Texts(spoken: true) : (title, summary);
        string spoken = (Notice?.AppName is { Length: > 0 } app ? app + ": " : "") + (saidSummary.Length > 0 ? said + ", " + saidSummary : said);
        AutomationProperties.SetName(line, spoken);
        AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Polite);
        _announced = line;
        double viewport = Math.Max(0, available - plate - gap);
        line.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = line.DesiredSize.Width;
        // The room left over goes before the group: none of it, half, or all.
        double shift = NoticeAligns.Shift(viewport, width, Align);
        Canvas.SetLeft(icon, origin + inset + shift);
        var window = new Canvas { Width = viewport - shift, Height = _island.Height, ClipToBounds = true };
        Canvas.SetLeft(window, origin + inset + shift + plate + gap);
        _content.Children.Add(window);
        Canvas.SetTop(line, (_island.Height - line.DesiredSize.Height) / 2);
        window.Children.Add(line);
        if (width <= viewport) return;
        if (!Soft.Enabled) { line.Width = viewport; line.TextTrimming = TextTrimming.CharacterEllipsis; return; }
        // Read from the start, carried along at an even pace to its end, held there, and back to the start.
        double travel = width - viewport, seconds = travel / (32 * scale);
        var move = new TranslateTransform();
        line.RenderTransform = move;
        var scroll = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(1.2 + seconds + 1.2) };
        scroll.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scroll.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));
        scroll.KeyFrames.Add(new LinearDoubleKeyFrame(-travel, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2 + seconds))));
        scroll.KeyFrames.Add(new LinearDoubleKeyFrame(-travel, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2 + seconds + 1.2))));
        move.BeginAnimation(TranslateTransform.XProperty, scroll);
    }

    // The notice's buttons at the right of the island, when the island shows any (NoticeActions.OnIsland: the panel opens on a click,
    // or on hover after a delay long enough to reach a button): its sender's, copying a verification code, opening the source
    // application, as many of the first two as leave the words their room (NoticeActions.Fit). Returns the width they take with
    // the gap before them. A click on a button does what it says; anywhere else on the notice opens the notices page as before.
    private double BuildButtons(double scale, double inset, double available)
    {
        if (_item is not { Kind: AttentionKind.Notice } || Notice is not { } notice || !NoticeActions.OnIsland(Settings.ClickToExpand, Settings.OpenDelay, Form, _part is not null)) return 0;
        bool hasCode = NoticeCodes.Words(notice, Settings.NoticeCodes).Hit is not null;
        var picked = NoticeActions.Pick(Center.Buttons(notice), hasCode, Center.CanOpen(notice));
        if (picked.Count == 0) return 0;
        double height = Math.Round(Math.Min(22 * scale, Math.Max(12, _island.Height - 8 * scale))), gap = Math.Round(6 * scale), side = Math.Round(9 * scale);
        // A label is cut so that one button always leaves the words their room, on a narrow island too.
        double labelRoom = Math.Clamp(available - NoticeActions.WordsRoom * scale - 2 * side - gap, 28 * scale, NoticeActions.LabelRoom * scale);
        var faces = new List<Border>();
        foreach (var button in picked)
        {
            var label = new TextBlock
            {
                Text = button.Label, FontFamily = UI.PanelFont, FontSize = 11.5 * scale, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = labelRoom, VerticalAlignment = VerticalAlignment.Center, Foreground = button.Primary ? Freeze(new SolidColorBrush(Color.FromRgb(_look.Surface.R, _look.Surface.G, _look.Surface.B))) : UI.Brush(_look.Primary)
            };
            // Where the whole label has no room, its shorter form says it rather than an ellipsis.
            if (button.Short is { } brief)
            {
                label.MaxWidth = double.PositiveInfinity;
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (label.DesiredSize.Width > labelRoom) label.Text = brief;
                label.MaxWidth = labelRoom;
            }
            var face = new Border
            {
                Height = height, CornerRadius = new CornerRadius(height / 2), Padding = new Thickness(side, 0, side, 0), Child = label,
                Background = UI.Brush(button.Primary ? _look.Primary : _look.Fill), Cursor = Cursors.Hand, SnapsToDevicePixels = true
            };
            AutomationProperties.SetName(face, button.Label);
            if (!button.Primary)
            {
                face.MouseEnter += (_, _) => face.Background = UI.Brush(_look.Hover);
                face.MouseLeave += (_, _) => face.Background = UI.Brush(_look.Fill);
            }
            else
            {
                face.MouseEnter += (_, _) => face.Opacity = .86;
                face.MouseLeave += (_, _) => face.Opacity = 1;
            }
            // On a button the pointer is about to click, not resting on the notice: the panel does not open under it meanwhile.
            face.MouseEnter += (_, _) => _hoverOpen.Stop();
            face.MouseLeave += (_, _) => { if (_hit.IsMouseOver) RestHover(); };
            face.MouseLeftButtonDown += (_, e) => e.Handled = true;
            face.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_item is not null && !_leaving) Press(notice, button); };
            face.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            faces.Add(face);
        }
        int shown = NoticeActions.Fit([.. faces.Select(face => face.DesiredSize.Width)], gap, available, NoticeActions.WordsRoom * scale);
        if (shown == 0) return 0;
        double total = faces.Take(shown).Sum(face => face.DesiredSize.Width) + gap * (shown - 1);
        // From the right edge of the content's room, the first button leftmost.
        double x = inset + available - total;
        foreach (var face in faces.Take(shown))
        {
            Canvas.SetLeft(face, x); Canvas.SetTop(face, (_island.Height - height) / 2);
            _buttons.Children.Add(face);
            x += face.DesiredSize.Width + gap;
        }
        return total + gap;
    }

    // What a button of the island does. One that could not do it (the clipboard was held, nothing opened) leaves the notice as it is.
    private async void Press(Notice notice, NoticeActions.Button button)
    {
        if (_pressing) return;
        _pressing = true;
        try
        {
            switch (button.Kind)
            {
                case NoticeActions.ButtonKind.Sender when button.Action is { } action:
                    await Center.RunAsync(notice, action);
                    break;
                case NoticeActions.ButtonKind.CopyCode:
                    // Only this click puts the code on the clipboard, as the notices page's button does.
                    if (NoticeCodes.Words(notice, Settings.NoticeCodes).Hit is { } code && NoticeCodeClipboard.Copy(code.CopyText)) { Center.Hub.MarkViewed(notice.Id); Center.Withdraw(notice.Id); }
                    break;
                case NoticeActions.ButtonKind.OpenApp:
                    bool opened = await Center.OpenAsync(notice);
                    // The notice may have ended or been replaced while the application started.
                    if (_item is null || _leaving || Notice?.Id != notice.Id) break;
                    if (opened) Center.Dismiss(_deck, reportToSource: false); else OpenPage(byKey: false, acted: true);
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception) { }
        finally { _pressing = false; }
    }

    private bool _pressing;

    // The notice's picture (a contact's avatar) round; else the application's own icon; without one, its first letter in its color
    // on a plate tinted with it (a bell for a summary).
    private FrameworkElement Plate(double size)
    {
        // A track change shows the song's cover, as the music part does.
        if (Track is { } track) return MusicVisuals.Cover(track.Cover, size, Math.Round(size * .22), look: PanelTheme.Main);
        var notice = Notice;
        if (notice is not null && NoticeLetter.ShowsPicture(notice, Settings) && Center.Icons.Get(notice.Picture, out _) is { } avatar) return NoticeLetter.Round(avatar, size);
        var image = _item?.Kind == AttentionKind.Summary ? null : Center.Icons.Get(notice?.IconRef, out _);
        if (image is not null)
        {
            var picture = new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            return picture;
        }
        var accent = notice is null ? PanelTheme.ColorOf("accent") : _look.AppColor(Center.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey)));
        return NoticeLetter.Plate(notice?.AppName, size, accent, bell: notice is null);
    }

    private void IconLoaded(string iconRef)
    {
        if (_leaving || Notice?.IconRef != iconRef && Notice?.Picture != iconRef) return;
        BuildContent();
    }

    // A text in a right-to-left language reads and aligns from the right (NoticeText.RightToLeft).
    internal static TextBlock Directed(TextBlock block) => Directed(block, block.Text);

    private static TextBlock Directed(TextBlock block, string text)
    {
        block.FlowDirection = NoticeText.RightToLeft(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        return block;
    }

    // Screen readers hear the new notice once, politely, without the window taking focus.
    private void Announce()
    {
        // A song's title is not read out at every track change: the music part never announced it either.
        if (Track is not null) return;
        if (_announced is null || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        (UIElementAutomationPeer.FromElement(_announced) ?? UIElementAutomationPeer.CreatePeerForElement(_announced))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static DoubleAnimation Fade(double to, double milliseconds, Action? completed = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(Math.Max(1, milliseconds))) { EasingFunction = Soft.Out };
        if (completed is not null) animation.Completed += (_, _) => completed();
        return animation;
    }

    // A layer of the drawing, drawn by its delegate whenever it is invalidated.
    private sealed class Layer : FrameworkElement
    {
        public Action<DrawingContext>? Draw;
        // False until the window has laid the island out: nothing is drawn then.
        public Func<bool>? Ready;
        public Layer() { IsHitTestVisible = false; }
        protected override void OnRender(DrawingContext drawingContext) { if (Ready?.Invoke() != false) Draw?.Invoke(drawingContext); }
    }
}
