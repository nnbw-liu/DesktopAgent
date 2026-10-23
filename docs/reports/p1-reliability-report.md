# P1 可靠性改造（P7 最后一公里）

> 日期：2026-10-23　范围：工具失败状态、补丁模糊定位与候选补丁、搜索宽松匹配、无修改回灌、shrink guard 开关。
> 目标：不换模型，先把 P7 轨迹里最贵的失败形态（F6 循环 / 补错位置 / 改错文件）压下去，并用 V4.1 Flash 与 4B 8K 重跑基准。

## 1. 结论摘要

1. **工具结果有状态了**：失败 / 被拒绝时结果首行带 `[TOOL_STATUS:rejected|error]`，`ToolTraceEntry` 新增 `rejected`，`AgentRunMetrics` 暴露 `RejectedToolCalls` / `FailedToolCalls`；被拒绝的写入不再被当成成功。
2. **补丁会“找最像的位置”**：精确 → 忽略行尾空白 → 逐行缩进容错（≥70%，选离 hunk 头最近的窗口）；失败时给一份用实际上下文重写、行号修正的候选补丁。
3. **搜索会退化**：正则无命中 → 字面量 → `Type.Member` 的成员名；非法正则也先按字面量试一次。`search_files` 新增 `literal` 参数。
4. **无修改回灌产品化**：Full 模式下，任务型输入、调用过工具、没有成功编辑、且不是反问时，宿主最多追问 3 次；Compact 不打扰。
5. **shrink guard 可配**：默认仍开启，设置页可关闭，用于合法的大段删减。
6. **评测**：`火山 / deepseek-v4.1-flash` 32K **8/8**（V4 Pro 基线 7/8）；本地 `qwen3.5-4b` 8K 结果见 §7。

## 2. 工具失败状态

| 改动 | 文件 |
| --- | --- |
| `StatusRejected` 常量 | `Contracts/Context/ToolTraceModels.cs` |
| 状态标记、异常 / 拒绝 / 编辑失败判定 | `Tools.Abstractions/AgentToolRegistry.cs` |
| 轨迹分类识别标记与编辑失败 | `Core/Services/AgentChatService.cs` |
| 本轮拒绝 / 失败计数 | `Contracts/Services/AgentRunMetrics.cs` |
| 测试 | `ToolStatusTests`、`ToolTraceTests` |

行为：

- 成功结果不写标记，保持旧 token 成本；失败才写一行 `[TOOL_STATUS:...]`。
- `未写入任何文件` / `未替换任何内容` / `疑似整文件重写丢失内容` / 用户拒绝 → `rejected`；
- 参数缺失、工具异常、非法正则、搜索超时 → `error`；
- 循环保护 / 连续失败禁用 → `rejected`，并计入 `RejectedToolCalls`。

> 为什么不改成强类型 `ToolResult`：现有 20+ 个工具都返回字符串，模型也按字符串消费；先加最小状态信封，避免大范围改签名。后续若要结构化，可让信封同时携带 JSON。

## 3. 补丁模糊定位与候选补丁

`Editing/PatchApplier.cs`：

- **三级匹配**：精确 → `TrimEnd` → 逐行 `Trim` 打分（精确 3 / 行尾 2 / 首尾 1），≥70% 才接受；
- **就近选择**：同分时选离 hunk 头最近的窗口，避免落到文件另一处的同名块；
- **单行块不启用**：块少于 2 行时不做缩进容错，降低误改风险；
- **候选补丁**：失败时用最近窗口的实际行重写上下文与删除行，修正 `@@` 行号，模型可直接复制后改一处再提交。

测试：

- `缩进漂移时按内容模糊定位`：8 空格 → 4 空格的上下文仍能应用；
- `候选补丁使用实际上下文与修正后的行号`：断言候选补丁包含 `@@ -1,3 +1,3 @@` 和文件实际行 `beta`。

风险：模糊匹配对“重复代码块”仍可能选错；用 70% 阈值、就近选择和单行块限制收敛，最终仍由写前 hash / 审批兜底。

## 4. 搜索宽松匹配

`Tools.Files/WorkspaceSearch.cs`：

1. 正则（原行为）；
2. 无命中 / 非法正则 → `Regex.Escape` 字面量；
3. 仍无命中且形如 `Type.Member` → 成员名 `Member`；
4. 每次回退在结果首行说明模式，避免模型误以为还是正则。

`search_files` 新增 `literal` 参数，模型可以显式按纯文本搜索。

测试：

- 非法正则 `[` 能在文本 `price [usd]` 上回退命中；
- `a.b` 的 `literal=true` 不再匹配 `axb`；
- `ParserSettings.MaximumDisplayWidth` 能通过成员名命中 `MaximumDisplayWidth` 属性行。

## 5. 无修改回灌

`Core/Execution/NoEditRepromptPolicy.cs` + `ChatRoom.razor`：

- 触发条件：Full 模式、任务型输入（有编辑动词，或非只读任务信号）、本轮至少调用过一次工具、没有成功编辑、输出不是反问；
- 最多 3 次；第 2 次起提示“不要重复读同一文件，按候选补丁改一处”；
- 纯问答、解释 / 检查类请求、Compact 模式不触发；
- 每次回灌是一条 `user` 宿主提示，追加到会话并落库，不伪造 `tool` 消息。

测试：`NoEditRepromptTests` 6 个，覆盖正常触发、成功编辑、被拒绝编辑、Compact、反问 / 纯聊天、上限。

## 6. shrink guard 开关

- `AgentSettings.WriteFileShrinkGuard` 默认 `true`；
- `EditToolPack.WriteFileAsync` 只在开启时拒绝 <70% 的覆盖；
- 设置页「文件写入」新增「拒绝明显缩水的整文件覆盖」；
- 测试：关闭后同一份 30 行文件可以合法覆盖成 `short`。

## 7. 评测结果（2026-10-23）

### 7.1 云端 DeepSeek V4.1 Flash / 32K Full

命令：`tools/AgentEval` + `eval/configs/deepseek-v4.1-flash-32k.json`，仓库 `commandlineparser/commandline@b0b0ec9`，一轮 8 任务。

| 任务 | 类型 | 结果 | 步数 | 轮次 | 耗时 |
| --- | --- | --- | --- | --- | --- |
| T01 宽度校验 | feature | ✅ | 16 | 1 | 22.2s |
| T02 IsParsed | feature | ✅ | 16 | 1 | 30.5s |
| T03 HelpText 宽度 | bugfix | ✅ | 17 | 1 | 23.3s |
| T04 转义引号 | bugfix | ✅ | 16 | 1 | 47.5s |
| T05 PreferShortName 测试 | tests | ✅ | 50 | 3 | 88.0s |
| T06 SplitArgs 测试 | tests | ✅ | 15 | 1 | 41.5s |
| T07 NeedsQuotes 重构 | refactor | ✅ | 16 | 1 | 30.9s |
| T08 SkipDefault 工厂 | mixed | ✅ | 16 | 1 | 17.2s |

**8/8 通过**（P7 的 V4 Pro 是 7/8，唯一失败项 T05）。T05 这次在 3 轮内完成，轨迹里能看到两处新行为：

- `workspace_info` 返回「测试项目：tests/CommandLine.Tests/CommandLine.Tests.csproj」，模型随后用 `glob_files` 定位到 `UnParserExtensionsTests.cs`；
- `apply_patch` 格式失败后被拒绝并带 `[TOOL_STATUS:rejected]`，模型改用 `replace_text`，一次写入成功。

> 注意：这次同时换了模型（V4 Pro → V4.1 Flash）和工具链（P1），**不能把 8/8 单独归因于代码改动**。V4.1 Flash 本身更强，T05 的定位成本也明显下降（V4 Pro 48 步仍失败，这次 50 步 3 轮通过）。

### 7.2 本地 Qwen3.5-4B / 8K Compact

同一任务集、同一 commit，`compact-8k.json`，一轮：

| 任务 | 结果 | 步数 / 轮次 / 耗时 | 失败分类 |
| --- | --- | --- | --- |
| T01–T07 | ❌ | 49–50 步 / 3 轮 / 65–112s | F6 循环 |
| T08 | ❌ | 38 步 / 3 轮 / 119s | F6 循环 |

**0/8**。P7 基线本地 8K 是 R1 0/8、R2 1/8（T02）；这次 0/8 仍在同一量级，说明 4B/8K 在外部仓库的瓶颈（定位文件 + 补丁锚点）没有因为 P1 改变。这正是 P2 把 8K 改成“本仓库短任务 ≥3/5”的原因。

### 7.3 与 P7 基线对比

| 配置 | P7 基线 | P1 重跑 | 说明 |
| --- | --- | --- | --- |
| 云端 32K | V4 Pro 7/8（T05 失败） | V4.1 Flash **8/8** | 模型 + 工具链同时变化 |
| 本地 8K | 4B R1 0/8、R2 1/8 | 4B **0/8** | 外部仓库仍不可用；8K 目标改为本仓库短任务 ≥3/5（P6 3/5） |

## 8. 测试与构建

- `dotnet build SDemo.DesktopAgent.Wpf.slnf -c Release`：0 警告 / 0 错误；
- Core：**632 通过 / 0 失败**（P1 新增 16 个，含 P2 的 workspace_info 用例）；
- Sandbox：**214 通过 / 1 跳过**（Release，`AppContainer作业删除文件产生Deleted变更` 仍跳过）。

## 9. 偏差与遗留

1. **状态信封是文本标记，不是强类型**：模型能看到，但宿主仍要靠解析；后续可升级为 `FunctionResultContent` + JSON。
2. **模糊匹配不是 AST / 语义定位**：对重复块仍可能选错，最终靠 hash、审批、checkpoint 兜底。
3. **候选补丁不保证可应用**：它把“盲试”变成“改一处再提交”，删除行用实际内容重写，可能改变模型原意，必须让模型核对。
4. **无修改回灌依赖关键词**：`LooksLikeEditTask` 是粗筛；“优化一下”这类模糊任务可能不触发，属于有意保守。
5. **Compact 不回灌**：8K 预算紧，避免三次追问把窗口烧光；P2 的单文件任务仍靠用户手动重试。
6. **评测只跑一轮**：V4.1 Flash 与 4B 8K 各一轮，方差未统计；下一轮 R1 再双跑。
