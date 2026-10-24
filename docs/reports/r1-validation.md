# R1 可信度验收报告

> 日期：2026-10-23　范围：R1.1 规模化真机验收、R1.2 UI 回归、R1.3 EF Core 迁移、R1.4 可观测性、R1.5 回归与发布。
> 计划：[r1-reliability-plan.md](r1-reliability-plan.md)　手测清单：[r1-ui-checklist.md](r1-ui-checklist.md)　截图基线：[ui-baseline/](ui-baseline/)
> 原则：报告里的数字全部来自运行器输出；跑不了的部分写清楚原因，不用估算值冒充。

## 1. 结论摘要

| 验收项 | 结果 | 证据 |
| --- | --- | --- |
| 30 工具 / 8K：工具段 ≤1.5K、请求可组装、分解面板一致 | ✅ 493 / 1500 token | `eval/results/r1/summary.json` |
| 20 技能：索引 ≤400 token、top5 命中、正文可续读 | ✅ 333 token、top1 命中 | 同上 |
| 20 条归档：首条命中目标、引用可跳转、正文可续读 | ✅ 首条 `arch-00` | 同上 |
| 8K 单文件任务 A–E ≥3/5（双跑） | ❌ 2/5、2/5（未达标；C/E 稳定，A/B/D 失败于编译或符号缺失） | `eval/results/r1/8k-round1/`、`8k-round2/` |
| 32K 外部仓库不低于 P1 基线 | ⚠️ 部分完成：实际跑到的 4 个任务 4/4 通过；T05–T08 被账号周配额截断（429） | `eval/results/r1/32k-round1/` |
| bUnit 覆盖 4 个组件 + ChatRoom 回灌 | ✅ 29 个用例 | `tests/SDemo.DesktopAgent.UI.Tests` |
| 手动清单 + 4 张截图基线 | ✅ 清单 30 项表格条目 + 回归检查 / 4 张 PNG | `r1-ui-checklist.md`、`ui-baseline/` |
| 空库 / 老库 / 已迁移库都能启动 | ✅ 7 个迁移测试 | `ChatStoreMigrationTests` |
| CI 全绿、README 与文档索引更新 | ✅ | `.github/workflows/`、`README.md` |

整体：R1.2–R1.5 达标；R1.1 的离线三项（工具 / 技能 / 归档）与 `--live` 请求达标，**8K 真机双跑 2/5 未达 ≥3/5**，32K 实际跑到的 4 个任务全过、其余被账号配额截断。未达标项已写清原因与下一步，没有靠改任务难度凑数。

## 2. R1.1 规模化验收

运行器：`tools/r1-validate`（离线、固定随机种子；`--live` 才发一次真实请求）。

```powershell
dotnet run --project tools/r1-validate -c Release -- --out eval/results/r1
```

### 2.1 30 工具 / 8K

用测试工具包注册 30 个函数（3 种参数形状、固定种子 `20261023`），走真实的 `CapabilityRouter.Route` + `AgentToolRegistry.CreateExposed`，再按与 `AgentChatService` 相同的方式测量 schema token。

| 指标 | 值 |
| --- | --- |
| 注册工具数 | 30 |
| 8K / Full 路由后暴露 | 6（5 个真实工具 + `search_capabilities`） |
| 工具 schema 实测 | **493 token**（上限 1500） |
| 全量暴露对照 | 2658 token（说明路由不是装饰） |
| 路由估算 | 876 token（偏保守，不低报） |
| `ContextReport.WithRouting` 回填后 | 工具段 493 = 实测值；各段之和 = 总估算 |

> 请求组装：`ContextBuilder.Build` 产出 522 token 的 8K 请求（含 493 工具段），结构与预算校验通过。
> `--live`（本轮用本机 `local / qwen3.5-4b`）把同一批工具 schema 直接 POST 到 OpenAI 兼容接口：**HTTP 200，6 个工具，29.7s**（4B 首包较慢，但请求确实发出并被服务端接受）。

### 2.2 20 技能

| 指标 | 值 |
| --- | --- |
| 技能数 / 索引条数 | 20 / 5 |
| 索引 token | **333**（上限 400） |
| top1 | `sandbox-acl-report`（目标技能） |
| 目标是否在 top5 | 是 |
| 正文分页 | 首页 1840 字符 → 续读 756 字符（offset 由返回给出） |

技能描述按“长描述”生成（每条 40–60 个中文字），确保索引仍有预算余量；`load_skill` 的正文按 `MidTurnToolResultChars` 分页。

### 2.3 20 条归档

造 20 条归档会话（目标会话 + 19 条“沙箱试跑”干扰项），写入工作区库的 FTS5 索引，再通过 **真实的 `search_workspace_memory` 工具** 提问。

| 指标 | 值 |
| --- | --- |
| 归档会话数 | 20 |
| 查询 | 上次关于沙箱 ACL 的结论 |
| 命中数 | 1 |
| 首条 | `沙箱 ACL 结论`（`arch-00`） |
| 引用 chip 数据源（`Metrics.Citations[0]`） | `arch-00` |
| 检索结果长度 | 169 字符 / 70 token |
| 正文分页 | 首页 1995 字符，`next=1800` |

“引用 chip 点击”属于 WebView2 交互，自动化只验证到 `Metrics.Citations` 的会话 id 正确（界面就是用它渲染 chip 并回调打开），点击跳转在手动清单 §4 覆盖。

### 2.4 8K 单文件任务 A–E

配置：`local / qwen3.5-4b`、Compact、8K、`P5_REPAIR=1`、任务间快照还原（`P5_ISOLATE=1`）、单轮 240s 超时。

| 任务 | Round 1 | Round 2 | 失败类型 |
| --- | --- | --- | --- |
| A 设置页开关（双文件） | ❌ 31 步 / 2 loops | ❌ 34 步 / 4 loops | R1：编译错误（AppSettings.cs）；R2：构建通过但缺验收符号 |
| B `list_files` 增加 `maxEntries` | ❌ 21 步 / 2 loops | ❌ 20 步 / 2 loops | R1：编译错误；R2：构建通过但参数/验收符号缺失 |
| C 高风险命令测试 | ✅ 4 步 / 0 loops | ✅ 5 步 / 0 loops | — |
| D 验证失败格式测试 | ❌ 21 步 / 2 loops | ❌ 28 步 / 2 loops | 测试项目编译失败（两次） |
| E npm 工作区命令探测测试 | ✅ 7 步 / 0 loops | ✅ 13 步 / 0 loops | — |
| **合计** | **2/5** | **2/5** | 方差 0 |

**结论：未达到计划里的 ≥3/5。** 不调整任务难度，记录真实结果。观察：

- C/E 这类“单文件 + 行为级验收”的任务两轮都稳定通过；
- A/B 是“双文件 + 指定符号”的强约束任务，4B 能读对文件，但写入的符号 / 位置不满足验收——这与 P1 报告里“4B 瓶颈在补丁锚点”一致；
- D 两次都停在测试项目编译，说明模型写出的测试代码本身不合法；
- 与 P6 的 3/5 不能直接比较：P6 当时 A/B 的旧路径（`src/SDemo.DesktopAgent.Core/...`）在当前仓库结构里已不存在；本轮先把 harness 路径修正到 `Contracts` / `Tools.Files`，任务描述与验收标准未改。

> 超时设置：4B 在任务 D 上曾出现长时间不结束的生成；本轮给单轮加了 240s 上限（超时按失败计），避免整轮评测挂死。

### 2.5 32K 外部仓库

同一镜像、同一 commit（`commandline@b0b0ec9`）、同一配置（`deepseek-v4.1-flash-32k.json`），单轮：

| 任务 | 结果 | 步数 | 耗时 | 说明 |
| --- | --- | --- | --- | --- |
| T01 拒绝非正帮助宽度 | ✅ | 16 | 25.3s | 与 P1 基线一致 |
| T02 IsParsed | ✅ | 17 | 18.9s | P1 为 16 步 |
| T03 HelpText 宽度 | ✅ | 16 | 26.5s | P1 为 17 步 |
| T04 转义引号 | ✅ | 16 | 41.6s | 与 P1 一致 |
| T05 PreferShortName 测试 | ❌ | 6 | 5.4s | **HTTP 429 `AccountQuotaExceeded`**（配额 2026-10-26 重置），不是模型失败 |
| T06 SplitArgs 测试 | ❌ | 0 | 0.3s | 同上 |
| T07 NeedsQuotes 重构 | ❌ | 0 | 0.6s | 同上 |
| T08 SkipDefault 工厂 | ❌ | 0 | 0.4s | 同上（验收里 T08 隐藏测试因产品代码未改而编译失败） |

**结论**：本轮实际跑起来的 4 个任务 **4/4 全部通过**，步数与 P1 基线同量级；余下 4 个任务被账号周配额截断，不能用来判断是否“低于 P1 基线”。恢复配额后可直接重跑：

```powershell
dotnet run --project tools/AgentEval -c Release -- `
  --repo F:\AI\Code\SDemo\p7-bench\commandline `
  --commit b0b0ec9f5470bacc0823c013a599ba3b4e115900 `
  --config eval/configs/deepseek-v4.1-flash-32k.json `
  --tasks eval/tasks --out eval/results/r1/32k-round1
```

## 3. R1.2 UI 回归

- **bUnit**（`tests/SDemo.DesktopAgent.UI.Tests`，29 个用例）：
  - `ContextMeterTests`：<80 / ≥80 / ≥95 三档、数字格式、展开/关闭明细、无报告时禁用；
  - `ContextBreakdownTests`：10 个分段齐全、条带宽度之和 ≈100%、压缩成本行、省略/附件/pin/裁剪/溢出/路由提示；
  - `CompactionDividerTests`：条数 / token / 兜底标记、点击触发 `OnOpen`；
  - `VerificationBadgeTests`：dirty、验证失败（首条问题）、已验证隐藏、点击运行验证、无命令提示；
  - `ChatRoomRepromptTests`：用假 Runner 走真实 `HandleSend → GenerateAsync` 路径，验证「只读工具 + 无编辑」时最多回灌 3 次、有成功编辑不打扰、Compact 不打扰、纯问答不打扰。
- **手动清单**：`r1-ui-checklist.md`，覆盖占用环颜色、压缩分隔线 / 原文弹层 / pin、引用 chip、模型选择器 / 拖拽 / 停止生成、设置页新增项，共 30 项表格条目 + 回归检查。
- **截图基线**：`ui-baseline/01-empty.png`、`02-chat.png`、`03-settings.png`、`04-compaction.png`；由 bUnit 渲染真实组件 HTML，再用系统 WebView2 运行时截图（同一份 `app.css`，不含 WPF 外壳）。

## 4. R1.3 EF Core 迁移

| 改动 | 文件 |
| --- | --- |
| 初始迁移 | `src/Storage/SDemo.DesktopAgent.Storage/Migrations/*_InitialCreate.cs` |
| 第二轮迁移（用量归属 / 审批次数） | `Migrations/*_AddMessageProviderModel.cs` |
| 设计时工厂 | `ChatDbContextFactory.cs` |
| 打开库的版本化升级 | `ChatDatabaseSchema.cs` |
| `EnsureCreated` 移除 | `ChatStore.OpenDatabaseAsync` |

行为：

1. 新库直接 `Migrate()` 建表 + 写 `__EFMigrationsHistory`；
2. 老库（`EnsureCreated` 时代、没有历史表）：先按旧列清单补列，再把 `InitialCreate` 记为已应用（基线化），之后只跑真正新增的迁移；
3. 表不全（只剩一张表）时不做基线化，交给 `Migrate()` 报明确错误，不静默改库；
4. FTS5 知识库表**不放进迁移**：`CREATE VIRTUAL TABLE` 在 EF 迁移里无法优雅降级，仍由 `ArchiveSchema` 在迁移后按需创建——这正是计划 §6 给出的缓解方案。

测试（`ChatStoreMigrationTests`，7 个）：空库建表 + 索引 + 历史表；老库补列后数据不丢；老库升级后可继续写；新增列（provider/model/审批）可读写；重复打开不重复应用迁移；FTS5 可用；新库与老库升级后的列集合一致。

## 5. R1.4 可观测性

| 项 | 实现 | 测试 |
| --- | --- | --- |
| 摘要成本 | `SummaryResult.InputTokens/OutputTokens`（输入按实际提示估算、输出按最终摘要）→ `CompactionOutcome` → `ContextReport.SummaryInputTokens/OutputTokens`，分解面板显示「摘要成本（累计）」 | `ContextSummarizerTests`、`ContextCompactorTests`、`ContextBreakdownTests` |
| 真实审批次数 | `ApprovalScope`（AsyncLocal）+ `ToolApprovalService` 在请求真正上屏时计数 → `AgentRunMetrics.ApprovalCount` → 消息落库并在气泡显示「审批 N 次」；评测的自动审批不经过审批服务，不计入 | `ApprovalScopeTests`、`ChatRoomRepromptTests`（间接） |
| 云端成本汇总 | `UsageCostService` 按 provider / model 汇总已落库 token；`ModelEntry` 新增 `InputPricePerMillion` / `OutputPricePerMillion`，未配价只显示「未配价」；设置 → 模型只读展示 | `UsageCostServiceTests` |
| 路由模型 UI | 设置 → 上下文新增「路由模型」选择 + 超时；写 `AgentSettings.RoutingProviderId/RoutingModelId`，可回退摘要模型 / 当前会话模型 | 手动清单 §6.1 |

> 摘要成本是**估算**：`ISummaryChatTransport` 只返回文本，没有 usage；输入按 system + user 提示走同一个 `HeuristicTokenCounter`，输出按最终摘要文本。报告里标注为“成本（累计）”，不冒充服务端账单。

## 6. R1.5 回归与发布

- CI：`build-and-test` 增加 bUnit 步骤；新增 `.github/workflows/r1-validation.yml`（手动触发，跑确定性三项并上传 `eval/results/r1-ci`）。
- 文档：README 增加「可信度与回归（R1）」段与命令；R1 计划状态更新；`eval/results/README.md` 补 `r1/` 说明；本报告 + 手测清单 + 截图基线 README。
- 发布：未打 tag / 未发 release notes（项目尚未对外；如需要，把本报告 §1 作为 release notes 草稿）。

## 7. 测试与构建

| 项 | 结果 |
| --- | --- |
| `dotnet build SDemo.DesktopAgent.Wpf.slnf -c Release` | 0 警告 / 0 错误 |
| Core 单测 | 648 通过 / 0 失败 |
| UI（bUnit） | 29 通过 / 0 失败 |
| Sandbox 单测 | 未跑（依赖 AppContainer / Windows Sandbox；CI 保持非阻塞） |

## 8. 偏差与遗留

1. **8K 未达到 ≥3/5**：双跑都是 2/5（C/E 通过，A/B/D 失败）。R1 不为了通过率改任务难度，也不把原因归到“模型不行”就结束：A/B/D 的失败都能定位到“写出的代码 / 测试不合法或符号缺失”，下一步应把 8K 出口改成行为级验收（像 C/E），或把 A/B 这类双文件任务移到 32K 档位。
2. **32K 真机被账号配额截断**：`火山 / deepseek-v4.1-flash` 在 T05 开始返回 `HTTP 429 AccountQuotaExceeded`（配额 2026-10-26 重置），T05–T08 的失败是环境原因，**不能算模型能力回退**；已保留 `verdict.json` 原始错误，恢复配额后用同一条命令重跑即可补齐。
3. **8K harness 的三处历史偏差**：`tools/p5-bench` 的任务 A/B 文件路径还停留在“Settings 在 Core、FileToolPack 在 Core”的旧结构；A–E 串行跑会互相污染；4B 偶发长时间不结束的生成。R1 修正了路径、补了 `WorkspaceSnapshot`（`P5_ISOLATE=1`）与单轮超时（`P5_TIMEOUT`），任务描述与验收标准没变。
4. **截图是组件渲染基线**：bUnit 渲染 + WebView2 截图，不含 WPF 外壳；外壳内的真实交互仍靠手动清单。计划本来就允许这个盲区，这里只是把边界写清楚。
5. **摘要成本是估算**；真实审批数只统计产品审批服务，评测自动审批为 0——这是有意的口径，避免把评测成本当成产品指标。
6. **FTS5 不进 EF 迁移**：保留手写 `CREATE VIRTUAL TABLE IF NOT EXISTS`，迁移只覆盖普通表。
7. **成本金额需要手动配价**：`settings.json` 里给模型补 `inputPricePerMillion` / `outputPricePerMillion` 后才显示金额；不内置厂商价格表，避免价格过期误导。

## 9. 下一步

- 配额恢复（2026-10-26）后补跑 32K T05–T08，更新 §2.5 与 `eval-matrix.md`。
- 8K 口径复盘：把“双文件 + 指定符号”任务从 8K 出口移出，或换 7B 复测；在复测前不把 2/5 写成能力上限。
- 把 8K 双跑脚本接进一台常开的 Windows 机器（LM Studio + WebView2），用 `r1-validation.yml` 的确定性部分做门禁、真机跑做周报。
- 若后续要对外，再把 §1 拆成 release notes，并给 R1 打 tag。



