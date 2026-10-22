# P6 — 小模型适配（第 6 期）

> 状态：🚧 实现完成，本地 8K 验证未达标（4B 0/2）
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
| 最大工具迭代 | 8 | 6 |
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
  - 改 / 修 / 加 / 重构 / 实现 → `read_file`、`search_files`、`apply_patch`、`run_command`；
  - 查 / 解释 / 在哪 → `search_files`、`read_file`、`list_tree`；
  - 跑 / 构建 / 测试 → `run_command`、`read_file`；
  - 不确定 → `list_tree`、`read_file`、`search_files`、`run_command`。
- 最多 4 个函数；`Compact` 下优先复用最近用过的工具（减少切换）；
- 路由结果写进 `ContextReport`，便于排查“为什么模型没用某个工具”；
- 用户显式开关（输入框工具 chip）优先于路由。

### 3.4 单工具模式

```csharp
code_edit(action, path, query, patch, offset, limit)
// action: read | search | patch
```

- 一个函数覆盖读/搜/改，参数少、描述短；
- 内部转调现有实现，行为一致（含读前写、审批、路径 jail）；
- 适合 3B–7B 模型；在评测里对比「多工具 vs 单工具」的成功率再决定默认值。

### 3.5 工具调用修复

在 `IChatClient` 装饰器里做（与 P1 的中途裁剪同一层，可合并为一个 `ResilientChatClient`）：

- `FunctionCallContent` 参数是字符串时：
  1. 去掉 ```json / ``` 围栏；
  2. 截取第一个平衡的 `{...}`；
  3. `JsonDocument` 解析失败 → 尝试修尾逗号、单引号、未转义换行；
  4. 类型纠正：`"3"` → `3`、`"true"` → `true`、数组包裹的标量拆开；
- 未知工具名 → 返回 `tool` 结果：`{"error":"unknown tool","available":[...]}`，让模型重试；
- 缺必填参数 → 返回 `{"error":"missing argument: path","schema":{...}}`；
- 连续 2 次修复失败 → 本轮禁用工具，要求模型用文本说明缺什么；
- 统计 `ToolCallRepaired` / `ToolCallRejected`，进评测报告。

### 3.6 评测运行器

新增 `tools/AgentEval`（`net10.0-windows`，引用 Core + Windows）：

```
dotnet run --project tools/AgentEval -- --config eval/compact.json --task T01 --model qwen2.5:7b
```

- 任务来自 `eval/tasks/*.md`：需求、验收命令、最大步数、允许工具；
- 每个任务在**临时副本**里跑（复制仓库 → 独立数据目录 → `SDEMO_DATA_ROOT` 隔离）；
- 记录：成功与否、步数、工具调用明细、峰值上下文、压缩次数、审批次数、耗时、修复次数；
- 结果写 `eval/results/{date}-{model}.json`，汇总成 `docs/plans/eval-matrix.md`；
- `temperature=0`；每个任务最多重跑 2 次取多数结果（减少抖动）；
- 不放进 CI（有成本），手动/nightly 运行。

### 3.7 评测矩阵

| 维度 | 取值 |
| --- | --- |
| 模型 | 1 个 32K 云端、1 个 8K 本地 7B、1 个 4–8K 本地 3B、1 个 128K 云端（示例，按可用性替换） |
| 模式 | Full / Compact / Compact+单工具 |
| 任务 | 5 个（本仓库 3 + 外部仓库 2，外部任务可先占位） |
| 指标 | 成功率、步数、峰值占用、修复率、审批数、耗时 |

报告目标（不达标就继续调，不虚报）：

- 8K 7B：≥ 3/5；
- 4–8K 3B：≥ 2/5（允许更简单的任务）；
- 32K：≥ 4/5；
- 工具调用解析失败率 ≤ 10%。

## 4. 任务拆解

| # | 任务 | 交付物 | 涉及文件 | 估时 | 状态 |
| --- | --- | --- | --- | --- | --- |
| 6.1 | 模式判定 | `AgentMode`、自动规则、会话覆盖、设置项 | `Core/Context/AgentModeResolver.cs` | 0.5 | ✅ |
| 6.2 | 短提示词 | `SystemPrompts`、技能截断、模式接线 | `AgentChatService.cs` | 0.5 | ✅ |
| 6.3 | 工具路由 | `IToolRouter`、意图规则、上限、报告字段 | `Core/Tools/ToolRouter.cs` | 1.0 | ✅ |
| 6.4 | 单工具模式 | `code_edit` 复合工具、开关、行为一致性测试 | `Core/Tools/CodeEditTool.cs` | 0.5 | ✅ |
| 6.5 | 调用修复 | 去围栏/抽 JSON/类型纠正/错误回传/统计 | `Core/Context/ResilientChatClient.cs` | 1.0 | ⚠ 纯函数，未接流 |
| 6.6 | 评测运行器 | `tools/AgentEval`、任务格式、结果 JSON | `tools/AgentEval/*` | 1.0 | ⚠ 复用 P5Bench |
| 6.7 | 矩阵运行 | 至少 2 个模型 × 2 模式 × 5 任务，结果归档 | `docs/plans/eval-matrix.md` | 1.0 | ❌ 本地 4B 0/2 |
| 6.8 | 文档与报告 | `docs/small-model-mode.md`、`p6-report.md` | docs | 0.5 | ✅ |

## 5. 测试清单

**单测**

- `AgentModeTests`：窗口阈值、无工具能力、名称标记、手动覆盖优先级。
- `CompactPromptTests`：长度上限、技能截断、必备规则存在。
- `ToolRouterTests`：四类意图、上限 4、最近轨迹加成、显式开关优先、报告记录。
- `CodeEditTests`：read/search/patch 行为与现有工具一致（含路径 jail、审批、读前写）。
- `ToolRepairTests`：围栏、前后废话、尾逗号、单引号、`"3"`、缺失参数、未知工具、连续失败禁用。
- `EvalHarnessTests`：任务解析、结果 JSON、隔离目录、超时中止。

**集成 / 手工**

- 同一任务在 Full / Compact / Compact+单工具 下各跑 2 次，记录差异。
- 8K 模型任务：读文件 → 改一行 → 构建 → 报告成功。
- 故意用坏模型（返回非 JSON）→ 修复链路能止损，不无限重试。

## 6. 验收标准

- [x] Compact 模式下系统提示 ≤ 250 token、工具 ≤ 4 个、技能 ≤ 600 token。
- [ ] 8K 7B 模型 ≥ 3/5 任务成功；3B ≥ 2/5；32K ≥ 4/5（按评测矩阵）。本地 4B 8K 实测 0/2。
- [ ] 工具调用解析失败率 ≤ 10%；修复后成功率提升有数据。
- [ ] 单工具模式与多工具模式的对比结论写进报告（默认值据此确定）。
- [x] 大模型模式无回归（P1–P5 的验收用例重跑通过）。
- [x] 新增测试 ≥ 20 个；现有测试全绿。

## 7. 风险与回退

| 风险 | 处理 |
| --- | --- |
| 小模型能力天花板 | 用矩阵定边界，不承诺“任意模型”；提供任务分级（简单任务才推荐小模型） |
| 路由选错工具 | 用户显式开关优先；路由结果可见；保留“全部工具”回退 |
| 修复逻辑误改参数 | 只做保守纠正；失败回传错误而不是瞎猜；统计可观测 |
| 评测抖动 | temperature=0 + 每任务 2 次取多数 + 固定仓库快照 |
| 单工具模式能力受限 | 只作为 Compact 默认，Full 不用；对比数据决定 |
| 评测成本 | 本地模型优先；云端只跑关键组合；不进 CI |

## 8. PR 拆分

1. **PR1**：模式判定 + 短提示 + 技能截断 + 单测。
2. **PR2**：工具路由 + 单工具模式。
3. **PR3**：调用修复 + 统计。
4. **PR4**：评测运行器 + 矩阵结果 + 文档/报告。

## 9. 完成定义（DoD）

- [ ] 评测矩阵可复现（给出命令与配置）；本地 4B 数据已记录，完整矩阵未跑。
- [ ] 结论明确：哪些模型/模式推荐，哪些不推荐（4B 不推荐，7B+ 待测）。
- [x] 总纲状态表改为 🚧（实现完成，矩阵未达标），P7 可直接使用该运行器。
- [x] `docs/small-model-mode.md` 写清降级策略与已知失败模式。
