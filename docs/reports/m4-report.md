# M4 报告：路由升级与评测

> 期次：M4（能力路由第 4 阶段）　状态：✅ 代码 / 单测 / 构建 / 路由评测完成（未重跑真机 8K 长任务）
> 配套文档：[capability-routing.md](../architecture/capability-routing.md)（§11 为 M4 实施记录）
> 验证日期：2026-10-23　分支：当前工作区（基于 M1–M3 未提交改动）

## 1. 结论摘要

1. **规则不确定时才花模型调用**：只有 Full、工具数超上限、输入有任务信号，且意图未知 / 多意图 / 命中工具都不可用时，才发一次 JSON 路由请求；Compact 永不调用。
2. **失败不阻塞**：超时（默认 8 秒）、网络错误、JSON 非法、空建议都回落规则；`RoutingDecision.Source` 保持 `rules`，并在 Reason 里写明回落原因。
3. **模型建议不能越权**：建议只决定优先顺序；宿主按「平台支持 ∩ 用户启用」过滤未知工具，规则结果补满剩余名额，元工具仍由宿主追加，窗口 / 模式上限不变。
4. **路由结果可观测**：`RoutingDecision` 新增 `Source`，分解面板显示「工具路由（规则 / 小模型）」；`AgentRunMetrics` 暴露「暴露 N 个 / 实际调用 M 个 / 未用 / 越权调用」。
5. **评测集 24 条**：离线规则 24/24（100%）；DeepSeek Flash 在线 24/24（100%，仅 7 次分类调用）；全量施压 22 次分类调用仍 24/24。
6. **回归**：Core **616 通过 / 0 失败**（新增 15 个），Sandbox **214 通过 / 1 跳过**，全解决方案构建 0 错误（仅 `p1-baseline` 既有 CA1416 警告）。

## 2. 交付物

| 模块 | 文件 |
| --- | --- |
| 路由建议契约 | `Contracts/Context/RoutingAdvice.cs`（建议 / 能力 / 请求 / `IRoutingAdvisor`）、`Contracts/Context/IRoutingChatTransport.cs` |
| 决策来源 | `Contracts/Context/RoutingDecision.cs`（`Source` + `RulesSource` / `SmallModelSource`） |
| 设置 | `Contracts/Models/AgentSettings.cs`：`SmallModelRoutingEnabled`、`RoutingProviderId/ModelId`、`RoutingTimeoutSeconds` |
| 规则触发 / 建议落地 | `Tools.Abstractions/CapabilityRouter.cs`：`ShouldConsultAdvisor`、`ApplyAdvice`、`ClassifyIntent`、`Finalize` 重构 |
| 小模型建议 | `Core/Services/SmallModelRoutingAdvisor.cs`：提示词 + JSON 解析 + 白名单 + 2 分钟缓存 |
| 传输 | `Agents/OpenAiRoutingChatTransport.cs`（`ResponseFormat.ForJsonSchema`；端点不支持时退回 JSON 模式）、DI 注册 |
| 接线 | `Core/Services/AgentSessionRunner.cs`（`ResolveRoutingAsync` / `PreviewAsync`）、`AgentChatService.cs`（记录路由） |
| 指标 | `Contracts/Services/AgentRunMetrics.cs`（暴露 / 调用 / 未用 / 越权） |
| 界面 | `ChatRoom.razor`（异步预览 + 版本号）、`ContextBreakdown.razor`（来源）、`ToolsSettings.razor`（开关 + 超时） |
| 评测 | 新工程 `tools/routing-eval`；`eval/routing/cases.json`；`AgentEval` transcript 增加 `Routing` 每轮记录 |
| 数据 | `docs/reports/data/m4-routing-eval.json`、`m4-routing-eval-live.json`、`m4-routing-eval-live-always.json` |
| 文档 | 本文 + `capability-routing.md` §11 + `eval/README.md` |

## 3. 验收核对

| 验收标准 | 结果 | 证据 |
| --- | --- | --- |
| 规则不确定时调用小模型路由（JSON Schema） | ✅ | `SmallModelRoutingAdvisorTests`（合法 / 代码块 / 未知工具 / 非法 JSON / 传输失败 / 缓存 / 提示词）、`CapabilityRouterTests`（建议落地 / 全非法回落 / 触发条件） |
| 失败回落规则，不阻塞对话 | ✅ | `AgentSessionRunnerRoutingTests.小模型失败时回落规则并记录原因`；advisor 契约返回 null，Runner 捕获超时/异常并追加 Reason |
| 评测集路由命中率 ≥90% | ✅ | 离线 24/24（100%）；在线 DeepSeek Flash 24/24（100%）；`docs/reports/m4-routing-eval*.json` |
| 指标：暴露 vs 实际调用 | ✅ | `AgentRunMetricsTests.暴露与实际调用可对比`；`AgentChatService` 每轮 `RecordRouting`；AgentEval transcript 写 `Routing`（exposed / called / unused） |
| 8K 任务完成率不低于 M1 | ⚠️ 未实测 | 代码路径未改 Compact 规则；`AgentEval` 已能记录每轮路由，但本机未加载 8K 模型，未重跑 T01–T08 |
| 小模型路由不可用时自动降级 | ✅ | 设置关闭 / Compact / 超时 / 传输异常 / JSON 非法五条路径单测覆盖 |
| 现有 Core / Sandbox 测试全绿 | ✅ | Core 616 / 616；Sandbox 214 / 215（1 跳过）；全解决方案构建 0 错误 |

## 4. 评测结果

评测集 `eval/routing/cases.json` 共 24 条：22 条 32K Full（全工具包）与 2 条 8K Compact（`files`/`edits`/`skills`）。命中定义：`expectedTools` 全部暴露且 `forbiddenTools` 未暴露。

| 运行 | 规则命中 | 最终命中 | 分类调用 | 成功 | 平均延迟 |
| --- | --- | --- | --- | --- | --- |
| 离线规则 | 24/24 | 24/24 | 0 | — | — |
| 在线（仅不确定） | 24/24 | **24/24** | 7 | 7 | 1369 ms |
| 在线（`--always-advise`） | 24/24 | **24/24** | 22 | 22 | 1253 ms |

- 在线模型：`ds / deepseek-flash`（用户 settings 中已配置的云端 provider；不打印 Key）。
- 24 条平均暴露 11.3 个工具（上限 12；Compact 两条为 4）。
- 在线 7 次建议全部是合法 JSON，`missing` 为空；模型建议会把 `list_files` / `glob_files` 等文件工具补进选择，但未挤掉期望工具。
- 调优记录：首次在线运行 R19「为什么构建失败了，看看日志」被建议了 `run_powershell`，触发 `forbiddenTools` 未命中；收紧系统提示「构建 / 日志优先 `run_command`，只有明确要求 PowerShell / 沙箱脚本才选 `run_powershell`」后恢复 24/24。该失败保留在过程记录中，未从数据里删除。

## 5. 与计划的偏差

1. **只路由工具，不路由技能**：`RoutingAdvice` 只带工具名，技能索引仍由 `SkillIndexBuilder` 规则挑选；M2 预留的“替换评分来源”没有在本轮做。
2. **Compact 不进 tier 1**：按 §4.3 计划，Compact 只用规则；8K 场景仍依赖 M1/M2 的规则 + 技能索引。
3. **路由模型复用摘要模型**：默认 `SummaryProviderId/ModelId`，未配置时用当前会话模型；显式覆盖只能写 `settings.json`（`agent.routingProviderId/routingModelId`），没有单独的选择器 UI。
4. **评测是路由级，不是任务完成级**：`tools/routing-eval` 验证的是「该给的给了、不该给的不给」；8K 长任务完成率需要 `tools/AgentEval` + 真实 8K 模型，本轮没有重跑，因此不能声称「不低于 M1」已被实测证明。代码层承诺是 Compact 路由路径保持 M1 行为不变。
5. **缓存键不含 provider**：同一输入在 2 分钟内换 provider 仍命中旧建议；不影响白名单与上限，只是少一次分类调用。
6. **任务信号是关键词粗筛**：纯寒暄不调用；「上次那个结论」这类不含任务词的回忆句直接走规则 memory，不花模型调用。

## 6. 复现

```powershell
# 离线规则评测（无网络、无模型）
dotnet run --project tools/routing-eval -c Release -- --mode rules `
  --out docs/reports/data/m4-routing-eval.json

# 在线小模型路由（DeepSeek Flash；从 %APPDATA% 读取并解密 Key）
dotnet run --project tools/routing-eval -c Release -- --mode live `
  --provider ds --model deepseek-flash `
  --out docs/reports/data/m4-routing-eval-live.json

# 对全部 case 施压（验证建议不会挤掉必要工具）
dotnet run --project tools/routing-eval -c Release -- --mode live `
  --provider ds --model deepseek-flash --always-advise `
  --out docs/reports/data/m4-routing-eval-live-always.json

# 8K 长任务（需要先加载 8K 模型；本轮未执行）
dotnet run --project tools/AgentEval -c Release -- `
  --repo F:\AI\Code\SDemo\p7-bench\commandline `
  --commit b0b0ec9f5470bacc0823c013a599ba3b4e115900 `
  --config eval/configs/compact-8k.json --tasks eval/tasks `
  --out eval/results/m4/compact-8k
```

## 7. 下一步

- 用 8K 模型补跑 `AgentEval` 的 T01–T08，把「完成率不低于 M1」从代码承诺变成实测数字。
- 小模型路由接入技能选择（`RoutingAdvice` 增加 skills 字段），替换 `SkillIndexBuilder.Select` 的评分来源。
- 评测集扩到 50–100 条，加入真实失败对话；把 `exposed / called / missing` 汇总进跨期评测矩阵。
- 语义检索（M3 遗留）与路由模型的本机 4B 档位对比。
