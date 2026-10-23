# P2 — 压缩与工作记忆（第 2 期）

> 状态：✅ 完成（2026-10-22） · 估时 6 人日 · 前置：P1 · 出口：超预算时只隐藏不删除，压缩后还能接着干活
> 关联总纲：[vibe-coding-roadmap.md](vibe-coding-roadmap.md) · 决策：只隐藏不删除 / 摘要模型可配置
> 结果：[p2-report.md](p2-report.md) · 真机数据：[p2-golden-questions.md](p2-golden-questions.md)

## 1. 本期要解决的问题

P1 只能「裁」：裁掉的轮次就真的没了。用户会立刻遇到两个问题：

1. **约束丢失**：第 3 轮说“不要动数据库”，第 60 轮模型已经忘了；
2. **没有可信的恢复手段**：用户不知道被裁了什么，也无法纠正。

本期补两层：**滚动摘要**（覆盖被淘汰的历史）+ **工作记忆状态包**（覆盖“现在在哪、改了什么”）。两者都不能靠用户手动维护。

## 2. 范围

**做**

- 增量滚动摘要：模板固定、只写事实、带来源消息序号、可回链；
- 确定性兜底摘要：模型失败 / 超时 / 格式不符时不阻塞；
- 摘要触发与滞回：请求前按预算触发，压缩后至少再跑 6–10 轮；
- 工作记忆状态包：工作区结构、最近改动文件、最近工具动作；
- 压缩透明度：分隔线、展开原文、pin / 取消 pin、「压缩现在」；
- 设置页「上下文」分区；
- 10 问 golden 检查（早期约束不丢）。

**不做**

- todo / 计划面板 → P5；
- 构建/测试结果进状态包 → P4 提供数据源后接入（本期留接口）；
- 跨会话记忆、向量检索 → 本周期不做。

## 3. 设计要点

### 3.1 压缩流水线

```
AgentSessionRunner.StreamAsync
  1. preflight = ContextBuilder.Build(...)          // 不含摘要
  2. if preflight.OmittedMessages > 0 && autoCompact:
        ContextCompactor.EnsureAsync(session, preflight)
        重新 Build（此时带 summary + state）
  3. if Build 抛 NeedsCompaction（单条就超）:
        压缩后重建；仍超才报错（带报告）
```

> 实际实现（见报告 §6.1）：P1 的裁剪器总会把请求裁到预算内，因此「超预算」表现为
> `OmittedMessages > 0`（已经开始丢历史），而不是 plan 的 `Estimated > Total`。

`EnsureAsync` 的增量逻辑：

```
chunkStart = session.SummaryThroughOrder          // 上次摘要覆盖到哪
keepFrom   = 最后 KeepRecentTurns 轮的起始序号
chunk      = messages[chunkStart .. keepFrom]     // 只喂新增的一段
if chunk < 4 轮 && saving < 200 token: return     // 避免每轮都摘要
newSummary = summarizer.Summarize(session.Summary, chunk)
if 失败/格式不符: newSummary = extractive(chunk)  // 兜底
session.Summary = Cap(newSummary, settings.SummaryMaxTokens)
session.SummaryThroughOrder = keepFrom
session.SummaryUpdatedAt = now
session.SummaryTokens = counter.Count(newSummary)
ChatStore.SaveSessionAsync(session)
```

- **滞回**：压缩到 `total × 0.6`；下次只有再次超过 `total` 才触发（约 6–10 轮后）。
- **原子性**：摘要与 `SummaryThroughOrder` 一次保存；保存失败则本轮不启用摘要（退回 L1），不阻塞请求。
- **原文不动**：`messages` 表一条不删、一条不改，仅靠 `SummaryThroughOrder` 标记“这之前的内容由摘要代表”。

### 3.2 摘要器

```csharp
public interface IContextSummarizer
{
    Task<SummaryResult> SummarizeAsync(
        string? previousSummary,
        IReadOnlyList<ChatMessageModel> chunk,
        ModelProvider provider,
        string modelId,
        CancellationToken cancellationToken);
}
```

实现：

- `LlmContextSummarizer`：走 OpenAI 兼容接口，`temperature=0`，`max_tokens = SummaryMaxTokens`（默认 800），无工具、无历史；
- `ExtractiveContextSummarizer`：纯规则，永远成功；
- `ContextCompactor` 先调 LLM，异常 / 超时（默认 20s）/ 章节缺失 / 超长 → 兜底。

**摘要提示词（固定模板，禁止自由格式）**：

```text
你在压缩一段开发会话的历史。只陈述事实，不写建议、不写推测。
输出必须且只能包含以下小节，保持顺序：

## 目标与约束
## 已确认决策
## 关键事实
## 已改动
## 未完成
## 用户偏好

要求：
- 每条事实末尾用 [m12] 标注来源消息序号；
- 路径、命令、错误码保留原文，不要改写；
- 总长度不超过 600 字；
- 没有内容的小节写「无」；
- 不得编造未出现过的文件、命令或结论。
```

**校验**：6 个标题齐全、总长 ≤ `SummaryMaxTokens`、来源序号可解析；任一不满足即走兜底。

**兜底格式**（开头标记 `（自动摘要，未使用模型）`）：

```
## 目标与约束
- 用户最近诉求：<最后 3 条用户消息各 120 字>
- 用户早期诉求：<最早 4 条用户消息各 120 字>   # 保住首块历史里的长期约束
## 已确认决策
- 无（自动摘要无法可靠提取）
## 关键事实
- <最近 20 条工具轨迹：read_file src/a.cs / apply_patch src/b.cs (+12 -3) / run_command dotnet build → error>
## 已改动
- <轨迹里去重后的写入路径>
## 未完成
- 无
## 用户偏好
- 无
```

> 兜底宁可少写，也不编造。用户在 UI 上能看到「自动摘要」标记。

### 3.3 工作记忆状态包

由宿主事实拼装，**不调用模型**，每轮重建：

```csharp
public sealed record WorkspaceState(
    string Root,
    IReadOnlyList<string> Tree,             // 深度 2，≤120 项，排除 bin/obj/node_modules/.git/.vs/.tmp
    IReadOnlyList<RecentEdit> RecentEdits,  // 来自会话工具轨迹，最多 8 个，路径 + hash 前缀
    string? LastVerification,               // P4 接入；本期输出「未运行验证」或空
    int PinnedCount);
```

渲染格式（≤800 token，小模型 ≤500）：

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
  src/Web/Program.cs（sha256:81cd，10:44）

## 最近动作
  [10:41] read_file src/Api/Options.cs → ok
  [10:43] apply_patch src/Api/Options.cs → ok (+8 -2)
  [10:45] dotnet build → error(exit 1)

## 验证
未运行验证
```

- 树缓存 30 秒，避免每轮扫盘；
- 排除规则集中在 `StatePackFormatter.DefaultExcludes`，后续可配置；
- 内容超限时按「结构 → 最近改动 → 最近动作」顺序截断，**验证结果最后截**（它最重要）。

### 3.4 透明压缩 UX

- **分隔线**：在 `SummaryThroughOrder` 对应的位置插入「已压缩 N 条历史（约 X token）· 查看原文」；
- **查看原文**：只读弹层，按原顺序列出被压缩的消息；可单条「pin」；
- **pin**：消息悬浮操作里的图钉；pin 的消息永不淘汰，数量上限 20，超出时提示先取消；
- **压缩现在**：会话菜单项，手动触发一次增量压缩（失败走兜底并提示）；
- **设置 → 上下文**：预算比例、保留轮数（普通 / 小模型）、固定块比例、自动压缩、中途裁剪、摘要模型（默认「当前会话模型」）、摘要上限 token、校准重置。

### 3.5 摘要模型配置

- `ContextSettings.SummaryProviderId` / `SummaryModelId` 为空表示用当前会话的 provider/model；
- 下拉列表来自「设置 → 模型」的目录；
- 校验：保存时确认 provider 存在、模型非空；不存在则回退默认并提示；
- 摘要请求**不**写会话消息、不显示在聊天流，只在报告与日志里记 token 消耗。

### 3.6 数据

P1 已建列，本期开始写入：`Summary`、`SummaryThroughOrder`、`SummaryUpdatedAt`、`SummaryTokens`。

- 加载会话时若 `SummaryThroughOrder` 大于消息数（库被手工改过），视为 0 并清空摘要；
- 删除消息（现有分支/删除会话）时，若删到摘要覆盖范围，`SummaryThroughOrder` 归零、摘要保留但标记「可能过期」，下次压缩重算；
- 「清空摘要」入口放会话菜单，清空后下轮重新压缩。

## 4. 任务拆解

| # | 任务 | 交付物 | 涉及文件 | 估时 | 状态 |
| --- | --- | --- | --- | --- | --- |
| 2.1 | 摘要器接口与提示词 | `IContextSummarizer`、`SummaryResult`、模板常量 | `Core/Context/Summarizers/*` | 0.5 | ✅ |
| 2.2 | LLM 摘要实现 | 请求组装、20s 超时、格式校验、用量记录 | `LlmContextSummarizer.cs` | 1.0 | ✅ |
| 2.3 | 确定性兜底 | `ExtractiveContextSummarizer`、轨迹摘要 | `ExtractiveContextSummarizer.cs` | 0.5 | ✅ |
| 2.4 | 压缩编排 | `ContextCompactor`、增量、滞回、原子保存、失败降级 | `ContextCompactor.cs` | 1.0 | ✅ |
| 2.5 | 状态包 | `WorkspaceStateProvider`、`StatePackFormatter`、缓存与排除 | `Core/Context/State/*` | 1.0 | ✅ |
| 2.6 | 流水线接线 | preflight → compact → rebuild；报告加 `SummaryUsed` | `AgentSessionRunner.cs`、`ContextBuilder.cs` | 0.5 | ✅ |
| 2.7 | 透明 UX | 分隔线、原文弹层、pin、压缩现在、清空摘要 | `CompactionDivider.razor`、`ChatRoom.razor` | 1.0 | ✅ |
| 2.8 | 设置分区 | `SettingsSection.Context`、`ContextSettings.razor` | `SettingsView.razor`、新组件 | 0.5 | ✅ |
| 2.9 | 10 问 golden | 问题集 + 手工记录表 + 结果 | `docs/reports/p2-golden-questions.md` | 0.5 | ✅ |
| 2.10 | 文档与报告 | `docs/architecture/context-compaction.md`、`p2-report.md` | docs | 0.5 | ✅ |

## 5. 测试清单

**单测**

- `SummaryFormatTests`：6 节齐全、超长截断、缺节判定、来源序号解析。
- `LlmSummarizerTests`：假 HTTP/假 client 返回正常 / 超时 / 非 JSON / 超长；断言降级。
- `ExtractiveSummaryTests`：无模型时产出稳定；不编造路径；空 chunk。
- `CompactionTests`：
  - 100 条消息压缩后估算 ≤ `total`；
  - `SummaryThroughOrder` 单调前进、不重复摘要；
  - chunk < 4 轮且节省 < 200 token 时不调用摘要器；
  - 保存失败退回 L1；
  - 摘要覆盖范围被删除后归零。
- `StatePackTests`：排除目录、深度/条目上限、超限截断顺序、缓存命中、≤800/500 token。
- `PinTests`：pin 保留、上限 20、超限提示、取消 pin 后可淘汰。
- `ChatStoreSummaryTests`：摘要读写、重启保留、老库缺列升级。

**集成 / 手工**

- 长会话脚本：构造 120 轮（含 3 个早期约束）→ 触发压缩 → 问 10 个问题（5 个早期约束、3 个改动文件、2 个当前状态）。
- 断网 / 假 500：摘要必走兜底，用户无感（只有标记）。
- 重启后：分隔线位置、摘要内容、pin 状态一致。

## 6. 验收标准

- [x] 120 轮会话压缩后，单次请求估算 ≤ `total × 0.62`；压缩后还能再跑 ≥ 6 轮不触发。
  - 实测 8K：1070 / 4874 = 22.0%；32K：993 / 22978 = 4.3%。滞回由 `KeepRecentTurns` 决定（8K 默认 6 轮）。
- [x] 10 问 golden：≥ 8 题正确；错误集中在「自动摘要」模式下允许 7/10。
  - 模型摘要 10/10；强制兜底 10/10；32K（末批兜底）10/10。见 [p2-golden-questions.md](p2-golden-questions.md)。
- [x] 摘要请求失败（超时 / 500 / 格式不符）时，本轮仍能正常回答，UI 标注「自动摘要」。
  - 单测覆盖；格式不符会先重试一次，再兜底。
- [x] 原文一条不少；分隔线、展开、pin、取消 pin 均可用；重启后状态保持。
  - 代码与持久化单测覆盖；UI 构建通过且启动冒烟无 DI 异常，未做像素级人工走查（见报告 §6.9）。
- [x] 未超预算时**不产生任何摘要调用**（用假 client 断言调用次数为 0）。
- [x] 状态包 ≤ 800 token（小模型 ≤ 500），且包含最近改动与验证状态。
- [x] 新增测试 ≥ 20 个；现有测试全绿。新增 57 个，全量 346 通过。

## 7. 风险与回退

| 风险 | 处理 |
| --- | --- |
| 摘要编造事实 | 只允许模板 + 来源序号；校验不通过即兜底；golden 10 问兜底 |
| 摘要模型自身超窗 | chunk 先按 8K 预算切块，分多次摘要后合并；单次输入 ≤ 6K |
| 压缩打断 prompt cache | 低频 + 批量；压缩后至少 6 轮不触发 |
| 用户误解「原文还在但模型看不到」 | 分隔线文案写清「模型不再看到，点击可查看」 |
| 状态包扫盘慢 | 30s 缓存 + 排除目录 + 条目上限；异步预热可选 |
| 摘要与分支冲突 | 删除覆盖范围即归零重算，绝不使用可疑摘要 |

## 8. PR 拆分

1. **PR1**：摘要器接口 + 兜底 + 格式校验 + 单测。
2. **PR2**：压缩编排 + 存储 + 流水线接线 + 集成测试。
3. **PR3**：状态包 + 报告字段。
4. **PR4**：透明 UX + 设置分区 + 文档/报告。

## 9. 完成定义（DoD）

- [x] 压缩链路在真机 8K / 32K 模型上各跑一遍，数据进报告；
- [x] golden 10 问结果存档；
- [x] 总纲状态表改为 ✅，指标表补「压缩有效率」实测值；
- [x] `docs/architecture/context-compaction.md` 含调参建议与已知限制。
