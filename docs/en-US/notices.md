# Live notices

When a notification arrives, the notch, capsule or indicator grows out of its place and shows the application's icon, title and summary; hover for about a quarter of a second or click to see the details, and a few seconds later it returns to its shape. By default Windows' own banners and notification center stay as they are and BrimDeck shows its own copy alongside; to see each notification only once, switch on "Hide banner" for an app (see below). Everything is processed on this PC, without any cloud service.

## Turning them on

Live notices have two switches, both off by default; notices are received once either is on:

- Settings › Collapsed view › Notices › Live notices: notices show on the island as they arrive.
- Settings › Expanded view › Show live notices: the open panel shows the notices page.

Settings › Live notices has two pages: "How notices show" sets how each of the three levels looks, and "Apps" sets which apps show notices and at which level. Then:

- The open panel gains a "Notice history" page (bell icon, with a red dot while there are notices you have not seen; a notice that popped up but that you did not hover, open or see on the page still counts as unseen). It lists the notices kept, where each can be read, opened in its application, copied or deleted; notices can be paused or cleared.
  - Notices of the same application with the same title are one conversation (a chat application's title is the chat's name): it takes one row in the list, showing its newest notice and how many it holds; the left side shows the newest, with the other messages under it. The delete button deletes the whole conversation's records.
  - A click on the icon or the title at the left, or on "Open app", opens the source application and, where possible, the conversation the notice belongs to: at the moment you click, BrimDeck reads that one notice's launch parameters (read-only) and hands them to the application; they are not saved or logged. If they cannot be read or the application does not support them, the application alone is opened.
  - The copy button copies the text (for a conversation with several messages, all of them in order of time); a right click on the text offers to copy the text or the title. A hidden verification code is not copied; use "Copy code" for it.
- Notices take the [panel theme](themes.md): they share the surface, gradient, scan lines, rim, shadow or glow and text colors of the notch, capsule, indicator and open panel. A glowing theme flashes once as a notice arrives. An application's color only marks a small dot, the progress ring and its initial, adjusted to read clearly on the theme.

## Arrival motion

Once a notice has grown to size, its surface moves like a viscous liquid for a moment and settles back to its shape:

- **Capsule**: both ends throw droplets and take them back as they refill; the glow along the top and bottom edges lights twice. The necks and droplets still joined to the body share its one continuous boundary (the rim, the gradient and the glass edge all follow it, with no line inside); a droplet that has parted has a boundary of its own.
- **Notch**: the two lower corners swell and the bulges run round the outline (always within 5 DIPs of it); the bulges are part of the body's outline, and the glow lights twice along that outline, thinning towards the shoulders.
- **Indicator bar**: no extra layer. The line first draws back a little (at most 5 DIPs), like a spring being loaded, and then grows into the card on the panel's opening spring; on the way back, once the card has returned to the line's place, it carries on a little past the line's length (at most 7 DIPs) and settles back. The size of the draw-back and the carry follows the weight of the notice.

The glow shows on light and dark desktops alike: behind the island a white backlight lies over a soft shade, and a line of light along the island's own edge flashes with it. On a long capsule or notch, long, low swells carry the liquid's motion from its ends to its middle.

How much of the motion plays follows the notice's level; see "Notice levels" below. The glow's and the droplets' colors and edges follow the [panel theme](themes.md); the pixel theme uses blocks. With animations off (BrimDeck's setting or Windows' own "animation effects") or in high contrast it does not play, and the notice is as it was; the capsule's and the notch's motion takes the display's frames only while it plays, about two seconds, and the indicator's is the panel's own animation; nothing otherwise. Quota alerts and track-change prompts are drawn by the island itself and do not play it.

Notices follow the "Scale" setting (100% / 150%): their size, corners, droplets, glow and the indicator's draw-back and carry grow with it, as the collapsed form and the open panel do.

The capsule's and the notch's liquid motion is the design "胶囊通知动效" provided for BrimDeck by the maintainer MoFeng2223 (<https://claude.ai/artifact/JU9bvJp5PPNY85vpGgUX6C>); the indicator bar's motion (the draw-back and the carry) and the adaptation to every kind of notice and every theme are BrimDeck's own implementation.

## Notice levels

Each notice shows on the island at one level:

| Level | What shows |
|---|---|
| 1 · Text | The island keeps its shape; only its content is replaced by the notice's words. The notice can take the whole island, or only the place of one part of the collapsed island (quota ring, music, system monitor, clock, file shelf, device batteries) while the other parts show as usual. The indicator, which has no room for words, lights up once. |
| 2 · Flash | As level 1, and the island's edge flashes: "Glow" lights the backlight twice and the island's outline keeps still; "Lively" adds the liquid ripples of the section above. The indicator breathes. |
| 3 · Stay | As level 2, and it stays until it is handled, playing again about every 3 seconds. The glow can be set to another color. |

There is also "List only": the notice only goes to the notice history and does not show on the island.

How each level looks is set under Settings › Live notices › How notices show: the default position of level 1, whether the app icon shows, how long levels 1 and 2 show, the effect of levels 2 and 3 (Glow or Lively), and the default color of level 3. "Try it" beside each level sends a sample notice.

When an app needs a different look, click the arrow beside its name under Settings › Live notices › Apps to open "Own looks", and change it level by level. That app's notices then follow its own settings, which come before the "How notices show" page and the look a message asks for. "Own looks" is closed by default and is seldom needed.

The level is the first of these that applies:

1. You set the app to "List only".
2. A call, a ringing alarm, an ended countdown, an event's reminder and an approval waiting for you are level 3.
3. The level you chose for the app.
4. The level its sender asked for (`-l` on the command line).
5. The default level: level 2, which you can change to level 1 or 3 on the "How notices show" page.

A notice that stays ends when:

- you click it, rest the pointer on it so the notices page opens, or open the panel;
- its source no longer has it: it was cleared from Windows' notification center (BrimDeck finds out the next time it reads the whole list), the alarm or countdown was stopped, the sender closed it with `brim-notify close` or sent the same `-r` key again at another level;
- it has shown for 3 minutes.

While it shows, other notices go to the notice history and wait as usual; a normal notice that waited over 30 seconds is not shown late. With animations off (in BrimDeck's settings or Windows' "Animation effects") or in high contrast, every level only shows the words.

## Sources

| Source | What it is | Default |
|---|---|---|
| Windows app notifications | The current user's notifications in Windows' notification center: source, icon, time and text. Only the apps in the list under Settings › Live notices › Apps show; other apps' notifications are not shown and do not go to the notice history. The list holds QQ and Clock by default; "Add app" adds apps that sent notifications recently or are registered for notifications in Windows. Messages inside applications are not read, and the notification center is never cleared or changed. | On |
| WeChat new messages | WeChat for PC does not send Windows notifications. While WeChat is in the "Apps" list, its flashing taskbar icon shows "WeChat has new messages"; the messages and their senders are not read. | In the list |
| brim-notify | `BrimDeck.Notify.exe` on Windows and `brim-notify` in WSL send notices directly. | On |
| WSL desktop notifications | Standard desktop notifications (freedesktop D-Bus, such as `notify-send`) in the chosen distributions. | Off, per distribution |

### Windows: event log mode and polling

As an ordinary desktop program BrimDeck gets no change events from Windows. Windows' push notification platform writes an entry to its log (Applications and Services Logs › Microsoft › Windows › PushNotification-Platform › Operational) for every notification delivered to the current user, and any user may subscribe to that log, so BrimDeck uses **event log mode** by default: about 0.1 s after an entry (a batch of entries is taken together) BrimDeck reads the one notification it names by its id, checks that the id and the application match, and shows it at once. This does not wait for a reading of the whole list, not even the first one after starting or waking (after waking, an entry logged before that reading begins counts as arrived during sleep). An entry without a usable id or application, or a notification that cannot be read or belongs to another application, is left to a full reading instead. The whole list is read in the background: after starting, being granted access or waking (notices already there are not shown again; those that arrived during sleep are recorded as missed), every 60 seconds as a safety check, and to learn which notices were cleared. A clear entry does not say which notice went, so that reading comes at most every 30 seconds; a notification Windows cannot return yet (Windows writes the log entry a little before the notification can be read) is looked up again every 0.1 seconds, up to 5 times; if it is still missing, or after an entry or a lookup that could not be trusted, that reading comes within about a second (unless a clear is already waiting). A lookup Windows does not answer within 5 seconds (seen after waking) counts as a failure: BrimDeck takes the notification listener again and reads the whole list.

When that log is turned off (by group policy, say), cannot be subscribed to or keeps failing, BrimDeck **polls** instead: every 2.5 seconds it checks for a new notification (about 2.5 ms of CPU) and reads the whole list every 15 seconds; a new notice usually appears within 0.2–2.6 seconds, at an idle cost of about 0.36% of one core. The status line in the settings shows which mode is in use.

Early test builds offered an identity package (`BrimDeck.Notices`) that needed Developer Mode, for an event mode; it is no longer needed, and the installer no longer manages it. If you registered it, remove it in PowerShell with `Get-AppxPackage BrimDeck.Notices | Remove-AppxPackage`, and turn Developer Mode off again if you do not need it otherwise.

If Windows has not yet decided whether BrimDeck may read notifications, the settings page shows "Allow access". A refusal can be changed in Windows Settings › Privacy & security › Notifications.

### Windows: turning off an app's own notification banners

Settings › Live notices › Apps › Hide banner (set per app, off by default). When it is on, BrimDeck turns off "Show notification banners" in Windows for that app, so its notifications show once, on the island; they still go to the notification center, where BrimDeck reads them.

- **What changes**: only the DWORD value `ShowBanner` (set to 0) under `HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\<app id>` of the current user, the value Windows Settings › System › Notifications › an app › "Show notification banners" writes. No administrator rights are needed and nothing is restarted.
- **Which apps**: only the apps on the "Apps" page with this switch on.
- **Which not**: BrimDeck itself; apps set to "List only" (their notifications show only in Windows); apps whose notifications are off in Windows; apps whose banner was already off. Sites in a browser share the browser's setting.
- **How it is undone**: before changing anything, BrimDeck records the previous state (no value, 0 or 1) in `%LOCALAPPDATA%\BrimDeck\notice-banners.json`. Switching this off, removing the app from the list or setting it to "List only", switching off live notices, Windows no longer letting BrimDeck read notifications, quitting or uninstalling BrimDeck restores it: a value that was not there is deleted, one that was is written back, and only where BrimDeck's 0 is still there. Anything BrimDeck could not restore after an unexpected exit is restored at its next start.
- **Your own changes**: if you turn an app's banner on again in Windows, BrimDeck keeps your choice and switches that app's "Hide banner" off; to turn the banner off again, switch "Hide banner" on again.
- **Sound**: only the banner goes, not the sound. Measured on Windows 11 26300: with `ShowBanner` at 0 no banner appears and the notification sound still plays; turn off "Play a sound" for an app in Windows Settings to silence it.
- **Note**: `ShowBanner` is not documented by Windows, and a later version may stop reading it. BrimDeck reads the value back after writing it and says so in the settings when Windows did not keep it; if banners still appear with this on, turn them off per app in Windows Settings › System › Notifications (the settings page has an "Open Windows notification settings" button).

### Windows: application names, icons and categories

The application id in a Windows notification does not always say which application sent it. BrimDeck handles these cases:

- **Web apps installed from Edge** (such as WhatsApp Web or Facebook): named after the site, for example "WhatsApp", not Microsoft Edge. Each site is a separate application with its own rules. A trailing line that repeats the site's address is dropped.
- **Site push notifications in Chrome and other browsers**: the browser adds the site's address as the last line. BrimDeck drops that line and tells sites apart, for example "Gmail · Google Chrome", so you can add one site alone or give it its own settings. Notifications with only two lines are left as they are.
- **Desktop programs without a Start menu shortcut**: the name and icon they registered are used (registry `HKCU\Software\Classes\AppUserModelId`; the icon only from a local file).
- **Common programs Windows gives no name for**: Bilibili, DingTalk, Gemini, classic Outlook and others take a name BrimDeck knows; Electron programs installed with Squirrel (Discord, Slack) are named after their program.
- **Tray balloons of older programs**: shown as "Tray app" when Windows gives no name, rather than a long number; desktop programs known only by their path are named after the program file.
- **Default categories**: Windows notifications carry no category. Notices from the Claude, Codex (ChatGPT) and Gemini desktop apps and Cursor are marked "Task"; Windows system notices, Windows Security, NVIDIA App, Adobe Creative Cloud, Clash Party/Clash Verge, Snipping Tool, Lenovo and Intel utilities and similar are marked "Info"; chat, mail and browsers stay "Message".
- **Calls**: a notice from a chat application (QQ, WeChat, Teams, WhatsApp, Discord and others) or Phone Link that is a voice or video call ("Incoming call", "is calling you", "邀请你进行语音通话"…) gets the call mark and shows at once like an alarm (unless the application is set to List only); a missed call does not count.
- **Replaced notifications**: when an application updates its notification (QQ gathering new messages into one, for example), Windows removes the old one and adds a new one. If both come from the same application with the same title in the same reading, BrimDeck updates the existing notice instead of keeping two.
- **Emoji**: WPF draws no color fonts, so emoji are monochrome; sequences joined from several emoji (a family, the rainbow flag) may show as a box, and flags as two letters. This is a platform limit; the text itself is unaffected.

### WSL

When you switch a distribution on under "WSL (off by default)", BrimDeck:

- copies its notice agent to `~/.local/share/brimdeck/brimdeck-agent` of the distribution's default user, with `bridge.json` and a version stamp. The agent is built for x64 and ARM64, each with glibc (Ubuntu, Debian, Fedora and most others) and with musl (Alpine); BrimDeck picks the build by the distribution's `uname -m` and program loader. A distribution none of them runs on (32-bit ARM, RISC-V, or a C library at another place) is shown as not supported in the settings and is not tried again for 30 minutes;
- adds `~/.local/bin/brim-notify`. It is updated only while its content is exactly what BrimDeck wrote; any other file of that name, or a symbolic link, is left alone and shown as a conflict in the settings (then use `~/.local/share/brimdeck/brimdeck-agent notify`). Every file is written to a new temporary file and renamed into place, so nothing is written through a link;
- starts the agent through `wsl.exe` only while the distribution **already runs**. A distribution is never started for notices, WSL's configuration is not changed and the system's `notify-send` is not replaced. WSL 1 and WSL 2 distributions both work;
- the agent is itself a `wsl.exe` session, and while it runs WSL does not stop the distribution or its virtual machine. So the agent looks once a minute: once the user has had no process in the distribution for 5 minutes other than the agent, WSL's init and relay processes and the session plumbing (`systemd --user`, `(sd-pam)`, `dbus-daemon`/`dbus-broker`) — terminals and background jobs both count — it ends by itself, and WSL stops the distribution and frees its memory as usual. The settings show that the agent ended. It connects again when the distribution has stopped and runs again; if the distribution keeps running for another reason, BrimDeck starts the agent again at most once every 30 minutes, never in a loop.

The agent looks for the session bus in this order: `DBUS_SESSION_BUS_ADDRESS`, `$XDG_RUNTIME_DIR/bus`, `/run/user/<uid>/bus` (both only when owned by the user), and `~/.dbus/session-bus/<machine-id>-<display>` (a bus X11 autolaunched where there is no systemd). While there is none it looks again every 3 seconds.

The agent adapts to the session bus:

| Situation | What happens |
|---|---|
| No notification service | The agent becomes the session's standard notification service; the application's buttons appear in the details and are passed back to it |
| Another service, watching allowed | The agent watches without replacing it; that service's buttons are not offered |
| Another service, watching not allowed | Shown as a limitation; `brim-notify` still works |
| No session bus | Shown as a limitation; `brim-notify` still works |

As the notification service the agent says `body-markup`: markup in a body shows as plain text (tags removed, `&lt;` and the like decoded), while titles always show as sent. Notifications with an `x-canonical-private-synchronous`, `synchronous` or `x-dunst-stack-tag` hint (volume, brightness) update one notice per application and tag; those with the `transient` hint show as usual but are not kept with the saved notices. An icon that is a symbolic link within `/usr/share/icons` or `/usr/share/pixmaps`, leading only within them, shows as the file it leads to (Debian and Ubuntu install many theme icons that way); any other link is still not read.

To remove it: switch the distribution off in settings, then delete `~/.local/share/brimdeck` and `~/.local/bin/brim-notify` in the distribution.

## The brim command

`brim` is BrimDeck's command line; notices are its first group of commands. On Windows it is `brim.exe` in BrimDeck's folder, and in a WSL distribution you switched on it is `~/.local/bin/brim`. It starts for each call, hands the message to the running BrimDeck and exits; while BrimDeck is not running it exits with code 3 after about 2 seconds and does not start BrimDeck.

Setup adds BrimDeck's folder to PATH (the current user's when installing for the current user, the machine's when installing for all users), so a newly opened terminal finds `brim`; uninstalling takes that entry out and leaves the rest of PATH as it is. A terminal opened before installing has to be opened again. The portable copy does not change PATH. While BrimDeck's folder is not in PATH, terminals, scripts and the hooks of AI tools do not find `brim` and have to call it by its full path ("Copy command line path" gives the full path of `brim.exe`). Settings › Live notices › Apps › Command line and programs then says "brim is not on PATH": "Add to PATH" adds it for the current user, and the same place says why when PATH could not be changed; the description beside the title has the steps to add it by hand (search the Start menu for "environment variables" › "Edit environment variables for your account" › Path under User variables › Edit › New), and "Copy folder" copies the folder to enter.

```text
# Show a notice
brim notify send "Build finished" "All tests passed"
# Name the source and the level (0 notices page only, 1 text, 2 flash, 3 stays)
brim notify send "Deployed" -s deploy-script -l 2
# The same -k key updates the same notice (a task's progress)
brim notify send "Deploying" -k deploy --progress 40
# Keep it until it is handled, then close it
brim notify send "Waiting for approval" -k deploy -l 3
brim notify close deploy
# Buttons, and wait for the choice: prints allow or deny
brim notify send "Run this command?" "dotnet test" -s "Claude Code" -k perm --action allow=Allow --action deny=Deny --timeout 5m
# Copy and open buttons are BrimDeck's to answer; nothing waits
brim notify send "GitHub code" "482913" --copy "Copy=482913" --open "Open page=https://github.com/login"
# A level 1 notice in the music part's place only, kept off the list
brim notify send "Switched to headphones" -l 1 --slot music --no-history
# Run a command, then show its result (exit code and time)
brim run -- dotnet test
# Check that BrimDeck receives notices
brim status
```

Options of `brim notify send`:

| Option | What it does |
|---|---|
| `-b/--body TEXT` | The body; it may also be the second argument |
| `-s/--source NAME` | The source's name; the environment variable `BRIM_SOURCE` names it for a whole script or hook. `brim` when not given. Settings › Live notices › Apps lists sources by this name, each with its own choices |
| `--context TEXT` | A second line beside the source: a project, a branch, a session |
| `-l/--level LEVEL` | `silent`, `plain`, `flash`, `persistent`, or `0` to `3`. The level you chose for the source comes first; how each level looks is decided under Settings › Live notices › How notices show |
| `-c/--category KIND` | `message`, `task`, `success`, `warning`, `error`, `info`. A notice that stays glows orange, red or green for `warning`, `error` and `success` |
| `--glow "#RRGGBB"` | The backlight of a notice that stays; it comes before the category's color and the default color of the settings |
| `--slot PLACE` | Where a level 1 notice shows: `island` (the whole island), `usage`, `music`, `monitor`, `clock`, `transfer`, `devices`. It comes before the default place of the settings; when the collapsed island has no such part, or is the indicator, the notice takes the whole island |
| `--align SIDE` | `left`, `center`, `right`: where the icon and the words sit on the island |
| `-i/--icon PATH` | A local icon file; `none` shows no icon. A relative path is from the current folder |
| `--image PATH` | A picture in the icon's place (an avatar) |
| `-k/--key KEY` | The same key updates the same notice; `brim notify close KEY` closes it (from the same source) |
| `--id ID` | Shown once however often it is retried |
| `--progress N` | A task's progress, 0 to 100 |
| `--action KEY=LABEL` | A button: when it is chosen, `brim` prints the key and exits. Such a button makes `brim` wait; the first is the main button |
| `--copy LABEL=TEXT` | A button that copies the text |
| `--open LABEL=TARGET` | A button that opens a web address (http, https), a local file or folder, or `app:<application id>`. From WSL only web addresses |
| `--wait` | Wait until the notice is handled, closed or runs out of time |
| `--timeout DURATION` | How long a notice that stays may stay, for example `5m`; 3 minutes by default, 30 at most |
| `--no-history` | Only show it on the island; do not list it among the recent notices |
| `--json FILE` | Read the whole message as JSON from a file, or from standard input with `-`; options given on the command line come over its values |

Where you gave a source its own looks (Settings › Live notices › Apps, the arrow beside its name), those come first: `--slot`, `--glow`, the category's color and `--icon none` then change nothing for it.

Buttons:

- At most 4. The island shows at most 2; the notices page shows them all. The island keeps its size, so one of the default width usually has room for the main button only; keep a label to a word or two, since a longer one is cut.
- BrimDeck decides whether the island shows buttons: it does when the panel opens on a click, or on hover with a delay of 0.5 seconds or more. Where resting the pointer opens the panel at once, it does not: the panel is open before a button could be reached, and the notices page has them. The indicator and a notice in one part's place show none.
- BrimDeck runs no command. When the target of `--open` is a file that would run (a program, a script, a shortcut), it is only selected in File Explorer. To have something done, wait with `--action` and let your own script do it.
- When BrimDeck recognizes a verification code (with "Hide verification codes" on) or can open the source application, it adds "Copy code" and "Open" itself.

Waiting for an answer (`--wait`, or any `--action`):

- The command keeps its connection until you choose a button, close or delete the notice, it runs out of time, or its sender closes it with `close`.
- Such a notice always stays until handled and is always listed on the notices page, unless you set its source to "List only" or "Don't show".
- Ending the command first (Ctrl+C) closes the notice too.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Delivered (or kept quiet by your rules); when waiting, a button was chosen and its key is on standard output |
| 1 | Usage error (a message over 64 KB even with its texts cut to BrimDeck's limits included) |
| 2 | Refused by BrimDeck (a `close` or `stop` that found no such key included) |
| 3 | BrimDeck is not running, live notices are off, or no answer came in time (the notice may still show) |
| 4 | When waiting: the notice was closed or deleted without a choice, or its source is set to "Don't show" |
| 5 | When waiting: it ran out of time |

`brim run` exits with the program's own code. Titles and bodies are cut to BrimDeck's limits before they are sent. A hook should fall back to its usual flow on exit code 3 rather than wait.

The JSON fields of a message match the options, for example:

```json
{
  "title": "Run this command?", "body": "dotnet test",
  "appName": "Claude Code", "context": "BrimDeck · mofeng",
  "level": "persistent", "category": "warning", "replaceId": "perm",
  "actions": [
    { "key": "allow", "label": "Allow", "style": "primary" },
    { "key": "deny", "label": "Deny" },
    { "key": "copy", "label": "Copy command", "type": "copy", "value": "dotnet test" }
  ],
  "timeoutMs": 300000
}
```

### The earlier name, brim-notify

`BrimDeck.Notify.exe` and `brim-notify` in WSL are still there and read their commands as before (`brim-notify "Title"`, `brim-notify close KEY`, `brim-notify timer 25m` and so on), with the same options as `brim`. New scripts should use `brim`.

## Time notices

Alarms, timers, countdowns, mail and event reminders are recognized and shown by kind:

| Kind | From | Shown |
|---|---|---|
| Alarm | Windows Clock's notifications for alarms, finished timers and finished focus sessions | High priority; "Open app" returns to Clock |
| Timer running (stopwatch) | `brim-notify stopwatch` | "Running 12:03", live; can be stopped |
| Countdown | `brim-notify timer` | "24:59 left" with a ring, live; shows again when it ends (high priority); can be cancelled or reminded again in 5 minutes |
| Alarm | Microsoft To Do reminders | As above |
| Mail | New-mail notifications of Outlook (new and classic), Mail and Calendar, Thunderbird, Foxmail and others; notifications of web mail in a browser (Gmail, Outlook on the web, QQ Mail, NetEase Mail and others); WSL desktop notifications with the `email` category | Ordinary notice |
| Event | Reminders of Microsoft 365 Calendar and Mail and Calendar; Outlook notifications that read as reminders ("In 15 minutes", "Now", or a line starting with "3:00 PM - 3:30 PM"); Teams meeting reminders and "meeting started"; Google Calendar reminders in a browser and web mail notifications that read as reminders; `brim-notify event` | High priority, "Starts in 14 min", "In progress"; for `brim-notify event` BrimDeck itself reminds before the start |

- Notices are sorted by the application's id (AUMID) and the notice's text, in memory on this PC; the original notification is not kept, and no mailbox or calendar is read. The sorting is heuristic: Outlook's and Teams' reminder wording varies by version and language, and what is not recognized shows as an ordinary notice.
- Clock is in the list under Settings › Live notices › Apps by default; Outlook, To Do, Teams, Calendar and similar apps need to be added there before their reminders show.
- Alarms, ended countdowns and event reminders are high priority, show at level 3 and are not affected by the "at most 3 in 10 seconds" limit, but at most 3 ring per minute from one source (each Windows application on its own; brim-notify, and WSL applications, each share one); the rest wait their turn, so a script that sets many timers at once does not hold back another application's alarm. An application you set to "List only" stays quiet as usual. While locked, presenting or paused they only count toward the summary. An event entered more than 10 minutes after its start is recorded as an ordinary notice. Windows notifications that arrived while the PC slept are recorded, not shown, on waking, except reminders raised in the last 10 minutes or for an event starting within 10 minutes either way, which still alert. At most 200 timers, events still to remind and running stopwatches are kept; beyond that, the one BrimDeck received first is dropped from the source holding the most (and the log says so), so other applications' reminders are not affected.
- The live words follow one shared second tick, which runs only while a running timer shows on the island or on the notice history page. Countdowns and events are worked out from the clock, again at once after waking or a change of the system time. A reminder missed by more than 10 minutes (asleep, BrimDeck not running) does not pop up; the list says "Ended at 15:30".
- Limits: Windows does not share the Clock app's running timers, stopwatch or alarm list, nor the snooze and dismiss buttons of its notifications, so a Clock alarm shows only when it rings and cannot be snoozed from BrimDeck; for a timer on the island use `brim-notify timer`. Calendar sync (reading Outlook's or Windows' calendar directly) is not supported yet; the applications' own reminder notifications are used.

```text
# count down 25 minutes, then remind (also 1h30m, 90s)
brim-notify timer 25m "Focus"
# count down to 15:30
brim-notify timer --at 15:30 "Meeting"
# a stopwatch showing the time elapsed
brim-notify stopwatch "Build" -r build
# stop the stopwatch, or cancel a countdown or event (same -s source as sent)
brim-notify stop build
# an event: reminds 10 minutes before and at its start
brim-notify event "Weekly" --at 15:00 --remind 10m --ends 16:00
# mark a plain notice as mail (or alarm)
brim-notify "New mail" --kind mail
```

- Times: `15:30` (the next 15:30) or a date and time such as `YYYY-MM-DD 15:30`, read in **Windows' time zone** (also from a WSL distribution set to another zone); or ISO 8601 with an offset, such as `YYYY-MM-DDT15:30:00+08:00`. A local time skipped by a daylight saving change moves to just after the change; a local time it repeats means the earlier of the two.
- Durations: `90s`, `25m`, `1h30m`, `2d`; countdowns from 1 second to 31 days, reminders at most 7 days ahead of the start, times from 1 day ago to 1 year ahead. A duration counts from when BrimDeck receives it, whatever the WSL clock says.
- Without `-r` a key is made up and printed on standard output, for `brim-notify stop KEY`; sending again with the same `-r` starts it over.
- Countdowns and events still running continue after a restart while the notice history is not set to "Until exit"; an event not reminded yet is not removed by the retention.

## How notices show

- A notice never takes the keyboard focus, and never pops up over the open panel or details you are using; while the panel is open, the bell only gets its red dot.
- Levels 1 and 2 stay 6 seconds by default, each changeable on the "How notices show" page; hovering, opening the details or working it from the keyboard pauses the time.
- A new notice of a conversation (the same application and title; a QQ notification's title is the conversation's name; without a title, the application) joins the one of that conversation that shows or waits on the island: it shows the latest words and a count. A merged notice stays at most twice its duration.
- A notice of another conversation shows at once: it takes the island over if the notice there has been up for 1.5 seconds, and otherwise as soon as it has (an alarm or a call does not wait). The notice it replaces is not shown again; it stays in the notice history. Several that wait take over in the order they arrived. A normal notice waits for a high priority one to end; a notice that stays until handled and one the pointer rests on are not replaced. A normal notice that waited over 30 seconds is not shown late.
- At most 3 notices per 10 seconds play their motion; one beyond that still shows at once, but at level 1 (no light, no ripples).
- The icon and the words sit in the middle when they fit (the sender can place them at the left or right); longer words start at the left and scroll.
- Notices from brim-notify and WSL are judged by when their sender wrote them: a message that had already waited over 30 seconds (5 minutes for high priority) when it was sent only goes to the notice history, unviewed, and does not pop up. The sender measures the wait by its own clock, so a WSL clock that differs from Windows' does not mislead it; time-kind notices follow their own reminder times. The same message (same event id) delivered again after BrimDeck restarts is neither recorded twice nor shown while the notice history still hold it.
- A notice updated with `-r` takes the time of the latest message: it pops up as "Just now", not with the time of the notice it replaced.
- A notice that arrives under a resting pointer does not open its details by itself: move away and back, or click.
- A click on a notice on the island opens the notices page, with that notice at the top.
- Order: what you are working with > high priority notices > other notices > quota alerts > track changes. Alarms, ended countdowns and event reminders are high priority (see "Time notices").
- Nothing is shown while the session is locked or switched, while the panels are hidden or hidden by a full-screen rule. During a Windows focus session, presentation mode and BrimDeck's own pause nothing pops up; afterwards one summary shows how many arrived. Windows' separate "Do not disturb" switch has no public interface and is not detected; use BrimDeck's pause instead.
- Several monitors: notices show on the screen in use (the one with the foreground window; the default), on the main panel's screen, or on every screen that shows BrimDeck. With the first two, a notice stays in the notice history if that screen's panel is hidden; it does not jump to another screen. With every screen, the same notice shows once on each and counts once toward the limit; handling it on one screen (a click, opening the notices page) ends it on the others too.
- Settings › Shortcuts › Open live notices (unset by default): with a notice on screen it opens the details for the keyboard (Tab to move, Enter to choose, Esc to close); otherwise it opens the notice history.

## Privacy

- Notice history is kept for 24 hours by default, at most 500, encrypted with Windows' per-user data protection (DPAPI) in the `notices` folder of BrimDeck's install folder (opened to the current user only; `%LOCALAPPDATA%\BrimDeck\notices` when the install folder cannot be written to). "Keep for" can be set to Until exit (memory only: then no application icons are kept on disk either), 1 hour, 1 day, 3 days or 7 days, and the list can be cleared at any time. A reminder that rang or a stopwatch that stopped is kept for the retention from that moment.
- The folder the notices are saved in is neither read nor written when it is a link, belongs to another account, or lets other accounts change its files, and the settings say why; for the last, "Make it mine only" in the settings opens the folder to the current user and the system only. Before every read and write BrimDeck checks that neither the folder nor `notices.dat` is a link, and a `notices.dat` that is a link is never read. When the saved file cannot be reached for now (in use, its drive or network location not available), BrimDeck tries again later and writes nothing until then, so the full list is never overwritten; notices deleted or cleared meanwhile do not come back once the file is read. A file that is actually damaged offers to be cleared; one this account may not read (made by BrimDeck running as administrator, say) offers both trying again and clearing. Every save is on the disk itself before it replaces the old file, so a power cut does not leave a damaged file.
- Deleting or clearing removes BrimDeck's own records only and tells no source. Only closing a notice (its close button or Esc) reports a WSL desktop notification as dismissed by the user, as closing a desktop notification bubble does. Switching notices off or clearing also cancels what shows and everything waiting to show.
- Raw notification content, attachments and pictures are never saved. Icons come only from Windows or local files; nothing is downloaded. Network paths, links and shortcuts as icons are refused.
- The diagnostic log `notices.log` holds only error types, codes and code locations, never titles or bodies.
- The local channel accepts only the current user on this PC (a named pipe that refuses network access). A notice's content is only ever shown as text: it is never run and its links are never opened.
- Only the Windows apps in the list on the "Apps" page show notices; each can be given a level (including "List only"), "Hide banner" for its own banner, and its own looks. Sources from the command line and programs are listed automatically after they first send, and can be set to "Don't show".
- Verification codes (Settings › Live notices › How notices show › Hide verification codes, off by default): when on, BrimDeck recognizes a code in a notice's title and body on this PC (a Chinese or English keyword with digits or a short letter-and-digit code nearby; order numbers, amounts, phone numbers, dates and links do not count) and hides it with dots on the island and the notices page; a screen reader hears "verification code hidden". The notices page gets a "Copy code" button. Recognition goes nowhere online and into no log; the clipboard is written only when that button is clicked, marked so that Windows keeps it out of the clipboard history and cloud sync, and emptied after 60 seconds if it still holds that code (left alone if something else was copied). The encrypted notice history keeps the hidden text, so a code cannot be copied after a restart; switching the setting off restores everything. The recognition rules are ported from xiaotang-daozai (MIT, see ThirdPartyNotices.txt) and prefer missing a code to hiding something that is not one.

## Compatibility (tested)

| Case | Result |
|---|---|
| Windows 11 (26300) | Snapshot readable; change events unavailable; event log mode reads by TrackingId and reconciles the full list periodically and after dismissals; polling remains the fallback |
| Icons of Windows notifications | Packaged apps give their icon; otherwise the application's initial |
| Opening the source application | Packaged apps through the app list; desktop apps by the id registered with their Start menu shortcut; otherwise the details say so |
| WSL Ubuntu 26.04 (systemd session bus, no notification service) | The agent serves; gdbus/notify-send notices show; buttons send `ActionInvoked` and `NotificationClosed` back |
| WSL with another notification service | Watched, with correct notification ids and closes |
| Several monitors (negative coordinates, 125% secondary) | Shows on the foreground window's monitor, placed and scaled correctly |
| 8 built-in themes × notch, capsule, indicator | Each checked by eye |

Cases not tested here (actual behavior on the lock screen) follow the design and may need confirming on your system.
