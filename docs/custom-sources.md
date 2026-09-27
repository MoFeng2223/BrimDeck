# 自定义配额来源

"设置 → 配额与统计"中，每个条目的"配额来源"下拉菜单分为两组：

- **自动读取**：Claude、Codex、Antigravity、Cursor、ZCode，不需要填写。
- **手动填写**：智谱 GLM、Z.ai GLM、New API、Sub2API、自定义。选择后展开该行，填写下文所列内容。

手动来源只提供配额。该条目的"今日""7 天"和模型明细来自另选的"统计应用"。

## 通用规则

- **密钥**：保存后输入框清空并显示"已保存"，输入新值即替换。密钥用 Windows DPAPI 加密后存入 `%LOCALAPPDATA%\BrimDeck\secrets.dat`，不写入 `settings.json`。删除条目或更换配额来源时，原密钥一并删除。
- **站点地址**：只接受 `http` 或 `https` 地址，不能包含用户名、密码、`#` 片段或查询参数。可以保留部署子路径。New API 与 Sub2API 会自动去掉末尾的 `/` 和 `/v1`；直接粘贴控制台页面地址时，从 `console`、`dashboard`、`panel`、`login`、`pricing`、`keys` 这一段起的部分会被去掉。
- **测试连接**：按实际读取逻辑执行一次，显示读取状态、耗时和脚本日志（日志中的密钥已隐藏）。
- **刷新**：与其他来源一样每 60 秒读取一次。来源、站点、脚本和密钥都相同的条目只请求一次。站点返回 HTTP 429 时，在 `Retry-After` 指定的时间之前不再请求（未指定时 60 秒）。
- **失败时**：保留最近一次成功读取的数据，并在名称的悬停提示中说明失败原因和数据时间。旧数据不触发额度提醒。
- **显示**：每列每页最多两条配额，点击该列翻页。有百分比时，进度条在已用达到 70% 和 90% 时分别改用预警色和告急色；没有百分比时，进度条以主题色画满。

## 智谱 GLM 与 Z.ai GLM

只需填写 GLM Coding Plan 的 API 密钥。智谱 GLM 查询 `https://open.bigmodel.cn/api/monitor/usage/quota/limit`，Z.ai GLM 查询 `https://api.z.ai/api/monitor/usage/quota/limit`，两者解析方式相同。

- 按 `data.limits[]` 中每项的 `type` 与 `unit` 识别窗口：`TOKENS_LIMIT` 或 `CREDIT_LIMIT` 且 `unit` 为 3 时是小时窗口（`number` 为 5 即"5 小时"），`unit` 为 6 时是"每周"；`TIME_LIMIT` 是"工具调用"次数。
- 一般套餐显示 `percentage`（已用比例）；积分制套餐（`CREDIT_LIMIT`）显示已用积分与总积分。
- 重置时间晚于窗口长度时不显示。套餐名称取自 `data.level`。
- 接口不返回额度的团队套餐暂不支持。

## New API

填写站点地址和 API 密钥。每次刷新请求 `/api/usage/token/`（旧版本站点不带末尾 `/`）、`/api/status`、`/v1/dashboard/billing/subscription` 与 `/v1/dashboard/billing/usage`。

按以下顺序显示，读不到的项跳过：

| 项目 | 条件与含义 |
| --- | --- |
| 账户余额 | 仅当站点关闭了"按令牌统计"、账单接口返回整个账户的数据时显示。只显示余额，不显示总额和百分比。 |
| 密钥余额 | 仅限有额度上限的密钥，显示剩余额度、总额和到期时间。 |
| 今日扣费 | 从本机时间 0 点起算。每天第一次读取时汇总 `/api/log/token` 中今天的消费减去退款，并记下基准；同一天之后用累计用量减基准。日志为空时不显示；日志返回满 100 条且都在今天时，数字前加"≥"。 |
| 密钥累计 | 仅限无额度上限的密钥，显示累计消费，并标注"无上限"。 |

金额单位按 `/api/status` 中站长的全站设置换算：原始额度 ÷ `quota_per_unit`；站点以人民币显示时乘 `usd_exchange_rate`，使用自定义货币时乘 `custom_currency_exchange_rate`。`/api/status` 读不到时按 500000 额度 = 1 美元显示，并提示"站点单位未确认，按美元显示"。站点以人民币显示且汇率不为 1 时不显示账户余额，因为账单接口是否已按汇率换算无法确定。

## Sub2API

填写站点地址和 API 密钥。请求 `/v1/usage` 与 `/v1/sub2api/billing`（后者用于倍率，旧版本站点没有时不显示倍率）。

| 返回模式 | 显示内容 |
| --- | --- |
| 钱包（`unrestricted`，无订阅） | 账户余额、今日扣费；名称右侧的标签显示倍率，例如 `1.47×`。 |
| 订阅（`unrestricted`，有订阅） | 设置了上限的每日、每周、每月窗口；只有每周窗口显示重置时间；标签显示套餐名；到期时间显示在最后一项。 |
| 限额密钥（`quota_limited`） | 5 小时、每日、每周窗口及重置时间，然后是密钥余额；标签显示倍率；到期时间显示在最后一项。 |

`isValid` 不为 `true` 时显示"密钥无效或已被禁用"。在 New API 与 Sub2API 之间选错类型时，读取状态会提示改用另一种。

## 自定义脚本

选择"自定义"后，代码框预先填入可直接运行的模板；代码被清空时恢复为模板。可以同时填写站点地址和密钥，二者都是可选的。

脚本必须定义 `fetchUsage(ctx)`（可以是 `async` 函数）：

```javascript
async function fetchUsage(ctx) {
  const res = await ctx.http.get(ctx.site + "/api/usage", {
    headers: { Authorization: "Bearer " + ctx.secrets.key }
  });
  if (res.status !== 200) throw new Error("读取失败：HTTP " + res.status);
  const data = res.json();
  return [
    { title: "本月", percent: data.used / data.limit * 100, value: "$" + data.used.toFixed(2), suffix: "已用", resetAt: data.resetText }
  ];
}
```

### ctx

| 成员 | 说明 |
| --- | --- |
| `ctx.site` | 站点地址，未填写时为空字符串。 |
| `ctx.secrets.key` | API 密钥，未填写时为空字符串。 |
| `ctx.language` | 界面语言，`"zh-CN"` 或 `"en-US"`。 |
| `ctx.now` | 本次读取的时间，ISO 格式字符串。 |
| `ctx.log(text)` | 写一行日志，在"测试连接"中显示（最多 30 行）。 |
| `ctx.http.get(url, options)`、`ctx.http.post(url, options)` | 发送请求。`options` 可含 `headers`、`timeout`（毫秒，100–10000，默认 8000）、`body`（仅 POST，字符串或 JSON 对象）、`pick`。返回 `{ status, headers, text(), json() }`，`headers` 的键为小写。网络错误、超时和重定向会抛出错误。 |
| `ctx.memory` | 同一连接上一次成功读取时返回的 `memory`（仅对象格式，见下文）。只保存在内存中，重启后为空。 |

`pick: { path: "data", fields: ["type", "quota"] }` 让程序先解析响应，只把 `path` 处数组中每个对象的指定字段（1–20 个）交给脚本；该位置不是数组时 `json()` 返回 `null`。使用 `pick` 的响应上限为 8 MB，适合日志一类的大响应。

### 返回数组

返回一个非空数组，每项是一条配额，按数组顺序显示，程序不重新排序，也不改写文字。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `title` | 字符串，必填 | 配额名称，显示在左上方。 |
| `percent` | 数字，选填，0–100 | 已用比例，决定进度条长度和颜色，并参与额度提醒。 |
| `value` | 字符串，选填 | 大号数字，显示在进度条上方。 |
| `suffix` | 字符串，选填 | `value` 右侧的灰色小字；`value` 为空时不显示。 |
| `note` | 字符串，选填 | 与 `title` 同一行，靠右显示。 |
| `resetAt` | 字符串，选填 | 与 `value` 同一行，靠右显示。只接受字符串，程序不解析时间，例如 `"2 时 13 分后"`。 |

- 字段类型不符、`title` 缺失或为空、`percent` 超出 0–100 的项会被跳过，并以"第 2 项：value 必须是字符串。"的形式显示在读取状态中。全部项无效或数组为空时读取失败。
- 最多显示 100 项；每个字符串最多保留 500 个字符。
- 脚本抛出的错误原文显示在读取状态中；只有"HTTP 401"这类不带说明的错误改用通用说明。

### 对象格式

内置服务的脚本返回带类型的对象，由程序决定显示文字、倒计时和排序；自定义脚本也可以使用。对象必须包含非空的 `metrics` 数组，可选 `plan`（套餐标签）、`details`、`warnings`（字符串数组，显示在悬停提示中）和 `memory`（对象，最多 16 KB，下次读取时作为 `ctx.memory` 传回）。

| `kind` | 必填 | 可选 |
| --- | --- | --- |
| `percent` | `label`、`percent` | — |
| `balance` | `label`、`amount` | `total`、`currency` |
| `spend` | `label`、`amount` | `unlimited`、`atLeast`（数值是下限，显示为"≥"）、`currency` |
| `count` | `label`、`used`、`total`（大于 0） | `unit` |

每项还可附加 `id`、`window`（正整数分钟，决定排序）、`resetAt`、`expiresAt`（ISO 时间或毫秒时间戳）。

## 沙盒限制

脚本在 Jint 引擎中运行，不能访问文件、进程或 .NET 对象，也不能用字符串编译代码。

| 项目 | 限制 |
| --- | --- |
| 单次读取总时间 | 10 秒 |
| 语句数 | 200 万条 |
| 内存分配 | 32 MB |
| 递归深度 | 200 层 |
| 脚本长度 | 128,000 个字符 |
| 请求次数 | 每次读取最多 16 次，同时最多 4 次 |
| 请求正文、响应 | 各 1 MB；使用 `pick` 的响应 8 MB |
| 返回内容 | 1 MB |
| 重定向 | 不跟随，错误信息给出跳转后的站点地址 |
| 请求头 | 不能设置 `Host`、`Content-Length`，值不能包含换行 |

Jint 与 Acornima 的许可证随应用附带，见 `src/BrimDeck/ThirdPartyNotices.txt`。
