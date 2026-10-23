# M1 报告：工具预算与路由

> 期次：M1（能力路由第 1 阶段）　状态：✅ 代码与单测完成（未做真机 30 工具 / 8K 长任务实测）
> 配套文档：[capability-routing.md](../architecture/capability-routing.md)（M1–M4 计划）
> 验证日期：2026-10-23　分支：当前工作区（未提交）

## 1. 结论摘要

1. **Full 模式不再全量暴露工具**：每轮按「核心集 → 意图补包 → 剩余按包 Order」选最多 N 个（8K 6 / 32K 12），Compact 仍保持 P6 的 ≤4 与四件套兜底。
2. **工具 schema 进入分段预算**：`ContextSegments` 新增 `ToolSchema` 段，`ContextBuilder` 先按路由粗估预留、宿主拿到真实工具后用 `ContextReport.WithRouting` 回填实测值，`Estimated == Segments.Total` 不变量仍然成立。
3. **每轮只路由一次**：`CapabilityRouter` 在 `AgentSessionRunner` 里算好并写进 `ContextBuildRequest.Routing`，`AgentChatService` 直接消费；插件 Run 等绕过 Runner 的路径由 `AgentChatService` 用同一规则补算一次。
4. **兜底入口**：Full 模式暴露 `search_capabilities`，按关键词自查「平台支持 ∩ 用户启用」的工具；它不绕过用户开关，也不会在 FICC 循环中途插工具（下一轮由宿主重新路由）。
5. **回归**：Core **565 通过 / 0 失败**（新增路由 / 预算 / 端到端共 16 个，替换 6 个旧路由测试），Sandbox 214 通过 / 1 跳过，WPF 与全解决方案构建 0 错误。

## 2. 交付物

| 模块 | 文件 |
| --- | --- |
| 路由契约 | `Contracts/Context/RoutingDecision.cs`、`Contracts/Models/AgentSettings.cs` |
| 规则路由 | `Tools.Abstractions/CapabilityRouter.cs`、`ToolSchemaEstimator.cs`（替换 `ToolRouter.cs`） |
| 工具暴露 | `Tools.Abstractions/AgentToolRegistry.cs`：`ToolNames` / `CreateExposed` / `search_capabilities` |
| 分段预算 | `Context/ContextBudget.cs`（`ToolSchemaLimit`）、`ContextReport.cs`（`ToolSchema` 段 + `Routing` + `WithRouting`）、`ContextBuilder.cs` |
| 接线 | `Core/Services/AgentSessionRunner.cs`（路由 + 预览）、`AgentChatService.cs`（生成 + 实测回填） |
| 界面 | `Chat/ContextBreakdown.razor`（工具段 + 路由行）、`Chat/ChatRoom.razor`（预览与发送共用 `EffectiveToolPacks`）、`Settings/ToolsSettings.razor`（上限 / 全量 / 兜底开关） |
| 文档 | 本文 + `capability-routing.md` 的「M1 实施记录」 |

## 3. 验收核对

| 验收标准 | 结果 | 证据 |
| --- | --- | --- |
| `AgentSettings.MaxExposedTools`；Full 也走核心集 + 路由 | ✅ | `CapabilityRouterTests`（Full 上限、核心优先、意图补包）、`AgentChatServiceToolExposureTests` |
| 30 个工具、8K 窗口下工具段 ≤1.5K token | ✅ 单测口径 | 路由只给 6 个（Compact 4 个），实测 schema 再按 `ToolSchemaLimit`（8K 1500）二次截断；集成测试断言 `ToolSchemaTokens ≤ 1500` |
| `ContextReport` / 分解面板显示工具 schema 与路由决策 | ✅ | `ToolBudgetTests`、`ContextBreakdown.razor` 新增「工具 schema」行与「工具路由」说明 |
| 现有 Core 测试全绿 | ✅ | 565 / 565；Sandbox 214 / 215（1 跳过） |
| 风险兜底：设置里可切回全量暴露 | ✅ | `AgentSettings.ExposeAllTools` + 设置页开关；全量时仍受工具段预算截断、且不再附加兜底入口 |

## 4. 与计划的偏差

1. **`search_capabilities` 不在 Compact 暴露**：小模型 + 4 个名额时工具自查收益低，Compact 保持 P6 行为；兜底入口只在 Full 计入名额。
2. **兜底结果不触发同轮续发**：遵守计划「不做 FICC 循环中途动态插工具」，命中未暴露工具时只提示模型下一轮说明用途。
3. **工具段估算 vs 实测**：路由发生在工具实例化之前，`ToolSchemaEstimator` 只能按工具名粗估；报告里的数字由 `AgentChatService` 实测覆盖，两者偏差体现在消息预算预留上（偏保守）。
4. **未做真机 30 工具 / 8K 长任务实测**：本轮交付代码路径与单测；`tools/routing-eval` 留到 M4。

## 5. 下一步（M2 起）

- M2 技能索引：`SkillCatalog` 的 `when-to-use` / `tags`，`load_skill` 走工具结果预算；`search_capabilities` 目前只列工具，届时补技能名。
- M4 评测：记录每轮 exposes / called / missing，验证路由命中率 ≥90%。
