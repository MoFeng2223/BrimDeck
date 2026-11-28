# AI 编程工具

为本机的 AI 编程工具安装钩子后，BrimDeck 可以：

- 在收起的刘海或胶囊上显示各会话的状态：运行中、待审批、待回答、已完成。
- 一轮对话完成时发送一条通知；需要审批或提问时发送一条留在岛上的通知。
- 直接在岛上或通知页中允许、拒绝操作，或回答工具的提问（可以选择选项，也可以输入自己的回答）。

全部在本机处理：钩子通过本机命名管道把会话状态交给 BrimDeck，不经过网络。Claude Code、CodeAgent、Qoder 的钩子会给出会话记录文件的位置，BrimDeck 只在本机读取其中新增的内容，用来得知你在工具中拒绝了操作或中断了本轮对话。

## 安装钩子

1. 打开 设置 › 通知 › AI 编程工具。
2. 点“查找已安装的工具”。BrimDeck 按各工具的设置目录和程序位置查找，列出找到的工具；只列出下表中支持的工具。
3. 对单个工具点“安装”或“卸载”，或用“全部安装”“全部卸载”一次处理所有找到的工具。

几个应用共用同一个设置文件时算作一个工具。例如 Claude Code CLI、Claude 桌面版和 IDE 扩展都读取 `%USERPROFILE%\.claude\settings.json`，安装一次即可。

安装会修改该工具的用户设置文件：

- 只添加 BrimDeck 自己的条目，命令为 BrimDeck 目录中的 `brim.exe hook ...`。文件中原有的设置和其他钩子保持不变。
- 首次修改前，在同一目录保留一份 `<文件名>.brimdeck-backup` 副本。
- 卸载只删除 BrimDeck 添加的条目。
- 文件不是有效的 JSON 时不作修改，并在设置页说明。
- BrimDeck 移动到其他目录后，列表中显示“钩子需要更新”，点“更新”即可。

## 支持的工具

| 工具 | 设置文件 | 岛上能做的事 |
|---|---|---|
| Claude Code（CLI、桌面版、IDE 扩展） | `%USERPROFILE%\.claude\settings.json`（或 `CLAUDE_CONFIG_DIR`） | 显示状态、完成通知、审批（允许、拒绝，以及 Claude Code 提供的总是允许等选项）、回答提问 |
| Codex（CLI、桌面版） | `%USERPROFILE%\.codex\hooks.json`（或 `CODEX_HOME`） | 显示状态、完成通知、审批（允许、拒绝；需打开开关）；计划模式提问时提醒 |
| Antigravity（桌面版、IDE、CLI） | `%USERPROFILE%\.gemini\config\hooks.json` | 显示状态、完成通知；提问时提醒 |
| Cursor（编辑器、CLI） | `%USERPROFILE%\.cursor\hooks.json` | 显示状态、完成通知 |
| CodeAgent CLI | `%USERPROFILE%\.cac\settings.json`（或 `CODEAGENT3_CONFIG_DIR`） | 显示状态、完成通知、审批、回答提问（需打开开关） |
| Qoder（CLI、桌面版、IDE） | `%USERPROFILE%\.qoder\settings.json` | 显示状态、完成通知、审批（IDE 需打开开关）、回答提问（桌面版需打开开关；IDE 提问时提醒） |
| Qoder CN（CLI、桌面版、IDE） | `%USERPROFILE%\.qoder-cn\settings.json` | 显示状态、完成通知、审批（IDE 需打开开关）、回答提问（桌面版需打开开关；IDE 提问时提醒） |
| WorkBuddy | `%USERPROFILE%\.workbuddy\settings.json` | 显示状态、完成通知、审批（允许、拒绝；需打开开关） |
| ZCode（自带的代理） | `%USERPROFILE%\.zcode\cli\config.json` | 显示状态、完成通知、审批、回答提问（需打开开关） |
| OpenCode（CLI、桌面版） | 插件 `%USERPROFILE%\.config\opencode\plugins\brimdeck.js` | 显示状态、完成通知、审批（允许一次、总是允许、拒绝）；1.x 还可以回答提问，2.x 提问时提醒 |
| MiMo Code（CLI） | 插件 `%USERPROFILE%\.config\mimocode\plugins\brimdeck.js` | 显示状态、完成通知、审批、回答提问 |
| Command Code（CLI、桌面版） | 插件 `%USERPROFILE%\.commandcode\mods\brimdeck.ts` | 显示状态、完成通知、审批（允许、拒绝）、回答提问（均需打开开关） |
| Kimi Code（桌面版、CLI） | `%USERPROFILE%\.kimi-code\config.toml`（或 `KIMI_CODE_HOME`） | 显示状态、完成通知；待审批、待回答时提醒 |
| TRAE（含 TRAE SOLO） | `%USERPROFILE%\.trae\hooks.json` | 显示状态、完成通知；待审批、待回答时提醒 |
| TRAE CN（含 TRAE SOLO CN） | `%USERPROFILE%\.trae-cn\hooks.json` | 显示状态、完成通知；待审批、待回答时提醒 |

“需打开开关”的含义见下文“审批与提问”。

说明：

- **Claude Code** 的提问与审批一样经过审批钩子：Claude Code 自己的提问框照常显示，岛上也可以回答，先回答的一方生效。
- **Codex** 只运行用户审阅过的钩子。安装后，在 Codex 中输入 `/hooks`（桌面版在“设置 › 钩子”中），审阅并信任 BrimDeck 的钩子后才会生效。钩子内容改变（例如“更新”之后）需要重新审阅。Codex 确认框中的“允许类似命令”不能由钩子选择，需要时点“在终端处理”，在 Codex 中选择。Codex 桌面版每轮结束后自己发出的后台请求不会显示为会话。
- **Antigravity** 的桌面版、IDE 与 CLI 读取同一个钩子文件，算作一个工具。它的钩子没有审批事件，审批仍在 Antigravity 中进行；提问时岛上显示一条提醒，在 Antigravity 中回答后自动收起。
- **Cursor** 的钩子没有单独的审批事件，因此 Cursor 的审批仍在 Cursor 中进行。
- **Cursor** 默认还会运行 Claude Code 设置文件中的钩子。BrimDeck 只按 Cursor 自己的钩子显示它的会话，所以要显示 Cursor 的会话，需要安装 Cursor 一行的钩子。
- **CodeAgent、Qoder、Qoder CN** 的钩子格式与 Claude Code 相同，BrimDeck 按 Claude Code 的格式安装和回复。Qoder 与 Qoder CN 的命令行在提问时，自己的提问框与岛上同时有效；桌面版与 CodeAgent 在岛上等待期间不显示提问框。
- **Qoder IDE、Qoder CN IDE** 与桌面版读取同一个设置文件，但钩子行为不同：IDE 先等待钩子返回，再显示自己的审批卡片和提问，所以审批默认只在岛上提醒，打开“在岛上审批（Qoder IDE）”后在岛上审批；提问时岛上只提醒，在 IDE 中回答。IDE 每轮结束都会结束会话，岛上的会话保留为空闲。
- **WorkBuddy** 的钩子不能添加“总是允许”规则，也不能回答提问（提问的钩子在你回答之后才运行），所以岛上只有“允许”“拒绝”，提问仍在 WorkBuddy 中回答。WorkBuddy 默认直接运行命令、不请求审批，这时岛上不会出现审批。
- **OpenCode** 没有命令钩子。BrimDeck 在它的插件目录中写入插件 `brimdeck.js`，OpenCode 启动时自动加载；卸载时删除这个文件。安装后需要重新启动 OpenCode。OpenCode 自己的确认框与岛上的按钮同时有效，先回答的一方生效；在 OpenCode 中先回答后，岛上的通知随之关闭。同一个文件适用于 OpenCode 2.x 与 1.18.29 及以上的 1.x。OpenCode 桌面版是否加载这个插件尚未验证。OpenCode 2.x 的插件不能回答提问：提问时岛上显示一条提醒，在 OpenCode 中回答后提醒自动收起。
- **MiMo Code** 基于 OpenCode 1.x，用同样的插件接入。Xiaomi MiMo 桌面版每次启动都会清空自己的插件目录，不加载用户插件，因此只有 MiMo Code CLI 的会话会显示。
- **Command Code** 的命令钩子没有审批事件，BrimDeck 改为写入插件（mod）`brimdeck.ts`，CLI 与桌面版在新的会话中自动加载；卸载时删除这个文件。Command Code 先等待插件、再显示自己的确认框，所以审批与提问默认只在岛上提醒；打开开关后先在岛上等待，交回后由 Command Code 照常询问。Command Code 出于安全强制询问的操作不接受插件的“允许”，始终在 Command Code 中确认。
- **Kimi Code** 的钩子写在 `config.toml` 末尾的 `[[hooks]]` 表中，安装后需要重新启动 Kimi Code。它的钩子不能作出决定：需要审批或提问时，岛上显示一条留在岛上的提醒，在 Kimi Code 中处理后提醒自动收起。
- **TRAE、TRAE CN** 各有自己的 `hooks.json`，分别安装。安装后，还需要在 TRAE 的“设置 › Hooks”中打开“已配置的 Hooks”，并把“Hooks 命令运行方式”从默认的“沙箱运行”改为“本地自动运行”：在沙箱中钩子不会运行，也没有任何提示。它的钩子不能回答 TRAE 自己的确认框：TRAE 等待审批或回答时，岛上显示一条留在岛上的提醒，在 TRAE 中处理后提醒自动收起。在 TRAE 中中断时没有任何钩子事件，会话会一直显示为运行中，30 分钟没有消息后移除。
- **ZCode** 的配置文件钩子默认关闭。安装时 BrimDeck 在该文件的 `hooks` 中打开 `enabled`，卸载时只在其中没有其他钩子时一并移除。安装只作用于 ZCode 自带的代理；在 ZCode 中选用的 Claude Code、Codex 等内置代理使用各自的配置，不受影响。
- Codex、Antigravity 与 Cursor 没有可由钩子回答的提问，它们的提问仍在工具中回答。
- Antigravity、Kimi Code 通过命令行运行钩子。BrimDeck 的安装路径带空格时，写入的是它的短路径（例如 `C:\PROGRA~1\BrimDeck\brim.exe`），这些工具才能正确运行。Codex 通过 PowerShell 运行钩子，路径带空格时写成 `& 'C:\Program Files\BrimDeck\brim.exe' hook codex`。

以下工具暂不支持，查找时不会列出：

| 工具 | 原因 |
|---|---|
| DeepSeek Harness | 没有命令钩子，只能通过插件和启动配置接入 |
| 千问办公、QwenWork | 只在部分对话引擎下运行用户钩子，且钩子不能回答审批 |
| WorkBuddy AI | 不读取设置文件中的钩子，只能通过它自己的插件接入 |

## 审批与提问

工具请求审批或提问时，岛上出现一条留在岛上的通知；收起栏中该会话显示为“待批准”或“待回答”。之后在哪里回答，取决于工具在钩子等待期间是否显示自己的确认框。

### 工具与岛上同时可答

适用于 Claude Code、CodeAgent、Qoder 与 Qoder CN 的桌面版和命令行、ZCode、OpenCode、MiMo Code 的审批，以及 Claude Code、Qoder 命令行、Qoder CN 命令行、MiMo Code 的提问。工具自己的确认框照常显示，岛上也可以回答，先回答的一方生效：

1. 悬停或单击岛，展开到通知页。审批显示要运行的命令或要修改的文件，以及“允许”“拒绝”“在终端处理”按钮。工具在审批时提供了其他允许方式的，岛上为每一种各显示一个按钮，并写明范围，例如“总是允许 echo OK *（本项目）”“允许访问 D:\Projects（本会话）”，点哪个就只应用哪一个。目前 Claude Code、CodeAgent、Qoder 与 Qoder CN 的桌面版和命令行、OpenCode、MiMo Code 会提供这些方式；Codex 的“允许类似命令”、ZCode 的“始终允许此命令”和切换模式等选项钩子不能选择，需要时点“在终端处理”。提问显示每个问题和选项，下方的输入框可以输入自己的回答。
2. 在岛上回答后，工具立即继续运行。
3. 在工具中先回答后，岛上的通知自动收起。多数工具在该操作运行结束、你发出新的指令或本轮结束时收起；Claude Code、CodeAgent、Qoder 在工具中拒绝或中断时，BrimDeck 从会话记录文件得知，几秒内收起。

### 等待期间工具不显示确认框

适用于 Codex、WorkBuddy、Qoder IDE、Qoder CN IDE 的审批，Command Code 的审批与提问，CodeAgent、Qoder 桌面版、Qoder CN 桌面版、ZCode 的提问。这些工具先等待钩子返回，再显示自己的确认框或提问框。

- 默认：岛上只提醒，不带按钮和选项；工具照常显示自己的确认框，在工具中处理后提醒自动收起。
- 在 设置 › 通知 › AI 编程工具 › 审批与提问 中打开该工具的“在岛上审批”或“在岛上回答提问”后：改为在岛上回答，期间工具不显示自己的确认框。以下情况交回工具，按工具原来的方式确认：
  - 超过“在岛上等待回答”中设置的时间（默认 5 分钟）。
  - 关闭这条通知，或点“在终端处理”“在终端回答”。
  - BrimDeck 没有运行。此时钩子立即结束，工具不会等待。

WorkBuddy 的这一行为来自对它程序代码的核对，尚未在真实会话中验证。

### 只提醒

Kimi Code、TRAE、TRAE CN 的审批与提问，以及 Antigravity、OpenCode 2.x、Codex（计划模式）、Qoder IDE、Qoder CN IDE 的提问，钩子不能作出决定或回答格式尚未确认：岛上显示一条留在岛上的提醒，在工具中处理后自动收起。

## 收起栏

刘海和胶囊的“通知”部件默认也显示 AI 编程工具的会话：每个活动会话一个圆形图标，用工具自己的程序图标（没有安装桌面版时显示首字母）；同一工具开了几个会话就叠几个，最多叠 3 个，最需要处理的会话排在最右。每个图标的外圈表示它自己的状态：蓝色转动为运行中；黄色外圈、图标轻晃并带“!”或“?”为等待批准或回答；绿色外圈带对勾为已完成。后面的文字说明状态：只有一个会话在运行时显示它已运行的时间，多个时显示数量。没有活动会话时不显示。单击打开通知页。

在 设置 › 收起外观 › 显示内容 中可以关闭“通知”，或拖动调整位置；在 设置 › 通知 › 提示方式 › 通知 中可以关闭“显示 AI 编程工具”，或打开“合并为一组”，让会话图标和未读通知的应用图标叠在同一组里。完成后的会话显示 10 分钟；30 分钟没有任何消息的运行中会话视为已结束。

## 连接其他工具

不在列表中的工具也可以接入。设置页“其他工具”一节给出了包含 `brim.exe` 完整路径的命令，可以直接复制。

### 钩子格式与 Claude Code 相同的工具

如果工具的钩子从标准输入接收 JSON，其中含有 `hook_event_name`、`session_id`，并使用 Claude Code 的事件名称（`SessionStart`、`UserPromptSubmit`、`PostToolUse`、`Stop`、`PermissionRequest`、`PreToolUse` 等），把下面的命令填为它的钩子命令：

```
"C:\Program Files\BrimDeck\brim.exe" hook claude --name "工具名称"
```

- `--name` 是在 BrimDeck 中显示的名称；`--id` 可另外指定标识，不填时由名称生成。
- 审批事件（`PermissionRequest`）和提问（`PreToolUse` 中的 `AskUserQuestion`）需要等待回答，钩子超时应设为 30 分钟以上；其他事件可以设为后台运行。
- 工具能否接受钩子返回的审批结果和回答，取决于工具本身。

### 其他工具：在脚本中报告状态

在工具的钩子、插件或你自己的脚本中运行：

```
brim agent report --tool "工具名称" --session 会话ID --state working --project 项目名
```

`--state` 可取：

| 值 | 含义 |
|---|---|
| `started` | 会话开始 |
| `working` | 正在处理 |
| `approval` | 等待审批 |
| `question` | 等待回答 |
| `done` | 本轮完成 |
| `idle` | 空闲，等待下一条指令 |
| `ended` | 会话结束，从收起栏移除 |

可选：`--title` 为当前任务，`--message` 为最后一条回复。

需要在岛上审批时，用 `brim notify send` 发送带按钮并等待结果的通知，按输出的按钮键继续：

```
brim notify send "请求运行命令" -b "npm test" -s "工具名称" --action allow=允许 --action deny=拒绝 --timeout 5m
```

退出码为 0 时，标准输出是所选按钮的键；为 4（关闭）或 5（超时）时，交回工具原来的流程。完整的选项见 `brim --help`。
