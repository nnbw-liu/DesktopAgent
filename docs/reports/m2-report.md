# M2 报告：技能索引与渐进披露

> 期次：M2（能力路由第 2 阶段）　状态：✅ 代码与单测完成（未做真机 20 技能实测）
> 配套文档：[capability-routing.md](../architecture/capability-routing.md)（M1–M4 计划，§9 为 M2 实施记录）
> 验证日期：2026-10-23　分支：`feature/m2-skill-index`（基于 `feature/m1-capability-routing`）

## 1. 结论摘要

1. **系统提示只放技能索引**：`name · 一句话 · 何时用 · 标签`，按当前输入选 top 5；20 个技能时索引块 ≤400 token（单测用 20 条长描述验证）。
2. **正文按需加载**：新增内置工具包 `Tools.Skills` 的 `load_skill(name, offset)`；正文按 `MidTurnToolResultChars` 分页，天然遵守工具结果预算，长技能可 `offset` 续读。
3. **手选技能不变**：用户点选的技能仍全文注入，索引里对应条目标注「本轮已手选，全文见下」。
4. **路由配合**：`load_skill` 与 `search_capabilities` 作为元工具各占一个名额（Full / Compact 都暴露 `load_skill`，因为 8K 主场景就是 Compact）；`search_capabilities` 现在也列技能。
5. **回归**：Core **584 通过 / 0 失败**（新增 19 个），Sandbox 214 通过 / 1 跳过，WPF 与全解决方案构建 0 错误。

## 2. 交付物

| 模块 | 文件 |
| --- | --- |
| 技能契约 | `Contracts/Services/ISkillLibrary.cs`（`SkillIndexEntry` + 索引 / 正文读取） |
| 技能库 | `Core/Services/SkillCatalog.cs`：`when-to-use` / `tags` 解析、`Index()` |
| 索引挑选 / 渲染 | `Core/Services/SkillIndexBuilder.cs`（关键词 + 中文 2-gram、top 5、紧凑渲染） |
| 上下文 | `ContextBuildRequest.SkillIndexText`、`ContextBuilder` 追加与截断、`ContextBudget.SkillIndexTokenLimit`（400） |
| 工具包 | 新工程 `Tools.Skills`：`SkillToolPack`（`load_skill`，Order 15，内置） |
| 路由 | `CapabilityRouter` 元工具名额、`AgentToolRegistry` 兜底入口列技能、`ToolContext.Skills` |
| 接线 | `AgentSessionRunner.BuildSkillIndexText`、`AgentChatService` 把技能库传给工具上下文、DI 注册 `ISkillLibrary` |
| 界面 | `ToolsSettings.razor`（索引开关）、`ChatRoom.razor`（无技能时不暴露技能包 / 刷新技能目录） |
| 文档 | 本文 + `capability-routing.md` §9 |

## 3. 验收核对

| 验收标准 | 结果 | 证据 |
| --- | --- | --- |
| 20 个技能时系统提示技能段 ≤400 token | ✅ | `SkillIndexBuilderTests.二十个技能的索引不超过四百token`（实际约 300–350） |
| `load_skill` 取到正文并遵守工具结果预算 | ✅ | `SkillToolPackTests` 4 个：正文 / 缺技能 / 分页续读 / 无技能库；单页按 `MidTurnToolResultChars` 分页 |
| 现有技能相关测试通过 | ✅ | `SkillCatalogTests` 原 7 个 + 新增 4 个；`CompactPromptTests` 技能 ≤600 仍通过 |
| 手选技能全文注入 | ✅ | `SkillIndexContextTests.索引与手选技能同时存在时都被注入` |
| 全量回归 | ✅ | Core 584 / 584；Sandbox 214 / 215（1 跳过） |

## 4. 与计划的偏差

1. **索引默认 5 条，不是全部**：20 条全列表会逼近 400 token，M2 的验收正是用「只放最相关」达成的；可通过 `AgentSettings.SkillIndexMaxEntries` 调整（1–20）。
2. **`load_skill` 分页而非全文**：单页按工具结果预算（默认 2000 字符）切分并返回下一页 `offset`，比让中途裁剪器粗暴截断更可控。
3. **Compact 也注入索引**：8K 是主要场景；因此 Compact 也暴露 `load_skill`，并从 4 个名额里让出 1 个。
4. **`search_capabilities` 与 `load_skill` 分工**：前者只在 Full 暴露（找工具/技能名），后者可在 Compact 用（读正文）。
5. 未做真机 20 技能注入与 8K 长任务实测；`routing-eval` 仍留到 M4。

## 5. 下一步（M3）

- 工作区知识库复用同一模式：系统提示只留「本工作区有 N 条归档会话」，检索与正文走 `Tools.Memory` 两个工具。
- 索引挑选用同一个评分器，M4 可整体替换成小模型路由。
