# 小模型模式（P6）

> 目标：同一套上下文策略下，让小窗口 / 弱工具模型也能完成简单任务。
> 计划：[plans/p6-small-model.md](../reports/p6-small-model.md) · 报告：[plans/p6-report.md](../reports/p6-report.md)

## 1. 双模判定

```csharp
public enum AgentMode { Full, Compact }
public enum AgentModePreference { Auto, Full, Compact }
```

`Auto` 按顺序命中第一个规则：

1. 窗口 ≤ 16K；
2. `SupportsTools == false`；
3. 模型名含 `3b` / `4b` / `7b` / `1.5b` / `tiny` / `mini`。

手动 `Full` / `Compact` 覆盖自动规则。设置弹窗「工具 → 工具暴露与路由」保存默认值。

| 项 | Full | Compact |
| --- | --- | --- |
| 系统提示 | P1 完整版 | `SystemPrompts.Compact`，≤250 token |
| 技能正文 | 固定块剩余额度 | ≤600 token |
| 工具数 | 全部启用包 | ≤4 个函数 |
| 最大工具调用 | 8 | 8（`AgentSettings.CompactMaxToolCalls`；6 实测会提前耗尽） |
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

- 改 / 修 / 加 / 重构 / 实现 → `read_file`、`replace_text`、`apply_patch`、`write_file`；
- 查 / 解释 / 在哪 → `search_files`、`read_file`、`list_tree`；
- 跑 / 构建 / 测试 → `run_command`、`read_file`；
- 不确定 → `list_tree`、`read_file`、`search_files`、`run_command`。

Compact 下最近用过的工具优先复用；`Full` 返回全部可用工具。路由只过滤已启用的工具，不会造工具。

## 4. 精确替换（小模型首选）

`replace_text(path, old_text, new_text)`：

- 三个参数，描述短；`old_text` 写要替换的原文，`new_text` 写替换后的内容；
- 读前写、审批、检查点、路径 jail 与 `apply_patch` 一致；
- `old_text` 用 LF 匹配 CRLF 文件，写回原换行风格；
- 找不到时先按“逐行 Trim 后完全一致”兜底（修 8 空格 / 4 空格缩进差）；
- 拒绝两类“越补越重复”：`new_text` 已存在、`new_text` 里声明了文件其他位置已有的成员；
- 连续 2 次失败 → 本轮禁用，提示改用 `apply_patch` 或 `write_file`。

`code_edit(action, path, query, patch, offset, limit)` 仍保留，但不再作为 Compact 默认；4B 实测复合参数反而增加选择负担。

## 5. 工具调用修复（真实链路）

`ToolCallRepair` 纯函数仍负责去围栏 / 抽 JSON / 尾逗号 / 单引号 / 类型纠正；本轮补上真实链路止损：

- 缺必填参数：`InstrumentedAIFunction` 捕获 MAF 的 `ArgumentException`，回传 `{"error":"missing or invalid argument","tool":...,"provided":...,"schema":{...}}`，模型可据此重试；
- 工具异常：转成“工具执行失败（未修改文件）”文本，不再只显示 `Error: Function failed.`；
- 编辑工具连续 2 次失败：本轮禁用该工具并给替代建议；
- 硬停：`FunctionInvokingChatClient.MaximumIterationsPerRequest = 调用上限 + 2`，模型无视 guard 也不会无限循环。

## 6. 本地 8K 验证结论（qwen3.5-4b）

本机 LM Studio，`http://localhost:1234/v1`，8K 档，Compact，`max_tokens=2048`，每任务干净工作区单跑：

| 任务 | 步数 | 结果 | 现象 |
| --- | --- | --- | --- |
| A 设置页开关 | 8 | ✅ | `apply_patch` 失败 2 次后切 `replace_text`，字段 + UI 开关都完成 |
| B list_files maxEntries | 14 | ❌ | 长文件补丁锚点找不到，9 次读取后仍未编辑 |
| C 高风险命令测试 | 4 | ✅ | 2 次格式失败后 `replace_text` 一次成功 |
| D 验证失败格式测试 | 13 | ❌ | 写出了新测试，但断言用了不存在的 `record.Format().Message` |
| E npm 工作区测试 | 7 | ✅ | 2 次格式失败 + 1 次找不到后 `replace_text` 成功 |

- **多数结果 3/5**（A 2/3、C 2/2、E 2/3 通过）；单轮最差 2/5，A/E 有抖动。
- thinking 核查（已更正）：默认不发 `reasoning_effort` 时 `reasoning_content` 为空、`reasoning_tokens=0`，`enable_thinking` / `/think` 对该构建无效；但显式发 `reasoning_effort: high` 能开启（实测 300 reasoning tokens 吃满 2048 输出预算）。所以它是**默认关的混合思考模型**，不是「不产生思考」——省略字段在 vLLM / Ollama 这类默认思考的后端会直接落回思考，设置页的「不思考」因此改为显式发 `reasoning_effort: none`。
- 失败主因不是“不调用工具”，而是 `apply_patch` 格式/锚点失败，以及新测试断言写错。

## 7. 边界

- 工具调用解析失败率仍高于 10%；当前策略是“失败两次换 `replace_text`”，不是消除失败。
- 评测复用 `tools/p5-bench`；`temperature` 未固定 0，任务 A/E 有抖动，结果按多数 + 最差同时报告。
- 只验证了本地 4B / 8K；没有 7B、32K 云端矩阵。
- 单工具与多工具未做同任务对照；`replace_text` 的优先级来自失败轨迹，不是严格 A/B。
- 4B 适合单文件或“字段 + 同文件 UI”的小改；长文件、双文件改造（B）不建议交给 4B。

## 8. 8K 定位（P2 决策）

8K 不再承诺“外部仓库多文件任务”，只承诺**本仓库单文件 / 极短任务**：

- 出口：单文件任务 A–E ≥3/5（P6 4B 已 3/5；单轮最差 2/5）；
- 外部仓库 T01–T08 的 1/8 保留为定位能力参考，不作为 8K 指标；
- 多文件、测试补全、外部仓库交给 32K（云端 DeepSeek 7/8）。

配套改动：

1. Full 提示词第 7 条要求测试类任务先用 `workspace_info` / `glob_files` 确认测试项目，只改测试文件；
2. `workspace_info` 会列出 `**/*.csproj` 中名字含 test 的项目（最多 6 个），并提示不要顺手改产品代码；
3. 这些提示只缩小“改错文件”的概率，不降低任务本身难度，也不会把实现细节泄露给模型。
