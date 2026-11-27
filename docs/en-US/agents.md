# AI coding tools

With hooks installed in the AI coding tools on this PC, BrimDeck can:

- Show each session's state on the collapsed notch or capsule: working, waiting for approval, waiting for an answer, done.
- Send a notice when a turn finishes, and a notice that stays on the island when a tool needs approval or asks a question.
- Let you allow or deny an action, or answer the tool's question (pick options, or type your own answer), right on the island or the notices page.

Everything stays on this PC: the hooks hand the session state to BrimDeck over a local named pipe, never over the network. The hooks of Claude Code, CodeAgent and Qoder name the file the session is recorded in; BrimDeck reads only what is added to it, on this PC, to learn that you refused an action or stopped the turn in the tool.

## Installing the hooks

1. Open Settings › Live notices › AI coding tools.
2. Choose "Find installed tools". BrimDeck looks for each tool's settings folder and program and lists the tools it finds.
3. Choose "Install" or "Remove" for one tool, or "Install all" and "Remove all" for every tool found.

Apps that share one settings file count as one tool. For example, the Claude Code CLI, the Claude desktop app and the IDE extensions all read `%USERPROFILE%\.claude\settings.json`, so installing once covers them all.

Installing changes the tool's user settings file:

- Only BrimDeck's own entries are added; their command is `brim.exe hook ...` in BrimDeck's folder. The file's other settings and hooks stay as they are.
- Before the first change, a copy is kept beside the file as `<file name>.brimdeck-backup`.
- Removing deletes only the entries BrimDeck added.
- A file that is not valid JSON is left as it is, and the settings page says so.
- After BrimDeck moves to another folder, the list shows "Hooks need an update"; choose "Update".

## Supported tools

| Tool | Settings file | What the island can do |
|---|---|---|
| Claude Code (CLI, desktop app, IDE extensions) | `%USERPROFILE%\.claude\settings.json` (or `CLAUDE_CONFIG_DIR`) | State, finished notices, approvals (allow, deny, always allow), answering questions |
| Codex (CLI, desktop app) | `%USERPROFILE%\.codex\hooks.json` (or `CODEX_HOME`) | State, finished notices, approvals (allow, deny; needs its switch) |
| Antigravity (desktop app, IDE, CLI) | `%USERPROFILE%\.gemini\config\hooks.json` | State, finished notices; a reminder for a question |
| Cursor (editor, CLI) | `%USERPROFILE%\.cursor\hooks.json` | State, finished notices |
| CodeAgent CLI | `%USERPROFILE%\.cac\settings.json` (or `CODEAGENT3_CONFIG_DIR`) | State, finished notices, approvals, answering questions (needs its switch) |
| Qoder (CLI, desktop app) | `%USERPROFILE%\.qoder\settings.json` | State, finished notices, approvals, answering questions (the desktop app needs its switch) |
| Qoder CN (CLI, desktop app) | `%USERPROFILE%\.qoder-cn\settings.json` | State, finished notices, approvals, answering questions (the desktop app needs its switch) |
| WorkBuddy | `%USERPROFILE%\.workbuddy\settings.json` | State, finished notices, approvals (allow, deny; needs its switch) |
| ZCode (its own agent) | `%USERPROFILE%\.zcode\cli\config.json` | State, finished notices, approvals, answering questions (needs its switch) |
| OpenCode (CLI, desktop app) | Plugin `%USERPROFILE%\.config\opencode\plugins\brimdeck.js` | State, finished notices, approvals (once, always, reject); answering questions in 1.x, a reminder for a question in 2.x |
| MiMo Code (CLI) | Plugin `%USERPROFILE%\.config\mimocode\plugins\brimdeck.js` | State, finished notices, approvals, answering questions |
| Command Code (CLI, desktop app) | Mod `%USERPROFILE%\.commandcode\mods\brimdeck.ts` | State, finished notices, approvals (allow, deny), answering questions (both need their switch) |
| Kimi Code (desktop app, CLI) | `%USERPROFILE%\.kimi-code\config.toml` (or `KIMI_CODE_HOME`) | State, finished notices; a reminder when it waits for approval or an answer |
| TRAE (with TRAE SOLO) | `%USERPROFILE%\.trae\hooks.json` | State, finished notices; a reminder when it waits for approval or an answer |
| TRAE CN (with TRAE SOLO CN) | `%USERPROFILE%\.trae-cn\hooks.json` | State, finished notices; a reminder when it waits for approval or an answer |

"Needs its switch" is explained under "Approvals and questions" below.

Notes:

- **Claude Code** asks a question through its approval hook, as it asks for an approval: Claude Code's own prompt shows as usual, the island can answer too, and the first answer counts.
- **Codex** runs only hooks you have reviewed. After installing, enter `/hooks` in Codex (in the desktop app, Settings › Hooks) and trust BrimDeck's hooks before they run. Changed hooks (after "Update", for example) need another review.
- **Antigravity**'s desktop app, IDE and CLI read one hooks file, so they count as one tool. Its hooks have no approval event, so approvals stay in Antigravity; for a question the island shows a reminder, which closes once you answered in Antigravity.
- **Cursor** hooks have no approval event of their own, so Cursor's approvals stay in Cursor.
- **Cursor** also runs the hooks in Claude Code's settings file by default. BrimDeck shows a Cursor session only from Cursor's own hooks, so install the hooks of the Cursor row to see its sessions.
- **CodeAgent, Qoder and Qoder CN** use Claude Code's hook format; BrimDeck installs and answers in that format. When the Qoder and Qoder CN CLIs ask a question, their own prompt and the island both work; the desktop apps and CodeAgent show no question of their own while the island waits. The Qoder IDE's hooks behave differently from the desktop app's and the CLI's and are not adapted yet.
- **WorkBuddy**'s hooks cannot add an "always allow" rule or answer a question (the question's hook runs only after you answered), so the island offers Allow and Deny, and questions stay in WorkBuddy. By default WorkBuddy runs commands without asking for approval, and then no approval shows on the island.
- **OpenCode** has no command hooks. BrimDeck writes the plugin `brimdeck.js` into its plugin folder, which OpenCode loads when it starts; removing deletes the file. Restart OpenCode after installing. OpenCode's own prompt and the island's buttons work at the same time and the first answer counts; answered in OpenCode first, the notice on the island closes. The same file serves OpenCode 2.x and 1.x from 1.18.29. Whether the OpenCode desktop app loads this plugin is not verified yet. A plugin of OpenCode 2.x cannot answer a question: the island shows a reminder for one, which closes once you answered in OpenCode.
- **MiMo Code** is built on OpenCode 1.x and is connected with the same plugin. The Xiaomi MiMo desktop app empties its own plugin folder at each start and loads no user plugins, so only sessions of the MiMo Code CLI show.
- **Command Code**'s command hooks have no approval event, so BrimDeck writes a mod, `brimdeck.ts`, which the CLI and the desktop app load in new sessions; removing deletes the file. Command Code waits for the mod before it shows its own prompt, so by default the island only tells of its approvals and questions; with the switches on they wait on the island first, and handed back, Command Code asks as it always does. A prompt Command Code forces for safety takes no "allow" from a mod and is always confirmed in Command Code.
- **Kimi Code**'s hooks are `[[hooks]]` tables at the end of `config.toml`; restart Kimi Code after installing. Its hooks decide nothing: when it needs approval or asks a question, the island shows a reminder that stays, and closes it once you answered in Kimi Code.
- **TRAE and TRAE CN** each have a `hooks.json` of their own and are installed separately. After installing, turn on the configured global hooks in TRAE's Settings › Hooks, and change how hook commands run from the default, the sandbox, to running locally: in the sandbox the hooks do not run, and nothing says so. A hook cannot answer TRAE's own prompt: while TRAE waits for approval or an answer, the island shows a reminder that stays, and closes it once you answered in TRAE. When you stop a turn in TRAE, no hook runs: the session keeps showing as working, and is dropped after 30 minutes without a message.
- **ZCode**'s settings-file hooks are off by default. Installing turns on `enabled` in that file's `hooks`; removing takes it out again only when no other hooks are left there. Installing covers ZCode's own agent; the built-in agents you can pick in ZCode, such as Claude Code and Codex, use settings of their own and are not affected.
- Codex, Antigravity and Cursor have no questions a hook can answer; their questions are answered in the tool.
- Codex, Antigravity and Kimi Code run a hook as a command line. Where BrimDeck's folder has a space in its path, its short path is written (`C:\PROGRA~1\BrimDeck\brim.exe`, for example), which these tools run correctly.

These tools are not supported yet, and looking for tools does not list them:

| Tool | Why |
|---|---|
| DeepSeek Harness | No command hooks; only plugins added to its startup configuration |
| QwenWork, QwenWork CN | User hooks run only with some of its engines, and a hook cannot answer an approval |
| WorkBuddy AI | Reads no hooks from its settings file; only its own plugins |

## Approvals and questions

When a tool asks for approval or asks a question, a notice that stays appears on the island, and the session shows as "Approval" or "Question" on the collapsed row. Where you answer depends on whether the tool shows its own prompt while its hook waits.

### The tool and the island both answer

This covers the approvals of Claude Code, CodeAgent, Qoder, Qoder CN, ZCode, OpenCode and MiMo Code, and the questions of Claude Code, the Qoder and Qoder CN CLIs and MiMo Code. The tool's own prompt shows as usual, the island can answer too, and the first answer counts:

1. Hover over or click the island to open the notices page. An approval shows the command to run or the file to change, with "Allow", "Deny", "Always allow" and "Use the terminal"; a question shows each question with its options, and a box below for your own answer.
2. Once you answer on the island, the tool goes on at once.
3. Answered in the tool first, the notice on the island closes by itself. For most tools that is when the action has run, you send a new prompt or the turn ends; when you refuse or stop in Claude Code, CodeAgent or Qoder, BrimDeck learns it from the session's record and closes the notice within seconds.

### The tool shows no prompt while the island waits

This covers the approvals of Codex and WorkBuddy, the approvals and questions of Command Code, and the questions of CodeAgent, the Qoder and Qoder CN desktop apps and ZCode. These tools wait for the hook to return before they show their own prompt.

- By default the island only tells, without buttons or options; the tool shows its own prompt as usual, and the reminder closes once you answered there.
- With the tool's "approve on the island" or "answer questions on the island" switched on in Settings › Live notices › AI coding tools › Approvals and questions, you answer on the island instead, and the tool shows no prompt of its own meanwhile. The tool asks as it always does when:
  - The time set in "Wait on the island" runs out (5 minutes by default).
  - You close the notice, or choose "Use the terminal" or "Answer in the terminal".
  - BrimDeck is not running. The hook then ends at once, and the tool does not wait.

For Codex and WorkBuddy this comes from reading their code; it has not been checked in a real session yet.

### Reminders only

The approvals and questions of Kimi Code, TRAE and TRAE CN, and the questions of Antigravity and OpenCode 2.x, cannot be decided by a hook: the island shows a reminder that stays, and closes it once you answered in the tool.

## Collapsed row

The notch and the capsule's "Live notices" part shows the AI coding tools' sessions too by default: a round icon for each active session, the tool's own program icon (its first letter when its desktop app is not installed); a tool with several sessions shows its icon once for each, at most 3 icons in all, the session that most needs you at the right. Each icon's ring shows its own session's state: turning blue while it works; yellow, with a small shake and a "!" or "?", while it waits for an approval or an answer; green with a tick once done. The words after them say what the sessions are doing: how long the only working session has run, or how many work or wait. Nothing shows without an active session. Clicking it opens the notices page.

You can switch "Live notices" off, or drag it to another place, in Settings › Collapsed view › Shown content; under Settings › Live notices › How notices show › Live notices you can switch off "Show AI coding tools", or switch on "Stack as one group" to stack the sessions' icons with the unread notices' app icons in one group. A finished session shows for 10 minutes; a working session that sends nothing for 30 minutes counts as ended.

## Connecting other tools

Tools that are not listed can be connected too. The "Other tools" section of the settings page gives the commands with the full path of `brim.exe`, ready to copy.

### Tools whose hooks work like Claude Code's

If the tool's hooks receive JSON on standard input with `hook_event_name` and `session_id`, and use Claude Code's event names (`SessionStart`, `UserPromptSubmit`, `PostToolUse`, `Stop`, `PermissionRequest`, `PreToolUse` and so on), use this as its hook command:

```
"C:\Program Files\BrimDeck\brim.exe" hook claude --name "Tool name"
```

- `--name` is the name BrimDeck shows; `--id` sets an id of its own, made from the name when not given.
- Approvals (`PermissionRequest`) and questions (`AskUserQuestion` in `PreToolUse`) wait for an answer, so give those hooks a timeout of 30 minutes or more; the other events can run in the background.
- Whether the tool accepts the approval and the answers the hook returns depends on the tool.

### Any other tool: report the state from a script

From the tool's hooks, plugins or your own scripts, run:

```
brim agent report --tool "Tool name" --session SESSION_ID --state working --project PROJECT
```

`--state` takes:

| Value | Meaning |
|---|---|
| `started` | The session started |
| `working` | Working |
| `approval` | Waiting for approval |
| `question` | Waiting for an answer |
| `done` | The turn finished |
| `idle` | Idle, waiting for the next prompt |
| `ended` | The session ended; it leaves the collapsed row |

Optional: `--title` for the current task, `--message` for the last reply.

To approve on the island, send a notice with buttons that waits for the result, and go on by the key it prints:

```
brim notify send "Wants to run a command" -b "npm test" -s "Tool name" --action allow=Allow --action deny=Deny --timeout 5m
```

With exit code 0, standard output holds the key of the chosen button; with 4 (closed) or 5 (timed out), go back to the tool's own flow. See `brim --help` for every option.
