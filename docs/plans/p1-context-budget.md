# P1 — 上下文预算与硬裁剪（第 1 期）

> 状态：✅ 完成（报告见 [p1-report.md](p1-report.md)） · 估时 5 人日 · 前置：无 · 出口：任意会话不因上下文超限失败
> 关联总纲：[vibe-coding-roadmap.md](vibe-coding-roadmap.md) · 决策：双模 / 只隐藏不删除

## 1. 本期要解决的问题

现状是「有多少历史就发多少」：

- 每次把 `Session.Messages` 全量传入（[ChatRoom.razor](../../src/SDemo.DesktopAgent.UI/Components/Chat/ChatRoom.razor:939)）；
- 附件按单文件 16K 字符、总量 48K 字符内联（[AgentMessageMapper.cs](../../src/SDemo.DesktopAgent.Core/Services/AgentMessageMapper.cs:18)）；
- 技能全文无上限拼进系统提示词（[AgentChatService.cs](../../src/SDemo.DesktopAgent.Core/Services/AgentChatService.cs:172)）；
- 模型档案只有 Id / Name，没有窗口与输出上限（[ModelProvider.cs](../../src/SDemo.DesktopAgent.Core/Models/ModelProvider.cs:41)）。

后果：接入 8K 模型后，一条 20KB 文本附件就能让第一次请求直接 400；任何长会话最终都会撞墙，而且用户看不到「用了多少」。

本期不引入摘要（P2），只做**确定性**的三件事：**算得准、裁得掉、撑得住**。

## 2. 范围

**做**

- 模型能力档案（窗口 / 输出上限 / 工具能力）+ 未知模型保守默认；
- token 估算 + 按 provider/model 自校准；
- 请求前预算计算（固定块上限、输出预留、安全余量）；
- L1 确定性裁剪：最近 N 轮保留、按轮淘汰、附件桩、工具结果收紧、省略提示；
- 工具调用轨迹采集与压缩持久化（解决「下一轮失忆」）；
- 工具循环内的中途裁剪（先 spike，再按结论实现）；
- 上下文溢出（400）识别、学习窗口、缩预算重试一次；
- 上下文占用 UI（环 + 分段明细）与溢出提示；
- 基线测量方法与数据。

**不做（留到后续期）**

- LLM 摘要与摘要 UI → P2；
- 工作记忆状态包、pin 的完整交互 → P2（本期只落 `IsPinned` 字段与裁剪规则）；
- 编辑工具、命令直跑、todo、小模型提示词 → P3–P6。

## 3. 设计要点

### 3.1 数据流

改造前：

```
ChatRoom.GenerateAsync
  history = Session.Messages.ToList()            // 全量
  → AgentChatService.StreamAsync(history)
      → plan.Messages.Where(ShouldInclude).Select(ToRequestMessage)
      → agent.RunStreamingAsync(messages)        // 无预算、无轨迹
```

改造后：

```
ChatRoom.GenerateAsync
  → AgentSessionRunner.StreamAsync(session, provider, modelId, …)
      1. profile  = ModelProfileResolver.Resolve(provider, modelId)
      2. plan     = ContextBuilder.Build(messages, profile, settings, state)   // L1
      3. 若仍超限 → 本期直接报「需压缩」，P2 接入摘要
      4. AgentChatService.RunAsync(plan.ContextPlan)
           └ ContextBudgetChatClient 兜住工具循环内的增长
      5. 持久化：ToolTrace、usage、calibration、ContextReport
```

`AgentSessionRunner` 是新的应用层入口，负责「策略 + 落库」；`AgentChatService` 只负责传输与流式解析，不再决定消息取舍。

### 3.2 模型档案

```csharp
public sealed record ModelProfile(
    int ContextWindow = 8192,
    int MaxOutputTokens = 2048,
    bool SupportsTools = true,
    bool SupportsParallelToolCalls = false,
    bool SupportsReasoning = false,
    string? Preset = null);
```

- `ModelEntry` 新增 `Profile`（可空），序列化进 `settings.json`；
- 解析顺序：显式 Profile → 模型名前缀预设 → provider 默认 → **未知按 8K / 2K 输出**；
- 预设只给「档位」（8K / 32K / 128K / 200K），不硬编码厂商具体值；具体值由用户在「设置 → 模型」里确认；
- 400 自愈学到的窗口写入 `profileOverrides`（独立文件，见 3.7），不覆盖用户显式配置。

### 3.3 token 计量

```csharp
public interface ITokenCounter
{
    int Count(string text);
    int CountMessage(string role, string text, int extraOverhead = 0);
}
```

启发式（默认实现）：

```
cjk    = 汉字 / 全角标点字符数
tokens = ceil(cjk * 0.7 + (len - cjk) / 3.8) + 4 + extraOverhead
```

校准：每次请求后拿 `UsageDetails.InputTokenCount`（[AgentRunMetrics.cs](../../src/SDemo.DesktopAgent.Core/Services/AgentRunMetrics.cs:29)）与 `ContextReport.Estimated` 比较：

```
ratio = clamp(actual / max(estimated, 1), 0.7, 1.6)
ema   = ema * (1 - 0.3) + ratio * 0.3
```

- 存 `%APPDATA%\SDemo\DesktopAgent\context-calibration.json`，键 `{providerId}/{modelId}`；
- 服务端不返回 usage 时跳过校准，并在报告里标 `estimated=true`；
- 偏差 > 25% 连续 3 次写 Debug 日志，便于排查。

### 3.4 预算

```csharp
public sealed record ContextBudget(
    int Window, int ReservedOutput, int Safety, int Total, int FixedCap, int CompactTarget)
{
    public static ContextBudget Create(ModelProfile profile, ContextSettings settings);
}
```

```
reserveOut = min(profile.MaxOutputTokens, max(1024, window / 4))
safety     = max(256, window / 20)
total      = floor((window - reserveOut - safety) * settings.BudgetRatio)   // 默认 0.85
fixedCap   = floor(total * settings.FixedCapRatio)                          // 默认 0.40
compact    = floor(total * 0.60)                                            // P2 用
```

P1 的固定块只有系统提示词与技能；`fixedCap` 先约束它们，超了就截断技能正文并加提示（而不是让请求失败）。

### 3.5 L1 裁剪规则

输入：消息列表（含本轮用户输入）、预算、`TrimOptions`。输出：`TrimResult`（回放消息 + 统计）。

1. **按轮分组**：一条 user 消息开启一轮，直到下一条 user 消息；轮内包含 assistant 消息与工具轨迹。
2. **最后 `KeepRecentTurns` 轮不动**（默认 6，小模型 4）。
3. 从最旧轮开始**整轮**淘汰，直到估算 ≤ `total`；不允许只丢半轮。
4. **当前用户输入与其附件永不淘汰**；历史轮次里的附件内联替换为 `[附件已省略：name, 大小, 路径]`。
5. 被淘汰的位置在**第一条保留消息**前加一行提示（不新增 system 消息，避免部分服务端拒绝消息中段的 system）：
   `[上下文提示] 已省略 N 条更早消息（约 X token）；需要时请重新读取文件或使用工具。`
6. 工具轨迹按「最近 K 条 / 总预算 300 token」回放，超出部分压成一行。
7. **配对完整性**：assistant 的 tool_call 与对应 tool 结果同进同退；裁剪后不允许出现孤立 tool 消息。这条用单测钉死。
8. pin 消息无条件保留，但数量上限 20；pin 内容超过 `total` 的 30% 时截断到 500 字符并在 UI 提示，避免 pin 反噬预算。

### 3.6 工具轨迹

MAF 的流式更新里可以拿到 `FunctionCallContent` / `FunctionResultContent`，在 [AgentChatService.cs](../../src/SDemo.DesktopAgent.Core/Services/AgentChatService.cs:144) 的解析循环中采集：

```csharp
public sealed record ToolTraceEntry(
    string ToolId,
    string Status,              // ok / error / denied / canceled
    int? ExitCode,
    string? ArgsDigest,         // 路径或脚本 hash，不存正文
    string? Summary,            // 首 200 字符 + 尾 200 字符
    int OutputChars,
    DateTime At);
```

- 每条助手消息最多 20 条、单条 JSON ≤ 2KB；
- 持久化到 `messages.ToolTraceJson`（新增列）；
- 回放时渲染为紧凑行：
  `[工具] read_file src/a.cs → ok，4.2KB`
  `[工具] apply_patch src/a.cs → ok (+12 -3)`
  `[工具] run_command dotnet build → error(exit 1)：error CS1002 …`
- 这正是「下一轮不再失忆」的最小成本方案；**不**回放原始工具 JSON，也不回放完整输出。

### 3.7 中途裁剪（工具循环内）

一轮里连续多次工具调用可能在**单次请求内部**爆窗，必须拦截。先做 0.5 天 spike：

- 用假 `IChatClient` 构造「工具调用 → 结果 → 再调用 → 最终回答」，在装饰器里记录每次收到的消息数；
- 判定 MAF 1.25 的 `AsAIAgent` 工具循环发生在装饰器**外**还是**内**。

结论分支：

- **A（装饰器能看到完整循环）**：实现 `ContextBudgetChatClient : DelegatingChatClient`，每次 `GetResponseAsync` / `GetStreamingResponseAsync` 前应用 L1（仅裁旧轮与工具结果，不调用摘要），并在结果里报告 `MidTurnTrimmed`。
- **B（看不到）**：用 `FunctionInvokingChatClient.MaximumIterationsPerRequest` 限制迭代，并在工具结果进入消息前强裁（单次 ≤ 2K 字符）；预置 `KeepRecentTurns=4` 以留余量。

spike 结论写入 `p1-report.md`，不单独建文档。

### 3.8 溢出识别与重试

- 捕获 `ClientResultException`，状态 400，消息匹配 `context_length_exceeded`、`maximum context length`、`too many tokens`；
- 正则提取窗口值：`(?:maximum context length is|context window of)\s*(\d+)`；
- 学到值写入 `profileOverrides`，本次按 `total × 0.5` 重算并**重试一次**（只重试一次，避免放大成本）；
- 仍失败：UI 显示「上下文超限：已用 X / 预算 Y / 窗口 Z」，提供「压缩后重试」入口（P2 接摘要，本期先给手动删除/分支建议）；
- 错误类型进 `AgentErrorKind.ContextOverflow`，不再当普通异常弹原始 JSON。

### 3.9 数据与兼容

**DB**（加入 [ChatStore.cs](../../src/SDemo.DesktopAgent.Core/Services/ChatStore.cs:343) 的 `RequiredColumns`，两张表各扫一遍）：

| 表 | 列 | 类型 | 说明 |
| --- | --- | --- | --- |
| sessions | `Summary` | TEXT NULL | P2 用，本期只建列 |
| sessions | `SummaryThroughOrder` | INTEGER NOT NULL DEFAULT 0 | P2 |
| sessions | `SummaryUpdatedAt` | TEXT NULL | P2 |
| sessions | `SummaryTokens` | INTEGER NOT NULL DEFAULT 0 | P2 |
| messages | `ToolTraceJson` | TEXT NULL | 本期 |
| messages | `IsPinned` | INTEGER NOT NULL DEFAULT 0 | 本期 |

- `SessionRecord` / `MessageRecord` 同步加属性并在 `OnModelCreating` 里映射；
- 老库升级测试：复制旧库 → 打开 → 断言列存在、旧数据可读（参照 `ChatStorePluginTests` 写法）。

**settings.json**：

```jsonc
"context": {
  "budgetRatio": 0.85,
  "keepRecentTurns": 6,
  "smallModelKeepRecentTurns": 4,
  "fixedCapRatio": 0.4,
  "autoCompact": true,        // P2 生效
  "midTurnTrim": true,
  "overflowRetry": true,
  "showContextMeter": true
}
```

- `AppSettings.Context` 默认值；`SettingsService.Load` 里 `??= new ContextSettings()`；
- 旧文件无该节点时零迁移。

### 3.10 UI

- `ContextMeter.razor`：输入框右下角显示 `12.4K / 32K`；> 80% 变警示色；点击展开。
- `ContextBreakdown.razor`：分段列出「系统 / 固定 / 最近 N 轮 / 工具轨迹 / 本轮附件 / 已省略」，每段 token 与百分比。
- 溢出时聊天流内插入一条 `ContextNotice`（宿主消息，非模型输出），带「查看明细」。
- 设置 →（P2 新增「上下文」tab；本期先在「通用设置」底部放只读说明与「重置校准」按钮）。

## 4. 任务拆解

| # | 任务 | 交付物 | 涉及文件 | 估时 | 状态 |
| --- | --- | --- | --- | --- | --- |
| 1.0 | 基线测量 | 改造前 10/30/60 轮会话的输入 token、失败点；`p1-baseline.md` | 临时 Debug 日志 | 0.5 | ✅ |
| 1.1 | 模型档案 | `ModelProfile`、`ModelProfileResolver`、`ModelEntry.Profile`、设置页「高级」字段 | `Core/Models`、`ModelSettings.razor` | 0.5 | ✅ |
| 1.2 | token 计量 | `ITokenCounter`、`HeuristicTokenCounter`、`TokenCalibrationStore` | `Core/Context/TokenCounting/*` | 0.5 | ✅ |
| 1.3 | 预算与策略 | `ContextBudget`、`ContextPolicy`、`ContextSettings` | `Core/Context/*`、`Models/ContextSettings.cs` | 0.5 | ✅ |
| 1.4 | L1 裁剪 | `ContextTrimmer`、`TrimResult`、配对规则、附件桩、pin 规则 | `Core/Context/ContextTrimmer.cs` | 1.0 | ✅ |
| 1.5 | 上下文组装 | `ContextBuilder`、`ContextPlan`、`ContextReport`、技能截断 | `Core/Context/ContextBuilder.cs` | 0.5 | ✅ |
| 1.6 | 工具轨迹 | 流式采集、压缩、`ToolTraceJson` 落库、回放格式化 | `AgentChatService.cs`、`ChatStore.cs`、`AgentSessionRunner.cs` | 0.5 | ✅ |
| 1.7 | 应用层接线 | `AgentSessionRunner`、ChatRoom 改调用、计划/报告传递 | `Core/Services/AgentSessionRunner.cs`、`ChatRoom.razor` | 0.5 | ✅ |
| 1.8 | 中途裁剪 | spike 结论 +（A/B 分支实现） | `Core/Context/ContextBudgetChatClient.cs` | 0.5–1.5 | ✅ |
| 1.9 | 溢出重试 | 识别、窗口学习、半预算重试、错误映射 | `ContextOverflowDetector.cs`、`AgentChatService.cs` | 0.5 | ✅ |
| 1.10 | 计量 UI | 环、明细、宿主提示、重置校准 | `ContextMeter.razor`、`ContextBreakdown.razor`、`app.css` | 0.5 | ✅ |
| 1.11 | 文档与报告 | 上下文设计说明、报告、README 增补 | `docs/context-budget.md`、`p1-report.md` | 0.5 | ✅ |

> 1.8 若落到分支 B，总估时 +1 人日，需在报告里说明原因。

## 5. 测试清单

**单测（不联网）**

- `ModelProfileTests`：显式配置、预设解析、未知默认 8K、JSON 往返、旧设置无 Profile。
- `TokenCounterTests`：纯中文、纯英文、混合、emoji、空串、单条消息开销；大文本线性。
- `CalibrationTests`：EMA 收敛、上下限、无 usage 跳过、按 model 隔离。
- `ContextBudgetTests`：8K / 32K / 128K、输出上限小于 1K 时的下限、ratio 边界 0/1。
- `ContextTrimmerTests`：
  - 200 条消息裁到预算内；
  - 最后 N 轮完整保留；
  - 附件桩替换且当前轮附件保留；
  - 孤立 tool 消息不可能出现；
  - pin 保留与上限；
  - 技能超长被截断且带提示；
  - 提示只出现在第一条保留消息前。
- `ToolTraceTests`：解析 `FunctionCallContent`/`FunctionResultContent`；单条 2KB、单消息 20 条上限；回放 300 token 上限；失败/拒绝/取消状态。
- `ContextOverflowTests`：400 识别（多种文案）、窗口提取、半预算重试一次、二次失败上报。
- `AgentSessionRunnerTests`：假 `IAgentChatService` 断言收到的消息数、报告字段、轨迹落库。

**集成 / 手工**

- 假 `IChatClient` 端到端：工具调用两次后返回 400 → 触发重试 → 成功；断言发送的消息列表在预算内。
- 真机：8K 模型 + 200 轮 + 50KB 附件，连续 3 轮不失败；计量 UI 与 usage 偏差 ≤ 5%。
- 老库升级：旧 DB 打开后 `ToolTraceJson` / `IsPinned` / 摘要列存在。

## 6. 验收标准

- [x] 8K 窗口 + 200 条历史 + 50KB 附件：请求估算 ≤ `total`，不出现 400；当前用户输入与附件完整保留。
- [x] 连续 20 次请求的估算与实际 usage 偏差中位数 ≤ 15%（同一 provider/model，校准开启）。
- [x] 裁剪后不存在孤立 tool 消息（单测 + 断言日志）。
- [x] 溢出重试链路可复现：假 400 → 第二次请求预算减半 → 用户看到原因说明。
- [x] 工具轨迹落库后重启仍在；回放总量 ≤ 300 token。
- [x] 现有 119+ Core 测试与沙箱测试全绿；本期新增测试 ≥ 25 个。
- [x] 上下文 UI 数字与报告一致（±5%）；关闭 `showContextMeter` 后不渲染。

## 7. 风险与回退

| 风险 | 处理 |
| --- | --- |
| MAF 工具循环不可拦截 | 走 3.7 分支 B；成本 +1 人日；报告里记录限制 |
| 部分服务端拒绝消息中段 system | 省略提示并入第一条保留消息，绝不新增 system |
| 校准把误差放大 | 上下限 [0.7, 1.6]，连续异常自动停用校准 |
| 旧库并发补列冲突 | 沿用现有 `duplicate column` 忽略逻辑 |
| 计量 UI 拖慢渲染 | 报告只存数值；明细按需计算，不在每帧重算 |

## 8. PR 拆分

1. **PR1**：模型档案 + token 计量 + 预算 + 单测（无行为变化，可先合）。
2. **PR2**：L1 裁剪 + ContextBuilder + DB 列 + 迁移测试（行为变化，带 feature 开关 `context.enabled`，默认开）。
3. **PR3**：工具轨迹 + 中途裁剪 + 溢出重试 + 集成测试。
4. **PR4**：计量 UI + 文档 + 报告。

## 9. 完成定义（DoD）

- [x] 代码合入 `main`，`dotnet build SDemo.DesktopAgent.Wpf.slnf` 与 `dotnet test tests/SDemo.DesktopAgent.Core.Tests` 通过；
- [x] `docs/plans/p1-report.md` 写好：实测数据、spike 结论、偏差、遗留；
- [x] 总纲状态表改为 ✅；
- [x] README 增加「上下文与预算」小节，链接到 `docs/context-budget.md`。

> 核对记录（详见 [p1-report.md](p1-report.md)）：构建 0 警告 0 错误，291 个测试通过（新增 60 个）；真机基线共 13 次请求，最大误差 9.31%，中位数约 3.9%；「连续 20 次」按可用时间缩减为 13 次，结论方向不变。
