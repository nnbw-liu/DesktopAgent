# AGENTS · DesktopAgent 子仓

> 这是**路由文件**，不是说明书。跨产品文档在父仓 `../docs/`。
> 一次会话只读你要动的目录的文档，不要把 `docs/architecture/detail/` 通读。

## 这是什么仓

智能体客户端：聊天、技能、沙箱、上下文预算、工具链、会话存储、WPF/MAUI 宿主。
**它是整个工作台里唯一持有模型 Provider 与 API Key 的地方**；其它应用需要模型能力时通过 MCP 请求它，不自己配 Key。

## 目录地图（只列会改的）

| 目录 | 内容 | 什么时候进 |
| --- | --- | --- |
| `src/Contracts/` | 纯契约：模型、设置 DTO、跨模块接口 | 加字段/加接口 |
| `src/App/Core/` | 编排与组合根（`AgentChatService` / `AgentSessionRunner`） | 改流程 |
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
| 跨产品（组件契约、宿主协议、蓝图、路线图） | 父仓 `../docs/` |

## 命令

```powershell
dotnet build SDemo.DesktopAgent.Wpf.slnf -c Release
dotnet test  tests/SDemo.DesktopAgent.Core.Tests -c Release     # 766 个
dotnet test  tests/SDemo.DesktopAgent.UI.Tests   -c Release     # 154 个
```

## 铁律

1. **别把应用逻辑写进 Agent**：待办/知识库/会议的领域规则属于各自应用；这里只保留"会话计划"的 `todo_write`。
2. **只此一处持有模型凭证**：应用里出现 `ApiKey` / `IChatClient` 都是错的。
3. **契约改动跨仓**：`widget.json`、宿主协议、MCP 工具面变了，先改父仓 `../docs/contracts/`，再改代码。
4. **删除一律软删**：会话侧也一样，不做物理删除。
5. **Agent 不依赖任何 App 工程**：跨仓引用在编译期就会失败，不要试图加。

## 常见坑

1. **改 DB 必须同时改迁移与 `ChatStore.RequiredColumns`**（老库靠它补列）。
2. **上下文只经 `ContextBuilder` 一条路**；不要在 UI 或工具里另拼消息列表。
3. **两份 `TodoItem` 语义不同**：`Tools.Todo` 是会话计划，`sdemo.planner` 的 todo 是持久待办，不要合并。
4. `src/App/SDemo.DesktopAgent.UI/wwwroot/` 里的 js 改动不会触发重新编译，需重建前端资源才会生效。

## 不要读

| 路径 | 原因 |
| --- | --- |
| `docs/architecture/detail/` | 三份 122 KB / 97 KB / 75 KB 设计文档，只在改对应模块时定向读 |
| `.archify/*.html` | 74 万字节生成物；改图请改 `candidate.json` 重跑 |
| `docs/reports/`、`docs/reports/data/` | 历史验收报告与基准 JSON，按文件名定向读 |
| `TestResults/`、`obj/`、`bin/` | 构建/测试产物 |

搜索请限定目录：`grep pattern src/App`，不要全仓 grep。
