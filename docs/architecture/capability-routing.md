# 能力路由与提示词预算（M1–M4）

> 计划文档（M1–M2 已实施，M3–M4 待实施）。目标：工具从 15 个长到 100 个、技能从 3 个长到 50 个、工作区知识库从 0 长到几千条归档时，8K 窗口仍然可用；**进入上下文的永远只是本轮真正需要的那一部分**。

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

### M3 工作区知识库工具（4–6 天）

范围：

- 归档状态 + 索引（摘要优先）+ FTS5；`Tools.Memory` 两个工具；引用跳转。
- 详细设计另起文档。

验收：

- 20 个归档会话，问“上次关于沙箱 ACL 的结论”能命中并跳转。
- 关闭知识库时上下文成本为 0；开启后单次检索 ≤800 token。

### M4 路由升级与评测（3–4 天）

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
