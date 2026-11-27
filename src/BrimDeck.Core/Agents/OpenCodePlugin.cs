using System.Text.Json;

namespace BrimDeck.Core.Agents;

// The plugin BrimDeck writes into OpenCode's plugin folder (%USERPROFILE%\.config\opencode\plugins\brimdeck.js), where OpenCode
// loads every plugin by itself. OpenCode has no command hooks: the plugin hears its events and hands each one to "brim hook
// opencode" in Claude Code's words; an answer chosen in BrimDeck comes back on brim's output, and the plugin gives it to OpenCode.
// OpenCode's own prompt stays open meanwhile, so nothing waits for BrimDeck, and the first answer counts; one given in OpenCode
// ends brim's wait, which closes the notice.
//
// The two generations of OpenCode load different things from the one file:
// - OpenCode 2.x reads the default export's id and setup (a plain object: a function is refused). setup subscribes to the
//   events (session.execution.*, session.tool.success, permission.asked, permission.replied) and answers an approval with
//   ctx.permission.reply. A question is a form there (form.created), which a plugin cannot answer: the island tells of it,
//   and it is answered in OpenCode (form.replied).
// - OpenCode 1.x (1.18.29 and later read the default export's server; MiMo Code, built on 1.x, calls a named export) gets the
//   event hook: session.status, session.idle, permission.asked and question.asked, answered through the plugin's client
//   (POST /permission/{id}/reply, /question/{id}/reply). A turn the user stopped (session.error with MessageAbortedError) or
//   ended by refusing an approval goes idle like a finished one, up to three times over: it is told as stopped, once.
//
// A report that the session works carries a tool's name only to mark it as another call than the one an approval waits on
// (AgentSessions): the tools' "busy" and "tool finished" come within milliseconds of an approval's request, by another process,
// and would otherwise end the wait when they arrive after it. The approval's own answer in the tool ends brim (settle).
public static class OpenCodePlugin
{
    public const string FileName = "brimdeck.js";
    // The first line of the file: how BrimDeck knows the file is its own, to update or delete it.
    public const string Marker = "// BrimDeck plugin for OpenCode";

    // OpenCode's own plugin, for both generations.
    public static readonly AgentPlugin Plugin = new(Marker, Text);
    // MiMo Code's: the same events and routes as OpenCode 1.x, under its own name.
    public static readonly AgentPlugin MiMo = new(Marker, brim => Build(brim, ["hook", "opencode", "--name", "MiMo Code", "--id", "mimo"], "export const BrimDeck = legacy"));

    public static string Text(string brim) => Build(brim, ["hook", "opencode"], "export default { id: \"brimdeck\", setup, server: legacy }");

    public static bool Ours(string? text) => Plugin.Ours(text);

    private static string Build(string brim, string[] arguments, string exports)
        => Marker + "\n" + Template.Replace("__BRIM__", JsonSerializer.Serialize(brim)).Replace("__ARGS__", JsonSerializer.Serialize(arguments)).Replace("__EXPORTS__", exports).ReplaceLineEndings("\n");

    private const string Template = """
        // Written by BrimDeck (Settings › Live notices › AI coding tools); removing the hooks there deletes this file.
        // Shows the tool's sessions on the BrimDeck island, and lets approvals and questions be answered there.
        import { spawn } from "node:child_process"

        const BRIM = __BRIM__
        const ARGS = __ARGS__

        // Hands one event to brim and gives back what it prints. started(child) hears the process while it waits for an answer.
        function hook(input, started) {
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
            if (started) started(child)
          })
        }

        // What a permission is for, in the words of Claude Code's tools.
        const toolInput = (permission, patterns) => {
          const words = (patterns || []).join(" ")
          if (permission === "bash" || permission === "shell") return { command: words }
          if (permission === "edit" || permission === "write") return { file_path: words }
          if (permission === "webfetch") return { url: words }
          return { patterns: patterns || [] }
        }

        // The approvals and questions brim waits on, by their request's id.
        const waiting = new Map()

        const ask = async (id, input, answer) => {
          const out = await hook(input, (child) => waiting.set(id, child))
          waiting.delete(id)
          let reply
          try { reply = JSON.parse(out) } catch { return }
          try { await answer(reply) } catch {}
        }

        // Answered in the tool itself: brim stops waiting, and BrimDeck closes the notice.
        const settle = (id) => {
          const child = waiting.get(id)
          if (!child) return
          waiting.delete(id)
          try { child.kill() } catch {}
        }

        // A report that the session is alive, marked as no call an approval could wait on.
        const alive = { hook_event_name: "PostToolUse", tool_name: "session" }

        // OpenCode 1.x and MiMo Code.
        const legacy = async (ctx) => {
          const directory = ctx.directory
          // Sessions whose turn the user stopped, or ended by refusing an approval: their next idle is no finished turn.
          const stopped = new Set()
          // Sessions told idle since they last worked: the tool says it up to three times for one turn.
          const idle = new Set()

          // Posts an answer to the tool's server: with the plugin's own client where it has one, else to the server's address.
          const post = async (url, requestID, body) => {
            const client = ctx.client && ctx.client._client
            try {
              if (client && client.post) {
                await client.post({ url, path: { requestID }, query: { directory }, body, headers: { "Content-Type": "application/json" } })
                return
              }
            } catch {}
            try {
              const target = new URL(url.replace("{requestID}", encodeURIComponent(requestID)), ctx.serverUrl)
              target.searchParams.set("directory", directory)
              await fetch(target, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) })
            } catch {}
          }

          return {
            event: async ({ event }) => {
              const p = (event && event.properties) || {}
              const base = { session_id: p.sessionID, cwd: directory }
              switch (event && event.type) {
                case "session.status":
                  if (p.status && p.status.type === "busy") {
                    idle.delete(p.sessionID)
                    void hook({ ...base, ...alive })
                  }
                  break
                case "session.error":
                  if (p.error && p.error.name === "MessageAbortedError") stopped.add(p.sessionID)
                  break
                case "session.idle":
                  if (idle.has(p.sessionID)) break
                  idle.add(p.sessionID)
                  void hook({ ...base, hook_event_name: "Stop", status: stopped.delete(p.sessionID) ? "aborted" : undefined })
                  break
                case "permission.asked":
                  idle.delete(p.sessionID)
                  void ask(p.id, {
                    ...base, hook_event_name: "PermissionRequest", tool_name: p.permission, tool_input: toolInput(p.permission, p.patterns),
                    permission_suggestions: (p.always || []).length > 0 ? p.always : undefined
                  }, (reply) => reply.reply && post("/permission/{requestID}/reply", p.id, { reply: reply.reply }))
                  break
                case "question.asked":
                  idle.delete(p.sessionID)
                  void ask(p.id, {
                    ...base, hook_event_name: "PreToolUse", tool_name: "AskUserQuestion",
                    tool_input: { questions: (p.questions || []).map((q) => ({ question: q.question, header: q.header, options: q.options || [], multiSelect: q.multiple === true })) }
                  }, (reply) => reply.answers && post("/question/{requestID}/reply", p.id, { answers: reply.answers }))
                  break
                case "permission.replied":
                  if (p.reply === "reject") stopped.add(p.sessionID)
                  settle(p.requestID)
                  break
                case "question.replied":
                case "question.rejected":
                  settle(p.requestID)
                  break
              }
            }
          }
        }

        // OpenCode 2.x.
        const setup = (ctx) => {
          const controller = new AbortController()
          const here = ctx.location && ctx.location.directory

          const handle = (event) => {
            const d = event.data || {}
            const form = event.type === "form.created" ? d.form : undefined
            const sessionID = form ? form.sessionID : d.sessionID
            if (!sessionID) return
            const base = { session_id: sessionID, cwd: (event.location && event.location.directory) || here }
            switch (event.type) {
              case "session.execution.started":
                void hook({ ...base, hook_event_name: "UserPromptSubmit" })
                break
              case "session.execution.succeeded":
                void hook({ ...base, hook_event_name: "Stop" })
                break
              case "session.execution.failed":
                void hook({ ...base, hook_event_name: "Stop", status: "error" })
                break
              case "session.execution.interrupted":
                void hook({ ...base, hook_event_name: "Stop", status: "aborted" })
                break
              case "permission.asked":
                void ask(d.id, {
                  ...base, hook_event_name: "PermissionRequest", tool_name: d.action, tool_input: toolInput(d.action, d.resources),
                  permission_suggestions: (d.save || []).length > 0 ? d.save : undefined
                }, (reply) => reply.reply && ctx.permission.reply({ sessionID: d.sessionID, requestID: d.id, decision: reply.reply }))
                break
              case "permission.replied":
                settle(d.requestID)
                break
              case "session.tool.success":
                void hook({ ...base, ...alive })
                break
              case "form.created": {
                // A question: the first one's title and words are told; it is answered in OpenCode.
                if (!form.metadata || form.metadata.kind !== "question") break
                const field = (form.fields || [])[0] || {}
                void hook({ ...base, hook_event_name: "Notification", notification_type: "ask_user_question", title: field.title, message: field.description || field.title })
                break
              }
              case "form.replied":
                void hook({ ...base, hook_event_name: "PostToolUse" })
                break
            }
          }

          void (async () => {
            try {
              for await (const event of ctx.event.subscribe({ signal: controller.signal })) {
                try { handle(event) } catch {}
              }
            } catch {}
          })()
          return () => controller.abort()
        }

        __EXPORTS__
        """;
}
