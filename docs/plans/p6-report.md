# P6 报告：小模型适配

> 期次：P6（第 6 期）　分支：`feature/vibe-p6-small-model`　状态：🚧 实现完成，本地 8K 验证未达标
> 配套文档：[small-model-mode.md](../small-model-mode.md)　原始计划：[p6-small-model.md](p6-small-model.md)
> 本地模型：`local / qwen3-4b-thinking-2507`（LM Studio，8K 档，Compact）

## 1. 交付物

| 模块 | 文件 |
| --- | --- |
| 双模判定 | `Core/Context/AgentMode.cs`、`Models/AgentSettings.cs` |
| 短提示词 | `Core/Context/SystemPrompts.cs`、`ContextBuilder`（技能 ≤600 token） |
| 工具路由 | `Core/Tools/ToolRouter.cs`、`AgentChatService` 接线 |
| 单工具模式 | `Core/Tools/CodeEditToolPack.cs` |
| 调用修复 | `Core/Context/ToolCallRepair.cs`（纯函数） |
| 设置 | `ToolsSettings.razor`（自动 / 完整 / 精简） |
| 文档 | `small-model-mode.md`、本报告、计划与路线图 |

## 2. 计划任务对照

| # | 任务 | 状态 | 说明 |
| --- | --- | --- | --- |
| 6.1 | 模式判定 | ✅ | 窗口 ≤16K、无工具、名称标记；手动覆盖优先 |
| 6.2 | 短提示词 | ✅ | `SystemPrompts.Compact`；Compact 技能 ≤600 token |
| 6.3 | 工具路由 | ✅ | 四类意图、最多 4 个、最近轨迹优先、Full 不裁剪 |
| 6.4 | 单工具模式 | ✅ | `code_edit` 转调 read/search/patch，默认关闭 |
| 6.5 | 调用修复 | ⚠ 部分 | 去围栏 / 平衡 JSON / 尾逗号 / 单引号 / 类型纠正；**未接进 IChatClient 流** |
| 6.6 | 评测运行器 | ⚠ 复用 | 复用 `tools/p5-bench`，没有独立 `tools/AgentEval` |
| 6.7 | 矩阵运行 | ❌ | 本地 4B/8K 跑 A、C 两任务均失败；未跑 7B / 32K 矩阵 |
| 6.8 | 文档与报告 | ✅ | 本文 + `small-model-mode.md` |

## 3. 验收核对

| 验收标准 | 结果 | 证据 |
| --- | --- | --- |
| Compact 系统提示 ≤250 token、工具 ≤4、技能 ≤600 | ✅ | `CompactPromptTests` 4 个；`ToolRouterTests` 6 个；技能分段断言 |
| 8K 7B ≥3/5；3B ≥2/5；32K ≥4/5 | ❌ | 本地只有 4B/8K；A、C 两个任务 0/2；未跑 7B/32K |
| 工具调用解析失败率 ≤10%，修复提升有数据 | ❌ 未测 | `ToolCallRepair` 单测通过，但没有真实流量统计 |
| 单工具 vs 多工具对比结论 | ❌ 未跑 | `code_edit` 行为单测通过，未做成功率对比 |
| 大模型模式无回归 | ✅ | Core **532 通过 / 0 失败**；Windows 214/1 跳过；构建 0 错误 |
| 新增测试 ≥20 | ✅ | 新增 26（模式 7、提示 4、路由 6、单工具 3、修复 6） |

## 4. 测试

- Core：**532 通过 / 0 失败**。
- Windows：**214 通过 / 1 跳过**（本轮无 Windows 改动）。
- `dotnet build SDemo.DesktopAgent.Wpf.slnf`：**0 错误**。

P6 新增 26 个用例：

| 文件 | 数量 | 覆盖 |
| --- | --- | --- |
| `AgentModeTests` | 7 | 8K/16K 边界、32K、无工具、名称标记、手动覆盖 |
| `CompactPromptTests` | 4 | ≤250 token、关键规则、8K 自动 Compact + 技能 600、32K Full |
| `ToolRouterTests` | 6 | 修改 / 查询 / 运行 / 未知意图、最近轨迹、Full 不裁剪 |
| `CodeEditTests` | 3 | read、search、patch 读前写 |
| `ToolRepairTests` | 6 | 围栏、前后废话、尾逗号、单引号、类型纠正、失败止损 |

## 5. 本地 8K 验证

环境：LM Studio，`local / qwen3-4b-thinking-2507`，8K 档，Compact；`temperature=0` 未显式设置（默认），`max_tokens` 512 / 1024 / 2048 尝试。

| 任务 | 步数 | 工具 | 结果 | 耗时 |
| --- | --- | --- | --- | --- |
| A 设置页开关 | 2 | `read_file` ×2 | ❌ 无编辑，输出预算耗尽 | ≈6 分钟 |
| C 高风险命令测试 | 1 | `read_file` ×1 | ❌ 无后续 tool call | ≈5 分钟 |

模型普通对话正常（`OK` 测试 256 token 返回），但 thinking 内容占满输出预算，工具任务里常常没有最终调用。结论：**qwen3-4b-thinking 在 8K/Compact 下不适合当前工具链**；继续跑 5 任务只会消耗大量本机时间，与“收着点”的约束冲突，因此停止矩阵。

## 6. 偏差与取舍

1. **本地验证未达标**。4B thinking 在 8K 下 0/2；没有伪造成 3/5。失败模式明确：思考 token 挤占输出，最终没有 tool call。
2. **P5 任务 B 的确认**。B 实测 9 步、`LoopsPrevented=1`：第一次 `apply_patch` 因 hunk 头格式无效被拒，第二次在达到 8 次工具调用上限时被阻断，最终没有编辑；模型明确说明未完成。P6 已把上限做成 `ToolCallGuard.Limit`（Full 默认 8、Compact 默认 6，来自 `AgentSettings.CompactMaxToolCalls`），但设置页尚未暴露数值，也没有据此把默认值调高；B 类“多次读取后编辑”的任务仍需要更高上限或更准的首次读取。**Compact 默认 6 反而会让 B 更早被截断**，这是下一轮要先解决的参数矛盾。
3. **修复器未接进真实流**。`ToolCallRepair` 是纯函数 + 单测；`IChatClient` 装饰器接入会改动 MAF 流式层，本轮没有做。
4. **评测器复用 P5Bench**。`tools/p5-bench` 已能跑模型 / 任务 / 工具 / 验证；没有再复制一份 `tools/AgentEval`，避免维护两套。
5. **单工具模式默认关闭**。`code_edit` 已实现并通过行为测试，但没有成功率对比，不能据此改默认值。
6. **工具路由是关键词级**。没有模型意图分类；Compact 下 4 个工具的限制可能仍然过紧/过松，需要真实任务调。
7. **输出上限现在真正传给模型**。`ContextBudgetChatClient` 把 `ModelProfile.MaxOutputTokens` 写入 `ChatOptions`；这是本地验证时发现 4B 思考模型需要显式上限后补的。

## 7. 遗留与风险

| 项 | 影响 | 建议 |
| --- | --- | --- |
| 4B thinking 无法完成工具任务 | 8K 小模型边界未达标 | 换 7B 非 thinking / 调低 reasoning；不要把 4B 推荐给工具任务 |
| 矩阵未跑 | 没有 7B/32K 数据 | 用 P5Bench 在性能更好的机器上补；单任务限时 |
| 修复器未上线 | 真实坏 JSON 仍可能失败 | 接 `ResilientChatClient` 并统计修复率 |
| 路由可能选错 | 小模型拿不到需要工具 | 保留 Full 回退；记录路由结果 |
| 单工具未对比 | 默认值缺数据 | 选 2 个任务做 Full vs Compact+code_edit 对比 |

## 8. 提交

P6 代码、测试与文档提交到 `feature/vibe-p6-small-model`；未推送远端。
