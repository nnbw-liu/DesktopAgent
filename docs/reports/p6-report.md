# P6 报告：小模型适配

> 期次：P6（第 6 期）　状态：✅ 8K 4B 多数结果 3/5（单轮最差 2/5，A/E 有抖动）
> 配套文档：[small-model-mode.md](../architecture/small-model-mode.md)　原始计划：[p6-small-model.md](p6-small-model.md)
> 本地模型：`local / qwen3.5-4b`（LM Studio，8K 档，Compact，非 thinking）
> 验证日期：2026-10-23　工作区：`F:\AI\Code\SDemo\DesktopAgent`（分支 `feature/vibe-p5-task-loop`，未提交）

## 1. 结论摘要

1. **上一轮的 0/2 不是工具链问题，是 `qwen3-4b-thinking-2507` 的思考 token 吃光了输出**。换成 `qwen3.5-4b` 后，同一套 8K / Compact 工具链可以完成真实任务。
2. **thinking 已确认关闭**：LM Studio 端点不回 `reasoning_content`、`reasoning_tokens=0`；`chat_template_kwargs.enable_thinking` 传 true/false 都没有区别。不需要为了“绕过思考”再带上下文。
3. 5 个任务按计划“最多重跑 2 次取多数”统计：**A/C/E 通过，B/D 失败 → 3/5**。最好一轮 3/5，最差一轮 2/5（A 或 E 抖动）。
4. 失败模式已经定位到具体工具调用，不再是“模型不调用工具”：B 是长文件补丁锚点找不到后空转，D 是新测试断言写错（`record.Format().Message`）且续跑没修对。
5. 为小模型补了 `replace_text`、补丁解析容错、缺参回传 schema、8 次工具上限的硬停、读前写路径归一化；Core 测试 541 通过，Windows 214 通过 / 1 跳过，WPF 构建 0 错误。

## 2. 交付物

| 模块 | 文件 |
| --- | --- |
| 双模判定 | `Core/Context/AgentMode.cs`、`Models/AgentSettings.cs` |
| 短提示词 | `Core/Context/SystemPrompts.cs`、`ContextBuilder`（技能 ≤600 token） |
| 工具路由 | `Core/Tools/ToolRouter.cs`、`AgentChatService` 接线 |
| 单工具 / 精确替换 | `Core/Tools/EditToolPack.cs` 的 `replace_text`；`CodeEditToolPack` 保留 |
| 调用修复 | `Core/Context/ToolCallRepair.cs`、`AgentToolRegistry` 缺参回传、`FunctionInvokingChatClient` 轮数硬停 |
| 编辑容错 | `Editing/UnifiedPatch.cs`（hunk 上下文行容错）、`FileReadRegistry` 路径归一化 |
| 评测运行器 | `tools/p5-bench`：真机工具轨迹、`P5_REPAIR` 续跑、严格标记、测试重建 |
| 设置 | `ToolsSettings.razor`（自动 / 完整 / 精简）；`CompactMaxToolCalls` 默认 8 |
| 文档 | `small-model-mode.md`、本报告、计划与路线图 |

## 3. 计划任务对照

| # | 任务 | 状态 | 说明 |
| --- | --- | --- | --- |
| 6.1 | 模式判定 | ✅ | 窗口 ≤16K、无工具、名称标记；手动覆盖优先 |
| 6.2 | 短提示词 | ✅ | `SystemPrompts.Compact`；Compact 技能 ≤600 token |
| 6.3 | 工具路由 | ✅ | 四类意图、最多 4 个、最近轨迹优先、Full 不裁剪；编辑意图改为 `read_file / replace_text / apply_patch / write_file` |
| 6.4 | 单工具模式 | ✅ | 保留 `code_edit`，新增更窄的 `replace_text`（精确替换）作为小模型首选编辑工具 |
| 6.5 | 调用修复 | ✅ 核心链路 | 缺必填参数不再抛 `Function failed.`，回传 `{"error":...,"schema":...}`；连续失败 2 次禁用该工具并提示替代工具；FICC 轮数硬停 |
| 6.6 | 评测运行器 | ✅ 复用并增强 | 复用 `tools/p5-bench`；新增工具轨迹、工具日志、`P5_MAX_CALLS`、`P5_PROMPT=auto`、验证失败自动续跑 1 次、严格双文件标记 |
| 6.7 | 矩阵运行 | ✅ 4B/8K 5 任务 | 7B / 32K 未跑（本机只有 4B），数据见 §5 |
| 6.8 | 文档与报告 | ✅ | 本文 + `small-model-mode.md` |

## 4. 验收核对

| 验收标准 | 结果 | 证据 |
| --- | --- | --- |
| Compact 系统提示 ≤250 token、工具 ≤4、技能 ≤600 | ✅ | `CompactPromptTests` 4 个；`ToolRouterTests` 6 个；技能分段断言 |
| 8K 模型 5 任务 ≥3/5 | ✅ 多数结果 | A/C/E 通过（A 2/3、C 2/2、E 2/3），B/D 失败；单轮最差 2/5 |
| 32K ≥4/5、7B ≥3/5 | ⏳ 未测 | 本机无 7B / 32K 端点；4B 已到 3/5，7B 目标大概率可达但无数据 |
| 工具调用解析失败率 ≤10% | ❌ 未达标 | 轨迹里 `apply_patch` 的格式/锚点失败仍常见（A 2 次、C 2 次、D 2 次）；靠 `replace_text` 兜底，未降到 10% |
| 单工具 vs 多工具对比 | ⚠ 有定性数据 | 多轮里 `apply_patch` 连续失败后切 `replace_text` 成功（A/C/E）；未做同任务 A/B 对照 |
| 大模型模式无回归 | ✅ | Core **541 通过 / 0 失败**；Windows 214 通过 / 1 跳过；WPF 构建 0 错误 |
| 新增测试 ≥20 | ✅ | P6 原 26 个 + 本轮 9 个（`replace_text` 8 + 重复成员 1），共 35 |

## 5. 本地 8K 验证

环境：LM Studio `http://localhost:1234/v1`，模型 `qwen3.5-4b`（`Q4_K_M`，VLM，capabilities 只有 `tool_use`），8K 档，Compact，`max_tokens=2048`，`CompactMaxToolCalls=8`，宿主每任务 `git reset --hard` 后单独跑，避免任务间污染；`P5_REPAIR=1` 允许验证失败后续跑 1 次。

原始结果（含每次工具轨迹与输出）：[p6-bench-results-2026-10-23.json](data/p6-bench-results-2026-10-23.json)。

### 5.1 代表结果（每个任务取最近一次通过/失败）

| 任务 | 步数 | 工具轨迹 | 结果 | 耗时 |
| --- | --- | --- | --- | --- |
| A 设置页开关 | 8 | 2×read → 2×apply_patch(失败) → 2×read → replace_text×2 | ✅ 标记 + 构建/测试通过 | 31.6s |
| B list_files maxEntries | 14 | 2×read → apply_patch(缺参) → 2×read → apply_patch(锚点失败) → 9×read | ❌ 没有有效编辑，标记缺失 | 83.1s |
| C 高风险命令测试 | 4 | read → 2×apply_patch(格式失败) → replace_text | ✅ 533 测试通过 | 20.2s |
| D 验证失败格式测试 | 13 | read → 2×apply_patch(格式失败) → replace_text(找不到) → write_file → … → write_file | ❌ 新测试断言失败（529/1 failed） | 73.6s |
| E npm 工作区测试 | 7 | read → 2×apply_patch(格式失败) → replace_text(找不到) → 2×read → replace_text | ✅ 533 测试通过 | 26.9s |

### 5.2 抖动与多数结果

同一份代码、同一端点，任务 A 和 E 会因模型随机性落在不同分支：

| 任务 | 通过 / 运行 | 判定 |
| --- | --- | --- |
| A | 2/3（8 步、8 步通过；22 步失败） | ✅ 多数通过 |
| B | 0/3 | ❌ |
| C | 2/2（4 步、4 步） | ✅ |
| D | 0/2（13 步、12 步） | ❌ |
| E | 2/3（7 步、16 步通过；19 步失败） | ✅ 多数通过 |

合计 **3/5**。单轮最差 2/5，最好 3/5。A 的失败轮次里，模型在 AppSettings 上重复添加属性会触发新的重复成员拦截，随后把 8 次调用耗在读取 UI 文件上；通过轮次则在 8 次内完成两个 `replace_text`。

### 5.3 失败模式

- **B：长文件锚点找不到后空转。** 14 步里 9 步是 `read_file`，`apply_patch` 两次失败（一次缺 `patch` 参数、一次 hunk 不匹配），没有切换到 `replace_text`。模型在 350 行文件上无法稳定引用上下文。
- **D：会改文件，但写不对测试。** 最终 `write_file` 写出了新测试，但断言用了不存在的 `record.Format().Message`；续跑只提示“验证失败”，模型没定位到该 API 错误。
- **格式失败仍是主因。** 4B 的 `apply_patch` 经常输出 `*** Begin Patch` / 单行多文件 / 缺 `+++`；这些都被工具拒绝并给出标准格式提示，但模型往往要失败 2 次才换工具。

## 6. thinking 模式核查

用户怀疑“思考模式没关成功”，本轮直接对端点做了三类探测：

| 探测 | 结果 |
| --- | --- |
| 普通对话（`max_tokens=2048`） | `reasoning_content` 为空，`usage.reasoning_tokens=0` |
| 复杂数学/规划提示 | 同上，`reasoning_tokens=0` |
| 流式（601 个 chunk） | 累计 reasoning 字符 0 |
| `chat_template_kwargs.enable_thinking=true / false` | 输出与用量完全一致，端点不接受该开关 |

结论：**这个 `qwen3.5-4b` 构建不产生可见思考 token**。LM Studio 的 `/v1/models` 也只声明 `tool_use`，没有 reasoning 能力。设置里的 `supportsReasoning=false` 只是 UI/档案提示，不会改变请求；实际行为已经是“非 thinking”。因此不需要再“带着上下文”去规避思考预算。

> **后续更正（P7 之后）**：上面的探测漏了 `reasoning_effort`。补测 `reasoning_effort: high` 后该模型返回 300 reasoning tokens / 835 字思维链（正文 0），说明它是**默认关的混合思考模型**，不是「不产生思考」。`enable_thinking` 与 `/think` 确实无效，能开也能关的只有 `reasoning_effort`（`none` 关闭、`low/medium/high` 开启）。因此设置页的「不思考」已从“省略字段”改为显式发 `none`。原结论保留作记录，但不要再据此判断模型没有思考能力。

## 7. 本轮修复（按失败倒推）

1. **`replace_text` 新工具**：`old_text` / `new_text` 精确替换；读前写、审批、检查点、原子写；`old_text` 按 LF 匹配 CRLF 文件，写回原换行风格；缩进不一致时按逐行 `Trim` 兜底；拒绝“新内容已存在”和“重复声明成员”的插入，避免任务 A 的 `CS0102`。
2. **`apply_patch` 容错**：hunk 内无前缀行按上下文行处理；去掉模型会伪造的 `expectedHash` 参数；错误信息带标准 diff 示例和最近相似片段。
3. **缺参不再炸循环**：`InstrumentedAIFunction` 捕获 MAF 的 `ArgumentException`，回传工具名、已提供参数和 JSON schema，模型可据此重试；其他工具异常也转成文本错误。
4. **失败止损**：`apply_patch` / `replace_text` / `write_file` 连续 2 次失败后本轮禁用，并给出替代工具建议。
5. **硬停**：`FunctionInvokingChatClient.MaximumIterationsPerRequest = toolCallLimit + 2`，即使模型无视 guard 提示也不会无限循环；Compact 默认调用上限从 6 提到 8，避免“读两次 + 重试两次”就耗尽。
6. **读前写路径归一化**：`FileReadRegistry` 去掉 `./`、重复 `/`，修掉“刚读过却报没读过”的误判。
7. **评测器对齐产品**：验证失败时用 user 消息回灌一次（不伪造 tool 消息）；标记改成多文件/多符号检查（A 必须同时有字段和 UI 开关）；测试命令去掉 `--no-build`，新增测试会真正编译执行。
8. **本机设置同步**：`%APPDATA%\SDemo\DesktopAgent\settings.json` 的 `agent.compactMaxToolCalls` 已从 6 改为 8，默认值也改为 8；旧配置不会自动迁移，其他机器需手动改。

## 8. 测试

- Core：**541 通过 / 0 失败**（新增 `replace_text` 未读拒绝、精确替换、CRLF、找不到、`./` 读记录、重复插入、Trim 缩进、重复成员、语句复用 9 个用例）。
- Windows / Sandbox：**214 通过 / 1 跳过**。
- `dotnet build SDemo.DesktopAgent.Wpf.slnf`：**0 错误**。
- 基准复现命令：

```powershell
$env:P5_PROVIDER='local'; $env:P5_MODEL='qwen3.5-4b'; $env:P5_MODE='Compact'
$env:P5_PROFILE='8K'; $env:P5_MAX_OUTPUT='2048'; $env:P5_MAX_CALLS='8'; $env:P5_REPAIR='1'
$env:P5_ONLY='A'; dotnet run --project tools/p5-bench -c Release -- <干净工作区>
```

逐任务前先 `git reset --hard`；结果 JSON 会写到工作区上级目录的 `p5-bench-results.json`。

## 9. 偏差与风险

1. **4B 只能算“简单任务可用”**。A/C/E 都是单文件或“字段 + 同文件 UI”的小改；B 这种跨 350 行源码 + 测试的双文件改动仍失败。不要向用户承诺 4B 能独立完成中等重构。
2. **抖动明显**。同一任务不同轮次会在 2/5 与 3/5 之间跳；`temperature` 未显式固定为 0（端点默认），计划里的“取多数”只能缓解，不能消除。
3. **解析失败率未达标**。`apply_patch` 的格式失败仍高，当前策略是“失败两次就换 `replace_text`”，不是把失败率降到 10%。
4. **7B / 32K 无数据**。4B 已到 3/5，但不能据此宣称 7B/32K 达标。
5. **续跑只有 1 次**。D 的 API 错误在 1 次续跑内没修对；更多续跑会放大成本，暂不默认开启。
6. **`replace_text` 不是万能补丁**。它要求模型能精确引用一段原文；B 的失败说明长文件上模型仍可能选错锚点。后续可做“按行号窗口替换”或更强的新旧内容 diff。

## 10. 提交

改动保留在工作区（分支 `feature/vibe-p5-task-loop`），未提交、未推送。验证用的干净 worktree 为 `F:\AI\Code\SDemo\DesktopAgent-p6check`，可删除。
