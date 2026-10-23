# P4 — 内循环提速（第 4 期）

> 状态：✅ 完成（2026-10-22；4.7 部分、4.9 可选未做） · 估时 6 人日 · 前置：P3 · 出口：编辑 → 构建 → 测试 p50 ≤ 20s
> 关联总纲：[vibe-coding-roadmap.md](vibe-coding-roadmap.md) · 决策：白名单直跑（默认关、会话级审批） · 报告：[p4-report.md](p4-report.md)

## 1. 本期要解决的问题

vibe coding 的核心体感是**循环速度**。现在每改一次代码：

1. 每次 `run_powershell` 都弹审批（[PowerShellToolPack.cs](../../src/Tools/SDemo.DesktopAgent.Tools.Windows/PowerShellToolPack.cs:125)）；
2. 超时封顶 120 秒（[PowerShellToolPack.cs](../../src/Tools/SDemo.DesktopAgent.Tools.Windows/PowerShellToolPack.cs:117)）；
3. 沙箱无网络，`dotnet restore` 根本跑不了；
4. 每个 job 复制整个工作区（[SandboxJobService.cs](../../src/Sandbox/SDemo.DesktopAgent.Sandbox.Windows/SandboxJobService.cs:12)），大仓库光准备就要几十秒。

结果就是「改一行、等一分钟、点三次确认」。本期在**不放松默认安全**的前提下，给构建/测试开一条可审计的快车道。

## 2. 范围

**做**

- 命令白名单与参数校验（禁止 shell 元字符、绝对路径、越界目录）；
- 直跑执行器（Job Object、超时、输出上限、环境变量白名单、审计）；
- 会话级审批：同一命令前缀本会话内不再重复弹窗，可撤销；
- 构建/测试结果解析（错误数、失败用例名、首个错误定位）；
- 验证记录进状态包（P2 预留接口）；
- 沙箱路径的离线包缓存映射 + 宿主预 restore 流程；
- 工作区级构建/测试命令配置（自动探测 + 人工确认）。

**可选（behind flag）**

- 热工作区：会话内复用沙箱副本，按 manifest 增量同步，而不是每次全量复制。

**不做**

- 终端 UI / 交互式 shell；
- 命令输出逐行流式进聊天（MAF 工具结果是一次性返回）→ P7 评估；
- 提交、推送、切分支等写操作 git 命令；
- 远程/容器执行。

## 3. 设计要点

### 3.1 执行模式

```csharp
public enum ExecutionMode
{
    SandboxFirst,       // 默认：所有命令进沙箱
    AllowlistDirect     // 白名单命令可直跑（仍需审批）
}
```

- 直跑**只覆盖** `run_command` 的白名单命令；`run_powershell` 永远走沙箱，保持不变；
- 默认 `SandboxFirst` + `DirectRunEnabled=false`，用户显式打开后才生效；
- 打开时设置页用红字说明：直跑**不受沙箱网络隔离**，可访问网络与真实工作区。

### 3.2 命令工具

```csharp
run_command(command, args = "", workingDirectory = ".", timeoutSeconds = 0, purpose = "")
```

- `command` 只能是**可执行文件名**（不带路径），从 `PATH` 解析；拒绝 `.exe` 路径、`cmd /c`、`powershell -Command`；
- `args` 按空白切分到 `ProcessStartInfo.ArgumentList`（绝不拼字符串、不用 shell）；
- 拒绝参数中的 `& | > < ; $ ( ) \` ^ %` 与换行；拒绝 `..` 越界；
- 工作目录必须是工作区内相对路径；
- `purpose` 仅用于 UI 展示（构建 / 测试 / 其他）与审计，不参与权限判断。

`workspace_info` 扩展返回：

```text
工作区根目录：C:\Code\Demo
构建命令：dotnet build Demo.sln -v minimal
测试命令：dotnet test Demo.sln --no-build
（来自工作区设置，可直接用于 run_command）
```

### 3.3 命令策略

```csharp
public sealed record CommandPolicyResult(bool Allowed, string Reason, ExecutionMode Mode);
```

规则（顺序执行，任一失败即拒绝）：

1. 模式允许直跑；
2. `command` 在 `ExecutionSettings.Allowlist` 中（默认 `dotnet`、`npm`、`pnpm`、`node`、`git`、`python`、`cargo`、`go`）；
3. 参数无元字符、无换行、长度 ≤ 4KB；
4. 工作目录在根内且存在；
5. git 只允许只读子命令（`status`、`diff`、`log`、`show`、`rev-parse`）；
6. 命令总时长受 `buildTimeoutSeconds`（默认 600）约束。

不满足时返回 `ERROR: 命令不在白名单 / 含不允许的字符 / 目录越界`，并提示“可改用 run_powershell 进沙箱”。

### 3.4 直跑执行器

`DirectProcessRunner`：

- `UseShellExecute=false`、`CreateNoWindow=true`；
- Job Object 绑定（复用现有实现），超时或取消时结束整棵进程树；
- 环境变量白名单：`PATH`、`TEMP`、`TMP`、`USERPROFILE`、`DOTNET_*`、`NUGET_PACKAGES`、`HOME`；其余不继承；
- 输出：stdout/stderr 各 12K 字符上限（沿用 `PowerShellResultFormatter` 的截断文案）；
- 返回：退出码、耗时、截断标记、`TimedOut`；
- 审计：`ToolAuditEntry` 增加 `Mode=direct`、`CommandHash`、`WorkingDirectory`、`Approval=SessionGrant/ApproveOnce`。

### 3.5 会话级审批

- 审批框新增「本会话允许 `dotnet build`」按钮（仅白名单命令可用）；
- 批准后写入内存字典：`{sessionId} → 已批准的命令前缀（exe + 前两个参数）`；
- 相同前缀后续直接执行，仍逐次写审计；
- 撤销：设置 → 工具设置 →「已授权的直跑命令」列表，可单个撤销；应用重启全部失效；
- 高风险命令（含 `rm`、`del`、`format`、`git reset`、`git clean`）**永远**不提供会话批准。

### 3.6 构建 / 测试解析

```csharp
public sealed record VerificationRecord(
    string Kind,            // build / test
    string Command,
    bool Success,
    int ExitCode,
    int Errors,
    int Warnings,
    int Passed,
    int Failed,
    IReadOnlyList<string> TopIssues,   // "src/A.cs(12,5): error CS1002: ..."
    DateTime At,
    long DurationMs);
```

- `dotnet build`：匹配 `(\d+) Error\(s\)` / `(\d+) Warning\(s\)` / `error CS…`；
- `dotnet test`：匹配 `Passed!` / `Failed!` / `Total tests` / `Passed:` / `Failed:`，提取失败用例名；
- `npm/pnpm test`：退出码 + 末尾 40 行；
- 解析失败时仍返回原始输出尾部，`errors=-1` 表示未知；
- 结果写入 `sessions.LastVerificationJson`（P4 新增列），P2 状态包读取展示：

```markdown
## 验证
dotnet build → 成功（0 errors / 2 warnings，8.4s，10:45）
dotnet test → 失败（12 passed / 1 failed：OptionsTests.RejectsEmptyEndpoint，10:47）
```

### 3.7 离线包与预 restore

沙箱无网络，但构建通常不需要联网（restore 之后）：

1. 宿主侧 `dotnet restore` 走直跑/审批（允许网络）；成功后 `obj/project.assets.json` 落在真实工作区；
2. 沙箱内执行 `dotnet build --no-restore`，并把 `%USERPROFILE%\.nuget\packages` 以**只读**方式授予 AppContainer 访问（沿用 S1 的 Package SID ACL 做法），设置 `NUGET_PACKAGES`；
3. 若 restore 缺失，构建报错时返回“请先运行 dotnet restore（需要直跑模式）”；
4. 设置页提供「预热包缓存」按钮：对当前工作区跑一次 restore。

### 3.8 热工作区（可选，flag）

`warmWorkspace.enabled=false` 时保持现状（每 job 全量复制）。开启后：

- 以 `{sessionId}` 为键维护沙箱工作副本；
- 每次执行前用 manifest 对比真实工作区，**只同步变化的文件**（新增/修改/删除）；
- 检测到外部改动（用户手工编辑）时，优先以真实工作区为准同步进副本；
- 变更集仍相对 `base` manifest 计算，确认写回逻辑不变；
- 会话关闭或 30 分钟无活动时清理；Janitor 增加对应清理规则；
- 风险较高：先用一周真实任务验证，再决定是否默认开。

## 4. 数据与配置

**DB**：`sessions.LastVerificationJson TEXT NULL`（加入 `RequiredColumns`）。

**settings.json**：

```jsonc
"execution": {
  "mode": "sandboxFirst",
  "directRunEnabled": false,
  "allowlist": ["dotnet", "npm", "pnpm", "node", "git", "python", "cargo", "go"],
  "defaultTimeoutSeconds": 120,
  "buildTimeoutSeconds": 600,
  "maxOutputChars": 12000
},
"warmWorkspace": { "enabled": false, "idleMinutes": 30 }
```

**Workspace**：`BuildCommand`、`TestCommand`（可空）。设置工作区时自动探测：

- 有 `.sln` → `dotnet build <sln> -v minimal`；
- 只有 `.csproj` → `dotnet build <csproj>`；
- `package.json` → `npm test` / `pnpm test`；
- 探测结果只作为建议，用户确认后保存。

## 5. 任务拆解

| # | 任务 | 交付物 | 涉及文件 | 估时 | 状态 |
| --- | --- | --- | --- | --- | --- |
| 4.1 | 命令策略 | `CommandPolicy`、白名单校验、拒绝文案、单测 | `Core/Execution/CommandPolicy.cs` | 0.5 | ✅ |
| 4.2 | 直跑执行器 | `DirectProcessRunner`（Job Object、超时、输出、环境） | `Windows/Execution/DirectProcessRunner.cs` | 1.0 | ✅ |
| 4.3 | 命令工具 | `run_command` 工具包、workspace_info 扩展 | `Windows/Tools/CommandToolPack.cs` | 0.5 | ✅ |
| 4.4 | 会话审批 | 前缀授权、撤销 UI、审计字段 | `ToolApprovalService.cs`、设置页 | 1.0 | ✅ |
| 4.5 | 结果解析 | `BuildResultParser`、`TestResultParser`、`VerificationRecord`、落库 | `Core/Execution/Verification/*` | 1.0 | ✅ |
| 4.6 | 状态包接入 | P2 状态包读取 `LastVerification` | `WorkspaceStateProvider.cs` | 0.25 | ✅ |
| 4.7 | 离线包 | NuGet 缓存只读映射、预 restore 流程、设置入口 | `Windows/Sandbox/*` | 1.0 | ⚠ 预 restore 已做；ACL 映射未做 |
| 4.8 | 工作区命令 | 探测 + 保存 + 设置页 | `Models/Workspace.cs`、设置页 | 0.5 | ✅ |
| 4.9 | 热工作区（可选） | 增量同步、外部改动检测、清理 | `Windows/Sandbox/*` | 1.0 | ⏸ 可选未实现 |
| 4.10 | 文档与报告 | `docs/architecture/inner-loop.md`、`p4-report.md` | docs | 0.25 | ✅ |

## 6. 测试清单

**单测**

- `CommandPolicyTests`：白名单命中/不命中、元字符、换行、绝对路径、`..`、`cmd /c`、git 写子命令、超长参数。
- `DirectRunnerTests`：正常退出、非零退出、超时杀进程树、输出截断、环境变量白名单、取消。
- `SessionApprovalTests`：批准一次、会话授权、前缀匹配、撤销、重启失效、高风险命令不出现该按钮。
- `BuildParserTests`：0 错误、多错误、警告、无解析结果、本地化输出（英文）。
- `TestParserTests`：全通过、有失败、跳过、无测试、`npm test` 回退。
- `VerificationStoreTests`：落库、读取、状态包渲染。
- `WarmWorkspaceTests`（若实现）：增量同步、删除、外部改动、清理。

**集成 / 手工**

- 本仓库：改 `Options.cs` → `dotnet build` → `dotnet test`，记录冷/热耗时与审批次数。
- 断网环境：预 restore 后沙箱 `--no-restore` 构建成功。
- 安全：`dotnet build & del x`、`cmd /c calc`、`git reset --hard`、`..\..\Windows\System32` 全部拒绝。
- 取消生成：进程树无残留（任务管理器验证）。

## 7. 验收标准

- [x] 直跑开启时，改→构建→测试 p50 ≤ 20s（本仓库、增量）；冷启动 ≤ 60s（**冷启动未单独测**，见报告）。
- [x] 单任务审批 ≤ 2 次（restore 一次 + 首个构建一次，后续会话授权）。
- [x] 直跑默认关闭；关闭时功能完整（走沙箱 + 预 restore），只是更慢。
- [x] 每次直跑都有审计记录（命令 hash、目录、退出码、耗时、授权来源）。
- [x] 拒绝用例 100% 拦截，且有可读原因；不产生任何进程。
- [x] 验证结果进入状态包，重启后仍显示最近一次。
- [x] 新增测试 ≥ 25 个；现有测试全绿。

## 8. 风险与回退

| 风险 | 处理 |
| --- | --- |
| 直跑破坏“默认安全” | 默认关；红字提示；白名单最小化；高风险命令永不授权；审计可追溯 |
| 进程/子进程泄漏 | Job Object + 超时 kill tree + 取消测试 |
| 解析依赖英文输出 | 解析失败回退原始输出；不因解析失败误判成功 |
| 热工作区脏数据 | 默认关；manifest 对比 + 外部改动检测；异常时回退全量复制 |
| 包缓存 ACL 放宽 | 只读授权、只对 Package SID、路径固定，不做通用目录授权 |
| 构建命令因仓库而异 | 工作区级配置 + 探测建议 + 模型可显式传命令 |

## 9. PR 拆分

1. **PR1**：命令策略 + 直跑执行器 + `run_command` + 单测。
2. **PR2**：会话审批 + 审计 + 设置页授权管理。
3. **PR3**：结果解析 + 验证落库 + 状态包接入 + 工作区命令配置。
4. **PR4**：离线包 + 热工作区（flag）+ 文档/报告。

## 10. 完成定义（DoD）

- [x] 本仓库热路径跑通并记录数据；冷启动路径未单独测；
- [x] 安全用例与进程清理用例通过；
- [x] 总纲指标表补「内循环耗时」「审批次数」实测；
- [x] `docs/architecture/inner-loop.md` 写清直跑边界、授权模型、离线包流程（含未实现的 ACL / 热工作区边界）。
