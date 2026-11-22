namespace BrimDeck.Protocol;

// brim's command line: "brim <object> <action>". Notices are one group of commands; settings and the island's place come
// later as groups of their own. The first word is always a command, never a title (brim-notify took any other first word
// as one, so a notice titled "status" could not be sent). The options are brim-notify's (NotifyCommandLine.ParseOptions).
public static class BrimCommandLine
{
    // The name a notice comes from when neither -s nor BRIM_SOURCE gives one.
    public const string DefaultSource = "brim";
    // The environment variable that names the source for every notice a script or a hook sends.
    public const string SourceVariable = "BRIM_SOURCE";

    public static readonly string Usage = """
        brim notify send TITLE [BODY] [options]   Show a notice in BrimDeck, or update one
        brim notify close KEY                     Close the notice sent with -k KEY
        brim run [options] -- PROGRAM ...         Run a program, then show its result
        brim timer start DURATION [TITLE]         Count down (25m, 1h30m, 90s), then remind
        brim timer start --at TIME [TITLE]        Count down to a time (15:30)
        brim timer stop KEY                       Cancel a countdown
        brim stopwatch start [TITLE]              Show the time elapsed since now
        brim stopwatch stop KEY                   Stop a stopwatch
        brim event add TITLE --at TIME            An event; reminds before it starts
        brim event remove KEY                     Cancel an event
        brim status [--json]                      Check that BrimDeck is running and receives notices

        Options of notify send:
          -b, --body TEXT        Body text
          -s, --source NAME      Name the notice comes from (default: $BRIM_SOURCE, then brim)
              --context TEXT     A second line beside the source: a project, a branch, a session
          -l, --level LEVEL      silent, plain, flash or persistent (or 0 to 3). BrimDeck's rules for the source come first
          -c, --category KIND    message, task, success, warning, error or info
              --glow COLOR       #RRGGBB: the backlight of a persistent notice
              --slot PLACE       Where a plain notice shows: island, usage, music, monitor, clock, transfer or devices
              --align SIDE       left, center or right: where the words sit on the island
          -i, --icon PATH        Local icon file; "none" shows no icon
              --image PATH       A picture in the icon's place (an avatar)
          -k, --key KEY          Update the earlier notice sent with the same key; close KEY closes it
              --id ID            Message id; a retry with the same id is shown once
              --progress N       Task progress, 0 to 100
              --action KEY=LABEL A button; brim waits and prints the key of the one chosen
              --copy LABEL=TEXT  A button that copies TEXT
              --open LABEL=URL   A button that opens a web address, a local file or app:<application id>
              --wait             Wait until the notice is answered, dismissed or runs out of time
              --timeout DURATION How long a persistent notice stays at most (default 3m, up to 30m)
              --no-history       Only show it on the island; do not list it among the recent notices
              --json FILE        Read the whole message as JSON from a file, or from standard input with "-"

        Options of timer, stopwatch and event:
              --at TIME          When: 15:30, YYYY-MM-DD 15:30, or ISO 8601 with offset
              --in DURATION      When, from now: 25m, 1h30m, 90s
              --ends TIME        An event's end
              --remind DURATION  How long before an event's start to remind (default 0)

          -h, --help             Show this help
              --version          Show the version

        Exit codes: 0 delivered (with --wait: a button was chosen), 1 usage error, 2 refused by BrimDeck,
        3 BrimDeck is not running or did not answer, 4 dismissed without a choice, 5 ran out of time.
        "run" exits with the program's own code.
        """.ReplaceLineEndings();

    // source: the value of BRIM_SOURCE, or null.
    public static NotifyCommand Parse(IReadOnlyList<string> args, string? source = null)
    {
        if (args.Count == 0) return new(NotifyCommandKind.Help);
        source = string.IsNullOrWhiteSpace(source) ? DefaultSource : source.Trim();
        string? verb = args.Count > 1 ? args[1] : null;
        switch (args[0])
        {
            case "-h" or "--help" or "help": return new(NotifyCommandKind.Help);
            case "--version" or "version": return new(NotifyCommandKind.Version);
            case "--stdio": return args.Count == 1 ? new(NotifyCommandKind.Stdio) : Fail("--stdio takes no arguments");
            case "status":
                return args.Count == 1 ? new(NotifyCommandKind.Status)
                    : args is [_, "--json"] ? new(NotifyCommandKind.Status, JsonOutput: true)
                    : Fail("status takes only --json");
            case "run": return NotifyCommandLine.ParseOptions(NotifyCommandKind.Run, null, args, 1, source, "brim run");
            case "notify":
                return verb switch
                {
                    "send" => NotifyCommandLine.ParseOptions(NotifyCommandKind.Send, null, args, 2, source, "brim notify send"),
                    "close" => NotifyCommandLine.ParseOptions(NotifyCommandKind.Send, "close", args, 2, source, "brim notify close"),
                    _ => Verbs("notify", verb, "send", "close")
                };
            case "timer":
                return verb switch
                {
                    "start" => NotifyCommandLine.ParseOptions(NotifyCommandKind.Send, NoticeTimes.Countdown, args, 2, source, "brim timer start"),
                    "stop" => NotifyCommandLine.ParseOptions(NotifyCommandKind.Send, "stop", args, 2, source, "brim timer stop"),
                    _ => Verbs("timer", verb, "start", "stop")
                };
            case "stopwatch":
                return verb switch
                {
                    "start" => NotifyCommandLine.ParseOptions(NotifyCommandKind.Send, NoticeTimes.Stopwatch, args, 2, source, "brim stopwatch start"),
                    "stop" => NotifyCommandLine.ParseOptions(NotifyCommandKind.Send, "stop", args, 2, source, "brim stopwatch stop"),
                    _ => Verbs("stopwatch", verb, "start", "stop")
                };
            case "event":
                return verb switch
                {
                    "add" => NotifyCommandLine.ParseOptions(NotifyCommandKind.Send, NoticeTimes.Event, args, 2, source, "brim event add"),
                    "remove" => NotifyCommandLine.ParseOptions(NotifyCommandKind.Send, "stop", args, 2, source, "brim event remove"),
                    _ => Verbs("event", verb, "add", "remove")
                };
            default: return Fail("unknown command: " + args[0]);
        }
    }

    private static NotifyCommand Verbs(string group, string? given, string first, string second)
        => Fail(given is null or "-h" or "--help" ? $"{group} takes {first} or {second}: brim {group} {first} ..." : $"unknown command: {group} {given} ({group} takes {first} or {second})");

    private static NotifyCommand Fail(string error) => new(NotifyCommandKind.Error, Error: error);
}
