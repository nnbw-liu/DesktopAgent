# 上下文压缩与工作记忆（P2）

> 适用版本：P2 完成后（分支 `feature/vibe-p2-compaction`）；P2b 已补「先修剪 / 热前缀 / 事务」
> 配套：[p2 计划](../reports/p2-compaction-memory.md)、[P2 报告](../reports/p2-report.md)、[P1 预算文档](context-budget.md)
> 一句话：**超预算时先把「较早历史」压成摘要，再按预算硬裁剪；原文永远保留，模型看不到但用户随时能看。**

## 1. 它在请求流水线里的位置

```text
AgentSessionRunner.StreamAsync
  1. ContextBuilder.Build(消息, 摘要, 快照, 预算)
       └─ ContextTrimmer：先修剪工具轨迹 → 再淘汰整轮 → 最后缩附件（P2b）
  2. 若 L1 需要淘汰历史（OmittedMessages > 0）且 AutoCompact：
       开始标记落库（事务 / 锁）
       ContextCompactor.EnsureAsync(...)   // 只改会话语义字段，不改原文
       摘要不缩小 → 拒绝；否则摘要 + 覆盖范围一次性提交
       重新 Build（带新摘要）
     压缩失败：继续用第 1 步的 L1 结果，不阻塞回答
  3. 若 Build 直接抛 NeedsCompaction：
       压缩后重建；仍放不下才把错误抛给 UI
```

三个关键点：

- **触发条件不是「估算 > total」本身，而是「L1 已经必须淘汰历史」**。P1 的裁剪器总会把请求裁到预算内，因此「超预算」在 P2 表现为 `OmittedMessages > 0`。这样既不会每轮都摘要，又能在真正丢历史时先摘要。
- **修剪排在淘汰之前（P2b）**：轨迹修完能进预算时 `OmittedMessages` 就是 0，第 2 步整段不会发生——一次模型调用都不花。这就是 DSH「先修剪，后摘要」的形状（见 §2.5）。
- **滞回是天然的**：压缩后 `SummaryThroughOrder` 覆盖被淘汰的那段，下一轮不再有 `OmittedMessages`；等新历史再涨满 `KeepRecentTurns` 轮，才触发下一次（8K 默认约 6 轮）。

## 2. 摘要

### 2.1 覆盖范围

- 每条消息在会话里的位置就是序号；`SummaryThroughOrder` 是**不包含**的上界。
- 压缩范围 `[旧 SummaryThroughOrder, keepFrom)`，其中 `keepFrom` 至少是最后 `KeepRecentTurns` 轮的起点。
- `keepFrom` 还会被最早的 pin 消息截住：pin 的消息不进入摘要，始终以原文发送。
- 原文一条不删、一条不改；只有请求组装时跳过。

### 2.2 固定模板

模型必须只输出 6 个二级小节，顺序固定，每条事实带 `[mN]` 来源序号：

```markdown
## 目标与约束
- 不要修改数据库 schema [m3]
## 已确认决策
- 使用只读视图扩展查询 [m5]
## 关键事实
- 构建命令是 dotnet build [m8]
## 已改动
- src/Api/Options.cs [m11]
## 未完成
- 无
## 用户偏好
- 中文，先结论 [m6]
```

校验规则（`SummaryFormat.Validate`）：6 节齐全且顺序正确、不得出现模板外二级标题、每条非「无」事实必须有来源序号、总长 ≤ `SummaryMaxTokens`。

### 2.3 失败与兜底

| 失败 | 行为 |
| --- | --- |
| 超时（默认 20s，可调） | 不重试，直接兜底 |
| 网络 / 401 / 5xx | 直接兜底 |
| 格式不符 | **重试一次**（追加格式纠正提示）；仍不符则兜底 |
| 超长 | 先按行截断到上限，再校验 |

兜底摘要 `ExtractiveContextSummarizer` **永远成功**，开头带 `（自动摘要，未使用模型）` 标记。内容：

- 目标与约束：**最早 4 条 + 最近 3 条**用户消息（各 120 字）。只取最近 3 条会在首块历史丢失早期约束，因此保留最早几条；
- 已确认决策：固定写「无（自动摘要无法可靠提取）」；
- 关键事实：最近 20 条工具轨迹行；
- 已改动：轨迹中写入类工具的去重路径（`write_file` / `apply_patch` / `edit_file` 等）；
- 未完成 / 用户偏好：固定写「无」。

> 兜底宁可少写，也不编造。UI 会显示「自动摘要」标记，用户可点分隔线查看原文或 pin。

### 2.4 摘要模型

- 设置 → 上下文 → 「摘要模型」；为空表示用当前会话的 provider / model。
- 摘要请求 `temperature = 0`、`max_tokens = SummaryMaxTokens`、不执行任何工具。
- **请求形状是原对话的真前缀（P2b）**：

  ```text
  [system]  会话本轮的系统提示词（逐字，不换成摘要器专用提示词）
  [assistant] 已有摘要（若此前压缩过）
  [user]/[assistant]  …被遮蔽区域逐条回放（role 与正文保持原样）
  [user]    压缩指令（6 小节模板 + 来源标注规则）
  ```

  工具声明（`ChatOptions.Tools`）也原样带上：前缀一致，端点才可能命中上一次请求的 KV 缓存。
  带工具后个别模型会直接发起工具调用（没有文本），这时**去掉工具重试一次**，而不是降级成兜底。
  拿不到会话提示词的路径（例如 L1 连放都放不下）退回摘要器自己的固定提示词——那条路没有前缀可复用。
- 摘要请求不写入会话消息，不计入当轮 `EstimatedInputTokens`，避免污染计量校准。
- 单次输入超过 `SummaryInputMaxTokens` 时按块增量摘要，后一块带上已生成的摘要。

### 2.5 先修剪，后摘要

收紧的**顺序**就是成本顺序（P2b）：

| 顺序 | 手段 | 代价 | 实现 |
| --- | --- | --- | --- |
| 1 | 修剪工具轨迹（配置 → 减半 → 0） | 不调模型、不丢轮次 | `ContextPruner.TraceTiers` + `ContextTrimmer` |
| 2 | 历史附件改桩 / 缩当前轮附件内联 | 不调模型，附件仍可按路径重读 | `ContextTrimmer` |
| 3 | 淘汰整轮历史 | 丢掉用户 / 助手原文 | `ContextTrimmer` |
| 4 | 摘要（把淘汰段压成一段） | **一次（或多次）模型调用** | `ContextCompactor` |

因为修剪排在淘汰之前，「需要摘要」这个信号本身就意味着修剪已经不够用了。修剪省下的量记在
`TrimStats.PrunedTraceTokens` → `ContextReport.PrunedTokens`，分解面板会单独列一行
「已先修剪工具轨迹约 N token（不必压缩历史）」。

> 注意：默认 `ToolTraceTokenBudget=300` 时，轨迹本来就只占 300 token，修剪很少是超预算的主因；
> 这条性质在预算调大（长上下文档）时才明显。它的另一个价值是把顺序钉死，避免以后加收缩档位时又回到「先丢历史」。

### 2.6 事务与恢复

一次压缩是一个事务，标记落在 `sessions` 表（P2b）：

| 字段 | 含义 |
| --- | --- |
| `CompactionStartedAt` | 开始时间。**先落库**，它同时是锁：同进程里看到「只有开始」= 有压缩在跑，本轮让路 |
| `CompactionFinishedAt` | 收尾时间。早于开始时间视为未收尾 |
| `CompactionReason` | `auto` / `manual` / `rejected` / `failed:…` / `auto-fallback`，排障用 |

规则：

- **摘要与覆盖范围只在最后一次落库里同时出现**：开始标记与摘要是两次写，中途崩溃库里只有「开始」。
- **未配对的开始分两种**：同一进程里是「有压缩在跑」→ 本轮跳过并提示；从库里读回来的是「上一个进程崩了」→
  打上 `CompactionInterruptedOnLoad`，由下一次压缩接管（口径是「上次没跑完，重做这一段」），
  界面上会话头和分隔线都会标出「上次压缩未完成」。
- **不缩小的摘要直接拒绝**：摘要 token ≥（被遮蔽区域 + 旧摘要）时丢弃，原文 / 旧摘要 / 覆盖范围都不动。
- 摘要器抛异常时也会把事务收尾（`failed:<异常类型>`），否则这个会话会永远停在「有压缩在跑」。
- `ContextBuilder` 把宿主上下文快照追加在裁剪之后，`ContextTrimmer` 把它并入「永不淘汰」集合，
  `ContextCompactor` 的轮次切分忽略它、压缩范围在它之前截断——快照在任何路径上都不进摘要。

## 3. 工作记忆状态包

每轮由宿主拼装（不调用模型），放在系统块里，限制 `StatePackTokenBudget`（默认 800，小模型 500）：

```markdown
## 工作区
根目录：C:\Code\Demo
结构（深度 2，前 120 项）：
  src/
    Api/
    Web/
  tests/
最近改动：
  src/Api/Options.cs（sha256:3f9a，10:42）

## 最近动作
  [10:41] [工具] read_file src/Api/Options.cs → ok
  [10:43] [工具] apply_patch src/Api/Options.cs → ok
  [10:45] [工具] run_command dotnet build → error(exit 1)

## 验证
未运行验证
```

- 工作区树：深度 2、最多 120 项、**30 秒缓存**；排除 `bin/obj/node_modules/.git/.vs/.tmp/.idea/dist/target`。
- 最近改动：来自会话工具轨迹的写入类工具，最多 8 条，新→旧，附文件 sha256 前 4 位（文件不存在则省略 hash）。
- 最近动作：最近 12 条工具轨迹的紧凑行。
- 验证：P4 接入构建/测试结果；当前固定「未运行验证」。
- 截断顺序：结构 → 最近改动 → 最近动作；**验证块永远保留**。

## 4. 用户体验

- **分隔线**：在 `SummaryThroughOrder` 位置显示「已压缩 N 条历史（约 X token）· 自动摘要 / 模型不再看到原文，点击查看」。
- **查看原文**：只读弹层按原顺序展示被压缩的消息；可单条 pin。
- **pin**：消息悬浮操作；pin 的消息不淘汰、不压缩，上限 `MaxPinnedMessages`（默认 20）；超出提示先取消。被摘要覆盖后 pin 一条消息，它会在下一次请求以原文重新发送。
- **压缩现在**：会话 `⋯` 菜单，强制压缩一次（忽略「新增不足 4 轮 / 不足 200 token」阈值）。
- **清空摘要**：会话 `⋯` 菜单；清空后下一轮重新压缩。
- **重启**：摘要、覆盖范围、更新时间从数据库读回；覆盖范围大于消息数时自动归零并清空，避免使用可疑摘要。
- **上次压缩未完成（P2b）**：库里留下「只有开始」的标记时，会话头显示一个琥珀色徽标、分隔线上多一行证据文案
  （悬浮可看开始时间与原因）。摘要没有提交、原文都在；下一次压缩会把这一段重做一次。

## 5. 调参与推荐值

| 参数 | 默认 | 范围 | 说明 |
| --- | --- | --- | --- |
| `BudgetRatio` | 0.85 | 0.1–0.95 | 越小越早压缩 |
| `KeepRecentTurns` | 6 | 1–50 | 压缩后保留的最近轮数；也是滞回长度 |
| `SmallModelKeepRecentTurns` | 4 | 1–50 | ≤8K 档使用 |
| `SummaryMaxTokens` | 800 | 128–4096 | 摘要正文上限，不是固定块 cap |
| `SummaryInputMaxTokens` | 6000 | 512–24000 | 单次摘要输入上限；超出分块 |
| `SummaryTimeoutSeconds` | 20 | 1–300 | 超时直接兜底 |
| `StatePackTokenBudget` | 800 | 64–2048 | 普通模型状态包上限 |
| `StatePackSmallModelTokenBudget` | 500 | 64–2048 | 小模型状态包上限 |
| `MaxPinnedMessages` | 20 | 0–200 | 0 表示禁止 pin |
| `AutoCompact` | true | — | 关闭后只做 L1 硬裁剪 |
| `StatePackEnabled` | true | — | 关闭后不发工作区状态 |
| `ToolTraceTokenBudget` | 300 | 0– | 每轮回放工具轨迹的总预算；P2b 起它在淘汰整轮之前先被逐档收紧 |

推荐：

- **8K 模型**：`BudgetRatio=0.85`、`KeepRecentTurns=4–6`、`SummaryMaxTokens=600–800`、状态包 500。
- **32K+ 模型**：`KeepRecentTurns=8–12`、`SummaryMaxTokens=1000–1200`，状态包 800。
- **成本敏感**：摘要模型指定便宜档；`SummaryInputMaxTokens` 调大以减少调用次数（单次更贵但总输入更省）。
- **发现摘要总在兜底**：先看会话提示 / 日志里的格式原因；换一个指令遵循更稳的摘要模型，或调大 `SummaryMaxTokens`（截断会破坏格式）。

## 6. 已知限制

1. **压缩目标不是硬保证 60%**：压缩后大小 = 系统块 + 摘要 + 最近 N 轮原文 + 状态包。最近几轮本身很大时，压缩后仍可能接近预算；此时 `ContextBuilder` 继续做 L1 裁剪，摘要留给下一轮。
2. **模型摘要格式仍可能失败**：实测 8K golden 首次运行出现过一批只输出 3 节；加入一次格式重试后连续 3 次成功。兜底摘要质量较低，但 10 问仍可答对（见报告）。
3. **摘要模型调用有成本**：每次压缩会对新增历史发 1–N 次请求（6K 输入上限）；这些 token 不显示在聊天流，只在报告/日志。
4. **状态包不读文件内容**：只给结构、路径、hash 前缀与工具轨迹；模型需要细节时仍应用工具读取。
5. **工作区树 30 秒缓存**：外部大量新增文件时，状态包最多滞后 30 秒。
6. **验证块尚未接真实结果**：P4 之前固定「未运行验证」。
7. **摘要不覆盖当前轮**：最后一条 user 消息与最近 `KeepRecentTurns` 轮永远原文发送。
8. **摘要请求携带历史正文**：使用云端摘要模型时，被压缩的历史会发给该模型；与主模型相同的隐私边界，未额外脱敏。
9. **热前缀是否命中缓存无法自证**：摘要请求按「原对话前缀」构造（P2b），但命中与否只能看端点上报的
   `CacheHitTokens`；本地推理服务通常不做前缀缓存，那时这条改造只是「请求形状更干净」。
10. **崩溃恢复不做「跳过这一段」**：覆盖范围不提交，所以重做是幂等的；`Busy` 只拦同进程的并发压缩。

## 7. 排障

| 现象 | 检查 |
| --- | --- |
| 从不压缩 | `AutoCompact` 是否开；会话是否真的超过预算（占用环/报告 `OmittedMessages`） |
| 一直兜底 | 会话提示、日志中的 `summary FAIL` 原因；摘要模型是否支持 `max_tokens`；超时是否过短 |
| 压缩后仍然很大 | 最近轮次 / 单条消息 / 状态包是否过大；调小 `KeepRecentTurns` 或 `StatePackTokenBudget` |
| pin 后仍看不到 | pin 是否在摘要范围内（应当保留）；是否超过 `MaxPinnedMessages` |
| 重启后摘要消失 | `SummaryThroughOrder` 是否大于消息数（被自动归零）；查看 `sessions` 表四列 |
| 显示「上次压缩未完成」 | `CompactionStartedAt` 有值而 `CompactionFinishedAt` 为空 / 更早：上一次压缩没跑完；下一次压缩会重做这一段，无需手工清理 |
| 每轮都提示「已先修剪工具轨迹」 | 轨迹预算偏大（`ToolTraceTokenBudget`）或历史轨迹条目多；属于正常收敛，想少修剪就把预算调小 |
| 分隔线位置不对 | `SummaryThroughOrder` 是消息序号上界；UI 按会话消息顺序比对 |

## 8. 相关代码

| 职责 | 文件 |
| --- | --- |
| 摘要接口 / 结果 | `Core/Context/Summarizers/IContextSummarizer.cs` |
| 模板 / 校验 / 截断 / 标记 | `Core/Context/Summarizers/SummaryFormat.cs` |
| 提示词 | `Core/Context/Summarizers/SummaryPrompt.cs` |
| 模型摘要 | `Core/Context/Summarizers/LlmContextSummarizer.cs` |
| OpenAI 兼容传输 | `Core/Context/Summarizers/OpenAiSummaryChatTransport.cs` |
| 确定性兜底 | `Core/Context/Summarizers/ExtractiveContextSummarizer.cs` |
| 压缩编排 | `Core/Context/ContextCompactor.cs` |
| 先修剪后摘要（P2b） | `Core/Context/ContextPruner.cs`（档位）+ `ContextTrimmer.cs`（收紧顺序） |
| 摘要传输契约（P2b） | `Core/Context/Summarizers/ISummaryChatTransport.cs`（`SummaryChatRequest`：messages + tools） |
| 压缩事务字段（P2b） | `Contracts/Models/ChatSession.cs`（`CompactionStartedAt/FinishedAt/Reason`）+ 迁移 `AddSessionCompactionState` |
| 本轮前缀（P2b） | `Core/Services/AgentChatService.cs`（`Describe`：提示词 + 工具声明） |
| 状态包 | `Core/Context/State/WorkspaceState.cs`、`WorkspaceStateProvider.cs`、`StatePackFormatter.cs` |
| 流水线接线 | `Core/Services/AgentSessionRunner.cs` |
| 报告字段 | `Core/Context/ContextReport.cs` |
| 持久化 | `Core/Services/ChatStore.cs` |
| UI | `UI/Components/Chat/CompactionDivider.razor`、`ChatRoom.razor`、`ContextBreakdown.razor`、`Settings/ContextSettingsPanel.razor` |
| 真机检查 | `tools/p2-golden/*` |
