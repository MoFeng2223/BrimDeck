using System.Text.Json;

namespace BrimDeck.Core.Agents;

// The mod BrimDeck writes into Command Code's mods folder (%USERPROFILE%\.commandcode\mods\brimdeck.ts), which the Command Code
// CLI and desktop app both load for every new session. Command Code's command hooks know four events and no approval, so the
// mod is used instead: it hands the session's events to "brim hook claude" in Claude Code's words.
//
// - onSessionStart gives the session's id; transformInput the prompt; tool_running, onStop, interrupted and run_error the work
//   and its end; onSessionEnd the end of the session.
// - onPermissionRequest is asked before Command Code shows its own prompt, and Command Code waits for it: "allow" or "deny"
//   answers the prompt, anything else lets Command Code ask as usual. A prompt Command Code forces for safety takes no
//   "allow" from a mod, so it is left to Command Code.
// - ask_user_question is answered in beforeToolCall with the tool's result, in the words Command Code itself gives the model:
//   one line "question → answer" for each question, then its guidance line.
public static class CommandCodeMod
{
    public const string FileName = "brimdeck.ts";
    // The first line of the file: how BrimDeck knows the file is its own, to update or delete it.
    public const string Marker = "// BrimDeck mod for Command Code";

    public static readonly AgentPlugin Plugin = new(Marker, Text);

    public static string Text(string brim) => Marker + "\n" + Template.Replace("__BRIM__", JsonSerializer.Serialize(brim)).ReplaceLineEndings("\n");

    private const string Template = """
        // Written by BrimDeck (Settings › Notices › AI coding tools); removing the hooks there deletes this file.
        // Shows Command Code's sessions on the BrimDeck island, and lets approvals and questions be answered there.
        import { spawn } from "node:child_process"

        const BRIM = __BRIM__
        const ARGS = ["hook", "claude", "--name", "Command Code", "--id", "commandcode"]

        // Hands one event to brim and gives back what it prints.
        function hook(input) {
          return new Promise((resolve) => {
            let out = ""
            let child
            try {
              child = spawn(BRIM, ARGS, { windowsHide: true, stdio: ["pipe", "pipe", "ignore"] })
            } catch {
              resolve("")
              return
            }
            child.stdout.on("data", (data) => { out += data })
            child.on("error", () => resolve(""))
            child.on("close", () => resolve(out))
            child.stdin.on("error", () => {})
            child.stdin.end(JSON.stringify(input))
          })
        }

        export default function (cmd) {
          let session = ""

          const send = (event, more) => session ? hook({ session_id: session, cwd: cmd.cwd, hook_event_name: event, ...more }) : Promise.resolve("")

          // What brim printed for an approval or a question answered in BrimDeck.
          const answer = async (event, more) => {
            try { return JSON.parse(await send(event, more)).hookSpecificOutput } catch { return undefined }
          }

          cmd.hooks({
            onSessionStart: ({ source, sessionId }) => {
              if (sessionId) session = sessionId
              if (source !== "reload") void send("SessionStart")
            },
            onSessionEnd: ({ reason }) => {
              if (reason === "reload") return
              void send("SessionEnd")
              session = ""
            },
            transformInput: ({ text }) => {
              void send("UserPromptSubmit", { prompt: text })
              return undefined
            },
            onStop: ({ lastAssistantText }) => {
              void send("Stop", { last_assistant_message: lastAssistantText })
              return undefined
            },
            onPermissionRequest: async ({ toolName, input, description, isSafetyPrompt }) => {
              if (isSafetyPrompt) return undefined
              const reply = await answer("PermissionRequest", { tool_name: toolName, tool_input: { description, ...(input || {}) } })
              const behavior = reply && reply.decision && reply.decision.behavior
              return behavior === "allow" || behavior === "deny" ? behavior : undefined
            },
            beforeToolCall: async ({ toolName, input }) => {
              if (toolName !== "ask_user_question") return undefined
              const reply = await answer("PreToolUse", { tool_name: "AskUserQuestion", tool_input: input })
              const answers = reply && reply.updatedInput && reply.updatedInput.answers
              if (!answers) return undefined
              const lines = ((input && input.questions) || []).filter((q) => answers[q.question]).map((q) => q.question + " → " + answers[q.question])
              if (lines.length === 0) return undefined
              return { result: [{ type: "text", text: lines.join("\n") + "\n\nContinue with the user's answers in mind." }] }
            }
          })

          cmd.on("run_start", (event) => { if (!session && event && event.sessionId) session = event.sessionId })
          cmd.on("tool_running", () => { void send("PostToolUse") })
          cmd.on("interrupted", () => { void send("Stop", { status: "aborted" }) })
          cmd.on("run_error", () => { void send("Stop", { status: "error" }) })
        }
        """;
}
