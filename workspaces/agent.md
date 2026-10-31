# 工作区：DesktopAgent（智能体客户端）

> 状态：✅ 已有（20 个 src 工程 + 3 个测试工程）
> 上游：`AGENTS.md`（仓库路由） · `docs/architecture/desktop-shell.md`（三块分工）

## 职责

聊天客户端与智能体运行时：模型接入、上下文预算与压缩、工具链、插件、执行沙箱、会话存储、WPF/MAUI 宿主。

**它也是整个工作台里唯一持有模型 Provider 与 API Key 的地方。** 任何一个新应用需要模型能力，都应当通过 MCP 请求它，而不是自己配 Key。

## 目录地图（只列会改的）

| 目录 | 内容 | 什么时候进 |
| --- | --- | --- |
| `src/Contracts/` | 纯契约：模型、设置 DTO、跨模块接口 | 加字段/加接口 |
| `src/App/Core/` | 编排与组合根（`AgentChatService` / `AgentSessionRunner` / 组合根） | 改流程 |
| `src/App/UI/` | 全部 Razor 组件 + `wwwroot`（css/js） | 改界面 |
| `src/Context/` | 预算、裁剪、压缩、状态包 | 改上下文 |
| `src/Tools/` | 每个工具包一个类库（files/search/edits/todo/windows/office/memory/skills） | 加工具 |
| `src/Storage/` | EF Core + SQLite 会话库与迁移 | 改表 |
| `src/Plugins/` | 声明式插件：清单 + 技能 md，**无副作用** | 加插件 |
| `src/Agents/` | MAF / OpenAI 唯一出入口（`IAgentRuntime`） | 换模型接入 |
| `src/Execution/` `src/Sandbox/` | 内循环、验证门、沙箱（含 Windows 实现） | 改执行 |
| `src/Hosts/` `src/Platform/` | WPF / MAUI 宿主、Windows 组合根 | 改外壳 |
| `tests/` | Core / UI(bUnit) / Sandbox | 改完补测试 |

## 必读（按改动类型挑一份，不要全读）

| 改什么 | 读 |
| --- | --- |
| 上下文/压缩 | `docs/architecture/context-budget.md`、`context-compaction.md` |
| 代码编辑/工具 | `docs/architecture/code-editing.md`、`capability-routing.md` |
| 沙箱/执行 | `docs/architecture/detail/sandbox-development-manual.md`（大，定向读） |
| 插件 | `docs/architecture/detail/plugin-system.md`（大，定向读）+ `docs/features/plugin-authoring.md` |
| 回归与指标 | `docs/reports/r1-validation.md` |

## 命令

```powershell
dotnet build SDemo.DesktopAgent.Wpf.slnf -c Release
dotnet test  tests/SDemo.DesktopAgent.Core.Tests -c Release     # 766 个
dotnet test  tests/SDemo.DesktopAgent.UI.Tests   -c Release     # 154 个
```

## 常见坑

1. **别把应用逻辑写进 Agent。** 待办/知识库/会议的领域规则属于各自应用；这里只保留「会话计划」的 `todo_write`。
2. **改 DB 必须同时改迁移与 `ChatStore.RequiredColumns`**（老库靠它补列）。
3. **上下文只经 `ContextBuilder` 一条路**；不要在 UI 或工具里另拼消息列表。
4. **两份 `TodoItem` 语义不同**：`Tools.Todo` 是会话计划，`sdemo.planner` 的 todo 是持久待办，不要合并。
5. `src/App/SDemo.DesktopAgent.UI/wwwroot/` 里的 js 改动不会触发重新编译，需重建前端资源才会生效。
