# AI coding tools

With hooks installed in the AI coding tools on this PC, BrimDeck can:

- Show each session's state on the collapsed notch or capsule: working, waiting for approval, waiting for an answer, done.
- Send a notice when a turn finishes, and a notice that stays on the island when a tool needs approval or asks a question.
- Let you allow or deny an action, or answer the tool's question (pick options, or type your own answer), right on the island or the notices page.

Everything stays on this PC: the hooks hand the session state to BrimDeck over a local named pipe, never over the network.

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
| Codex (CLI, desktop app) | `%USERPROFILE%\.codex\hooks.json` (or `CODEX_HOME`) | State, finished notices, approvals (allow, deny) |
| Antigravity (desktop app, IDE, CLI) | `%USERPROFILE%\.gemini\config\hooks.json` | State, finished notices |
| Cursor (editor, CLI) | `%USERPROFILE%\.cursor\hooks.json` | State, finished notices |
| CodeAgent CLI | `%USERPROFILE%\.cac\settings.json` (or `CODEAGENT3_CONFIG_DIR`) | State, finished notices, approvals, answering questions |
| Qoder (CLI, desktop app) | `%USERPROFILE%\.qoder\settings.json` | State, finished notices, approvals, answering questions |
| Qoder CN (CLI, desktop app) | `%USERPROFILE%\.qoder-cn\settings.json` | State, finished notices, approvals, answering questions |
| WorkBuddy | `%USERPROFILE%\.workbuddy\settings.json` | State, finished notices, approvals (allow, deny) |
| WorkBuddy AI | `%USERPROFILE%\.workbuddy-ai\settings.json` | State, finished notices, approvals (allow, deny) |
| ZCode (its own agent) | `%USERPROFILE%\.zcode\cli\config.json` | State, finished notices, approvals, answering questions |
| OpenCode (CLI, desktop app) | Plugin `%USERPROFILE%\.config\opencode\plugins\brimdeck.js` | State, finished notices, approvals (once, always, reject); answering questions in 1.x |
| MiMo Code (CLI) | Plugin `%USERPROFILE%\.config\mimocode\plugins\brimdeck.js` | State, finished notices, approvals, answering questions |
| Command Code (CLI, desktop app) | Mod `%USERPROFILE%\.commandcode\mods\brimdeck.ts` | State, finished notices, approvals (allow, deny), answering questions |
| Kimi Code (desktop app, CLI) | `%USERPROFILE%\.kimi-code\config.toml` (or `KIMI_CODE_HOME`) | State, finished notices; a reminder when it waits for approval or an answer |
| TRAE (with TRAE SOLO) | `%USERPROFILE%\.trae\hooks.json` | State, finished notices; a reminder when it waits for approval or an answer |
| TRAE CN (with TRAE SOLO CN) | `%USERPROFILE%\.trae-cn\hooks.json` | State, finished notices; a reminder when it waits for approval or an answer |

Notes:

- **Codex** runs only hooks you have reviewed. After installing, enter `/hooks` in Codex (in the desktop app, Settings › Hooks) and trust BrimDeck's hooks before they run. Changed hooks (after "Update", for example) need another review.
- **Antigravity**'s desktop app, IDE and CLI read one hooks file, so they count as one tool. Its hooks have no approval or question events; approvals and questions stay in Antigravity.
- **Cursor** hooks have no approval event of their own, so Cursor's approvals stay in Cursor.
- **Cursor** also runs the hooks in Claude Code's settings file by default. BrimDeck shows a Cursor session only from Cursor's own hooks, so install the hooks of the Cursor row to see its sessions.
- **CodeAgent, Qoder and Qoder CN** use Claude Code's hook format; BrimDeck installs and answers in that format.
- **WorkBuddy and WorkBuddy AI** each have a settings file of their own and are installed separately. Their hooks cannot add an "always allow" rule or answer a question (the question's hook runs only after you answered), so the island offers Allow and Deny, and questions stay in WorkBuddy.
- **OpenCode** has no command hooks. BrimDeck writes the plugin `brimdeck.js` into its plugin folder, which OpenCode loads when it starts; removing deletes the file. Restart OpenCode after installing. OpenCode's own prompt and the island's buttons work at the same time and the first answer counts; answered in OpenCode first, the notice on the island closes. The same file serves OpenCode 2.x and 1.x from 1.18.29. A plugin of OpenCode 2.x cannot answer a question, so questions stay in OpenCode there.
- **MiMo Code** is built on OpenCode 1.x and is connected with the same plugin. The Xiaomi MiMo desktop app empties its own plugin folder at each start and loads no user plugins, so only sessions of the MiMo Code CLI show.
- **Command Code**'s command hooks have no approval event, so BrimDeck writes a mod, `brimdeck.ts`, which the CLI and the desktop app load in new sessions; removing deletes the file. Approvals and questions wait on the island first; handed back, Command Code asks as it always does. A prompt Command Code forces for safety takes no "allow" from a mod and is always confirmed in Command Code.
- **Kimi Code**'s hooks are `[[hooks]]` tables at the end of `config.toml`; restart Kimi Code after installing. Its hooks decide nothing: when it needs approval or asks a question, the island shows a reminder that stays, and closes it once you answered in Kimi Code.
- **TRAE and TRAE CN** each have a `hooks.json` of their own and are installed separately. After installing, turn on global hooks in TRAE's Settings › Hooks, or the hooks do not run. A hook cannot answer TRAE's own prompt: while TRAE waits for approval or an answer, the island shows a reminder that stays, and closes it once you answered in TRAE. TRAE runs hooks in a sandbox by default; whether the sandbox lets a hook reach BrimDeck is not verified yet. If no session shows, switch the hooks to run locally in TRAE's hook settings.
- **ZCode**'s settings-file hooks are off by default. Installing turns on `enabled` in that file's `hooks`; removing takes it out again only when no other hooks are left there. Installing covers ZCode's own agent; the built-in agents you can pick in ZCode, such as Claude Code and Codex, use settings of their own and are not affected.
- Codex, Antigravity and Cursor have no questions a hook can answer; their questions are answered in the tool.
- Codex, Antigravity and Kimi Code run a hook as a command line. Where BrimDeck's folder has a space in its path, its short path is written (`C:\PROGRA~1\BrimDeck\brim.exe`, for example), which these tools run correctly.

These tools can be found but not yet installed automatically:

| Tool | Why |
|---|---|
| DeepSeek Harness | No command hooks; only plugins added to its startup configuration |
| QwenWork, QwenWork CN | User hooks run only with some of its engines, and a hook cannot answer an approval |

## Approvals and questions

When a tool asks for approval or asks a question:

1. A notice that stays appears on the island, and the session shows as "Approval" or "Question" on the collapsed row.
2. Hover over or click the island to open the notices page. An approval shows the command to run or the file to change, with "Allow", "Deny", "Always allow" and "Use the terminal"; a question shows each question with its options, and a box below for your own answer.
3. Once you answer, the tool goes on at once.

While it waits, the terminal shows no prompt. The terminal asks as it always does when:

- The time set in Settings › Live notices › AI coding tools › Wait on the island runs out (5 minutes by default).
- You close the notice, or choose "Use the terminal" or "Answer in the terminal".
- BrimDeck is not running. The hook then ends at once, and the tool does not wait.

In the Claude desktop app and the IDE extensions, the app's own prompt and the island's buttons work at the same time; the first answer counts.

## Collapsed row

The notch and the capsule show the "AI coding tools" part by default: a mark with the tool's first letter for each active session, with a dot at its lower right for the state (yellow: waiting for you; blue: working; green: done), and the state of the session in front in words. Nothing shows without an active session. Clicking it opens the notices page.

You can switch the part off, or drag it to another place, in Settings › Collapsed view › Shown content. A finished session shows for 10 minutes; a working session that sends nothing for 30 minutes counts as ended.

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
