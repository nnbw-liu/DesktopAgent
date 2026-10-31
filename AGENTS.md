# AGENTS · SDemo 工作台（单仓多产品）

> 这是**路由文件**，不是说明书。细节在各自工作区的 `AGENTS.md`。
> 单次会话请只读你所在工作区的入口，不要把整仓读完。

## 这是什么仓

一个仓库，四个可独立发布的产品：

| 产品 | 状态 | 工作区入口 |
| --- | --- | --- |
| DesktopAgent（智能体客户端） | ✅ 已有 | `src/App/AGENTS.md` |
| DesktopAssembly（桌面外壳） | 🚧 规划中 | `src/Shell/AGENTS.md`（待建） |
| sdemo.planner（待办 + 日历） | 🚧 规划中 | `src/Apps/Planner/AGENTS.md`（待建） |
| sdemo.kb（知识库） | 🚧 规划中 | `src/Apps/Kb/AGENTS.md`（待建） |
| sdemo.meeting（会议） | 🚧 规划中 | `src/Apps/Meeting/AGENTS.md`（待建） |
| 跨产品契约 / 共享层 | ✅ 已有(部分) | `src/Shared/AGENTS.md`（待建） |

## 铁律（改代码前先看这五条）

1. **契约改动要单独提**：`widget.json`、宿主协议、MCP 工具面、URI 规范变了，必须同时改 `docs/architecture/*.md` 与 JSON Schema，不能只改代码。
2. **产品之间零引用**：`App.*` 之间不得互相 `ProjectReference`；`Agent` 不依赖任何 `App.*`；应用经 MCP 访问，不直连。
3. **应用零模型凭证**：只有 DesktopAgent 持有 Provider 与 Key。任何应用里出现 `ApiKey` / `IChatClient` 都是错的。
4. **宿主无业务数据**：`Shell.*` 里不得出现 `DbContext`、SQL、业务表。
5. **删除一律软删**：`deleted_at` + 回收站，不做物理删除；MCP 写操作要落审计。

## 常用命令

```powershell
dotnet build SDemo.DesktopAgent.Wpf.slnf -c Release      # 现有 Agent 门禁
dotnet test tests/SDemo.DesktopAgent.Core.Tests -c Release
dotnet test tests/SDemo.DesktopAgent.UI.Tests   -c Release
pwsh tools/check-repo-guards.ps1                          # 文档/布局守卫（改了文档就跑）
```

新产品的 slnf 建好后各自补一条（见 `docs/architecture/repository-layout.md` §7）。

## 不要读（会白烧 token）

| 路径 | 原因 |
| --- | --- |
| `docs/architecture/detail/` | 三份超大设计文档（122 KB / 97 KB / 73 KB）；只在改对应模块时定向读 |
| `.archify/*.html` | 74 万字节的生成物；要改图请改同目录 `candidate.json` 重跑 |
| `docs/reports/` 与 `docs/reports/data/` | 历史验收报告与基准 JSON，只在查历史结论时按文件名定向读 |
| `TestResults/` | 测试日志，看失败请直接重跑单测 |
| `*.Designer.cs` / `obj/` / `bin/` | 构建产物 |

> 基准脚本（`tools/r1-validate`）已改为把工作区与日志写到仓外 `_scratch/`，不要把它指回仓内。

搜索请限定目录：`grep pattern src/Apps/Planner`，不要全仓 grep。

## 文档地图（按需读，不要通读）

| 想知道 | 读这个 |
| --- | --- |
| 三块分工、硬约束 | `docs/architecture/desktop-shell.md` |
| 组件清单、宿主协议 | `widget-contract.md` / `host-protocol.md` |
| 仓库怎么摆、怎么发版 | `repository-layout.md` |
| 产品范围与需求 | `sdemo-brand-blueprint.md` |
| 知识库版本模型 | `kb-versioning.md` |
| 会议应用 | `meeting-design.md` |
| 待办工具面 | `todo-mcp-surface.md` |
| 界面使用 / 数据存储 / 模型 / 上下文 / 技能插件 / 开发约定 | `docs/guide/*.md`（由 README 拆出，按需读单篇） |
| 全部工程清单、多宿主、运行方式 | `docs/guide/projects.md` |
