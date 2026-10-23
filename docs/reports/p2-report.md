# P2 报告：压缩与工作记忆

> 期次：P2（第 2 期）　分支：`feature/vibe-p2-compaction`　状态：✅ 完成
> 配套文档：[context-compaction.md](../architecture/context-compaction.md)、[p2-golden-questions.md](p2-golden-questions.md)、[p2-golden-data.json](p2-golden-data.json)、[p2-golden-32k-data.json](p2-golden-32k-data.json)、[p2-golden-fallback-data.json](p2-golden-fallback-data.json)
> 原始计划：[p2-compaction-memory.md](p2-compaction-memory.md)

## 1. 交付物

| 模块 | 文件 |
| --- | --- |
| 摘要接口 / 结果 | `Core/Context/Summarizers/IContextSummarizer.cs` |
| 模板 / 校验 / 截断 / 兜底标记 | `Core/Context/Summarizers/SummaryFormat.cs` |
| 提示词（含格式纠正） | `Core/Context/Summarizers/SummaryPrompt.cs` |
| 模型摘要 | `Core/Context/Summarizers/LlmContextSummarizer.cs` |
| OpenAI 兼容传输 | `Core/Context/Summarizers/OpenAiSummaryChatTransport.cs` |
| 确定性兜底 | `Core/Context/Summarizers/ExtractiveContextSummarizer.cs` |
| 压缩编排 | `Core/Context/ContextCompactor.cs` |
| 工作区状态包 | `Core/Context/State/WorkspaceState.cs`、`WorkspaceStateProvider.cs`、`StatePackFormatter.cs` |
| 报告扩展 | `Core/Context/ContextReport.cs`（`SummaryUsed` / `SummaryFallback` / `CompactedMessages` / `Segments.Summary` / `Segments.StatePack`） |
| 构建器 | `Core/Context/ContextBuilder.cs`（摘要 / 状态包注入块、跳过范围、分段计量） |
| 裁剪器 | `Core/Context/ContextTrimmer.cs`（`SkipBefore`、pin 复活、压缩提示） |
| 流水线 | `Core/Services/AgentSessionRunner.cs`（`BuildOrCompactAsync`、`CompactAsync`、摘要模型解析） |
| 持久化 | `Core/Models/ChatSession.cs`、`Core/Services/ChatStore.cs`（四个摘要列读写 + 越界归零） |
| pin 规则 | `Core/Context/PinPolicy.cs` |
| 设置 | `Core/Models/ContextSettings.cs`（摘要模型 / 上限 / 状态包 / 超时 / pin 上限） |
| 组合根 | `Core/Composition/AgentCoreServiceCollectionExtensions.cs` |
| UI | `Chat/CompactionDivider.razor`、`ChatRoom.razor`（分隔线 / 原文弹层 / pin / 压缩现在 / 清空摘要）、`ContextBreakdown.razor`、`Settings/ContextSettingsPanel.razor`、`SettingsSection.cs`、`SettingsView.razor`、`AppShell.razor`（分支克隆摘要）、`wwwroot/css/app.css` |
| 真机工具 | `tools/p2-golden/*`（8K / 32K / 强制兜底三种模式） |
| 文档 | `docs/architecture/context-compaction.md`、本报告、`p2-golden-questions.md`、计划状态与路线图更新 |

## 2. 计划任务对照

| # | 任务 | 状态 | 说明 |
| --- | --- | --- | --- |
| 2.1 | 摘要器接口与提示词 | ✅ | `IContextSummarizer` / `SummaryRequest` / `SummaryResult`；固定 6 节模板 |
| 2.2 | LLM 摘要实现 | ✅ | 20s 超时、`temperature=0`、格式校验、**格式不符重试一次** |
| 2.3 | 确定性兜底 | ✅ | 永远成功；保留最早 + 最近用户消息、最近 20 条轨迹、写入路径去重 |
| 2.4 | 压缩编排 | ✅ | 增量、分块、pin 下界、阈值跳过、原子保存与回滚、失败降级 |
| 2.5 | 状态包 | ✅ | 树深度 2 / ≤120 项、30s 缓存、最近 8 改动、最近 12 动作、截断顺序 |
| 2.6 | 流水线接线 | ✅ | L1 需要淘汰历史时先压缩再重建；压缩失败继续硬裁剪；无 `yield` in catch |
| 2.7 | 透明 UX | ✅ | 分隔线、只读原文弹层、pin、压缩现在、清空摘要 |
| 2.8 | 设置分区 | ✅ | 新增「上下文」tab（`ContextSettingsPanel`）；从「通用」移除旧区块 |
| 2.9 | 10 问 golden | ✅ | 8K 10/10、8K 强制兜底 10/10、32K 10/10；数据已存档 |
| 2.10 | 文档与报告 | ✅ | 本报告 + 压缩文档 + golden 问题集 |

## 3. 完成定义（DoD）核对

| DoD | 结果 | 证据 |
| --- | --- | --- |
| 压缩后单次请求 ≤ `total × 0.62` | ✅ | 8K：1070 / 4874 = **22.0%**；32K：993 / 22978 = **4.3%** |
| 压缩后还能再跑 ≥ 6 轮不触发 | ✅ | 触发条件是 L1 有淘汰；压缩覆盖到最后 6 轮起点，需再涨满 6 轮才产生新淘汰 |
| 10 问 golden ≥ 8 | ✅ | 模型摘要 **10/10**；强制兜底 **10/10**（计划允许 7/10）；32K **10/10** |
| 摘要失败仍能正常回答并标注 | ✅ | 单测（超时 / 异常 / 格式）+ 强制兜底 golden；UI 用兜底标记显示「自动摘要」 |
| 原文一条不少 | ✅ | 压缩只写 `ChatSession.Summary*`，不删改 `ChatMessage`；`ChatStoreSummaryTests` + 代码审查 |
| 分隔线 / 展开 / pin / 取消 pin 可用 | ✅ | 组件与事件已接；构建通过。**未做像素级人工走查**（见 §6.9） |
| 重启后状态保持 | ✅ | 摘要四列读写、越界归零、兜底标记识别有单测；分支克隆有摘要复制规则 |
| 未超预算不产生摘要调用 | ✅ | 新单测断言 `FakeSummarizer.Calls == 0` |
| 状态包 ≤ 800 / 小模型 ≤ 500 | ✅ | 单测覆盖 800、500、300 三档与截断顺序 |
| 新增测试 ≥ 20 | ✅ | 新增 **57** 个 P2 用例；全量 **346** 通过 |
| 文档 | ✅ | `context-compaction.md`、本报告、golden 问题集、路线图状态 / 指标 |

## 4. 测试

新增 57 个用例（全部确定性单测，无网络）：

| 文件 | 数量 | 覆盖 |
| --- | --- | --- |
| `SummaryFormatTests` | 7 | 6 节齐全、缺节 / 乱序 / 多余节、来源序号、截断、兜底标记与来源解析 |
| `ContextSummarizerTests` | 10 | 模型成功、格式失败、超时、传输异常、格式重试、重试仍失败、兜底内容 / 续写 / 去重 / 最早约束 |
| `ContextCompactorTests` | 9 | 覆盖范围、二次不重复、阈值跳过、强制压缩、保存失败回滚、pin 下界、兜底、分块、持久化 |
| `StatePackTests` | 9 | 扫描排除 / 深度 / 上限、预算与截断顺序、800/500 档、缓存、最近改动去重 |
| `PinPolicyTests` | 4 | 计数、上限拒绝、上限 0、取消不受上限影响 |
| `ChatStoreSummaryTests` | 3 | 摘要读写、重启后兜底识别、覆盖范围越界归零 |
| `ContextBuilderSummaryTests` | 6 | 摘要 / 状态包分段、被覆盖消息不发送、pin 复活、超长截断、直通、空注入 |
| `AgentSessionRunnerCompactionTests` | 9 | L1 触发压缩、关闭开关、摘要失败兜底、预览不压缩、手动压缩、无压缩器、L1 不可压缩、未超预算零调用 |

`dotnet test tests/SDemo.DesktopAgent.Core.Tests`：**346 通过 / 0 失败 / 0 跳过**。
`dotnet build SDemo.DesktopAgent.Wpf.slnf`：**0 错误**（仅离线环境的 NU1900 漏洞源警告）。

## 5. 真机 golden 摘要

| 场景 | 轮次 | 压缩 | 摘要 | 兜底 | 压缩后估算 / 预算 | 10 问 |
| --- | --- | --- | --- | --- | --- | --- |
| 8K 模型摘要 | 120 | 228 条 | 316 token | 否 | 1070 / 4874（22%） | **10/10** |
| 8K 强制兜底 | 120 | 228 条 | 498 token | 是 | 1253 / 4874（26%） | **10/10** |
| 32K 模型摘要（末批兜底） | 300 | 588 条 | 239 token | 是 | 993 / 22978（4%） | **10/10** |

模型：`火山 / deepseek-v4.1-flash`；单次压缩耗时约 8–29 秒（含多次摘要请求）。
逐题回答与判定见 [p2-golden-questions.md](p2-golden-questions.md)。

稳定性：8K 正常模式 6 次运行中 5 次模型摘要成功、1 次某批格式不符走兜底；加入格式重试后连续 3 次成功。32K 运行中 1 个批次重试后仍失败，最终混合摘要（前面批次模型摘要 + 末批兜底）仍 10/10。

## 6. 偏差与取舍

1. **触发条件改为「L1 需要淘汰历史」**。计划写的是 `preflight.OverBudget`，但 P1 的 `ContextBuilder` 总会把请求裁到预算内，正常运行不会出现「估算 > total」的 plan。P2 用 `OmittedMessages > 0` 表示「本来会超、已经开始丢历史」，此时先摘要再重建；`Build` 仍抛 `NeedsCompaction` 的极端情况（单条消息就超预算）走压缩后重建、仍不行才报错。这样既满足「超预算才压缩」，也避免每轮摘要。
2. **摘要 / 状态包不受 `FixedCap` 约束**。`FixedCap` 继续只限制技能块；摘要用 `SummaryMaxTokens`、状态包用 `StatePackTokenBudget`，两者都计入 `Total`，从消息预算里扣除。否则 8K 下固定块会把消息空间挤没。
3. **兜底摘要保留「最早 4 条 + 最近 3 条」用户消息**，而不是计划里的「最近 3 条」。真机发现只取最近 3 条会在首块历史丢失早期约束；加入最早几条后，强制兜底 golden 从 3/10 提升到 10/10。代价是兜底摘要稍长（498 token），仍在上限内。
4. **格式不符增加一次重试**。计划只要求「失败走兜底」；真机观察到模型偶尔只输出 3 节，直接兜底会降低质量。现在先追加格式纠正提示重试一次，仍失败才兜底；超时与网络错误不重试。
5. **pin 会「复活」被摘要覆盖的消息**。计划说 pin 的消息不淘汰；实现上只要 pin 位于 `SummaryThroughOrder` 之前，它仍以原文发送，且不计入「已压缩」省略数；`RenderedItem.Compacted` 只在「被覆盖且未 pin」时为 true。
6. **`SummaryIsFallback` 不落库**，由摘要正文开头的兜底标记派生；避免新增列与历史数据不一致。
7. **`ContextReport.CompactedMessages` 是本请求实际隐藏的条数**（`Compacted` 且未 pin），不是 `SummaryThroughOrder`；UI 分隔线的 N 用 `SummaryThroughOrder`（覆盖范围上界）。无 pin 时两者相等。
8. **状态包未进 golden**。golden 关闭 `StatePackEnabled`，只测摘要链路；状态包的排除 / 缓存 / 截断由单测覆盖。真机状态包的效果要等 P3/P4 有真实文件改动与构建结果后再测。
9. **UI 只做到构建、启动冒烟与逻辑验证**。分隔线、弹层、pin、菜单、设置分区均已接好，`Wpf.slnf` 构建 0 错误，应用启动无 DI 异常；没有 bUnit / 截图比对，也没做完整人工点击走查。P1 的同类限制继续保留。
10. **设置组件命名**为 `ContextSettingsPanel.razor`（计划里叫 `ContextSettings.razor`），避免与 `Core/Models/ContextSettings` 重名。
11. **8K / 32K 是预算档位，不是物理窗口**：沿用 P1 的做法，用 `ModelProfile` 把预算压到对应档位；请求大小由预算决定，结论仍然成立。

## 7. 遗留与风险

| 项 | 影响 | 建议 |
| --- | --- | --- |
| 模型摘要格式仍会偶发失败 | 兜底质量较低、摘要变长 | 监控兜底率；换指令遵循更稳的摘要模型；已加重试 |
| 摘要请求的 token 成本未进报告 | 用户看不到摘要花了多少 | 后续在 `ContextReport` 加摘要 input/output usage；当前只在日志 |
| 压缩后仍可能接近预算 | 最近几轮本身很大时，0.62 不是硬保证 | 调小 `KeepRecentTurns` / 状态包；必要时继续 L1 裁剪 |
| 压缩成功但重建仍超限 | 本轮用旧 L1 结果发送，摘要留给下一轮，可能浪费一次摘要 | 已捕获并回退；后续可增加「压缩前预演」避免无效摘要 |
| UI 未做自动化 / 像素走查 | 交互回归只能靠手动 | P3 前补 bUnit 或截图冒烟 |
| 状态包验证块固定「未运行验证」 | P4 前信息不完整 | P4 接入构建/测试结果 |
| 分支克隆只复制覆盖范围内的摘要 | 分支历史与摘要可能不一致 | 现有规则：`SummaryThroughOrder <= 分支消息数` 才复制，否则清空；已实现 |
| 真实 8K 物理模型未压测 | 仍是预算档模拟 | P6 用真实小模型复测 |

## 8. 提交

单次提交在 `feature/vibe-p2-compaction` 分支，包含代码、测试、golden 工具与文档；未推送远端（按约定只留本地分支）。
