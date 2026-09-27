# 数据与隐私

BrimDeck 只读取其他应用已有的数据，不修改、续期或注销它们的登录信息，也不上传会话内容。

## 数据来源

| 来源 | 配额 | Token 用量与费用 |
| --- | --- | --- |
| Claude | 使用 Claude Code 的登录（`%USERPROFILE%\.claude\.credentials.json`），不可用时使用 Claude 桌面版的登录，请求 Anthropic 的账户用量接口 | Claude Code 的本地会话记录 `projects\**\*.jsonl`；不包含桌面版普通聊天 |
| Codex | 使用 `%USERPROFILE%\.codex\auth.json` 的登录请求账户用量接口；失败时显示会话记录中的配额快照并标明可能过时 | 本地 `sessions` 与 `archived_sessions` 中的会话记录 |
| Antigravity | 查找正在运行的 Antigravity 桌面版的本地服务（仅 127.0.0.1），读取额度；不读取 Antigravity CLI | `%USERPROFILE%\.gemini` 下桌面版与 CLI 的本地对话数据库 |
| Cursor | 以只读方式从 `%APPDATA%\Cursor\User\globalStorage\state.vscdb` 读取登录令牌，请求 Cursor 账户接口 | Cursor 账户用量明细接口，包含该账户在其他设备上的用量 |
| ZCode | 读取 ZCode 当前登录区域的 Coding Plan 额度，以及 ZCode 的 Start Plan 与 MCP 每日额度 | `%USERPROFILE%\.zcode\cli\db\db.sqlite` 的请求记录 |
| 智谱 GLM、Z.ai GLM、New API、Sub2API、自定义 | 使用用户在设置中填写的 API 密钥和站点地址 | 不提供，统计应用另选 |

- 设置了 `CLAUDE_CONFIG_DIR`、`CODEX_HOME` 或 `ZCODE_HOME` 且为绝对路径时，使用该目录。
- 这些账户接口和本地数据格式都不是稳定的公开接口。无法读取时，面板显示实际状态，不生成数值。

## 本机保存的文件

BrimDeck 的数据保存在 `%LOCALAPPDATA%\BrimDeck`：

| 文件 | 内容 |
| --- | --- |
| `settings.json` | 设置。不含任何登录令牌或 API 密钥 |
| `secrets.dat` | 手动来源的 API 密钥，使用 Windows DPAPI 按当前用户加密 |
| `model-prices.json`、`manual-model-prices.json` | 下载的模型价格缓存、手动添加的价格 |
| `update-prompt.txt`、`updates\` | 已提示过的版本号、下载的安装包 |
| `crash.log` | 未处理的错误记录，超过 1 MB 时清空 |

## 网络与安全

- 其他应用的登录令牌只在读取时于内存中使用（Claude 桌面版的登录缓存用当前用户的 DPAPI 在内存中解密），不写入磁盘。
- 网络请求只发往：各配额来源的官方接口、用户填写的站点、OpenRouter 模型目录、歌词服务（LRCLIB、网易云音乐、QQ 音乐）、播放器提供的封面地址，以及 GitHub Releases。
- 自定义脚本在沙盒中运行，不能访问文件、进程或 .NET 对象，限制见 [自定义配额来源](custom-sources.md)。
- 律动条按正在播放的播放器的声音跳动，声音只在内存中分析，不保存。
- **网易云音乐完整控制的风险**：完整控制需要网易云开启本机调试端口（127.0.0.1:21631）。端口开启期间，本机其他程序和浏览器网页有可能借此操控网易云，读取其页面内容和登录状态。BrimDeck 连接前会检查端口只监听回环地址且属于网易云进程，但这不能消除端口本身的暴露。请只在信任的环境中启用；网易云以普通方式重新启动后即恢复普通模式。
