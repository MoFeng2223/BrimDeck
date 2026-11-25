using System.Text.Json.Serialization;

namespace BrimDeck.Core.Notices;

// Where a notice came from: the Windows notification center, a WSL desktop notification, brim-notify (from Windows or WSL),
// BrimDeck's own test notice, or one of BrimDeck's own features (Internal: a track change, a quota alert), which hand their
// notices over inside the process (NoticeCenter.Post).
public enum NoticeOrigin { Windows, Wsl, Cli, Test, Internal }
// Shown as a small status mark beside the application's icon; never as the application's color.
public enum NoticeCategory { Message, Task, Success, Warning, Error, Info }
public enum NoticePriority { Low, Normal, High }
// How strongly a notice shows on the collapsed island: the three levels of the settings, and Silent (only in the recent notices).
// Plain is level 1 (文字): the island keeps still and only shows the words. Glow is level 2 (闪动): it flashes as well, with the
// backlight alone or with the liquid as NoticeStyle.Liquid says. Persistent is level 3 (常驻): as level 2, and it stays until it is
// handled, playing again every few seconds. Lively is an older name of level 2 that a sender may still use; for an item without a
// style (a quota alert) it plays the liquid. Auto: not chosen; NoticeLevels.Resolve decides.
public enum NoticeLevel { Auto, Silent, Plain, Glow, Lively, Persistent }
// Where a sender asks its plain notice to show (NoticeMessage.Slot): the whole island, or the place of one part of the collapsed
// content. Not given, the whole island.
public enum NoticeSlot { Island, Usage, Music, Monitor, Clock, Transfer, Devices }
// Where the icon and the words sit in the island when they fit; longer words start at the left and scroll.
public enum NoticeAlign { Left, Center, Right }

// What BrimDeck can do with a notice's source, by what the source offers. Nothing is shown that cannot be done.
[Flags]
public enum NoticeCapabilities
{
    None = 0,
    // The source application can be started or brought forward.
    OpenApp = 1,
    // The notice has actions BrimDeck can pass back to the source (a WSL notice while the agent is the notification service).
    Actions = 2,
    // The source can be told that the user dismissed the notice.
    ReportDismissal = 4
}

// What a notice's button does. Reply: its key goes back to the source (a WSL application, or a sender that waits). Copy: Value
// goes to the clipboard. Open: Value is opened (a web address, a local file, "app:<application id>"). BrimDeck runs nothing.
public enum NoticeActionKind { Reply, Copy, Open }

public sealed record NoticeAction(string Key, string Label, NoticeActionKind Kind = NoticeActionKind.Reply, string? Value = null, bool Primary = false);

// A notice as a source delivers it, before BrimDeck's rules. AppKey identifies the application across notices
// ("windows:<AUMID>", "windows:<browser AUMID>|<site>" for a site's notice in a browser, "wsl:<distro>:<name>", "cli:<name>"). InstanceKey identifies this notice at its source; an existing
// notice with the same InstanceKey, or with the same AppKey and ReplaceKey, is updated instead of added. Replaces names the
// InstanceKey of a notice this one took the place of at its source (see NoticeReplacements); that notice is updated too.
public sealed record NoticeInput
{
    public required NoticeOrigin Origin { get; init; }
    public required string AppKey { get; init; }
    public required string AppName { get; init; }
    public required string InstanceKey { get; init; }
    public string AppId { get; init; } = "";
    public string? Distro { get; init; }
    public string? ReplaceKey { get; init; }
    public string? Replaces { get; init; }
    public string? EventId { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string? IconRef { get; init; }
    // A picture of who or what the notice is from (a contact's or a group's avatar), shown in place of the icon; same forms as IconRef.
    public string? Picture { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    // How long the message had waited before it was sent, by the sender's clock (NoticeMessage.AgeMs); null when not told.
    public TimeSpan? Age { get; init; }
    public NoticeCategory Category { get; init; } = NoticeCategory.Message;
    public NoticePriority PriorityHint { get; init; } = NoticePriority.Normal;
    public double? Progress { get; init; }
    public IReadOnlyList<NoticeAction> Actions { get; init; } = [];
    public NoticeCapabilities Capabilities { get; init; }
    // Set for the result of "brim-notify run"; the hub words the title and body from them when the sender gave none.
    public int? ExitCode { get; init; }
    public long? DurationMs { get; init; }
    public string? Command { get; init; }
    // A time-kind notice's times (NoticeTime.cs); null for every other notice.
    public NoticeTiming? Timing { get; init; }
    // Ends the timing of the earlier notice with the same AppKey and ReplaceKey (brim-notify stop) instead of adding one.
    public bool StopTiming { get; init; }
    // A stop or a removal that names the notice by BrimDeck's own id (NoticeMessage.NoticeId) instead of by ReplaceKey.
    public string? TargetId { get; init; }
    // Arrived while BrimDeck could not read its source (the PC slept): recorded, unviewed, but not shown as it arrives (core-M5).
    public bool Missed { get; init; }
    // Handed over already as it arrived (WindowsNoticeList.Observe) and sent again only so the hub merges the notice it took the
    // place of (Replaces): it never adds a notice, and one the user deleted or cleared meanwhile stays gone. Set by
    // WindowsNoticeList; never taken from a sender.
    public bool Redelivery { get; init; }
    // An incoming call, as BrimDeck reads a Windows notification (WindowsNoticeApps.ReadsAsCall); never taken from a sender.
    public bool Call { get; init; }
    // A desktop notification its application marked "transient" (NoticeMessage.Transient): shown and listed, never saved.
    public bool Transient { get; init; }
    // The level and the alignment the sender asked for (NoticeMessage.Level, Align); Auto and null leave them to BrimDeck.
    public NoticeLevel Level { get; init; }
    public NoticeAlign? Align { get; init; }
    // Where a plain notice shows (NoticeMessage.Slot); null is the whole island.
    public NoticeSlot? Slot { get; init; }
    // "#RRGGBB" for the backlight of a notice that stays until handled; null takes the category's or the setting's.
    public string? Glow { get; init; }
    // A second line beside the source's name.
    public string Context { get; init; } = "";
    public bool HideIcon { get; init; }
    // Shown on the island only: not listed among the recent notices and never saved.
    public bool Unlisted { get; init; }
    // Its sender waits for how it ends (NoticeMessage.Wait): it stays until handled, and its Reply buttons answer the sender.
    public bool Awaited { get; init; }
    // How long it may stay until handled; null takes AttentionCoordinator.PersistentLimit.
    public TimeSpan? Timeout { get; init; }
    // Questions its sender waits to have answered (NoticeMessage.Questions); empty for every other notice.
    public IReadOnlyList<BrimDeck.Protocol.NoticeQuestion> Questions { get; init; } = [];
    // A command or a path it is about (NoticeMessage.Code), shown as code.
    public string Code { get; init; } = "";
}

// A notice BrimDeck keeps. Id is BrimDeck's own; Revision counts updates, so a view can tell a changed notice apart.
public sealed class Notice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string InstanceKey { get; set; } = "";
    public string AppKey { get; set; } = "";
    public string AppName { get; set; } = "";
    public string AppId { get; set; } = "";
    public NoticeOrigin Origin { get; set; }
    public string? Distro { get; set; }
    public string? ReplaceKey { get; set; }
    // The sender's id of the message that last added or changed the notice. Kept with the notice, so that a retry is
    // recognized after a restart too.
    public string? EventId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? IconRef { get; set; }
    public string? Picture { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public NoticeCategory Category { get; set; }
    public NoticePriority Priority { get; set; } = NoticePriority.Normal;
    public double? Progress { get; set; }
    public List<NoticeAction> Actions { get; set; } = [];
    public NoticeCapabilities Capabilities { get; set; }
    // Whether the notice still exists at its source (still in the notification center, not closed by the application).
    public bool SourcePresent { get; set; } = true;
    // Seen in BrimDeck: its details were opened or it was listed on the recent notices page. Says nothing about the source
    // application's own read state.
    public bool Viewed { get; set; }
    // The strip offering to add the notice to the planner (or showing what it added) was closed: the page no longer offers it,
    // in the focus or in the list. A plain flag, as Call is, so that an older BrimDeck still reads the saved notices.
    public bool AgendaDismissed { get; set; }
    public int Revision { get; set; }
    public NoticeTiming? Timing { get; set; }
    // An incoming call (NoticeInput.Call): shown with the call mark and as urgent as a ringing alarm. A plain flag, so that
    // an older BrimDeck still reads the saved notices.
    public bool Call { get; set; }
    // Not to be saved (NoticeInput.Transient): NoticeHistory.Save leaves it out, so it is gone once BrimDeck restarts. A
    // plain flag, as Call is.
    public bool Transient { get; set; }
    // What the sender asked for (NoticeInput.Level, Align); Auto and null leave them to BrimDeck's rules and settings.
    public NoticeLevel Level { get; set; }
    public NoticeAlign? Align { get; set; }
    public NoticeSlot? Slot { get; set; }
    public string? Glow { get; set; }
    public string Context { get; set; } = "";
    public bool HideIcon { get; set; }
    // NoticeInput.Unlisted, Awaited and Timeout. None of them outlives BrimDeck: an unlisted notice is never saved, and after a
    // restart no sender waits any more.
    [JsonIgnore] public bool Unlisted { get; set; }
    [JsonIgnore] public bool Awaited { get; set; }
    [JsonIgnore] public TimeSpan? Timeout { get; set; }
    // NoticeInput.Questions: answered only while the sender waits, so not saved either.
    [JsonIgnore] public IReadOnlyList<BrimDeck.Protocol.NoticeQuestion> Questions { get; set; } = [];
    public string Code { get; set; } = "";
    [JsonIgnore] public bool HasProgress => Progress is not null;
    [JsonIgnore] public NoticeTimeKind TimeKind => Timing?.Kind ?? NoticeTimeKind.None;
    public Notice Copy() { var copy = (Notice)MemberwiseClone(); copy.Actions = [.. Actions]; copy.Timing = Timing?.Copy(); return copy; }
}

// The user's choices for one application or source (the "应用" settings page). A taskbar-flash chat shows only while it has a
// rule (NoticeKeys.ListedOnly); a Windows application, a command-line or a WSL source shows without one. Enabled false drops
// its notices (the "接收" switch). Level is Auto or Silent: Auto shows its notices at their fixed level (NoticeLevels.Resolve);
// Silent ("岛上提示" off) keeps them in the recent notices without showing them as they arrive.
public sealed class NoticeAppRule
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public NoticeLevel Level { get; set; }
    // The application's own looks ("单独设置样子"), used in place of the settings' NoticeStyles while not null.
    public NoticeLevelStyles? Styles { get; set; }
    // "#RRGGBB" chosen by hand; empty takes the color read from the icon.
    public string AccentColor { get; set; } = "";
    // WeChat's row only ("读取消息内容"): its messages are read from its chat list, with who sent them and their words, and can
    // be answered (WeChatSource). Off, only the flash of its taskbar button is told. Off by default: it changes the running WeChat.
    public bool ReadContent { get; set; }
    public NoticeAppRule Copy() { var copy = (NoticeAppRule)MemberwiseClone(); copy.Styles = Styles?.Copy(); return copy; }
}

// An application BrimDeck has received notices from, so that the settings can list it before the user has made any rule.
public sealed class NoticeKnownApp
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public NoticeOrigin Origin { get; set; }
    public string? IconRef { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public static class NoticeKeys
{
    public static string Windows(string appUserModelId) => "windows:" + appUserModelId;
    public static string Wsl(string distro, string appName) => "wsl:" + distro + ":" + appName;
    public static string Cli(string? distro, string appName) => distro is null ? "cli:" + appName : "cli:" + distro + ":" + appName;
    // The sources that show only once the user lists them on the "应用" page: the chats whose taskbar button flashes (ChatFlash),
    // since watching the taskbar is the user's choice. Windows applications, command-line and WSL sources show without a rule.
    public static bool ListedOnly(string appKey) => appKey.StartsWith("flash:", StringComparison.Ordinal);
    // The rows of the "Windows 应用" list on the "应用" page: Windows applications and the taskbar-flash chats. Not the notices of
    // a sender Windows does not name (UnknownWindowsApp): they cannot be set apart by application.
    public static bool OnAppList(string appKey) => appKey.StartsWith("windows:", StringComparison.Ordinal) && appKey != UnknownWindowsApp || ListedOnly(appKey);
    // The key of a Windows notice whose application cannot be told (WindowsNoticeSource.Read).
    public const string UnknownWindowsApp = "windows:";
    public static NoticeCategory Category(string? text) => text switch
    {
        "task" => NoticeCategory.Task, "success" => NoticeCategory.Success, "warning" => NoticeCategory.Warning,
        "error" => NoticeCategory.Error, "info" => NoticeCategory.Info, _ => NoticeCategory.Message
    };
    public static NoticePriority Priority(string? text) => text switch { "low" => NoticePriority.Low, "high" => NoticePriority.High, _ => NoticePriority.Normal };
    // BrimDeck's own features (NoticeOrigin.Internal): "brimdeck:music", "brimdeck:usage". No sender can name itself so, since
    // the link a message arrives on decides its key's prefix (NoticeIntake).
    public const string InternalPrefix = "brimdeck:";
    public static string Internal(string module) => InternalPrefix + module;
    // A level or an alignment BrimDeck does not know (a later version's) reads as not given. A sender names three levels and
    // silent, also by number; flash is level 2 (NoticeLevel.Glow), and glow and lively are its earlier names: which effect it
    // plays is the user's setting (NoticeStyle.Liquid).
    public static NoticeLevel Level(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "silent" or "0" => NoticeLevel.Silent, "plain" or "1" => NoticeLevel.Plain, "flash" or "glow" or "lively" or "2" => NoticeLevel.Glow,
        "persistent" or "3" => NoticeLevel.Persistent, _ => NoticeLevel.Auto
    };
    // The collapsed part a slot names; null for the whole island.
    public static CompactPart? Part(NoticeSlot slot) => slot switch
    {
        NoticeSlot.Usage => CompactPart.Rings, NoticeSlot.Music => CompactPart.Music, NoticeSlot.Monitor => CompactPart.Monitor,
        NoticeSlot.Clock => CompactPart.Clock, NoticeSlot.Transfer => CompactPart.Transfer, NoticeSlot.Devices => CompactPart.Devices, _ => null
    };
    public static NoticeSlot? Slot(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "island" => NoticeSlot.Island, "usage" => NoticeSlot.Usage, "music" => NoticeSlot.Music, "monitor" => NoticeSlot.Monitor,
        "clock" => NoticeSlot.Clock, "transfer" => NoticeSlot.Transfer, "devices" => NoticeSlot.Devices, _ => null
    };
    // "#RRGGBB" in capitals, or null for anything else.
    public static string? Color(string? text)
        => text?.Trim() is { Length: 7 } hex && hex[0] == '#' && hex.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0 ? hex.ToUpperInvariant() : null;
    public static NoticeAlign? Align(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "left" => NoticeAlign.Left, "center" => NoticeAlign.Center, "right" => NoticeAlign.Right, _ => null
    };
}
