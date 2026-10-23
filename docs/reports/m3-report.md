# M3 报告：工作区知识库与引用跳转

> 期次：M3（能力路由第 3 阶段）　状态：✅ 代码 / 单测 / 构建完成（未做真机 20 条归档的端到端跳转实测）> 配套文档：[capability-routing.md](../architecture/capability-routing.md)（§10 为 M3 实施记录）、[workspace-memory.md](../architecture/workspace-memory.md)（详细设计）
> 验证日期：2026-10-23

## 1. 结论摘要

1. **归档即记忆**：工作区会话菜单 / 侧栏 / 设置页三处都能归档；归档会话从侧栏隐藏，进入当前工作区 FTS5 索引。
2. **默认成本为 0**：知识库关闭、没有工作区、没有归档会话三种情况下，系统提示不追加任何文本，`Segments.Knowledge = 0`，`memory` 工具包不进入本轮工具集合。
3. **开启只多一句话**：系统提示追加「本工作区有 N 条归档会话」（≤120 token）；检索结果与正文都只走工具结果预算。
4. **中文 + 英文都能检索**：宿主预分词（中文 2-gram / 拉丁小写）+ FTS5；`沙箱 ACL` 能命中同一会话，AND 无命中自动放宽为 OR。
5. **单次检索受预算约束**：结果上限 1000 字符，单测按 `HeuristicTokenCounter` 断言 ≤800 token。
6. **引用可跳转**：工具命中记录 `MemoryCitation`，随助手消息落库，消息下渲染「引用来源」chip，点击按 sessionId 打开原归档会话。
7. **回归**：Core **601 通过 / 0 失败**（新增 17 个），Sandbox **214 通过 / 1 跳过**；WPF 过滤解决方案与全解决方案构建 0 错误。

## 2. 交付物

| 模块 | 文件 |
| --- | --- |
| 知识库契约 | `Contracts/Services/IWorkspaceMemory.cs`（检索 / 读取 / 索引 / 重建） |
| 引用模型 | `Contracts/Context/MemoryCitation.cs`；`ChatMessage.MemoryCitations`、`MemoryCitationsJson` |
| 归档状态 | `ChatSession.IsArchived`、`SessionRecord.IsArchived`、`ChatStore.RequiredColumns` 迁移 |
| FTS5 索引 | `Storage/ArchiveSchema.cs`、`Storage/WorkspaceMemoryIndex.cs`（预分词、片段、分页、懒重建） |
| 归档编排 | `Core/Services/SessionArchiveService.cs` |
| 工具包 | 新工程 `Tools.Memory`：`search_workspace_memory` / `read_archived_session` |
| 上下文 | `ContextBuildRequest.MemoryText`、`ContextSegments.Knowledge`、`ContextBudget.KnowledgeLimit`、`AgentSessionRunner.BuildMemoryText` |
| 路由 | `CapabilityRouter` 新增 memory 意图（排在 shell 之前） |
| 接线 | `AgentChatService` 把 `WorkspaceId` / `IWorkspaceMemory` 传进 `ToolContext`；DI 注册索引与归档服务 |
| 界面 | 侧栏 / ChatRoom 会话菜单归档、引用来源 chip、设置页「工作区知识库」、`ContextBreakdown` 新增知识库分段 |
| 文档 | 本文 + `workspace-memory.md` + `capability-routing.md` §10 |

## 3. 验收核对

| 验收标准 | 结果 | 证据 |
| --- | --- | --- |
| 20 个归档会话，问「上次关于沙箱 ACL 的结论」能命中 | ✅ | `WorkspaceMemoryTests.二十个归档会话里能命中中文加英文缩写`（首条命中目标会话，片段含「沙箱」）；`MemoryToolPackTests.检索结果引用来源且不超过八百token` |
| 命中结果能跳转 | ✅ | 工具记录 `MemoryCitation` → `ApplyMetrics` 写入消息 → ChatRoom「引用来源」chip → `OnOpenSessionById`；持久化见 `ChatStoreContextTests.归档状态与知识库引用落库后能读回` |
| 关闭知识库时上下文成本为 0 | ✅ | `MemoryRunnerTests.关闭知识库时上下文成本为零`、`MemoryContextTests.没有知识库时不注入且分段为零`；`memory` 包在 `GetUnavailableReason` 中按设置关闭 |
| 开启后单次检索 ≤800 token | ✅ | `MemoryToolPackTests.检索结果引用来源且不超过八百token`（`CountMessage("tool", result) <= 800`） |
| 老库兼容 | ✅ | `ChatStoreContextTests.老库自动补摘要与轨迹列` 新增断言 `IsArchived` / `MemoryCitationsJson`；FTS 表 `CREATE VIRTUAL TABLE IF NOT EXISTS` 自建 |
| 索引可自愈 | ✅ | `WorkspaceMemoryTests.老库会话可以懒重建索引`（清空 FTS 后搜索仍命中） |
| 路由不被「沙箱」抢走 | ✅ | `CapabilityRouterTests.回忆意图补知识库工具且不会被沙箱关键词抢走`（`意图=memory`，memory 工具排在 `run_powershell` 之前） |
| 全量回归 | ✅ | Core 601 / 601；Sandbox 214 / 215（1 跳过） |

## 4. 与计划的偏差

1. **`body` 以 UNINDEXED 列保留在 FTS 表**：为了在摘要不含查询词时仍能生成准确片段；磁盘约为原库 3 倍，规模变大时应改造。
2. **不做语义检索**：关键词可命中，同义改写不行；语义 / 向量检索留到后续。
3. **引用不进入上下文**：`MemoryCitationsJson` 只用于界面跳转，和派生卡片同一条规则。
4. **归档仅限工作区会话**：历史聊天没有工作区 DB；工作区被删除时会话移到历史库并自动取消归档。
5. **未做真机端到端**：单测覆盖索引 / 检索 / 分页 / 预算 / 引用记录；真实 20 条归档 + 点击跳转待手工验收。
6. **索引在归档会话继续对话时同步重建**：ChatRoom 每轮结束后对已归档会话调用 `Index`；其他写入路径（插件直接保存）不会触发，搜索前的行数检查可兜底。

## 5. 下一步（M4）

- 规则不确定时调用小模型路由（JSON Schema），失败回落规则；`RoutingDecision` 与知识库接口不变。
- 评测脚本记录每轮 exposes / called / missing，并补 8K 长任务回归。
