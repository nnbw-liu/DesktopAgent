# SDemo.DesktopAgent

[![CI](https://github.com/nnbw-liu/DesktopAgent/actions/workflows/ci.yml/badge.svg)](https://github.com/nnbw-liu/DesktopAgent/actions/workflows/ci.yml)

一个仓库，四个可独立发布的产品：**智能体客户端 + 常驻桌面外壳 + 三个桌面应用**。
一套 Razor 组件与业务服务，同时挂在 **WPF** 与 **.NET MAUI** 两个外壳里。

## 产品矩阵

| 产品 | 作用 | 状态 |
| --- | --- | --- |
| **DesktopAgent** | 智能体客户端：聊天、技能、沙箱、上下文预算。**唯一持有模型凭证的地方** | ✅ 已有 |
| **DesktopAssembly** | 常驻桌面外壳（托盘）：组件宿主、窗口管控、进程管家。不存业务数据 | 🚧 规划中（P1） |
| **sdemo.planner** | 待办 + 日历（todo 与 event 同库不同表） | 🚧 规划中（P2） |
| **sdemo.kb** | 知识库：文档版本链、块级差异、检索与引用追溯 | 🚧 规划中（P4） |
| **sdemo.meeting** | 会议：录音、本地转写、议题决议、待办候选 | 🚧 规划中（P6） |

三条跨产品不变式：**宿主无业务数据**、**应用零模型凭证**、**应用之间不直连**（只存 URI 引用，跨应用动作由智能体编排）。

## 30 秒上手

```powershell
# 需要 .NET 10 SDK + Microsoft Edge WebView2 Runtime
dotnet run --project src/Hosts/Wpf
```

```powershell
dotnet build SDemo.DesktopAgent.Wpf.slnf -c Release      # 桌面端构建（CI 门禁）
dotnet test  tests/SDemo.DesktopAgent.Core.Tests -c Release
dotnet test  tests/SDemo.DesktopAgent.UI.Tests   -c Release
```

完整运行方式（MAUI / Android 目标）、多宿主说明与全部工程清单见 [docs/guide/projects.md](docs/guide/projects.md)。

## 仓库布局

```
DesktopAgent/
├─ AGENTS.md                  ← 先读这个：工作区路由、五条铁律、禁区
├─ docs/
│  ├─ architecture/           ← 契约与设计（见下方索引）
│  │  └─ detail/              ← 超大设计文档（按需读）
│  ├─ features/               ← 使用与开发指南
│  ├─ guide/                  ← 由本 README 拆出的操作说明
│  └─ reports/                ← 阶段计划、验收报告、基准数据（data/）
├─ src/
│  ├─ App/         Agent 的编排（Core）与界面（UI，Razor 类库）
│  ├─ Hosts/       WPF 与 MAUI 宿主外壳
│  ├─ Contracts/   零实现契约层
│  ├─ Agents/      MAF + OpenAI 唯一出入口
│  ├─ Tools/       内置工具包（一个能力一个类库）
│  ├─ Storage/     EF Core + SQLite
│  ├─ Context/     预算 / 裁剪 / 压缩
│  ├─ Execution/   内循环与验证门
│  ├─ Sandbox/     沙箱（平台中立 + Windows 实现）
│  ├─ Plugins/     声明式插件（纯文本，无副作用）
│  ├─ Platform/    Windows 组合根
│  └─ (规划) Shell/ 与 Apps/ —— DesktopAssembly 与三个应用
├─ tests/                      ← Core / UI(bUnit) / Sandbox
├─ tools/  eval/               ← 评测与基准控制台
└─ .archify/                   ← 架构图产物（HTML，勿直接读）
```

## 文档索引

| 想知道 | 读这个 |
| --- | --- |
| **四个产品的规划 / 代码 / 数据分别在哪** | [product-map.md](docs/architecture/product-map.md) |
| 三块分工、已确认决策、硬约束 | [desktop-shell.md](docs/architecture/desktop-shell.md) |
| 组件清单 `widget.json` / 宿主控制协议 | [widget-contract.md](docs/architecture/widget-contract.md) · [host-protocol.md](docs/architecture/host-protocol.md) |
| 仓库怎么摆、怎么发版、怎么分工作区 | [repository-layout.md](docs/architecture/repository-layout.md) · [agent-workspace.md](docs/architecture/agent-workspace.md) |
| 产品范围与需求清单（DA/PL/KB/MT） | [sdemo-brand-blueprint.md](docs/architecture/sdemo-brand-blueprint.md) |
| 知识库的版本、差异与追溯 | [kb-versioning.md](docs/architecture/kb-versioning.md) |
| 会议应用（录音/转写/议题/候选） | [meeting-design.md](docs/architecture/meeting-design.md) |
| 待办 MCP 工具面 | [todo-mcp-surface.md](docs/architecture/todo-mcp-surface.md) |
| 工作台分期与里程碑 | [workbench-roadmap.md](docs/architecture/workbench-roadmap.md) |
| 界面与使用（聊天室、工作区、设置、托盘） | [guide/usage.md](docs/guide/usage.md) |
| 数据目录与存储约定 | [guide/storage.md](docs/guide/storage.md) |
| 模型接入、路由、小模型模式 | [guide/models.md](docs/guide/models.md) |
| 上下文预算、压缩、内循环、任务循环 | [guide/context.md](docs/guide/context.md) |
| 技能、插件、主题 | [guide/skills.md](docs/guide/skills.md) |
| 开发约定、回归验证、已知注意事项 | [guide/development.md](docs/guide/development.md) |
| 全部工程清单与多宿主细节 | [guide/projects.md](docs/guide/projects.md) |

## 约定

- **改代码前先读 [AGENTS.md](AGENTS.md)**：它给出工作区路由、五条铁律与"不要读"清单。
- `SDemo.DesktopAgent.Wpf.slnf` 是现有 CI 门禁；新产品落地时各自补 slnf。
- 契约（`widget.json` / 宿主协议 / MCP 工具面）变动时，必须同 PR 更新 `docs/architecture/` 与 JSON Schema。
