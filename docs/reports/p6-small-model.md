# P6 — 小模型适配（第 6 期）

> 状态：✅ 4B / 8K / Compact 多数结果 3/5（单轮 2–3/5，A/E 有抖动）；7B、32K 矩阵未跑
> 关联总纲：[vibe-coding-roadmap.md](vibe-coding-roadmap.md) · 报告：[p6-report.md](p6-report.md)

## 1. 本期要解决的问题

同一个提示词 + 同一套工具，对大模型够用，对小模型是灾难：

- 20 多个工具函数、长描述、可选参数 → 选错工具、漏参数、乱填参数；
- 长系统提示 + 长技能 → 还没开始任务就占掉一半窗口；
- 多步工具循环里，小模型容易重复调用、绕圈；
- 工具调用 JSON 常被包在 ```json 里，或类型不对（`"3"` vs `3`）。

结论：**小模型能不能用，主要取决于工具面和提示词，而不是模型本身**。本期做「降维」：更少的工具、更短的提示、更强的修复。

## 2. 范围

**做**

- 双模：`Full` / `Compact`，按 `ModelProfile` 自动选择，可手动覆盖；
- 短系统提示词（≤ 250 token）+ 技能截断；
- 工具路由：每轮最多 4 个函数，按意图与最近轨迹选择；
- 单工具模式：一个复合 `code_edit` 工具（读/搜/改三合一）；
- 工具调用修复：去围栏、抽 JSON、类型纠正、未知工具回错、缺参提示；
- 小模型评测矩阵与运行器；
- 一份可复现的 `eval-matrix.md`。

**不做**

- 模型微调 / LoRA；
- 本地推理引擎集成（Ollama 等仍按 OpenAI 兼容端点接入）；
- 多模态；
- 自动模型路由（按任务难度切换模型）→ 下一周期。

## 3. 设计要点

### 3.1 双模判定

```csharp
public enum AgentMode { Full, Compact }
```

自动规则（按顺序，取第一个命中）：

1. 用户显式覆盖（会话级开关 / 设置默认值）；
2. `profile.ContextWindow ≤ 16K`；
3. `!profile.SupportsTools`；
4. 模型名含 `3b` / `1.5b` / `tiny` / `mini` 等小模型标记（保守判定，可被 1 覆盖）。

`Compact` 下生效的差异：

| 项 | Full | Compact |
| --- | --- | --- |
| 系统提示 | 现有完整版 | 专用短版 ≤ 250 token |
| 技能正文 | ≤ 2000 token | ≤ 600 token（超出截断并提示） |
| 工具数量 | 全部启用包 | ≤ 4 个函数 |
| 并行工具 | 允许 | 禁止 |
| 保留轮数 | 6 | 4 |
| 最大工具迭代 | 8 | 8（`AgentSettings.CompactMaxToolCalls`；6 会在“读两次 + 重试两次”时提前耗尽） |
| 工具描述 | 完整 | 精简（只留必填参数） |
| 单工具模式 | 关 | 可开（默认开） |

设置页「上下文」里增加「默认模式：自动 / 完整 / 精简」与会话级切换。

### 3.2 短系统提示词

```text
你是运行在 Windows 上的编码助手。每次只做一步：先输出一行 PLAN，再调用一个工具。
规则：
1. 改代码前必须先用 read_file 读取目标文件；
2. 一次只调用一个工具，等结果再继续；
3. 命令结果以工具返回为准，不要假装执行过；
4. 文件不存在就先用 glob_files / list_tree 找，不要猜；
5. 任务完成后输出「完成：<一句话>」，不要再调用工具。
```

- 约 150–200 汉字，≤ 250 token；
- 技能注入改为「只注入与本次任务匹配的一个技能」；无匹配则不注入；
- 不注入插件说明、沙箱说明的完整段落，只保留一行能力边界。
- 产物：`SystemPrompts.Full` / `SystemPrompts.Compact` 常量，另存 `prompts/compact-system.md` 便于调参（构建时嵌入或直接读文件均可，二选一，需在实现里固定）。

### 3.3 工具路由

```csharp
public interface IToolRouter
{
    IReadOnlyList<AITool> Select(
        ToolSelectionContext context,   // 用户输入、最近 6 条轨迹、模式、可用包
        int maxTools);
}
```

- 意图分类（关键词 + 最近轨迹）：
  - 改 / 修 / 加 / 重构 / 实现 → `read_file`、`replace_text`、`apply_patch`、`write_file`；
  - 查 / 解释 / 在哪 → `search_files`、`read_file`、`list_tree`；
  - 跑 / 构建 / 测试 → `run_command`、`read_file`；
  - 不确定 → `list_tree`、`read_file`、`search_files`、`run_command`。
- 最多 4 个函数；`Compact` 下优先复用最近用过的工具（减少切换）；
- 路由结果写进 `ContextReport`，便于排查“为什么模型没用某个工具”；
- 用户显式开关（输入框工具 chip）优先于路由。

### 3.4 单工具模式

```csharp
replace_text(path, old_text, new_text)   // 小模型首选：精确替换一段原文
code_edit(action, path, query, patch, offset, limit)  // 旧复合工具，保留
```

- `replace_text` 参数只三个，描述短；内部复用读前写、审批、检查点、路径 jail；
- 匹配容错：`old_text` 按 LF 归一化后查找 CRLF 文件；缩进不一致时按逐行 `Trim` 兜底；
- 安全拦截：`new_text` 已存在、或重复声明成员时拒绝，避免“越补越重复”；
- `code_edit` 保留但不再作为 Compact 默认，4B 实测 `apply_patch` 的格式负担太重。

### 3.5 工具调用修复

分两层：

1. **`ToolCallRepair` 纯函数**（已有）：去围栏、抽平衡 JSON、尾逗号、单引号、类型纠正；
2. **真实链路止损**（本轮接入）：
   - 缺必填参数：`InstrumentedAIFunction` 捕获 MAF 的 `ArgumentException`，回传 `{"error":"missing or invalid argument","schema":{...}}`，不再只得到 `Error: Function failed.`；
   - 未知工具 / 连续 2 次编辑失败：本轮禁用该工具，提示改用 `replace_text` 或 `write_file`；
   - 硬停：`FunctionInvokingChatClient.MaximumIterationsPerRequest = toolCallLimit + 2`，模型无视提示也不会无限循环；
   - 统计仍以 `AgentRunMetrics.ToolTrace` 记录，未单独落 `ToolCallRepaired` 计数。

### 3.6 评测运行器

复用 `tools/p5-bench`，不另建 `tools/AgentEval`（避免维护两套），本轮补齐：

- 每个任务在干净工作区里单跑（跑前 `git reset --hard`），结果 JSON 写到工作区上级；
- 记录每次工具调用（工具名 / 参数摘要 / 结果摘要 / 异常），可用 `P5_TOOL_LOG` 落盘；
- `P5_PROMPT=auto` 使用真实 Compact 提示词；`P5_MAX_CALLS` 覆盖调用上限；
- `P5_REPAIR=1` 时，验证失败或验收标记缺失会以 user 消息回灌一次，最多续跑 1 次（对齐产品 `autoVerifyAfterEdit`）；
- 测试命令不再用 `--no-build`，新增测试会真正编译执行；
- 验收标记支持“多文件 + 多符号”，任务 A 必须同时有字段和 UI 开关。

局限：没有独立的任务文件夹与外部仓库任务；`temperature` 未固定为 0；7B / 32K 未跑。

### 3.7 评测矩阵

| 维度 | 计划取值 | 本轮实际 |
| --- | --- | --- |
| 模型 | 32K 云端、8K 7B、3B、128K | 只有 `qwen3.5-4b`（8K 档） |
| 模式 | Full / Compact / Compact+单工具 | Compact（含 `replace_text`） |
| 任务 | 5 个本仓库任务 | A–E 全部单跑 |
| 指标 | 成功率、步数、峰值、修复率、审批数、耗时 | 成功率、步数、耗时、工具轨迹 |

实测（多数结果）：**3/5**，A/C/E 通过，B/D 失败；单轮 2/5–3/5。

报告目标（不达标就继续调，不虚报）：

- 8K 7B：≥ 3/5（4B 已达到 3/5，7B 未测）；
- 4–8K 3B：≥ 2/5（4B 已达到）；
- 32K：≥ 4/5（未测）；
- 工具调用解析失败率 ≤ 10%（**未达标**，`apply_patch` 格式失败仍常见）。

## 4. 任务拆解

| # | 任务 | 交付物 | 涉及文件 | 估时 | 状态 |
| --- | --- | --- | --- | --- | --- |
| 6.1 | 模式判定 | `AgentMode`、自动规则、会话覆盖、设置项 | `Core/Context/AgentModeResolver.cs` | 0.5 | ✅ |
| 6.2 | 短提示词 | `SystemPrompts`、技能截断、模式接线 | `AgentChatService.cs` | 0.5 | ✅ |
| 6.3 | 工具路由 | `IToolRouter`、意图规则、上限、报告字段 | `Core/Tools/ToolRouter.cs` | 1.0 | ✅ |
| 6.4 | 精确替换工具 | `replace_text`、读前写/审批/检查点、换行与缩进容错 | `Core/Tools/EditToolPack.cs` | 0.5 | ✅ |
| 6.5 | 调用修复 | 缺参回传 schema、连续失败禁用、FICC 硬停、补充测试 | `AgentToolRegistry.cs`、`AgentChatService.cs` | 1.0 | ✅ 核心链路 |
| 6.6 | 评测运行器 | 工具轨迹、`P5_REPAIR` 续跑、严格标记、测试重建 | `tools/p5-bench` | 1.0 | ✅ 复用增强 |
| 6.7 | 矩阵运行 | 5 任务单跑，结果归档 | `docs/reports/p6-report.md` | 1.0 | ✅ 4B 3/5；7B/32K 未跑 |
| 6.8 | 文档与报告 | `docs/architecture/small-model-mode.md`、`p6-report.md` | docs | 0.5 | ✅ |

## 5. 测试清单

**单测**

- `AgentModeTests`：窗口阈值、无工具能力、名称标记、手动覆盖优先级。
- `CompactPromptTests`：长度上限、技能截断、必备规则存在。
- `ToolRouterTests`：四类意图、上限 4、最近轨迹加成、显式开关优先、报告记录。
- `CodeEditTests`：read/search/patch 行为与现有工具一致（含路径 jail、审批、读前写）。
- `EditToolPackTests`（新增 9 个）：`replace_text` 未读拒绝、精确替换、CRLF + LF 匹配、找不到原文、`./` 读记录、重复插入、Trim 缩进兜底、重复声明拦截、语句复用放行。
- `ToolRepairTests`：围栏、前后废话、尾逗号、单引号、`"3"`、缺失参数、未知工具、连续失败禁用。
- `ToolPackCompatibilityTests` / `ToolRouterTests`：编辑包顺序、最近轨迹复用。

**集成 / 手工**

- 同一任务在 Full / Compact / Compact+单工具 下各跑 2 次，记录差异。
- 8K 模型任务：读文件 → 改一行 → 构建 → 报告成功。
- 故意用坏模型（返回非 JSON）→ 修复链路能止损，不无限重试。

## 6. 验收标准

- [x] Compact 模式下系统提示 ≤ 250 token、工具 ≤ 4 个、技能 ≤ 600 token。
- [x] 8K 模型 ≥ 3/5：4B 多数结果 3/5（A/C/E；单轮 2–3/5）。7B ≥3/5、32K ≥4/5 未测。
- [ ] 工具调用解析失败率 ≤ 10%；当前 `apply_patch` 格式失败仍常见，靠 `replace_text` 兜底。
- [ ] 单工具模式与多工具模式的同任务对照未跑；有“补丁失败后切 replace_text 成功”的定性数据。
- [x] 大模型模式无回归：Core 541 / Windows 214 / WPF 构建 0 错误。
- [x] 新增测试 ≥ 20 个：本轮新增 9 个，P6 累计 35 个。

## 7. 风险与回退

| 风险 | 处理 |
| --- | --- |
| 小模型能力天花板 | 用矩阵定边界，不承诺“任意模型”；提供任务分级（简单任务才推荐小模型） |
| 路由选错工具 | 用户显式开关优先；路由结果可见；保留“全部工具”回退 |
| 修复逻辑误改参数 | 只做保守纠正；失败回传错误而不是瞎猜；统计可观测 |
| 评测抖动 | 每任务单跑、最多重跑 2 次取多数；`temperature` 未固定为 0，结果按“多数 + 最差”同时报告 |
| 单工具模式能力受限 | 只作为 Compact 默认，Full 不用；对比数据决定 |
| 评测成本 | 本地模型优先；云端只跑关键组合；不进 CI |

## 8. PR 拆分

1. **PR1**：模式判定 + 短提示 + 技能截断 + 单测。
2. **PR2**：工具路由 + 单工具模式。
3. **PR3**：调用修复 + 统计。
4. **PR4**：评测运行器 + 矩阵结果 + 文档/报告。

## 9. 完成定义（DoD）

- [x] 评测矩阵可复现（命令写在报告 §8）；4B/8K 5 任务已跑，7B/32K 未跑。
- [x] 结论明确：4B 适合单文件/小步编辑，不适合长文件双文件改造；7B+ 待测。
- [x] 总纲状态表改为 ✅（4B 多数 3/5），P7 可直接复用该运行器。
- [x] `docs/architecture/small-model-mode.md` 写清降级策略与已知失败模式。
