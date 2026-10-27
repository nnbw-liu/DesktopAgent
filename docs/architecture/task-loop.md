# 任务循环与验证门（P5）

> 目标：让多步任务可见、可收敛、可回滚；不新增“智能”，只加约束。
> 计划：[plans/p5-task-loop.md](../reports/p5-task-loop.md) · 实测报告：[plans/p5-report.md](../reports/p5-report.md)

## 1. todo_write

```csharp
todo_write(items: [{ content, status }])
```

- 语义是**整体替换**，不是追加；最后一次调用为准。
- 1–20 项；每项 ≤200 字符；`status` 为 `pending` / `in_progress` / `completed`。
- 多于 1 个 `in_progress` 时保留第一个，其余降级为 `pending`，结果里明确提示。
- 存到 `ChatSession.Todos`，通过 `sessions.TodosJson` 落库；重启后恢复。
- ChatRoom 标题下是可折叠面板：`☑ 任务计划 2/5`，展开显示 `☐ / ◐ / ☑`；不打断聊天流。
- 状态包增加「当前计划」段，独立限制 200 token；没有计划时不占预算。

## 2. 验证门

```text
lastEditAt   = 最近一次带 Edits 的成功编辑轨迹时间
lastVerified = 最近一次成功 build/test 的 At
dirty        = lastEditAt > lastVerified（或从未成功验证）
```

- 只统计**本次会话的工具轨迹**；外部手工改文件不会触发，避免误判。
- `VerificationBadge` 在会话标题下显示：
  - `未验证改动：N 个文件 · 最近验证 10:45`
  - 验证失败时变红，显示第一个错误定位。
- 「运行验证」按钮由用户点击，执行工作区已保存的构建 / 测试命令（不接受模型参数），写审计（`UserInitiated`），结果写回 `LastVerification` 并落库。
- 自动续跑：设置 `verification.autoVerifyAfterEdit`（默认开）且工作区有 build/test 命令时，正常结束后追加一条 **user 角色**宿主提示，最多 1 次；仍 dirty 则保留徽标，不再续跑。不伪造 tool 消息。

## 3. checkpoint / 撤销

- `apply_patch` / `write_file` 落盘前，把将被修改的文件复制到
  `%APPDATA%\SDemo\DesktopAgent\checkpoints\{sessionId}\{timestamp}\files\...`，同目录 `manifest.json` 记录路径、原 hash、是否新建、时间、turnId。
- 一个 turn 的多次编辑共用同一个 checkpoint（按 `RunId` 聚合）；`MarkEdited` 记录编辑后的 hash。
- 保留最近 20 个；`CheckpointStore.DeleteSession` 供会话删除时清理。
- 撤销：`EditSummaryPanel` 的「撤销最近一次改动」；恢复前比较当前 hash 与编辑后 hash：
  - 不一致 → 列出冲突文件，要求确认；确认后 force 覆盖。
  - 新建文件撤销 = 移入 `checkpoints/trash/...`。
- 恢复写审计 `checkpoint.restore`；`CheckpointSettings.Enabled=false` 时不创建。

## 4. 循环保护

`ToolCallGuard`：

- 同一 `(toolId, argsDigest)` 连续 3 次 → 工具结果前加 `[宿主提示]`，提醒换策略；
- 单轮总调用超过 8 次 → 不再执行工具，直接返回“请用文本总结”的结果；
- 计数按每次生成新建；`LoopsPrevented` 供 P7 统计。

这是结果级保护，不是中断 MAF 的内部循环；模型收到阻断结果后应自然收尾。

## 5. 设置

```jsonc
"verification": { "autoVerifyAfterEdit": true, "maxAutoContinue": 1 },
"checkpoints": { "enabled": true, "keep": 20 }
```

设置弹窗「工具 → 验证与检查点」可关闭自动验证提示与检查点。

## 6. 边界

- 5 个基准任务已跑（`tools/p5-bench`，ds / deepseek-flash）：4/5 成功，A/C/D/E 分别 5/4/7/6 步且验证通过；B 在 8 次工具上限处被阻断，未完成编辑。详见 [p5-report.md](../reports/p5-report.md)。
- 撤销只针对最近一个 checkpoint；多次生成后不能选择更早的检查点（API 已有 `List`，UI 暂未做历史选择）。
- 自动续跑依赖 UI 入口，尚未做假模型端到端集成测试；策略本身有单测。
- 验证门只认本会话轨迹；外部编辑器改动不会让徽标变 dirty。
- checkpoint 是文件副本，不是 git commit；大文件会占磁盘，保留 20 个是硬上限。
