# P5 报告：任务循环与验证门

> 期次：P5（第 5 期）　分支：`feature/vibe-p5-task-loop`　状态：🚧 实现完成，5 任务基准未跑
> 配套文档：[task-loop.md](../task-loop.md)　原始计划：[p5-task-loop.md](p5-task-loop.md)

## 1. 交付物

| 模块 | 文件 |
| --- | --- |
| todo 工具 | `Core/Tools/TodoToolPack.cs`、`Core/Models/TodoItem.cs` |
| todo 持久化 | `ChatSession.Todos`、`SessionRecord.TodosJson`、`ChatStore`、`RequiredColumns` |
| todo UI | `UI/Components/Chat/TodosPanel.razor`、`ChatRoom.razor` |
| 状态包 | `StatePackFormatter`（「当前计划」≤200 token） |
| 验证门 | `Core/Execution/VerificationGate.cs`、`UI/Components/Chat/VerificationBadge.razor` |
| 手动验证 | `Core/Execution/VerificationRunner.cs`、`Windows/Execution/VerificationRunner.cs` |
| 自动续跑 | `Core/Execution/AutoContinuePolicy.cs`、`ChatRoom.razor`（宿主 user 提示，最多 1 次） |
| checkpoint | `Core/Editing/CheckpointStore.cs`、`EditToolPack` 接线、`EditSummaryPanel` 撤销 |
| 循环保护 | `Core/Context/ToolCallGuard.cs`、`AgentToolRegistry` 工具包装 |
| 设置 | `VerificationSettings` / `CheckpointSettings`、`ToolsSettings.razor` |
| 文档 | `docs/task-loop.md`、本报告、计划与路线图 |

## 2. 计划任务对照

| # | 任务 | 状态 | 说明 |
| --- | --- | --- | --- |
| 5.1 | todo 工具 | ✅ | 整体替换、1–20 项、200 字符、多 in_progress 降级 |
| 5.2 | todo 持久化与 UI | ✅ | `TodosJson`、加载 / 保存、折叠面板、进度 |
| 5.3 | 状态包接入 | ✅ | 「当前计划」≤200 token；无计划不占段 |
| 5.4 | 验证门 | ✅ | dirty 判定、徽标、失败红色、手动运行验证、结果落库 |
| 5.5 | 自动续跑一次 | ✅ | policy + UI 宿主 user 提示；最多 1 次 |
| 5.6 | checkpoint | ✅ | 同 turn 聚合、原 / 后 hash、保留 20、恢复、回收站、审计 |
| 5.7 | 撤销 UI | ✅ | 最近一次改动、冲突确认、force 覆盖 |
| 5.8 | 循环保护 | ✅ | 连续 3 次提醒、8 次阻断、`LoopsPrevented` |
| 5.9 | 文档与报告 | ✅ | `task-loop.md` + 本报告 |

## 3. 验收核对

| 验收标准 | 结果 | 证据 |
| --- | --- | --- |
| 5 个基准任务 ≥4 个在 15 步内完成，且有成功验证 | ❌ 未跑 | 需要真实模型 5 任务脚本；本轮只做实现与单测 |
| 有改动未验证 100% 显示徽标；点运行验证触发并回填 | ⚠ 逻辑完成 | `VerificationGateTests` 5 个；`VerificationBadge` 构建通过；未做 UI 点击走查 |
| 自动续跑最多 1 次，不产生额外循环 | ✅（策略） | `AutoContinueTests` 4 个；UI 递归入口已接，未做假模型端到端断言 |
| 撤销能恢复本次生成全部文件；冲突有确认 | ✅ | `CheckpointTests` 7 个 + `EditToolPackCheckpointTests` 1 个 |
| 重复 3 次提示；8 次后收尾 | ✅ | `ToolCallGuardTests` 3 个；工具包装返回阻断结果 |
| todo 重启保持；状态包计划 ≤200 token | ✅ | `TodoTests.持久化往返`、`状态包计划不超过二百token` |
| 新增测试 ≥15；现有全绿 | ✅ | Core 新增 27；Core **506**、Windows **214 + 1 跳过**；构建 0 错误 |

## 4. 测试

- Core：`dotnet test tests/SDemo.DesktopAgent.Core.Tests` → **506 通过 / 0 失败**。
- Windows：`dotnet test tests/SDemo.DesktopAgent.Sandbox.Tests` → **214 通过 / 1 跳过**（P5 未新增 Windows 用例）。
- `dotnet build SDemo.DesktopAgent.Wpf.slnf` → **0 错误**。
- WPF 启动冒烟：启动后保持运行 8 秒，无异常。

P5 新增 27 个用例：

| 文件 | 数量 | 覆盖 |
| --- | --- | --- |
| `TodoTests` | 7 | 整体替换、上限、长度、多个 in_progress、持久化、工具绑定、状态包预算 |
| `VerificationGateTests` | 5 | 无编辑、未验证、成功清除、失败保留、同刻边界 |
| `AutoContinueTests` | 4 | 续跑、最多一次、关闭、无命令 |
| `CheckpointTests` | 7 | 多文件、同 turn、冲突、force、新建撤销、保留 20、删除会话 |
| `EditToolPackCheckpointTests` | 1 | 补丁成功后恢复原文 |
| `ToolCallGuardTests` | 3 | 连续 3 次、参数变化重置、8 次阻断 |

## 5. 偏差与取舍

1. **5 任务基准未跑**。P5 的出口指标需要真实模型多步任务（步数、验证、撤销），本轮把实现与确定性测试做完，但没有伪造“5 个任务通过”的数据。
2. **验证门不统计外部修改**。只认本会话的 `ToolTrace.Edits`；用户手工改文件不会让徽标 dirty，避免误报。
3. **撤销只针对最近一个 checkpoint**。`CheckpointStore.List` 已能列历史，UI 暂未提供历史选择；恢复语义是“撤销最近一次生成”。
4. **自动续跑是 UI 层递归**，不是 `AgentSessionRunner` 内部状态机。策略与上限有单测，但没有假模型端到端验证；递归前会把宿主提示写成 user 消息，不伪造 tool。
5. **循环保护是结果级阻断**。工具包装在超过 8 次时返回“请总结”，不强行中断 MAF 内部循环；模型仍可能继续发起调用，但都会被拒绝。
6. **checkpoint 存在 `%APPDATA%`**，不是计划里的 `%LOCALAPPDATA%`；复用 `IAppPaths.DataRoot`，便于现有清理 / 测试，代价是可能进入漫游配置。
7. **checkpoint 原 / 后 hash 方案**：为区分“模型自己的编辑”和“外部后续改动”，`MarkEdited` 记录编辑后 hash；没有它时恢复不判冲突（兼容直接调用 CheckpointStore 的测试）。
8. **UI 只做构建 + 启动冒烟**，没有 bUnit / 像素走查；todo、徽标、撤销按钮的交互仍需人工确认。

## 6. 遗留与风险

| 项 | 影响 | 建议 |
| --- | --- | --- |
| 5 任务基准缺失 | 无法证明真实任务收敛率 | P6 前用 5 个固定脚本跑真实模型，记录步数 / 验证 / 撤销 |
| 自动续跑未端到端 | 可能在真实 UI 里递归异常 | 加假模型集成测试；限制并发 / 取消 |
| 撤销历史不可选 | 多轮后只能撤回最近一次 | 面板加 checkpoint 列表 |
| checkpoint 占磁盘 | 大文件副本累积 | 保留 20 已有；后续加大小上限 |
| 外部修改不算 dirty | 用户手改后徽标可能不提示 | 可加“文件 hash 与最后轨迹不一致”提示，但不默认阻断 |
| 8 次上限可能过早 | 复杂任务被截断 | 设置化上限；P6 按小模型调参 |

## 7. 提交

P5 的代码、测试与文档合并为 `feature/vibe-p5-task-loop` 上的一个提交；未推送远端。
