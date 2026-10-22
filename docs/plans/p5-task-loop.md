# P5 — 任务循环与验证门（第 5 期）

> 状态：🚧 实现完成（2026-10-22）；5 任务基准未跑 · 估时 3 人日 · 前置：P3 / P4 · 出口：任务能收敛、改动可回滚
> 关联总纲：[vibe-coding-roadmap.md](vibe-coding-roadmap.md) · 报告：[p5-report.md](p5-report.md)

## 1. 本期要解决的问题

有了编辑和构建能力后，新的失败模式是「跑偏而不自知」：

- 模型改了 5 个文件、没跑任何验证，就说“已完成”；
- 同一个 `read_file` 反复调用，绕圈；
- 改错了想撤回，只能手动改回去（沙箱有 trash，但文件编辑没有回滚点）；
- 多步任务没有可见进度，用户不知道进行到哪。

本期不新增“智能”，只加**约束与可见性**：todo、验证门、回滚点、循环保护。

## 2. 范围

**做**

- `todo_write` 工具 + 会话级持久化 + 进度面板；
- 验证门：本次生成有改动但无成功验证时，标注「未验证」，可一键验证；
- 可选自动续跑一次（仅一次，避免死循环）；
- 编辑前自动 checkpoint，支持「撤销本次改动」；
- 重复调用保护与最大步数收尾。

**不做**

- 多 Agent 分工、子任务并行；
- 自动提交 git；
- 复杂的计划 DSL / 依赖图；
- 长周期后台任务（关闭应用即停止）。

## 3. 设计要点

### 3.1 todo

```csharp
todo_write(items: [{ content, status }])
```

- `status`：`pending` / `in_progress` / `completed`；
- 语义是**整体替换**（不是追加），最后一次调用为准；
- 校验：1–20 项、每项 ≤ 200 字符、最多 1 个 `in_progress`（多于 1 个取第一个，其余降级为 pending 并提示）；
- 持久化到 `sessions.TodosJson`；P2 状态包增加「当前计划」段（≤ 200 token）；
- UI：会话标题下的可折叠面板，显示 `☐ / ◐ / ☑` 与进度 `2/5`；不打断聊天流。

给模型的提示词（放进系统提示，短）：

```text
多步任务先调用 todo_write 列出步骤；每完成一步更新状态；结束前所有项应为 completed 或明确说明未完成原因。
```

### 3.2 验证门

判定「有未验证改动」：

```
lastEditAt   = 最近一次 apply_patch / write_file 成功的轨迹时间
lastVerified = 最近一次成功的 build/test 记录时间
dirty        = lastEditAt > lastVerified
```

行为：

1. 生成正常结束（模型返回纯文本、无工具调用）且 `dirty` → 消息下方显示徽标
   `未验证改动：3 个文件 · 最近验证 10:45`，附「运行验证」按钮；
2. 若 `execution.autoVerifyAfterEdit=true` 且工作区配置了构建/测试命令：
   - 宿主追加一条 **user 角色**提示：`[宿主提示] 工作区有未验证改动，请先运行 <命令> 并修复错误后再结束。`
   - 自动续跑**最多 1 次**；若本轮仍无成功验证，保留徽标，不再续跑；
3. 验证失败（exit != 0）时徽标变红，提示首个错误定位；模型可继续修；
4. 用户可随时点「运行验证」手动触发，结果注入下一轮上下文（作为宿主提示）。

> 不伪造 tool 消息：宿主提示一律用 user 角色 + 明确前缀，避免破坏消息配对约束。

### 3.3 checkpoint / 撤销

- 在 `apply_patch` / `write_file` 落盘前，把**将被修改的文件**复制到：
  `%LOCALAPPDATA%\SDemo\DesktopAgent\checkpoints\{sessionId}\{yyyyMMdd-HHmmss-fff}\files\...`
  同目录写 `manifest.json`：相对路径、原 hash、时间、触发消息 id；
- 一次生成（一个 turn）内多次编辑共用同一个 checkpoint；
- 保留最近 20 个，超出按时间清理；会话删除时一并清理；
- UI：「撤销本次改动」按钮在 `EditSummaryPanel` 上；
- 恢复语义：
  - 文件在 checkpoint 后被外部改过（hash 不符）→ 弹确认：“该文件在检查点后又被修改，仍要覆盖吗？”；
  - 恢复成功写审计（`ToolId=checkpoint.restore`，变更文件 hash）；
  - 新建文件撤销 = 删除（进回收站，复用 `sandbox/trash` 约定或独立 `checkpoints/trash`）。

### 3.4 循环保护

- 单轮工具调用上限 8（P1 已有）；
- 相同 `(toolId, argsDigest)` 连续出现 3 次 → 注入宿主提示“同一调用已重复 3 次，请换策略或先总结”；
- 达到上限或重复阈值后：强制模型输出文本总结，禁止继续调用工具（通过移除工具 + 追加提示实现）；
- 记录到报告 `LoopsPrevented`，用于 P7 分析。

## 4. 数据与配置

**DB**（加入 `RequiredColumns`）：

| 表 | 列 | 类型 |
| --- | --- | --- |
| sessions | `TodosJson` | TEXT NULL |

**settings.json**：

```jsonc
"verification": {
  "autoVerifyAfterEdit": true,
  "maxAutoContinue": 1
},
"checkpoints": { "enabled": true, "keep": 20 }
```

## 5. 任务拆解

| # | 任务 | 交付物 | 涉及文件 | 估时 | 状态 |
| --- | --- | --- | --- | --- | --- |
| 5.1 | todo 工具 | `todo_write`、校验、整体替换、轨迹记录 | `Core/Tools/TodoToolPack.cs` | 0.5 | ✅ |
| 5.2 | todo 持久化与 UI | `TodosJson`、加载/保存、折叠面板 | `ChatStore.cs`、`ChatRoom.razor`、`TodosPanel.razor` | 0.5 | ✅ |
| 5.3 | 状态包接入 | 「当前计划」段、上限 200 token | `StatePackFormatter.cs` | 0.25 | ✅ |
| 5.4 | 验证门 | dirty 判定、徽标、手动验证、宿主提示 | `VerificationGate.cs`、`ChatRoom.razor` | 0.75 | ✅ |
| 5.5 | 自动续跑一次 | 配置、上限、失败保留徽标 | `ChatRoom.razor`、`AutoContinuePolicy.cs` | 0.25 | ✅（UI 递归实现） |
| 5.6 | checkpoint | 备份、manifest、清理、恢复、审计 | `Core/Editing/CheckpointStore.cs` | 0.75 | ✅ |
| 5.7 | 撤销 UI | 按钮、确认、冲突提示 | `EditSummaryPanel.razor` | 0.25 | ✅ |
| 5.8 | 循环保护 | 重复检测、强制收尾、统计 | `Core/Context/ToolCallGuard.cs` | 0.5 | ✅ |
| 5.9 | 文档与报告 | `docs/task-loop.md`、`p5-report.md` | docs | 0.25 | ✅ |

## 6. 测试清单

**单测**

- `TodoTests`：整体替换、数量/长度上限、多 in_progress、持久化往返、老库缺列。
- `VerificationGateTests`：无改动不标；改动未验证标；验证成功清除；验证失败标红；`lastEditAt == lastVerified` 边界。
- `AutoContinueTests`：最多一次；关闭时不续跑；续跑仍是 user 提示而非 tool 消息。
- `CheckpointTests`：多文件备份、manifest 正确、恢复覆盖、外部改动冲突、新建文件撤销、保留 20 个、会话删除清理。
- `ToolCallGuardTests`：重复 3 次告警、8 次上限、收尾后禁止再调用、统计正确。

**集成 / 手工**

- 本仓库任务 A–C 各跑一遍，统计步数、是否有验证记录、是否触发自动续跑。
- 故意改坏一个文件 → 构建失败 → 模型修复 → 通过；验证徽标状态变化正确。
- 撤销：跑完任务点撤销，工作区回到改动前；再撤销一次无操作。

## 7. 验收标准

- [ ] 5 个基准任务中 ≥ 4 个在 15 步内完成，且会话里有成功验证记录（本轮未跑，见报告）。
- [x] 有改动未验证时，100% 显示徽标；点「运行验证」能触发并回填结果（逻辑与构建通过，未做 UI 点击走查）。
- [x] 自动续跑最多 1 次，不产生额外循环（策略单测；UI 入口未做假模型端到端断言，见报告）。
- [x] 「撤销本次改动」能把本次生成修改的文件全部恢复；冲突有确认提示。
- [x] 重复调用保护可复现：脚本化模型连续 3 次同参调用 → 出现提示；8 次后收尾。
- [x] todo 面板重启后保持；状态包里的计划 ≤ 200 token。
- [x] 新增测试 ≥ 15 个；现有测试全绿。

## 8. 风险与回退

| 风险 | 处理 |
| --- | --- |
| 自动续跑变成死循环 | 硬上限 1 次；失败只留徽标 |
| 验证门误判（外部改动也算 dirty） | 只统计本次会话的编辑轨迹；外部改动不触发 |
| checkpoint 占磁盘 | 只备份被改文件 + 保留 20 个 + 会话删除清理 |
| 撤销覆盖用户手工修改 | hash 冲突二次确认，默认不覆盖 |
| todo 变成形式主义 | 状态包只放摘要；不强制每步都更新；评测看是否真的减少跑偏 |

## 9. PR 拆分

1. **PR1**：todo 工具 + 持久化 + UI + 状态包。
2. **PR2**：验证门 + 自动续跑。
3. **PR3**：checkpoint + 撤销 UI + 循环保护 + 文档/报告。

## 10. 完成定义（DoD）

- [ ] 5 个基准任务的结果进报告（步数、验证、撤销是否可用）；
- [x] 总纲状态表改为 🚧（实现完成，基准未跑），P6/P7 可以直接复用这套任务脚本；
- [x] `docs/task-loop.md` 写清验证门语义、checkpoint 边界、循环上限。
