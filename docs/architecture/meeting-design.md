# 会议纪要应用（`sdemo.meeting`）

> 状态：设计稿，2026-10-29
> 关联：[工作台规划](workbench-roadmap.md) · [知识库版本与追溯](kb-versioning.md) · [待办 MCP 工具面](todo-mcp-surface.md)
> 修订：推翻早晚两版「会议是 planner 的一个模块」的判断（理由见 §1）

---

## 1. 为什么独立成应用

按 [工作台规划](workbench-roadmap.md) §2 的准入三问重新过一遍：

| 准入条件 | 会议是否满足 |
| --- | --- |
| 独立持久数据 | ✅ 会议记录、议题、转写片段、录音文件——都不是「计划」这个域的东西 |
| 独立后台生命周期 | ✅ 录音 + 转写 + 分析是长任务，可能跑几十分钟，不该挂在计划应用里 |
| 能被 AI 调用的能力面 | ✅ 检索历史会议、取某段的转写、取议题决议——planner 给不了 |

之前把它塞进 planner 的理由是「产出就是待办」，但那是**产出**的归属，不是**数据**的归属。按产出归应用会得出一个荒谬结论：凡是能产生待办的都该并进 planner（那知识库、聊天、甚至邮件都该并进去）。

真正的判断依据是：**一个周会同时横跨三个项目。** 会议不隶属任何项目，它是**横切**的。这才是它必须独立建模的原因。

## 2. 核心模型：会议不属于任何项目，topic 才属于

这是整个设计的第一性原理。

```
会议（mtg_meeting）—— 只有一个时间与参与人身份，不挂项目
  └─ 议题（mtg_topic）× N —— 每个议题各自指向不同的项目/文档/清单
       ├─ refs: [sdemo.kb/doc/…, sdemo.planner/list/…, sdemo.agent/workspace/…]
       ├─ 决议（mtg_decision）× N
       └─ 待办候选（mtg_action）× N
```

举例：周三的周会

| 议题 | 指向 | 产出 |
| --- | --- | --- |
| 1. 客户 A 的需求变更 | `sdemo.kb/doc/{需求文档}` | 决议 1 条 + 待办候选 2 条 |
| 2. 内部工具重构进度 | `sdemo.agent/workspace/{仓库}` | 决议 1 条 + 待办候选 3 条 |
| 3. 下周排期 | —（无具体项目） | 待办候选 1 条 |

三个议题互不相干，但它们属于同一场会议、同一份录音、同一份纪要。**如果把会议挂在某个项目上，第 2、3 个议题就会变成"别的项目的数据"，检索与权限全乱。**

引用一律是 URI 字符串（`sdemo.kb/doc/{id}`），会议应用**不直连**别的应用——这是 [规划](workbench-roadmap.md) §4.1 定下的不变式。

## 3. 数据模型

| 表 | 内容 | 要点 |
| --- | --- | --- |
| `mtg_meeting` | 会议头：标题、起止时间、类型（standup / weekly / 1:1 / review / ad-hoc）、地点、参与人、状态 | `status`: recording → transcribing → analyzing → ready |
| `mtg_topic` | 议题：序号、标题、摘要、`refs[]`、`decision_ids[]` | 序号决定纪要排版；refs 是跨项目唯一的连接方式 |
| `mtg_segment` | 转写片段：起止毫秒、说话人（可空）、`speaker_source`（manual / auto）、文本、置信度 | 时间轴对齐，用于"跳转到录音位置" |
| `mtg_decision` | 决议：正文、理由、负责人、状态（proposed / confirmed） | `confirmed` 才进纪要正式版 |
| `mtg_action` | **待办候选**：正文、负责人、期限、优先级、状态（candidate / intaken / rejected）、`planner_ref` | 见 §6，候选不等于待办 |
| `mtg_analysis` | Agent 分析存档：类型（summary / topics / decisions / actions / risks）、`model`、`prompt_version`、`content_json` | 见 §5，可重跑、可并存多版 |
| `mtg_artifact` | 存档件：录音 / 转写稿 / 附件 / 导出稿，`path` + `sha256` + `bytes` | 见 §4 |
| `mtg_link` | 与外部实体的引用（与 `topic.refs` 分开：这个是会议级的） | 例如 `planner/event/{id}` 表示这场会议对应哪个日程 |

与 planner 的分工：**重复的周会是 planner 里的 `event`（带 RRULE），会议记录是每次实例化的产物**。会议不实现重复规则，只在 `mtg_link` 里存 `planner/event/{id}`，由 planner 的日程负责"下周三还有一场"。

## 4. 存档三件套

你提的三件事正好对应三类产物，**必须分开存、分别可追溯**：

| 存档 | 形态 | 位置 | 保留 |
| --- | --- | --- | --- |
| **录音** | 原始音频（M4A/Opus，16kHz 单声道） | `%APPDATA%\SDemo\Apps\sdemo.meeting\meetings\{id}\audio\` | 可配置：永久 / 90 天 / 转写完成即删；随时一键导出 |
| **纪要与转写** | 结构化行（DB）+ 导出稿（Markdown/JSON） | `%APPDATA%\SDemo\Apps\sdemo.meeting\meeting.db` | 永久 |
| **Agent 分析** | `mtg_analysis` 行（带 model / prompt_version） | 同上 | 永久，可重跑叠加 |

**录音放应用数据目录，但必须可导出**：一场 1 小时的会议，Opus 约 15–20MB、WAV 超过 100MB。放在 `%APPDATA%\SDemo\Apps\sdemo.meeting\` 与其它数据同根，备份、迁移、卸载策略才统一（卸载保留数据、升级不动库）。

「藏得太深就找不回来」这个风险用两件事补：

1. 设置页提供**目录一键打开**，并把当前占用显示出来；
2. 选中任何一场会议都能**导出录音 / 导出纪要**，文件名带日期与标题，落盘到用户指定位置（默认桌面）。

如果需要录音直接可见（放进自己的网盘目录），设置里可把 `sdemo.meeting` 的数据根目录指到自定义位置——应用不强制，但默认不这么干：`%APPDATA%` 在域环境会随漫游配置同步到服务器，几十兆一场会议推到服务器上不合适。

**分析存档为什么要带 `model` 和 `prompt_version`**：模型换代后，同一场会议重新分析结果会不一样。如果只存一份最新结果，会出现"上次说的和现在说的不一致但查不到原因"。存成多条记录，用户可以对比、可以重跑、可以标注"以 v2 那次为准"。

这是你说的「Agent 分析存档」的真正含义：**不是存一份分析结果，是存分析的履历。**

## 5. 转写方案：v1 用本地 whisper，不引云端

这里有个绕不过去的矛盾：「应用零凭证」原则 vs 转写必须要模型。

先看两条路：

| 方案 | 凭证 | 隐私 | 成本 | 质量 |
| --- | --- | --- | --- | --- |
| 云端 ASR（各家语音 API） | ❌ 需要 Key | 音频上云 | 按时长计费 | 好 |
| 本地 whisper.cpp | ✅ 不需要 | 音频不出本机 | 一次性的算力 | 中上（中文可接受） |

**v1 选本地**，两个理由都是硬的：

1. **会议录音是最敏感的数据**——里面有客户名、报价、人事评价。把这类数据默认上云，是个会后悔的决定，哪怕云端质量更好。
2. **零凭证原则不能为了转写破例**。一旦会议应用自己配 Key，你就有了第二处密钥和第二套额度统计，而这正是我们想避免的。

代价也要说清楚：速度取决于模型与硬件，1 小时音频大致在「0.5× 到 0.1× 实时」这个量级，也就是 12 分钟到 1 小时；CPU 跑 large 模型会很慢，建议默认 `small` 或 `medium`，用户可选。转写是后台任务，慢可以接受，但不能让界面卡住。

**例外条款**（写进文档防止以后偷偷破例）：如果将来确实需要云端 ASR，必须由用户在 `sdemo.meeting` 自己的设置里显式开启并填 Key，界面上标红提示「音频将上传至第三方」。这是**用户主动选择的例外**，不是架构默认。

**说话人分离：v1 手动标注，接口预留自动。**

whisper 不做 diarization，自动分离需要 pyannote / sherpa-onnx 这类重模型，所以 v1 先把**位置留好**：

- 数据层现在就带两列：`mtg_segment.speaker` 与 `speaker_source`（`manual` / `auto`）；UI 只按 `speaker_source` 决定是否显示「自动识别」标记。
- 应用内定义 `IDiarizationProvider`（输入音频 + 已切好的片段，输出 speaker 标签），v1 注册一个空实现直接返回 `null`。
- 接上自动分离时**只替换这个实现**：数据库、UI、MCP 工具面都不用动。

手动标注的交互：转写片段按时间轴排列，点一段即可选/填说话人；支持「从此处起都算张三」的批量标注。周会 3–6 人，标一遍成本很低。

## 6. 从纪要到工作计划：候选 ≠ 待办

这是最容易做错的一步。**会议应用永远不直接创建待办。**

```
会议应用：产出 mtg_action（status=candidate）
   ↓ 用户点「安排到待办」
DesktopAgent：读 meeting_get_actions → 调 planner 的 todo_plan_from_text
   ↓
planner：产出草稿（ai_drafts）
   ↓ 用户审阅 ✓/✎/✕
planner：写入 todos，带 provenance
   ↓
meeting：把该候选标为 intaken，记 planner_ref
```

为什么绕这一圈：

1. 应用之间不直连（不变式）；
2. 会议的候选常有「说了但没定」的，直接进待办会污染清单；
3. 审阅这一步必须在待办应用的界面里——它才掌握现有清单，能判重、能合并。

摩擦成本怎么办：不要让用户手工复制粘贴。会议界面上的按钮直接开一个**深链** `sdemo.agent://arrange?meetingId={id}`，DesktopAgent 收到就自动跑上面的流程，把草稿卡片弹出来。用户感受到的是一次点击，架构上仍然没有应用直连。

## 7. 采集：两种模式

| 模式 | 场景 | 采集 | 注意 |
| --- | --- | --- | --- |
| `room` | 线下会议室 | 麦克风 | 远场拾音差，建议外接麦；v1 不做回声消除 |
| `online` | 腾讯会议 / 飞书 / Teams | WASAPI 环回 + 麦克风各一路 | 环回录对方，麦克风录自己；两路分别转写再按时间合并，避免"自己说话被录两遍" |

录音时在桌面组件上显示一个极简的「录音中 00:12:34」——这里正好用上 DesktopAssembly 的 `native` 组件（半透明、置顶、不抢焦点），跟待办小组件同一套机制。

## 8. MCP 工具面草案

| 工具 | 风险 | 审批 | 说明 |
| --- | --- | --- | --- |
| `meeting_search(query, since?, kind?)` | 低 | 否 | 全文检索纪要与转写 |
| `meeting_get(id)` | 低 | 否 | 会议头 + 议题结构 |
| `meeting_transcript(id, fromMs?, toMs?, topicId?)` | 低 | 否 | 按时间或议题取转写片段 |
| `meeting_actions(id, status?)` | 低 | 否 | 待办候选清单 |
| `meeting_decisions(id)` | 低 | 否 | 决议清单（默认只返回 confirmed） |
| `meeting_mark_action(id, plannerRef)` | 中 | 否 | 候选标记为已安排（不创建待办） |
| `meeting_tag_speaker(id, segmentIds, speaker)` | 中 | 否 | 人工标注说话人（写 `speaker_source=manual`） |
| `meeting_create(title, kind)` | 中 | 否 | 手工建一场会议记录 |
| `meeting_update_topic(id, topicPatch)` | 中 | 否 | 修议题标题/摘要/refs |
| `meeting_delete(id)` | **高** | **总是** | 软删，回收站 7 天 |
| `meeting_rerun_analysis(id, kind)` | 中 | 否 | 重跑分析（结果并存入 `mtg_analysis`） |
| `meeting_export(id, format)` | 低 | 否 | 导出 Markdown / JSON |

`meeting_update_topic` 里让 AI 改 `refs` 时要注意：模型容易把 `refs` 编造成不存在的路径。约定：**refs 只能来自工具返回的既有 id**，写入时应用侧校验存在性，不存在则拒绝并返回 `MTG_REF_NOT_FOUND`。

## 9. 磁盘与保留估算

| 场景 | 每月增量 |
| --- | --- |
| 每周 1 场周会，1h，Opus | ≈ 20MB/场 ≈ 80MB/月 |
| 转写文本 | ≈ 1MB/场（含片段切分） |
| 分析存档 | ≈ 50KB/场 × 条数 |

保留策略默认：录音 **90 天**，纪要/Analysis **永久**。用户可改成"录音永久"，应用在设置里显示当前占用。

## 10. 非目标

| 不做 | 原因 |
| --- | --- |
| 实时字幕 / 边说边转写 | 流式 ASR + 低延迟是另一套工程，v1 用"会后再转" |
| 说话人自动分离（diarization） | v1 用 `IDiarizationProvider` 的空实现 + 手动标注；自动分离排 P2，接上时只替换实现 |
| 与腾讯会议/飞书直接集成（读它们的 API） | 各家 API 不统一，v1 用环回录音这条路更通用 |
| 自动把纪要发布成知识库文档 | 跨应用写入必须经 DesktopAgent，见 §6 |
| 会议应用自己调云端模型做分析 | 违反零凭证原则；分析由 DesktopAgent 执行后写回 |
