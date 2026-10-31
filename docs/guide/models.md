# 模型接入与路由

> 由根 README 拆出（2026-10-31），内容与原章节一致，仅调整链接前缀。

## 模型接入（Microsoft Agent Framework）

包：`Microsoft.Agents.AI` + `Microsoft.Agents.AI.OpenAI`（均 1.25.0，会带入 `OpenAI` 2.14.0、`Microsoft.Extensions.AI` 等传递依赖）。

```csharp
var client = new OpenAIClient(new ApiKeyCredential(provider.ApiKey),
    new OpenAIClientOptions { Endpoint = new Uri(provider.BaseUrl) });
var agent = client.GetChatClient(modelId)
                  .AsAIAgent(instructions: "...", name: "SDemo Agent");

await foreach (var update in agent.RunStreamingAsync(messages, cancellationToken: ct))
    // update.Text 是增量文本
```

要点：

- 1.25.0 的创建方法是 **`AsAIAgent`**（`OpenAI.Chat` 命名空间），没有 `CreateAIAgent`；响应类型是 `AgentResponse` / `AgentResponseUpdate`。
- **BaseUrl 必须带 `/v1`**：OpenAI SDK 把它当基址，只在后面拼 `chat/completions`，不会补 `/v1`（这是最常见的 404 原因）。
- 取消通过 `CancellationToken`，抛 `TaskCanceledException`（`OperationCanceledException` 子类），先于通用 `Exception` 捕获；HTTP 错误是 `System.ClientModel.ClientResultException`。
- 每次请求都新建 agent（当前实现简单直接）；后续可按 provider+model 缓存，减少构建与连接开销。
- 包体积：framework-dependent 输出约 +10MB；WPF/Blazor Hybrid 默认不裁剪，**不要开** `PublishTrimmed`/AOT（反射与序列化路径未验证）。

## 小模型模式（P6）

8K / 无工具 / 名称含 `3b`、`4b`、`7b` 等模型自动进入 Compact：短系统提示（≤250 token）、技能 ≤600 token、最多 4 个工具、关键词路由、最大 8 次工具调用；可手动切回完整模式。提供 `code_edit` 单工具模式（read/search/patch 三合一）与工具参数修复函数。**8K 只承诺单文件 / 极短任务**（实测 4B 在本仓库短任务 3/5；外部仓库多文件任务 1/8）；实测 `qwen3-4b-thinking-2507` 在 8K/Compact 下无法完成工具任务（思考 token 占满输出），不建议把 4B thinking 模型用于工具任务；详见 [small-model-mode.md](../architecture/small-model-mode.md)。

## 智能模型路由

同一个聊天室里可以按任务难度自动换模型：**闲聊 / 简单** 走便宜模型，**代码** 走工具调用稳定的模型，**复杂**（架构、根因、跨文件重构、长上下文）走强模型。聊天室输入框的模型选择器新增「自动选择」；手选一个模型则固定该聊天室，随时可以切回自动。

- **四种工作方式**（设置 → 模型）：关闭 / 规则（本地判断，零额外请求）/ 混合（规则拿不准才问一次路由模型）/ 总控（路由模型在候选清单里直接选模型）。
- **任务 → 模型映射**：简单、代码、复杂三类分别指定 provider + model，另有兜底模型；路由模型默认复用摘要模型或当前模型，可在设置里单独指定。
- **护栏**：候选必须装得下本轮上下文、代码 / 复杂任务优先要求工具能力；路由失败 / 超时 / JSON 非法逐级回落规则 → 兜底 → 当前模型，不允许阻塞对话。
- **会话粘性**：同会话、同任务类型、同配置时沿用上一轮模型，减少切模型导致的提示缓存失效；任务类型或配置变化会重新路由。
- **可解释**：消息底部显示实际模型；模型选择器选中自动后只显示「自动」，本轮类型 / 模型 / 理由 / 置信度在悬浮说明里；底部累计用量的 tips 也按模型拆分输入 / 输出。

设置与设计细节见 [docs/architecture/model-routing.md](../architecture/model-routing.md)。
