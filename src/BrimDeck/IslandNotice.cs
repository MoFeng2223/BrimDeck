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
using BrimDeck.Core.Agenda;
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
// one moves as well, one set to the liquid alone moves without the light, and one that stays until handled plays its motion
// again every few seconds.
//
// The window lies over the island with room around it for the ripples and the backlight, and is shown only while a notice is.
// While it shows, the island underneath is hidden (MainWindow.SetIslandCovered) and drawn here in the same outline and material.
// A level-1 notice whose sender asks for a collapsed part's place (Notice.Slot) leaves the island as it is and draws over that
// part alone, in the island's material, with the words inside it. It never takes focus.
//
// What a frame of the motion costs: the island's shadow, the shade, the backlight, the light along the edge and the indicator's
// bloom are each drawn once and kept as a picture (Layer), blur included; a frame only changes how strongly they show (Paint).
// The island itself keeps its drawing too. Only what the liquid moves is drawn again every frame: the stretches of the outline
// that stand out with their beads (DrawParts) and, while the light flashes, the light along the edge that follows them, both
// from as few of the outline's points as the ripples need (Sample); or the indicator's thread, and that only while the liquid
// moves it or its color changes. Redrawn whole every frame, a long island (about 1800 outline points, nine strokes for the
// edge's light and three blurs) missed about one frame in five at 165 Hz.
//
// A frame's drawings go straight into their layers (Layer.DrawNow), with each figure's points handed over whole, and the frames
// end as soon as the shape is at rest and the light has faded (IslandLiquid.Settled). What is left is mostly what WPF itself
// takes to run a frame, drawn or not: it evaluates every frame on this thread, whatever the graphics card does afterwards.
internal sealed partial class IslandNotice : Window
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
    private readonly Layer _shadow = new(kept: true), _shade = new(kept: true), _glowWide = new(kept: true), _glowCore = new(kept: true), _bloomWide = new(kept: true), _bloomMiddle = new(kept: true),
        _bloomSmall = new(kept: true), _body = new(), _parts = new(), _edge = new(kept: true), _beads = new();
    // Back to front (see the constructor).
    private Layer[] Layers => [_shadow, _shade, _glowWide, _glowCore, _bloomWide, _bloomMiddle, _bloomSmall, _body, _parts, _edge, _beads];
    private readonly Border _hit = new() { Background = Brushes.Transparent, Cursor = Cursors.Hand };
    private readonly Canvas _content = new() { IsHitTestVisible = false, ClipToBounds = true };
    // The notice's buttons (BuildButtons), inside the pointer's area: the pointer on one is still on the notice.
    private readonly Canvas _buttons = new();
    private readonly PromptTimer _hoverOpen, _replay;
    private AttentionItem? _item;
    private CollapsedShape _shape = null!;
    private NoticeLook _look = NoticeLook.Current();
    // The display's scale the window was last dressed for.
    private double _dpi = 1;
    private Rect _stageRect, _island;
    // In a part's place: the part's area in the stage, within _island; null while the notice takes the whole island.
    private Rect? _part;
    // The backlight's color of the shown item (NoticeStyle.Color for levels 2 and 3), white otherwise.
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
    // The island's model at rest, for what is drawn once whatever plays: the backlight's shape and the line of the edge's light.
    private IslandLiquid? _still;
    // Whether the last frame drew any of the liquid, so that the frame after the last ripple clears it; and whether the kept
    // light along the edge was last drawn along a moving outline, so that it is drawn at rest again before it shows without the liquid.
    private bool _partsDrawn, _edgeMoved;

    public IslandNotice(MainWindow deck, App app)
    {
        _deck = deck; _app = app;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowActivated = false; ShowInTaskbar = false; Title = "BrimDeck notices"; UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Content = _stage;
        // Back to front: the shadow, the shade and over it the backlight (or the indicator's bloom) behind the island, the island,
        // what the liquid pushes out of it, the light along its edge, the beads that trail the ripples, the content and the area
        // that takes the pointer.
        foreach (var layer in Layers)
        {
            // Nothing is drawn before the island has been laid out (Layout sets _shape): Prepare shows the window once, far off screen,
            // before any notice, and the layers' first render then asked for the island's form (a NullReferenceException on every start
            // that left the window shown).
            layer.Ready = () => _shape is not null;
            _stage.Children.Add(layer.Element);
        }
        foreach (var layer in new[] { _shade, _glowWide, _glowCore, _bloomWide, _bloomMiddle, _bloomSmall, _edge }) layer.Shown = 0;
        // The content is clipped to the island's outline. It lies in a container of its own: a frame that draws anything makes
        // WPF work out the bounds of every child of the stage again, and for a clipped child that is the bounds of its clip, each
        // time. Straight under the stage, the content's clip took about a quarter of what a frame of the liquid cost this thread.
        _stage.Children.Add(new Canvas { IsHitTestVisible = false, Children = { _content } }); _stage.Children.Add(_hit);
        _hit.Child = _buttons;
        _shadow.Draw = DrawShadow; _shade.Draw = DrawShade; _glowWide.Draw = d => DrawGlow(d, wide: true); _glowCore.Draw = d => DrawGlow(d, wide: false);
        _bloomWide.Draw = d => DrawBloom(d, 3, 4); _bloomMiddle.Draw = d => DrawBloom(d, 1.5, 1.5); _bloomSmall.Draw = d => DrawBloom(d, .5, 0);
        _body.Draw = DrawBody; _parts.Draw = DrawParts; _edge.Draw = DrawEdgeLight; _beads.Draw = DrawBeads;
        // The notice lies over the island's opaque body; its text keeps ClearType under the clips that hold it.
        TextClarity.SetClearType(_content, true);
        AutomationProperties.SetName(_hit, Loc.T("通知", "Notice"));
        _hoverOpen = new(Dispatcher, () => { _hoverOpen!.Stop(); if (_item is not null && !_leaving && _hit.IsMouseOver) OpenPage(byKey: false); });
        // A notice that stays until handled plays its motion again while it shows.
        _replay = new(Dispatcher, () => { _replay!.Stop(); if (_item is { Persistent: true } && !_leaving && IsVisible) StartMotion(); });
        _hit.MouseEnter += (_, _) => PointerEntered();
        _hit.MouseLeave += (_, _) => PointerLeft();
        _hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_item is not null && !_leaving) Activate(byKey: false); };
        _hit.MouseRightButtonUp += (_, e) => e.Handled = true;
        SourceInitialized += (_, _) => WindowsHost.ConfigureOverlay(this, _app.Settings.WindowCapture);
        _app.Notices.Icons.Loaded += IconLoaded;
        Closed += (_, _) => { StopMotion(); _scrollStop?.Invoke(); _hoverOpen.Stop(); _replay.Stop(); _app.Notices.Icons.Loaded -= IconLoaded; _item = null; };
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
        if (IsVisible && !continuing && _item is not null && !_leaving) { StopMotion(); ClearContent(); _deck.SetIslandCovered(false); }
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
        ClearContent();
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
        AutomationProperties.SetName(_hit, Loc.T("通知", "Notice"));
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
        foreach (var layer in Layers) { layer.Width = stage.Width; layer.Height = stage.Height; layer.Fit(_dpi, stage.Width, stage.Height); }
        _part = PartOf(_item) is { } part ? new Rect(part.X - stage.X, part.Y - stage.Y, part.Width, part.Height) : null;
        // The pointer's area: the island (a part's place), and for the thin indicator the band round it the island's own hover area takes.
        var hit = _part ?? (Form == IslandForm.Line ? new Rect(_island.X - 12, 0, _island.Width + 24, _island.Bottom + 10) : _island);
        Canvas.SetLeft(_hit, hit.X); Canvas.SetTop(_hit, hit.Y); _hit.Width = hit.Width; _hit.Height = hit.Height;
        // The content lies over the whole island and is clipped to its outline, and in a part's place to the part as well.
        Canvas.SetLeft(_content, _island.X); Canvas.SetTop(_content, _island.Y); _content.Width = _island.Width; _content.Height = _island.Height;
        var outline = IslandBorder.BuildOutline(_island.Width, _island.Height, _shape.Shoulder, new CornerRadius(_shape.TopRadius, _shape.TopRadius, _shape.BottomRadius, _shape.BottomRadius));
        _content.Clip = _part is { } area ? Geometry.Combine(outline, new RectangleGeometry(new Rect(area.X - _island.X, area.Y - _island.Y, area.Width, area.Height)), GeometryCombineMode.Intersect, null) : outline;
        _still = null; _rest = null; _sampled = null; _edgeBrushes = null; _rimBrush = null; _gradient = null;
        Redraw();
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
        _look = NoticeLook.Current(dpi); _dpi = dpi;
        _fill = null; _rim = null; _edgeBrushes = null; _rimBrush = null; _gradient = null;
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
        _bloomSmall.Effect = new BlurEffect { Radius = 1.6 * 3 * _k, RenderingBias = RenderingBias.Performance };
        _bloomMiddle.Effect = new BlurEffect { Radius = 4 * 3 * _k, RenderingBias = RenderingBias.Performance };
        _bloomWide.Effect = new BlurEffect { Radius = 9 * 3 * _k, RenderingBias = RenderingBias.Performance };
        foreach (var layer in Layers) layer.Fit(dpi, _stageRect.Width, _stageRect.Height);
        Redraw();
    }

    // For pictures of the window taken off screen at a scale of their own (scripts/readme-media): the kept layers are made at
    // that scale, as they are at the display's when the window shows.
    internal void KeepAt(double scale) { foreach (var layer in Layers) layer.Fit(scale * MainWindow.SystemScale, _stageRect.Width, _stageRect.Height); }

    // Everything is drawn again: the island, its theme or the display's scale changed.
    private void Redraw()
    {
        foreach (var layer in Layers) layer.InvalidateVisual();
        Paint();
    }

    // The light's color changed: what is drawn in it is drawn again.
    private void Relight()
    {
        _edgeBrushes = null;
        foreach (var layer in new[] { _glowWide, _glowCore, _bloomWide, _bloomMiddle, _bloomSmall, _edge }) layer.InvalidateVisual();
    }

    // ----- Motion -----

    // With animations on and outside high contrast; without, the island keeps still and only shows the words.
    private bool MotionAllowed => Soft.Enabled && !_look.HighContrast;

    // What the shown item plays on this island (NoticeLevels.Effect): a notice that shows beyond the rate limit plays as a plain one.
    private NoticeEffect Played => _item is { } item ? NoticeLevels.Effect(item.Level, item.Subdued, Form, item.Style?.Liquid, item.Style?.Light ?? true) : NoticeEffect.Still;

    // The backlight's color: the one chosen for levels 2 and 3 (white, the application's or a swatch), white for level 1.
    private Color Light()
    {
        if (_item is not { Level: NoticeLevel.Glow or NoticeLevel.Persistent }) return GlowColor;
        // For level 3 the notice's own (its sender's color, or the one of a warning, an error or a success) comes before the
        // level's style, unless the application has its own looks.
        if (_item.Level == NoticeLevel.Persistent && !OwnLooks(_item) && Notice is { } own && NoticeTones.Glow(own) is { } tone && ColorConverter.ConvertFromString(tone) is Color toned) return toned;
        if (_item is not { Style.Color: { Length: > 0 } chosen }) return GlowColor;
        if (chosen == NoticeStyle.AppColor)
            return Notice is { } notice ? _look.AppColor(Center.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey))) : GlowColor;
        return ColorConverter.ConvertFromString(chosen) is Color color ? color : GlowColor;
    }

    private IslandLiquid Model(bool liquid)
    {
        var form = Form;
        double width = _island.Width / _k, height = _island.Height / _k;
        return form switch
        {
            IslandForm.Notch => new IslandLiquid(form, width, height, liquid: liquid, fury: Fury, corner: _shape.BottomRadius / _k, shoulder: _shape.Shoulder / _k),
            _ => new IslandLiquid(form, width, height, liquid: liquid, fury: Fury)
        };
    }

    private IslandLiquid Still => _still ??= Model(liquid: false);

    // A short rest after a notice's motion before the next notice may take the island with its own.
    private const double Rest = .25;

    private void StartMotion()
    {
        StopMotion();
        _replay.Stop();
        var effect = Played;
        if (!MotionAllowed || effect == NoticeEffect.Still) { Paint(); return; }
        // Without the liquid the model keeps the island's shape and only times the backlight (the indicator: its breathing).
        bool liquid = effect is NoticeEffect.Liquid or NoticeEffect.LiquidOnly;
        _liquid = Model(liquid);
        _effect = effect;
        var light = Light();
        if (light != _light) { _light = light; Relight(); }
        else if (_edgeMoved) _edge.InvalidateVisual();
        // The frames end once the shape is at rest again and the light has faded: the model's whole length (Total) leaves a
        // fifth of a second after the last ripple in which every frame would draw nothing.
        double lit = effect == NoticeEffect.LiquidOnly ? 0 : NoticeLevels.EffectLength(effect == NoticeEffect.Brighten ? effect : NoticeEffect.Glow, Form);
        _length = (liquid ? Math.Max(_liquid.Settled, lit) : lit) + .05;
        _start = Eased.Now; _t = 0;
        Motion.Start(_body, Step);
        if (_item is { Persistent: true }) { _replay.Interval = AttentionCoordinator.PersistentReplay; _replay.Start(); }
        // Another notice waits for this one's motion to have played (AttentionCoordinator.Playing).
        if (_item is { IsNotice: true }) Center.Playing(_deck, TimeSpan.FromSeconds(_length / Pace + Rest));
    }

    // One display frame of the motion; false once it has played.
    private bool Step(double now)
    {
        if (_liquid is null) return false;
        _t = Math.Max(0, now - _start) * Pace;
        if (_t >= _length) { StopMotion(); return false; }
        Paint();
        return true;
    }

    private void StopMotion()
    {
        if (_liquid is null) return;
        _liquid = null;
        Paint();
    }

    // How strong the shade is at its darkest, under the backlight and under the indicator's bloom.
    private const double ShadeMost = .55, LineShadeMost = .65;

    // One frame: how strongly each kept layer shows at this moment, and a new drawing of what the liquid moves. The shade rises
    // and falls with the light, and is as strong as the light is bright, since a white light is lost on a light desktop while a
    // deep color shows there by itself and would only be dulled. Nothing of the light shows on a notice in a part's place.
    private void Paint()
    {
        if (_shape is null) return;
        // The liquid alone (NoticeEffect.LiquidOnly) plays without any of the light.
        bool line = Form == IslandForm.Line, playing = _liquid is not null && _part is null, lit = playing && _effect != NoticeEffect.LiquidOnly;
        double glow = lit && !line ? IslandLiquid.GlowAt(_t) : 0;
        double breath = lit && line && _effect != NoticeEffect.Brighten ? IslandLiquid.BreathAt(_t) : 0;
        _glowWide.Shown = _glowCore.Shown = glow;
        _edge.Shown = glow > 0 ? glow * EdgeLight().Strength : 0;
        // The light along the edge follows the liquid while it shows. What a frame draws is drawn at once (Layer.DrawNow), not
        // left to the next layout pass.
        if (glow > 0 && _liquid is { Liquid: true }) _edge.DrawNow();
        _shade.Shown = (line ? breath * LineShadeMost : glow * ShadeMost) * new ThemeColor(255, _light.R, _light.G, _light.B).Luminance;
        _bloomWide.Shown = breath * .55; _bloomMiddle.Shown = breath * .6; _bloomSmall.Shown = breath * .8;
        if (line) PaintLine();
        else if (_liquid is { Liquid: true } || _partsDrawn) { _parts.DrawNow(); _beads.DrawNow(); }
    }

    // The indicator is drawn again only when it changes: every frame while the liquid moves it, else when its color does.
    // What it was last drawn as (DrawLine).
    private (bool Moving, Color Color)? _lineDrawn;

    private void PaintLine()
    {
        bool moving = _liquid is { Liquid: true };
        if (!moving && _lineDrawn == (moving, LineColor())) return;
        _body.DrawNow();
    }

    // ----- Drawing -----

    private Brush? _fill, _rim, _gradient, _rimBrush;

    private Brush Fill => _fill ??= Freeze(new SolidColorBrush(_look.Surface));
    private Brush Rim => _rim ??= Freeze(_look.Rim.Clone());

    // The island at rest, kept until the island changes.
    private Geometry? _rest;

    // The outline at the frame being drawn: how far each of its points stands out, and the points a drawing needs. On a straight
    // edge those are few: a chord between two of them stays within Fine of the points it skips, so an edge at rest takes a point
    // every Span and a long swell one in about eight; every point of a curve is kept. Made once for the layers that draw it.
    private double[] _outs = [];
    private readonly List<int> _kept = [];
    // The kept points in the stage: where each lies at rest, its outward normal as long as one DIP of the design, and how far it
    // stands out. The drawings of a frame move each point along its normal from these, without asking the model again.
    private Point[] _at = [];
    private Vector[] _normal = [];
    private double[] _out = [];
    // The points of one figure of a frame's drawing, filled and handed over whole (PolyLineTo).
    private readonly List<Point> _run = [];
    private (IslandLiquid Model, double Time)? _sampled;
    private const double Fine = .05;
    private const int Span = 24;

    private IslandLiquid Sample()
    {
        var moving = _liquid is { Liquid: true } ? _liquid : null;
        var model = moving ?? Still;
        double time = moving is null ? 0 : _t;
        if (_sampled is { } made && ReferenceEquals(made.Model, model) && made.Time == time) return model;
        var outline = model.OutlinePoints;
        int count = outline.Length;
        if (_outs.Length < count) _outs = new double[count];
        if (moving is null) Array.Clear(_outs, 0, count); else moving.EdgeOuts(time, _outs);
        _kept.Clear();
        for (int a = 0; a < count;)
        {
            _kept.Add(a);
            if (a == count - 1) break;
            int b = a + 1;
            ref readonly var at = ref outline[a];
            // At rest so far: every point from a to b lies on the outline, as most of a long island does at any moment. The next
            // one that does too needs no look at those between.
            bool rest = _outs[a] == 0 && _outs[b] == 0;
            for (; b < count - 1 && b - a < Span; b++)
            {
                ref readonly var next = ref outline[b + 1];
                if (next.Nx != at.Nx || next.Ny != at.Ny) break;
                if (rest && _outs[b + 1] == 0) continue;
                rest = false;
                double step = (_outs[b + 1] - _outs[a]) / (b + 1 - a);
                bool fits = true;
                for (int j = a + 1; j <= b && fits; j++) fits = Math.Abs(_outs[j] - _outs[a] - step * (j - a)) <= Fine;
                if (!fits) break;
            }
            a = b;
        }
        int kept = _kept.Count;
        if (_at.Length < kept) { _at = new Point[kept]; _normal = new Vector[kept]; _out = new double[kept]; }
        for (int k = 0; k < kept; k++)
        {
            ref readonly var p = ref outline[_kept[k]];
            _at[k] = new Point(_island.X + p.X * _k, _island.Y + p.Y * _k);
            _normal[k] = new Vector(p.Nx * _k, p.Ny * _k);
            _out[k] = _outs[_kept[k]];
        }
        _sampled = (model, time);
        return model;
    }

    // The k-th kept point moved off along its normal, in the stage: by off from where it lies at rest (At), or by off more than
    // it stands out at this frame (Off).
    private Point At(int k, double off) => _at[k] + _normal[k] * off;
    private Point Off(int k, double off) => _at[k] + _normal[k] * (_out[k] + off);

    // The k-th kept point as it stands at this frame, moved in by depth (the stage's DIPs) across the edge as it runs there,
    // between its neighbours within from..to. A band between the edge and these points is as wide on a ripple's slope as on
    // level edge, as a line drawn with a pen is; moved along the normals at rest it would thin out on the slopes.
    private Point Inside(int k, int from, int to, double depth)
    {
        var point = Off(k, 0);
        var along = Off(Math.Min(to, k + 1), 0) - Off(Math.Max(from, k - 1), 0);
        var across = new Vector(along.Y, -along.X);
        double length = across.Length;
        if (!(length > 1e-9)) return point - _normal[k] / _k * depth;
        across /= length;
        return across * _normal[k] < 0 ? point + across * depth : point - across * depth;
    }

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

    // The island's shadow, from a copy of the island at rest: the few DIPs the liquid stands out do not show in it.
    private void DrawShadow(DrawingContext drawing)
    {
        if (_shadow.Effect is null || Form == IslandForm.Line || _part is not null) return;
        drawing.DrawGeometry(Fill, null, RestOutline());
    }

    // The island at rest. What the liquid pushes out of it is drawn over its edge (DrawParts), so it is not drawn again while
    // the liquid moves; the indicator's thread is (DrawLine).
    private void DrawBody(DrawingContext drawing)
    {
        if (Form == IslandForm.Line) { DrawLine(drawing); return; }
        var body = RestOutline();
        // In a part's place only that part of the island is drawn, over the part the island shows there.
        if (_part is { } part) drawing.PushClip(new RectangleGeometry(part));
        drawing.DrawGeometry(Fill, null, body);
        if (_look.Gradient is { } gradient) drawing.DrawGeometry(gradient, null, body);
        if (_look.Pattern is { } pattern) drawing.DrawGeometry(pattern, null, body);
        if (_look.RimWidth > 0)
        {
            // Clipped to the body, the outer half of the stroke goes, so the pen is twice the rim's width (as IslandBorder's).
            drawing.PushClip(body);
            drawing.DrawGeometry(null, new Pen(Rim, _look.RimWidth * 2), body);
            drawing.Pop();
        }
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
    // further in, EdgeMost · exp(−(d / 2.2)^1.3) at the distance d from there, as bands of growing depth whose strengths add up to that.
    private const double EdgeMost = .9;
    private static readonly (double Depth, double Alpha)[] EdgeSteps = MakeEdgeSteps();
    private Brush[]? _edgeBrushes;
    private Point[] _edgeLine = [];

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

    // How far inside the island's edge its light begins. The strip left between them keeps the island's own color, so the light
    // has the island on both sides of it and shows as a line of light on any desktop; right at the edge, a white light met a
    // light desktop and only made the edge look soft.
    private const double EdgeInset = 1.25;

    // The light along the edge, flashing with the backlight (Paint). It is drawn on the island, which BrimDeck colors itself, so
    // it shows whatever the desktop behind is; under the content, which stays as it is. Each of EdgeSteps is a band from the
    // line EdgeInset inside the outline to its depth further in, filled; the notch's are left open at its top edge, which lies
    // against the screen. Without the liquid the outline is the island's at rest and the picture is kept; with it the light
    // follows the frame's outline and is drawn again while it shows (Paint), from the few points Sample keeps. (Strokes of the
    // whole outline clipped to its inside, as at first, cost a long island several milliseconds a frame.)
    private void DrawEdgeLight(DrawingContext drawing)
    {
        _edgeMoved = false;
        if (_part is not null || Form == IslandForm.Line) return;
        Sample();
        int count = _kept.Count;
        if (count < 3) return;
        _edgeMoved = _liquid is { Liquid: true };
        var (color, _) = EdgeLight();
        bool notch = Form == IslandForm.Notch;
        // Brightest over the middle and dimmer towards the ends, as the backlight.
        (double, double)[] stops = notch ? [(0, .6), (.06, .72), (.28, 1), (.72, 1), (.94, .72), (1, .6)] : [(0, .6), (.12, .78), (.32, 1), (.68, 1), (.88, .78), (1, .6)];
        _edgeBrushes ??= [.. EdgeSteps.Select(step => (Brush)Fade(_island.X, _island.Right, stops, color, step.Alpha))];
        // The notch's fades out up its shoulders to nothing where they meet the screen.
        if (notch) drawing.PushOpacityMask(Freeze(new LinearGradientBrush(Colors.Transparent, Colors.Black, new Point(0, _island.Y + _k), new Point(0, _island.Y + Math.Max(2 * _k, _shape.Shoulder * 7 / 16)))
            { MappingMode = BrushMappingMode.Absolute }));
        // The line the light begins at is the same for every band: its points are made once.
        if (_edgeLine.Length < count) _edgeLine = new Point[count];
        for (int k = 0; k < count; k++) _edgeLine[k] = Off(k, -EdgeInset);
        for (int step = 0; step < EdgeSteps.Length; step++)
        {
            double depth = EdgeInset + EdgeSteps[step].Depth;
            var band = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var g = band.Open())
            {
                // The capsule's: the ring between two loops. The notch's: along the line and back along its depth.
                g.BeginFigure(_edgeLine[0], true, true);
                _run.Clear();
                for (int k = 1; k < count; k++) _run.Add(_edgeLine[k]);
                if (notch) for (int k = count - 1; k >= 0; k--) _run.Add(Off(k, -depth));
                g.PolyLineTo(_run, true, false);
                if (!notch)
                {
                    g.BeginFigure(Off(0, -depth), true, true);
                    _run.Clear();
                    for (int k = 1; k < count; k++) _run.Add(Off(k, -depth));
                    g.PolyLineTo(_run, true, false);
                }
            }
            band.Freeze();
            drawing.DrawGeometry(_edgeBrushes[step], null, band);
        }
        if (notch) drawing.Pop();
    }

    // A theme's gradient or rim spreads over the island's bounds; drawn on a part of the island alone it is given those bounds.
    private Brush OnIsland(Brush brush)
    {
        if (brush is not LinearGradientBrush { MappingMode: BrushMappingMode.RelativeToBoundingBox } relative) return brush;
        var absolute = relative.Clone();
        absolute.MappingMode = BrushMappingMode.Absolute;
        absolute.StartPoint = new Point(_island.X + relative.StartPoint.X * _island.Width, _island.Y + relative.StartPoint.Y * _island.Height);
        absolute.EndPoint = new Point(_island.X + relative.EndPoint.X * _island.Width, _island.Y + relative.EndPoint.Y * _island.Height);
        return Freeze(absolute);
    }

    // What the liquid moves at this frame, over the island at rest: each stretch of the outline that stands out, from just inside
    // the island's edge (so no seam shows, and the rim there is covered) out to where the ripples push it, in the island's
    // material with the rim along its new edge. Only those stretches are made, from the points Sample keeps, however long the
    // island is.
    private void DrawParts(DrawingContext drawing)
    {
        _partsDrawn = false;
        if (_liquid is not { Liquid: true } || Form == IslandForm.Line || _part is not null) return;
        Sample();
        // A see-through surface drawn twice would show darker: there the stretch begins at the edge itself.
        double rim = _look.RimWidth, seam = _look.Surface.A < 255 ? 0 : Math.Max(1, rim + .5) / _k;
        const double Least = .04;
        StreamGeometry? swells = null, rims = null;
        StreamGeometryContext? g = null, r = null;
        int count = _kept.Count;
        for (int k = 0; k < count; k++)
        {
            if (!(_out[k] > Least)) continue;
            int from = Math.Max(0, k - 1);
            while (k < count && _out[k] > Least) k++;
            int to = Math.Min(count - 1, k);
            g ??= (swells = new StreamGeometry { FillRule = FillRule.Nonzero }).Open();
            g.BeginFigure(At(from, -seam), true, true);
            _run.Clear();
            for (int j = from; j <= to; j++) _run.Add(Off(j, 0));
            for (int j = to; j > from; j--) _run.Add(At(j, -seam));
            g.PolyLineTo(_run, true, false);
            if (!(rim > 0)) continue;
            // The rim along the new edge: the band from the edge to the rim's width inside it, filled. Drawn as a line with a
            // pen, as at first, it cost a frame more than anything else of the liquid: WPF widens a stroke on the processor.
            r ??= (rims = new StreamGeometry { FillRule = FillRule.Nonzero }).Open();
            r.BeginFigure(Off(from, 0), true, true);
            _run.Clear();
            for (int j = from + 1; j <= to; j++) _run.Add(Off(j, 0));
            for (int j = to; j >= from; j--) _run.Add(Inside(j, from, to, rim));
            r.PolyLineTo(_run, true, false);
        }
        g?.Close(); r?.Close();
        if (swells is not null)
        {
            swells.Freeze();
            drawing.DrawGeometry(Fill, null, swells);
            if (_look.Gradient is { } gradient) drawing.DrawGeometry(_gradient ??= OnIsland(gradient), null, swells);
            if (_look.Pattern is { } pattern) drawing.DrawGeometry(pattern, null, swells);
            if (rims is not null)
            {
                rims.Freeze();
                drawing.DrawGeometry(_rimBrush ??= OnIsland(Rim), null, rims);
            }
            _partsDrawn = true;
        }
    }

    // The beads that trail some ripples, in the island's own color, over the light along its edge.
    private void DrawBeads(DrawingContext drawing)
    {
        if (_liquid is not { Liquid: true } liquid || Form == IslandForm.Line || _part is not null) return;
        liquid.Beads(_t, _circles);
        if (_circles.Count == 0) return;
        var beads = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var b = beads.Open())
            foreach (var c in _circles) Circle(b, _island.X + c.X * _k, _island.Y + c.Y * _k, c.R * _k);
        beads.Freeze();
        drawing.DrawGeometry(Fill, null, beads);
        _partsDrawn = true;
    }

    // How bright the indicator is at this frame: two breaths, or the one of a plain notice.
    private double LineLight => _effect switch { NoticeEffect.Brighten => IslandLiquid.FlashAt(_t), NoticeEffect.LiquidOnly => 0, _ => IslandLiquid.BreathAt(_t) };

    // The indicator's color at this frame: its own, brightened towards the light as it breathes.
    private Color LineColor()
    {
        var color = PanelTheme.ColorOf("indicator");
        if (_liquid is null) return color;
        double b = LineLight * .85;
        var flash = _light == GlowColor ? LineFlash : _light;
        return Color.FromArgb((byte)Math.Round(color.A + (flash.A - color.A) * b), (byte)Math.Round(color.R + (flash.R - color.R) * b),
            (byte)Math.Round(color.G + (flash.G - color.G) * b), (byte)Math.Round(color.B + (flash.B - color.B) * b));
    }

    // The indicator: its thread with round ends, the droplets leaving them, in the indicator's color brightening as it breathes.
    private void DrawLine(DrawingContext drawing)
    {
        var lit = LineColor();
        _lineDrawn = (_liquid is { Liquid: true }, lit);
        // Without the liquid the thread keeps its shape, the one it has at rest: only its color changes.
        if (_liquid is not { Liquid: true } liquid) { drawing.DrawGeometry(Freeze(new SolidColorBrush(lit)), null, RestOutline()); return; }
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

    // The capsule's and the notch's backlight: behind the island, so the liquid shows dark against it. wide: the blurred outer
    // layer. Drawn once at its brightest; Paint sets how bright it shows.
    private void DrawGlow(DrawingContext drawing, bool wide)
    {
        if (Form == IslandForm.Line || _part is not null) return;
        var model = Still;
        var band = new StreamGeometry { FillRule = FillRule.EvenOdd };
        LinearGradientBrush brush;
        if (Form == IslandForm.Capsule)
        {
            double cx = _island.X + _island.Width / 2, cy = _island.Y + _island.Height / 2;
            double left = -model.Width / 2, right = model.Width / 2;
            var (outer, inner) = model.CapsuleRing(left, right, wide ? 10 : 3.4);
            using (var c = band.Open()) { Polygon(c, outer, cx, cy); Polygon(c, inner, cx, cy); }
            brush = Fade(cx + left * _k, cx + right * _k, [(0, .6), (.12, .78), (.32, 1), (.68, 1), (.88, .78), (1, .6)]);
        }
        else
        {
            using (var c = band.Open()) Polygon(c, model.NotchBand(wide ? 12 : 3.8), _island.X, _island.Y);
            brush = Fade(_island.X, _island.Right, [(0, .6), (.06, .72), (.28, 1), (.72, 1), (.94, .72), (1, .6)]);
        }
        band.Freeze();
        drawing.DrawGeometry(brush, null, band);
    }

    // The indicator's bloom: its own shape at rest grown by grow and moved down by drop, blurred (the layer's effect). Paint
    // lets it breathe; a plain notice has none, the line alone lights up.
    private void DrawBloom(DrawingContext drawing, double grow, double drop)
    {
        if (Form != IslandForm.Line) return;
        double cx = _island.X + _island.Width / 2, cy = _island.Y + _island.Height / 2;
        double half = _island.Height / 2 + grow * _k, reach = _island.Width / 2 + grow * _k;
        drawing.DrawRoundedRectangle(Freeze(new SolidColorBrush(_light)), null, new Rect(cx - reach, cy + drop * _k - half, 2 * reach, 2 * half), half, half);
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
        ClearContent();
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
        // A call's ringing time, or what a coding agent waits for, at the right (IslandNotice.Marks.cs).
        available = Math.Max(0, available - BuildTrailing(scale, origin + inset, available, gap));
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
        Scroll(line, width - viewport, (width - viewport) / (32 * scale));
    }

    // Stops the scrolling of the words that show (Scroll); null while none scroll.
    private Action? _scrollStop;

    private void ClearContent()
    {
        _scrollStop?.Invoke(); _scrollStop = null;
        _content.Children.Clear();
    }

    // How long words that scroll rest at their start and at their end, in seconds.
    private const double ScrollHold = 1.2;

    // Words longer than their room are read from the start, carried along at an even pace to their end, held there, and put
    // back at the start, round after round. Only the carrying is an animation; a timer waits out the holds. As one animation
    // over the whole round, as at first, WPF ran every frame of the display for as long as the notice showed, the holds
    // included, which cost more processor time than the notice's own motion.
    private void Scroll(TextBlock line, double travel, double seconds)
    {
        var move = new TranslateTransform();
        line.RenderTransform = move;
        bool stopped = false, atEnd = false;
        var hold = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(ScrollHold) };
        hold.Tick += (_, _) =>
        {
            hold.Stop();
            if (stopped) return;
            if (atEnd) { atEnd = false; move.X = 0; hold.Start(); return; }
            var carry = new DoubleAnimation(0, -travel, TimeSpan.FromSeconds(Math.Max(.01, seconds))) { FillBehavior = FillBehavior.Stop };
            carry.Completed += (_, _) => { if (stopped) return; atEnd = true; hold.Start(); };
            // The words' place once the animation is over, set before it starts: they stay there when it ends.
            move.X = -travel;
            move.BeginAnimation(TranslateTransform.XProperty, carry);
        };
        hold.Start();
        _scrollStop = () => { stopped = true; hold.Stop(); move.BeginAnimation(TranslateTransform.XProperty, null); };
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
                // A planner reminder's buttons act on this island's panel ("查看日程" opens its planner).
                case NoticeActions.ButtonKind.Sender when button.Action is { } action && AgendaReminders.Owns(notice):
                    _deck.AgendaReminderAction(notice, action.Key);
                    break;
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
        // A call and a coding agent's question or approval have marks of their own (IslandNotice.Marks.cs).
        if (OwnMark(size) is { } mark) return mark;
        if (notice is not null && NoticeLetter.ShowsPicture(notice, Settings) && Center.Icons.Get(notice.Picture, out _) is { } avatar) return NoticeLetter.Round(avatar, size);
        var image = _item?.Kind == AttentionKind.Summary ? null : Center.Icons.Get(notice?.IconRef, out _);
        var accent = notice is null ? PanelTheme.ColorOf("accent") : _look.AppColor(Center.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey)));
        // The application's icon round, as the collapsed row shows it (CompactStacks.Icon).
        if (notice is not null && _item?.Kind != AttentionKind.Summary) return CompactStacks.Icon(image, size, _shape.Scale, PanelLook.Of(_deck), notice.AppName, accent, notice.IconRef);
        return NoticeLetter.Plate(notice?.AppName, size, accent, bell: notice is null);
    }

    private void IconLoaded(string iconRef)
    {
        if (_leaving || Notice?.IconRef != iconRef && Notice?.Picture != iconRef && !OwnMarkIcon(iconRef)) return;
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
    // kept: it is drawn once and kept as a picture, together with what its effect makes of it (the effect is the layer's, the
    // picture belongs to the element around it), so a frame that only changes how strongly it shows (Shown) neither draws nor
    // blurs anything. The island's own shadow is kept the same way (MainWindow.Shadow.cs).
    private sealed class Layer : FrameworkElement
    {
        public Action<DrawingContext>? Draw;
        // False until the window has laid the island out: nothing is drawn then.
        public Func<bool>? Ready;
        private readonly Canvas? _keeper;
        private readonly BitmapCache? _picture;
        private readonly Bounds? _bounds;
        // What the layer shows, in a visual of its own under it: a frame of the motion draws into it at once (DrawNow). Drawn
        // through OnRender alone, every layer a frame changes took a layout pass of its own as well.
        private readonly DrawingVisual _drawn = new();
        public Layer(bool kept = false)
        {
            IsHitTestVisible = false;
            AddVisualChild(_drawn);
            if (!kept) return;
            _picture = new BitmapCache { SnapsToDevicePixels = true };
            _keeper = new Canvas { IsHitTestVisible = false, CacheMode = _picture, Children = { (_bounds = new Bounds()), this } };
        }
        // What stands in the window's tree for this layer.
        public UIElement Element => (UIElement?)_keeper ?? this;
        public double Shown { set { var element = Element; if (element.Opacity != value) element.Opacity = value; } }

        // The picture is drawn at the display's scale and reaches past the layer and its blur by whole device pixels: one whose
        // edge fell between pixels is resampled when drawn. WPF already draws it at the system's DPI, so it is asked only for
        // what this monitor's scale adds. Called when the layer's size, its effect or the scale changes.
        public void Fit(double scale, double width, double height)
        {
            if (_keeper is null || !(scale > 0)) return;
            double render = scale / MainWindow.SystemScale;
            if (_picture!.RenderAtScale != render) _picture.RenderAtScale = render;
            double spread = Effect switch { BlurEffect blur => blur.Radius, DropShadowEffect shadow => shadow.BlurRadius + shadow.ShadowDepth, _ => 0 };
            double reach = Math.Ceiling((spread + 2) * scale) / scale;
            var area = new Rect(-reach, -reach, Math.Ceiling((width + reach) * scale) / scale + reach, Math.Ceiling((height + reach) * scale) / scale + reach);
            if (_bounds!.Area == area) return;
            _bounds.Area = area; _bounds.InvalidateVisual();
        }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => index == 0 ? _drawn : throw new ArgumentOutOfRangeException(nameof(index));

        // Draws the layer now. InvalidateVisual still draws it with the next layout pass, for what changes it seldom.
        public void DrawNow()
        {
            using var context = _drawn.RenderOpen();
            if (Ready?.Invoke() != false) Draw?.Invoke(context);
        }
        protected override void OnRender(DrawingContext drawingContext) => DrawNow();

        // Draws nothing that shows: it gives the kept picture its bounds.
        private sealed class Bounds : FrameworkElement
        {
            public Rect Area;
            public Bounds() { IsHitTestVisible = false; }
            protected override void OnRender(DrawingContext drawingContext) { if (!Area.IsEmpty && Area.Width > 0 && Area.Height > 0) drawingContext.DrawRectangle(Brushes.Transparent, null, Area); }
        }
    }
}
