# R1 可信度计划（上一轮建议中的 P3）

> 状态：**✅ 已实现（R1.1–R1.5）**。结果与偏差见 [r1-validation.md](r1-validation.md)；UI 手工清单见 [r1-ui-checklist.md](r1-ui-checklist.md)。
> 仓库里已有一个历史上的 P3（代码编辑工具链），为避免混淆，本文用 **R1** 命名。
> 前置：CI（`.github/workflows/ci.yml`）、P1 可靠性改造、P2 的 8K 口径调整。
> 目标：把“代码与单测通过”提升到“真机规模化可复现、UI 可回归、数据库可升级”。

## 1. 为什么需要 R1

当前能力面已经覆盖 M1–M4，但还有三类“可信度缺口”：

1. **规模化只到单测口径**：30 工具 / 20 技能 / 20 条归档 / 8K 长任务都没有真机复测；
2. **UI 回归靠手点**：占用环、压缩分隔线、验证徽标、引用 chip 没有自动化；
3. **数据库只做加列**：`EnsureCreated` + `RequiredColumns` 能补列，但表结构变更、索引、回填没有版本化迁移。

R1 不新增用户功能，只把这些缺口补上，让后续迭代敢改。

## 2. 工作流

### R1.1 规模化真机验收（2–3 天）

| 场景 | 做法 | 验收 |
| --- | --- | --- |
| 30 工具 / 8K | 用测试用工具包注册 30 个函数，走 `CapabilityRouter` + `CreateExposed`；记录真实 schema token | 工具段 ≤1.5K；请求能发出；`ContextBreakdown` 数字与实测一致 |
| 20 技能 | 生成 20 条长描述技能，走 `SkillIndexBuilder` + `load_skill` | 索引 ≤400 token；top5 命中目标技能；正文分页可续读 |
| 20 条归档 | 造 20 条归档会话 + FTS5，问「上次关于沙箱 ACL 的结论」 | 首条命中目标；引用 chip 点击能打开原会话 |
| 8K 长任务 | 用 4B/8K 跑单文件任务 A–E（P2 口径） | ≥3/5；连续两轮记录方差 |
| 32K 外部仓库 | 用 DeepSeek V4.1 Flash 跑 T01–T08 | 与 P1 基线对比，记录步数 / 失败类型 |

产物：`docs/reports/r1-validation.md` + `eval/results/r1/`。所有数字必须来自运行器输出，不能手填。

### R1.2 UI 回归（2 天）

- **bUnit** 覆盖纯组件逻辑：
  - `ContextMeter` / `ContextBreakdown` 的分段与阈值；
  - `CompactionDivider` 的展开 / pin；
  - `VerificationBadge` 的 dirty / verified / failed；
  - `ChatRoom` 的“无修改回灌”触发条件（用假 Runner）。
- **手动验收清单**（WebView2 里的真实交互，bUnit 覆盖不到）：
  - 占用环 >80% / >95% 颜色；
  - 压缩分隔线、原文弹层、单条 pin；
  - 引用来源 chip 跳转；
  - 模型选择器、附件拖拽、停止生成。
- 截图基线：至少 4 张（空态 / 有记录 / 设置 / 压缩），放 `docs/reports/ui-baseline/`，人工比对。

### R1.3 EF Core 迁移（2 天）

现状：`Database.EnsureCreated()` + `ChatStore.RequiredColumns` 自动补列；新增索引、改列类型、回填数据都没有版本化路径。

计划：

1. 为 `ChatDbContext` 建初始迁移（`InitialCreate`），把现有 `EnsureCreated` 路径切到 `Migrate()`；
2. 老库兼容：启动时先检测 `__EFMigrationsHistory` 是否存在；没有则用现有 `RequiredColumns` 补列，再 `Migrate()`；
3. 把 `ArchiveSchema` 的 FTS5 表改为迁移里的 `CREATE VIRTUAL TABLE IF NOT EXISTS`；
4. 加迁移测试：空库 / 老库 / 已迁移库三种输入。

风险：SQLite + FTS5 的迁移在 EF Core 里支持有限；必要时保留 `ArchiveSchema` 手写 SQL，只把普通表纳入迁移。

### R1.4 可观测性补全（1 天）

- 摘要请求的 input/output token 进入 `ContextReport`，分解面板显示“摘要成本”；
- 每轮记录真实审批次数（不是评测的自动审批），写进 `AgentRunMetrics`；
- 云端 token 成本按 provider / model 汇总，设置页只读展示；
- 路由模型独立选择 UI（当前只能改 `settings.json`）。

### R1.5 回归与发布（1 天）

- CI 增加 R1 的验证脚本入口（手动触发，避免每次都跑 30 工具 / 20 归档）；
- 更新 `README` 的已知限制与文档索引；
- 打 tag / 发 release notes（如果准备对外）。

## 3. 排期

| 天 | 内容 |
| --- | --- |
| D1–D3 | R1.1 规模化真机验收（先 8K 单文件，再 32K 外部仓库） |
| D4–D5 | R1.2 bUnit + 手动清单 + 截图 |
| D6–D7 | R1.3 EF 迁移 |
| D8 | R1.4 可观测性 + R1.5 回归 |

总计约 **8 人日**；可与 P2 的文档收尾并行。

## 4. 验收标准

- 30 工具 / 8K 请求真实发出，工具段 ≤1.5K，分解面板数字一致；
- 20 技能索引 ≤400 token，top5 命中，`load_skill` 可分页；
- 20 条归档检索首条命中，引用可跳转；
- 8K 单文件任务 ≥3/5；32K 外部仓库不低于 P1 基线；
- bUnit 覆盖 4 个组件，手动清单全绿；
- 空库 / 老库 / 已迁移库都能启动；迁移测试通过；
- CI 仍全绿，README 与文档索引更新。

## 5. 非目标

- 不做语义 / 向量检索（R1 只验证现有 FTS5 的规模化）；
- 不做多 Agent、LSP、云端沙箱；
- 不为了通过率改任务难度；任务规格要改必须单独记录原因；
- 不把评测的自动审批当成产品审批指标。

## 6. 风险

| 风险 | 影响 | 缓解 |
| --- | --- | --- |
| EF 迁移与 FTS5 不兼容 | 迁移卡住 | 普通表走迁移，FTS 保留手写 SQL |
| bUnit 覆盖不到 WebView2 | UI 回归仍有盲区 | 保留手动清单 + 截图基线 |
| 8K 长任务仍抖动 | 指标不稳定 | 双跑取多数，同时报告最差 |
| 30 工具测试夹具难维护 | 测试变慢 | 用生成器造工具描述，固定随机种子 |
