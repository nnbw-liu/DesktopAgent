# 上下文预算与硬裁剪（P1）

> 面向开发者的实现说明。用户侧只需要知道三件事：每次请求都有预算、超出的内容会被明确裁剪、输入框下方的占用环能点开看明细。
> 基线数据与实测结论见 [p1-baseline.md](../reports/p1-baseline.md)，验收记录见 [p1-report.md](../reports/p1-report.md)。

## 1. 不变量

1. **所有请求只有一个入口**：`ContextBuilder.Build(ContextBuildRequest)`。普通对话经由 `AgentSessionRunner`，插件经由 `PluginRunner`（passthrough），UI 不再自己拼消息列表。
2. **原始消息永不删除**：裁剪只影响「这一次发出去的内容」，`ChatSession.Messages`、数据库与界面都保持原样；被省略的消息在请求里用一行说明代替。
3. **进入上下文的一切都要估算**：系统提示词、技能、历史消息、附件正文、工具轨迹、pin 消息分别计量，报告里各段之和等于总量。
4. **模型无关**：窗口与输出上限来自 `ModelProfile`；代码里没有针对具体厂商的硬编码分支。
5. **失败可解释**：超预算抛 `ContextBudgetException`（`NeedsCompaction`），400 溢出会被识别并自动重试一次；两种情况都给用户一句宿主提示。

## 2. 预算公式

`ContextBudget.Create(ModelProfile, ContextSettings)`：

```text
reserveOut = min(max(256, MaxOutputTokens), max(1024, window / 4))
safety     = max(256, window / 20)
total      = floor((window - reserveOut - safety) * BudgetRatio)   // 默认 0.85
fixedCap   = floor(total * FixedCapRatio)                          // 默认 0.40
compact    = floor(total * 0.60)                                   // P2 摘要目标
```

- `total` 是**输入预算**，UI 占用环的分母就是它；`window - reserveOut - safety` 是硬上限，永不越界。
- `fixedCap` 是「系统提示词 + 技能」的固定块上限；技能超限时截断正文并加一行说明，系统提示词本身不截断。
- 溢出重试时 `Scale(0.5)` 只缩小预算（`total` / `fixedCap` / `compact`），窗口与预留保持不变，报告里能看出前后差异。

示例（8K 窗口 / 2048 输出）：`reserveOut = 2048`、`safety = 409`、`total = 4874`、`fixedCap = 1949`、`compact = 2924`。

## 3. 模型档案（ModelProfile）

```csharp
record ModelProfile(
    int ContextWindow = 8192,
    int MaxOutputTokens = 2048,
    bool SupportsTools = true,
    bool SupportsParallelToolCalls = false,
    bool SupportsReasoning = false,
    string? Preset = null)
```

解析顺序（`ModelProfileResolver`）：

1. 模型目录里的显式 `ModelEntry.Profile`（只选了档位时按档位表展开：8K / 32K / 128K / 200K）；
2. 模型名里的档位，例如 `qwen2.5-32k`；
3. 常见家族前缀（`gpt-4o`、`deepseek`、`claude`…）；
4. provider 的 `DefaultProfile`；
5. 都没有 → 未知模型按 8K / 2K 输出保守处理。

400 自愈学到的窗口写在 `context-calibration.json` 的 `WindowOverride` 里，只对「配置无法确定档案」的模型生效，不覆盖用户显式选择。

## 4. token 计量与校准

`HeuristicTokenCounter`：

```text
tokens = ceil(cjk * 0.7 + (len - cjk) / 3.8) + 4 + extraOverhead
```

- 汉字 / 假名 / 全角标点按 0.7 token/字，其他字符按 3.8 字符/token；emoji 代理对只算一个字符。
- 每条消息固定 4 token 开销（角色、分隔符）。

校准（`TokenCalibrationStore`，文件 `%APPDATA%\SDemo\DesktopAgent\context-calibration.json`）：

- 键为 `{providerId}/{modelId}`；用服务端返回的 `usage.input_tokens` 除以上述估算得到比值。
- 因子是 EMA：`factor = factor * 0.7 + clamp(ratio, 0.7, 1.6) * 0.3`，初始 1.0。
- 连续 3 次偏差 > 25% 自动停用，避免把误差放大进预算；设置页「重置校准」清空全部记录。
- 服务端不返回 usage 时跳过校准（报告标 `ActualInputTokens = null`），不影响发送。

## 5. L1 确定性裁剪（`ContextTrimmer`）

顺序固定，全部是确定性操作，不调用模型：

1. 按「user 消息」把历史切成轮次；保留最近 `KeepRecentTurns` 轮（小模型用 `SmallModelKeepRecentTurns`）。当前输入永远保留，即使它单条就超预算。
2. 从最旧的整轮开始淘汰，直到消息预算内；被省略的消息在第一条保留消息前加一行 `[已省略 N 条较早消息…]`。
3. 历史附件改为桩：`[附件已省略：文件名, 大小, 路径]`；当前轮附件在 `AttachmentInlineTokens`（默认 4096 token，按 2 字符/token 换算）以内内联，超出只留桩——文件仍可用 `read_file` / `pdf_read` 重新读取。
4. 工具轨迹按 `ToolTraceTokenBudget`（默认 300）从新到旧回放，放不下的折成一行「另有 N 次调用已省略」。
5. pin 消息：数量上限 `MaxPinnedMessages`（20），总占比不超过 `PinnedShareLimit`（30%），超量时截断正文到 `PinnedTruncateChars`。P1 先落字段与规则；P2 已接完整交互，且 pin 的消息即使被摘要覆盖也仍以原文发送（见 [context-compaction.md](context-compaction.md)）。

裁剪后的请求消息是 `Microsoft.Extensions.AI.ChatMessage` 的新列表；领域对象（`SDemo.DesktopAgent.Models.ChatMessage`）不被修改。

## 6. 工具轨迹（`ToolTrace`）

一次工具循环结束后，每条助手消息最多保留 `MaxEntriesPerMessage`（20）条：

```csharp
record ToolTraceEntry(
    string ToolId, string Status, int? ExitCode,
    string? ArgsDigest, string? Summary, int OutputChars, DateTime At)
```

- `Status`：`ok` / `error` / `denied` / `canceled`；`ExitCode` 从结果文本里的 `exit code N` 提取。
- `ArgsDigest`：路径参数直接保留（≤120 字符），其余参数记 `sha256:…` 与原始长度，不把脚本正文写进数据库。
- `Summary`：首 200 + 尾 200 字符，总长 ≤ 400；单条 JSON ≤ 2KB。
- 回放格式：`[工具] read_file docs/a.md → ok：读取成功`。

落库字段：`messages.ToolTraceJson`、`messages.IsPinned`；老库由 `ChatStore.RequiredColumns` 自动补列。

## 7. 工具循环里的中途裁剪（`ContextBudgetChatClient`）

MAF 1.25 的 `FunctionInvokingChatClient`（FICC）插在装饰器**外层**，因此每次 LLM 请求（含工具结果回灌）都会经过这里（结论由 `MidTurnPipelineSpikeTests` 固定）：

1. 单条 `FunctionResultContent` 超过 `MidTurnToolResultChars`（默认 2000）时：
   - **P2c 起先落盘**（`SpillLargeToolOutputs` 默认开）：全文写入工作区 `.agent/spill/<callId>.log`，上下文里换成「头 1200 + 尾 300 + `.agent/spill` 路径 + `read_file(…, offset: N)` 回读指引」。写盘失败（只读工作区 / 权限不足）退回纯截断，并在省略标记里注明「全文未能落盘」——落盘永远是尽力而为，不会把成功的工具调用变成错误。
   - `read_file` 的输出**不落盘**（它本来就能按 `offset` 再读一次，落盘等于自我复制）；关闭开关时也退回纯截断。
   - 落盘目录自带 `.agent/.gitignore`（内容 `*`），不污染用户的 `git status`；`.agent` 同时进 `WorkspaceExcludes` 与 `WorkspaceSearch.ExcludedDirectories`，树 / `@` 引用 / 搜索里都不会出现，只有显式 `read_file(".agent/spill/…")` 能读。
   - 文件数上限 200，超出按最后写入时间清理最旧的；同一 `callId` 重复请求命中同一文件，内容不变就不重写。
2. 仍超预算时从最旧「整块」淘汰（一块 = 一条 user 消息及其后的 assistant / tool 消息），永远保留 system 前缀与最后一块；孤立 tool 消息一并丢弃。
3. 每次请求都累计 `ContextRunTelemetry.EstimatedInputTokens` 与 `RequestCount`，供 usage 校准；发生裁剪时 `MidTurnTrimmedRequests++` 并给用户提示。

## 8. 400 溢出自愈（`AgentSessionRunner`）

1. 发送前 `ContextBuilder` 若仍超预算 → 直接抛 `NeedsCompaction`，不发请求。P2 起先尝试增量摘要再重建；压缩不可用 / 压缩后仍超时才提示用户（新建会话 / 减少附件 / 调大窗口），见 [context-compaction.md](context-compaction.md)。
2. 传输层抛出异常时，`ContextOverflowDetector` 只在「文案明确是上下文长度 + 状态码为 400（或没有状态码）」时判定为溢出，同时尝试从文案里学习窗口值。
3. **只重试一次**：按 `OverflowRetryScale`（默认 0.5）重算预算并重新裁剪；已吐出正文后不再重试，避免内容重复和成本翻倍。
4. 重试仍失败 → `ContextBudgetException(ContextOverflow)`；学到的窗口写入校准文件，下次直接按真实窗口建预算。

## 9. 设置项（`ContextSettings`）

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | `true` | 关闭后等价旧行为（直通），仍记录报告 |
| `BudgetRatio` | `0.85` | 输入预算占可用窗口比例 |
| `KeepRecentTurns` | `6` | 保留最近轮次 |
| `SmallModelKeepRecentTurns` | `4` | ≤8K 模型保留轮次 |
| `FixedCapRatio` | `0.40` | 系统提示词 + 技能上限 |
| `MidTurnTrim` | `true` | 工具循环内裁剪 |
| `OverflowRetry` | `true` | 400 自动重试 |
| `OverflowRetryScale` | `0.5` | 重试预算比例 |
| `ShowContextMeter` | `true` | 输入框占用环 |
| `ToolTraceTokenBudget` | `300` | 每条消息的轨迹回放预算 |
| `AttachmentInlineTokens` | `4096` | 当前轮附件内联预算（token） |
| `MidTurnToolResultChars` | `2000` | 单条工具结果上限 |
| `SpillLargeToolOutputs` | `true` | 超限结果落盘到 `.agent/spill`（P2c），关闭后退回纯截断 |
| `MaxPinnedMessages` | `20` | pin 数量上限 |
| `PinnedShareLimit` | `0.30` | pin 占消息预算上限 |
| `PinnedTruncateChars` | `500` | pin 超量时保留字符 |

设置页「通用 → 上下文」展示当前生效值，并提供「重置校准」；模型编辑器里每个模型可以单独选窗口档位（自动 / 8K / 32K / 128K / 200K）。

## 10. 报告与 UI

`ContextReport` 是唯一数据源，字段包括：窗口、输出预留、安全余量、总预算、固定块、估算/原始估算、校准因子、分段（`ContextSegments`：系统 / 技能 / 历史 / 附件 / 轨迹 / pin）、省略消息数与 token、保留轮次、pin 统计、附件桩、修剪标记、直通、中途裁剪、溢出重试、实际输入、学到的窗口。

- `ContextMeter`：输入框**下方**（`.composer-usage`）的环，显示 `估算 / 输入预算`；新聊天室还没有消息时不渲染；>80% 变黄、>95% 变红；tooltip 给出窗口、预留与 usage 对比。
- `ContextBreakdown`：点开后展示分段条形图与明细；**各段之和 = 报告估算**，UI 数字直接来自报告，不做二次换算（因此 UI 与报告一致）。
- 宿主提示（`ContextRunTelemetry.ContextNotice`）显示在消息区末尾，带关闭按钮；它不是模型输出，也不会进入下一轮上下文。

## 11. 已知边界（P1 + P2 范围内）

- P1 只做**确定性裁剪**；P2 增加**滚动摘要 + 工作记忆状态包**（见 [context-compaction.md](context-compaction.md)），超预算且无法再裁时不再直接失败，而是先摘要再重建。原文仍永久保留。
- 压缩的触发条件是「L1 已开始淘汰历史」（`OmittedMessages > 0`），不是 plan 的 `Estimated > Total`；细节与调参见 [context-compaction.md](context-compaction.md)。
- 50KB 附件在 8K 窗口下不会全部内联：当前轮最多内联约 4096 token 的正文，其余只留文件名 / 路径，模型需要全文时用工具重新读取。这是有意的取舍，不是缺陷。
- 估算器是启发式的，靠校准逼近真实 usage；基线误差数据见 [p1-baseline.md](../reports/p1-baseline.md)。
- 中途裁剪只覆盖 `GetResponseAsync` / `GetStreamingResponseAsync` 的请求；它只从历史里的 `tool_calls` 读工具名（唯一用途是跳过 `read_file` 的落盘），不解释业务语义，只保证 tool_call / result 成对。
