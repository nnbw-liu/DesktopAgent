# 四个产品的落地地图

> 状态：索引文档，2026-10-31（随产品推进更新）
> 用途：回答"这个产品的规划在哪、代码放哪、数据存哪、什么时候做"。
> 关联：[工作台规划](workbench-roadmap.md) · [品牌蓝图与需求](sdemo-brand-blueprint.md) · [仓库规划](repository-layout.md)

---

## 1. 一屏总表

| 产品 | 代码位置（待建） | 数据位置 | 设计文档 | 需求编号 | 分期 |
| --- | --- | --- | --- | --- | --- |
| **DesktopAssembly** | `src/Shell/` | `%APPDATA%\SDemo\DesktopAssembly\`（仅 `apps\` `widgets.json` `settings.json`，**无业务数据**） | [desktop-shell](desktop-shell.md) · [widget-contract](widget-contract.md) · [host-protocol](host-protocol.md) | DA-01–11 | P1 |
| **sdemo.planner** | `src/Apps/Planner/` | `%APPDATA%\SDemo\Apps\sdemo.planner\`（`planner.db` + `outbox\`） | [todo-mcp-surface](todo-mcp-surface.md) + [workbench-roadmap](workbench-roadmap.md) §3.1 | PL-01–14 | P2 |
| **sdemo.kb** | `src/Apps/Kb/` | `%APPDATA%\SDemo\Apps\sdemo.kb\`（`kb.db`） | [kb-versioning](kb-versioning.md) | KB-01–13 | P4 |
| **sdemo.meeting** | `src/Apps/Meeting/` | `%APPDATA%\SDemo\Apps\sdemo.meeting\`（`meeting.db` + `meetings\{id}\audio\`） | [meeting-design](meeting-design.md) | MT-01–14 | P6 |
| **共享层**（非产品） | `src/Shared/` | 无 | [repository-layout](repository-layout.md) §6 | — | M1 |
| **DesktopAgent**（已有） | `src/App` `src/Tools` 等 | `%APPDATA%\SDemo\DesktopAgent\` | [guide/projects](../guide/projects.md) | — | 已交付 |

进程级数据（端口 / token / 锁）一律在 `%LOCALAPPDATA%\SDemo\DesktopAssembly\runtime\`，不放漫游目录。

## 2. 数据与进程分布

```
%APPDATA%\SDemo\
├─ DesktopAgent\                 ← 已有：settings.json / chats\ / skills\ / plugins\
├─ DesktopAssembly\              ← 外壳（P1）
│  ├─ apps\<appId>\              ← 安装目录：app.json + widget.json + payload\
│  ├─ widgets.json               ← 组件布局（位置/层级/显隐）
│  └─ settings.json
└─ Apps\
   ├─ sdemo.planner\planner.db   ← todo + event（P2）
   ├─ sdemo.kb\kb.db             ← 版本链 + 块 + 索引（P4）
   └─ sdemo.meeting\             ← meeting.db + meetings\{id}\audio\（P6）

%LOCALAPPDATA%\SDemo\
├─ DesktopAssembly\runtime\      ← runtime.json（pid/端口/token），不可漫游
└─ logs\
```

**安装与数据分离**：程序在 `apps\`，数据在 `Apps\`；卸载不删数据，升级不动库。

## 3. 各产品：已定 / 未定 / 缺口

### DesktopAssembly（P1）

- **已定**：常驻托盘、按需拉起、单实例、空闲退出、崩溃退避重启；`widget.json` 字段与两种 renderer；宿主只做 6 个 Win32 窗口操作（禁止 `SetParent` 用于 native）；native 不支持贴桌面层；位置按 DIP + 显示器标识记忆。
- **未定**：组件数量上限（建议 4）、宿主自身是否要开机自启默认开。
- **缺口**：窗口管控的实现细节（`GetWindowRect` 回写、多屏拔插的迁移算法）目前只写在协议里，还没有单独的实现文档；**Win+D / `SC_MINIMIZE` 拦截行为必须实测**。

### sdemo.planner（P2）

- **已定**：`todo` 与 `event` 同库不同表；AI 只产草稿、人审阅后入库；删除软删 + 7 天回收站；高风险（删除/批量≥5/清空）不可关闭审批；ICS 只做导出 + 只读订阅；桌面组件 ≤8 条待办 + 快速新增。
- **未定**：清单（list）与标签的默认模型；重复规则（RRULE）的 UI 复杂度取舍。
- **缺口**：**详细设计文档还没有**（日历视图交互、ICS 映射细则、重复规则、组件布局）；`todo-mcp-surface.md` 还只有 `todo_*`，需要扩 `calendar_*` 并改名为 `planner-mcp-surface.md`。

### sdemo.kb（P4）

- **已定**：文档实体 + 不可变版本链（DAG）+ 块级内容寻址；块级与词级 diff；变更理由 `rationale` 三入口；三个索引面（当前态 / 历史态 / 变更事实）+ 查询路由；**不向量化差异**；引用存 `docId@version#block` 以支持影响面反查；零模型凭证。
- **未定**：文档的 `kind` 分类（note / reference / requirement / decision）默认行为；导入源优先级（md / docx / pdf 谁先）。
- **缺口**：MCP 工具面仍是草案（§10），需要随 P4 细化。

### sdemo.meeting（P6）

- **已定**：会议不隶属任何项目，用 `mtg_topic` 拆分议题、各自挂 URI；录音与转写全本地（whisper）；**候选 ≠ 待办**，转待办经 DesktopAgent 中转；分析存档带 `model` + `prompt_version` 可重跑；说话人 v1 手动标注 + `IDiarizationProvider` 预留自动；录音存应用数据目录但可导出。
- **未定**：云端 ASR 作为用户显式例外是否要提供；会议类型枚举是否够用。
- **缺口**：ASR 运行时（whisper 模型分发与校验）没有落地文档；`meeting_*` 工具面仍是草案（§8）。

## 4. 还缺哪几份设计文档

| 待写 | 服务于 | 什么时候需要 |
| --- | --- | --- |
| `planner-design.md`（event / 日历视图 / ICS / RRULE / 组件布局） | P2 | P2 开工前 |
| `planner-mcp-surface.md`（在 `todo-mcp-surface.md` 上扩 `calendar_*`） | P2 / P3 | P2 中后期 |
| `agent-mcp-client.md`（连 server、工具暴露预算、审批映射、断线降级） | P3 | P3 开工前 |
| `shared-foundation.md`（UUIDv7 / HLC / outbox 格式 / FTS 分词助手 / MCP 信封） | M1 | 建 `src/Shared` 时 |
| `sync-design.md`（op-log 格式、合并规则、冲突呈现） | P5 | P5 开工前 |
| `asr-runtime.md`（whisper 模型分发、校验、耗时档位） | P6 | P6 开工前 |

已写齐、可直接用的：`desktop-shell` / `widget-contract` / `host-protocol` / `kb-versioning` / `meeting-design` / `repository-layout` / `agent-workspace` / `sdemo-brand-blueprint` / `workbench-roadmap`。

## 5. 分期与依赖

```
M0 基建（已做）──► M1 Shared ──┬──► P1 Shell ──► P2 Planner ──► P3 Agent 接 MCP
                              │                                   │
                              └───────────────────────────────────┴──► P4 KB ──► P5 同步 ──► P6 Meeting
```

- **P1** 是唯一有技术不确定性的（窗口管控 + Win+D），先做它，失败就得改口径。
- **P3** 是横切的一步：DesktopAgent 学会连 MCP server、按风险审批、解析跨应用引用。它做完之后，P4/P6 的产品才真正"可用"。
- **P5** 可以在 P4 之后任意时点插入；schema 已经预留 `revision` / 墓碑 / outbox。

## 6. 想知道什么去哪找

| 问题 | 去哪 |
| --- | --- |
| 这个产品到底要做哪些功能 | [sdemo-brand-blueprint](sdemo-brand-blueprint.md)（DA / PL / KB / MT 编号） |
| 什么时候做、多少工作量 | [workbench-roadmap](workbench-roadmap.md) §6 |
| 代码放哪个目录、几个工程 | [repository-layout](repository-layout.md) §4 |
| 现在有没有开始建 | `pwsh tools/ws.ps1 list` 与 `workspaces/<id>.json` 的 `status` |
| 数据写在哪、能不能备份 | 本文 §2 |
| 组件清单长什么样、宿主怎么拉起 | [widget-contract](widget-contract.md) / [host-protocol](host-protocol.md) |
| 某个应用的工具有哪些、谁要审批 | 各产品的设计文档 + [todo-mcp-surface](todo-mcp-surface.md) §6 |
