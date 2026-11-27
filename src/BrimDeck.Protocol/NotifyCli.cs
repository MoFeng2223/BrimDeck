using System.Diagnostics;
using System.Text;

namespace BrimDeck.Protocol;

// What one brim-notify differs in: how a message reaches BrimDeck, how "run" starts a program, and what it says when BrimDeck
// cannot be reached. Start returns the started process, or null with the exit code to return (it printed why); Report then
// says whether that code still counts as the program's result, sent to BrimDeck as a finished run (a program not found on
// Windows, as cmd reported it before).
public sealed record NotifySender(
    string Version,
    Func<NoticeMessage, Task<NoticeMessage?>> Send,
    Func<IReadOnlyList<string>, (Process? Process, int Failure, bool Report)> Start,
    string Unreachable,
    string NotReceiving)
{
    // The command's name in what it prints, and its help.
    public string Name { get; init; } = "brim-notify";
    public string Usage { get; init; } = NotifyCommandLine.Usage;
    // Sends a notice whose sender waits (NoticeMessage.Wait) and keeps the link until BrimDeck says how it ended: the reply,
    // and the "result" that follows a reply marked Waiting (null when the link ended first, or nothing was waited for).
    public Func<NoticeMessage, Task<(NoticeMessage? Reply, NoticeMessage? Result)>>? SendWaiting { get; init; }
    // Sends as Send does, but gives up sooner when BrimDeck is not there: a hook runs with every prompt and tool, and must not
    // hold the tool up while BrimDeck is closed.
    public Func<NoticeMessage, Task<NoticeMessage?>>? SendQuick { get; init; }
    // Where a hook's input is read from; standard input when null (tests give their own).
    public Func<Stream>? Input { get; init; }
}

// brim-notify's commands, shared by the Windows sender (BrimDeck.Notify.exe) and the WSL agent (q-P1.5). Exit codes: 0
// delivered (or filtered by the user's rules), 1 usage error (a message too large to send included), 2 refused by BrimDeck
// (a stop or close that found nothing included), 3 BrimDeck could not be reached or did not answer in time; "run" exits with
// the program's own code.
public static class NotifyCli
{
    // Dismissed and Expired are how a notice that was waited for ended without a button chosen.
    public const int Usage = 1, Refused = 2, Unreachable = 3, Dismissed = 4, Expired = 5;
    // The reply brim-notify makes up itself for a message too large for a frame even with its texts cut (W7).
    public const string TooLarge = "too-large";
    // A file given to --json at most; a message is at most a frame.
    private const int MaxJson = NoticeProtocol.MaxFrame;

    // The command's name in what is printed while a command runs (brim or brim-notify).
    private static string? s_name;
    private static string Name => s_name ?? "brim-notify";

    public static async Task<int> RunAsync(NotifyCommand command, NotifySender sender)
    {
        s_name = sender.Name;
        switch (command.Kind)
        {
            case NotifyCommandKind.Help: Console.WriteLine(sender.Usage); return 0;
            case NotifyCommandKind.Version: Console.WriteLine(sender.Version); return 0;
            case NotifyCommandKind.Error: Console.Error.WriteLine(Name + ": " + command.Error); Console.Error.WriteLine("Try " + Name + " --help."); return Usage;
            case NotifyCommandKind.Stdio: Console.Error.WriteLine(Name + ": --stdio is for the Windows sender."); return Usage;
            case NotifyCommandKind.Status when command.JsonOutput:
            {
                var reply = await sender.Send(new NoticeMessage { Type = NoticeTypes.Status });
                bool receiving = reply?.Result == NoticeResults.Accepted;
                // Refused or unanswered, BrimDeck still runs; with no reply at all it could not be reached.
                Console.WriteLine($"{{\"running\":{Word(reply is not null)},\"receiving\":{Word(receiving)},\"detail\":{System.Text.Json.JsonSerializer.Serialize(reply?.Detail ?? "", NoticeJson.Default.String)}}}");
                return receiving ? 0 : Unreachable;
            }
            case NotifyCommandKind.Status:
            {
                var reply = await sender.Send(new NoticeMessage { Type = NoticeTypes.Status });
                if (reply is null) { Console.WriteLine(sender.NotReceiving); return Unreachable; }
                // Written but not answered ("no-reply": an agent ending, or an old one still holding the socket), or refused:
                // not confirmed, so not reported as receiving (review-batch1 item 6).
                if (reply.Result != NoticeResults.Accepted)
                {
                    Console.WriteLine("BrimDeck did not confirm that it is receiving notices" + (reply.Detail is { Length: > 0 } why ? " (" + why + ")." : "."));
                    return Unreachable;
                }
                Console.WriteLine("BrimDeck is receiving notices." + (reply.Detail is { Length: > 0 } detail ? " " + detail : ""));
                return 0;
            }
            case NotifyCommandKind.Run: return await RunProgramAsync(command, sender);
            case NotifyCommandKind.List: return await ListAsync(command, sender);
            case NotifyCommandKind.Hook: return await HookAsync(command.Hook!, sender);
            default:
            {
                var message = command.Message!;
                if (command.JsonInput is { } file)
                {
                    if (ReadJson(file) is not { } read) return Usage;
                    if (NotifyCommandLine.Combine(read, message, Environment.GetEnvironmentVariable(BrimCommandLine.SourceVariable) is { Length: > 0 } named ? named.Trim() : BrimCommandLine.DefaultSource) is not { } whole)
                    {
                        Console.Error.WriteLine(Name + ": a title or body is needed");
                        return Usage;
                    }
                    message = whole;
                }
                if (message.Wait == true && message.Stop != true) return await WaitAsync(sender, message);
                int code = Report(await SendAsync(sender, message), sender.Unreachable, message);
                // A timer, stopwatch or event: its key, for stopping it later.
                if (code == 0 && NotifyCommandLine.PrintsKey(message)) Console.WriteLine(message.ReplaceId);
                return code;
            }
        }
    }

    private static string Word(bool value) => value ? "true" : "false";

    // A hook's input at most (a file write's whole text comes with its approval).
    private const int MaxHookInput = 8 * 1024 * 1024;

    // brim hook: the tool's hook input from standard input, planned by AgentHook. It always exits with 0 and prints only an
    // answer given in BrimDeck (or, for a tool that reads an object from every hook, an empty one): unreadable input, BrimDeck not
    // running, a question dismissed, timed out or sent back to the terminal all leave the tool to its own flow.
    private static async Task<int> HookAsync(AgentHookCommand hook, NotifySender sender)
    {
        System.Text.Json.JsonElement input;
        try
        {
            using var stream = sender.Input?.Invoke() ?? Console.OpenStandardInput();
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int n;
            while ((n = await stream.ReadAsync(chunk)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > MaxHookInput) return Print(AgentHook.Quiet(hook.Format, ""));
            }
            // A tool that writes its hook input in the PC's code page rather than UTF-8 is still read.
            ReadOnlySpan<byte> json = InputText.Utf8(buffer.ToArray());
            if (json.StartsWith("\uFEFF"u8)) json = json[3..];
            using var document = System.Text.Json.JsonDocument.Parse(json.ToArray());
            input = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return Print(AgentHook.Quiet(hook.Format, "")); }
        bool chinese = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var plan = AgentHook.Plan(hook.Format, hook.Id, hook.Name, input, chinese);
        var quick = sender.SendQuick ?? sender.Send;
        // BrimDeck is not running: nothing more is tried, so the tool waits for nothing.
        if (plan.Report is { } report && await quick(report) is null) return Print(plan.Quiet);
        if (plan.Close is { } close && Prepare(close) is null) await quick(close);
        if (plan.Notice is { } notice && Prepare(notice) is null) await quick(notice);
        if (plan.Ask is not { } ask || sender.SendWaiting is null) return Print(plan.Quiet);
        // A long command or file is cut until the message fits a frame.
        while (Prepare(ask) is not null && ask.Code is { Length: > 256 } code) ask.Code = code[..(code.Length / 2)] + "…";
        if (Prepare(ask) is not null) return Print(plan.Quiet);
        var (reply, result) = await sender.SendWaiting(ask);
        if (reply?.Waiting != true || plan.Reply(result) is not { } output) return Print(plan.Quiet);
        Print(output);
        if (plan.Answered is { } answered) await quick(answered);
        return 0;
    }

    // Writes a hook's output, if any; a hook exits with 0 either way.
    private static int Print(string? output)
    {
        if (output is null) return 0;
        Console.Out.Write(output);
        Console.Out.Flush();
        return 0;
    }

    // What list --json prints: the words as they are rather than as \u escapes, for a reader as much as for a script.
    private static readonly NoticeJson Readable = new(new System.Text.Json.JsonSerializerOptions(NoticeJson.Default.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    // A page more than the notices page can hold (NoticeHub keeps at most 500, a page holds at least one) ends the listing.
    private const int MaxPages = 600;

    // brim notify list: the pages one after another, then every notice printed at once, as JSON or one line each.
    private static async Task<int> ListAsync(NotifyCommand command, NotifySender sender)
    {
        var listing = new NoticeListing();
        string? cursor = null;
        for (int page = 0; page < MaxPages; page++)
        {
            var request = new NoticeMessage { Type = NoticeTypes.List, AppName = command.Message?.AppName, Cursor = cursor };
            var reply = await sender.Send(request);
            if (reply?.Result != NoticeResults.Accepted) return Report(reply, sender.Unreachable, request);
            listing.Scope = reply.Scope ?? NoticeScopes.Cli;
            listing.Notices.AddRange(reply.Notices ?? []);
            cursor = reply.Cursor;
            if (cursor is null) break;
        }
        if (command.JsonOutput) { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(listing, Readable.NoticeListing)); return 0; }
        if (listing.Scope != NoticeScopes.All)
            Console.Error.WriteLine("Only the notices of brim and other programs are listed. To list every notice, turn on \"Let brim list and remove all notices\" in BrimDeck's settings (Notices › Apps › Compatibility).");
        if (listing.Notices.Count == 0) { Console.Error.WriteLine("No notices."); return 0; }
        foreach (var notice in listing.Notices) Console.WriteLine(Line(notice));
        return 0;
    }

    // One notice on one line: its id, when it came, where from, its key and state, and its words.
    private static string Line(NoticeEntry notice)
    {
        var line = new StringBuilder(notice.Id).Append("  ").Append(notice.ReceivedAt.ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture))
            .Append("  ").Append(notice.Source);
        if (notice.Origin != "cli") line.Append(" (").Append(notice.Origin).Append(')');
        if (notice.Key is { Length: > 0 } key) line.Append("  key=").Append(key);
        foreach (var (on, word) in new[] { (notice.Unread, "unread"), (notice.Shown, "shown"), (notice.Waiting, "waiting"), (notice.Closed, "closed") })
            if (on) line.Append("  ").Append(word);
        string words = notice.Body.Length > 0 ? (notice.Title.Length > 0 ? notice.Title + " — " + notice.Body : notice.Body) : notice.Title;
        line.Append("  ").Append(words.ReplaceLineEndings(" "));
        return line.ToString();
    }

    // The message of --json: a file, or standard input for "-". Null (said why) when it cannot be read or is not a message.
    private static NoticeMessage? ReadJson(string file)
    {
        byte[] json;
        try
        {
            if (file == "-")
            {
                using var input = Console.OpenStandardInput();
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int n;
                while ((n = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, n);
                    if (buffer.Length > MaxJson) { Console.Error.WriteLine(Name + ": the JSON on standard input is larger than a message may be."); return null; }
                }
                json = buffer.ToArray();
            }
            else
            {
                if (new FileInfo(file) is { Exists: true, Length: > MaxJson }) { Console.Error.WriteLine(Name + ": " + file + " is larger than a message may be."); return null; }
                json = File.ReadAllBytes(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine(Name + ": could not read " + (file == "-" ? "standard input" : file) + ": " + ex.Message);
            return null;
        }
        // A file saved with a byte order mark is still JSON, and so is one in the PC's code page (what cmd pipes, a file saved
        // as "ANSI").
        ReadOnlySpan<byte> text = InputText.Utf8(json, file: file != "-");
        if (text.StartsWith("﻿"u8)) text = text[3..];
        if (NoticeProtocol.Deserialize(text) is { } message) return message;
        Console.Error.WriteLine(Name + ": " + (file == "-" ? "standard input" : file) + " does not hold a message as JSON.");
        return null;
    }

    // A notice that waits: the chosen button's key is printed, and the exit code says how it ended.
    private static async Task<int> WaitAsync(NotifySender sender, NoticeMessage message)
    {
        if (sender.SendWaiting is null) { Console.Error.WriteLine(Name + ": this sender cannot wait for an answer."); return Usage; }
        if (Prepare(message) is { } tooLarge) return Report(tooLarge, sender.Unreachable, message);
        var (reply, result) = await sender.SendWaiting(message);
        int code = Report(reply, sender.Unreachable, message);
        if (code != 0) return code;
        // The user's rules drop the source's notices: nothing shows, so nothing is answered.
        if (reply!.Result == NoticeResults.Filtered) return Dismissed;
        if (reply.Waiting != true)
        {
            Console.Error.WriteLine(Name + ": the notice is shown, but this BrimDeck does not wait for an answer.");
            return 0;
        }
        if (result is null) { Console.Error.WriteLine(Name + ": the connection to BrimDeck ended before the notice was answered."); return Unreachable; }
        switch (result.Result)
        {
            case NoticeWaits.Action: Console.WriteLine(result.ActionKey ?? ""); return 0;
            case NoticeWaits.Expired: return Expired;
            default: return Dismissed;
        }
    }

    // A notice cut to BrimDeck's limits and its icon given as a full path before it goes; one that still does not fit in a
    // frame is not sent, and gets TooLarge (W7).
    private static async Task<NoticeMessage?> SendAsync(NotifySender sender, NoticeMessage message)
        => Prepare(message) ?? await sender.Send(message);

    // Null when the message can go; else the reply made up for one too large.
    private static NoticeMessage? Prepare(NoticeMessage message)
    {
        NoticeProtocol.ClipTexts(message);
        bool windows = OperatingSystem.IsWindows();
        message.Icon = NotifyCommandLine.ResolveIcon(message.Icon, windows, Path.GetFullPath, File.Exists);
        message.Picture = NotifyCommandLine.ResolveIcon(message.Picture, windows, Path.GetFullPath, File.Exists);
        // Room for the id and times the sender still adds.
        return NoticeProtocol.Serialize(message).Length > NoticeProtocol.MaxFrame - 512 ? NoticeProtocol.Reply(NoticeResults.Rejected, TooLarge) : null;
    }

    // sent: the message the reply is for, which names the key of a stop.
    public static int Report(NoticeMessage? reply, string unreachable, NoticeMessage? sent = null)
    {
        if (reply is null) { Console.Error.WriteLine(Name + ": " + unreachable); return Unreachable; }
        if (reply is { Result: NoticeResults.Rejected, Detail: TooLarge })
        {
            Console.Error.WriteLine(Name + ": the message is too large to send, even with its texts cut to BrimDeck's limits.");
            return Usage;
        }
        // The message went out, but no answer came in time (BrimDeck busy, starting or waking up): it may still show (W3).
        if (reply is { Result: NoticeResults.Rejected, Detail: "no-reply" })
        {
            Console.Error.WriteLine(Name + ": BrimDeck did not answer in time; the notice may still be shown.");
            return Unreachable;
        }
        // A BrimDeck from before list and remove.
        if (reply is { Result: NoticeResults.Rejected, Detail: "unknown-type" } && sent?.Type is NoticeTypes.List or NoticeTypes.Remove)
        {
            Console.Error.WriteLine(Name + ": this BrimDeck cannot " + sent.Type + " notices; update BrimDeck.");
            return Refused;
        }
        if (reply.Result == NoticeResults.Rejected) { Console.Error.WriteLine(Name + ": " + "BrimDeck refused the notice: " + (reply.Detail ?? "unknown reason")); return Refused; }
        // A stop, close or remove that found nothing: a mistyped key or id, one sent with another -s (W8), or one the settings
        // keep from the command line.
        if (reply is { Result: NoticeResults.Filtered, Detail: "no-timer" or "not-found" })
        {
            Console.Error.WriteLine(Name + ": " + (sent?.NoticeId is { } id ? "no notice brim can reach has the id " + id + " (brim notify list gives the ids)."
                : (sent?.Type == NoticeTypes.Remove ? "no notice has the key " : "no timer, stopwatch, event or open notice has the key ") + (sent?.ReplaceId ?? "given")
                    + " (one sent with -s is " + (sent?.Type == NoticeTypes.Remove ? "removed" : "stopped or closed") + " with the same -s)."));
            return Refused;
        }
        if (reply.Result == NoticeResults.Filtered) Console.Error.WriteLine(Name + ": received; BrimDeck's rules keep this source quiet.");
        if (reply is { Result: NoticeResults.Accepted, Detail: "windows-kept" })
            Console.Error.WriteLine(Name + ": removed from BrimDeck; Windows' notification center still has it (Windows did not allow removing it, or it changed meanwhile).");
        return 0;
    }

    private static async Task<int> RunProgramAsync(NotifyCommand command, NotifySender sender)
    {
        var watch = Stopwatch.StartNew();
        var (process, failure, report) = sender.Start(command.Program!);
        if (process is null) return report ? await ReportRunAsync(command, sender, failure, watch.Elapsed) : failure;
        using (process)
        {
            // Ctrl+C reaches the program too; brim-notify waits for it to finish and still reports the result.
            Console.CancelKeyPress += (_, e) => e.Cancel = true;
            await process.WaitForExitAsync();
            return await ReportRunAsync(command, sender, process.ExitCode, watch.Elapsed);
        }
    }

    private static async Task<int> ReportRunAsync(NotifyCommand command, NotifySender sender, int code, TimeSpan elapsed)
    {
        if (await SendAsync(sender, NotifyCommandLine.RunResult(command.Message!, code, elapsed)) is null)
            Console.Error.WriteLine(Name + ": the program finished, but BrimDeck could not be reached.");
        return code;
    }
}

/// How the Windows brim-notify finds and starts "run"'s program (sec-L1). A bare name is looked for in PATH only, the way a
// shell would with the current folder left out: CreateProcess alone would first look in BrimDeck's folder and the current one.
// Programs other than .exe and .com (batch files, scripts opened by their file type) and cmd's own commands run through
// cmd, with a command line built so that cmd hands every argument over as it was given (C-M1).
public static class WindowsPrograms
{
    // What a program that is not found exits with, as cmd says it: brim-notify used to leave that to cmd, and scripts check it.
    public const int NotFound = 9009;

    // cmd's own commands, which are no files to find.
    private static readonly HashSet<string> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        "assoc", "cd", "chdir", "cls", "copy", "date", "del", "dir", "echo", "erase", "ftype", "md", "mkdir", "mklink",
        "move", "path", "rd", "ren", "rename", "rmdir", "set", "time", "title", "type", "ver", "vol"
    };

    // cmd's own commands that read their arguments as a command line once more (call, and start's new cmd), expanding %NAME%
    // again after CmdArguments kept it from the first reading (N2). They are refused; "cmd /c" says so openly when meant.
    public static bool Reparses(string name) => name.Equals("call", StringComparison.OrdinalIgnoreCase) || name.Equals("start", StringComparison.OrdinalIgnoreCase);

    // The full path of the program, or null. pathExt is %PATHEXT%; exists says whether a file is there. skip is BrimDeck's
    // own folder, never searched even when it is in PATH: it holds no program of the user's, and others may be able to write
    // to it (S5).
    public static string? Find(string name, string? path, string? pathExt, Func<string, bool> exists, string? skip = null)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || name.Contains('*') || name.Contains('?')) return null;
        var extensions = (string.IsNullOrWhiteSpace(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Given with a folder (.\build.bat, C:\tools\x): that file, as written.
        if (name.Contains('\\') || name.Contains('/') || name.Contains(':'))
            return Candidates(Path.GetFullPath(name), extensions).FirstOrDefault(exists);
        string? skipped = skip is null ? null : Folder(skip);
        foreach (var folder in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folderPath = folder.Trim('"');
            if (!Path.IsPathFullyQualified(folderPath)) continue;
            if (skipped is not null && string.Equals(Folder(folderPath), skipped, StringComparison.OrdinalIgnoreCase)) continue;
            if (Candidates(Path.Combine(folderPath, name), extensions).FirstOrDefault(exists) is { } found) return found;
        }
        return null;
    }

    private static string Folder(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // The name as given when it already has one of the extensions; otherwise with each in turn, and a name with an extension
    // of its own (script.py) first as it is, which cmd then opens by its file type.
    private static IEnumerable<string> Candidates(string file, string[] extensions)
    {
        if (extensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))) return [file];
        var appended = extensions.Select(extension => file + extension);
        return Path.HasExtension(file) ? appended.Prepend(file) : appended;
    }

    public static bool IsBuiltin(string name) => Builtins.Contains(name);

    // Whether the program is started itself rather than through cmd.
    public static bool RunsDirectly(string file)
        => file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".com", StringComparison.OrdinalIgnoreCase);

    // The arguments for cmd.exe that run the program with its arguments, escaped the way Rust's standard library escapes
    // them for batch files (its fix of CVE-2024-24576): an argument is quoted only when it needs to be, so "release" reaches
    // %1 as release; a quote inside is doubled; % becomes %%cd:~,% (an empty substring of %cd% after it), which keeps cmd from
    // expanding %NAME% even inside quotes; /v:off leaves ! alone. Null only when a part holds a line break or NUL, which would
    // end cmd's command line early, or the program's path cannot be quoted. A builtin's name stays bare, since cmd does not
    // know "echo" with quotes.
    public static string? CmdArguments(string program, IEnumerable<string> arguments, bool builtin)
    {
        // /e:on: the %cd:~,% substring needs cmd's extensions, which the registry could have switched off.
        var line = new StringBuilder("/e:on /d /v:off /s /c \"");
        if (builtin) line.Append(program);
        else
        {
            // A file name cannot hold a quote, and a trailing \ would escape the closing one.
            if (program.Length == 0 || program.Contains('"') || program.EndsWith('\\') || Unsafe(program)) return null;
            line.Append('"');
            AppendEscaped(line, program);
            line.Append('"');
        }
        foreach (var argument in arguments)
        {
            if (Unsafe(argument)) return null;
            line.Append(' ');
            AppendArgument(line, argument);
        }
        // /s: cmd takes everything between the first and the last quote as the command, as it is.
        return line.Append('"').ToString();
    }

    private static bool Unsafe(string text) => text.IndexOfAny(['\r', '\n', '\0']) >= 0;

    private static void AppendArgument(StringBuilder line, string argument)
    {
        // An empty argument would vanish unquoted; one ending in \ is quoted for scripts that pass "%~1" on to a program,
        // whose parser would otherwise read that \ as escaping the closing quote.
        bool quote = argument.Length == 0 || argument.EndsWith('\\');
        // Every ASCII symbol is taken as one cmd may treat specially, except these known to be plain; control characters too.
        foreach (char c in argument)
            if (c < 128 && !(char.IsAsciiLetterOrDigit(c) || "#$*+-./:?@\\_".Contains(c)) || char.IsControl(c)) { quote = true; break; }
        if (!quote) { line.Append(argument); return; }
        line.Append('"');
        int backslashes = AppendEscaped(line, argument);
        // Backslashes before the closing quote are doubled, so that they stay backslashes.
        line.Append('\\', backslashes).Append('"');
    }

    // The text with each quote doubled (the backslashes before it doubled too) and each % made one cmd does not expand;
    // returns how many backslashes it ends with.
    private static int AppendEscaped(StringBuilder line, string text)
    {
        int backslashes = 0;
        foreach (char c in text)
        {
            if (c == '\\') { backslashes++; line.Append(c); continue; }
            if (c == '"') line.Append('\\', backslashes).Append('"');
            else if (c == '%') line.Append("%%cd:~,");
            backslashes = 0;
            line.Append(c);
        }
        return backslashes;
    }
}
