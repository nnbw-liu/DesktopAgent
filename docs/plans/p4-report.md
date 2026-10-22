# P4 报告：内循环提速

> 期次：P4（第 4 期）　分支：`feature/vibe-p4-inner-loop`　状态：✅ 完成（含两项明确的未实现边界）
> 配套文档：[inner-loop.md](../inner-loop.md)　原始计划：[p4-inner-loop.md](p4-inner-loop.md)
> 真机工具：[tools/p4-loop](../../tools/p4-loop)

## 1. 交付物

| 模块 | 文件 |
| --- | --- |
| 命令策略 | `Core/Execution/CommandPolicy.cs`（白名单、shell / 路径 / git 校验、引号切分） |
| 会话授权 | `Core/Execution/SessionGrantStore.cs` |
| 直跑执行器 | `Windows/Execution/DirectProcessRunner.cs`（Job Object、超时、取消、环境白名单、输出上限） |
| 命令工具 | `Windows/Tools/CommandToolPack.cs`（`run_command`、审批、会话授权、审计） |
| 结果解析 | `Core/Execution/BuildResultParser.cs`、`TestResultParser.cs`、`VerificationRecord.cs` |
| 验证落库 | `Core/Models/ChatSession.cs`、`Core/Data/ChatDbContext.cs`、`Core/Services/ChatStore.cs`（`LastVerificationJson` + 轻量迁移） |
| 状态包 | `Core/Context/State/WorkspaceStateProvider.cs`（读取 `LastVerification`） |
| 工作区命令 | `Core/Execution/WorkspaceCommandDetector.cs`、`Models/Workspace.cs`、`Tools/FileToolPack.cs`（`workspace_info`） |
| 预 restore | `Core/Execution/WorkspaceWarmup.cs`、`Windows/Execution/WorkspaceWarmupService.cs` |
| 设置 / 审批 | `ToolsSettings.razor`（直跑开关、白名单、超时、授权撤销、工作区命令、预热按钮）、`ToolApprovalHost.razor` |
| 真机工具 | `tools/p4-loop/*` |
| 文档 | `docs/inner-loop.md`、本报告、计划与路线图状态 |

## 2. 计划任务对照

| # | 任务 | 状态 | 说明 |
| --- | --- | --- | --- |
| 4.1 | 命令策略 | ✅ | 默认关闭、白名单、shell / 换行 / `..` / 绝对路径 / UNC 拒绝、git 只读、4KB 参数上限 |
| 4.2 | 直跑执行器 | ✅ | Job Object、超时 / 取消杀进程树、环境变量白名单、stdout/stderr 上限 |
| 4.3 | 命令工具 | ✅ | `run_command`；`workspace_info` 返回构建 / 测试命令；默认关闭，开启直跑时自动启用 |
| 4.4 | 会话审批 | ✅ | 前缀授权、`ApproveForSession`、设置页授权列表与撤销、审计字段、高风险不授权 |
| 4.5 | 结果解析 | ✅ | 英文 / 中文 build 摘要、dotnet test / xUnit 失败名、中文 test 摘要、未知输出回退 |
| 4.6 | 状态包接入 | ✅ | `LastVerification` 渲染进状态包，验证块最后截断 / 永远保留 |
| 4.7 | 离线包 | ⚠ 部分 | 宿主「预热包缓存」`dotnet restore` 已接；NuGet 缓存对 AppContainer 的只读 ACL 映射未实现 |
| 4.8 | 工作区命令 | ✅ | `BuildCommand` / `TestCommand`、sln / csproj / package.json 探测、设置页保存、workspace_info |
| 4.9 | 热工作区（可选） | ❌ 未实现 | `WarmWorkspaceSettings` 字段已预留，仍每次全量复制；属于计划中的可选 flag |
| 4.10 | 文档与报告 | ✅ | `inner-loop.md` + 本报告 + 计划 / 路线图 |

## 3. 验收核对

| 验收标准 | 结果 | 证据 |
| --- | --- | --- |
| 直跑开启时改→构建→测试 p50 ≤ 20s（增量） | ✅ | `tools/p4-loop` 3 轮：build 2.19 / 2.15 / 2.12s，test 4.57 / 4.55 / 4.57s；总和 p50 **6.7s**（最慢 6.74s） |
| 冷启动 ≤ 60s | ⚠ 未单独测 | 仓库已 restore / 编译；`--no-restore` 首轮 2.28s。冷 restore 路径要等 CI 或清 obj 后复测 |
| 单任务审批 ≤ 2 | ✅ | 3 轮共 2 次审批（首个 build、首个 test 各一次「本会话允许」）；后续 4 次命令走 `SessionGrant` |
| 直跑默认关闭；关闭时功能完整 | ✅ | `ExecutionSettings` 默认 `SandboxFirst` + `DirectRunEnabled=false`；`CommandToolPack` 默认关闭；`run_powershell` 不受影响；单测覆盖关闭时拒绝 |
| 每次直跑都有审计 | ✅ | `CommandToolPackTests.审计记录模式命令hash与目录`；字段 `mode=direct` / `commandSha256` / `workingDirectory` / 退出码 / 耗时 / `Approval` |
| 拒绝用例 100% 拦截且可读；不产生进程 | ✅ | 12 个 `CommandPolicyTests` 用例 + 元字符在审批前拒绝；假 runner 断言零调用 |
| 验证结果进入状态包，重启后仍显示 | ✅ | `VerificationStoreTests` 保存 / 重载；`WorkspaceStateProvider` 渲染 `LastVerification` |
| 新增测试 ≥ 25；现有全绿 | ✅ | Core **479**、Windows **215**（1 跳过）→ 共 **694**；P3 基线 433，新增 **60** |
| 文档 | ✅ | `docs/inner-loop.md` + 本报告 |

## 4. 测试

- Core：`dotnet test tests/SDemo.DesktopAgent.Core.Tests` → **479 通过 / 0 失败**。
- Windows：`dotnet test tests/SDemo.DesktopAgent.Sandbox.Tests` → **214 通过 / 1 跳过 / 0 失败**。
- 构建：`dotnet build SDemo.DesktopAgent.Wpf.slnf` → **0 错误**。

P4 新增用例（60 个）：

| 文件 | 数量 | 覆盖 |
| --- | --- | --- |
| `CommandPolicyTests` | 25 | 默认拒绝、白名单、shell 元字符、绝对 / UNC / `..`、`cmd`、git 写子命令、引号、超长、工作目录 |
| `SessionGrantTests` | 4 | 前缀命中、会话隔离、高风险拒绝、撤销 / 清空 |
| `BuildResultParserTests` | 5 | 英文 / 中文摘要、多错误、无摘要回退、状态包格式 |
| `TestResultParserTests` | 5 | 全通过、失败用例名、中文摘要、npm 回退、失败格式 |
| `VerificationStoreTests` | 2 | 落库 / 重载、状态包渲染 |
| `WorkspaceCommandDetectorTests` | 4 | sln / csproj / pnpm / 空目录 |
| `DirectProcessRunnerTests` | 6 | 正常 / 非零 / 超时 / 取消 / 截断 / 环境白名单 |
| `CommandToolPackTests` | 8 | 未开启、元字符、拒绝、会话授权、高风险、验证、审计、默认关闭 |
| `ToolPackCompatibilityTests` 扩展 | 1 | `workspace_info` 返回构建 / 测试命令 |

> 组合根测试曾暴露 `SessionGrantStore` 未注册，已用 `TryAddSingleton` 修复；以上结果为修复后复跑。

## 5. 实测内循环

`tools/p4-loop` 走完整 `run_command` 路径：策略 → 审批桩 → `DirectProcessRunner` → 验证解析；工作区为本仓库，命令：

```text
dotnet build SDemo.DesktopAgent.Wpf.slnf --no-restore -v minimal
dotnet test tests/SDemo.DesktopAgent.Core.Tests/... --no-build -v minimal
```

| 轮次 | build | test | 合计 | 结果 |
| --- | --- | --- | --- | --- |
| 1 | 2.19s | 4.57s | 6.76s | build 0；479 tests passed |
| 2 | 2.15s | 4.55s | 6.70s | build 0；479 tests passed |
| 3 | 2.12s | 4.57s | 6.69s | build 0；479 tests passed |

p50 ≈ **6.70s**；审批 2 次；审计 6 条。冷启动 / restore ACL 未测，见遗留。

## 6. 偏差与取舍

1. **`run_command` 默认关闭**，而计划只要求直跑模式默认关。理由：工具出现在模型面前但一调用就报“未开启”会造成无效轮次；开启直跑时设置页自动把 `command` 包打开。
2. **npm / pnpm 直跑仍不完整**。Windows 上是 `.cmd`，而 `ProcessStartInfo` 不经过 shell 无法直接启动；当前只解析 `.exe`，实际能用的是 `dotnet` / `node` / `git` 等。计划里的 npm 需要单独的 `.cmd` 安全启动策略。
3. **NuGet 缓存 ACL 映射未实现**。当前是用户点「预热包缓存」在宿主 restore，沙箱内需显式 `--no-restore`；没有给 AppContainer 授予 `%USERPROFILE%\.nuget\packages` 只读权限。这样避免了放宽用户目录 ACL 的风险，但不是完整离线包方案。
4. **热工作区未实现**。`WarmWorkspaceSettings` 已预留，`SandboxJobService` 仍每次全量复制；计划本身把它标为可选 flag。
5. **冷启动未单独测**。仓库已 restore、已有编译产物；`--no-restore` 的 2.1–2.3s 不能代表干净 clone 的第一次 restore。
6. **审批前缀只含前两个参数**。`dotnet build A.sln` 与 `dotnet build B.sln` 是不同授权；这符合“同命令前缀”的保守设计，但同一构建换参数会多一次审批。
7. **工作区命令自动探测只建议、不自动保存**，避免把错误命令写进设置；用户确认后保存。
8. **UI 没有 bUnit / 像素走查**；直跑开关、授权撤销、工作区命令、预热按钮只做构建与启动冒烟。

## 7. 遗留与风险

| 项 | 影响 | 建议 |
| --- | --- | --- |
| npm / pnpm `.cmd` 启动 | 白名单里有但直跑不可用 | 用 `cmd.exe /d /s /c` + 严格参数白名单，或只保留 `.exe` 白名单 |
| NuGet ACL / 离线构建 | 沙箱内仍可能缺包 | 只读 ACL + `NUGET_PACKAGES`；先做安全评审 |
| 热工作区 | 大仓库准备慢 | 按可选 flag 实现 manifest 增量同步，默认关 |
| 冷启动数据缺失 | 无法证明干净环境 ≤60s | 干净 clone / 清 obj 后跑 `p4-loop` |
| 直跑进程树清理 | 极端子进程可能残留 | Job Object + 超时 / 取消测试已覆盖；继续观察 |
| 验证解析依赖输出格式 | 本地化 / 自定义 logger 可能解析失败 | 失败回退原始输出，`errors=-1`，不误判成功 |

## 8. 提交

P4 的代码、测试、真机工具与文档合并为 `feature/vibe-p4-inner-loop` 上的一个提交；未推送远端（按约定只留本地分支）。
