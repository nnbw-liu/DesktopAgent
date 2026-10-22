# 小模型模式（P6）

> 目标：同一套上下文策略下，让小窗口 / 弱工具模型也能完成简单任务。
> 计划：[plans/p6-small-model.md](plans/p6-small-model.md) · 报告：[plans/p6-report.md](plans/p6-report.md)

## 1. 双模判定

```csharp
public enum AgentMode { Full, Compact }
public enum AgentModePreference { Auto, Full, Compact }
```

`Auto` 按顺序命中第一个规则：

1. 窗口 ≤ 16K；
2. `SupportsTools == false`；
3. 模型名含 `3b` / `4b` / `7b` / `1.5b` / `tiny` / `mini`。

手动 `Full` / `Compact` 覆盖自动规则。设置页「内循环 → 小模型模式」保存默认值。

| 项 | Full | Compact |
| --- | --- | --- |
| 系统提示 | P1 完整版 | `SystemPrompts.Compact`，≤250 token |
| 技能正文 | 固定块剩余额度 | ≤600 token |
| 工具数 | 全部启用包 | ≤4 个函数 |
| 最大工具调用 | 8 | 6（`AgentSettings.CompactMaxToolCalls`） |
| 工具路由 | 不裁剪 | 关键词 + 最近轨迹 |

## 2. 短提示词

`ContextBuilder` 根据模式和技能块统一拼接；`Compact` 下基础提示词换成：

```text
你是 Windows 上的编码助手。每次只做一步：先用一行 PLAN 说明下一步，再调用一个工具。
...
完成后输出「完成：<一句话>」，不要再调用工具。
```

技能超 600 token 时按原逻辑截断，并追加「技能内容过长」提示。

## 3. 工具路由

`ToolRouter.Select` 根据用户输入关键词选择（最多 4 个）：

- 改 / 修 / 加 / 重构 / 实现 → `read_file`、`search_files`、`apply_patch`、`run_command`；
- 查 / 解释 / 在哪 → `search_files`、`read_file`、`list_tree`；
- 跑 / 构建 / 测试 → `run_command`、`read_file`；
- 不确定 → `list_tree`、`read_file`、`search_files`、`run_command`。

Compact 下最近用过的工具优先复用；`Full` 返回全部可用工具。路由只过滤已启用的工具，不会造工具。

## 4. 单工具模式

`code_edit(action, path, query, patch, offset, limit)`：

- `read` → `read_file`；
- `search` → `search_files`；
- `patch` → `apply_patch`。

内部转调现有工具，所以读前写、审批、路径 jail 行为一致。工具包默认关闭，评测时通过设置启用。

## 5. 工具调用修复

`ToolCallRepair`（当前是纯函数，尚未接进 `IChatClient` 装饰器）：

- 去 ```json / ``` 围栏；
- 截取前后废话中的平衡 `{...}`；
- 允许尾逗号、单引号；
- 把字符串 `"3"` / `"true"` 还原成数字 / 布尔；
- 解析失败返回错误，不猜参数。

## 6. 本地 8K 验证结论（qwen3-4b-thinking-2507）

本机 LM Studio，`http://localhost:1234/v1`，8K 档，Compact，`max_tokens` 分别 512 / 1024 / 2048：

| 任务 | 步数 | 结果 | 现象 |
| --- | --- | --- | --- |
| A 设置页开关 | 2 | ❌ | 两次 `read_file` 后用尽输出，没有编辑 |
| C 高风险命令测试 | 1 | ❌ | 一次 `read_file` 后没有后续工具调用 |

模型可以普通对话（`OK` 测试正常），但**思考 token 占满输出预算**，在工具任务里不产出最终 tool call；单任务耗时约 5–6 分钟。结论：**4B thinking 模型在 8K / Compact 下不适合当前工具链**，需要 7B+ 或非 thinking 模型；不建议为了跑满矩阵而消耗更多本机时间。

## 7. 边界

- 工具调用修复尚未接入真实流式响应，统计 `ToolCallRepaired` 还没有落到报告。
- 评测运行器复用 `tools/p5-bench`，没有单独的 `tools/AgentEval`。
- 只验证了本地 4B / 8K；没有 7B、32K 云端矩阵。
- 单工具与多工具的对比未跑，默认值仍以多工具为主。
