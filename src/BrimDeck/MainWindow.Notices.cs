using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;
using BrimDeck.Core.Notices;
using BrimDeck.Native;
using static BrimDeck.Tracks;
using Track = BrimDeck.Core.Notices.NoticeMotion.Track;

namespace BrimDeck;

// The panel's part of dynamic notices: its lane in the attention coordinator (notices, quota alerts and track changes take
// turns on its island), the notice shown on its island (IslandNotice), and the notices page.
public partial class MainWindow
{
    private static readonly TimeSpan TrackNoticeTime = TimeSpan.FromSeconds(4);
    private IslandNotice? _noticeWindow;
    private bool _noticesSubscribed, _noticesRenderQueued, _keepPageOnOpen;
    // Notices not seen before the page opened; each keeps its dot until it is shown in the focus (_readOnPage).
    private HashSet<string> _freshNotices = [];
    // Those of _freshNotices shown in the focus since the page opened: their dot is gone. The unread list keeps them until it is
    // chosen again, so that a row does not vanish from under the pointer as it is clicked.
    private readonly HashSet<string> _readOnPage = new(StringComparer.Ordinal);
    private bool Fresh(Notice notice) => _freshNotices.Contains(notice.Id) && !_readOnPage.Contains(notice.Id);
    // The rows the page lists, with the revision each was built from, and what else the page was built from (see UpdateNoticeRows).
    private readonly List<(Notice Notice, int Revision, string Key, FrameworkElement Row)> _pageRows = [];
    private string _pageKey = "";
    // The settings, size and theme the page was last built for (NoticeLayout).
    private (DeckSettings? Settings, int Theme, double Width, double Height) _noticeLayout;
    // The empty page's text block, whose width follows the page's (FitNoticeLayout).
    private FrameworkElement? _pageEmpty;
    // A drag of the page's edges is being saved (FinishResize): the settings that come back are a copy of those the page was
    // fitted to during the drag, so it is not built again for them.
    private bool _noticeResizeSaved;

    // The coordinator's name for this panel: the main panel's stays the same when its monitor changes.
    internal string NoticeLane => _lead is null ? "main" : "display:" + (Display?.Id ?? "");
    // ui-2: nothing starts while the panel powers off; it draws back into the dot in sight (PowerOff ends a shown notice).
    internal bool CanShowNotices => IsVisible && !IsSuppressed && !_preview && !_poweringOff;
    // Arriving notices show on this panel: the switch of the profile it shows ("提示新通知" under 收起外观).
    internal bool ShowsNewNotices => Settings.NoticeIsland;
    private bool NoticeCovering => _noticeWindow?.Covering == true;

    private void SubscribeNotices()
    {
        if (_noticesSubscribed || _app.Notices is null) return;
        _noticesSubscribed = true;
        _app.Notices.Changed += NoticesChanged;
        SubscribeAgenda();
        SubscribeAgents();
        _app.Notices.Icons.Loaded += NoticeIconLoaded;
        PrepareNoticeWindow();
    }

    // The first notice builds the notice window (its window, its theme's materials, its fonts), which took the UI thread tens of
    // milliseconds in the middle of the notice's own arrival (live check 10-28: 70 ms in the first notice, 8 to 26 ms in later
    // ones). It is built a few seconds after the panel has started, while nothing moves, with no window shown.
    private void PrepareNoticeWindow()
    {
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle, Dispatcher) { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_noticesSubscribed && _noticeWindow is null && !_expanded && !_booting && !_bootWaiting && CanShowNotices && Settings.NoticeIsland) (_noticeWindow = new IslandNotice(this, _app)).Prepare();
        };
        timer.Start();
    }

    private void CloseNotices()
    {
        if (_noticesSubscribed) { _app.Notices.Changed -= NoticesChanged; _app.Notices.Icons.Loaded -= NoticeIconLoaded; }
        _noticesSubscribed = false;
        UnsubscribeAgenda();
        UnsubscribeAgents();
        if (_app.Notices is not null) { _liveRows.Clear(); WatchPageLive(); }
        _noticeWindow?.Close(); _noticeWindow = null;
        _bootNotice?.Stop(); _noticeAfterBoot = null;
        _app.Notices?.DeckRetired(this);
    }

    // ----- The coordinator's steps for this panel -----

    internal void AttentionStarted(AttentionItem item)
    {
        switch (item.Kind)
        {
            case AttentionKind.Notice or AttentionKind.Summary:
                if (!CanShowNotices) { _app.Notices.Skip(this); return; }
                // ui-2: one that arrives while the panel boots (the summary of what came in while BrimDeck was quiet) waits for
                // the boot to play; covering the island then would hide the boot, and the island would show in the wrong shape.
                // Likewise while the panel is still closing: the notice would take the island's final shape in one frame and the
                // half-closed panel would vanish under it (N50-4).
                if (HoldsNotices) { ShowAfterBoot(item); return; }
                (_noticeWindow ??= new IslandNotice(this, _app)).Present(item);
                break;
            case AttentionKind.Quota when item.Payload is CompactAlertInfo alert:
                PresentAlert(alert); break;
            case AttentionKind.Track:
                // The title takes the whole island, shown as a notice is; the indicator has no room for words, so it shows nothing.
                // One that comes while the panel boots or closes waits for it, as a notice does.
                if (Collapsed.Style == CompactStyle.Line || !CanShowNotices || TrackContent() is not { } content) { _trackShown = false; _app.Notices.Skip(this); return; }
                _trackShown = true;
                // Set to the music part (MusicTrackNoticeInPart): the part's own words show the title in place of the lyric, in their
                // face and size, and the island keeps the width rules it has for them.
                if (TrackInPartPossible) { _trackInPart = true; RenderCompact(); FitCompactToContent(); break; }
                item.Payload = content;
                if (HoldsNotices) { ShowAfterBoot(item); return; }
                (_noticeWindow ??= new IslandNotice(this, _app)).Present(item);
                break;
        }
    }

    // A track change's title shows on the island (or waits for the boot to show it there).
    private bool _trackShown;
    // It shows as the music part's words (_trackShown too): set so, with the part showing words now (its middle text chosen and not
    // hidden while paused). Without them the title takes the whole island.
    private bool _trackInPart;
    private bool TrackInPartPossible => Settings.MusicTrackNoticeInPart && Settings.MusicText != CompactMusicText.None && Settings.ShowsMusic(EffectiveStyle)
        && !PausedHides(PausedMusicHide.Text);

    private TrackNoticeContent? TrackContent()
        => _mediaTrack is { Title.Length: > 0 } track ? new TrackNoticeContent(track.Title, track.Artist, _mediaCover) : null;

    internal void AttentionEnded(AttentionItem item)
    {
        switch (item.Kind)
        {
            case AttentionKind.Notice or AttentionKind.Summary:
                if (_noticeAfterBoot?.Key == item.Key) _noticeAfterBoot = null;
                _noticeWindow?.End(item);
                // Ended because the panel opened: no exit motion over the opening panel.
                if (_expanded) _noticeWindow?.Abort();
                break;
            case AttentionKind.Quota when item.Payload is CompactAlertInfo alert: EndAlert(alert); break;
            case AttentionKind.Track:
                if (!_trackShown) break;
                _trackShown = false;
                if (_trackInPart) { _trackInPart = false; RenderCompact(); FitCompactToContent(); break; }
                if (_noticeAfterBoot?.Key == item.Key) _noticeAfterBoot = null;
                _noticeWindow?.End(item);
                if (_expanded) _noticeWindow?.Abort();
                break;
        }
    }

    internal void AttentionUpdated(AttentionItem item)
    {
        if (item.IsNotice && _noticeAfterBoot?.Key == item.Key) _noticeAfterBoot = item;
        else if (item.IsNotice) _noticeWindow?.Update(item);
        // The next song while the title of the one before still shows on the island: its words change in place.
        else if (item.Kind == AttentionKind.Track && _trackInPart) { RenderCompact(); FitCompactToContent(); }
        else if (item.Kind == AttentionKind.Track && _trackShown && TrackContent() is { } content)
        {
            item.Payload = content;
            if (_noticeAfterBoot?.Key == item.Key) _noticeAfterBoot = item; else _noticeWindow?.Update(item);
        }
        // The same quota rose again while its alert shows: the alert shows the new level.
        else if (item.Kind == AttentionKind.Quota && item.Payload is CompactAlertInfo alert && _alert is not null) { _alert = alert; RenderCompact(); }
    }

    private AttentionItem? _noticeAfterBoot;
    private System.Windows.Threading.DispatcherTimer? _bootNotice;
    // win2 P4: when the held notice started waiting. A boot that never ends (its clock replaced, frames stopped on a locked
    // session) holds it no longer than BootNoticeLimit; the notice then shows over whatever the island is.
    private long _bootNoticeSince;
    private const int BootNoticeLimit = 10_000;
    // When the panel's closing ends (SetExpanded); 0 when it is not closing.
    private long _collapseEnds;
    // A notice waits for the boot to play, for the panel to finish closing, and for the island to reach the width its content
    // last asked for: started during that move, the notice took the new width at once over an island still on its way there.
    private bool HoldsNotices => BootHoldsPointer || Environment.TickCount64 < Math.Max(_collapseEnds, _contentResizeEnds);

    // Shows the item once the boot has played and the panel has closed; ended or updated meanwhile (AttentionEnded,
    // AttentionUpdated), the latest wins.
    private void ShowAfterBoot(AttentionItem item)
    {
        _noticeAfterBoot = item;
        if (_bootNotice is null)
        {
            _bootNotice = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, Dispatcher);
            _bootNotice.Tick += (_, _) =>
            {
                long now = Environment.TickCount64;
                if (HoldsNotices && _noticeAfterBoot is not null && now - _bootNoticeSince < BootNoticeLimit)
                {
                    // Once the boot has started (or the closing) its end is known: the next look is then, not every 100 ms.
                    _bootNotice.Interval = BootNoticeWait(now);
                    return;
                }
                _bootNotice.Stop();
                if (_noticeAfterBoot is not { } waiting) return;
                _noticeAfterBoot = null;
                if (!CanShowNotices || _expanded) { _app.Notices.Skip(this); return; }
                (_noticeWindow ??= new IslandNotice(this, _app)).Present(waiting);
            };
        }
        if (!_bootNotice.IsEnabled) _bootNoticeSince = Environment.TickCount64;
        _bootNotice.Interval = BootNoticeWait(Environment.TickCount64);
        _bootNotice.Start();
    }

    // Until the boot's or the closing's end, or a short look while the boot still waits for an idle dispatcher (Boot), never
    // past the limit.
    private TimeSpan BootNoticeWait(long now)
    {
        long ends = Math.Max(_bootEnds, Math.Max(_collapseEnds, _contentResizeEnds));
        long wait = _bootWaiting || ends <= now ? 100 : ends - now + 1;
        return TimeSpan.FromMilliseconds(Math.Clamp(wait, 1, Math.Max(1, _bootNoticeSince + BootNoticeLimit - now)));
    }

    // While a notice covers the island, the island itself is not drawn (see IslandNotice.Present and Finish). A notice that has
    // gone back into the island (reveal) leaves it without content; the island's own content then lands as it does when the
    // panel closes (SetExpanded's compact track), instead of appearing all at once.
    // ui-2: being covered is its own state, shown through HoverZone's Visibility; the boot and the power-off own its Opacity.
    // The island shows only when both allow it, and neither undoes the other.
    private bool _islandCovered;
    internal void SetIslandCovered(bool covered, bool reveal = false)
    {
        _islandCovered = covered;
        HoverZone.Visibility = covered ? Visibility.Hidden : Visibility.Visible;
        if (covered) return;
        // The width the content asked for while the notice showed (FitCompactToContent) is taken once this call is done: it is
        // reached from inside SetExpanded (the panel opening over the notice) and ApplySettings, which a fit must not run within.
        if (_fitAfterNotice) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, FitCompactToContent);
        if (!_compactFaded) return;
        // The island's own content faded out for the notice (FadeIslandContent) comes back: by its opacity alone once the notice ran
        // out, at once when the panel opens over it (its opening moves the content itself).
        _compactFaded = false;
        bool fade = reveal && Soft.Enabled && !_expanded && !_poweringOff && !BootHoldsPointer;
        if (fade) Animate(Compact, OpacityProperty, 1, IslandNotice.FadeIn, new Track(0, 1), Soft.Out);
        else SetImmediate(Compact, OpacityProperty, 1d);
    }

    // The island's own content fades out before a notice takes the island, so the notice's words come in after it rather than over
    // it; done follows once it has gone (at once when nothing fades: the indicator has no content, an open panel, no animations).
    private bool _compactFaded;
    internal void FadeIslandContent(Action done)
    {
        if (_expanded || _poweringOff || BootHoldsPointer || !Soft.Enabled || Compact.Visibility != Visibility.Visible || Collapsed.Style == CompactStyle.Line) { done(); return; }
        _compactFaded = true;
        Animate(Compact, OpacityProperty, 0, IslandNotice.FadeOut, new Track(0, 1), Soft.InOut, done);
    }

    // The island moved or changed style, or the theme changed: a shown notice follows.
    private void NoticesLayoutChanged() => _noticeWindow?.Refresh();

    // The notices hotkey with a notice on this panel: its details take the keyboard.
    internal bool NoticeKeyboard()
    {
        if (_noticeWindow?.Item is null) return false;
        _noticeWindow.EnterKeyboard();
        return true;
    }

    // Opens the panel on the notices page, as the expand hotkey opens it: it stays open until the pointer has been in it.
    // byKey: the notices hotkey, rather than the pointer resting on a notice on the island. focus: the notice to put in front.
    internal void OpenNoticesPage(bool byKey = true, string? focus = null)
    {
        if (!DeckPages.Shown(Settings, DeckPage.Notices)) return;
        if (focus is not null) _noticeFocus = focus;
        if (_expanded) { SelectPage(DeckPage.Notices); if (focus is not null) RenderNotices(); return; }
        SelectPage(DeckPage.Notices, remember: false);
        // Opening marks the list seen before it draws the page (SetExpanded): marked here afterwards, the page was built a second
        // time in the middle of the opening.
        _keepPageOnOpen = true;
        try { OpenFromHotkey(byKey: byKey); } finally { _keepPageOnOpen = false; }
    }

    private void NoticesPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Notices);

    // The page is on screen: everything listed counts as seen; what was new keeps its dot until it is shown in the focus.
    private void NoticesPageShown()
    {
        TakeFreshNotices();
        _noticeEarlier = false;
        _app.Notices.Hub.MarkAllViewed();
    }

    // The notices not seen yet. While they are the same notices the set stays the same, with what of it was read on the page, so
    // a page built for them while the panel was closed (PrebuildNotices) is still what the opening shows.
    private void TakeFreshNotices()
    {
        var fresh = _app.Notices.Hub.Notices.Where(n => !n.Viewed).Select(n => n.Id).ToHashSet();
        if (_freshNotices.SetEquals(fresh)) return;
        _freshNotices = fresh;
        _readOnPage.Clear();
    }

    // ----- Building the page while the panel is closed -----
    //
    // Every other page is built while the panel is closed, so its opening only shows it. The notices page was built as the panel
    // opened, and building and laying it out held the opening's first frame for 70 to 270 ms (ReadmeMedia notice-perf,
    // 2026-11-25): the surface appeared most of the way open instead of growing. It is now built a while after what it shows
    // changed, while the panel is closed on it, without marking anything seen; the opening shows it when nothing changed since.

    // The page on screen was built while the panel was closed, and has not been shown since.
    private bool _noticesPrebuilt;
    private System.Windows.Threading.DispatcherTimer? _noticePrebuild;
    private long _noticePrebuildAt;
    // After a notice arrives the page waits for its motion on the island (about 1.8 s), which the build would hold up for a few
    // frames; after the panel closes, for the closing.
    private static readonly TimeSpan PrebuildAfterNotice = TimeSpan.FromSeconds(2), PrebuildAfterClosing = TimeSpan.FromMilliseconds(600);

    private void QueueNoticePrebuild(TimeSpan after)
    {
        if (_expanded || _preview || CurrentPage != DeckPage.Notices) return;
        long now = Environment.TickCount64, at = Math.Max(_noticePrebuildAt, now + (long)after.TotalMilliseconds);
        if (_noticePrebuild is null)
        {
            _noticePrebuild = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle, Dispatcher);
            _noticePrebuild.Tick += (_, _) => PrebuildNotices();
        }
        _noticePrebuildAt = at;
        _noticePrebuild.Stop();
        _noticePrebuild.Interval = TimeSpan.FromMilliseconds(Math.Max(1, at - now));
        _noticePrebuild.Start();
    }

    private void PrebuildNotices()
    {
        _noticePrebuild?.Stop();
        _noticePrebuildAt = 0;
        if (_expanded || _preview || CurrentPage != DeckPage.Notices || _app.Notices is null || !DeckPages.Shown(Settings, DeckPage.Notices)) return;
        // What the opening resets (NoticesPageShown), without marking anything seen.
        TakeFreshNotices();
        _noticeEarlier = false;
        if (_noticesPrebuilt && NoticesContent.Visibility == Visibility.Visible && FitNoticeLayout() && UpdateNoticeRows()) return;
        RenderNotices(fresh: true);
        _noticesPrebuilt = true;
    }

    // The panel opens on the notices page: the page built while it was closed shows when it is still what the page shows,
    // otherwise the page is built now.
    private void ShowNoticesOnOpening()
    {
        NoticesPageShown();
        bool ready = _noticesPrebuilt && NoticesContent.Visibility == Visibility.Visible && FitNoticeLayout() && UpdateNoticeRows();
        _noticesPrebuilt = false;
        if (!ready) { RenderNotices(fresh: true); return; }
        // What RenderNotices does only for a page on screen.
        WatchPageLive();
    }

    // The panel closed: the page is built again for the next opening, at the top of its list and without what was opened on it.
    private void NoticesPanelClosed() => QueueNoticePrebuild(PrebuildAfterClosing);

    private void NoticesChanged()
    {
        UpdateNoticeBadge();
        NoticeStripChanged();
        if (!_expanded) QueueNoticePrebuild(PrebuildAfterNotice);
        if (!_expanded || CurrentPage != DeckPage.Notices || _noticesRenderQueued) return;
        // Several changes in a row (a burst, icons arriving) are drawn once.
        _noticesRenderQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            // Marked viewed while the render is still queued: the change that raises is not drawn a second time (perf-8).
            bool showing = _expanded && CurrentPage == DeckPage.Notices;
            if (showing) _app.Notices.Hub.MarkAllViewed();
            _noticesRenderQueued = false;
            if (showing && !UpdateNoticeRows()) RenderNotices();
        });
    }

    // What the page shows besides its rows; a change of it builds the whole page again.
    private string NoticePageKey(NoticeCenter center) => string.Join('|', center.Paused, center.Quiet, center.Epoch, _noticeEarlier, _noticeUnread, FocusedNotice(center)?.Id,
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_freshNotices), Loc.Language, center.WindowsState, DateTime.Today, _noticeSyncSaid);

    // The sync button's state: what the page says in place of the day's count while it syncs and for a few seconds after.
    private string? _noticeSyncSaid;
    private bool _noticeSyncing;
    private System.Windows.Threading.DispatcherTimer? _noticeSyncTimer;
    private static readonly TimeSpan NoticeSyncSaidFor = TimeSpan.FromSeconds(4);

    // Makes the notices from Windows' notification center match it: those gone from it are deleted, those missing are added.
    // Notices of other sources stay (NoticeCenter.SyncWindowsAsync).
    private async Task SyncWindowsNotices()
    {
        if (_noticeSyncing || _app.Notices is not { } center) return;
        _noticeSyncing = true;
        _noticeSyncTimer?.Stop();
        ShowNoticeSync(Loc.T("正在同步 Windows 通知…", "Syncing Windows notifications…"));
        try
        {
            var (status, removed, added) = await center.SyncWindowsAsync();
            ShowNoticeSync(status switch
            {
                NoticeCenter.WindowsSyncStatus.Done when removed == 0 && added == 0 => Loc.T("已与 Windows 通知一致", "Already matches Windows notifications"),
                NoticeCenter.WindowsSyncStatus.Done => Loc.T($"已同步：删除 {removed} 条，补充 {added} 条", $"Synced: {removed} removed, {added} added"),
                NoticeCenter.WindowsSyncStatus.NoAccess => Loc.T("未获得读取 Windows 通知的权限", "No access to Windows notifications"),
                NoticeCenter.WindowsSyncStatus.Off => Loc.T("未接收 Windows 应用通知", "Not receiving Windows notifications"),
                _ => Loc.T("同步失败，请稍后再试", "Sync failed; try again later")
            });
        }
        finally { _noticeSyncing = false; }
        _noticeSyncTimer ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, Dispatcher) { Interval = NoticeSyncSaidFor };
        _noticeSyncTimer.Tick -= NoticeSyncSaidEnded; _noticeSyncTimer.Tick += NoticeSyncSaidEnded;
        _noticeSyncTimer.Start();
    }

    private void NoticeSyncSaidEnded(object? sender, EventArgs e)
    {
        _noticeSyncTimer?.Stop();
        ShowNoticeSync(null);
    }

    private void ShowNoticeSync(string? said)
    {
        _noticeSyncSaid = said;
        if (_expanded && CurrentPage == DeckPage.Notices) RenderNotices();
    }

    // What the page's frame was built for: the settings (each change replaces them), its size and the theme.
    private (DeckSettings? Settings, int Theme, double Width, double Height) NoticeLayout()
        => (Settings, PanelTheme.Generation, ContentCanvas.Width, ContentCanvas.Height);

    // RenderUsage on the notices page (N50-2). Closed, the page is built a while later (PrebuildNotices), or as the panel opens
    // (SetExpanded) with what arrived meanwhile. Open with the page already on screen for the same theme, a data refresh or the minute's tick
    // builds it again only when what it shows changed, and a new size only fits it (FitNoticeLayout).
    private void RenderNoticesPage()
    {
        if (!_expanded && !_preview) { QueueNoticePrebuild(PrebuildAfterClosing); return; }
        if (NoticesContent.Visibility == Visibility.Visible && FitNoticeLayout() && UpdateNoticeRows()) { RefreshStatus(); return; }
        RenderNotices();
    }

    // Whether the page on screen was built for these settings and this theme, and fits it to a new size if that is all that
    // changed: its two columns share the width by proportion, so a drag of the panel's edges only fits the header and the margins.
    private bool FitNoticeLayout()
    {
        var layout = NoticeLayout();
        if (_noticeLayout == layout) return true;
        bool sameSettings = ReferenceEquals(_noticeLayout.Settings, layout.Settings) || _noticeResizeSaved;
        if (!sameSettings || _noticeLayout.Theme != layout.Theme) return false;
        ApplyPageHeader(PanelHeader.ForPanel(ContentCanvas.Height));
        NoticesContent.Margin = PageMargin;
        if (_pageEmpty is { } empty) empty.MaxWidth = EmptyWidth;
        _noticeLayout = layout;
        return true;
    }

    // Whether the page on screen still shows what the notices are: the same notices in the same order, none changed and no
    // relative time moved on. False when the page needs building again.
    private bool UpdateNoticeRows()
    {
        var center = _app.Notices;
        if (NoticeFormKeepsPage(center) || QuestionFormKeepsPage(center) || ReplyKeepsPage(center)) return true;
        if (_pageKey != NoticePageKey(center)) return false;
        var listed = Listed(center);
        if (_noticeBatches is { Pending: true } batches)
        {
            if (!listed.SequenceEqual(batches.Items, ReferenceEqualityComparer.Instance)) return false;
        }
        else if (listed.Count != _pageRows.Count) return false;
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < _pageRows.Count; i++)
        {
            var (notice, revision, key, _) = _pageRows[i];
            if (!ReferenceEquals(listed[i], notice) || notice.Revision != revision || RowKey(notice, now) != key) return false;
        }
        return _focusKey == FocusKey(FocusedNotice(center), now);
    }

    // The bell's 16 DIP box without a circle 1 DIP wider than the unseen dot, so the bell keeps clear of the dot on any fill.
    private static readonly Geometry NoticeBellCut = FrozenGeometry("M 0,0 H 16 V 16 H 0 Z M 11,2 A 3,3 0 1 0 17,2 A 3,3 0 1 0 11,2 Z");

    private void UpdateNoticeBadge()
    {
        if (_app.Notices is null) return;
        int count = _app.Notices.Hub.UnviewedCount;
        bool unseen = count > 0 && Settings.NoticesEnabled;
        NoticesBadge.Visibility = unseen ? Visibility.Visible : Visibility.Collapsed;
        NoticesBell.Clip = unseen ? NoticeBellCut : null;
        AutomationProperties.SetName(NoticesPageButton, Loc.T("通知", "Notices") + (count > 0 ? " · " + NoticeText.Count(count) : ""));
        UpdateMoreButton();
    }

    // ----- The notices page -----
    //
    // Two columns, as the music page: on the left the focus, one notice at a time with its application's icon as the cover, its
    // title, its words and what can be done with it; on the right today's notices, scrolling within the panel. The focus is an
    // incoming call, else the notice the user picked (in the list, or by resting the pointer on it on the island), else the newest.
    // Without any notice, one empty state takes both columns.

    // Status colors of the design: an incoming call (and a success), a warning, an error (danger); unread dots. All from the theme,
    // which keeps each readable on its surface (PanelThemes.Tokens).
    private static string CallColor => PanelTheme.Hex("success");
    private static string WaitColor => PanelTheme.Hex("caution");
    private static string UnreadColor => UI.Accent;
    // A call stays in front for this long after it arrived.
    private static readonly TimeSpan CallFocus = TimeSpan.FromMinutes(2);
    // The notice put in front (by a click in the list, or from the island); the list shows only unread ones; earlier days are listed.
    private string? _noticeFocus;
    private bool _noticeUnread, _noticeEarlier;
    private string _focusKey = "";

    private Thickness PageMargin => new(_header.ContentSide, 0, Math.Max(0, _header.ContentSide - 6), 14);

    private static bool Today(Notice notice) => notice.ReceivedAt.ToLocalTime().Date == DateTime.Today;

    // Notices of one conversation: a chat application's with the same title (NoticeConversations). Any other notice stands alone.
    private static string Conversation(Notice notice) => NoticeConversations.Key(notice);

    // How many notices each listed conversation holds and whether any of them is unread, as of the last Listed.
    private readonly Dictionary<string, (int Count, bool Fresh)> _conversations = new(StringComparer.Ordinal);

    // The notices the right column lists: today's (or all, once earlier ones were asked for), all or only the unread ones. A
    // conversation takes one row, its newest notice; the focus shows the others under it.
    private List<Notice> Listed(NoticeCenter center)
    {
        IEnumerable<Notice> notices = center.Hub.Notices;
        if (!_noticeEarlier) notices = notices.Where(Today);
        if (_noticeUnread) notices = notices.Where(n => _freshNotices.Contains(n.Id));
        _conversations.Clear();
        var heads = new List<Notice>();
        foreach (var notice in notices)
        {
            string key = Conversation(notice);
            bool fresh = Fresh(notice);
            if (_conversations.TryGetValue(key, out var seen)) { _conversations[key] = (seen.Count + 1, seen.Fresh || fresh); continue; }
            _conversations[key] = (1, fresh);
            heads.Add(notice);
        }
        return heads;
    }

    private (int Count, bool Fresh) ConversationOf(Notice notice) => _conversations.TryGetValue(Conversation(notice), out var found) ? found : (1, Fresh(notice));

    private Notice? FocusedNotice(NoticeCenter center)
    {
        var notices = center.Hub.Notices;
        if (notices.Count == 0) return null;
        var now = DateTimeOffset.UtcNow;
        if (notices.FirstOrDefault(n => n.Call && n.SourcePresent && now - n.ReceivedAt < CallFocus) is { } call) return call;
        if (_noticeFocus is not null && notices.FirstOrDefault(n => n.Id == _noticeFocus) is { } picked) return picked;
        // A sender that waits for an answer (an approval, a question) comes before the newest notice, the newest of them first.
        if (notices.FirstOrDefault(n => n.Awaited && center.Awaits(n)) is { } waiting) return waiting;
        return notices[0];
    }

    // fresh: the page comes into view (the panel opens on it, or another page gives way to it).
    private void RenderNotices(bool fresh = false)
    {
        _noticesPrebuilt = false;
        fresh |= NoticesContent.Visibility != Visibility.Visible;
        SubscribeNotices();
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed; ShelfContent.Visibility = Visibility.Collapsed; DevicesContent.Visibility = Visibility.Collapsed; AgendaContent.Visibility = Visibility.Collapsed; NoticesContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        ApplyPageHeader(PanelHeader.ForPanel(ContentCanvas.Height));
        double offset = fresh ? 0 : _pageScroll?.VerticalOffset ?? 0;
        // Worked from the keyboard, the page keeps the focus on the same notice when it is built again.
        string? refocus = NoticesContent.IsKeyboardFocusWithin ? _focusedNotice : null;
        NoticesContent.Children.Clear(); NoticesContent.ColumnDefinitions.Clear();
        _waitingPlates.Clear(); _focusGlow = null;
        _liveRows.Clear();
        _pageRows.Clear(); _noticeBatches?.Cancel(); _noticeBatches = null; _wordBatches?.Cancel(); _wordBatches = null; _pageEmpty = null; _pageScroll = null;
        NoticesContent.Margin = PageMargin;
        var center = _app.Notices;
        var now = DateTimeOffset.UtcNow;
        _pageKey = NoticePageKey(center); _noticeLayout = NoticeLayout();
        var focus = FocusedNotice(center);
        MarkReadOnPage(center, focus);
        KeepNoticeForm(focus);
        _focusKey = FocusKey(focus, now);
        if (focus is null) { NoticesContent.Children.Add(_pageEmpty = Empty(center)); WatchPageLive(); return; }
        // The columns share the width by proportion, but the list never gets less than ListMinimum: at the narrowest page (440) the
        // proportion alone left it about 170 DIP, too little for a title, a time and a header.
        NoticesContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(452, GridUnitType.Star), MinWidth = FocusMinimum });
        NoticesContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(308, GridUnitType.Star), MinWidth = ListMinimum });
        var left = FocusColumn(focus, center, now);
        NoticesContent.Children.Add(left);
        var right = ListColumn(center, focus, now, refocus, offset > 0);
        Grid.SetColumn(right, 1);
        NoticesContent.Children.Add(right);
        if (offset > 0 && _pageScroll is { } scroll) { scroll.UpdateLayout(); scroll.ScrollToVerticalOffset(offset); }
        if (refocus is not null && _pageRows.FirstOrDefault(r => r.Notice.Id == refocus).Row is { } focused)
            Dispatcher.BeginInvoke(() => focused.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        WatchPageLive();
    }

    // The notice in the focus has been read, with the rest of its conversation, which the focus shows under it: their dots go.
    private void MarkReadOnPage(NoticeCenter center, Notice? focus)
    {
        if (focus is null || _freshNotices.Count == 0) return;
        string conversation = Conversation(focus);
        foreach (var notice in center.Hub.Notices)
            if (_freshNotices.Contains(notice.Id) && Conversation(notice) == conversation) _readOnPage.Add(notice.Id);
    }

    // The unread list from now on leaves out what was read on the page; a new set, so that the page key changes.
    private void ShowUnreadOnly()
    {
        if (_readOnPage.Count > 0) { _freshNotices = _freshNotices.Where(id => !_readOnPage.Contains(id)).ToHashSet(); _readOnPage.Clear(); }
        _noticeUnread = true;
        RenderNotices();
    }

    private ScrollViewer? _pageScroll;
    // The height of the fade at the foot of the list, and of the one under a long text in the focus.
    private const double FadeHeight = 36, WordsFadeHeight = 18, NoticeListGap = 10;
    // The least width of the focus and of the list (DIP).
    private const double FocusMinimum = 170, ListMinimum = 228;

    // What the focus column shows apart from what RowKey covers: its relative time and its live words.
    private string FocusKey(Notice? notice, DateTimeOffset now) => notice is null ? "" : string.Join('\u0001', notice.Id, notice.Revision, NoticeText.Relative(notice.ReceivedAt, now),
        notice.Progress, NoticeClock.IsLive(notice.Timing, now), _app.Notices.CanOpen(notice), AgendaKey(notice));

    // ----- The focus column -----

    private FrameworkElement FocusColumn(Notice notice, NoticeCenter center, DateTimeOffset now)
    {
        var accent = PanelTheme.Readable(center.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey)));
        _focusWords.Clear();
        var column = new Grid { Margin = new Thickness(0, 4, 20, 0), ClipToBounds = false };
        // A faint glow in the color of the picture in front (the sender's avatar, or the application's icon) behind the column, as
        // the music page takes its color from the cover. As wide as the column (and 60 beyond it to the left), not a fixed 420: a glow
        // wider than its column is cut at the column's edge where it is still visible.
        var glow = new Border
        {
            Height = 300, Margin = new Thickness(-60, -64, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false
        };
        PaintGlow(glow, notice);
        _focusGlow = (glow, notice);
        column.Children.Add(glow);
        var stack = new DockPanel { LastChildFill = true };
        column.Children.Add(stack);

        // The status line: what kind of notice is in front, and how many of the same conversation it stands for.
        string conversation = Conversation(notice);
        var group = center.Hub.Notices.Where(n => Conversation(n) == conversation).ToList();
        var meta = new DockPanel { Height = 20 };
        DockPanel.SetDock(meta, System.Windows.Controls.Dock.Top); stack.Children.Add(meta);
        var (status, color) = StatusOf(notice, center);
        if (color is not null)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(color), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(dot);
        }
        meta.Children.Add(UI.Fixed(status, 12, 20, color ?? UI.Secondary, FontWeights.SemiBold));
        string aside = group.Count > 1 ? Loc.T($"{group.Count} 条合并", $"{group.Count} merged") : NoticeText.OriginName(notice.Origin, notice.Distro);
        var asideText = UI.Fixed(aside, 11.5, 20, UI.Tertiary); asideText.HorizontalAlignment = HorizontalAlignment.Right;
        meta.Children.Add(asideText);

        // The hero: the icon as the cover, the application and when, the title, and where it came from.
        var hero = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        DockPanel.SetDock(hero, System.Windows.Controls.Dock.Top); stack.Children.Add(hero);
        // The cover is drawn at 56 and shown smaller on a narrow column (HeroNarrow), leaving the words more room.
        var plate = NoticePlate(notice, 56);
        var cover = new Viewbox { Width = 56, Height = 56, Stretch = Stretch.Uniform, Child = plate };
        var coverBox = new Border { Child = cover, VerticalAlignment = VerticalAlignment.Center };
        hero.Children.Add(coverBox);
        // Only the icon opens what the main button opens: the application, or else the mailbox ("打开邮箱"). The words beside it are
        // for selecting and copying, and a press between them does nothing. The hero takes a right click anywhere (OfferCopy).
        hero.Background = Brushes.Transparent;
        Func<Task>? openFromIcon = center.CanOpen(notice) ? () => OpenFromPage(notice)
            : !AgendaReminders.Owns(notice) && PageMainAction(notice, center) is { Kind: NoticeActionKind.Open } mailbox ? async () => { await center.RunAsync(notice, mailbox); RenderNotices(); } : null;
        if (openFromIcon is not null)
        {
            coverBox.Background = Brushes.Transparent; coverBox.Cursor = Cursors.Hand;
            coverBox.MouseLeftButtonUp += (_, e) => { e.Handled = true; _ = openFromIcon(); };
        }
        var who = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(who, 1); hero.Children.Add(who);
        // The sender's second line (a project, a branch, a session) stands between the source and the time.
        var source = SelectableWords(notice.AppName + (notice.Context.Length > 0 ? " · " + notice.Context : "") + " · " + NoticeText.Relative(notice.ReceivedAt, now), 12, 17, UI.Secondary, line: true);
        who.Children.Add(TrimmedLine(source));
        // A verification code is hidden wherever the notice shows its words, and a screen reader is told so instead of hearing dots.
        var shown = NoticeCodes.Words(notice, Settings.NoticeCodes);
        var title = SelectableWords(shown.Title.Length > 0 ? shown.Title : notice.AppName, 19, 25, UI.Primary, line: true, FontWeights.SemiBold);
        title.Margin = new Thickness(0, 2, 0, 0);
        if (shown.HasCode && notice.Title.Length > 0) AutomationProperties.SetName(title, shown.SpokenTitle);
        who.Children.Add(TrimmedLine(title));
        string context = NoticeFormOpen(notice) && NoticeText.Summary(shown.Body) is { Length: > 0 } summary ? summary : ShownPresent(notice) ? NoticeText.CategoryName(notice.Category) : Loc.T("来源已关闭这条通知", "Closed at the source");
        Grid? contextLine = null;
        if (context.Length > 0) { var ctx = SelectableWords(context, 12, 17, UI.Tertiary, line: true); ctx.Margin = new Thickness(0, 2, 0, 0); contextLine = TrimmedLine(ctx); who.Children.Add(contextLine); }
        // On a narrow column the cover is smaller, and the third line is left out where it does not fit whole: cut, it says too
        // little to keep. The title and the line above it end in "…" (TrimmedLine).
        hero.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            bool narrow = e.NewSize.Width < HeroNarrow;
            cover.Width = cover.Height = narrow ? 40 : 56;
            who.Margin = new Thickness(narrow ? 10 : 14, 0, 0, 0);
        };
        // The line's whole width is kept (FitLineBox): measuring it again at each new width of a drag cost a layout of its words per frame.
        if (contextLine is not null && contextLine.Children[1] is FitLineBox whole)
            who.SizeChanged += (_, e) =>
            {
                if (e.WidthChanged) contextLine.Visibility = whole.Natural <= e.NewSize.Width ? Visibility.Visible : Visibility.Collapsed;
            };

        // The buttons, at the foot of the column.
        var buttons = FocusButtons(notice, center);
        DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom); stack.Children.Add(buttons);
        // The form adding the notice to the planner, or changing what it added, takes the place of the strip and the words.
        if (NoticeFormFor(notice, meta, hero) is { } form) { stack.Children.Add(form); return column; }
        // A sender's questions take the place of the strip and the words: the questions are the words.
        if (QuestionFormFor(notice, center) is { } questions) { stack.Children.Add(questions); return column; }
        // Above them, what BrimDeck read from the notice: a time to add to the planner, what was added, a verification code.
        // A WeChat chat can be answered here, right above the buttons.
        if (NoticeReplyStrip(notice, center) is { } reply) { DockPanel.SetDock(reply, System.Windows.Controls.Dock.Bottom); stack.Children.Add(reply); }
        if (NoticeAgendaStrip(notice) is { } strip) { DockPanel.SetDock(strip, System.Windows.Controls.Dock.Bottom); stack.Children.Add(strip); }

        // The words, scrolling within the column when they are long; the other messages of the same conversation under them. Only
        // the messages in view are laid out again for each width of a drag (ViewportStack).
        var words = new ViewportStack { Margin = new Thickness(0, 0, 0, WordsFadeHeight) };
        if (NoticeTimeLine(notice, now) is { } timeLine) { timeLine.FontSize = 13; timeLine.FontWeight = FontWeights.SemiBold; timeLine.Margin = new Thickness(0, 0, 0, 4); words.Children.Add(timeLine); }
        if (notice.Body.Length > 0)
        {
            var body = SelectableWords(shown.Body, group.Count > 1 ? 14 : 13, group.Count > 1 ? 22 : 21, UI.Primary, line: false);
            if (shown.HasCode) AutomationProperties.SetName(body, shown.SpokenBody);
            if (group.Count > 1)
            {
                // Among the messages of a conversation the newest carries its time on the right as the others do.
                var newest = new DockPanel();
                var when = UI.Fixed(NoticeText.Relative(notice.ReceivedAt, now), 11, 22, UI.Tertiary);
                DockPanel.SetDock(when, System.Windows.Controls.Dock.Right); when.Margin = new Thickness(8, 0, 0, 0); when.VerticalAlignment = VerticalAlignment.Top;
                newest.Children.Add(when);
                newest.Children.Add(body);
                words.Children.Add(newest);
            }
            else words.Children.Add(body);
        }
        if (notice.Code.Length > 0) words.Children.Add(NoticeCodeBlock(notice.Code));
        if (notice.Progress is { } progress) words.Children.Add(ProgressTrack(progress, accent));
        if (group.Count > 1)
        {
            // The other messages in full, wrapped and selectable like the newest: a chat's messages are put together here, so cutting
            // them to a line would leave the rest of what was said nowhere to read. Up to twenty, the first few at once and the rest
            // from queued passes: built all at once, their text boxes held the panel's opening for well over a frame.
            words.Children.Add(new Border { Height = 1, Background = UI.Brush(UI.Hairline), Margin = new Thickness(0, 8, 0, 2) });
            var others = group.Where(n => !ReferenceEquals(n, notice)).Take(20).ToList();
            _wordBatches = new RowBatches<Notice>(others, (_, other) => AddOtherWords(words, other, now),
                action => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, action), OtherWordsBatch);
            _wordBatches.Start(InitialOtherWords);
        }
        var scroll = new ScrollViewer { Content = words, Margin = new Thickness(0, 14, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly,
            Background = Brushes.Transparent };
        // A right click on the words or on the lines beside the icon offers to copy what is selected, the whole text and the title.
        // Also after a text box has marked the click handled, and marking it handled here keeps the text box's own menu away.
        void OfferCopy(object sender, MouseButtonEventArgs e)
        {
            string selected = WordsBox(e.OriginalSource) is { SelectionLength: > 0 } picked ? picked.SelectedText : "";
            var text = CopyText(notice, group.Count > 0 ? group : [notice]);
            if (selected.Length == 0 && text is not { Length: > 0 }) return;
            e.Handled = true;
            var menu = NewMenu();
            if (selected.Length > 0) menu.Items.Add(Item(Loc.T("复制所选文字", "Copy selection"), () => CopyWords(selected)));
            if (text is { Length: > 0 }) menu.Items.Add(Item(Loc.T("复制内容", "Copy text"), () => CopyWords(text)));
            if (shown.Title.Length > 0) menu.Items.Add(Item(Loc.T("复制标题", "Copy title"), () => CopyWords(shown.Title)));
            OpenMenu(menu, (FrameworkElement)sender, atPointer: true);
        }
        scroll.AddHandler(MouseRightButtonUpEvent, new MouseButtonEventHandler(OfferCopy), true);
        hero.AddHandler(MouseRightButtonUpEvent, new MouseButtonEventHandler(OfferCopy), true);
        // A long text fades at the column's foot rather than being cut mid line.
        NoticeFade.Foot(scroll, WordsFadeHeight);
        stack.Children.Add(scroll);
        return column;
    }

    // The earlier messages of the focus's conversation built at once, and per queued pass afterwards.
    private const int InitialOtherWords = 4, OtherWordsBatch = 3;
    private RowBatches<Notice>? _wordBatches;

    // One earlier message of the focus's conversation under its newest: its words, wrapped, and when it came on the right.
    private void AddOtherWords(Panel words, Notice other, DateTimeOffset now)
    {
        var otherWords = NoticeCodes.Words(other, Settings.NoticeCodes);
        if (otherWords.Body.Length == 0) return;
        var line = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var when = UI.Fixed(NoticeText.Relative(other.ReceivedAt, now), 11, 20, UI.Tertiary);
        DockPanel.SetDock(when, System.Windows.Controls.Dock.Right); when.Margin = new Thickness(8, 0, 0, 0); when.VerticalAlignment = VerticalAlignment.Top;
        line.Children.Add(when);
        var text = SelectableWords(otherWords.Body, 13, 20, UI.Secondary, line: false);
        if (otherWords.HasCode) AutomationProperties.SetName(text, otherWords.SpokenBody);
        line.Children.Add(text);
        words.Children.Add(line);
    }

    // Words of the focus that the pointer can select (PanelSelectableText), drawn as the text they replace. line: one line of the
    // given height, cut at the column's edge (TrimmedLine gives it an ellipsis; a drag along it brings the rest into view); otherwise
    // wrapped. A press on them gives the panel the keyboard, so Ctrl+C copies the selection.
    private TextBox SelectableWords(string text, double size, double lineHeight, string color, bool line, FontWeight? weight = null)
    {
        var box = new TextBox
        {
            Style = (Style)FindResource("PanelSelectableText"), Text = text, FontFamily = UI.PanelFont, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            Foreground = UI.Brush(color), TextWrapping = line ? TextWrapping.NoWrap : TextWrapping.Wrap,
            FlowDirection = NoticeText.RightToLeft(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };
        TextBlock.SetLineHeight(box, lineHeight); TextBlock.SetLineStackingStrategy(box, LineStackingStrategy.BlockLineHeight);
        if (line) { box.Height = lineHeight; box.VerticalContentAlignment = VerticalAlignment.Center; }
        box.PreviewMouseLeftButtonDown += (_, _) => TakeKeyboardForWords();
        WatchWords(box);
        return box;
    }

    // Selectable words of the focus column. Each text box keeps its selection, and shows it after losing the keyboard
    // (PanelSelectableText), so a left press anywhere on the panel other than in a box clears the selections of every other box,
    // as a press beside selected text does in a browser.
    private void WatchWords(TextBox box)
    {
        _focusWords.Add(box);
        if (_wordsPressWatched) return;
        _wordsPressWatched = true;
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            var pressed = WordsBox(e.OriginalSource);
            foreach (var other in _focusWords) if (!ReferenceEquals(other, pressed) && other.SelectionLength > 0) other.Select(0, 0);
        };
    }
    private bool _wordsPressWatched;

    // Below this width (DIP) the hero shows its cover at 40 instead of 56.
    private const double HeroNarrow = 260;

    // A line of SelectableWords that ends in "…" where the column cuts it. A text box has no ellipsis, so while its words are cut
    // and nothing in them is selected or pressed, the same words in a TextBlock that trims them are shown in its place. The box
    // stays under the pointer (it is only made transparent), so a press shows it again, and a drag selects and brings the rest into view.
    private static Grid TrimmedLine(TextBox box)
    {
        var shown = new TextBlock
        {
            Text = box.Text, FontFamily = box.FontFamily, FontSize = box.FontSize, FontWeight = box.FontWeight, Foreground = box.Foreground, FlowDirection = box.FlowDirection,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top,
            LineHeight = TextBlock.GetLineHeight(box), LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Opacity = 0
        };
        // The trimmed words are laid out again only while they are cut (FitLineBox), not at every width of a drag.
        var fit = new FitLineBox { Child = shown, IsHitTestVisible = false };
        var host = new Grid { Margin = box.Margin, Children = { box, fit } };
        box.Margin = new Thickness(0);
        bool pressed = false, placed = false;
        void Update()
        {
            // The trimmed words start where the box's first letter does, which a text box sets in from its edge by itself. Left to
            // right that place does not change with the width, so it is asked for once.
            if ((!placed || box.FlowDirection == FlowDirection.RightToLeft) && box.IsLoaded && box.Text.Length > 0
                && box.GetRectFromCharacterIndex(0) is { IsEmpty: false } first && double.IsFinite(first.Left) && double.IsFinite(first.Top))
            {
                var margin = new Thickness(first.Left, first.Top, first.Left, -first.Top);
                if (shown.Margin != margin) shown.Margin = margin;
                placed = true;
            }
            bool trimmed = !pressed && box.SelectionLength == 0 && !box.IsKeyboardFocusWithin && fit.Cut;
            if (trimmed && shown.Opacity == 0) box.ScrollToHome();
            shown.Opacity = trimmed ? 1 : 0;
            box.Opacity = trimmed ? 0 : 1;
        }
        host.SizeChanged += (_, _) => host.Dispatcher.BeginInvoke(Update, System.Windows.Threading.DispatcherPriority.Loaded);
        box.PreviewMouseLeftButtonDown += (_, _) => { pressed = true; Update(); };
        box.PreviewMouseLeftButtonUp += (_, _) => { pressed = false; Update(); };
        box.LostMouseCapture += (_, _) => { pressed = false; Update(); };
        box.SelectionChanged += (_, _) => { if (!pressed) Update(); };
        box.LostKeyboardFocus += (_, _) => Update();
        return host;
    }

    // The focus column's selectable words (SelectableWords), from the column built last.
    private readonly List<TextBox> _focusWords = [];

    // The panel opens without the keyboard (PanelKeyboard). A press on the words takes it, before the text box handles the press,
    // so the selection can be copied with Ctrl+C; the window that had it gets it back when the panel closes. Under mouse
    // pass-through without the keyboard (PanelTakesKeyboard) the panel keeps out of it, and the right-click menu still copies.
    private void TakeKeyboardForWords()
    {
        if (IsActive || !PanelTakesKeyboard) return;
        if (PanelKeys.Take()) _app.Shelf.TypingTarget = PanelKeys.Previous;
    }

    // The text box of the focus's words that holds an element the pointer is on, or null.
    private static TextBox? WordsBox(object? source)
    {
        for (var element = source as DependencyObject; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is TextBox box) return box;
        return null;
    }

    // Whether the page shows a notice as still there at its source: a test notice, and one whose source never says it is gone
    // (NoticeKeys.NeverClosed: a taskbar flash, a Feishu banner, a DingTalk message popup), are never "closed at the source".
    private static bool ShownPresent(Notice notice) => notice.SourcePresent || notice.Origin == NoticeOrigin.Test || NoticeKeys.NeverClosed(notice.InstanceKey);

    // The status line's words and its dot's color (null for no dot).
    private static (string Text, string? Color) StatusOf(Notice notice, NoticeCenter center)
    {
        if (notice.Call) return (Loc.T("来电", "Incoming call"), CallColor);
        if (WaitingStatus(notice) is { } waiting) return waiting;
        if (notice.Category == NoticeCategory.Error) return (NoticeText.CategoryName(notice.Category), PanelTheme.Hex("danger"));
        if (notice.Category == NoticeCategory.Warning) return (NoticeText.CategoryName(notice.Category), WaitColor);
        bool latest = center.Hub.Notices.Count > 0 && ReferenceEquals(center.Hub.Notices[0], notice);
        return (latest ? Loc.T("最新", "Latest") : Loc.T("通知", "Notice"), null);
    }

    private static FrameworkElement ProgressTrack(double progress, Color accent)
    {
        double done = Math.Clamp(progress, 0, 1);
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var label = UI.Fixed(Loc.T($"{done:P0}", $"{done:P0}"), 11, 16, UI.Tertiary); DockPanel.SetDock(label, System.Windows.Controls.Dock.Right); label.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(label);
        var track = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Center };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(done, GridUnitType.Star), MinWidth = 3 });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - done, GridUnitType.Star) });
        var rail = new Border { Background = UI.Brush(UI.Track), CornerRadius = new CornerRadius(1.5) }; Grid.SetColumnSpan(rail, 2);
        track.Children.Add(rail);
        track.Children.Add(new Border { Background = new SolidColorBrush(accent), CornerRadius = new CornerRadius(1.5) });
        row.Children.Add(track);
        AutomationProperties.SetName(row, Loc.T($"进度 {done:P0}", $"Progress {done:P0}"));
        return row;
    }

    // Delete, the notice's own and BrimDeck's actions, and opening the application as the main one on the right.
    private FrameworkElement FocusButtons(Notice notice, NoticeCenter center)
    {
        // Delete and the notice's own actions, then the main button, which stays at the right. Where they do not fit one line, "打开应用"
        // becomes a round icon like the others, then copy is left out and the buttons move closer; they wrap to a second line only
        // where even that does not fit (NoticeButtons).
        var row = new NoticeButtons { Margin = new Thickness(0, 12, 0, 0) };
        var flow = row.Children;
        // Delete takes the whole conversation the focus shows: left behind, its earlier messages would take the row again.
        string conversation = Conversation(notice);
        var members = center.Hub.Notices.Where(n => Conversation(n) == conversation).ToList();
        string deleteName = members.Count > 1 ? Loc.T($"删除这个会话的 {members.Count} 条记录", $"Delete this conversation's {members.Count} notices") : Loc.T("删除这条记录", "Delete this notice");
        var delete = PillButton("", deleteName, () => { foreach (var member in members) center.Delete(member); _noticeFocus = null; RenderNotices(); });
        ((Border)delete.Content).Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Foreground = UI.Brush(UI.Primary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ((Border)delete.Content).Padding = new Thickness(0); delete.Width = 32; delete.ToolTip = deleteName;
        flow.Add(delete);
        if (CopyText(notice, members) is { Length: > 0 } text)
        {
            string copyName = Loc.T("复制内容", "Copy text");
            Button? copyAll = null;
            copyAll = GlyphPill("", copyName, () => SayCopied(copyAll!, CopyWords(text), copyName));
            flow.Add(copyAll);
            row.Optional = copyAll;
        }
        foreach (var add in NoticeAddButtons(notice)) flow.Add(add);
        // The notice's own buttons that can still do something (NoticeCenter.Buttons): one that answers its sender while the sender
        // waits, one that copies or opens always. When no application can be opened, the page's main one is the sender's main button,
        // else its first that opens something ("打开邮箱" of a mail), which then turns into the open icon as "打开应用" does.
        var own = center.Buttons(notice).Take(NoticeIntake.MaxButtons).ToList();
        var mainAction = PageMainAction(notice, center);
        foreach (var action in own.OrderBy(action => ReferenceEquals(action, mainAction)))
        {
            bool main = ReferenceEquals(action, mainAction);
            if (AgendaReminders.Owns(notice)) { flow.Add(ReminderButton(notice, action, main)); if (main) row.MainLast = true; continue; }
            var button = PillButton(action.Label, action.Label, async () => { await center.RunAsync(notice, action); RenderNotices(); }, primary: main);
            flow.Add(button);
            if (!main) continue;
            row.MainLast = true;
            if (action.Kind == NoticeActionKind.Open) CompactToOpenIcon(row, button, action.Label);
        }
        foreach (var action in center.TimeActions(notice).Take(2))
            flow.Add(PillButton(action.Label, action.Label, () => { center.TimeAction(notice, action.Key); RenderNotices(); }));
        // A call that still rings is answered or declined here, with its window's own buttons, in place of opening the application.
        if (center.CanAnswer(notice))
        {
            flow.Add(AnswerButton(notice, center, CallAnswer.Decline));
            if (center.CanAnswerWithVoice(notice)) flow.Add(AnswerButton(notice, center, CallAnswer.Voice));
            flow.Add(AnswerButton(notice, center, CallAnswer.Accept));
            row.MainLast = true;
        }
        else if (center.CanOpen(notice))
        {
            string open = Loc.T("打开应用", "Open app");
            var main = PillButton(open, open, () => _ = OpenFromPage(notice), primary: true);
            row.MainLast = true;
            CompactToOpenIcon(row, main, open);
            flow.Add(main);
        }
        // The row sets the space between its buttons itself (NoticeButtons.Gap).
        foreach (var button in flow.OfType<FrameworkElement>()) button.Margin = new Thickness(0);
        return row;
    }

    // The sender's button that the page shows as its main one, when no application can be opened: its main button, else its first
    // that opens something.
    private static NoticeAction? PageMainAction(Notice notice, NoticeCenter center)
    {
        if (center.CanOpen(notice)) return null;
        var own = center.Buttons(notice).Take(NoticeIntake.MaxButtons).ToList();
        return own.FirstOrDefault(action => action.Primary) ?? own.FirstOrDefault(action => action.Kind == NoticeActionKind.Open);
    }

    // Lets the row turn its main button, one that opens something, into a round open icon where its words do not fit.
    private static void CompactToOpenIcon(NoticeButtons row, Button main, string name)
    {
        var face = (Border)main.Content;
        var words = face.Child;
        var icon = AgendaDraw.Icon(OpenIcon, 14, PanelTheme.Hex("surface"));
        icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Center;
        row.CompactMain = compact =>
        {
            face.Child = compact ? icon : words;
            face.Padding = compact ? new Thickness(0) : new Thickness(16, 0, 16, 0);
            main.Width = compact ? 32 : double.NaN;
            main.ToolTip = compact ? name : null;
        };
    }

    // A box with an arrow leaving it at the top right, in the 24 unit box of AgendaDraw.Icon: an opening main button ("打开应用",
    // "打开邮箱") where its words do not fit.
    private const string OpenIcon = "M14 4h6v6M20 4l-8.5 8.5M18 14v4.5a1.5 1.5 0 0 1-1.5 1.5h-11A1.5 1.5 0 0 1 4 18.5v-11A1.5 1.5 0 0 1 5.5 6H10";

    // A planner reminder's buttons on this panel: "查看日程" opens the planner here; "稍后提醒" offers when to remind again (5 or 10
    // minutes, or at the start while it is more than a minute away).
    private Button ReminderButton(Notice notice, NoticeAction action, bool main)
    {
        if (action.Key != AgendaReminders.SnoozeAction)
            return PillButton(action.Label, action.Label, () => { AgendaReminderAction(notice, action.Key); RenderNotices(); }, primary: main);
        Button? button = null;
        button = PillButton(Loc.T("稍后提醒", "Remind later"), Loc.T("稍后提醒", "Remind later"), () =>
        {
            var menu = NewMenu();
            void Later(TimeSpan delay, DateTime? at = null) { AgendaReminderAction(notice, action.Key, delay, at); RenderNotices(); }
            menu.Items.Add(Item(Loc.T("5 分钟后", "In 5 minutes"), () => Later(TimeSpan.FromMinutes(5))));
            menu.Items.Add(Item(Loc.T("10 分钟后", "In 10 minutes"), () => Later(TimeSpan.FromMinutes(10))));
            if (notice.Timing?.StartsAt is { } start && start - DateTimeOffset.Now > TimeSpan.FromMinutes(1))
                menu.Items.Add(Item(Loc.T("开始时", "At the start"), () => Later(TimeSpan.Zero, start.LocalDateTime)));
            OpenMenu(menu, button!);
        }, primary: main);
        return button;
    }

    // "挂断" (red), "语音接听" (where the call window offers to answer a video call with voice only) or "接听" (the main button) of
    // a ringing call. Pressed, each waits for the call's window; where its button did not take effect, the button says so for a
    // moment and the call is left to the application.
    private Button AnswerButton(Notice notice, NoticeCenter center, CallAnswer answer)
    {
        bool accept = answer != CallAnswer.Decline;
        string name = answer switch { CallAnswer.Decline => Loc.T("挂断", "Decline"), CallAnswer.Voice => Loc.T("语音接听", "Answer with voice"), _ => Loc.T("接听", "Answer") };
        Button? button = null;
        bool busy = false;
        button = PillButton(name, name, async () =>
        {
            if (busy) return;
            busy = true;
            bool done = await center.AnswerAsync(notice, answer);
            busy = false;
            if (done) { RenderNotices(); return; }
            string said = accept ? Loc.T($"未能接听，请在{notice.AppName}中操作", $"Could not answer; use {notice.AppName}") : Loc.T($"未能挂断，请在{notice.AppName}中操作", $"Could not decline; use {notice.AppName}");
            void Say(string words) { if (((Border)button!.Content).Child is InkCentredText label) label.Text.Text = words; AutomationProperties.SetName(button!, words); }
            Say(said);
            var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            back.Tick += (_, _) => { back.Stop(); Say(name); };
            back.Start();
        }, primary: answer == CallAnswer.Accept, fill: answer == CallAnswer.Decline ? PanelTheme.Hex("danger") : null);
        return button;
    }

    // A round button that shows only a glyph of the system's icon font.
    private static Button GlyphPill(string glyph, string name, Action click)
    {
        var button = PillButton("", name, click);
        var face = (Border)button.Content;
        face.Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Foreground = UI.Brush(UI.Primary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        face.Padding = new Thickness(0); button.Width = 32; button.ToolTip = name;
        return button;
    }

    // What "copy" puts on the clipboard: the notice's words as the page shows them (a verification code stays hidden; it has its
    // own button), and under them the other messages of the conversation, oldest first.
    private string CopyText(Notice notice, IReadOnlyList<Notice> group)
    {
        var lines = group.Where(n => n.Body.Length > 0).OrderBy(n => n.ReceivedAt).Select(n => NoticeCodes.Words(n, Settings.NoticeCodes).Body).ToList();
        if (lines.Count == 0) lines.Add(NoticeCodes.Words(notice, Settings.NoticeCodes).Title);
        return string.Join(Environment.NewLine, lines);
    }

    // True when the text is on the clipboard; another application may hold the clipboard for a moment.
    private static bool CopyWords(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true); return true; }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(30); }
        }
        return false;
    }

    // Says on the button whether the copy worked, for the eye and for a screen reader, then goes back to its name.
    private static void SayCopied(Button button, bool done, string name)
    {
        string said = done ? Loc.T("已复制", "Copied") : Loc.T("复制失败，请再点一次", "Copy failed; click again");
        button.ToolTip = said; AutomationProperties.SetName(button, said);
        if (((Border)button.Content).Child is TextBlock glyph) glyph.Text = done ? "" : "";
        var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        back.Tick += (_, _) => { back.Stop(); button.ToolTip = name; AutomationProperties.SetName(button, name); if (((Border)button.Content).Child is TextBlock block) block.Text = ""; };
        back.Start();
    }

    // Copies the code on the user's click and says so on the button, for the eye and for a screen reader, then goes back to its name.
    private static void CopyCode(Button button, string text, string label)
    {
        bool done = NoticeCodeClipboard.Copy(text);
        string said = done ? Loc.T($"已复制，{CodeClipboardGuard.ClearAfter.TotalSeconds:0} 秒后清除", $"Copied; cleared in {CodeClipboardGuard.ClearAfter.TotalSeconds:0} s") : Loc.T("复制失败，请再点一次", "Copy failed; click again");
        void Say(string words) { if (((Border)button.Content).Child is TextBlock block) block.Text = words; AutomationProperties.SetName(button, words); }
        Say(said);
        var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        back.Tick += (_, _) => { back.Stop(); Say(label); };
        back.Start();
    }

    // A button of the page in its states: paint(over, down) is called as the pointer enters, leaves, presses and releases, so that
    // each state takes its own color from the theme (hover, pressed, selected) instead of one fill for all of them.
    private static void Feel(Button button, Action<bool, bool> paint)
    {
        bool over = false, down = false;
        button.MouseEnter += (_, _) => paint(over = true, down);
        button.MouseLeave += (_, _) => paint(over = false, down = false);
        button.PreviewMouseLeftButtonDown += (_, _) => paint(over, down = true);
        button.PreviewMouseLeftButtonUp += (_, _) => paint(over, down = false);
    }

    // A capsule button of the design: 32 high, the main one in the text color with the surface's color on it. Its words are centred
    // by their letters as drawn and on whole device pixels (AgendaDraw.Label), as the planner's buttons are.
    // fill: a color of its own (the red of "挂断"), with the words in the panel's surface color as on the main button.
    private static Button PillButton(string text, string name, Action click, bool primary = false, string? fill = null)
    {
        string rest = fill ?? (primary ? UI.Primary : UI.Fill);
        bool solid = primary || fill is not null;
        var face = new Border { CornerRadius = new CornerRadius(PanelTheme.Corner(16)), Background = UI.Brush(rest), Padding = new Thickness(16, 0, 16, 0), Height = 32 };
        var label = AgendaDraw.Label(text, 12.5, solid ? PanelTheme.Hex("surface") : UI.Primary, FontWeights.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        face.Child = label;
        label.FitHeight(face, 32);
        var button = new Button { Content = face, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0), FocusVisualStyle = (Style)Application.Current.FindResource("PanelPillKeyboardFocus"), Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) } };
        if (primary) button.Margin = new Thickness(0);
        // Under the pointer the main button dims a little, the others take the selected fill; pressed, the main one dims more and
        // the others take the pressed fill.
        Feel(button, (over, down) =>
        {
            if (solid) face.Opacity = down ? .76 : over ? .88 : 1;
            else face.Background = UI.Brush(down ? PanelTheme.Hex("pressed") : over ? UI.FillSelected : rest);
        });
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, name);
        return button;
    }

    // ----- The list column -----

    // Rows built at once when the list is drawn, and rows added per background pass afterwards (a long list built whole took the
    // page's first frame well past a frame). A page built again, or released, drops what is left.
    private const int InitialNoticeRows = 8, NoticeBatchSize = 3;
    private RowBatches<Notice>? _noticeBatches;

    private FrameworkElement ListColumn(NoticeCenter center, Notice focus, DateTimeOffset now, string? refocus, bool restoreScroll)
    {
        var column = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var border = new Border { BorderBrush = UI.Brush(UI.Hairline), BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(14, 0, 0, 0), Child = column };
        // The title, its count and the tools on one line; when the column is too narrow for that the count goes under them (NoticeHead).
        var head = new NoticeHead();
        DockPanel.SetDock(head, System.Windows.Controls.Dock.Top); column.Children.Add(head);
        head.Children.Add(UI.Fixed(Loc.T("通知", "Notices"), 15, 20, UI.Primary, FontWeights.SemiBold));
        int today = center.Hub.Notices.Count(Today);
        string count = Loc.T($"今天 {today} 条", $"{today} today");
        if (center.Paused && center.Settings.NoticesPausedUntil is { } until) count += Loc.T($" · 暂停至 {until.ToLocalTime():HH:mm}", $" · paused until {until.ToLocalTime():HH:mm}");
        else if (center.Quiet) count += Loc.T(" · 安静中", " · quiet");
        if (_noticeSyncSaid is { } said) count = said;
        var countText = UI.Centered(count, 11.5, UI.Tertiary);
        countText.TextWrapping = TextWrapping.Wrap; countText.LineHeight = NoticeLines.CountLine; countText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        head.Children.Add(countText);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (center.WindowsState != NoticeSourceState.Off)
            tools.Children.Add(IconToggle("", Loc.T("同步 Windows 通知", "Sync Windows notifications"), () => _ = SyncWindowsNotices()));
        var pause = IconToggle(center.Paused ? "" : "", center.Paused ? Loc.T("恢复提醒", "Resume") : Loc.T("暂停提醒 1 小时", "Pause for 1 h"),
            () => { center.Pause(center.Paused ? null : TimeSpan.FromHours(1)); RenderNotices(); });
        tools.Children.Add(pause);
        tools.Children.Add(FilterSwitch());
        head.Children.Add(tools);

        // The rows out of view are not laid out again for every width of a drag (ViewportStack).
        var list = new ViewportStack { Margin = new Thickness(0, 0, 0, FadeHeight) };
        KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Contained);
        var listed = Listed(center);
        // Keep the full extent when restoring a scroll offset. Build through the keyboard row
        // before requesting focus, even when that row is below the initial batch.
        int keyboardIndex = refocus is null ? -1 : listed.FindIndex(notice => notice.Id == refocus);
        int first = restoreScroll ? listed.Count : Math.Min(listed.Count, Math.Max(InitialNoticeRows, keyboardIndex + 1));
        // A later batch can bring the page's first live row: WatchPageLive after each, or nothing would follow the tick.
        _noticeBatches = new RowBatches<Notice>(listed, (i, notice) => AddNoticeRow(list, i, notice, focus, i < first ? now : DateTimeOffset.UtcNow),
            action => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, action), NoticeBatchSize, WatchPageLive);
        _noticeBatches.Start(first);
        if (listed.Count == 0)
        {
            var none = UI.Fixed(_noticeUnread ? Loc.T("没有未读的通知", "No unread notices") : Loc.T("今天还没有通知", "No notices today"), 12, 18, UI.Tertiary);
            none.Margin = new Thickness(8, 6, 0, 0); list.Children.Add(none);
        }
        if (!_noticeEarlier && center.Hub.Notices.Any(n => !Today(n)))
        {
            var more = PageButton(Loc.T("查看更早的通知", "Show earlier notices"), () => { _noticeEarlier = true; RenderNotices(); });
            more.HorizontalAlignment = HorizontalAlignment.Center; more.Margin = new Thickness(0, 6, 0, 12);
            list.Children.Add(more);
        }
        // The rows' fill reaches 8 beyond the title on the left, so that their icons line up with it. The scroll viewer itself reaches
        // out, not the list in it: the viewer clips what lies beyond its bounds, and cut the rows' rounded left edge flush to their icons.
        // The space under the header lies outside the viewer too: inside it, the space scrolled away with the rows, and a scrolled
        // list was cut flush to the header.
        var scroll = new ScrollViewer { Content = list, Margin = new Thickness(-8, NoticeListGap, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
        _pageScroll = scroll;
        // The list fades out at its foot into the panel, by a mask that is the same on every material (NoticeFade); the free
        // space under the last row is as high as the fade.
        NoticeFade.Foot(scroll, FadeHeight);
        column.Children.Add(scroll);
        return border;
    }

    private void AddNoticeRow(Panel list, int index, Notice notice, Notice focus, DateTimeOffset now)
    {
        var row = Row(notice, now, Conversation(notice) == Conversation(focus));
        list.Children.Insert(index, row);
        _pageRows.Add((notice, notice.Revision, RowKey(notice, now), row));
    }

    // "全部"/"未读": the panel's segmented switch (AgendaDraw.Segments), as on the planner page. One fill on the track marks the
    // chosen half and glides to the other on a choice; the other half takes no fill under the pointer, only its words brighten.
    private Border FilterSwitch()
    {
        string[] names = [Loc.T("全部", "All"), Loc.T("未读", "Unread")];
        var segments = new List<Button>();
        for (int i = 0; i < names.Length; i++)
        {
            bool on = (i == 1) == _noticeUnread;
            var words = AgendaDraw.Label(names[i], 11, on ? Look.Primary : Look.Secondary);
            Action choose = i == 1 ? ShowUnreadOnly : () => { _noticeUnread = false; RenderNotices(); };
            var button = AgendaDraw.Segment(Look, words, names[i], on, choose, bright => words.Text.Foreground = UI.Brush(bright ? Look.Primary : Look.Secondary),
                FilterHeight, new Thickness(9, 0, 9, 0));
            AgendaDraw.Fit(button, words, FilterHeight, names);
            if (on) AutomationProperties.SetItemStatus(button, Loc.T("已选中", "Selected"));
            segments.Add(button);
        }
        int chosen = _noticeUnread ? 1 : 0;
        int? from = _filterDrawn is { } drawn && drawn != _noticeUnread ? (drawn ? 1 : 0) : null;
        _filterDrawn = _noticeUnread;
        var group = AgendaDraw.Segments(Look, segments, chosen, from, Animates);
        group.Margin = new Thickness(6, 0, 0, 0);
        return group;
    }
    private const double FilterHeight = 18;
    // The filter the switch was last drawn for: drawn for the other, its fill glides from it.
    private bool? _filterDrawn;

    private static Button IconToggle(string glyph, string name, Action click)
    {
        var face = new Border { CornerRadius = new CornerRadius(6), Width = 24, Height = 20, Background = UI.Brush("Transparent") };
        face.Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = UI.Brush(UI.Secondary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = face, Cursor = Cursors.Hand, ToolTip = name, FocusVisualStyle = (Style)Application.Current.FindResource("PanelKeyboardFocus"), Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) } };
        Feel(button, (over, down) => face.Background = UI.Brush(down ? PanelTheme.Hex("pressed") : over ? PanelTheme.Hex("hover") : "Transparent"));
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, name);
        return button;
    }

    private double EmptyWidth => Math.Max(200, ContentCanvas.Width - 60);

    // No notices at all: what the page is for, whether notices are being received, and the way to the settings.
    private FrameworkElement Empty(NoticeCenter center)
    {
        var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = EmptyWidth, Margin = new Thickness(0, 0, 0, 14) };
        var bell = new Grid { Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Center };
        bell.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = UI.Brush(UI.Fill) });
        // The notices tab's bell (MainWindow.xaml, NoticesBell), drawn at 20 DIP.
        var glyph = new Viewbox
        {
            Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Child = new Grid { Width = 16, Height = 16, Children = { new Path { Data = NoticesBell.Data, Stretch = Stretch.None, Stroke = UI.Brush(UI.Secondary), StrokeThickness = 1 } } }
        };
        bell.Children.Add(glyph);
        empty.Children.Add(bell);
        var title = UI.Fixed(Loc.T("今天还没有通知", "No notices today"), 19, 26, UI.Primary, FontWeights.SemiBold); title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 16, 0, 0);
        empty.Children.Add(title);
        string where = Collapsed.Style switch { CompactStyle.Notch => Loc.T("刘海", "the notch"), CompactStyle.Capsule => Loc.T("胶囊", "the capsule"), _ => Loc.T("指示条", "the indicator") };
        var hint = UI.Line(Loc.T($"新通知到达时{where}会闪动，鼠标移上去即可在这里查看", $"When a notice arrives, {where} flashes; rest the pointer on it to see it here"), 12.5, UI.Tertiary);
        hint.TextWrapping = TextWrapping.Wrap; hint.TextAlignment = TextAlignment.Center; hint.Margin = new Thickness(0, 5, 0, 0);
        empty.Children.Add(hint);
        var (state, color) = center.WindowsState switch
        {
            NoticeSourceState.NeedsAccess => (Loc.T("尚未允许读取 Windows 通知", "Access to Windows notifications is not allowed yet"), PanelTheme.Hex("danger")),
            NoticeSourceState.Denied => (Loc.T("Windows 已拒绝通知访问，可在系统隐私设置中开启", "Windows denies access to notifications; it can be allowed in Windows' privacy settings"), PanelTheme.Hex("danger")),
            NoticeSourceState.Off => (Loc.T("未接收 Windows 应用通知", "Not receiving Windows notifications"), UI.Tertiary),
            _ => (Loc.T("正在接收 Windows 应用通知", "Receiving Windows notifications"), CallColor)
        };
        if (_noticeSyncSaid is { } said) state = said;
        var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        status.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(color), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        status.Children.Add(UI.Fixed(state, 11.5, 16, UI.Tertiary));
        empty.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        if (center.WindowsState != NoticeSourceState.Off)
            buttons.Children.Add(PillButton(Loc.T("同步 Windows 通知", "Sync Windows notifications"), Loc.T("同步 Windows 通知", "Sync Windows notifications"), () => _ = SyncWindowsNotices()));
        buttons.Children.Add(PillButton(Loc.T("通知设置", "Notice settings"), Loc.T("通知设置", "Notice settings"), () => _app.OpenSettingsPage(SettingsWindow.NoticesSettingsPage)));
        empty.Children.Add(buttons);
        return empty;
    }

    // The most characters of a row's preview: more than the widest list fits at its narrowest letters (about 3 DIP at 12).
    private const int PreviewLength = 160;

    // A row of the list: the icon with a dot while unread, the title and when, and a line of preview.
    private FrameworkElement Row(Notice notice, DateTimeOffset now, bool selected)
    {
        var row = new Border { CornerRadius = new CornerRadius(PanelTheme.Corner(10)), Padding = new Thickness(8, 7, 8, 7), Background = UI.Brush(selected ? UI.FillSelected : "Transparent"), Cursor = Cursors.Hand };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        row.Child = grid;
        var icon = new Grid { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        icon.Children.Add(NoticePlate(notice, 24));
        // The unread dot has a ring in the color of what is behind it, which is the row's own fill (it changes with the row's state).
        Ellipse? dot = null;
        var (count, fresh) = ConversationOf(notice);
        if (fresh)
            icon.Children.Add(dot = new Ellipse { Width = 9, Height = 9, Fill = UI.Brush(notice.Call ? CallColor : UnreadColor), StrokeThickness = 2,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -3, -3, 0), IsHitTestVisible = false });
        grid.Children.Add(icon);
        // The title, the time and a line of preview; the time moves under a title it would leave too short (NoticeRowTexts).
        var texts = new NoticeRowTexts { Opacity = ShownPresent(notice) ? 1 : .78 };
        Grid.SetColumn(texts, 1); grid.Children.Add(texts);
        var codeWords = NoticeCodes.Words(notice, Settings.NoticeCodes);
        var title = IslandNotice.Directed(UI.Fixed(codeWords.Title.Length > 0 ? codeWords.Title : notice.AppName, 12.5, NoticeLines.RowTitleLine, UI.Primary, FontWeights.SemiBold));
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        if (codeWords.HasCode && notice.Title.Length > 0) AutomationProperties.SetName(title, codeWords.SpokenTitle);
        texts.Children.Add(title);
        // A conversation's row says how many notices it stands for, before the time of its newest.
        var when = UI.Fixed((count > 1 ? Loc.T($"{count} 条 · ", $"{count} · ") : "") + NoticeText.Relative(notice.ReceivedAt, now), 11, NoticeLines.RowTitleLine, UI.Tertiary);
        texts.Children.Add(when);
        string preview = notice.Body.Length > 0 ? NoticeText.Summary(codeWords.Body) : notice.AppName;
        // A cut line is laid out whole at every width of a drag; more than a row can show only costs time.
        if (preview.Length > PreviewLength) preview = preview[..(char.IsHighSurrogate(preview[PreviewLength - 1]) ? PreviewLength - 1 : PreviewLength)];
        var second = new TextBlock { FontFamily = UI.PanelFont, FontSize = 12, LineHeight = NoticeLines.RowPreviewLine, Foreground = UI.Brush(UI.Secondary), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        var (status, color) = notice.Call ? (Loc.T("来电", "Call"), CallColor) : notice.Category == NoticeCategory.Error ? (NoticeText.CategoryName(notice.Category), PanelTheme.Hex("danger"))
            : notice.Category == NoticeCategory.Warning ? (NoticeText.CategoryName(notice.Category), WaitColor) : ("", "");
        if (status.Length > 0) second.Inlines.Add(new System.Windows.Documents.Run(status + " · ") { Foreground = UI.Brush(color) });
        if (NoticeClock.Live(notice.Timing, now, TimeZoneInfo.Local) is { } live) second.Inlines.Add(new System.Windows.Documents.Run(live + " · ") { Foreground = UI.Brush(UI.Accent) });
        AddNoticeAgendaMark(second, notice, now);
        second.Inlines.Add(new System.Windows.Documents.Run(preview));
        IslandNotice.Directed(second);
        if (codeWords.HasCode && notice.Body.Length > 0) AutomationProperties.SetName(second, (status.Length > 0 ? status + ", " : "") + NoticeText.Summary(codeWords.SpokenBody));
        texts.Children.Add(second);
        // The row's states: the chosen one has the selected fill, one under the pointer the hover fill, one being pressed the pressed
        // fill. Its time is drawn in the secondary text on any of them (the tertiary text is not held to 4.5:1 on those fills).
        bool over = false, down = false;
        void Paint()
        {
            string? fill = down ? "pressed" : selected ? "fillSelected" : over ? "hover" : null;
            row.Background = UI.Brush(fill is null ? "Transparent" : PanelTheme.Hex(fill));
            when.Foreground = UI.Brush(fill is null ? UI.Tertiary : UI.Secondary);
            dot?.Stroke = UI.Brush(RowGround(fill));
        }
        Paint();
        row.MouseEnter += (_, _) => { over = true; Paint(); };
        row.MouseLeave += (_, _) => { over = false; down = false; Paint(); };
        row.PreviewMouseLeftButtonDown += (_, _) => { down = true; Paint(); };
        row.MouseLeftButtonUp += (_, e) => { _noticeFocus = notice.Id; RenderNotices(); e.Handled = true; };
        // From the keyboard, once a hotkey gave the panel the keyboard (MainWindow.Hotkeys.cs): Enter or Space puts the notice in front.
        row.Focusable = true;
        row.FocusVisualStyle = (Style)Application.Current.FindResource("PanelRowKeyboardFocus");
        row.GotKeyboardFocus += (_, e) => { if (e.NewFocus == row) _focusedNotice = notice.Id; };
        row.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space) || e.OriginalSource != row) return;
            _noticeFocus = notice.Id; RenderNotices(); e.Handled = true;
        };
        AutomationProperties.SetName(row, notice.AppName + ", " + codeWords.SpokenTitle + ", " + NoticeText.Relative(notice.ReceivedAt, now));
        return row;
    }

    // What a row's fill looks like over the panel, as an opaque color: the color behind the unread dot's ring.
    private static string RowGround(string? fill)
    {
        var surface = PanelTheme.Current.Surface;
        return (fill is null ? surface : PanelTheme.Current[fill].Over(surface)).ToString();
    }

    // Everything a row shows.
    private string RowKey(Notice notice, DateTimeOffset now)
        => string.Join('\u0001', Settings.NoticeCodes, notice.Title, notice.Body, notice.AppName, notice.IconRef, notice.Picture, notice.SourcePresent, notice.Category, notice.Call,
            NoticeClock.Live(notice.Timing, now, TimeZoneInfo.Local), NoticeText.Relative(notice.ReceivedAt, now), ConversationOf(notice), NoticeAgendaRowKey(notice, now));

    // The notice whose row last had the keyboard focus, to keep it there when the page is built again.
    private string? _focusedNotice;

    private async Task OpenFromPage(Notice notice)
    {
        if (await _app.Notices.OpenAsync(notice)) { SetExpanded(false); return; }
        MessageHint(Loc.T("无法打开该应用；它可能不支持从通知启动。", "The application could not be opened; it may not support being started from a notice."));
    }

    private void MessageHint(string text)
    {
        if (NoticesContent.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<DockPanel>().FirstOrDefault() is not { } column) return;
        var hint = UI.Line(text, 11, PanelTheme.Hex("danger")); hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 6, 0, 0);
        DockPanel.SetDock(hint, System.Windows.Controls.Dock.Bottom);
        column.Children.Insert(0, hint);
    }

    // Plates on the notices page still waiting for their icon. An icon that arrives fills only its own plates: rebuilding the
    // page for it would ask again for icons the cache let go of, whose arrival would rebuild it again.
    private readonly Dictionary<string, List<(Grid Plate, Notice Notice)>> _waitingPlates = new(StringComparer.Ordinal);

    // The focus column's glow and its notice: an avatar or icon that arrives after the page was drawn gives the glow its color.
    private (Border Glow, Notice Notice)? _focusGlow;

    private void PaintGlow(Border glow, Notice notice)
    {
        var color = PanelTheme.Readable(_app.Notices.Icons.GlowFor(notice, Settings.NoticeRule(notice.AppKey), NoticeLetter.ShowsPicture(notice, Settings)) ?? PanelTheme.ColorOf("textSecondary"));
        glow.Background = new RadialGradientBrush(Color.FromArgb(NoticeLines.GlowAlpha, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B));
    }

    private void NoticeIconLoaded(string iconRef)
    {
        NoticeStripIconLoaded(iconRef);
        if (_focusGlow is var (glow, focused) && (iconRef == focused.Picture || iconRef == focused.IconRef)) PaintGlow(glow, focused);
        if (!_waitingPlates.Remove(iconRef, out var plates)) return;
        foreach (var (plate, notice) in plates) FillPlate(plate, notice);
    }

    // The application's icon, or its letter until the icon arrives.
    private FrameworkElement NoticePlate(Notice notice, double size)
    {
        var plate = new Grid { Width = size, Height = size };
        if (!FillPlate(plate, notice))
            foreach (var reference in new[] { notice.IconRef, NoticeLetter.ShowsPicture(notice, Settings) ? notice.Picture : null })
            {
                if (reference is null) continue;
                if (!_waitingPlates.TryGetValue(reference, out var plates)) _waitingPlates[reference] = plates = [];
                plates.Add((plate, notice));
            }
        return plate;
    }

    // True when the plate shows the icon; false when it shows the initial (the icon may still arrive). The focus's cover (56) holds
    // the icon on a faint plate, as the music page holds its cover; the list's rows show the icon itself.
    private bool FillPlate(Grid plate, Notice notice)
    {
        double size = plate.Width;
        bool cover = size >= 48;
        plate.Children.Clear();
        // A contact's avatar, round, as Windows shows it; on the focus's cover the application's icon sits on its corner.
        if (NoticeLetter.ShowsPicture(notice, Settings) && _app.Notices.Icons.Get(notice.Picture, out _) is { } avatar)
        {
            plate.Children.Add(NoticeLetter.Round(avatar, size));
            if (cover && _app.Notices.Icons.Get(notice.IconRef, out _) is { } logo)
            {
                var small = new Image { Source = logo, Stretch = Stretch.Uniform, Width = 20, Height = 20 };
                // A 128 pixel icon drawn at 20 DIPs is jagged and blurred unless WPF scales it down with its best filter.
                RenderOptions.SetBitmapScalingMode(small, BitmapScalingMode.HighQuality);
                var badge = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(PanelTheme.ColorOf("surface")), Padding = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -4, -4), Child = small };
                plate.Children.Add(badge);
            }
            return true;
        }
        var image = _app.Notices.Icons.Get(notice.IconRef, out _);
        if (image is null)
        {
            plate.Children.Add(NoticeLetter.Plate(notice.AppName, size, PanelTheme.Readable(_app.Notices.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey))), iconRef: notice.IconRef));
            return false;
        }
        // The icon itself at the plate's size, with nothing behind it: on a plate of its own it read as an icon boxed into another.
        var picture = new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        plate.Children.Add(picture);
        return true;
    }

    // A small flat button of the panel's kind.
    private static Button PageButton(string text, Action click)
    {
        var face = new Border { CornerRadius = new CornerRadius(6), Background = UI.Brush(UI.Fill), Padding = new Thickness(9, 0, 9, 0), Height = 22, Margin = new Thickness(4, 0, 0, 0) };
        face.Child = UI.InkCentered(text, 11.5, 22, UI.Secondary);
        var button = new Button { Content = face, Cursor = Cursors.Hand, FocusVisualStyle = (Style)Application.Current.FindResource("PanelKeyboardFocus"), Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) } };
        Feel(button, (over, down) => face.Background = UI.Brush(down ? PanelTheme.Hex("pressed") : over ? UI.FillSelected : UI.Fill));
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, text);
        return button;
    }
}
