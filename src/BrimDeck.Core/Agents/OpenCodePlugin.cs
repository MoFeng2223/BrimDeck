using System.Text.Json;

namespace BrimDeck.Core.Agents;

// The plugin BrimDeck writes into OpenCode's plugin folder (%USERPROFILE%\.config\opencode\plugins\brimdeck.js), where OpenCode
// loads every plugin by itself. OpenCode has no command hooks: the plugin hears its events and hands each one to "brim hook
// opencode" in Claude Code's words. A session's work, its end, an approval (permission.asked) and a question (question.asked)
// are shown in BrimDeck; an answer chosen there comes back on brim's output and is posted to OpenCode's own server through the
// plugin's client (POST /permission/{id}/reply, /question/{id}/reply). OpenCode's own prompt stays open meanwhile, so nothing
// waits for BrimDeck, and the first answer counts; one given in OpenCode ends brim's wait, which closes the notice.
public static class OpenCodePlugin
{
    public const string FileName = "brimdeck.js";
    // The first line of the file: how BrimDeck knows the file is its own, to update or delete it.
    public const string Marker = "// BrimDeck plugin for OpenCode";

    public static string Text(string brim) => Marker + "\n" + Template.Replace("__BRIM__", JsonSerializer.Serialize(brim)).ReplaceLineEndings("\n");

    public static bool Ours(string? text) => text is not null && text.StartsWith(Marker, StringComparison.Ordinal);

    private const string Template = """
        // Written by BrimDeck (Settings › Live notices › AI coding tools); removing the hooks there deletes this file.
        // Shows OpenCode's sessions on the BrimDeck island, and lets approvals and questions be answered there.
        import { spawn } from "node:child_process"

        const BRIM = __BRIM__

        // Hands one event to brim and gives back what it prints. started(child) hears the process while it waits for an answer.
        function hook(input, started) {
          return new Promise((resolve) => {
            let out = ""
            let child
            try {
              child = spawn(BRIM, ["hook", "opencode"], { windowsHide: true, stdio: ["pipe", "pipe", "ignore"] })
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

        export const BrimDeck = async (ctx) => {
          const directory = ctx.directory
          const waiting = new Map()

          // Posts an answer to OpenCode's server: with the plugin's own client where it has one, else to the server's address.
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

          const ask = async (id, input, answer) => {
            const out = await hook(input, (child) => waiting.set(id, child))
            waiting.delete(id)
            let reply
            try { reply = JSON.parse(out) } catch { return }
            await answer(reply)
          }

          // Answered in OpenCode: brim stops waiting, and BrimDeck closes the notice.
          const settle = (id) => {
            const child = waiting.get(id)
            if (!child) return
            waiting.delete(id)
            try { child.kill() } catch {}
          }

          const toolInput = (permission, patterns) => {
            const words = (patterns || []).join(" ")
            if (permission === "bash") return { command: words }
            if (permission === "edit" || permission === "write") return { file_path: words }
            if (permission === "webfetch") return { url: words }
            return { patterns: patterns || [] }
          }

          return {
            event: async ({ event }) => {
              const p = (event && event.properties) || {}
              const base = { session_id: p.sessionID, cwd: directory }
              switch (event && event.type) {
                case "session.status":
                  if (p.status && p.status.type === "busy") void hook({ ...base, hook_event_name: "PostToolUse" })
                  break
                case "session.idle":
                  void hook({ ...base, hook_event_name: "Stop" })
                  break
                case "permission.asked":
                  void ask(p.id, {
                    ...base, hook_event_name: "PermissionRequest", tool_name: p.permission, tool_input: toolInput(p.permission, p.patterns),
                    permission_suggestions: (p.always || []).length > 0 ? p.always : undefined
                  }, (reply) => reply.reply && post("/permission/{requestID}/reply", p.id, { reply: reply.reply }))
                  break
                case "question.asked":
                  void ask(p.id, {
                    ...base, hook_event_name: "PreToolUse", tool_name: "AskUserQuestion",
                    tool_input: { questions: (p.questions || []).map((q) => ({ question: q.question, header: q.header, options: q.options || [], multiSelect: q.multiple === true })) }
                  }, (reply) => reply.answers && post("/question/{requestID}/reply", p.id, { answers: reply.answers }))
                  break
                case "permission.replied":
                case "question.replied":
                case "question.rejected":
                  settle(p.requestID)
                  break
              }
            }
          }
        }
        """;
}
