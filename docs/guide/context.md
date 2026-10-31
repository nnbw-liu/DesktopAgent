# 上下文预算与执行循环

> 由根 README 拆出（2026-10-31），内容与原章节一致，仅调整链接前缀。

## 上下文预算、压缩与工作记忆

每次请求都会先估算 token、按预算裁剪，再交给 Agent Framework；原始消息不删除，数据库里的历史保持完整。实现说明见 [docs/architecture/context-budget.md](../architecture/context-budget.md) 与 [docs/architecture/context-compaction.md](../architecture/context-compaction.md)；工具 / 技能 / 知识库规模化后的能力路由与分段预算计划见 [docs/architecture/capability-routing.md](../architecture/capability-routing.md)。

- **单一入口**：`ContextBuilder` 是唯一组装请求的地方（普通对话 → `AgentSessionRunner`，插件 → `PluginRunner` 的 passthrough）。
- **预算**：`total = (window - 输出预留 - 安全余量) * 0.85`；8K 窗口下输入预算约 4.9K，保留最近 6 轮（小模型 4 轮）。
- **裁剪顺序**：保留最近轮次 → 从最旧整轮淘汰 → 历史附件改桩 → 工具轨迹限额 → pin 截断；当前输入永不淘汰。
- **压缩**：L1 开始淘汰历史时，先把被淘汰的一段做成增量摘要（固定 6 节、带来源序号），再带摘要重建；模型失败 / 超时 / 格式不符走确定性兜底。原文只隐藏不删除。
- **状态包**：每轮把工作区结构（深度 2、≤120 项、30 秒缓存）、最近改动（含 sha256 前缀）、最近工具动作与验证状态放进系统块，默认 800 token（小模型 500）。
- **工具循环**：`ContextBudgetChatClient` 在 FICC 内层，第二次请求前把过长的工具结果落盘到 `<工作区>/.agent/spill`，上下文里只留「头尾预览 + `read_file` 回读指引」（写不进去就退回纯截断），必要时整块淘汰历史，避免「工具结果一多就 400」。
- **溢出重试**：识别到上下文溢出时按半预算只重试一次，并把服务端提示的窗口记进 `context-calibration.json`。
- **计量校准**：用服务端 `usage.input_tokens` 修正启发式估算（EMA，连续 3 次偏差过大自动停用）；「设置 → 上下文 → 高级」可重置。
- **UI**：输入框下方是占用环（估算 / 输入预算），旁边显示本聊天室累计 token（输入 + 输出）；新聊天室不显示，>80% 变黄、>95% 变红；点开看系统提示词 / 技能 / 历史 / 附件 / 轨迹 / 摘要 / 状态包 / pin 的分段明细。被压缩的位置有分隔线，点开可只读查看原文、单条 pin；会话 `⋯` 菜单有「压缩现在 / 清空摘要」。宿主提示（溢出重试、需压缩、自动摘要）显示在消息区末尾，不会进入上下文。
- **模型窗口**：模型编辑器里每个模型可单独选「自动 / 8K / 32K / 128K / 200K」，自动档按模型名与家族推断，拿不准就选 8K（只会更保守）。

## 内循环（P4）

构建 / 测试默认进沙箱；在「设置 → 执行与沙箱 → 直跑命令」显式打开直跑后，`run_command` 可以跳过沙箱复制的等待。直跑只放行白名单可执行文件，参数不经过 shell，每次执行写审计；安全命令可「本会话允许」，高风险命令永不提供。默认关闭，关闭时 `run_powershell` 与沙箱行为不变。构建 / 测试结果会解析成验证记录，落库并进入状态包。详见 [docs/architecture/inner-loop.md](../architecture/inner-loop.md)。

## 任务循环（P5）

多步任务可以用 `todo_write` 维护计划（整体替换、会话级持久化、标题下面板展开）；有编辑但之后没有成功 build/test 时会显示「未验证改动」徽标，可手动运行验证。每次编辑前会创建 checkpoint，`EditSummaryPanel` 可「撤销最近一次改动」，冲突（检查点后又被外部修改）会先要求确认。同一工具调用连续 3 次会收到宿主提示，单轮超过 8 次会被阻断并要求用文本总结。详见 [docs/architecture/task-loop.md](../architecture/task-loop.md)。
