# 能力路由与提示词预算（M1–M4）

> 计划文档（M1–M4 已实施）。目标：工具从 15 个长到 100 个、技能从 3 个长到 50 个、工作区知识库从 0 长到几千条归档时，8K 窗口仍然可用；**进入上下文的永远只是本轮真正需要的那一部分**。

## 1. 现状与三个爆点

| 分段 | 现状 | 8K 下的风险 |
|---|---|---|
| 工具 schema | Full 模式把**所有启用工具**交给 `FunctionInvokingChatClient`；只有 Compact 走 `ToolRouter` 关键词裁到 ≤4 | 15 个约 2.5–3.5K；30 个约 5–7K，历史几乎没地方 |
| 技能 | `ContextBuilder.BuildInstructions` 注入**选中技能的全文**（Compact 上限 600 token），一次只注入一个 | 技能多了以后是“选不准”，不是“撑爆”；但索引化缺失 |
| 知识库 | 未实现 | 若按 RAG 预注入 top-k，8K 必爆 |

**结论**：三个爆点要用三种机制，不能靠“把用户输入总结一下”一把梭。

## 2. 不变量

1. 用户原句永远保留；`TaskBrief` 只做附加，不替代原句。
2. 工具按“核心集 + 按需包 + 逃生口”三层暴露。
3. 技能只放索引，正文按需 `load_skill` 加载。
4. 工作区知识库只做工具，**默认上下文成本为 0**。
5. 每轮只路由一次；工具循环内不重路由。
6. 路由失败必须可恢复：`search_capabilities` 兜底，宿主可带新工具重发一轮。
7. 分段预算显式化，`ContextReport` / `ContextBreakdown` 能看到每一段吃了多少。

## 3. 预算账本

| 分段 | 8K | 32K | 裁剪优先级 |
| --- | --- | --- | --- |
| 系统提示（核心） | 700 | 800 | 最后 |
| 工具 schema | ≤6 个 / ~1.5K | ≤12 个 / ~3K | 高 |
| 技能索引 | 200 | 300 | 中 |
| 状态包 | 500 | 800 | 中 |
| 知识库检索结果 | 0（默认） | 0–1K | 低（工具结果预算内） |
| 输出预留 | 1500 | 3000 | — |
| 历史 / 摘要 / 轨迹 | 剩余 | 剩余 | 先淘汰 |

裁剪顺序：核心系统提示 > 当前任务（原句 + TaskBrief）> pinned > 最近轮次 > 工具 schema > 技能索引 > 状态包。

## 4. 设计

### 4.1 能力注册表

`CapabilityDescriptor`（放 `Tools.Abstractions`，工具包 / 技能 / 知识库共用）：

```
Id            search / file / edits / pdf / translate(plugin) / workspace-memory
Kind          ToolPack | Skill | Knowledge
Name          显示名
WhenToUse     一句话：什么时候需要它（路由与模型搜索都用）
Tags          ["write","files","execute","research","memory"]
TokenCost     暴露给模型时的粗略 token 成本
RequiresApproval
Order
```

现有 `AgentToolPackDescriptor` 增加 `Tags` / `WhenToUse` / `TokenCost` 即可；技能由 `SkillCatalog` 的 frontmatter 提供 `description`，补 `when-to-use` / `tags`。

### 4.2 TaskBrief

```
Goal          一句话目标（不替换原句）
Constraints   用户明确限制：只读 / 别改 X / 输出格式
Capabilities  选中的 ToolPack Id + Skill Id + 是否需要 workspace-memory
Risk          是否涉及写入 / 执行 / 审批
KbScope       none | workspace | history
```

路由输入 = 当前输入 + 最近 1–2 轮 + pinned 约束 + 状态包一句话摘要 + 当前 `AgentMode`。

### 4.3 CapabilityRouter

三档，按成本从低到高：

| 档 | 做法 | 何时用 |
| --- | --- | --- |
| 0 | 规则：关键词 + 最近工具 + 会话状态 + 用户手选 + 模式 | 默认；Compact 只用这一档 |
| 1 | 小模型结构化输出（JSON Schema 约束） | 规则命中不了、多意图、含糊指令 |
| 2 | 主模型自行声明所需能力 | **不做**（不稳定、易循环） |

- 一轮只算一次，结果可缓存；用户换话题 / 新需求才重算。
- 规则层保留现有 `ToolRouter` 的关键词表，作为 fallback 与 Compact 实现。
- 输不选时给“核心集”，不阻断。

### 4.4 工具暴露

```
core（永远暴露，≤6）：read_file, search_files, apply_patch, run_command, todo_write, search_capabilities
packs（按路由）：pdf / powershell / web-search / edits 变体 …
escape hatch：search_capabilities(query) → 返回可用工具名 + 技能名 + 一句话
```

- 新增 `AgentSettings.MaxExposedTools`（8K 默认 6 / 32K 默认 12 / 可手动）。
- 超限时按“路由得分 → 核心集 → Order”截断。
- **名额是上限，不是指标**：不再用「全部可用工具」填满剩余名额；没被意图 / 最近轨迹选中的工具不暴露，靠 `search_capabilities` 兜底（2026-10-24 修订，见 §12）。
- **不在 FICC 循环中途插工具**；需要扩展时由宿主带新工具集重发一次。

### 4.5 技能渐进披露

- 系统提示只放索引：`name — 一句话 — 何时用`，按路由选 top 3–5；技能总数很大时只放最相关。
- 新增 `load_skill(name)` 工具：正文进入工具结果预算，用完即弃。
- 用户手选技能仍然最高优先级：本轮强制全文注入（现有行为保留）。

### 4.6 工作区知识库（接口占位）

- `search_workspace_memory(query, topK)` → 会话卡（标题 / 日期 / 摘要片段 / sessionId）。
- `read_archived_session(sessionId, query?)` → 相关轮次，按 token 预算截断。
- 结果只走工具结果预算；系统提示只留一句“本工作区有 N 条归档会话”。
- 存储与索引细节见后续 `docs/architecture/workspace-memory.md`（待写）。

### 4.7 分段预算

- `ContextBudget` 增加 `ToolSchemaTokens` / `SkillIndexTokens` / `KnowledgeTokens` 上限。
- `ContextBuilder` 按账本分配，超出时先砍低优先段。
- `ContextReport` 增加 `ToolsTokens` / `SkillIndexTokens` / `KnowledgeTokens` / `RoutingDecision`，UI 直接显示。

## 5. 里程碑

### M1 工具预算与路由（2–3 天）✅ 已实施

> 实施细节与偏差见 [§8](#8-m1-实施记录)；报告见 [../reports/m1-report.md](../reports/m1-report.md)。

范围：

- `AgentSettings.MaxExposedTools`；Full 模式也走“核心集 + 路由”。
- `ToolRouter` 扩展为 `CapabilityRouter`（规则版），保留现有关键词逻辑。
- `AgentToolRegistry.Create` 支持“核心集 + 选中包 + search_capabilities”。
- `ContextReport` / `ContextBreakdown` 显示工具 schema token 与路由决策。

验收：

- 30 个工具、8K 窗口下请求仍能发出，工具段 ≤1.5K token。
- 现有 548 个 Core 测试全绿。
- 分解面板能看到“工具 schema / 技能 / 状态包 / 历史”的新数字。

风险：路由漏工具 → `search_capabilities` 兜底 + 设置里可临时切回“全量暴露”。

### M2 技能索引（3–4 天）✅ 已实施

> 实施细节与偏差见 [§9](#9-m2-实施记录)；报告见 [../reports/m2-report.md](../reports/m2-report.md)。

范围：

- `SkillCatalog` 索引元数据（description / when-to-use / tags）。
- 系统提示改为技能索引；新增 `Tools.Skills` 的 `load_skill`。
- 手选技能仍全文注入。

验收：

- 20 个技能时系统提示技能段 ≤400 token。
- `load_skill` 能取到正文并遵守工具结果预算。
- 现有技能相关测试通过。

### M3 工作区知识库工具（4–6 天）✅ 已实施

> 详细设计与偏差见 [workspace-memory.md](workspace-memory.md)；实施记录见 [§10](#10-m3-实施记录)；报告见 [../reports/m3-report.md](../reports/m3-report.md)。

范围：

- 归档状态 + 索引（摘要优先）+ FTS5；`Tools.Memory` 两个工具；引用跳转。
- 详细设计另起文档。

验收：

- 20 个归档会话，问“上次关于沙箱 ACL 的结论”能命中并跳转。
- 关闭知识库时上下文成本为 0；开启后单次检索 ≤800 token。

### M4 路由升级与评测（3–4 天）✅ 已实施

> 实施细节与偏差见 [§11](#11-m4-实施记录)；报告见 [../reports/m4-report.md](../reports/m4-report.md)；评测数据见 [../reports/m4-routing-eval.json](../reports/m4-routing-eval.json)。

范围：

- 规则不确定时调用小模型路由（JSON Schema），失败回落规则。
- 指标：暴露工具数 vs 实际调用数、路由命中率、8K 长任务完成率。
- 评测脚本放 `tools/`。

验收：

- 评测集上路由命中率 ≥90%，8K 任务完成率不低于 M1。
- 小模型路由不可用时自动降级，不阻塞对话。

## 6. 测试与评测

- 单测：路由规则、预算分配、超限截断、`load_skill`、`search_capabilities`。
- 回归：现有 548 Core + 214 Sandbox。
- 评测：`tools/` 下新增 `routing-eval`，记录每轮的 exposes / called / missing。
- 可视化：扩展 `ContextBreakdown`，把“路由决策”也列出来。

## 7. 明确的非目标

- 不做“每轮 LLM 总结用户输入”。
- 不做 FICC 循环中途动态插工具。
- 不做知识库预注入。
- 不做技能/工具的动态 UI 插件化（本轮只做索引与路由）。

## 8. M1 实施记录

状态：✅ 2026-10-23。Core 565 / Sandbox 214 全绿，WPF 与全解决方案 0 错误。

### 落地形态

| 计划项 | 实现 |
| --- | --- |
| 能力注册表 | `Contracts/Context/RoutingDecision.cs`（纯契约：暴露集合 / 核心集 / 包集 / 上限 / 估算 token / 兜底 / 截断 / 理由）；`AgentSettings` 增加 `MaxExposedTools`、`ExposeAllTools`、`SearchCapabilitiesEnabled`，`ResolveMaxExposedTools(window)` 给出 8K 6 / 32K 12 |
| 规则路由 | `Tools.Abstractions/CapabilityRouter.cs`：Compact 保留 P6 关键词 + 最近轨迹 + ≤4；Full 改为「核心集 → 意图补包 → 最近 → 按包 Order 填满」。关键词表新增 pdf / powershell / 联网 / 计划四组，减少路由漏工具 |
| 工具暴露 | `AgentToolRegistry.ToolNames(enabled)` 只展开描述符（不实例化）；`CreateExposed(...)` 按路由结果生成，并附加 `search_capabilities`（只查「平台支持 ∩ 用户启用」） |
| 分段预算 | `ContextBudget.ToolSchemaLimit`（4K 1K / 8K 1.5K / 32K 3K）；`ContextBuildRequest.Routing` 让 `ContextBuilder` 先预留；`ContextSegments.ToolSchema` 进入总估算 |
| 实测回填 | `AgentChatService` 用 `HeuristicTokenCounter` 量真实 schema（名称 + 描述 + 参数 JSON），`ContextReport.WithRouting` 同步修正 `Estimated` / `RawEstimated`，超限时从路由尾部丢弃 |
| 只路由一次 | `AgentSessionRunner` 在 `BuildRequest` 里算好路由（预览与发送同一决策）；插件 Run 等直接构造 plan 的路径由 `AgentChatService` 同规则补算 |
| 可视化 | `ContextBreakdown` 新增「工具 schema」分段与「工具路由：暴露 N 个（上限 M）· 理由」；设置页新增上限 / 全量暴露 / 兜底开关 |

### 与计划的偏差（取舍）

1. `search_capabilities` 只在 Full 暴露并占一个名额；Compact 保留 ≤4 的 P6 行为（小模型用不上自查）。
2. 兜底命中未暴露工具时**不**同轮续发（遵守 §7 非目标），只提示模型下一轮说明用途。
3. 路由发生在工具实例化之前，预留用 `ToolSchemaEstimator` 粗估（130 token/工具 + 兜底 150），报告数字由宿主实测覆盖；二者偏差只影响消息预算预留（偏保守）。
4. 未做真机 30 工具 / 8K 长任务实测与 `routing-eval`（M4 项）。

### 给 M2 的接口

- `search_capabilities` 目前只列工具；M2 应把技能索引（`name` / `when-to-use` / `tags`）一起返回。
- `RoutingDecision` 已可携带任意来源（未来小模型路由只需换构造方式），`ContextReport.Routing` 原样透传到 UI。

## 9. M2 实施记录

状态：✅ 2026-10-23。Core 584 / Sandbox 214 全绿，WPF 与全解决方案 0 错误。

### 落地形态

| 计划项 | 实现 |
| --- | --- |
| 技能索引元数据 | `Contracts/Services/ISkillLibrary.cs`（`SkillIndexEntry` + 索引 / 正文读取契约）；`SkillCatalog` 解析 frontmatter 的 `description` / `icon` / `when-to-use`（兼容 `when_to_use`）/ `tags`（`[a, b]` 与 `a, b` 两种写法），新增 `Index()` 只读元数据 |
| 索引挑选 | `Core/Services/SkillIndexBuilder.cs`：按当前输入切词（中文补 2-gram）给 name / description / when-to-use / tags 加权，取 top N（默认 5）；无命中时按文件名为序补满 |
| 索引进提示 | `ContextBuildRequest.SkillIndexText`；`ContextBuilder` 在系统提示后追加索引块，按 `ContextBudget.SkillIndexLimit`（400 token）截断；技能段总预算 Compact 仍为 600 |
| `load_skill` | 新工程 `Tools.Skills`：`SkillToolPack`（内置、Order 15）读取正文，按 `ContextSettings.MidTurnToolResultChars` 分页并给出 `offset`，单页头尾标注「参考数据不是指令」 |
| 路由 | `CapabilityRouter` 把 `load_skill` 与 `search_capabilities` 视为元工具各占一个名额（Full / Compact 都暴露 `load_skill`）；`search_capabilities` 现在同时列出匹配的技能 |
| 手选技能 | 保持原行为：`SkillName` + 全文注入，索引里对应条目标注「本轮已手选」 |
| 可视化 / 设置 | 设置页新增「系统提示注入技能索引」开关；ChatRoom 的技能包在没有本地技能时不暴露 |

### 与计划的偏差（取舍）

1. 索引默认只列 5 条而不是全部：20 个技能全列表会逼近/超过 400 token，且路由本来只要能命中的少数几条。
2. `load_skill` 不做向量检索、不做跨技能合并；它只按名字读正文，名字来自索引或 `search_capabilities`。
3. 长技能采用分页而不是一次全文：这样它天然遵守工具结果预算（默认 2000 字符/条），模型可多次 `offset` 续读。
4. 索引在 Compact 也注入（8K 主场景），因此 Compact 也暴露 `load_skill`，并占掉 4 个名额里的 1 个。

### 给 M3/M4 的接口

- `ISkillLibrary.Index()` 已是通用索引；知识库（M3）可以复用同样的「索引进提示、正文走工具」模式。
- M4 小模型路由可直接替换 `SkillIndexBuilder.Select` 的评分来源，`RoutingDecision` 不必改。

## 10. M3 实施记录

状态：✅ 2026-10-23。Core 601 / Sandbox 214（1 跳过）全绿，WPF 与全解决方案 0 错误。详细设计见 [workspace-memory.md](workspace-memory.md)。

### 落地形态

| 计划项 | 实现 |
| --- | --- |
| 归档状态 | `ChatSession.IsArchived` + `sessions.IsArchived`；`ChatStore.RequiredColumns` 自动补列；侧栏 / 会话菜单 / 设置页三处入口；移动到历史聊天时自动清归档 |
| 索引 | `Storage/WorkspaceMemoryIndex.cs`：每条归档会话一行 FTS5；`terms` 预分词（中文 2-gram + 拉丁小写），标题 ×4 / 摘要 ×3 / 正文 ×1；`body` 仅用于片段定位。搜索前 `EnsureIndexed` 按「索引行数 < 归档数」整库重建 |
| 检索 | `search_workspace_memory(query, topK)`：AND → OR 放宽 → 单字 `LIKE` 兜底；每条片段 ≤140 字，整段 ≤1000 字（实测 ≤800 token） |
| 正文 | `read_archived_session(sessionId, query?, offset)`：有 query 时按词频挑最多 6 轮，按 `MidTurnToolResultChars` 分页 |
| 系统提示 | `ContextBuildRequest.MemoryText` + `ContextSegments.Knowledge` + `ContextBudget.KnowledgeLimit`（120 token）；关闭 / 无工作区 / 0 条归档时为 null，成本 0 |
| 路由 | `CapabilityRouter` 新增 memory 意图，排在 shell 之前（「上次关于沙箱 ACL」不会被带去 `run_powershell`）；`memory` 包 Order 24，只在路由命中时占名额 |
| 引用跳转 | `AgentRunMetrics.Citations` → `ChatMessage.MemoryCitations`（`MemoryCitationsJson`）→ 消息下「引用来源」chip → `AppShell.OpenSessionById` |
| 设置页 | 工具设置新增「工作区知识库」：总开关、归档列表（打开 / 取消归档）、重建索引 |

### 与计划的偏差（取舍）

1. **正文落两份**：FTS 表保留 `body UNINDEXED` 用于生成含查询词的片段；代价是磁盘约为原库 3 倍，规模变大时应改成外部内容表。
2. **不做语义 / 向量检索**：同义改写（「隔离配置」↔「沙箱 ACL」）命中不了；本轮只保证关键词可命中。
3. **引用不持久化到上下文**：`MemoryCitationsJson` 只给界面跳转，和派生卡片一样不进入后续请求。
4. **归档只对工作区会话开放**：历史聊天没有工作区 DB，也就没有知识库作用域；移到历史聊天时自动取消归档。
5. **未做真机 20 条归档的端到端跳转实测**：单测覆盖索引 / 检索 / 分页 / 预算 / 引用记录，UI 跳转由 `OnOpenSessionById` 接线。

### 给 M4 的接口

- `RoutingDecision` 仍与来源无关；M4 小模型路由只要替换 `CapabilityRouter.Route` 的调用点，不需要改知识库。
- 检索评分集中在 `WorkspaceMemoryIndex`，语义检索可在此替换实现，工具契约不变。

## 11. M4 实施记录

状态：✅ 2026-10-23。Core 616 / Sandbox 214（1 跳过）全绿，全解决方案构建 0 错误。报告见 [../reports/m4-report.md](../reports/m4-report.md)。

### 落地形态

| 计划项 | 实现 |
| --- | --- |
| 路由建议契约 | `Contracts/Context/RoutingAdvice.cs`：`RoutingAdvice`（工具 / 意图 / 理由 / 置信度）、`RoutingCapability`、`RoutingAdviceRequest`、`IRoutingAdvisor`；`IRoutingChatTransport` 与摘要传输分开 |
| 小模型建议 | `Core/Services/SmallModelRoutingAdvisor.cs`：JSON Schema 提示 + 严格校验 + 2 分钟缓存；解析时过滤未知工具、大小写归一、空结果视为无效；任何异常返回 null |
| 规则触发条件 | `CapabilityRouter.ShouldConsultAdvisor`：仅 Full、未全量、工具数超上限、输入有任务信号，且意图未知 / 多意图 / 命中工具都不可用时调用；Compact 永不调用 |
| 建议落地 | `CapabilityRouter.ApplyAdvice`：建议只决定优先顺序，未知工具丢弃；规则结果补齐剩余名额，元工具（`search_capabilities` / `load_skill`）仍由宿主按模式追加；仍受窗口 / 模式上限约束 |
| 失败回落 | `AgentSessionRunner.ResolveRoutingAsync`：超时（默认 8s） / 网络 / JSON 非法 / 空建议都回落规则，`RoutingDecision.Source` 保持 `rules`，Reason 追加「回落规则」 |
| 一轮一次 | `AgentSessionRunner` 在 `StreamAsync` / `PreviewAsync` 开头算一次，压缩重建与溢出重试复用；`RoutingDecision.Source` 透传到 `ContextReport` 与分解面板 |
| 指标 | `AgentRunMetrics.RecordRouting` + `ExposedToolCount` / `CalledToolCount` / `UnusedExposedTools` / `CalledUnexposedTools`；AgentEval transcript 每轮记录 `Routing`（exposes / called / unused） |
| 评测 | 新工程 `tools/routing-eval`：`eval/routing/cases.json` 24 条（22 条 32K Full + 2 条 8K Compact），记录 exposes / missing / forbidden；支持 `--mode rules` 与 `--mode live`，`--always-advise` 可对全量 case 施压 |
| 设置 / UI | 工具设置新增「规则不确定时用小模型辅助路由」与超时；`ContextBreakdown` 显示「工具路由（规则 / 小模型）」；预览改为异步，旧结果用版本号丢弃 |
| 路由模型解析 | `AgentSettings.RoutingProviderId/ModelId` 显式优先，其次摘要模型，最后当前会话模型；未配置路由模型也能工作，但会使用会话模型 |

### 评测结果（2026-10-23）

| 配置 | 规则命中 | 最终命中 | 调路由模型 | 平均延迟 |
| --- | --- | --- | --- | --- |
| `--mode rules`（离线） | 24/24 | 24/24 | 0 | — |
| `--mode live`（DeepSeek Flash，仅不确定） | 24/24 | **24/24** | 7/24 | 1369 ms |
| `--mode live --always-advise`（DeepSeek Flash，全量施压） | 24/24 | **24/24** | 22/24（Compact 2 条跳过） | 1253 ms |

- 24 条 case 平均暴露 11.3 个工具（上限 12；2 条 8K Compact 为 4）。
- 小模型 7 次建议全部返回合法 JSON；没有一次因建议导致缺工具（missing 为空）。
- 初次 live 曾出现一次 R19 选择 `run_powershell` 的过度暴露，收紧提示词（构建 / 日志优先 `run_command`，明确要求 PowerShell 才选）后消除；数据保留在报告里作为提示词调优记录。

### 与计划的偏差（取舍）

1. **只路由工具，不路由技能**：`RoutingAdvice` 只携带工具名；技能索引仍由 `SkillIndexBuilder` 规则挑选。M2 预留的“替换评分来源”留到后续。
2. **Compact 不进入 tier 1**：按 §4.3 计划，Compact 只用规则；8K 主场景仍靠 M1/M2 的规则 + 索引。
3. **路由模型不做独立选择 UI**：复用摘要模型（`ContextSettings.SummaryProviderId/ModelId`），可用 `settings.json` 的 `agent.routingProviderId/routingModelId` 覆盖；避免为一次分类调用再维护一套 provider 下拉。
4. **评测是路由级，不是任务完成级**：`tools/routing-eval` 冻结 24 条查询与期望工具，验证命中率 ≥90%；8K 长任务完成率仍由 `tools/AgentEval` 跑，需真实 8K 模型，本轮没有重跑（M1–M3 也没有真机 8K 长任务基线）。
5. **缓存键不含 provider**：同一输入换 provider 仍会命中 2 分钟缓存；影响仅是少一次分类调用，不影响工具白名单。
6. **`ShouldConsultAdvisor` 依赖关键词任务信号**：纯寒暄不调用；不含任务词的回忆句（如「上次那个结论」）直接走规则 memory，不额外花模型调用。

## 12. 先决定做什么：TurnIntent 闸门（2026-10-24）

### 问题

M1–M4 解决的是「从很多工具里挑哪些」，但从未回答「这一轮到底要不要工具」：

- 系统提示词写死「先用一行 PLAN，再调用一个工具」，连寒暄也被要求调工具；
- `CapabilityRouter` 永远先给核心集，`Finalize` 再按可用顺序补满名额，**「嗯」也会拿到 6–12 个工具**；
- 小模型路由的 JSON Schema 要求 `tools` 非空，它在结构上就说不出「不需要工具」；
- `NoEditRepromptPolicy` 先判编辑词、后判只读疑问，「解释一下怎么改」会被追问三次去动文件。

结果就是「不管什么事都先调工具」：模型没有机会先确定任务，宿主也没有「不用工具」这个状态。

### 设计

在选工具之前加一层成本档 0 的判定（`TurnClassifier`）：

| 档 | 判定 | 工具 | 提示词 |
| --- | --- | --- | --- |
| `Chat` | 寒暄 / 致谢 / 身份 / 闲聊 / 明确说「不要用工具」 | 0 个 | `SystemPrompts.Chat`（直接回答） |
| `Answer` | 看、查、解释、回忆 | 核心集 + 命中意图 | Full / Compact |
| `Task` | 明确的动手指令（帮我改 / 修一下 / 实现 / 跑一下…） | 核心集 + 意图 + 最近轨迹 | Full / Compact |

- 判定保守：默认 `Answer`；只有整句很短且确属寒暄才判 `Chat`，避免把「你好，帮我改个文件」误判。
- 用户手选技能时强制 `Task`（明确要用能力）。
- `RoutingDecision.Intent` / `NeedsTools` 把结果透传给运行时与 UI；`NeedsTools=false` 时工具集为空、系统提示换成对话版。
- 小模型建议只在 `TurnIntent != Chat` 时生效，不能把对话轮改回工具轮。
- `NoEditRepromptPolicy` 先看 `TurnIntent`，且「疑问句且没有动手指令」一律不追问。

### 验收

- 「你好」「嗯」不调用工具、不花工具 schema token，回复里不出现 PLAN。
- 「帮我改一下这个文件」仍拿到 `read_file` / `apply_patch` 等必要工具。
- 「解释一下怎么改」「这个文件改哪里了？」不触发无修改追问。
- 全量暴露（排障开关）仍然优先于对话判定。
