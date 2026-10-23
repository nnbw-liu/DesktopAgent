# P1 报告：上下文预算与硬裁剪

> 期次：P1（第 1 期）　分支：`feature/vibe-p1-context`　状态：✅ 完成
> 配套文档：[context-budget.md](../architecture/context-budget.md)、[p1-baseline.md](p1-baseline.md)、[p1-baseline-data.json](p1-baseline-data.json)
> 原始计划：[p1-context-budget.md](p1-context-budget.md)

## 1. 交付物

| 模块 | 文件 |
| --- | --- |
| 模型档案 | `Core/Models/ModelProfile.cs`、`Core/Context/ModelProfileResolver.cs`、`Core/Models/ModelProvider.cs`（`DefaultProfile` / `ModelEntry.Profile`） |
| 设置 | `Core/Models/ContextSettings.cs`、`Core/Models/AppSettings.cs`、`Core/Services/SettingsService.cs` |
| 计量与校准 | `Core/Context/TokenCounting/*`（`ITokenCounter`、`HeuristicTokenCounter`、`TokenCalibrationStore`） |
| 预算与策略 | `Core/Context/ContextBudget.cs`、`ContextPolicy.cs` |
| L1 裁剪 | `Core/Context/ContextTrimmer.cs`、`Core/Services/AgentMessageMapper.cs`（`Render` 分段） |
| 单一入口 | `Core/Context/ContextBuilder.cs`、`ContextReport.cs`、`ContextBudgetException.cs`、`AgentInstructions.cs` |
| 工具轨迹 | `Core/Context/ToolTrace.cs`、`AgentRunMetrics.cs`、`AgentChatService.cs`（采集 / 摘要 / 分类） |
| 中途裁剪 | `Core/Context/ContextBudgetChatClient.cs`、`ContextRunTelemetry.cs` |
| 溢出识别 | `Core/Context/ContextOverflowDetector.cs` |
| 应用层 | `Core/Services/AgentSessionRunner.cs`、`Plugins/PluginRunner.cs`（passthrough）、`Composition/AgentCoreServiceCollectionExtensions.cs` |
| 持久化 | `Core/Data/ChatDbContext.cs`（summary 列 + `ToolTraceJson` / `IsPinned`）、`Core/Services/ChatStore.cs`（`RequiredColumns` + 读写） |
| UI | `UI/Components/Chat/ContextMeter.razor`、`ContextBreakdown.razor`、`ChatRoom.razor`（runner / 占用环 / 宿主提示）、`AppShell.razor`（克隆字段）、`Settings/GeneralSettings.razor`（上下文区 + 重置校准）、`Settings/ProviderEditor.razor`（窗口档位）、`wwwroot/css/app.css` |
| 基线工具 | `tools/p1-baseline/*`（可复现的真实请求采样） |
| 文档 | `docs/architecture/context-budget.md`、本报告、`p1-baseline.md`、README |

## 2. 计划任务对照

| # | 任务 | 状态 | 说明 |
| --- | --- | --- | --- |
| 1.1 | MAF 工具循环拦截 spike | ✅ | 结论：`FunctionInvokingChatClient` 在装饰器外层，分支 A 可行；快照测试 `MidTurnPipelineSpikeTests` |
| 1.2 | 模型档案 + 设置持久化 | ✅ | 显式档位 / 名字 / 家族 / provider 默认四级解析；老设置自动补 `Context` |
| 1.3 | 启发式计量 | ✅ | 中英加权 + 每条 4 token 开销；真实误差见基线 |
| 1.4 | 校准存储 | ✅ | EMA、偏差停用、窗口学习、原子写盘、重置 |
| 1.5 | 预算与策略 | ✅ | 预留输出 / 安全余量 / 固定块 / compact 目标，全部夹在合理范围 |
| 1.6 | L1 裁剪 | ✅ | 保留轮次、整轮淘汰、附件桩、轨迹限额、pin 上限 |
| 1.7 | 单一上下文入口 | ✅ | `ContextBuilder`；`AgentMessageMapper.Render` 负责渲染 |
| 1.8 | 报告结构 | ✅ | `ContextReport` / `ContextSegments` / `ContextPlan` |
| 1.9 | 工具轨迹 | ✅ | 采集、摘要、状态分类、DB 列、回放格式 |
| 1.10 | 中途裁剪 | ✅ | 强裁工具结果 + 整块淘汰；保留 system 前缀与最后一块 |
| 1.11 | 溢出识别与重试 | ✅ | 明确文案 + 400 状态；半预算重试一次；窗口学习 |
| 1.12 | 上下文占用环 | ✅ | 占用环 + 分段明细 + tooltip；>80% / >95% 变色 |
| 1.13 | 宿主提示 | ✅ | 溢出重试 / 需压缩显示在消息区末尾，可关闭，不入上下文 |
| 1.14 | 真实基线 | ✅ | 本机三模型 + 火山一模型；13 次真实请求，数据见 `p1-baseline.md` |

## 3. 完成定义（DoD）核对

| DoD | 结果 | 证据 |
| --- | --- | --- |
| 所有请求都有预算 | ✅ | `ContextBuilder` 唯一入口；`ContextPlan` 必含 `Budget` + `Report`；UI 不再拼消息 |
| 可计量、不溢出 | ✅ | 报告含估算 / 实际 / 分段 / 省略；8K 预算下 200 轮 + 50KB 附件 4/4 成功 |
| 8K + 200 轮 + 50KB 不 400 | ✅ | 基线第 3 节：估算 575 ≤ 4874，服务端 200 |
| 计量误差 ≤ 15% | ✅ | 最大 9.31%，短对话 ≤ 2.28%（`p1-baseline.md`） |
| 新增测试 ≥ 25 | ✅ | 新增 60 个用例（见 §4），全量 291 通过 |
| UI 与报告 ±5% | ✅ | UI 直接读 `ContextReport.Estimated/Total`；分段和 = 估算由单测固定，展示仅做 K 格式化（相对误差 < 1%） |
| 文档 | ✅ | `context-budget.md`、`p1-baseline.md`、本报告、README 章节、路线图状态 |
| 构建与测试 | ✅ | `dotnet build SDemo.DesktopAgent.Wpf.slnf` 0 警告 0 错误；291/291 通过 |

## 4. 测试

新增 60 个用例，全部为确定性单测（无网络、无真实模型）：

| 文件 | 数量 | 覆盖 |
| --- | --- | --- |
| `ContextTokenCounterTests` | 6 | 公式、空值、CJK、代理对、全角 |
| `ContextBudgetTests` | 5 | 8K 公式、小窗口夹取、比例夹取、Scale、固定块上限 |
| `ModelProfileResolverTests` | 7 | 显式档位、名字档位、家族、provider 默认、归一化、学窗口 |
| `TokenCalibrationStoreTests` | 6 | EMA、偏差停用、恢复、缺 usage、夹取、持久化与重置 |
| `ContextTrimmerTests` | 5 | 保留轮次、当前输入不可淘汰、附件桩、轨迹预算、pin 截断 |
| `ContextBuilderTests` | 7 | 分段和、NeedsCompaction、技能截断、直通、50KB 附件、历史附件桩、校准因子 |
| `ToolTraceTests` | 7 | 行格式、回放预算、零预算、摘要截断、参数摘要、状态分类 |
| `ContextOverflowDetectorTests` | 4 | 文案识别、窗口学习、嵌套异常、误判防护 |
| `AgentSessionRunnerTests` | 6 | 预览、溢出自愈、半预算、出字后不重试、超限报错、usage 校准 |
| `ContextBudgetChatClientTests` | 4 | 工具结果强裁、整块淘汰、关闭直通、请求计数 |
| `ChatStoreContextTests` | 2 | 轨迹 / pin 读写、老库补列 |
| `MidTurnPipelineSpikeTests` | 1 | FICC 在装饰器外层 |

`dotnet test tests/SDemo.DesktopAgent.Core.Tests`：**291 通过 / 0 失败 / 0 跳过**。

## 5. 真机基线摘要

| 场景 | 最大误差 | 结果 |
| --- | --- | --- |
| 短对话（10 轮） | −2.28% | 4/4 成功 |
| 8K 预算 + 200 轮 + 50KB 附件 | +9.31% | 4/4 成功，无 400；请求从 ≈20K token 压到 ≈600 token |
| 连续校准 4 次 | +0.14% | 因子稳定在 1.00 附近，无漂移 |

完整数据与方法见 [p1-baseline.md](p1-baseline.md)。

## 6. 偏差与取舍

1. **50KB 附件不会全量内联**（有意）：8K 窗口下最多内联约 4096 token 正文，其余只留文件名 / 大小 / 路径；模型可再用 `read_file` / `pdf_read` 读取。DoD 的「不 400」通过「桩化 + 工具回读」达成，而不是把 50KB 硬塞进请求。这一条已写进 `context-budget.md` §11。
2. **8K 是预算档位，不是物理窗口**：本机与云端模型真实窗口都更大，基线以 8K 档模拟 8K 模型；请求大小由预算决定，因此结论仍成立。
3. **「预检超限」用异常表达**：`ContextBuilder.Build` 在无法再裁时抛 `ContextBudgetException(NeedsCompaction)`，而不是返回超限 plan。P2 接摘要时需要 catch 后用 `ex.Report` 判断，或新增非抛出变体（见 §7）。
4. **P1 不做摘要**：超限时的用户提示是「新建会话 / 减少附件 / 调大窗口」；摘要与状态包留给 P2。
5. **设置页未单开「上下文」分区**：复用「通用 → 上下文」区块（展示参数 + 关闭占用环 + 重置校准）。P2 计划里的独立 `ContextSettings.razor` 仍可拆，不影响数据模型。
6. **pin 只有数据与裁剪规则，没有交互**：`ChatMessage.IsPinned` 已落库、`AgentSessionRunner` 已把它传给裁剪器；悬浮图钉按钮与上限提示按计划留给 P2。
7. **UI 未做像素级截图比对**：占用环与明细直接读 `ContextReport`（同源），分段和 = 估算由单测固定；另做了启动冒烟（应用正常启动、无 DI 异常），视觉细节仍需人工点一遍。

## 7. 对 P2（压缩与工作记忆）的影响评估

**结论：没有阻断性返工；P1 提供了 P2 需要的预算、报告、裁剪、轨迹与持久化基础。以下 6 处需要在 P2 显式处理。**

### 7.1 流水线钩子：`Build` 是「抛异常」而不是「返回超限 plan」

P2 计划写的是：

```text
preflight = ContextBuilder.Build(...)
if preflight.OverBudget && autoCompact: ...
```

但 P1 的 `ContextBuilder.Build` 在裁剪后仍超预算时**直接抛** `ContextBudgetException(NeedsCompaction)`（带完整 `Report`），不会返回 `OverBudget` 的 plan。P2 有两种接法：

1. **推荐**：在 `AgentSessionRunner.StreamAsync` 里 catch `ContextBudgetException`，用 `ex.Report` 判断 `AutoCompact`，调 `ContextCompactor.EnsureAsync`，再带摘要重建；
2. 或在 P1 接口上加 `ContextBuildRequest.AllowOverBudget`（或 `TryBuild(out plan)`），让 preflight 不抛异常。

无论哪种，**不要把「压缩」放进 `ContextBuilder` 内部**——`ContextBuilder` 必须保持纯函数（只做预算与确定性裁剪），否则 P2 的「未超预算不产生摘要调用」验收项无法保证。

### 7.2 `ContextBuildRequest` 需要新增摘要 / 状态包输入

当前请求只有消息、技能、pin、instructions 覆盖与预算缩放，**没有** `Summary` / `StatePack` / `SummaryThroughOrder`。P2 需要：

- `ContextBuildRequest` 增加 `SummaryText`、`SummaryTokenEstimate`、`StatePackText`、`SummaryThroughOrder`；
- `ContextBuilder` 把摘要与状态包放进系统块（或紧随系统提示词的一条 system 消息），并计入 `FixedCap` 与分段；
- 裁剪器必须**跳过** `Order < SummaryThroughOrder` 的消息（它们已由摘要代表），否则摘要与原文会重复占用预算。

建议新增一个 `ContextInjection` 记录统一携带，而不是继续往位置参数上追加。

### 7.3 报告的「可扩展性」：位置记录需要加可选字段

`ContextSegments`（6 个位置参数）与 `ContextReport`（近 20 个位置参数）都是 `record`，P2 计划要求报告加 `SummaryUsed`、分段加「摘要 / 状态包」。直接加参数会破坏所有构造点（Core、测试、UI 里的 `Report()` 测试夹具）。建议：

- `ContextSegments` 末尾追加 `int Summary = 0, int StatePack = 0`，`Total` 纳入两项；
- `ContextReport` 末尾追加 `bool SummaryUsed = false, int SummaryTokens = 0, int CompactedMessages = 0`；
- UI 的 `ContextBreakdown.Bands()` 需要同步新增两段颜色与行（P2 的改动点，不是 P1 遗留缺陷）。

### 7.4 持久化：列已建好，但 `ChatSession` 还没有摘要字段

P1 已建列并做了老库补列：`sessions.Summary`、`SummaryThroughOrder`、`SummaryUpdatedAt`、`SummaryTokens`（`ChatStore.RequiredColumns` + 迁移测试）。但：

- `ChatSession` **没有**对应的领域字段，`ChatStore.SaveCoreAsync` / `ToSession` 目前**不读不写**这四列（EF 实体 `SessionRecord` 已有属性）；
- P2 需要补 `ChatSession.Summary/S...`、保存与加载映射、「`SummaryThroughOrder` 大于消息数时归零」的守卫、「删除 / 分支时摘要过期」的处理（计划 3.6）；
- `AppShell` 的分支克隆目前只克隆消息（`CloneMessage` 已含 `IsPinned` / `ToolTrace`），P2 还要决定分支是否复制摘要（建议复制并把 `SummaryThroughOrder` 按新消息顺序重算）。

因此 P2 的 `ChatStoreSummaryTests` 仍然必要，但**不需要再改迁移清单**。

### 7.5 设置与 UI：分区可拆，字段已就位

- `ContextSettings.AutoCompact` 已存在（P1 未使用），P2 直接消费；
- `Budget.CompactTarget` 已经是 `total × 0.6`，`Scale()` 也会同步缩小，P2 的「压缩到 60%」不用另算；
- `ContextReport.Occupancy` / `WindowOccupancy` 可直接做滞回判断；
- P2 计划要加的 `SummaryProviderId` / `SummaryModelId` / `SummaryMaxTokens` / 状态包预算等字段，直接加到 `ContextSettings` 即可（`SettingsService` 整对象序列化，无需改存储格式）；
- P1 的「通用 → 上下文」区块可继续保留展示项；P2 若要独立 `ContextSettings.razor` 设置分区，只是 UI 拆分，无数据影响；
- `ContextMeter` / `ContextBreakdown` 需要增加「已压缩」状态与摘要分段；`ContextRunTelemetry.ContextNotice` 已能承载「压缩失败走兜底」这类提示，无需新通道。

### 7.6 状态包的数据源已经存在

P2 的「最近工具动作 / 验证结果」不需要新的事件系统：`ChatMessage.ToolTrace`（P1 持久化）已经包含 `ToolId / Status / ExitCode / ArgsDigest / Summary / OutputChars / At`；`ToolTraceFormatter.RenderLine` 可直接复用做紧凑行，`AgentRunMetrics.ToolTrace` 是生成端来源。工作区结构则复用现有的工作区根路径与文件工具。

### 7.7 与溢出重试的交互

P1 的 400 自愈在 `AgentSessionRunner` 内、按半预算重试一次，并会重置 `EstimatedInputTokens` 以避免污染校准。P2 接入摘要后需要保证：

- **摘要调用本身的 token 不计入**当轮请求的 `EstimatedInputTokens`（否则校准因子会被摘要请求拉偏）；摘要消耗应单独记录（建议进 `ContextReport.SummaryTokens`）；
- 压缩失败（超时 / 500 / 格式不符）必须回退 L1，并允许 P1 的溢出重试继续兜底；
- `PluginRunner` 走 `Passthrough`，P2 的压缩/摘要不应作用于插件单轮调用。

### 7.8 测试需要跟着改的地方

- `ContextBuilderTests.超出预算且无法再裁时抛需要压缩` 在 P2 仍是有效契约（builder 层），保留；
- `AgentSessionRunnerTests` 需要新增「超限 → 压缩 → 重建」用例，现有「超限直接报错」用例在 `AutoCompact=false` 下继续成立；
- `ContextSegments` / `ContextReport` 加可选字段后，`ContextBudgetChatClientTests.Report()` 夹具与 `ContextBuilderTests.报告分段之和等于估算总量` 会自动兼容（可选参数），但 UI 的 `ContextBreakdown` 断言（目前无 bUnit 测试）需要人工检查。

### 7.9 对 P2 计划的其它提示

- P2 计划 2.6 的预估 0.5 人日偏乐观：`AgentSessionRunner.StreamAsync` 现在含「显式枚举器 + 溢出重试 + 校准收尾」三段控制流，插入压缩步骤时要避免在 `catch` 里 `yield`（C# 限制），建议把压缩放在 preflight 与首次枚举之间；
- P2 计划 3.4 的「分隔线」需要会话级 `SummaryThroughOrder` 才能定位；该字段 P2 加在 `ChatSession` 上后，`ChatRoom` 渲染消息列表时按 `Message.Order` 与它比较即可，不需要改消息模型；
- P1 的「当前输入永不淘汰」规则与 P2 压缩范围有交集：压缩的 `keepFrom` 必须 ≥ 最后 `KeepRecentTurns` 轮的起点，且永远不覆盖最后一条 user 消息。

## 8. 遗留与风险

| 项 | 影响 | 建议 |
| --- | --- | --- |
| 未做真正的 8K 物理窗口压测 | 基线是「8K 预算」而非「8K 模型」 | P6 小模型适配时用真实 8K 模型复测 |
| 工具循环中途裁剪未真机压测 | 单测覆盖逻辑，缺少真实工具轮数据 | P4/P5 有真实工具循环后补一次长工具轮采样 |
| 校准因子在低误差场景几乎不动 | 无法在真机演示「收敛」 | 逻辑已由单测覆盖；P6 换模型时观察 |
| `ContextReport` 位置参数较多 | P2 扩展时需要可选参数 | 见 §7.3 |
| 摘要列建好但未读写 | P2 需补领域字段与映射 | 见 §7.4 |

## 9. 提交

单次提交在 `feature/vibe-p1-context` 分支，包含代码、测试、基线工具与上述文档；未推送远端（按约定只留本地分支）。
