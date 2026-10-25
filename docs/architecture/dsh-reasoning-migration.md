# 从 DeepSeek Harness 迁移推理流程与思维链展示

> 结论先行：**能迁，但要拆成三件事分别对待**。
> 思维链"怎么展示"几乎可以整搬；"怎么选工具"要搬的是**思路**而不是实现，而且它有一个硬前置——**把工具循环从 `FunctionInvokingChatClient` 手里拿回来**；上下文机制只值得搬三样（快照消息、压缩事务、大输出落盘），其余是 DSH 的引擎包袱，不要搬。
>
> 本文基于对 DSH 0.2.1-rc.3（`app.asar` 解包后的 `@deepseek-ai/*` 包，含各包 README 与实现）与本仓库当前代码的逐行核对。所有结论都带证据位置；凡未验证的地方都写明。

## 0. 结论表

| 层次 | DSH 的做法 | 我们能迁多少 | 代价 | 建议 |
|---|---|---|---|---|
| 思维链/过程展示 | 轮次=步骤序列；过程组（答案在组外）+ 四档配置对象 + 折叠时机 + 工具卡客户端派生 | **80–90%**，主要是 UI 与持久化 | 3–4 天，不动引擎 | **P0，先做** |
| 推理选工具 | **没有路由**：暴露面本身就是决策面，工具描述里写决策规则，模型自己用 CoT 选 | **思路全搬，实现不能搬** | 需要先"拿回循环"，5–6 天 | **P1，核心价值** |
| 历史保真 | 真实 `tool/call` + `tool/result` 消息写进日志，下一步原样回放 | 现在做不到（FICC 内部消化了） | 与 P1 同一件事 | 并入 P1 |
| 上下文机制 | 事件日志 + 派生投影 + **注册式上下文段**（名字/顺序/required/归因）+ 事务化压缩 + token 计量 + 输出落盘 | **只搬 3 件**（快照=内容注册+尾部消息 / 压缩事务 / spill） | 4–6 天 | **P2** |
| 输入与引用 | `@` 文件（只给路径）、`/` 命令（不进模型）、`/` 技能（文本即事实 + 宿主注入） | **90%**，纯新功能 | 4–5 天，与三层解耦 | **P0.5** |
| 上下文仪表 | 社区插件 `dsh-context`：per-request 折叠 + 七类归因 + delta + 事件轴 | **不采用**（纯观测，零优化；评估见 §4.1） | — | — |
| 会话存储/插件体系/PTC | 事件溯源、cordis 作用域、`run_code` 代码模式 | **不搬** | — | 我们的 EF Core + 工具包已经够用 |

一句话版本：**展示层直接搬；输入框补 `@`/`/`（新功能，收益最快）；上下文机制只挑三样；选工具层要动架构（FICC → 自建 loop）。**

## 1. DSH 到底是怎么做的

### 1.1 轮次与步骤：一切都是日志事件

一个 turn = 若干 step，一个 step = 一次模型请求（外加它触发的工具）。每一步推进都写进只追加的会话日志，事件名固定：

```
turn/start → step/start → request/header
           → (assistant 流) → assistant/message        ← 成功，或"有内容的中断"
                            ↳ assistant/attempt        ← 什么都没产生的失败
           → tool/call → tool/result → … → step/end → … → turn/end
```

（`dsh-agent-loop/lib/index.js:970`（`step/start`）、`:994`（`step/end`）、`:1113`（`assistant/attempt`）；事件全集由 `session.append("…")` 实测枚举：`turn/start|end`、`step/start|end`、`request/header`、`request/context`、`assistant/attempt`、`assistant/message`、`tool/call`、`tool/result`、`system/message`、`user/message`、`developer/message`。）

要点：

- **`step` 是有编号的**（`{ turn, step }`），所以「第几步、这一步花了多久、这一步用了多少 token」都能算，也能被 UI 定位。
- **工具结果是被"接纳"进历史的**：`每个被接纳的事实都会在下一步据此派生之前追加到会话日志`（`dsh-agent-loop/README.zh.md:75`）。也就是说下一步模型看到的是**真正的** `tool/call` + `tool/result` 配对，不是宿主转述的一段文本。
- 循环自己是**唯一**的编排者，没有框架代跑工具（对比我们的 `FunctionInvokingChatClient`）。

### 1.2 选工具：DSH 根本不做路由

这是整件事里最容易误判的一点。我把证据摆出来：

- 工具注册表只负责**曝光面**：`模型会看到每个获准工具声明的名称、描述与参数 schema；按 agent 设置的限制可缩小该可见集合`（`dsh-tools/README.zh.md:12`）。
- 缩小曝光面的手段只有三种：**组合期挂不挂这个插件**、`ctx.tools.restrict(filter)` 的允许/拒绝掩码（同文件 `:85`；`dsh-tools/lib/index.js:3055-3075` 的 `view()` 先合并全局与祖先层再按 `restrict` 过滤）、以及 PTC 模式的"折叠成 `run_code`"（是呈现方式，不是按任务选工具）。
- 没有任何关键词表、意图分类器、小模型选工具器。全仓库搜不到 routing 逻辑，也**没有 `tool_choice`**：DeepSeek adapter 的请求体只有 `tools` 映射（`dsh-llm-deepseek/lib/index.js:1726-1742`）。`toolOrder` 只是**排序**（`dsh-system-prompt/lib/index.js:87-95`），默认组合不配置它，于是按名字字典序 —— 稳定，但和"相关性"无关。
- 顺带纠正一个常见误解：`deferLoading` **不会把 schema 从请求里删掉**，它被原样保留进 schema，最终映射为 provider 的 `defer_loading: true`（`dsh-system-prompt/lib/index.js:370-375`、`dsh-llm-deepseek/lib/index.js:1736-1741`）。所以它省的是"模型注意力"，不是"工具 schema token"。
- 工具集真变了，才追加一条 `developer/message`（`tool-addition`/`tool-removal`，带 `headerSeq` 指回哪个 header），并按路由能力重投影声明（`in-history` 保留被移除工具的历史定义）（`dsh-agent-loop/lib/index.js:1241-1259`、`dsh-llm/lib/types/content.js:290-353`）。

那它凭什么选得准？三件事：

1. **工具描述本身就是决策规则**。不是"Read a file from disk."这种功能说明，而是带行为策略的整段话。例如 `todo_write` 的描述是由常量拼出来的：

   ```
   记录并更新任务列表，用来规划多步工作并展示进度；单步琐碎任务跳过。
   开工前每个具体步骤加一条 todo。……任务完成立即标 completed。
   ```
   （`dsh-tool-todo/lib/index.js:21-24`）

   这里塞的是**何时用、何时不用、先做什么后做什么**——模型拿到的不是 API 文档，是操作守则。

2. **曝光面小而稳定**。一个 preset 挂多少个工具就是多少个，不随每轮输入抖动。`README` 明确说 KV cache 的前缀复用依赖"可见定义及其顺序不变"（`dsh-tools/README.zh.md:176`）。所以 DSH 宁可让模型偶尔"想起"某工具不该用，也不让宿主每轮换牌。

3. **兜底是"声明"而不是"猜"**。PTC 模式下模型看到的是 `run_code` + 一份生成的 SDK；`native` 模式就是原生 function calling（`dsh-tools/README.zh.md:128-136`）。两种都不需要宿主预测意图。

**这套能成立的前提**：窗口大（128K+）、模型强（自带高质量 CoT）、风险由别处兜住。它的护栏全是"事后/协作式"的，不是"事前筛工具"：

| 护栏 | 形态 | 证据 |
|---|---|---|
| 重复工具提醒 | `tools/post-execute` 统计同参数连续次数（默认 3/5/8），命中就**塞一条提醒消息**，从不阻断 | `dsh-repeat-tool-reminder/lib/index.js:2118-2126,2234-2260` |
| 工具超时 | 协作式：只 abort `exec.signal`，且仅对**自己声明了 `timeoutMs`** 的工具生效，无全局默认 | `dsh-tool-call-timeout-policy/lib/index.js:93-134` |
| 审批 | `ask` 交给 ApprovalService；`never` 在 waterfall 之前直接 rejected；无"永久允许" | `dsh-user-approval/lib/index.js:172-176` |
| plan mode | 只加一段提示词 + 一个 `exit_plan_mode`，**工具面完全不变** | `dsh-plan-mode/lib/index.js:170-177,230-237` |

也就是说：DSH 的哲学是"**先让模型看到全部，再用提示、审批、超时、压缩兜住**"；我们的哲学是"**先替模型筛掉，再让它选**"。两者都自洽，差别在于我们的筛选器本身会成为错误来源（筛漏了 = 模型永远看不见）。我们的目标场景包含 8K 窗口和小模型，不能照抄 DSH 的全量暴露——见 §3.2 的折中方案。

### 1.3 流式事件与思维链：reasoning 是"一等公民"

LLM 层的流式 chunk 是一个判别联合，`reasoning-delta` 和 `text-delta` 平级，且**按 block index 寻址**：

```
StreamChunk = {type:'block-start', index, blockType}
            | {type:'text-delta', index, text}
            | {type:'reasoning-delta', index, text}
            | {type:'tool-call-delta', index, id, name?, argumentsDelta}
            | {type:'block-end', index, block}
            | {type:'usage', usage}
            | {type:'finish', reason, replayState?}
```

（`dsh-llm/lib/typert.host.js:572`；累加逻辑见 `dsh-llm/lib/types/assembler.js:46-52`；provider 的 `thinking_delta` 翻译成 `reasoning-delta` 见 `dsh-llm-deepseek/lib/index.js:1900-1907`。）

三个关键设计：

- **按 block 组织，不按字符串拼**。一次 assistant 消息的 content 是 block 数组：`reasoning` / `text` / `tool-call` 交错，`index` 即插入位。所以"思考→工具→再思考→正文"是**结构**，不是事后按字符偏移猜出来的。客户端侧同样如此：`{kind:"reasoning"|"text"|"tool-call"}` 是同一个 `blocks[]` 里的兄弟（`dsh-client-ui-chat/lib/client.js:7805-7829`）。
- **`signature` 字段**：`if (block.signature !== void 0 && (block.type !== "reasoning" || typeof block.signature !== "string")) return fail(...)`（`dsh-llm-deepseek/lib/index.js:1538`）。Anthropic 系要把带签名的 thinking 回放（`:1570-1574` 序列化成 `{type:"thinking", thinking, signature}`）；而 **user / tool-result 内容里的 reasoning 必须丢掉**（`:1609` `if (block.type === "reasoning" || block.type === "tool-call") return []`）。**存下来 ≠ 每次都要发**，这是按路由区分的。
- **落地成"要么完整、要么只有尝试"**：正常结束提交 `assistant/message`（含最终 message、compact stream、`usage`）；**已经产生内容后被中断**，也提交一条 `assistant/message` 并带 `interrupted: true`，内容是已交付前缀；**什么都没产生**的失败只提交 `assistant/attempt`（只留流的时间信息，**不保留 reasoning 文本**）（`dsh-agent-loop/lib/index.js:1098-1135`）。所以"用户看到什么"和"历史里有什么"永远一致。

`usage` 带回四个字段：`inputTokens / outputTokens / cacheReadTokens / cacheWriteTokens`（`dsh-llm-deepseek/lib/index.js:1832-1844`）——这是我们做"缓存命中率"和"每轮用量"的数据来源，值得先接上（见 §3.1-(8)）。

### 1.4 上下文：两套替换机制，别混成一套

DSH 没有"每轮重建一个 request 对象"这种概念。它只有一份日志，请求是从日志**派生**出来的。这里其实有**两套**机制，很容易混成一套：

**(a) 系统提示词 = node 0，原位替换。** 系统提示词是日志里的 `system/message` 节点，**最新非空的那个就是有效版本**。提示词变了，就在首个 system 节点上做 `surfaceOp: {op:"replace", startSeq, endSeq}`，并把后续非空系统节点用"空内容替换"清掉；如果路由支持 `systemPromptUpdate: 'in-history'` 且是同一请求系列，则改为**追加**一个新版本（`dsh-agent-loop/lib/index.js:264-295`；`dsh-agent-loop/README.zh.md:122`）。

**(b) 运行时快照 = 末尾的 user 消息，靠措辞自我取代。** 工作目录、沙箱策略、审批策略、时间这些动态事实，**不是**系统提示词的一部分，而是一条 `user/message`：`source = { kind: "runtime-context", form: "snapshot", sections }`，正文以 `Current runtime context. This snapshot supersedes earlier runtime-context snapshots.` 开头（`dsh-agent-loop/lib/index.js:334-349`；`dsh-system-prompt/lib/index.js:136-140`）。

关键细节，**它不删旧快照**：旧 snapshot 仍然留在 surface 里，只是新快照声明"我取代前面所有快照"；唯一的"替换"发生在 step 接纳时——已认领但尚未提交的候选会被本次新渲染的文本替换掉，避免写入过期快照（`:1067-1072`）。旧快照只有在 compaction 的 `replace` 把它们遮蔽之后才退出历史（`:319-326`）。**这是"最后一条权威"而不是"唯一一条"**，代价是历史里会堆若干条过期快照，直到压缩。

**(c) 请求 = 派生结果。** `header.config` + `deriveMessages()` + `toolHistory()` + `header.tools`，**不带 `system` 字段**（`dsh-agent-loop/lib/index.js:1274-1288`）；`request/header` 只记录 config 与工具 schema，不记录系统提示词（`dsh-session/README.md:56`）。

**代价写得也很直白**：`原地替换某个系统节点的提示词变更会使请求从该节点的第一个 token 起就不同——该节点是第 0 号节点时则整个请求都不同`（`dsh-agent-loop/README.zh.md:164`）。DSH 认这个账，换来的是"只有一个权威提示词"。它真正省 cache 的手段是 (a) 的 `in-history` 分支和 (b) 的"追加在末尾"。我们的 P2a（§3.3.1）正是把 (b) 的思路做干净：**只留一条快照，每轮替换它自己**，既拿到"末尾追加"的缓存收益，又不堆垃圾。

### 1.5 压缩与计量：一个服务算价，日志标记即事务

- **单一测量服务**：`ctx.tokenMeter` 在同一个日志 revision 上给"最新规范 envelope + 当前 surface"定价；压缩读的是 `measure()`，UI 的占用率也是它。provider usage 优先、启发式（4 字符/token + block/role 固定开销）兜底（`dsh-token-meter/lib/types/estimate.js:9-13,34-55`）。
- **事务化压缩**：先追加 `compaction/start`（这就是锁，未匹配的 start = busy），准备摘要，重新校验稳定性，再追加 `compaction/summary` + 一条 `surfaceOp:{op:"replace"}` 的 user 消息，最后恰好一次 `compaction/end`（`dsh-compaction-basic/lib/index.js:469,634-661,484`）。崩溃留下的是可恢复证据，不是半截状态。
- **范围规则**：从"第一个非 system 节点"开始，尾部反向累积到 `retainTokens`（默认 16% 的可用窗口），并回退到不切断 tool call/result 配对的边界（`:410-433`）。**系统提示词永不被遮蔽**。
- **先修剪再摘要**：`dsh-compaction-tool-result-pruner` 不调模型，超 8192 字符的文本结果改成 head 4096 + 标记 + tail 1024，修剪后回到阈值内就**整次摘要都省掉**（`dsh-compaction-tool-result-pruner/lib/index.js:8-14,91-124`）。
- **摘要请求复用热前缀**：按顺序回放 surface node 0 的 system、上次的工具声明、被遮蔽区域的消息，最后才追加压缩指令——摘要调用是原对话的真前缀（`dsh-compaction-basic/lib/index.js:292-323`）。
- **不缩小的摘要直接拒绝**（`:596-606`）。
- 溢出恢复：`agent/request-error` 收到 `CONTEXT_WINDOW_EXCEEDED` 时，绕过阈值做一次最大头部缩减，并且只在 surface 换代成功后才允许重试（`:861-887`）。

### 1.6 大输出：两层，别混成一层

| 层 | 时机 | 阈值 | 结果 |
|---|---|---|---|
| **spill**（`dsh-spill-policy`） | 每个工具结果被接纳时**立即** | `maxInlineTokens`（默认 12500 token） | 全文落盘，上下文里换成"头/尾预览 + `(Omitted N bytes. Full result stored at: <path>.)`"（`dsh-spill-policy/lib/index.js:24-27,179-235`） |
| **pruner**（`compaction-tool-result-pruner`） | **只在 compaction 触发后** | 8192 字符 | head 4096 + `[... pruned ...]` + tail 1024（§1.5） |

spill 是存储 seam（`saveText()` 返回不透明 locator/bytes/retrievalHint），本地后端按会话分目录、文件 0600；失败是**尽力而为**：存不下就保留内联结果并告警，绝不把成功的工具调用变成错误（`dsh-spill/README.zh.md`；`dsh-spill-policy/lib/index.js:232-235`）。`read` 工具的输出被显式跳过（`:239`），否则会自己 spill 自己。

### 1.7 展示：过程组是折叠单元，reasoning 只是组里的一行

DSH 的聊天区不是"一条消息 + 一堆工具行"，而是 **Turn → Step → Block**，再加一层"过程组"：

- **一个 `assistant-step` 节点会被拆成两个视图席位**：`groupPart:"reasoning"` 留在过程组里（跟着折叠），`groupPart:"response"` 作为答案浮出到组外（`dsh-client-ui-chat/lib/client.js:11515-11528`；渲染时按 part 过滤 `:6268-6269`）。这是"思考 → 工具 → 思考 → 答案"视觉秩序的根——**答案从来没被塞进过程组**，所以不需要"答案开始就自动折叠思考"这种补丁。
- **折叠单元是过程组，不是 reasoning 行**。reasoning 的展开态是每行独立的 `useDisclosure`（`CHAT:6165`）；它被隐藏，只可能是因为整个 Turn 的过程组被折叠（`CHAT:6362`）。运行中 / 被中断 / 失败 / abort 的 Turn **永不折叠**（`CHAT:3644-3648`）。
- **reasoning 行的形态**：`icon(14px) + "Think"/"思考"`，默认折叠，折叠态显示**一行摘要**：运行中取"**最后**一个已完成段落的首行"、结束后取"**第一行**"（`CHAT:6166`），去掉 `**`，带扫光 + 右侧渐隐（`CHAT:6178`）。展开才渲染正文，左缩进 22px。**没有每行的时长/token 计数**——时长和 token 都在 Turn 级（运行中 `Deep diving for {duration}`，`CHAT:5611`；用量行里单列 `({tokens} reasoning)`，`CHAT:6902`）。
- **四档显隐是一个配置对象，不是散落的 if**（`CHAT:12662-12691`）：

  | 档 | `foldCompletedTurns` | `stepGrouping` | `liveProcessDetail` | `settledReasoningPreview` |
  |---|---|---|---|---|
  | compact | true | `collapsed` | false | **false** |
  | standard | true | `collapsed` | true | true |
  | detailed | true | `history` | true | true |
  | verbose | **false** | `none` | false | true |

  另有独立的**折叠时机** `collapseTiming: "completion" | "next-input"`（`CHAT:12621`）：后者要等下一条输入才折叠（`:2752,2842`），这就是"我还想再看一眼刚想的东西"的解法。
- **手动开合按 `(turn, answerStep)` 记忆**（`CHAT:3670-3673`），答案步变化后旧选择失效；模式切换保留开合状态，只有外层真正隐藏成员时才重置披露（`CHAT:3768-3775`）。
- **工具行进化为"单行 + 键控卡片"**：一行 `icon + title + · + summary`；5 态 `preparing/running/ok/error/stopped`，**状态只改 `data-state` 与无障碍文案，不换图标**（`dsh-client-ui-tool/lib/client.js:317,1579-1587,1833`）；只有 `state != preparing` 且存在 input/output/card 时可展开（`:1612`）；富卡片按 wire tool name 走 `tool.call.toolview` 键控分发，未注册的回落 generic（`:1967`）。长输出用**固定高度 + 内部滚动**（terminal 224px、列表 320px），不靠截断字数（`:2435,1321`）。
- **工具展示由客户端派生**：内置 Web Client **不消费** server 的 `presentCall/presentResult`，而是从"原始参数 + 结果 content + isError + 持久 meta"现场算卡片（`dsh-tools/README.md:95`）。这条对我们特别有用，见 §3.1-(2)。
- **transient 与 durable 双轨**：流式是先按**分数 seq** 插进事件流的 `assistant/live-chunk`（client-only，不持久化），结算时按 `attemptId` 整批替换成 durable 的 `assistant/message`（`dsh-api-session-controller/lib/client.js:2036-2048`；`CONV:2633-2650`）。所以重试不会出现"半截文本 + 完整文本"并存。
- **每轮 token 用量**可展开，但"记账不完整或相互矛盾时整个详情都不显示"（`dsh-client-ui-chat/README.zh.md:67`）——宁可没有，不给假数字。
- **思考正文是可替换的 slot**：`conversation.chat.reasoning.body` 只在展开时提供"原始文本 + 流式状态"；官方条目（优先级 100）调 `conversation.chat.reasoning.content` Factory，替换项可喂派生文本（例如翻译），但必须保留查看原文的能力（`dsh-client-ui-chat/README.zh.md:120`；`CHAT:7177-7190`）。

顺带说明两个容易张冠李戴的包：`dsh-agent-tool-presentation` **不是**"每个工具的展示规格"，它只是 agent preset 里声明"模型看到 `native` 还是 `ptc`"的一行（`dsh-agent-tool-presentation/lib/index.js`）；翻译插件叫 `dsh-experimental-client-ui-cot-translation`，做法见 §3.1-(7)。

### 1.8 上下文段：谁都能贡献一段，带名字、顺序、必需性

> **先分清两个"context"**：
> 1. **DSH 发行包内的上下文机制**（本节）——`dsh-system-prompt` 的**上下文贡献注册表**，加上 `dsh-time-context` / `dsh-harness-dev-context` / `dsh-tmux-context` 这几个贡献者（README 里合称"context 组"）。发行包里**没有**名为 `dsh-context` 的包。
> 2. **社区插件 `dsh-context`**（`github.com/bowenliang123/dsh-context`）——它是**上下文仪表**（读数、归因、趋势、浏览），不参与请求组装，只读官方计量投影与会话日志；**零优化，已评估不采用**（见 §4.1）。

**注册契约**（`dsh-system-prompt/lib/index.js:253-313`）：

```ts
ctx.systemPrompt.context({
  name: 'time-context',      // 稳定名字，用于归因、去重、作用域覆盖
  order: 115,                // 排序位（CENTRAL 的 CONTEXT_ORDERS 里统一分配）
  text: (context) => '…',    // 或静态字符串；渲染时插值 {{var}}
  required?: true,           // 预算抑制时也不许丢
  interpolate?: false,       // 原文输出，不做 {{}} 替换
})
```

配套四件事：

- **顺序是中央分配的常量表**，不是各自拍脑袋：`WORKING_DIRECTORY 100`、`SANDBOX_POLICY 110`、`APPROVAL_POLICY 115`、`SUBAGENT_DELEGATION 120`、`TEAM_IDENTITY 125`、`HARNESS_SOURCE 200`、`WEB_SURFACE 210`（`dsh-system-prompt/lib/index.js:44-52`）。排序规则是 `order → name`（`:101-103`），所以**同样输入必得同样字节**，可测试、可缓存。
- **空段整段消失**（`filter(text => text.length > 0)`，`:155`），成本为 0——不是"渲染一个空标题"。
- **required 段在抑制时仍然保留**：`suppressRuntimeContext()` 只摘掉可选段，`required: true` 的一律留（`:324-346,394`）。这是给安全事实用的：沙箱策略、审批策略这类东西**不能因为预算紧、或者用户说"忽略前面的指令"就消失**。
- **归因是结构化的**：渲染时同时产出 `renderContextSections()` 的**具名贡献数组**（`:151-156`），所以 UI/计量能按段说明"这 47 token 是沙箱策略贡献的"，而不是只有一个总数。

**每个贡献者自带三样东西**，以 `dsh-time-context` 为例（`dsh-time-context/README.zh.md`）：

1. **读数形态固定、可机读**：
   ```
   Time sampled while preparing turn 1, step 1: 2026-10-24T19:50:40+08:00[Asia/Shanghai]
   Browser time zone for this request: Asia/Shanghai.
   Elapsed since the preceding model-visible message: unavailable.
   ```
2. **节流**：`refreshIntervalMs` 默认 10 分钟，因为**每条读数都会一直留在历史里直到压缩**（`#### Token 影响：每个读数都会累积`）；设 0 才是每步都发。
3. **缺失/歧义时的指令**：时区混杂 → 要求模型**去问用户**而不是猜；没有基线 → `unavailable`；时钟倒退 → 钳到 0。

它落地成一条 `user/message`，`source = { kind: 'time-context', form: 'snapshot', sections: [{ name: 'time-context', text }] }`（`:67`），KV 影响记的是"仅追加"。

**这套设计真正的价值不在"加了几段文字"，而在于：**

| 性质 | 没有它会发生什么 |
|---|---|
| 段有**名字** | 无法单独开关 / 单独计量 / 单独测；只能测"整个状态包" |
| 段有**中央顺序** | 渲染字节随代码路径变化，提示词缓存和 golden test 都不稳定 |
| 段有 **required** | 安全事实会被预算或压缩顺手剪掉 |
| 段**空则消失** | 每个开关都得额外写"if 关闭就跳过"，成本不可归零 |
| 段有**所属子系统** | 加一个上下文事实要改唯一的那个 god-formatter，谁都怕动 |

### 1.9 输入框：`@` 与 `/` 是三条独立管线

DSH 的输入框不是"一个文本框 + 一个发送键"，而是三条各自独立、共享同一套触发器基础设施（`dsh-client-ui-input-trigger`）的管线：

| 管线 | 触发 | 候选来源 | 选中后发生什么 | 进模型吗 |
|---|---|---|---|---|
| **`@` 文件引用** | 行首或空白后的 `@` | `fileReferences/list`（工作区路径，只有路径没有内容） | 插入**普通提示词文本** `@path` / `@"path with spaces"` | 是（**只有路径文本**） |
| **`/` 命令** | 行首 `/` | `ctx.commands` 注册表 | 直接对该 agent 执行 handler | **否**（不进模型、不花 token） |
| **`/` 技能** | 同一个 `/` | `skills/list` | 字面文本照发 + 宿主在 pre-step 注入 `<skill_content>` | 是（正文被注入） |

#### `@` 文件：只给路径，绝不给内容

这是最值得抄的一条决定。`dsh-file-reference` 写得很直白：**"选中候选绝不读取或附带文件内容；模型必须调用文件系统工具才能查看文件"**（`README.zh.md:12`）。配套的是提供方可以装一段 `FILE_REFERENCE_PROMPT`，告诉模型"先读被引用的文件，再声称检查过它"（`:40`）。

好处在 8K 下尤其明显：引用一个 5000 行的文件只花几个 token，而不是 5000 行；而且模型仍然必须走一次 `read_file`——**"引用"不等于"看过"**。

语法细节（`dsh-file-reference/README.zh.md:32`）：

- 行首或空白后的 `@path` 触发补全；`foo@bar.com` 这种词内 `@` 不触发。
- `@"path with spaces"` 打开带引号的 mention；**目录候选在尾斜杠之后保持引号打开**，所以可以一层层往下钻。
- 格式化器拒绝含控制字符或内嵌引号的路径（语法表达不了就不给）。
- 相对路径按工作区根解析，绝对路径原样保留。
- **原子 chip 的序列化形式就是那段文本**（`dsh-client-ui-reference/README.zh.md:32`）：隐藏 `ref`、剪贴板、发送出去的都是同一个 `@path`。所以"手打 `@foo.cs`"和"从菜单选"完全等价，历史回放也不需要任何额外协议。
- 目录行有**两个动作**：pick（Enter / 点行主体）选目录本身；drill（Tab / 行尾 chevron）不关菜单、继续下钻（`:32`）。这个细节决定"@"能不能用起来。
- 菜单里还有第二个域 **`@session`**：选另一个会话，插入 `@[label](dsh-session:…)`，由 session-reference 服务在 `agent/pre-step` 校验并捕获上下文（`:34`）。这是"引用一段历史"的入口，和文件引用共享同一个菜单。

#### `/` 命令：UI 平面，不进模型

`dsh-commands` 的核心契约（`README.zh.md:12,50`）：

- **命令不产生模型消息**：handler 直接操作 agent，结果由 UI 在模型历史之外渲染，token 成本为 0；只会留下 `command/run` / `command/done` 两条日志事件。
- **严格语法**：首字节必须是 `/`，随后小写名（可含 `_` `-`），再之后是输入末尾或空白；名字之后的所有字节（**包括分隔空白**）都归该命令自己解析。
- **未知命令必须被拒绝**，不能变成用户提示词（`:50`）。这条很重要：`/compcat` 打错一个字母，不该变成"用户说了一句话"。
- 命令可以声明 `input.attachments` 接受图片/文件；**附件准入在 handler 之前**，不符合声明就直接结算成错误（`:58`）。
- 同一作用域重名注册直接抛异常；agent 作用域的命令遮蔽全局同名命令（`:54`）。

#### `/` 技能：文本即事实，命令优先

`dsh-client-ui-skill` + `dsh-tool-skill` 的组合很精巧（`dsh-client-ui-skill/README.zh.md:12,28`；`dsh-tool-skill/README.zh.md`「调用边界」）：

1. 用户 `/` 选技能或直接打 `/name`，**发出的消息里保留字面 `/name`**——没有结构化载荷、没有 occurrence 身份。
2. 宿主在 `agent/pre-step` 扫描"已认领的用户消息"，发现空白分隔的 `/name` 指向工作区里**用户可调用**的技能，就把同一份 `renderSkillContent` 输出作为 `user` 指令上下文**追加在该步骤所有注入的最后**（最贴近模型回答的位置）。
3. **裁决顺序**：名字与宿主命令相同时，**命令赢**，这行根本不会成为提示词（`dsh-client-ui-skill/README.zh.md:28`）。
4. 目录（`<available_skills>`）里专门写了一句防重复加载："用户显式注入的技能不要再经工具加载"（`dsh-tool-skill/README.zh.md`「会话目录」）。
5. **`disable-model-invocation` 的技能只有 `/` 能进**——它不出现在目录里，模型永远看不到，只有用户显式点名才加载（同节）。这是个干净的权限边界。

菜单排序在两边是同一套：查询按**大小写不敏感的有序子序列**匹配，前缀命中排最前，同分保持宿主顺序（`dsh-client-ui-skill/README.zh.md:32`）。

## 2. 和我们现状的逐项对照

（我们的代码位置来自对 `src/` 的核对。）

| 机制 | DSH | 我们 | 差距性质 |
|---|---|---|---|
| 编排 | 自建 loop，step 有编号 | `FunctionInvokingChatClient` 代跑工具循环（`MafAgentRuntime.cs:43-46`） | **架构级**：拿不到 step 边界 |
| 推理事件 | `reasoning-delta` 与文本并列、按 block | `TextReasoningContent → AgentStreamEvent.Reasoning`（`MafAgentRuntime.cs:73-75`），但和正文**合并成一个 `AgentDelta(Text, Reasoning)`**（`AgentChatService.cs:224-229`） | 数据够，粒度不够 |
| 工具历史 | 真 `tool/call`+`tool/result` 进日志 | 历史里是 `[工具轨迹]` **文本块**（`AgentMessageMapper.cs:70-72,195-208`） | **保真度损失** |
| 工具选型 | 不路由，靠描述 + 稳定曝光 | 关键词/意图路由 + 上限截断 + 小模型建议（`CapabilityRouter.cs:107-176`） | 哲学相反 |
| 同轮补工具 | （不需要） | 明确禁止；`search_capabilities` 只能"下一轮再说"（`AgentToolRegistry.cs:154`） | 缺一个闭环 |
| 提示词 | 系统提示词 node 0 原位替换（或 `in-history` 追加） | 每轮重建；状态包**拼在系统提示词尾部**（`ContextBuilder.cs:70-79,416-479`） | 位置不同：应该挪到历史末尾 |
| 运行时快照 | 末尾 **user 消息** + "supersedes" 措辞，不删旧快照 | **没有这个概念**；最接近的是状态包文本 | 需要新建（P2a） |
| 压缩 | 事务 + 先修剪 + 复用热前缀 + 拒绝不缩小 | `ContextCompactor` 先定范围再摘要，失败退化为抽取式；无事务标记（`ContextCompactor.cs:67-215`） | 稳健性差距 |
| 大输出 | spill 落盘（每个工具结果）+ 压缩期 pruner | 头 1200 / 尾 300 截断 + 省略说明（`ContextBudgetChatClient.cs:74-75,199-261`） | 能力差距（不可回读） |
| 展示 | 过程组为折叠单元，答案在组外；四档配置对象；工具卡客户端派生 | 交错时间线 + 每步折叠 + "思考中…"（`ChatTimeline.razor:14-62`）；**答案在组内、工具行不可展开、无档位** | 完成度 + 结构差距 |
| 重开还原 | 日志回放，零信息损失 | 靠 `ReasoningChars/TextChars` 字符边界切（`ChatTimeline.cs:16-48`），旧记录 `<0` 直接退化成分组 | **脆弱** |
| 步骤持久化 | 事件即事实 | `Steps` 只在内存（`ChatMessage.cs:21-25`）；落库只有 `Reasoning` + `ToolTraceJson`（`ChatStore.cs:296,313`） | 缺口 |
| 输入框 | `@` 文件 + `/` 命令 + `/` 技能 三条管线 | 只有一个 `<textarea>` + 附件（`ChatComposer.razor:39-43`），提交只有 `Text` + `Attachments`（`ComposerSubmission.cs:4`） | 功能缺口（P0.5） |

### 2.1 已被误读的两点，先纠正

1. **"DSH 用小模型路由" —— 没有。** 小模型选工具这件事，恰恰是我们自己的 M4 方案（`SmallModelRoutingAdvisor`）。DSH 的选择是"把决策规则写进工具描述，让主模型自己想"。
2. **"我们的 reasoning 展示已经很接近了" —— 形态接近，机制不同。** 我们是"实时按到达顺序追加 + 重开时按字符偏移反推"；DSH 是"结构化的 step 记录 + 日志回放"。前者每加一种过程行（比如重试行、审批行、压缩行）都要再发明一个偏移量；后者加一种事件就行。

## 3. 迁移方案

### 3.1 P0：思维链与过程展示（直接搬，不动引擎）

目标：**不碰运行时的前提下**，把 DSH 的"看得懂的推理过程"补齐。这块 ROI 最高，风险最低。

#### (1) 步骤模型升级

现在 `ChatStep` 只有一个 `Kind + Text + ToolIndex`（`ChatStep.cs`），信息量不足以画 DSH 那种行。扩成：

```csharp
public enum ChatStepKind { Reasoning, Text, Tool, HostPrompt, Approval, Retry }
public enum ChatGroupPart { Reasoning, Tool, Response }   // 答案与过程的分界线

public sealed record ChatStep(
    ChatStepKind Kind,
    string Text,
    int ToolIndex = -1,
    int StepIndex = 0,                     // 第几次模型请求（DSH 的 step）
    ChatGroupPart Part = ChatGroupPart.Reasoning,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? EndedAt = null,
    string? CallId = null);                // Tool 步骤用 CallId 而不是下标做身份
```

关键点有三个：

- `StepIndex` 让"第 2 步的思考"成立，也是 P1 直接对接事件的地方；
- `Part` 决定它进过程组还是留在组外（§3.1-(3)）——这是结构而不是样式；
- `CallId` 让步骤在"运行中条目被原地替换"（`AgentRunMetrics.cs:143-164`）时保持身份稳定，不必依赖 `ToolIndex` 的数组位置。

#### (2) 工具行：客户端派生展示（照抄 DSH 的架构决策）

DSH 不让工具作者写 UI，而是客户端拿"原始参数 + 结果 + 失败状态"现场算卡片（`dsh-tools/README.zh.md:95`）。我们现在的 `ToolTraceEntry` 只有 `ArgsDigest`（摘要），画不出卡片，所以要补两个字段：

```csharp
// ToolTraceModels.cs
public sealed record ToolTraceEntry(
    string ToolId, string Status, int? ExitCode, string? ArgsDigest, string? Summary,
    int OutputChars, DateTime At,
    IReadOnlyList<EditedFile>? Edits = null,
    int ReasoningChars = -1, int TextChars = -1, string? CallId = null,
    // 新增
    string? ArgsJson = null,        // 截断到 ~512 字符，供客户端渲染卡片
    string? Title = null,           // 例：dotnet build SDemo.sln
    int DurationMs = -1,
    string? ResultPreview = null);  // 首 200 字，供展开预览
```

然后一个纯展示层类：

```csharp
public static class ToolPresentation
{
    public static ToolCard Describe(ToolTraceEntry e) => e.ToolId switch
    {
        "run_command" or "run_powershell" => new ToolCard("terminal", e.Title ?? e.ToolId, "命令", e.Summary, Diff: null),
        "replace_text" or "apply_patch"     => new ToolCard("diff", e.Title ?? e.ToolId, "编辑", e.Summary, e.Edits),
        "read_file"                          => new ToolCard("file", e.Title ?? e.ToolId, "读取", e.Summary, null),
        "search_files" or "glob_files"       => new ToolCard("search", e.Title ?? e.ToolId, "搜索", e.Summary, null),
        _                                    => new ToolCard("generic", e.ToolId, null, e.Summary, null),
    };
}
```

这一步的收益不是"好看"，是**同一条轨迹能同时服务聊天区和将来的轨迹视图**，且加工具不用改 UI。

#### (3) 过程组（Process Group）：照抄"答案在组外"的结构

这是整块里最值得抄的一条，而且**它解释了为什么我们现在的折叠逻辑总是别扭**：

DSH 把一个 `assistant-step` 拆成两个成员——`groupPart:"reasoning"` 进过程组，`groupPart:"response"` 留在组外（`dsh-client-ui-chat/lib/client.js:11515-11528,6268-6269`）。因此"答案永不隐藏"不是靠规则，是**结构上就做不到**。

我们现在的做法是"在一个消息容器里交错渲染思考/工具/正文"（`ChatTimeline.razor:6-65`），于是必须靠"工具一开始就把思考收起"这类补丁来维持秩序（`ChatRoom.razor:1998-2002`）。改成：

```
[ProcessGroup 组头]  ▸ 过程 · 5 步 · 3 次工具 · 12.4s        ← 只有这一段可折叠
   ├ 思考 · 1,240 字                     ← 每行独立开合
   ├ run_command   dotnet build …  成功
   └ 思考 · 380 字
[答案正文]                                                   ← 永远在组外，永不折叠
```

`ChatStep` 加一个 `GroupPart`（`Reasoning`/`Response`/`Tool`），渲染时按 part 分流：`Steps.Where(s => s.GroupPart != Response)` 进组，`Response` 留在外面。这是**纯渲染层改动**，但从此不需要"思考自动收起"的启发式。

#### (4) 四档显隐：做成一个配置对象

照 DSH 的四个字段来（`CHAT:12662-12691`），别散落成 `if`：

```csharp
public sealed record ProcessDisplayPolicy(
    bool FoldCompletedTurns,          // 完成态轮次是否收起过程组
    StepGrouping Grouping,            // Collapsed | History | None
    bool LiveProcessDetail,           // 运行中是否显示细节
    bool SettledReasoningPreview,     // 结束态是否保留一行思考预览
    CollapseTiming CollapseTiming);   // Completion | NextInput
```

| 档 | FoldCompletedTurns | Grouping | LiveProcessDetail | SettledReasoningPreview |
|---|---|---|---|---|
| 简洁 | true | Collapsed | false | false |
| 标准 | true | Collapsed | true | true |
| 详细 | true | History | true | true |
| 完全展开 | **false** | None | false | true |

两条硬规则：**运行中 / 被中断 / 失败 / abort 的轮次永不折叠**（`CHAT:3644-3648`）；**手动开合按 `(turn, answerStep)` 记忆**，模式切换不清（`CHAT:3670-3673`）。我们已经有 `_manualReasoning`（`ChatRoom.razor:935-945`），把键从 `messageId` 换成 `(messageId, stepIndex)` 即可。

`CollapseTiming = NextInput` 是个便宜但体感很好的选项：回答完不立刻收起，等下一条输入才收——用户"再看一眼刚想了什么"的诉求就解决了。

#### (5) 计时与运行行

**不要照抄 DSH 的"每行计时"——它没有。** DSH 的时长和 token 都在 **Turn 级**：运行中 `Deep diving for {duration}`（`CHAT:5611`），结束后在 turn footer；用量行里单列 `({tokens} reasoning)`（`CHAT:6902`）。

所以这块按 DSH 的边界做：

- **Turn 级**：`stepStartTime / firstTokenTime / completedTime` 三元组（`dsh-client-ui-chat` 的 Turn 计时、`trajectory/lib/client.js:1484-1488`），派生出 TTFT、总时长、tok/s——全部在 ViewModel 算。
- **进行中的行不要伪造时长**（DSH 明确只画 start marker）。我们只显示 `思考中…` 的脉冲（已有，`ChatTimeline.razor:17-29`）和"已产生 N 字"。
- 组头显示 `N 步 · M 次工具 · 失败 K · 12.4s`，这是**统计值不是计时器**，可以在结束态显示。
- 如果确实想给每行加耗时（我们的宿主知道工具何时开始/结束，成本很低），可以做，但要标明这是**我们的加法**：DSH 没有，别拿它当"对齐 DSH"。
- 顺带一句：我们现在折叠行上的"思考 · N 字"是**字数**（`ChatTimeline.razor:25`），不是 token。保留没问题，但不要和 P0-(8) 的 usage 混在一行，否则会被误读成"这次思考花了 N token"。

#### (6) 步骤持久化（治脆）

现在重开会话靠 `ReasoningChars/TextChars` 反推（`ChatTimeline.cs:16-48`），任何旧记录缺字段就整段退化成"思考块 + 工具列表"。改成存结构化步骤：

- `ChatMessage` 增加 `StepsJson`（`ChatStore.cs:296` 旁边加一列，沿用迁移惯例）；
- `StepsFor` 优先读 `StepsJson`，没有才回退 `FromTrace`（保留兼容，不回填历史）；
- 每个 Tool 步骤存 **`CallId`** 而不是数组下标——下标在"运行中条目被原地替换"时是稳定的，但在"压缩/裁剪/重排"后并不稳定。

对应 DSH 的规矩：**有内容的中断也要落一条完整消息**（带 `interrupted: true`），**零内容的失败只落"尝试"记录、不留半截推理**（`dsh-agent-loop/lib/index.js:1098-1135`）。我们现在 `CancelRunningTools` 只处理轨迹（`AgentChatService.cs:234-236`），消息体的中断标记值得补。

#### (7)（可选）思考正文的可替换渲染

DSH 把"思考正文"做成单值 slot（`conversation.chat.reasoning.body`），翻译插件就是挂在这里：只翻**已完成段落**、未完成尾部保留原文、串行单飞 + 按源文本缓存（`dsh-experimental-client-ui-cot-translation/lib/client.js:397-508`）。

我们这边对应的位置是 `ChatTimeline.razor:34` 的 `<pre class="reasoning-body">`。如果以后要做"翻译思考"或"折叠长思考摘要"，**先把它抽成一个组件参数**（`RenderFragment<string> ReasoningBody`），别等到要改三处代码时再重构。

#### (8) 每轮用量页脚

照 DSH：**只在数据完整时显示**（`dsh-client-ui-chat/README.zh.md:67`）。我们目前只累计 `InputTokens/OutputTokens`（`AgentChatService.cs:180-182`）。DSH 的 usage 还带 `cacheReadTokens/cacheWriteTokens/reasoningTokens`（`dsh-llm-deepseek/lib/index.js:1832-1844`、`trajectory/lib/client.js:1344-1351`）。如果要在页脚显示"缓存命中率"，前提是先拿到 `UsageDetails.CachedInputTokenCount`（当前全库没有引用，grep 为空）——**这是 P2 的前置测量项，不是 P0 的必需项**。

#### 验收（P0）

- 30 个工具、任意会话：工具行能显示"名称 + 参数摘要 + 状态 + 展开后的输入/输出"，且新增工具不需要改 UI（只注册 presenter，回落 generic）。
- **答案正文在过程组之外**：折叠过程组后正文仍完整可见（用 `ChatRoomPresentationTests` 断言 DOM 结构，而不是断言"文字出现一次"）。
- 四档模式切换后，用户手动展开过的行保持展开；`NextInput` 时机下，回答完成时过程组仍展开。
- 重开 20 条历史会话（含升级前的旧会话）：旧会话回退分组视图，新会话完整还原"思考→工具→思考"。
- 无新增 Core 依赖（`ArchitectureTests` 不破）。

### 3.2 P1：把"选工具"从宿主猜换成模型判（核心）

> 实施情况：§3.2.1 见 §10（P1a 已落地），§3.2.2–§3.2.5 见 §11（P1b 已落地）。

#### 3.2.1 先说实话：这一步的前提是拿回循环

DSH 那套"模型自己用 CoT 选工具"能成立，靠的是**模型看得到完整决策依据**：真工具结果在历史里、工具描述带策略、曝光面稳定。我们现在三样都缺，而且缺的根因是同一个：

`FunctionInvokingChatClient` 在内部循环里消化了 `tool/call` → 执行 → `tool/result`，我们只在外层看到**流事件**。后果：工具调用历史的真实结构丢了，只能压成 `[工具轨迹]` 文本行（`AgentMessageMapper.cs:70-72`）；step 边界丢了，所以只能在 UI 侧按到达顺序拼时间线；曝光面在请求发出前就固定了（`AgentRuntime.cs:9-20`），所以"模型想用一个没暴露的工具"永远无法在**本轮**解决。

所以 P1 的第一件事是：**写一个自己的 `AgentLoop` 替换 FICC**（保留 `Decorate` 装饰链的位置语义，`ContextBudgetChatClient` 仍需看到"每一次"请求）。

伪代码（MEAI 原语足够，不需要换框架）：

```csharp
var messages = new List<ChatMessage>(history);
for (var step = 0; step < maxSteps; step++)
{
    yield return new AgentStreamEvent.StepStarted(step, DateTimeOffset.Now);

    var calls = new List<FunctionCallContent>();
    await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
        foreach (var c in update.Contents)
            switch (c)
            {
                case UsageContent u:      yield return new AgentStreamEvent.Usage(…); break;
                case TextReasoningContent r: yield return new AgentStreamEvent.ReasoningDelta(step, r.Text); break;
                case TextContent t when c is not TextReasoningContent:
                                          yield return new AgentStreamEvent.TextDelta(step, t.Text); break;
                case FunctionCallContent f: calls.Add(f); yield return new AgentStreamEvent.ToolCallStarted(step, f); break;
            }

    if (calls.Count == 0) break;

    // 关键：真消息入历史，而不是压缩成文字
    messages.Add(new ChatMessage(ChatRole.Assistant, calls.Cast<AIContent>().ToList()));
    foreach (var call in calls)
    {
        var result = await ExecuteAsync(call, ct);           // 复用现有 InstrumentedAIFunction
        messages.Add(new ChatMessage(ChatRole.Tool, new AIContent[] { new FunctionResultContent(call.CallId, result) }));
        yield return new AgentStreamEvent.ToolCompleted(step, call.CallId, result);
    }
    yield return new AgentStreamEvent.StepCompleted(step, DateTimeOffset.Now);
}
```

这一步同时拿到四样东西：step 编号、每步用量、真实工具历史、**本轮内可以换工具集**（在 `ExecuteAsync` 里返回"该工具未暴露"时可决定是否补一次请求）。

> 风险提示：`ContextBudgetChatClient` 现在是"FICC 内侧装饰器"，靠 FICC 每轮回调生效。自建 loop 后它依然可以用，但要显式在每次 `GetStreamingResponseAsync` 前调用一次，并且**不能在循环中途改变它的语义**（否则会中途触发压缩，和 P2 的事务语义打架）。

#### 3.2.2 工具描述：从"功能说明"改成"决策规则"

我们已经有 `WhenToUse`（`CapabilityDescriptor`），但它只喂给**路由器**，不喂给模型。DSH 的做法相反：**同一句话写进 `description`，让模型读**。

改造示例（`CommandToolPack`）：

```diff
- "运行一个白名单内的可执行文件（不带路径），返回 stdout/stderr 与退出码。"
+ "运行白名单内的可执行文件（不带路径），返回 stdout/stderr 与退出码。"
+ "用于构建、测试、跑脚本。只看文件内容不要用它——用 read_file/search_files；"
+ "需要管道、重定向、环境变量或 PowerShell 语义时改用 run_powershell。"
+ "失败时先读输出里的第一处错误，不要原样重试同一条命令。"
```

这不是文风问题，是**把宿主原本藏在 `CapabilityRouter` 关键词表里的先验，搬到模型能推理的位置**。关键词表可以留着保底，但它不再是唯一的知识来源。

#### 3.2.3 曝光面：从"每轮关键词裁"改成"按任务档稳定"

现在的路由是 `CapabilityRouter` 按当前输入命中意图 → 补包 → 按 Order 填满名额（`CapabilityRouter.cs:107-176`）。副作用是**工具集在相邻两轮之间跳变**，这在 DSH 的度量里是双重损失：模型要重新规划，提示词前缀也没法复用。

建议改成两级：

| 级别 | 决定时机 | 内容 | 例子 |
|---|---|---|---|
| 任务档（profile） | **进入任务时**（首轮或用户换话题） | 核心集 + 该任务档的固定包集 | "编码/排障" = 读 + 搜 + 编辑 + 命令 + todo |
| 轮内微调 | 每轮 | 只增不减（`search_capabilities` 命中后补），并受 `MaxExposedTools` 约束 | 命中 pdf 才补 `*_pdf` |

也就是说：**允许"这一轮多一个工具"，不允许"这一轮少一半工具"**。`TurnClassifier`（Chat/Answer/Task，`TurnClassifier.cs:54-81`）保留——它解决的是"要不要给工具"，这个判定我们比 DSH 更需要（8K + 小模型）。

#### 3.2.4 同轮补工具：把 `search_capabilities` 从"提示"变成"闭环"

现在兜底命中未暴露工具时，只回一句"下一轮重新路由"（`AgentToolRegistry.cs:154`）。DSH 不需要这个机制，因为它不裁工具；但我们要裁，就必须让"模型自查"有结果。

建议（有界、最多一次）：

```
模型调用 search_capabilities("pdf")
  → 命中 pdf_pack
  → 宿主：本轮内追加这些工具，重新发一次请求（不新增用户轮次）
  → 同时写入一条 SystemNotice 步骤「本轮补充工具：read_pdf」
```

展示上用 P0 的 `ChatStepKind.Retry` 行表达，用户看得见"为什么突然多了一个工具"。这正是"推理选工具"的闭环：**模型的推理产生了动作，宿主没有装作没听见。**

#### 3.2.5 删掉 PLAN 强制

`SystemPrompts.cs:14` 要求"先用一行 PLAN 说明下一步，再调用一个工具"。这是**在没有推理通道时的替代品**——把 CoT 挤到正文里。DSH 没有这条：它的默认系统提示词里没有 plan 指令，`/plan` 是**独立的模式**，只额外注册一段 `plan:policy` 提示词和一个 `exit_plan_mode` 工具，**工具面一点都不变**（`dsh-plan-mode/lib/index.js:170-177,230-237`）。

按模型能力分叉：

```csharp
public static string Full(bool supportsReasoning) => supportsReasoning
    ? FullWithReasoning      // 不要求 PLAN；要求"先在思考里定步骤，再动手"
    : FullWithPlanLine;      // 现状
```

判断依据已经有了：`ModelProfile.SupportsReasoning`（`ModelProfile.cs:13`）。收益：正文干净、工具行的触发原因进思考区、不再出现"PLAN 了但没调工具"的半截状态。

#### 验收（P1）

- 用一个 8K 模型 + 30 个工具跑现有 `tools/AgentEval`：完成率不比 M4 差，且**每轮平均工具数下降**（说明模型在选，不是宿主在填）。
- 新增"工具历史保真"测试：第二轮请求里能断言 `ChatRole.Tool` 消息存在且 `CallId` 配对。
- `search_capabilities` 命中后，同一轮内出现补充请求（可用假端点断言请求次数）。
- 路由评测集（`eval/routing/cases.json`）仍 100% 命中——因为规则还在，只是不再是唯一路径。

### 3.3 P2：上下文机制，只搬三件

#### 3.3.1 上下文段：注册式内容 + 尾部快照（把"状态包"拆开）

> 实施情况：✅ 已落地，见 §13（P2a）。

现状有两个独立问题，值得一起解决：

**(问题一：内容）** `StatePackFormatter` 是唯一的 god-renderer，四个 `##` 段（工作区 / 最近动作 / 当前计划 / 验证）硬编码在 182 行里（`StatePackFormatter.cs:61-144`），截断顺序也是写死的（树 → 改动 → 动作，验证永不截断）。后果：加一个上下文事实（沙箱模式、审批策略、相对时间）就要改这个文件；每段没有独立上限、没有独立开关、`ContextBreakdown` 里也只有 `StatePack` 一个总数（`ContextReport.cs:41-42,94-98`）。

**(问题二：位置）** `finalInstructions = instructions + injection.Text`（`ContextBuilder.cs:71-74`），状态包拼在系统提示词**尾部**。系统提示词每个 turn 都变，端点若有前缀缓存也从第一个 token 全失效。

DSH 把这两件事分别解决了（§1.8 + §1.4-(b)）。我们照它的形状做，但**落地方式更省**——因为我们的历史是"每请求重建"的，不需要像 DSH 那样把快照持久化进日志再追加。

##### (1) 内容：`IAgentContextSection` 注册表

```csharp
public interface IAgentContextSection
{
    string Name { get; }        // "policy" / "workspace" / "changes" / "verification" / "plan" / "time"
    int Order { get; }          // 中央常量表分配，段之间不抢
    bool Required { get; }      // 预算再紧、模式再小也不许丢
    int MaxTokens { get; }      // 单段上限（段内自带截断策略）
    string? Render(ContextSectionInput input);   // null / 空白 => 整段消失，成本 0
}
```

`ContextBuilder.BuildInjection` 改成：**渲染全部 → 丢空段 → required 先按 Order 占预算 → 可选段按 Order 填剩余 → 每段按 `MaxTokens` 截断 → 记录每段用量**。段顺序用 `Order` 再按 `Name` 兜底（DSH 的 `comparePromptSections`，`dsh-system-prompt/lib/index.js:101-103`），保证**同样输入得到同样字节**——这条是 golden test 和提示词缓存的前提。

> 这份段表是**唯一事实源**：提示词注入、`ContextReport`、界面分解面板都读它。不要为任何展示再写一套分类/估算，否则面板和预算会对不上。

初始段表（对应现在的状态包 + 两个新增）：

| Order | Name | Required | 现在有没有 | 内容 |
|---|---|---|---|---|
| 100 | `workspace` | 否 | ✅（`## 工作区`） | 根目录 + 深度 2 结构；**加一句"树是快照，可能过期，用工具核对"** |
| 110 | **`policy`** | **是** | ❌ **缺** | 当前模式、沙箱模式、审批策略、是否允许写入真实工作区、命令白名单摘要 |
| 120 | `changes` | 否 | ✅（`## 最近改动`） | 最近改动文件 + **相对时间** |
| 130 | `verification` | **是** | ✅（`## 验证`） | 最近构建/测试结果（现在的"永不截断"改成 required 表达） |
| 140 | `plan` | 否 | ✅（`## 当前计划`，独立 200 token） | 保持现状 |
| 150 | `time` | 否 | ❌ **缺** | 当前时间（带 IANA 时区与偏移）+ 距上一条模型可见消息的经过时长 |

两处新增值得单独说：

- **`policy` 是当前最大的内容缺口**。`SystemPrompts` 里没有任何"当前策略"的描述；`AgentInstructions.Default`（`AgentInstructions.cs:9-19`）虽然写了沙箱与写回规则，但它是**死代码**（全库只有定义、无引用），而且是静态文案——用户切到直跑模式、改审批策略之后，模型完全不知道。DSH 把 `sandbox:policy`、`approval:policy` 作为**独立 required 段**（顺序 110 / 115），正是为了让"我现在能做什么"始终在场。这条不只是体验，是安全属性。
- **`time` 段照 `dsh-time-context` 的三行形态**，但**不做节流**：我们每轮只发一条（替换而非追加），成本恒定，节流只会在 8K 下省几十个 token 却让模型误判新鲜度。要用的是它另外三条：IANA 时区 + 偏移、**相对时长**（"距上一条消息 12 分钟"）、以及**缺失时的指令**（时区混杂就要求澄清）。我们现在只有 `HH:mm`（`StatePackFormatter.cs:106,128`）——没有日期、没有时区、没有相对量，模型无法判断"这个构建结果是 3 分钟前还是昨天"。

##### (2) 位置：末尾一条快照消息，替代 system 尾部拼接

```
[system]   稳定提示词（会话内不改）                     ← 可缓存前缀
[user]     …
[assistant] …
[user]     ## 宿主上下文快照（宿主生成，不是用户输入；本快照取代之前的快照）
           …按段渲染…
```

三条收益：前缀可缓存；压缩从"第一个非 system 节点"起时天然落在保留区（我们再给它打 `IsPinned`）；`ContextReport` 可以按段归因。

代价与注意（都是实话）：
- **多数 OpenAI 兼容端点不接受中途的 `system` 消息**，所以只能用 `user` + 明确前缀（现在的状态包已经这么写了，`ContextBuilder.cs:516`）。DeepSeek adapter 是把 `developer/message` 转成 user turn 之后的 system tool-update（`dsh-llm-deepseek/lib/index.js:1641-1687`）——各家实现不一，别假设统一。
- `ContextTrimmer` 按"user 轮分组"淘汰（`ContextTrimmer.cs:104-`），快照消息必须显式排除，否则会被当成最旧的一轮删掉。
- **DSH 的"只在变化时发"我们借不了**：它的快照留在日志里，不发也有；我们是每请求重建，不发就等于丢状态。我们对应的优化是"位置 + 稳定字节"，不是"少发"。
- **先测再改**：加 `CacheHitTokens`（`UsageDetails.CachedInputTokenCount`），改动前后各跑 20 轮同一会话。端点若不做前缀缓存，这条改造只是"结构更干净"，不要当性能项目汇报。

##### (3) 验收（并入 P2a）

- **required 段永不消失**：把可选段全部关掉 + 预算压到 200 token，`policy` 与 `verification` 仍在，且 `ContextReport` 标记它们 `Truncated=false`。
- **字节稳定**：同一会话连续 5 轮输入不变，快照段渲染结果逐字节相同（golden test）。
- **归因可见**：`ContextBreakdown` 能列出每段名字与 token，而不是只有一个 `StatePack` 总数。
- **单段可关**：关掉 `time` / `plan` 后该段成本严格为 0（不是渲染空标题）。
- **切换策略会被看见**：改成直跑 / 只读后，下一轮 `policy` 段的文本随之变化（断言 golden 文本）。

#### 3.3.2 压缩：补三个性质，不重写

> 实施情况：✅ 已落地，见 §14（P2b）。

我们已经有 `ContextCompactor`（保留最近 N 轮、pin 保护、防抖、失败退化为抽取式、落库回滚）。缺的是 DSH 的三个性质：

| 性质 | DSH 做法 | 我们的补丁 |
|---|---|---|
| **先修剪，后摘要** | pruner 不调模型；修完够用就整次跳过摘要 | 把"工具轨迹按预算回放"（`ContextPolicy.cs:31`）提前到压缩判定之前，作为第一档手段 |
| **摘要请求复用热前缀** | 逐字回放 system + tools + 被遮蔽区域 | 摘要请求带上**同一份** instructions + 同一组工具声明，而不是只喂消息 |
| **不缩小的摘要拒绝；事务化** | `compaction/start` … `compaction/summary` … `end`，未匹配 start = busy | 在 `ChatSession` 加三个状态字段（`CompactingStartedAt/FinishedAt/Reason`），落库后即可恢复；摘要不缩小输入就丢弃 |

第三条尤其重要：我们现在"压缩失败回滚内存"（`ContextCompactor.cs:169-200`），但**进程崩溃时没有痕迹**，用户只会看到会话突然变短或重复压缩。有 start/summary 标记就能诊断。

#### 3.3.3 大工具输出落盘

> 实施情况：✅ 已落地，见 §15（P2c）。

现状是截断（头 1200 / 尾 300 + 省略说明，`ContextBudgetChatClient.cs:74-75`）。问题是**模型再想看也没了**，只能让工具重跑一遍——对 `run_command` 是重跑构建，代价很高。

DSH 的做法：每个工具结果被接纳时就判一次（阈值 `maxInlineTokens`，默认 12500 token），超过就把全文落盘，上下文里换成"头/尾预览 + 路径 + 取回指引"（`dsh-spill-policy/lib/index.js:24-27,179-235`）。`read` 工具的输出显式跳过，避免自己 spill 自己（`:239`）。我们有现成的两件配套：`read_file` 的 `offset/limit` 分页，和沙箱工作区复制。

建议：

```
输出 > MidTurnToolResultChars 时：
  1. 写入 <workspace>/.agent/spill/<callId>.log（沙箱内可见，随工作区移动）
  2. 上下文里给：
     [输出已截断] 全文 <n> 字符，已保存到 .agent/spill/<callId>.log
     前 1200 字符：…
     需要更多时用 read_file(".agent/spill/<callId>.log", offset: 1200, limit: 4000)
```

失败要尽力而为：写盘失败就退回纯截断，并把这件事记进 `ToolTraceEntry`（`Summary` 里加一句"未落盘"）。

#### 验收（P2）

- 构造 200K 字符的 `run_command` 输出：模型能在一个后续轮次里读回中间片段，不需要重跑命令。
- 快照消息改造前后各 20 轮：缓存命中率（若端点上报）不下降，且 `ContextReport` 的 `StatePack` 段仍能单独计量。
- 压缩中断（杀进程）后重开：能看到未完成的压缩标记，不会重复压同一段。

### 3.4 P0.5：输入框的 `@文件` 与 `/技能`（独立于上面三层）

现状：**完全没有**。`ChatComposer` 是一个 `<textarea>` + `@oninput`，提交时只带文本与附件：

```csharp
var submission = new ComposerSubmission(Text.Trim(), _attachments.ToList());   // ChatComposer.razor:556
```

全库没有 `@` mention、没有 `/` 命令解析（grep 无命中）。这块和推理/上下文三条线都不耦合，可以独立排，收益直接可见。

#### 3.4.1 `@` 文件引用（1.5–2 天）

**候选源**：复用 `WorkspaceSearch`（`Tools.Files`，已有）+ `StatePackFormatter.DefaultExcludes` 的排除表；加一层 30 秒缓存，避免每次击键都扫盘。

**触发与插入**：按 caret 位置在 C# 侧判定（行首或空白后的 `@`），语法与 DSH 对齐：

| 场景 | 结果 |
|---|---|
| `@src/` | 目录候选；选中后**保留引号/保持菜单打开**继续下钻 |
| `@Program.cs` | 插入 `@Program.cs` |
| `@"my file.cs"` | 带空格路径；格式化器拒绝含控制字符/内嵌引号 |
| `foo@bar.com` | 不触发（词内 `@`） |

**三个硬决定**（照 DSH，理由见 §1.9）：

1. **只放路径，不读文件内容**。8K 下一条长文件就是灾难；而且"引用 ≠ 看过"，模型仍须 `read_file`——这和我们"改代码前必须 read_file"的规则天然一致。
2. **插入的是纯文本**，chip 只是渲染。手打 `@foo.cs` 与从菜单选完全等价，历史回放不需要任何协议改动。`ComposerSubmission` 最多加一个只给 UI 用的 `References` 字段（高亮/校验/点击预览），**不参与模型输入**。
3. **两套键盘动词**：Enter/点行 = 选中；Tab/chevron = 下钻。少一个就变成"选目录只能选到根"。

**实现注意**：Blazor 的 `<textarea>` 拿不到 caret，需要 3 个 JS 互操作（`getSelection` / `setSelection` / `focus`）。**不要为了这个上富文本编辑器**——DSH 自己也是"纯文本 + 客户端扫描"，它的 README 明说"文本是唯一依据、没有 occurrence 身份"（`dsh-client-ui-skill/README.zh.md:105`）。

**顺带**：加一个可选上下文段 `file-reference`（`Required=false`，走 §3.3.1 的注册表）：

> 用户用 `@` 引用的文件只是路径提示；未调用 `read_file` 前不要声称读过它。

#### 3.4.2 `/` 命令（1–1.5 天）

```csharp
public interface IComposerCommand
{
    string Name { get; }              // "compact" / "new" / "model" / "index"
    string Description { get; }       // 菜单里的一行说明
    string? InputHint { get; }        // "/plan [message]" 这类提示
    bool AllowWhileRunning { get; }   // 忙碌时是否可用
    Task<CommandResult> ExecuteAsync(ComposerCommandContext context, CancellationToken ct);
}
```

放在 Core 层（不引用 UI），DI 注册；首版四条，全部转调已有能力：

| 命令 | 转调 | 忙碌时 |
|---|---|---|
| `/compact` | `AgentSessionRunner.CompactAsync(force: true)` | 不可用，提示"正在生成，稍后再压缩"（DSH 同款：`dsh-command-compact` 在轮次中报"暂不可用"） |
| `/new` | 新建会话 | 可用 |
| `/model` | 打开模型选择 | 可用 |
| `/index` | 重建工作区知识库索引（已有入口） | 可用 |

**严格语法 + 未知命令拒绝**：首字节 `/`、小写名（`_`/`-` 允许）、其余全是 `rawInput`；`/compcat` 这种未知命令**弹 toast，不发送**，绝不退化成用户提示词（`dsh-commands/README.zh.md:50`）。这条看似小事，实际是"用户以为自己在调命令、模型却收到一句奇怪的话"的事故源。

命令**不进模型**：执行结果在聊天区渲染成一行"命令"步骤（可复用 P0 的 `ChatStepKind`），token 成本为 0。在历史里也保留这一步，但 `AgentMessageMapper` 不渲染它（和 `HostPrompt` 同级的宿主事件）。

#### 3.4.3 `/` 技能（1–1.5 天）

**候选源**：`ISkillLibrary.Index()`（已有 `name` / `description` / `when-to-use` / `tags`，`SkillCatalog` 解析）。

**裁决链**（照 DSH 的顺序，很重要）：

```
用户输入 /translate
  → 先查命令表：有同名命令？ → 执行命令，结束（这行不进模型）
  → 再查技能目录：是用户可调用技能？
      是 → 字面文本照发 + 注入技能全文
      否 → 当作普通文本（不报错，不猜）
  → 都不是 → 兜底：当作普通文本
```

**注入通道已经有了**：`ContextBuildRequest.SkillName` / `SkillText` 就是"手选技能全文注入"（`ContextBuilder.cs:294-355`），`/translate` 直接复用，连新字段都不用加。要改两处：

1. **预算**。现在 Compact 下技能全文硬上限 600 token（`ContextBuilder.cs:307-311`）。**用户显式点名**时这个上限应该放宽到"固定块剩余额度"，并在截断时明确告诉用户"技能太长已截断"——DSH 是**无条件全量注入**（`dsh-client-ui-skill/README.zh.md:91`：成本与模型用工具加载相同，只是无条件支付）。用户既然点了名，就不该被静默截掉半截。
2. **防重复加载**。技能索引里要加一句（照 DSH 的目录模板）："用户显式注入的技能不要再用 `load_skill` 加载"。否则模型会把同一份正文加载两遍——我们现在的 `load_skill` 是分页的（`SkillToolPack`），重复加载会白烧 2000 字符。

**新增 `disable-model-invocation`**（技能 `frontmatter` 一个新布尔字段）：

- 从 `SkillIndexBuilder.Select` 的候选里剔除（模型看不到名字）；
- `load_skill` 拒绝它（错误信息："该技能只能由用户显式调用"）；
- 只有 `/name` 能进。

这条等于给技能加了一个**权限位**：重型/危险的技能（比如"重写整个文件"）不再出现在模型的候选里，只有用户主动点名才生效。这是 DSH 里我觉着最实用的一条边界设计。

#### 3.4.4 三个 source 共用一套触发器

一个 `InputTrigger.razor`（弹层 + 键盘导航 + 分组标题）+ 一个 `IInputTriggerSource` 接口：

```csharp
public interface IInputTriggerSource
{
    char Trigger { get; }                       // '@' 或 '/'
    bool TryMatch(string text, int caret, out TriggerQuery query);
    Task<IReadOnlyList<TriggerItem>> ListAsync(TriggerQuery query, CancellationToken ct);
}
```

排序统一：大小写不敏感的**有序子序列**匹配，前缀命中优先，同分保持宿主顺序（`dsh-client-ui-skill/README.zh.md:32`）。某个 source 抛错只丢自己那一组，菜单照常显示其它组。

**明确不做**：富文本 chip 编辑器（DSH 都用纯文本）；`@session` 引用（留 P3，我们的 `search_workspace_memory` 已有摘要能力可复用）；`@` 触发网络搜索。

#### 验收（P0.5）

- `@` 弹层在 200ms 内出结果（本地路径 + 30 秒缓存），目录可连续下钻，带空格路径正确加引号。
- 选中 `@` 后**请求 token 不增加**（只有路径文本）；模型在未 `read_file` 前不会声称读过。
- `/compcat` 不发请求、弹提示；`/compact` 不忙时压缩并回报"压缩了 N 条 / 省了 M token"。
- `/translate` 后模型收到技能全文，且**不再调用 `load_skill`**（断言请求里的工具调用）。
- `disable-model-invocation` 的技能不出现在索引与 `load_skill` 的可选范围里，但 `/name` 能加载。

## 4. 不迁移清单（以及为什么）

| DSH 的东西 | 为什么不搬 |
|---|---|
| 事件溯源会话日志 + projections | 我们是 EF Core + JSON 列，单机单进程。收益（确定性回放、多投影）抵不上重写存储的代价。但"步骤持久化"这一小块要搬（§3.1-(6)）。 |
| cordis 插件作用域/生命周期 | 我们的插件体系（`Plugins.Runtime`）目标不同（内容包而非能力注入），搬过来只会打架。 |
| PTC（`run_code` + 生成 SDK） | 需要 TypeScript 运行时与生成器；Windows 桌面端收益不明，且与沙箱模型冲突。 |
| `deferLoading` | 它**不删 schema**，只是给 provider 一个 `defer_loading: true` 提示（§1.2）。OpenAI 兼容端点不认这个字段，搬过来等于什么都没做。 |
| `developer/message` 增量声明工具 | 只有 `toolUpdate: in-history` 这类路由支持；我们的兼容端点没有等价物。用"本轮补齐 + 重发一次"更稳（§3.2.4）。 |
| 全量工具暴露 | 8K 窗口 + 小模型下会直接爆窗。保留我们的上限与闸门。 |
| 精确每轮 token 记账 | 依赖"每次尝试都有完整 usage"；我们的兼容端点经常缺字段。可以做，但必须照 DSH 的规矩：**不完整就不显示**。 |
| 四档模式的"完全展开"作为默认 | DSH 默认标准档；我们的用户更关心结果，别一上来就刷屏。 |
| 社区插件 `dsh-context` | **纯观测，零优化**（见 §4.1）。评估后不采用。 |

### 4.1 评估记录：`dsh-context` 为什么没有进计划

仓库：`git@github.com:bowenliang123/dsh-context.git`，v0.75.1，已完整读过（host + client + shared），代码在 `.tmp/dsh-context`（可丢弃）。

**结论：它是一台上下文仪表，不含任何上下文优化。** 证据：

- **唯一的写路径**是 `src/host/promptInjection.ts`：注册两个 `systemPrompt.section`，把用户配的 prefix/suffix 拼进系统提示词**最前/最后**。这是"加内容"不是"减内容"，而我们已经能用 `ContextBuildRequest.InstructionsOverride` 做到。
- **host 入口只注册只读投影**：`src/host/index.ts:30-59` 全部是 `sessionProjections.register(...)`（timeline / headers / activity）以及 detail / attachment / balance / fleet / skills 的**读取通道**；没有 `ctx.tools.*`，也没有 `ctx.commands.*`。
- **全仓没有任何变更动词**：`compact(` / `prune(` / `restrict(` / `evict(` / `dropMessage` / `deleteMessage` 全部零命中。它不压缩、不裁剪、不禁用工具、不重排消息、不做图片 offload/spill。
- 它读的是 harness 官方投影（`contextPressure` / `contextBreakdown`），**自身不产生任何上下文**。
- `/context` 只是打开一个弹窗（客户端命令），不是上下文动作。

**唯一值得记住的一条纪律**（不是计划项）：它拿官方总量当锚、估算只做比例分配（`src/client/categories.ts` 的 `officialParts` / `anchoredParts`），估算常量逐字镜像 harness 计量（`src/shared/estimate.ts`：4 字符/token + 固定开销）。**如果**将来我们要做分解面板，必须守这条：不与 `ContextReport` 分叉、不造第二个总数。现在不做。

## 5. 落地顺序与工时

| 阶段 | 内容 | 工时 | 依赖 | 风险 | 状态 |
|---|---|---|---|---|---|
| **P0** | 步骤模型（含 `GroupPart`）+ 过程组 + 工具行/卡片 + 四档配置对象 + `StepsJson` | 3–4 天 | 无 | 低（纯 UI + 存储列） | ✅ §8 |
| **P0.5** | 输入框 `@文件`（1.5–2d）+ `/` 命令（1–1.5d）+ `/` 技能（1–1.5d，含 `disable-model-invocation`） | 4–5 天 | 无 | 低（新功能，不碰引擎） | ✅ §9 |
| **P1a** | 自建 `AgentLoop` 替换 FICC，真工具消息入历史，step 事件 | 3–4 天 | 无 | **中**：FICC 现有行为（重试、非法参数修复、并行）要一一补齐 | ✅ §10 |
| **P1b** | 工具描述改造 + 曝光面按任务档稳定 + 同轮补齐 + 删 PLAN | 2 天 | P1a | 中：需要评测集重跑 | ✅ §11 |
| **P2a** | 上下文段注册表（含新增 `policy` / `time` 段）+ 尾部快照消息 + `CacheHitTokens` 测量 | 2–3 天 | 无 | 低 | ✅ §13 |
| **P2b** | 压缩补三性质 | 1–2 天 | P2a | 中：涉及会话状态迁移 | ✅ §14 |
| **P2c** | 大输出落盘 | 1 天 | 无 | 低 | ✅ §15 |

建议排期：**P0 → P0.5 → P2a → P1a → P1b → P2b → P2c**。先把"看得见的推理"做好，再补输入框，然后动上下文，最后才动循环（要评测兜底）。

## 6. 风险与反向意见

1. **"拿回循环"是最大的单点风险。** `FunctionInvokingChatClient` 帮我们处理了：并行工具调用、参数反序列化失败修复（我们有 `ToolCallRepair`）、结果回填顺序、`MaximumIterationsPerRequest` 上限。自建 loop 必须逐条补齐，否则会出现"以前能跑现在卡住"的回归。**建议 P1a 先做一个开关**（`AgentSettings.NativeLoop`），新旧两条路径都能跑，用 `tools/AgentEval` 对比后再切换默认。
2. **不要为了像 DSH 而放弃 8K 场景。** DSH 不路由是因为它有 128K 和强模型。我们的 `TurnClassifier` + 上限 + 技能索引是**正确的差异化**，不该删。要改的只是"每轮按关键词换牌"这一层。
3. **展示层最容易过度设计。** DSH 有四档、shimmer、鲸鱼、轮次轨道、滚动跟随……那是一个成熟产品的累积。我们只做档位 + 组头 + 工具卡 + 计时，其余等有人抱怨再说。
4. **快照消息的收益取决于端点。** 如果端点根本不做前缀缓存（很多本地推理服务），这条改造只是"结构更干净"，不要拿它当性能项目汇报。
5. **工具描述改写要配评测。** 描述是模型行为的输入，改错了会静默降低成功率。每次改描述都跑 `eval/routing/cases.json` + `AgentEval`，把 diff 和分数一起提交。

## 7. 一页速查：DSH → 我们的对应关系

```
思考              reasoning-delta(index)     → AgentStreamEvent.Reasoning（已有，缺 index/step）
工具调用           tool/call event            → ToolTraceEntry + ChatStep(Tool)（已有，缺 StepIndex/CallId）
步骤边界           step/start … step/end      → ChatStep.StepIndex（P1a 已落地）
工具消息入历史      assistant/message + tool/result → ChatRole.Tool 消息（P1a 已落地）
工具行             tool.call.toolview 键控    → IToolViewRegistry + ToolPresentation（P0）
工具卡             client-derived props       → 同上，从 ArgsJson/结果现场算（P0）
过程组             group part reasoning/response → ChatGroupPart + ProcessGroup.razor（P0）
四档显隐           display policy 对象        → ProcessDisplayPolicy（P0）
工具描述           描述即决策规则（何时用/换谁）→ ToolPlaybooks 拼进 description（P1b）
曝光面             稳定（不随每轮关键词抖动） → ToolTaskProfile 任务档 + 只增不减（P1b）
同轮补工具         （不需要：不裁工具）        → search_capabilities 闭环：本轮补 ≤3 个并重发（P1b）
PLAN 强制          （没有：/plan 是独立模式）  → FullFor(SupportsReasoning) 按能力分叉（P1b）
上下文段           systemPrompt.context({name,order,required}) → IAgentContextSection 注册表（P2a）
运行时快照         user 消息 + source 归因    → 历史末尾的单条 snapshot 消息（P2a）
时间上下文         time-context 三行读数       → `time` 段（IANA 时区 + 相对时长 + 缺失指令）（P2a）
@ 文件引用          file-reference（只给路径）   → InputTrigger(`@`) + 纯文本 mention + `file-reference` 段（P0.5）
/ 命令             commands 注册表（UI 平面）   → IComposerCommand + 严格语法 + 未知拒绝（P0.5）
/ 技能             skill 手势 + 目录            → SkillName/SkillText 通道 + `disable-model-invocation`（P0.5）
压缩事务           compaction/start|summary|end → ChatSession 压缩状态字段（P2b）
先修剪后摘要        pruner 先跑，够用就跳过摘要   → 修剪档位排在「淘汰整轮」之前（P2b）
摘要复用热前缀      逐字回放 system+tools+区域    → SummaryChatRequest（instructions + tools + 真消息）（P2b）
大输出            spill（12500 token）+ pruner（8192 字符） → .agent/spill/*.log + read_file 分页（P2c；阈值用 MidTurnToolResultChars）
token 计量         ctx.tokenMeter              → ContextReport（已有，够用；补每段归因）
```

## 8. P0 实施记录（2026-10-24）

状态：✅ 已落地。WPF 解决方案 0 警告 0 错误；Core 717、UI 87、Sandbox 219 测试全绿。

### 落地形态

| 计划项 | 实现 |
|---|---|
| 步骤模型 | `ChatStep` 增加 `StepIndex` / `CallId` / `StartedAt` / `EndedAt`，并新增**计算属性** `Part`（`Text → Answer`，其余 `Process`）——不用给生产者加字段，也不用迁移存量 JSON |
| 步骤持久化 | `ChatMessage.Steps` → `messages.StepsJson`（新迁移 `20261024133546_AddMessageSteps`）；`StepsFor` 优先读结构化步骤，旧消息仍回退 `ChatTimelineBuilder.FromTrace`（顺带补上 `CallId`） |
| 过程组 | `ChatTimeline.razor` 拆成「过程组 + 组外答案」：过程组装思考 / 工具 / 宿主追问 / **过程正文**，只有最后一次工具之后的正文留在组外；组头显示 `过程 · 12.4 s` 与 `N 步 · M 次工具 · 失败 K`，收起时贴一行最新思考或过程正文 |
| 四档显隐 | `ProcessDisplayMode` + `ProcessDisplayPolicy`（四个开关 + 收起时机）落在 Contracts，`ChatPresentationSettings` 进 `AppSettings.Chat`；设置页「通用 → 对话」两个下拉 |
| 工具行/卡片 | `ToolPresentation`（客户端派生）+ `ChatStepRow.razor`：单行 `名称 · 状态 · 摘要 · 耗时`，点开看目标 / 参数 / 编辑行 / 结果预览；未知工具回落 generic |
| 工具轨迹补数据 | 记录轨迹时算好 `Title`（命令+前两参数 / 路径 / 模式）、`ArgsJson`（截断 512）、`DurationMs`、`ResultPreview`（前 200 字）；running 条目也带标题，工具一开跑就能看出在做什么 |
| 计时 | Turn 级：组头用 `ChatMessage.DurationMs`，工具行用 `ToolTraceEntry.DurationMs`。**没有做**逐行思考计时（DSH 也没有，见 §3.1-(5)） |

### 与计划的偏差（取舍）

1. **过程正文与答案分开**：工具调用之前说的正文（"我先看一下"）在工具一开始就被改判成 `ChatStepKind.Preamble`，跟着过程组一起收起；只有最后一次工具之后的正文是 `Text`，永远在组外。判定不是按字数或时间猜，而是「下一次工具调用」这个确定事件；重开会话时由 `ReasoningChars/TextChars` 边界得到同一结论。没有工具的一次生成，正文始终是答案。
2. **只要还有 running 的工具，这一轮就不收起**。DSH 用 `turn.status === open` 表达同一件事；我们的工具可能比流更晚结束，所以在 `CanFoldProcess` 里显式判断。
3. **`LiveProcessDetail` 暂未参与渲染**：运行中的轮次一律展开（用户要看着它跑）。字段保留，等有"简洁档也要运行细节"的需求再接。
4. **思考正文的可替换渲染（§3.1-(7)）没做**：`ChatStepRow` 已独立成组件，将来抽 `RenderFragment` 只改一处。
5. **每轮用量页脚（§3.1-(8)）没做**：依赖 `UsageDetails.CachedInputTokenCount`，属 P2a 的测量项。

### 测试

- 新增 `ProcessDisplayTests`（档位映射、Part、合并规则）、`AgentChatServicePresentationTests`（标题/参数/预览）、`ToolPresentationTests`（形态映射、状态与耗时文案、HasDetail）。
- `ChatRoomPresentationTests` 增 4 条：标准档合成一组且保留答案、点组头展开、详细档默认展开、切档保留手动展开；工具卡展开断言参数与结果。
- `ChatStoreContextTests` 增步骤落库往返；`ChatTimelineTests` 增 `CallId`/`Part` 断言。
- `ChatRoomStreamFailureTests` 覆盖「流中途失败」：错误提示必须落成一步，否则按步骤拼出来的答案会漏掉它。
- 另用 `SDEMO_UI_BASELINE=1` 渲染了一份临时 HTML 逐项核对过（过程组只含过程、答案在组外、工具卡能展开到参数/结果）；截图基线的 PNG 管线在本次 checkout 里缺 `tools/ui-baseline`，所以没有提交新的基线产物。

## 9. P0.5 实施记录（已完成：`@` 文件、`/` 命令、`/` 技能）

状态：三条管线全部落地。构建 0 警告 0 错误；Core 758、UI 102 测试全绿。

### 已落地

| 计划项 | 实现 |
|---|---|
| 触发语法 | `ComposerTriggers.Detect(text, caret)`：`@` 只认行首/空白之后，`/` 只认行首；`@"带 空格"` 允许空格直到闭合引号；`foo@bar` 与「和/或」不误触发 |
| 候选排序 | `ComposerMatch`：大小写不敏感的有序子序列，路径段前缀优先，其次匹配间隙、再按候选长度、最后保持宿主顺序 |
| 路径索引 | `WorkspaceReferenceIndex`：广度优先、深度 8、上限 4000 条、复用状态包的排除表（bin/obj/node_modules…）、30 秒缓存 + `Invalidate()` |
| 菜单交互 | `sdComposer` 接管菜单开合：`↑↓` 移动、`Enter`/`Tab` 确认、`Esc` 关闭；菜单打开时 Enter 不再发送 |
| `@` 插入 | 只插**纯文本** `@path` / `@"带 空格"`；目录补成 `@"dir/` 并保持菜单（下一次输入自动下钻）；文件插入后补一个空格并关闭 |
| **选中样式** | `ComposerTokens.Scan` 扫已落下的 token（`@` 引用、行首 `/` 命令），`ToHighlightHtml` 输出转义后的高亮层；透明镜像层画 token 底色（紫=引用、琥珀=命令），输入框文字保持可见，滚动由 `sdComposer.syncScroll` 同步 |
| `/` 命令 | `ComposerCommands`：严格语法（首字节 `/` + 小写名 + 空白/行尾）、未知命令拒绝；`/compact`（转 `CompactNowAsync`）、`/model`（打开模型设置）、`/index`（重建知识库）、`/help`；命令**不进模型**，执行后清空输入 |
| `/` 技能 | 同一 `/` 菜单：命令在前、技能在后，**同名时命令赢**；点选插入 `/name `，提交时 `ComposerSubmission.InvokedSkill` 带上技能名；手打 `/name` 同样识别（任何空白分隔处），正文由现有 `SkillName`/`SkillText` 通道全文注入 |
| 防重复加载 | 技能全文注入时提示词里加一句「不要再调用 load_skill 加载同名技能」 |
| 权限位 | 技能 frontmatter 新增 `disable-model-invocation`：不进索引、`load_skill` 拒绝、只能 `/name` 加载 |

### 与计划的偏差

1. **文本在 C# 里算、JS 只改 DOM**：`replaceRange` 不再返回新文本，避免界面/测试/回灌三条路径拿到不同结果。
2. **目录下钻靠「让 input 事件重新识别」**：插入 `@"dir/` 后不主动查询，交给下一次 `HandleInput` 重算，代码里只有一条识别路径。
3. **键盘仲裁放在 JS**：沿用现有 `sdComposer` 的 document 捕获监听（Enter 一直是 JS 管的），菜单只是多一种「吃掉按键」的状态，不引入第二套 keydown 监听。
4. **高亮层用「透明镜像 + 底色」而不是把文字也画在高亮层**：输入框自己画字，中文输入法的组合态才不会消失；token 只贡献背景与下划线，避免字形重影。
5. **`/技能` 的识别范围比 `/命令` 宽**：命令只认行首（避免「和/或」误触发），技能按 DSH 的手势规则认任意空白分隔处。
6. **`/new` 没做**：新建会话要新的宿主回调（AppShell 目前没把「新建」暴露给 ChatRoom），留到补回调时一起做。
7. 测试里用 bUnit 的 `JSInterop.Setup<int>("sdComposer.caretOf", invocation => true)`（带 matcher 的重载才匹配带参数的调用），并避开 `RefreshableElementCollection` 的索引器（bUnit 1.40 与仓库里 AngleSharp 1.8.5 不兼容，会抛 `MissingMethodException`）。

### 测试

- 契约：`ComposerTriggerTests`（语法/格式化）、`ComposerTokenTests`（扫描/高亮/技能识别/命令行解析）。
- Core：`ComposerReferenceTests`（枚举/排除/缓存/排序）、`SkillCatalogTests`（`disable-model-invocation` 解析）、`SkillIndexBuilderTests`（禁用技能不进索引）、`SkillToolPackTests`（`load_skill` 拒绝）。
- UI：`ChatComposerMentionTests`（`@` 弹菜单/方向键/下钻/关闭）、`ChatComposerCommandTests`（命令在前、执行不发送、技能插入、手打识别、未知命令转交宿主、高亮 token）、`ChatRoomCommandTests`（未知命令只提示、`/compact` 打到服务、`/help` 列表、无工作区时 `/index` 提示）。

### 修补（2026-10-24）：鼠标点选候选后键盘失效

现象：`@` / `/` 候选区用鼠标点选之后，Enter 不再发送、Tab 不再有反应、Esc 也关不掉菜单。

根因有两条，第一条是主因：

1. **`@for` 的循环变量被所有候选行共享**。`@onclick="() => CommitAsync(index)"` 捕获的是同一个 `index`，
   循环结束时它已经等于 `_items.Count`——**鼠标点任何一行都会越界**，`CommitAsync` 直接 return，
   菜单既不插入也不关闭，行还在原地并拿到了焦点。
2. keydown 监听的第二道判断是 `event.target.closest('.composer-text')`。鼠标点过按钮后焦点在按钮上，
   于是 ↑↓ / Enter / Tab / Esc 全被挡掉，菜单再也关不掉（而且 JS 的 `_menuOpen` 还是 true）。

修法（三层一起，任何一层单独都够挡住复现）：

- 每轮拷一份 `var itemIndex = index;` 再进 lambda（`ChatComposer.razor`）。
- 候选行加 `tabindex="-1"` 与 `@onmousedown:preventDefault`：鼠标按下不转移焦点，Tab 也不会落到行上。
- `sdComposer` 的 keydown 改为**菜单打开时按键归菜单**（只接管「输入框内 / 焦点已丢失」两种情形，
  不抢别的输入控件），不再要求焦点一定在 textarea 里。
- 菜单关闭后用 `sdComposer.focusInput` 把焦点还给输入框（已经在输入框时不动光标）。
- C# 侧留一份 `_menuOpen` 镜像，`CloseMenuAsync` / 切换会话时与 JS 对齐，避免「看不见的菜单吃掉按键」。
- `CommitAsync` 越界时顺手关菜单自愈，而不是静默 return。

测试：`ChatComposerMentionTests` 新增「鼠标点选后插入路径、关菜单、还焦点」「候选行不进 Tab 顺序」
「没有候选时确认只复位不发送」「鼠标移上去高亮那一行」四条。

### 修补（2026-10-24）：确认候选后文本被插了两遍

现象：`@` 菜单里按 **Tab / Enter** 确认（鼠标点选同样）之后，输入框里出现两份文件名——
高亮 token 之后多出一截 `物业交割单.docx `。

根因是文本有两条写入路径：C# 先改 `Text` 并触发 `TextChanged`，Blazor 的重渲染**已经把
`<textarea>` 的值换成了新文本**；紧接着 JS 的 `sdComposer.replaceRange` 又在 `[start, end)`
上做了一次 `setRangeText`，于是插入内容被叠到已更新的文本上（它想替换的 `@` 已经不在原位）。
bUnit 里 `TextChanged` 没人接、组件不重渲染，所以测试一直是绿的，真机上才复现。

修法（三条一起，任何一条单独都够）：

- **JS 不再按区间插入**：只保留 `sdComposer.applyText(element, value, caret)`——文本由 C#
  算好后原样落到 DOM，顺带摆光标。文本永远只有一份来源，插几次都不会叠。
- **光标在渲染之后才摆**：`OnAfterRenderAsync` 里消费 `_pendingCaret`。另外位置要在
  `StateHasChanged()` **之前**存进局部变量——这次渲染可能同步执行，`OnAfterRender` 会立刻把
  `_pendingCaret` 清掉（目录下钻一开始就是栽在这里：拿回的光标是 -1，识别不出下一层）。
- **目录下钻不再依赖 JS 补发的 `input` 事件**：插入后直接 `RefreshMenuAsync(caret)` 算下一层，
  否则去掉 input 事件之后菜单要等用户再敲一个字才刷新。

测试：`ChatComposerMentionTests` 新增「确认候选只插入一次文本_光标落在插入之后」（断言最终文本、
`TextChanged` 的回灌值、以及不存在第二条按区间插入的互操作），并把「选目录后菜单保持打开继续下钻」
改成断言下一层候选**立刻**出现。

## 10. P1a 实施记录（内置工具循环）

状态：✅ 已落地，**默认关闭**（`AgentSettings.NativeLoop`）。WPF 解决方案 0 警告 0 错误（Debug / Release 均通过）；Core 768、UI 106 测试全绿。

### 落地形态

| 计划项 | 实现 |
|---|---|
| 自建循环 | 新增 `Agents/NativeAgentLoop.cs`；`MafAgentRuntime` 按 `AgentInvocation.NativeLoop` 二选一，两条路径共用同一条客户端链（`OpenAI → Decorate`）与同一份 `ChatOptions` |
| 真工具消息入历史 | 每步结束后把整条 assistant 消息（正文 + `tool_calls`）与逐条 `ChatRole.Tool` 消息写进本次调用的消息表；下一步原样回放，不再压成 `[工具轨迹]` 文本 |
| step 事件 | `AgentStreamEvent.StepStarted(step, maxSteps)` / `StepCompleted(step)`；`FunctionCall` / `FunctionResult` 带上 `Step` |
| 每步用量 | 每次请求的 `UsageContent` 各自透出（FICC 里也是每次都报，这里语义一致） |
| 收尾 | 最后一次请求摘掉工具（`ChatOptions.Clone()` 后 `Tools=null` + `ToolMode=None`），模型必须把已有结果写成正文——照抄 FICC 的 `MaximumIterationsPerRequest` 行为 |
| 工具失败 | 未知工具 / 异常都回「结果文本」而不是抛异常；未知工具带 `[TOOL_STATUS:error]`，模型下一轮能看见原因 |
| 接上界面 | `AgentDelta.Step`（-1 = 运行时看不到 step）→ `ChatRoom` 用 `stepBase` 偏移成整条回合的序号，`ChatStep.StepIndex` 第一次真正表示「第几次模型请求」 |
| 开关 | `AgentSettings.NativeLoop` + 设置页「工具预算与路由 → 内置工具循环（P1a，实验）」；默认关 |

### 与计划的偏差（取舍）

1. **`FunctionCall` 事件比 FICC 晚一点点**：流式 delta 里的工具参数是分片到达的，逐块解析只能拿到半截 JSON，
   所以工具调用在流结束后从 `updates.ToChatResponse()` 整条取（FICC 的流式实现也是这么做的）。执行紧随其后，用户无感。
2. **串行执行，不做并发**：FICC 的 `AllowConcurrentInvocation` 默认就是 false，并发写文件本来也是风险。
3. **默认关闭**：按 §6.1，先做一个开关、新旧都能跑，用 `tools/AgentEval` 对比后再翻默认。本机没有可用模型端点，
   所以这一步留给评测。
4. **`ContextBudgetChatClient` 仍在循环内侧**：每次 `GetStreamingResponseAsync` 前都会裁剪一次，语义与 FICC 时期一致，
   循环中途不触发压缩（那是 P2b 的事务语义）。
5. **reasoning 的回放交给 provider**：assistant 消息里的 reasoning 照 FICC 一样进历史，OpenAI 兼容适配器只序列化
   `content` / `tool_calls`，所以实测不会把思维链重复发回端点。

### 测试

- `NativeAgentLoopTests`（假 SSE 端点）：工具调用/结果作为真实消息回灌（断言第二次请求体里有 `tool_calls`、`role:tool`、
  `tool_call_id` 与工具输出）、一次响应里的两个工具按顺序配对、最后一轮不再声明工具、未知工具只回错误结果、
  参数不是合法 JSON 时不炸整轮、没有工具调用时只有一步、没有声明工具时幻觉调用只回一次错误、上下文裁剪装饰器每次请求都运行。
- `AgentChatServiceToolExposureTests`：step 边界跟着增量透传（0 → 1）、设置打开时 `NativeLoop` 进到 `AgentInvocation`。
- 原来的假端点抽成 `FakeOpenAiServer`，FICC 与内置循环两条路径共用。

### 下一步（P1b 的前置）

- 跑 `tools/AgentEval` + `eval/routing/cases.json` 对比两条路径，再决定是否把 `NativeLoop` 改成默认。
- 循环里已经握着工具表：「本轮补工具」只需要在 `InvokeAsync` 命中「未暴露」时追加工具并重发一次请求，不需要等下一轮。

## 11. P1b 实施记录（工具描述 / 曝光面 / 同轮补齐 / 删 PLAN）

状态：✅ 已落地。WPF 解决方案 0 警告 0 错误（Debug / Release）；Core 784、UI 108、Sandbox 219（1 跳过）测试全绿；
`tools/routing-eval --mode rules` **28/28（100%）**。

### 落地形态

| 计划项 | 实现 |
|---|---|
| §3.2.2 工具描述带决策规则 | 新增 `Tools.Abstractions/ToolPlaybooks.cs`：一张「工具名 → 决策规则」表（20 个工具），`InstrumentedAIFunction.Description` = 功能说明 + 规则；幂等，嵌套包装不会重复追加 |
| §3.2.3 曝光面按任务档稳定 | 新增 `Contracts/Context/ToolTaskProfile.cs`（General / Read / Edit / Run / Shell / Document / Web / Memory / Plan）；`CapabilityRouter.ResolveProfile`（本句有任务信号用它，否则沿用最近一条用户输入）→ **Full = 核心集 + 任务档工具 + 本句意图工具 + 最近工具**；`RoutingDecision.Profile/ProfileInherited` 进报告与 Reason |
| 只增不减 | 任务档内工具集固定；`PreviousUserInputs` 从会话历史取最近 4 条（跳过 `[宿主提示]`），Runner 与 ChatService 两条路由路径都传 |
| §3.2.4 同轮补工具 | `search_capabilities` 执行时回调 `CapabilitySearchOutcome`（命中 + 未暴露）→ `AgentChatService` 在本轮内补最多 3 个、**只补一次**、受 schema 预算约束 → `AgentInvocation.ExpandTools` → `NativeAgentLoop` 扩充 `ChatOptions.Tools` 重发，并透出 `AgentStreamEvent.ToolsExpanded` |
| 补工具可见 | `AgentDelta.Notice` → `ChatRoom` 落成 `ChatStepKind.HostPrompt` 一步（过程组内，标签「宿主提示」）；`AgentRunMetrics.ToolExpansions` 记账 |
| §3.2.5 删 PLAN | `SystemPrompts.FullWithReasoning` / `FullWithPlanLine` + `FullFor(ModelProfile.SupportsReasoning)`；`ContextBuilder.SelectBasePrompt` 与 `IntentHint(Task)` 按能力分叉 |
| 评测 | `eval/routing/cases.json` 24 → 28 条（新增 4 条带 `history` 的随访 / 换话题用例）；`tools/routing-eval` 支持 `history` 字段 |

### 与计划的偏差（取舍）

1. **决策规则集中在一张表，而不是改 9 个工具包的 `description`**：成对关系（`read_file` ↔ `search_files`、
   `run_command` ↔ `run_powershell`、三种写文件工具）写在一处才不会互相矛盾；模型看到的仍是同一条 description，
   `MeasureToolSchema` 量到的也是拼好后的文本（预算不会漏算）。代价是规则与工具实现分处两个文件。
2. **「只增不减」靠任务档粘性实现，不靠记住上一轮曝光集合**：不需要给 Runner 加会话级状态；
   派生自消息历史 → 同一份历史必得同一份决策，可复现、可测试。换话题时任务档合法地换掉旧工具。
3. **补工具只补一次、最多 3 个**：无界补充等于「边跑边全量暴露」；`AgentSettings.NativeLoop` 关闭时（FICC）
   不传 `ExpandTools`，因为那时宿主看不到 step 边界。
4. **界面复用 `HostPrompt` 步骤，不新增 `Retry` 类型**：P0 的步骤枚举已经有「宿主追问」，把标签改成「宿主提示」即可同时承载两类提示。
5. **`R11` 是顺带修的关键词缺口**：P1b 之前这批用例实际是 23/24（`R11`「看看工作区目录结构」一直 MISS）；
   `ReadWords` 补「目录结构 / 看目录」后 28/28。

### 测试

- 契约 / 路由：`ToolExposureProfileTests`（追问沿用编辑档、无历史只给核心集、换话题换档、本句优先于历史、
  `PreviousUserInputs` 取值与宿主提示过滤、描述拼接与幂等、关键工具规则互相指路）。
- 注册表：`AgentToolRegistryTests` 增「兜底入口把命中但未暴露的工具报给宿主」「模型看到的描述里带决策规则」。
- 循环：`NativeAgentLoopTests` 增「本轮内补工具后下一步请求就带上新工具」（3 次请求体断言）「本轮补工具只补一次」。
- 提示词：`FullPromptTests` / `CompactPromptTests` 覆盖两种 Full 变体与 `SupportsReasoning` 分叉。
- 界面：`ChatRoomToolExpansionTests`（补工具提示落成宿主提示步骤、答案不受影响）。
- 评测：`tools/routing-eval --mode rules` → 28/28。

## 12. 参考（证据位置）
DSH（0.2.1-rc.3，`app.asar` 内 `@deepseek-ai/`；`CHAT` = `dsh-client-ui-chat/lib/client.js`，`TOOL` = `dsh-client-ui-tool/lib/client.js`）：

> 复现方式：用 `.tmp/asar-extract.js` 从 `DeepSeek Harness\resources\app.asar` 解出 `dsh` 子树（`node .tmp/asar-extract.js <asar> dsh .tmp/dsh-src`），然后直接读各包 README 与 `lib/*.js`。`.tmp/` 是可丢弃的。

- `dsh-agent-loop/README.zh.md` §「一个步骤做什么」「轮次与步骤流程」（step 生命周期、提示词节点替换、KV cache 影响）
- `dsh-agent-loop/lib/index.js:264-295`（系统提示词节点原位替换）、`:334-349`（运行时快照）、`:970/:994`（step 边界）、`:1098-1135`（中断落地）、`:1241-1259`（工具增删声明）
- `dsh-tools/README.md:95`（Web Client 不消费 `presentCall`）、`dsh-tools/lib/index.js:3055-3075`（`view()` 与 `restrict`）
- `dsh-system-prompt/lib/index.js:44-52`（`CONTEXT_ORDERS`：working-directory/sandbox-policy/approval-policy 的位置）、`:101-103`（order→name 排序）、`:151-156`（具名贡献数组）、`:253-313`（`section/context/variable/tools` 注册）、`:324-346`（`refreshContext` 保留 required）、`:356-406`（组装）
- `dsh-time-context/README.zh.md`（三行读数、10 分钟节流、`unavailable`、时钟倒退钳零、KV 仅追加）
- `dsh-system-prompt/lib/index.js:87-95,117-156,356-406`（section 排序、快照渲染、组装）
- `dsh-llm/lib/typert.host.js:572`（StreamChunk 联合）、`dsh-llm-deepseek/lib/index.js:1570-1574,1609,1832-1841`（signature 回放、reasoning 丢弃、usage 四字段）
- `dsh-compaction-basic/lib/index.js:292-323,410-433,469,596-606,634-661`（热前缀、范围、事务、拒绝不缩小）
- `dsh-compaction-tool-result-pruner/lib/index.js:8-14,91-124`（8192/4096/1024）
- `dsh-token-meter/lib/types/estimate.js:9-13`（启发式）、`dsh-token-meter/README.zh.md`（measure/投影）
- `dsh-spill-policy/lib/index.js:24-27,179-235`（12500 token、头尾预览、尽力而为）
- `CHAT:6164-6217`（ReasoningRow）、`:7805-7829`（block 联合）、`:11515-11528`（group part 拆分）、`:12662-12691`（四档配置）、`:12621`（折叠时机）、`:3644-3648`（不可折叠的条件）
- `TOOL:307-321`（行模型与 5 态）、`:1579-1587,1612,1837-1851,1967`（状态、可展开、分发）
- `dsh-file-reference/README.zh.md:12,32,40`（只给路径、mention 语法、`FILE_REFERENCE_PROMPT`）、`dsh-client-ui-reference/README.zh.md:32-34`（pick/drill、`@session`）
- `dsh-commands/README.zh.md:12,50,54,58`（UI 平面、严格语法、未知拒绝、附件准入）、`dsh-client-ui-skill/README.zh.md:12,28,32,91,105`（`/` 技能、命令优先、排序、token、纯文本）
- `dsh-tool-skill/README.zh.md` §「调用边界」「会话目录」（`<skill_content>` 注入位置、防重复加载、`disable-model-invocation`）
- `dsh-client-ui-chat/README.zh.md` §「轮次过程折叠」「分组渲染」

第三方插件 `dsh-context`（`git@github.com:bowenliang123/dsh-context.git`，v0.75.1；本地 clone 在 `.tmp/dsh-context`，可丢弃）——**评估后不采用，仅保留评估记录**：

- `src/host/promptInjection.ts`（唯一写路径：系统提示词前后缀）
- `src/host/index.ts:30-59`（只注册只读投影）
- `src/shared/estimate.ts`（计量常数镜像）、`src/client/categories.ts`（官方锚定 + 比例分配纪律）

本仓库：

- `src/Agents/SDemo.DesktopAgent.Agents/AgentRuntime.cs`（`AgentInvocation.NativeLoop`、step 事件、`FunctionCall/Result.Step`）
- `src/Agents/SDemo.DesktopAgent.Agents/NativeAgentLoop.cs`（P1a 自建循环：真工具消息、最后一轮摘工具、未知工具/异常兜底）
- `src/Agents/SDemo.DesktopAgent.Agents/MafAgentRuntime.cs`（两条循环的分叉点、客户端链与 `ChatOptions` 共用）
- `src/App/SDemo.DesktopAgent.Core/Services/AgentChatService.cs`（step 跟踪、`AgentDelta.Step`、`NativeLoop` 透传）
- `src/Contracts/SDemo.DesktopAgent.Contracts/Models/AgentSettings.cs`（`NativeLoop` 开关，默认关）
- `src/App/SDemo.DesktopAgent.UI/Components/Settings/ToolsSettings.razor`（「内置工具循环」开关）
- `src/Agents/SDemo.DesktopAgent.Agents/MafAgentRuntime.cs:32-46,59-90`（FICC 与事件映射）
- `src/App/SDemo.DesktopAgent.Core/Services/AgentChatService.cs:174-230`（合并 delta、轨迹记录）
- `src/Context/SDemo.DesktopAgent.Context/SystemPrompts.cs:11-47`（`TaskRules` 共用规则、`FullWithPlanLine` / `FullWithReasoning`、`FullFor(SupportsReasoning)`）
- `src/Context/SDemo.DesktopAgent.Context/ContextBuilder.cs:60-79,416-479`（提示词拼装与注入块）
- `src/Context/SDemo.DesktopAgent.Context/AgentMessageMapper.cs:70-72,195-208`（`[工具轨迹]` 文本块）
- `src/Contracts/SDemo.DesktopAgent.Contracts/Models/ChatTimeline.cs:16-48`、`Context/ToolTraceModels.cs:16-21`（字符边界还原）
- `src/App/SDemo.DesktopAgent.UI/Components/Chat/ChatTimeline.razor:14-62`（运行中 17-29、默认收起 78）、`ChatRoom.razor:935-945,162-202,2255-2278,2339-2392`
- `src/Tools/SDemo.DesktopAgent.Tools.Abstractions/CapabilityRouter.cs`（`Route`、`ResolveProfile` / `ToolsOfProfile`、`PreviousUserInputs`、`HostPromptPrefix`）、`ToolPlaybooks.cs`（模型看到的决策规则表）、`TurnClassifier.cs:54-81`
- `src/Context/SDemo.DesktopAgent.Context/ContextCompactor.cs:67-215`、`ContextBudgetChatClient.cs`（中途裁剪；P2c 起含落盘与回读预览）、`ContextTrimmer.cs:104-148`
- `src/Context/SDemo.DesktopAgent.Context/State/StatePackFormatter.cs:21-59,61-144`（P2a 前的 god-renderer 与写死的截断顺序，已删除）、`State/WorkspaceState.cs`（数据源）、`State/WorkspaceExcludes.cs`（共用排除表）
- `src/Context/SDemo.DesktopAgent.Context/AgentInstructions.cs:9-19`（死代码；静态沙箱文案）
- `src/App/SDemo.DesktopAgent.UI/Components/Chat/ChatComposer.razor`（触发菜单、命令路由、`/技能` 点名、高亮层）、`ChatRoom.razor`（`HandleComposerCommandAsync`、`_turnSkill`）
- `Contracts/Models/ComposerTrigger.cs`、`ComposerTokens.cs`、`ComposerCommands.cs`、`ComposerMatch.cs`（语法、扫描、命令目录、排序）
- `src/App/SDemo.DesktopAgent.Core/Services/WorkspaceReferenceIndex.cs`（`@` 路径索引）
- `src/Context/SDemo.DesktopAgent.Context/SkillCatalog.cs`（`disable-model-invocation` 解析）、`SkillIndexBuilder.cs`、`Tools.Skills/SkillToolPack.cs`（索引过滤与 `load_skill` 拒绝）
- `src/App/SDemo.DesktopAgent.UI/Components/Chat/ContextBreakdown.razor`、`ContextMeter.razor`（分解面板与环形；P2a 起按段渲染）；`Contracts/Context/ContextReport.cs`（段定义）
- `src/Context/SDemo.DesktopAgent.Context/ContextSections/`（P2a）：`IAgentContextSection.cs`（接口 + 中央顺序 + `ContextSectionUsage`）、`ContextSectionRegistry.cs`（排序 / required 先占 / 段内截断）、`StateContextSections.cs`（workspace / changes / actions / verification / plan / time）、`PolicyContextSection.cs`、`AgentPolicyFacts.cs`、`ContextSectionText.cs`（段内截断与相对时间）
- `src/Context/SDemo.DesktopAgent.Context/HostSnapshot.cs`（快照前缀与 `IsSnapshot` 判据）、`ContextBuilder.cs`（`BuildSnapshot` / 快照最后追加 / 预算从历史里扣）、`ContextReport.cs`（`Sections` / `CacheHitTokens`）
- P2b：`src/Context/SDemo.DesktopAgent.Context/ContextPruner.cs`（修剪档位）、`ContextTrimmer.cs`（收紧顺序：修剪 → 淘汰 → 缩附件）、`ContextCompactor.cs`（事务标记 / 拒绝不缩小 / 快照不压缩）、`Summarizers/ISummaryChatTransport.cs`（`SummaryChatRequest`：messages + tools）、`Summarizers/SummaryPrompt.cs`（真前缀回放 + 压缩指令）、`Summarizers/SummaryFormat.cs`（`RemapSources`）、`Summarizers/LlmContextSummarizer.cs`（去工具重试）
- P2b：`src/Contracts/SDemo.DesktopAgent.Contracts/Models/ChatSession.cs`（`CompactionStartedAt/FinishedAt/Reason`、`HasUnfinishedCompaction`）、`src/Storage/SDemo.DesktopAgent.Storage/Migrations/20261025050412_AddSessionCompactionState.cs`、`src/App/SDemo.DesktopAgent.Core/Services/AgentChatService.cs`（`Describe` = 本轮提示词 + 工具声明）、`UI/Components/Chat/CompactionDivider.razor`（中断证据行）
- P2c：`src/Context/SDemo.DesktopAgent.Context/Spill/ToolResultSpillStore.cs`（`IToolResultSpillStore` + 工作区实现：文件名清洗、`.agent/.gitignore`、文件数上限）、`ContextBudgetChatClient.cs`（落盘 + 头尾预览 + `read_file` 回读指引、`read_file` 跳过落盘）、`ContextRunTelemetry.cs`（`SpilledToolResults` / `SpillFailures`）、`Contracts/Models/ContextSettings.cs`（`SpillLargeToolOutputs`）、`Tools.Files/FileToolPack.cs`（`maxLineChars`）、`State/WorkspaceExcludes.cs` + `WorkspaceSearch.ExcludedDirectories`（`.agent` 不入树 / 搜索）、`UI/Components/Chat/ContextBreakdown.razor`（落盘说明行）
- `src/Agents/SDemo.DesktopAgent.Agents/AgentRuntime.cs`（`Usage.CacheHitTokens`）、`MafAgentRuntime.cs` / `NativeAgentLoop.cs`（`CachedInputTokenCount` 透出）、`Contracts/Services/AgentRunMetrics.cs`

## 13. P2a 实施记录（上下文段注册表 + 尾部快照 + 缓存命中测量）

状态：✅ 已落地。WPF 解决方案 0 警告 0 错误（Debug / Release）；Core 800、UI 111、Sandbox 219（1 跳过）测试全绿；
`tools/routing-eval --mode rules` 仍 28/28。

### 落地形态

| 计划项 | 实现 |
|---|---|
| §3.3.1-(1) 段注册表 | 新增 `Context/ContextSections/`：`IAgentContextSection`（`Name` / `Title` / `Order` / `Required` / `MaxTokens` / `Render`）+ `ContextSectionOrders` 中央顺序表 + `ContextSectionRegistry`（按 `Order → Name` 排序 → 丢空段 → required 先按 Order 占预算 → 可选段填剩余 → 每段按 `MaxTokens` 截断），重名注册直接抛异常 |
| 内置段 | `workspace` 100 / `policy` 110 / `changes` 120 / `actions` 125 / `verification` 130 / `plan` 140 / `time` 150；`required` = `policy` + `verification` + `time` |
| §3.3.1-(2) 尾部快照 | `ContextBuilder.BuildSnapshot` 在裁剪**之后**追加一条 `user` 消息，前缀由 `HostSnapshot.Header` 统一（「宿主生成，不是用户输入；本快照取代之前的快照」）。系统提示词里只剩稳定前缀 + 压缩摘要 |
| `policy` 段（新增） | `AgentPolicyFacts.FromSettings`：本轮模式、沙箱（类型 / 必需 / 副本只读）、直跑与命令白名单、写回是否需确认 |
| `time` 段（新增） | 宿主时钟 ISO + 偏移 + IANA 时区、距上一条模型可见消息的时长（`unavailable` / 刚刚 / N 分钟 / N 小时…）；Windows 时区 id 先转 IANA，转不出才退回原 id |
| 归因 | `ContextReport.Sections`（`ContextSectionUsage[]`：名字 / 标题 / 顺序 / required / token / truncated）；`ContextBreakdown` 在「上下文快照」行下面按段列出，并标出必需 / 已截断 |
| 单段开关 | 新增 `TimeContextEnabled` / `PlanContextEnabled`；`StatePackEnabled` 收窄为「工作区结构 / 改动 / 动作」三段的开关（策略与验证不受它影响，关掉时也不扫盘） |
| 缓存命中测量 | `UsageDetails.CachedInputTokenCount` → `AgentStreamEvent.Usage.CacheHitTokens` → `AgentRunMetrics.CacheHitTokens` → `ContextReport.CacheHitTokens` + `CacheHitRate`；界面只在「有实际输入 token 且命中 > 0」时显示命中率 |
| 摘要留在固定块 | `ContextBuilder.BuildInjection` 只渲染压缩摘要：它只在压缩时变，不会再每轮改系统前缀 |

### 与计划的偏差（取舍）

1. **段标题用 `###`**，`##` 留给快照本身——层级上快照是「一条消息」，段是它的子结构。
2. **保留 `actions`（最近工具动作）段**：计划表里没有它，但它是现有状态包的一部分，删掉等于静默丢事实；
   它和 `workspace` / `changes` 一样受 `StatePackEnabled` 管。
3. **`time` 做成 required**：它只有 ~40 token，却是「这条构建结果是 3 分钟前还是昨天」的唯一依据；
   用户仍可用开关整段关掉——**开关优先于 required**，两者是不同轴线。
4. **快照在裁剪之后追加**，所以不需要给裁剪器加「快照例外」：它在结构上就不可能被当成最旧的一轮淘汰。
   这一点和 DSH 不同（DSH 的快照要持久化进日志，必须显式排除），因为我们的历史是每请求重建的。
5. **`AgentChatService.ResolveRouting` 必须按前缀排除快照消息**：否则「最后一条 user 消息」会变成快照，
   把对话轮误判成任务轮（回归由 `AgentChatServiceToolExposureTests.对话轮不生成工具并切换对话提示词` 挡住）。
   任何以后「从请求消息里取用户输入」的代码都要用 `HostSnapshot.IsSnapshot` 过滤。
6. **段内截断统一为「保留头部行 + 一行标记」**；只有计划段保留自己的 200 token 增量截断，因为它需要
   「计划已截断」这句人话。`required` 段不参与总额度竞争，但仍按自己的上限截断。
7. **删除 `StatePackFormatter`**，`DefaultExcludes` 移到 `WorkspaceExcludes`：树扫描、`@` 引用索引、
   快照的 `workspace` 段三处共用同一张排除表（以前是两处引用同一份常量）。
8. **字节稳定靠注入 `TimeProvider`**：`ContextBuilder` 多了可选 `ContextSectionRegistry` / `TimeProvider`
   两个参数，DI 不注册也能解析（可选参数走默认值），测试里冻住时钟就能断言逐字节相同。
9. **没有做「改动前后各 20 轮」的缓存实测**：本机没有可用模型端点。测量链路已经端到端测通
   （假 SSE 上报 `prompt_tokens_details.cached_tokens` → 事件 → 指标 → 报告），但「命中率是否下降」
   必须在真实端点上跑；而且端点若根本不报这个字段，这条改造只是「结构更干净」（§6 风险 4）。

### 测试

- 段 / 契约：`StatePackTests`（扫描排除与上限、workspace 截断后 required 仍在、相对时间、关段后零成本、
  关段不扫盘、树缓存、改动去重、计划 200 token、重名抛异常、中央顺序覆盖注册顺序）；
  `ContextSnapshotTests`（预算压到 200 时 required 仍在且 `Truncated=false`、连续 5 轮逐字节相同、
  关 `time`/`plan` 成本为 0、切直跑 / 只读后 `policy` 文本变化、时间读数三行、快照永远是最后一条 `user` 消息、
  快照预算从历史预算里扣）。
- 构建器：`ContextBuilderSummaryTests`（摘要仍在系统块、快照在末尾且带段归因、直通不加快照、
  无工作区时仍带策略与时间）、`ContextBuilderTests`（消息数 +1、分段之和 = 总估算）。
- 回路：`CacheHitTests`（假 `IChatClient` 与假 SSE 两条路径都透出 cached tokens、没有上报时为 0、
  命中率只在有实际输入时计算）。
- 界面：`ContextBreakdownTests` 增 3 条（按段渲染 + 必需 / 截断标记、命中率显示、无上报不显示）。

### 边界（留给 P2b / P2c）

- 压缩还没事务化：`compaction/start|summary|end` 三个状态字段属于 P2b。→ ✅ P2b 已补（§14）。
- 快照不参与压缩范围计算：压缩从「第一个非 system 节点」起、快照在末尾，天然落在保留区；
  P2b 需要显式给它打 `IsPinned`。→ ✅ P2b 已补（§14）。
- 大输出仍然只是截断（头 1200 / 尾 300 字符），落盘属于 P2c。→ ✅ P2c 已补（§15）。

## 14. P2b 实施记录（压缩补三性质：先修剪 / 热前缀 / 事务）

状态：✅ 已落地。WPF 解决方案 0 警告 0 错误（Debug / Release）；Core 821、UI 116、Sandbox 219（1 跳过）
测试全绿；`tools/routing-eval --mode rules` 仍 28/28。

### 落地形态

| 计划项 | 实现 |
|---|---|
| §3.3.2-(1) **先修剪，后摘要** | 新增 `Context/ContextPruner.cs`（档位：配置值 → 减半 → 0，低于 100 直接归零）；`ContextTrimmer` 的收紧顺序改为「**修剪工具轨迹 → 淘汰整轮 → 缩当前轮附件**」；省下的量记进 `TrimStats.PrunedTraceTokens` → `ContextReport.PrunedTokens` / `PrunedInsteadOfCompacted`；Runner 在「修剪后就没有历史需要淘汰」时直接跳过摘要（0 次模型调用）并给一行提示；分解面板单独列「已先修剪工具轨迹约 N token」 |
| §3.3.2-(2) **摘要请求复用热前缀** | `ISummaryChatTransport` 从契约层**下移**到 Context（它要接 `AITool`，而契约层不许引用 AI SDK）；`SummaryChatRequest`（messages + tools + maxTokens）；`SummaryPrompt.BuildMessages` 拼 `[system=会话原样] + [已有摘要] + [被遮蔽区域逐条回放] + [user=压缩指令]`；`ContextCompactionRequest.Instructions/Tools` 由 `IAgentChatService.Describe(plan)` 提供（`AgentChatService` 走与 `RunAsync` **同一条** `CreateExposedTools` 路径，声明顺序一致）；`Agents` 工程新增对 `Context` 的引用（方向仍是上层 → 上下文引擎，无环） |
| §3.3.2-(3a) **拒绝不缩小的摘要** | 摘要 token ≥（被遮蔽区域 + 旧摘要）时拒绝：`CompactionOutcome.Rejected`，原文 / 旧摘要 / 覆盖范围都不动，标记记 `rejected` |
| §3.3.2-(3b) **事务化** | `ChatSession.CompactionStartedAt / FinishedAt / Reason` + `HasUnfinishedCompaction` + 运行期 `CompactionInterruptedOnLoad`；`sessions` 表三列（迁移 `20261025050412_AddSessionCompactionState`）；压缩**先把开始标记落库（这就是锁）**，摘要与覆盖范围最后一次性提交；未配对的开始 = 同进程有压缩在跑（本轮让路 `Busy`）或崩溃留下的证据（读回后由下一次压缩接管） |
| §13 边界 **快照显式 pin** | `ContextTrimmer` 把快照消息并入「永不淘汰」集合（不占 pin 名额、不参与 pin 截断）；`ContextCompactor` 的轮次切分忽略快照，压缩范围在第一条快照前截断 |
| 界面 | 会话头「上次压缩未完成」徽标（带悬浮说明：开始时间 + 原因 + 摘要未提交）、分隔线上多一行证据文案 |

### 与计划的偏差（取舍）

1. **「先修剪」的实际触发频率取决于轨迹预算。** 默认 `ToolTraceTokenBudget=300` 时，轨迹本来就只占 300 token，
   修剪很少是超预算的主因；这条性质在预算调大（长上下文档）时才明显。顺序钉死的价值在于：
   以后再加收缩档位时不会再回到「先丢历史、再修细节」。
2. **回放不是 100% 逐字节。** 区域消息按 `AgentMessageMapper.Render(msg, 0, null)` 渲染（附件桩、无轨迹），
   而真实请求的第一条保留消息可能带一行 `[上下文提示] 已省略…`。不一致时只是缓存不命中，不影响摘要质量；
   做到逐字节需要把上次实际发出的 payload 存下来，那是 P2c 之后的事。
3. **来源序号改为「按位置数」再回填。** 逐字回放不能往每条消息前面插 `[mN]`，所以指令改成「第一条是 m1」，
   由 `SummaryFormat.RemapSources` 换回会话绝对序号；超出本批范围的值原样保留（通常是上一版摘要里的绝对序号，或模型照抄的来源）。
4. **带工具声明后个别模型会直接发起工具调用**（没有文本）：`LlmContextSummarizer` **去掉工具重试一次**，
   而不是把这次压缩降级成确定性兜底。格式不符的既有重试保持不变。
5. **「不缩小」按「被替换内容 = 区域 + 旧摘要」比较**，不是只比区域——摘要整体取代旧摘要，只比区域会把
   「摘要变小但比旧摘要大」的情况误判成有效。
6. **崩溃恢复不做「跳过这一段」。** 覆盖范围没提交，所以重做是幂等的（同一段最多重做一次）；
   `Busy` 只拦同进程的并发压缩。界面上的「上次压缩未完成」是证据与诊断，不是拒绝服务。
7. **手动压缩也走同一条事务与热前缀**：「压缩现在」先尽力建一次上下文拿前缀（`DescribeBestEffort`），
   建不出来（超预算）就退回摘要器固定提示词，压缩照做。
8. **没有独立 `compaction/summary` 事件**：只有 start / end 两个标记 + 原因，够诊断、够恢复，
   但没有 DSH 那样的事件流；要事件流就得先把会话存储换成事件溯源（§4 已明确不搬）。
9. **失败 / 取消也一定把内存里的标记收尾**（落库用 `CancellationToken.None` 尽力而为）：
   否则一次失败的压缩会让这个会话在本进程里永远「有压缩在跑」，之后每一轮都让路。
   库里的「只有开始」留到下次打开时显示未完成——两份状态说的是同一件事：**这次压缩没有提交**。

### 测试

- 修剪：`ContextPrunerTests`（档位序列 / 单调 / 首档是原配置）；`ContextTrimmerTests` 增 3 条
  （先修剪再淘汰整轮、正文本身超预算时才淘汰、快照永不被淘汰）。
- 热前缀：`ContextSummarizerTests` 增 4 条（真前缀结构 + 工具声明、无会话提示词时退回固定提示词、
  工具调用无文本时去工具重试、来源序号按位置回填）；`AgentSessionRunnerCompactionTests` 增 2 条
  （Runner 把 `Describe` 的前缀转交给摘要请求、修剪后就放得下时一次摘要都不调）。
- 事务：`ContextCompactorTests` 增 7 条（开始 / 提交各落库一次且中间态只有开始标记、拒绝不缩小、
  未收尾时让路、崩溃读回后接管、提交前崩溃时库里没有半截摘要、提交保存失败后不会被当成有压缩在跑、
  摘要器抛异常时事务也会收尾、快照不进入压缩范围）；`ChatStoreSummaryTests` 增 2 条（标记落库往返、崩溃读回标成中断）。
- 界面：`CompactionDividerTests` 增 2 条（中断证据行 / 正常压缩不显示）；`ChatRoomPresentationTests`
  增「未完成徽标 + 没有分隔线 + 原文照常」；`ContextBreakdownTests` 增 2 条（修剪行、没修剪不显示）。
- 回归：Core 821（+21）、UI 116（+5）、Sandbox 219（1 跳过，未变）、routing-eval 28/28。

### 边界（留给 P2c）

- 大输出仍然只是截断（头 1200 / 尾 300 字符）：落盘与 `read_file` 回读属于 P2c。→ ✅ P2c 已补（§15）。
- 摘要请求的前缀**是否真的命中缓存**只能看端点上报的 `CacheHitTokens`；本机没有可用端点，
  测量链路已通（P2a），结论留给真实端点。
- `ContextBudgetChatClient` 的中途裁剪没有接入修剪档位：它在工具循环内部只做「单条工具结果强裁 + 整块淘汰」，
  与 P2c 的 spill 一起重做更合适。→ ✅ P2c 已在同一处把「强裁」换成「落盘 + 预览」（§15）；
  修剪档位仍只在 `ContextTrimmer` 里，循环内部不调摘要（P2b 的事务语义不变）。

## 15. P2c 实施记录（大工具输出落盘）

状态：✅ 已落地。WPF 解决方案 0 警告 0 错误（Debug / Release）；Core 832（+11）、UI 118（+2）、Sandbox 219（1 跳过）
测试全绿；`tools/routing-eval --mode rules` 仍 28/28。

### 落地形态

| 计划项 | 实现 |
|---|---|
| §3.3.3 落盘 seam | 新增 `Context/Spill/ToolResultSpillStore.cs`：`IToolResultSpillStore.Save(callId, text)` + `WorkspaceToolResultSpillStore`（写 `<workspace>/.agent/spill/<callId>.log`，UTF-8 无 BOM，同内容不重写） |
| 上下文替换 | `ContextBudgetChatClient` 把「强裁」换成「先落盘再预览」：头 1200（在**行边界**收口）+ 尾 300 + 全文路径 + `read_file("…", offset: N)` 指引；整份输出没有换行时改为提示 `maxLineChars` |
| `read` 不自落盘 | 从历史里的 `tool_calls` 建 CallId → 工具名，`read_file` 的输出显式跳过落盘（DSH 同样跳过 `read`） |
| 尽力而为 | 写盘失败返回 `null` → 退回纯截断并注明「全文未能落盘」；`ContextRunTelemetry.SpillFailures` 记账，工具结果本身仍是成功 |
| 不污染工作区 | 落盘目录自带 `.agent/.gitignore`（内容 `*`）；`.agent` 进 `WorkspaceExcludes` 与 `WorkspaceSearch.ExcludedDirectories`（树 / `@` / 搜索都不见），只能显式 `read_file` 读；文件数上限 200，超出按最后写入时间清理最旧的 |
| 回读能力 | `read_file` 新增 `maxLineChars`（默认 500，最多 20000）：日志里的超长单行可一次多看一些；跨行续读仍走原有 `offset` / 「接续用 offset=」链 |
| 设置 | `ContextSettings.SpillLargeToolOutputs`（默认开）+ 设置页「高级」开关 |
| 归因 | `ContextReport.SpilledResults` / `SpillFailures` → 分解面板一行「已把 N 条超长工具输出落盘到 .agent/spill（可用 read_file 回读，无需重跑）」 |
| 决策规则 | `ToolPlaybooks` 的 `read_file` / `run_command` / `run_powershell` 各补一句「输出被截断时按 .agent/spill 路径回读，不要重跑命令」 |

### 与计划的偏差（取舍）

1. **落盘点在「中途裁剪装饰器」，不是「工具结果被接纳的那一刻」**。FICC 与 `NativeAgentLoop` 都在
   `ContextBudgetChatClient` 内侧，这个装饰器是两边都经过的唯一 seam；按 §14 的边界，把「强裁」就地换成
   「落盘 + 预览」最合适。代价：文件在下一次请求前才写（用户无感），工具名要靠历史里的 `tool_calls` 反查。
2. **阈值沿用 `MidTurnToolResultChars`（默认 2000 字符），没有另加 `maxInlineTokens`**。DSH 用 12500 token
   是因为它不裁工具结果；我们的上下文里本来最多只留 2000 字符，再设一个更大的落盘阈值等于「先丢 200K 再落盘」。
   同一个数字既是触发条件也是内联上限，只有一条规则。
3. **落到工作区而不是 AppData**：`read_file` 只允许读工作区（+ 附件只读根），落到外面就没有回读通道，
   还得给工具面加新工具。工作区方案的副作用（`git status` 污染）用目录内 `.gitignore` 解决。
4. **`.agent` 不进搜索 / 树 / `@` 索引**：它们是会话日志，不是工作区内容；让 `search_files` 扫几百个
   200K 日志只会拖慢并淹没真正的命中。显式路径的 `read_file` 不受影响。
5. **清理不是精确的**：按最后写入时间保留 200 个。很久以前那次的全文可能被清掉，但预览仍在历史里——
   这是有意的有界化，不是完整性承诺。
6. **`read_file` 加了 `maxLineChars`**（计划里没有）：没有它，压缩成单行的 JSON / 日志即使落盘也只能读到
   前 500 字符，回读等于没做。参数默认 500，行为与旧版逐字节一致。
7. **没有把「全文路径」写进 `ToolTraceEntry`**：轨迹在结果回来那一刻落库，而落盘发生在下一次请求前；
   要写进去就得让装饰器反向更新 metrics，收益（工具卡多一行路径）不抵这条反向依赖。落盘 / 失败条数走报告。

### 测试

- Core：`ToolResultSpillTests`（8 条）：超长结果落盘且上下文只剩预览 / 按预览里的 `read_file(path, offset)`
  一路翻到第 3000 行的中间标记 / `read_file` 的结果不落盘 / 写盘失败退回纯截断并记 `SpillFailures` /
  同一 `callId` 二次请求不重写文件且预览逐字节相同 / 文件名清洗与缺省哈希 / 落盘目录自带 `.gitignore` /
  没有工作区时不落盘。
- 接线：`AgentChatServiceToolExposureTests` 新增 2 条（走真实的装饰器链验证「超长结果落到工作区 `.agent/spill`」、
  关掉 `SpillLargeToolOutputs` 后不落盘）。
- 读取：`ReadFileRangeTests` 新增「超长单行可调大单行上限读完」。
- 界面：`ContextBreakdownTests` 新增 2 条（落盘条数与失败条数、没落盘不显示）。
- 回归：Core 832（+11）、UI 118（+2）、Sandbox 219（1 跳过）、routing-eval 28/28。

### 边界（后续）

- 落盘的是**原始工具结果**（含 `[TOOL_RESULT_BEGIN:…]` 包装），不是清洗后的正文：回读时模型看到的内容
  与它上一轮看到的一致，不另存一份格式。
- 回读成本仍按普通 `read_file` 计（每次 30K 字符 / 2000 行）；按字节切片要改 `read_file` 的语义，暂不做。
- 沙箱里的 `run_command` 看到的是**工作区副本**：`.agent/spill` 在副本里也在，但它是宿主写的，不参与写回比对。
