# SDemo.DesktopAgent

[![CI](https://github.com/nnbw-liu/SDemo.DesktopAgent/actions/workflows/ci.yml/badge.svg)](https://github.com/nnbw-liu/SDemo.DesktopAgent/actions/workflows/ci.yml)

**智能体客户端**：聊天、技能、沙箱、上下文预算、工具链。
它是整个 SDemo 工作台里**唯一持有模型 Provider 与 API Key** 的地方；需要模型能力的其它应用都通过 MCP 请求它。

> 本仓是 [SDemo 主仓](https://github.com/nnbw-liu/SDemo) 的子模块。产品矩阵、跨产品文档、其余四个产品在父仓里。

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
├─ AGENTS.md                  ← 先读这个：目录地图、命令、铁律、坑
├─ docs/
│  ├─ architecture/           ← Agent 自己的设计（跨产品文档在 ../docs/）
│  │  └─ detail/              ← 超大设计文档（按需读）
│  ├─ features/               ← 使用与开发指南
│  ├─ guide/                  ← 操作说明
│  └─ reports/                ← 阶段计划、验收报告、基准数据（data/）
├─ src/
│  ├─ App/         Agent 的编排（Core）与界面（UI，Razor 类库）
│  ├─ Hosts/       WPF 与 MAUI 宿主外壳
│  ├─ Contracts/   零实现契约层
│  ├─ Agents/      MAF + OpenAI 唯一出入口
│  ├─ Tools/       内置工具包（一个能力一个类库）
│  ├─ Storage/     EF Core + SQLite 会话库
│  ├─ Context/     预算 / 裁剪 / 压缩
│  ├─ Execution/   内循环与验证门
│  ├─ Sandbox/     沙箱（平台中立 + Windows 实现）
│  ├─ Plugins/     声明式插件（纯文本，无副作用）
│  ├─ Platform/    Windows 组合根
│  └─ (不在本仓)   DesktopAssembly 与三个应用各自成仓
├─ tests/                      ← Core / UI(bUnit) / Sandbox
├─ tools/  eval/               ← 评测与基准控制台
└─ .archify/                   ← 架构图产物（HTML，勿直接读）
```

## 文档索引

| 想知道 | 读 |
| --- | --- |
| Agent 的目录、命令、常见坑 | [AGENTS.md](AGENTS.md) |
| 界面与使用（聊天室、工作区、设置、托盘） | [docs/guide/usage.md](docs/guide/usage.md) |
| 数据目录与存储约定 | [docs/guide/storage.md](docs/guide/storage.md) |
| 模型接入、路由、小模型模式 | [docs/guide/models.md](docs/guide/models.md) |
| 上下文预算、压缩、内循环、任务循环 | [docs/guide/context.md](docs/guide/context.md) |
| 技能、插件、主题 | [docs/guide/skills.md](docs/guide/skills.md) |
| 开发约定、回归验证、已知注意事项 | [docs/guide/development.md](docs/guide/development.md) |
| 全部工程清单与多宿主细节 | [docs/guide/projects.md](docs/guide/projects.md) |
| **跨产品**（蓝图、路线图、仓规划、组件/宿主契约） | [../docs/](../docs) |

## 约定

- **改代码前先读 [AGENTS.md](AGENTS.md)**：目录地图、命令、五条铁律与"不要读"清单。
- `SDemo.DesktopAgent.Wpf.slnf` 是现有 CI 门禁；本仓不负责其它产品的构建。
- 本仓只改 Agent 自己的文档；`widget.json` / 宿主协议 / MCP 工具面等跨产品契约在主仓 `docs/contracts/` 维护。
- 子模块改动要在本仓提交并推送，再回主仓 `git add DesktopAgent` bump 指针。
