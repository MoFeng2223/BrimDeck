using BrimDeck.Protocol;

namespace BrimDeck.Core.Notices;

// Turns a protocol message into a notice input. The link a message arrived on decides its origin and distribution, never
// the message itself: a sender cannot claim to be a Windows application or another distribution.
public static class NoticeIntake
{
    // A message from the local pipe (BrimDeck.Notify.exe on Windows, or brim-notify in WSL through it while the agent is not
    // running). The pipe only admits the current user, so a distribution named by such a sender is taken as a label.
    // now and zone resolve a time-kind notice's times (NoticeTimeIntake); they default to the present and Windows' time zone.
    public static NoticeInput FromPipe(NoticeMessage message, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        string name = Name(message.AppName, "brim-notify");
        // Only a name WSL itself allows is taken as a label (sec-L4).
        string? distro = Blank(message.Distro) is { } named && IsDistroName(named) ? named : null;
        // Its instance keys are marked as the pipe's, apart from those the distribution's agent delivers (S3).
        string instance = CliInstance(distro, name, message);
        if (distro is not null) instance = PipePrefix + instance["cli:".Length..];
        return Build(message, NoticeOrigin.Cli, NoticeKeys.Cli(distro, name), name, distro, instance, NoticeCapabilities.None, now, zone);
    }

    private const string PipePrefix = "pipe:";

    // Whether a command-line notice naming a distribution came over the pipe rather than from that distribution's agent.
    public static bool ViaPipe(string instanceKey) => instanceKey.StartsWith(PipePrefix, StringComparison.Ordinal);

    // A message from a distribution's agent. Desktop notifications get the agent's capabilities: actions and dismissal reach
    // the application only while the agent is the session's notification service.
    public static NoticeInput FromAgent(NoticeMessage message, string distro, bool agentIsServer, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        if (message.Origin == "dbus")
        {
            string name = Name(message.AppName, Loc.T("Linux 应用", "Linux app"));
            var capabilities = agentIsServer ? NoticeCapabilities.ReportDismissal | (message.Actions is { Count: > 0 } ? NoticeCapabilities.Actions : 0) : NoticeCapabilities.None;
            string instance = $"wsl:{distro}:{message.Session ?? ""}:{message.NativeId ?? Guid.NewGuid().ToString("N")}";
            return Build(message, NoticeOrigin.Wsl, NoticeKeys.Wsl(distro, name), name, distro, instance, capabilities, now, zone);
        }
        string cliName = Name(message.AppName, "brim-notify");
        return Build(message, NoticeOrigin.Cli, NoticeKeys.Cli(distro, cliName), cliName, distro, CliInstance(distro, cliName, message), NoticeCapabilities.None, now, zone);
    }

    // The WSL link's own count of notices, on the bridge's reading thread before anything reaches the UI thread, as the pipe
    // counts its messages (W5): a program in the distribution calling Notify in a loop must not fill the UI thread's queue.
    // Whether the message goes on; refusal is the reply for a brim-notify waiting through the agent, null when nobody waits.
    // Only what a sender asks counts (a notice, a list, a removal): status reports and closes are few and bounded by the agent.
    public static bool AdmitFromAgent(IntakeGate gate, NoticeMessage message, long nowMilliseconds, out NoticeMessage? refusal)
    {
        refusal = null;
        if (message.Type is not (NoticeTypes.Notify or NoticeTypes.List or NoticeTypes.Remove) || gate.Admit(nowMilliseconds)) return true;
        if (message.Origin == "cli" && message.EventId is { } id) refusal = NoticeProtocol.Reply(NoticeResults.Rejected, "rate-limited", id);
        return false;
    }

    // A command-line notice's instance at its source: the sender's event id within its own name (core-M1), so the same
    // --id from another -s is another notice; a blank id is no id, and the notice is its own.
    private static string CliInstance(string? distro, string name, NoticeMessage message)
        => $"cli:{distro}:{name}:" + (Blank(message.EventId) ?? Guid.NewGuid().ToString("N"));

    private static NoticeInput Build(NoticeMessage message, NoticeOrigin origin, string appKey, string appName, string? distro, string instance, NoticeCapabilities capabilities,
        DateTimeOffset? now, TimeZoneInfo? zone) => new()
    {
        Origin = origin, AppKey = appKey, AppName = appName, AppId = NoticeText.Clean(message.AppId ?? "", singleLine: true), Distro = distro, InstanceKey = instance,
        ReplaceKey = Blank(message.ReplaceId), EventId = Blank(message.EventId),
        Title = message.Title ?? "", Body = message.Body ?? "", IconRef = IconRef(message.Icon, origin, distro),
        CreatedAt = message.CreatedAt, Age = message.AgeMs is >= 0 and var age ? TimeSpan.FromMilliseconds(age) : null, Category = NoticeKeys.Category(message.Category), PriorityHint = NoticeKeys.Priority(message.Priority),
        Progress = message.Progress,
        // A sender that waits is told which button was chosen, as a WSL application is while the agent serves it.
        Capabilities = capabilities | (origin == NoticeOrigin.Cli && Waits(message) ? NoticeCapabilities.Actions : 0),
        Actions = origin == NoticeOrigin.Cli ? Buttons(message, distro) : DesktopActions(message, capabilities),
        ExitCode = message.ExitCode, DurationMs = message.DurationMs, Command = Blank(message.Command),
        Timing = NoticeTimeIntake.Resolve(message, now ?? DateTimeOffset.UtcNow, zone ?? TimeZoneInfo.Local),
        StopTiming = message.Stop == true, TargetId = Blank(message.NoticeId),
        Transient = message.Transient == true,
        Level = NoticeKeys.Level(message.Level), Align = NoticeKeys.Align(message.Align),
        Slot = NoticeKeys.Slot(message.Slot), Glow = NoticeKeys.Color(message.Glow),
        Context = NoticeText.Clean(message.Context ?? "", singleLine: true).Trim(),
        Picture = IconRef(message.Picture, origin, distro), HideIcon = message.HideIcon == true,
        // A notice kept off the list cannot be answered from the notices page, so one that is waited for is always listed.
        Unlisted = message.History == false && !Waits(message),
        Awaited = Waits(message),
        // Questions can only be answered while their sender waits.
        Questions = Waits(message) && origin == NoticeOrigin.Cli ? Questions(message) : [],
        Code = message.Code is { Length: > 0 } code ? NoticeText.Clean(code).Trim() : "",
        Timeout = message.TimeoutMs is { } limit ? TimeSpan.FromMilliseconds(Math.Clamp(limit, NoticeProtocol.MinTimeoutMs, NoticeProtocol.MaxTimeoutMs)) : null
    };

    // Whether the sender waits for how its notice ends. Only a plain notice can be waited for: a timer, a stopwatch, an event
    // or a stop are answered at once.
    public static bool Waits(NoticeMessage message)
        => message.Wait == true && message.Stop != true && message.Kind is not (NoticeTimes.Countdown or NoticeTimes.Stopwatch or NoticeTimes.Event);

    // A sender's questions with their words cleaned as a title is; a question without options can only be answered in words.
    private static List<NoticeQuestion> Questions(NoticeMessage message)
        => message.Questions is not { } questions ? [] : [.. questions.Where(q => q is not null && !string.IsNullOrWhiteSpace(q.Question)).Select(q => new NoticeQuestion
        {
            Id = q.Id, Header = q.Header is { Length: > 0 } header ? NoticeText.Clean(header, singleLine: true).Trim() : null, Question = NoticeText.Clean(q.Question).Trim(),
            MultiSelect = q.MultiSelect == true, AllowOther = q.AllowOther == true || q.Options is not { Count: > 0 },
            Options = [.. (q.Options ?? []).Select(o => new NoticeOption { Key = o.Key, Label = NoticeText.Clean(o.Label, singleLine: true).Trim(), Description = o.Description is { Length: > 0 } d ? NoticeText.Clean(d).Trim() : null })]
        })];

    // Buttons a command-line sender may give at most.
    public const int MaxButtons = 4;

    // A desktop notification's actions, offered while the agent can pass the choice back. Labels show as buttons: cleaned as a
    // title is (C-L11).
    private static List<NoticeAction> DesktopActions(NoticeMessage message, NoticeCapabilities capabilities)
        => (capabilities & NoticeCapabilities.Actions) != 0 && message.Actions is { } actions ? actions.Select(a => new NoticeAction(a.Key, Name(a.Label, a.Key))).ToList() : [];

    // A command-line sender's buttons. One whose key goes back to the sender is kept only while the sender waits for it; a
    // copy needs its text and an open a target BrimDeck may open (OpenTarget), else the button is left out. At most one is
    // the main button.
    private static List<NoticeAction> Buttons(NoticeMessage message, string? distro)
    {
        var buttons = new List<NoticeAction>();
        if (message.Actions is not { } actions) return buttons;
        bool waits = Waits(message), main = false;
        foreach (var action in actions)
        {
            if (buttons.Count >= MaxButtons) break;
            if (action is null || Blank(action.Key) is not { } key) continue;
            string label = Name(action.Label, key);
            bool primary = !main && string.Equals(action.Style, NoticeActionTypes.Primary, StringComparison.OrdinalIgnoreCase);
            NoticeAction? button = action.Type?.Trim().ToLowerInvariant() switch
            {
                null or "" or NoticeActionTypes.Reply => waits ? new(key, label, NoticeActionKind.Reply, null, primary) : null,
                NoticeActionTypes.Copy => action.Value is { Length: > 0 } text ? new(key, label, NoticeActionKind.Copy, text, primary) : null,
                NoticeActionTypes.Open => OpenTarget(action.Value, distro) is { } target ? new(key, label, NoticeActionKind.Open, target, primary) : null,
                _ => null
            };
            if (button is null || buttons.Any(b => b.Key == button.Key)) continue;
            main |= primary;
            buttons.Add(button);
        }
        return buttons;
    }

    public const string AppTarget = "app:";

    // Files that opening would run rather than show: programs, scripts, installers, shortcuts and the like, by their extension.
    // An open button shows such a file in its folder instead of opening it.
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msc", ".msi", ".msp", ".msix", ".appx", ".scr", ".pif", ".cpl",
        ".lnk", ".url", ".reg", ".hta", ".jar", ".py", ".pyw", ".appref-ms", ".application", ".gadget", ".inf", ".scf", ".sh", ".dll", ".sys", ".appinstaller"
    };

    public static bool Runs(string path)
    {
        string extension;
        try { extension = Path.GetExtension(path.TrimEnd(' ', '.')); }
        catch (ArgumentException) { return true; }
        // A name without an extension has no program for its type; whatever else PATHEXT names runs too.
        return extension.Length == 0 || Runnable.Contains(extension)
            || (Environment.GetEnvironmentVariable("PATHEXT") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    // What an open button may open, as given: a web address (http or https); from a Windows sender also a local file or folder
    // written out in full (IsLocalWindowsPath) or "app:<application id>". Null for anything else. A sender inside a WSL
    // distribution only opens web addresses: its paths are not Windows'.
    public static string? OpenTarget(string? value, string? distro)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > NoticeProtocol.MaxActionValue || value.Any(char.IsControl)) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return uri.IsWellFormedOriginalString() || uri.Host.Length > 0 ? uri.AbsoluteUri : null;
        if (distro is not null) return null;
        if (value.StartsWith(AppTarget, StringComparison.OrdinalIgnoreCase))
        {
            string id = value[AppTarget.Length..].Trim();
            // An application user model id: a package family and application, or a name; never a path or a command line.
            return id.Length is > 0 and <= 256 && id.IndexOfAny(['\\', '/', '"', ' ', '*', '?', '<', '>', '|']) < 0 ? AppTarget + id : null;
        }
        return IsLocalWindowsPath(value) ? value.Replace('/', '\\') : null;
    }

    // Icons are local only: a Windows file path for the Windows link, a Linux path or icon name inside the distribution for
    // WSL. Anything that looks like a web address is ignored.
    public static string? IconRef(string? icon, NoticeOrigin origin, string? distro)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        icon = icon.Trim();
        if (icon.Contains("://", StringComparison.Ordinal) && !icon.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        if (icon.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) icon = Uri.TryCreate(icon, UriKind.Absolute, out var uri) ? uri.LocalPath : "";
        if (icon.Length == 0 || icon.Length > 1024 || icon.Any(char.IsControl)) return null;
        if (distro is null) return IsLocalWindowsPath(icon) ? "file:" + icon : null;
        return IsLinuxIcon(icon) ? $"wsl:{distro}:{icon}" : null;
    }

    // A path on a local drive, written out in full: "C:\folder\icon.png". Network paths (\\server\share), device paths
    // (\\?\, \\.\), relative paths, alternate data streams and ".." are refused.
    public static bool IsLocalWindowsPath(string path)
    {
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return false;
        if (path.IndexOf(':', 2) >= 0 || path.Contains("..", StringComparison.Ordinal) || path.Contains("\\\\", StringComparison.Ordinal) || path.Contains("//", StringComparison.Ordinal)) return false;
        return path.IndexOfAny(['*', '?', '"', '<', '>', '|']) < 0;
    }

    // An absolute path inside the distribution, or an icon name; no "..", no Windows separators, no drive letters.
    public static bool IsLinuxIcon(string icon)
    {
        if (icon.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|']) >= 0) return false;
        if (icon.Split('/').Any(part => part == "..")) return false;
        return icon.StartsWith('/') ? !icon.StartsWith("//", StringComparison.Ordinal) : icon.IndexOf('/') < 0;
    }

    // A WSL distribution's name as WSL allows it: letters, digits, ".", "_" and "-". It is used in \\wsl.localhost paths and
    // passed to wsl.exe, so nothing else may get through.
    public static bool IsDistroName(string name)
        => name.Length is > 0 and <= 64 && name is not ("." or "..") && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static string Name(string? name, string fallback) => NoticeText.Clean(name ?? "", singleLine: true).Trim() is { Length: > 0 } clean ? clean : fallback;
    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
