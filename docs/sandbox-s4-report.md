# 沙箱 S4 实施报告：Windows Sandbox

日期：2026-10-21
分支：`feature/sandbox-s4-wsb`
基线：`feature/sandbox-s3-powershell`（S3 提交 `b1daa79`）

## 1. 完成内容

### 1.1 可用性检测

- 新增 `Windows/WindowsSandboxAvailability.cs`：
  - `ReadHostInfo()` 只读检测：注册表 `EditionID` + `%SystemRoot%\System32\WindowsSandbox.exe` 是否存在；
  - `Detect(info, verified)` 纯函数：Home（`Core` / `CoreN` / `CoreSingleLanguage` / `CoreCountrySpecific`）
    明确不可用；非 Home 且 exe 缺失 → 「功能未启用」+ `Enable-WindowsOptionalFeature … Containers-DisposableClientVM` 提示；
  - `Verified` 只来自自检（真的启动一次 VM），不是“exe 存在”就放行。
- 不调用 `Get-WindowsOptionalFeature -Online`（需要管理员），符合手册 §8.2。

### 1.2 `.wsb` 配置生成

- 新增 `Windows/WindowsSandboxConfig.cs`：
  - XML 由 `XElement` 生成并转义，不手工拼字符串；
  - `Networking=Disable`、`vGPU=Disable`、`ClipboardRedirection=Disable` 固定写入，**没有任何打开网络 / 剪贴板的参数入口**；
  - `MemoryInMB` 下限 2048（系统规定），上限 16384；
  - 元素顺序按 Microsoft 文档：Networking → vGPU → ClipboardRedirection → MemoryInMB → MappedFolders → LogonCommand；
  - 三个映射目录必须存在且为绝对路径，否则直接拒绝生成（避免容器启动时才失败）；
  - 写文件用 UTF-8 无 BOM + XML 声明。

### 1.3 沙箱内脚本

- 新增 `Windows/WindowsSandboxRunScript.cs`，生成两个文件到只读的 `input` 映射：
  - `run.ps1`：LogonCommand 入口。写 `started.txt` 启动标记 → `Start-Process` 启动子 PowerShell（stdout / stderr 重定向到映射的 `output`）
    → 读退出码 → 原子写 `result.json`（先 `.tmp` 再 `Move`，PS 5.1 没有三参 `File.Move`，先删旧文件）；
  - `run-script.ps1`：子 PowerShell 的包装脚本，先设置 UTF-8 输出编码再 `& script.ps1`（PS 5.1 + 重定向下中文不乱码），
    写文件时带 UTF-8 BOM（PS 5.1 只认 BOM）。
  - `jobId` 只允许 `[A-Za-z0-9-]`；工作目录只接受 `work/...` 相对路径，拒绝 `..`、引号、换行。

### 1.4 运行器

`Windows/WindowsSandboxRunner.cs`（替换 S0 占位）：

- `ProbeAsync`：只做事实检测，不启动 VM；`Verified` 由自检置位（同时提供 `MarkVerified`）。
- `RunAsync`：
  1. 校验 `jobId`、`WindowsSandboxJobSpec`；
  2. **映射目录必须是 job 布局本身**（`{JobRoot}\input` / `work` / `output`）——`work` / `output` 在 VM 里可写，
     这条检查挡住“把真实工作区映射进沙箱”的路径；
  3. `SemaphoreSlim(1,1)` 单实例排队，最多等 60 秒，超时返回明确的「沙箱忙」；
  4. 清理上次残留的 `result.json` / `stdout.txt` / `stderr.txt` / `started.txt`；
  5. 生成 `run.ps1` / `run-script.ps1` / `job.wsb`，启动 `WindowsSandbox.exe`；
  6. 每 200 ms 轮询 `result.json`，同时检查取消；超时 = 脚本超时 + 启动宽限（默认 60 秒）；
  7. 读 `stdout.txt` / `stderr.txt`（按 `MaxOutputBytes` 截断）、按 `started.txt` 区分“没启动”与“启动了但没结果”；
  8. `finally` 终止虚拟机、删除 `.wsb`；job 目录保留给变更集审阅。
- 终止方式：`WindowsSandbox.exe` / `WindowsSandboxClient.exe` / `WindowsSandboxRemoteSession.exe` /
  `WindowsSandboxServer.exe` 中，**启动时刻之后**出现的进程才杀（5 秒时钟余量），避免误杀用户手动打开的沙箱。
- 应用退出兜底：`AppDomain.ProcessExit` 时终止本会话里仍在跑的实例（硬杀进程 / 断电仍会残留，交给 S6 Janitor）。
- 新增 `Windows/WindowsSandboxLauncher.cs`：`IWindowsSandboxLauncher` / `IWindowsSandboxInstance` 抽象 +
  真实实现，测试可注入假启动器而不开虚拟机。

### 1.5 选择器

- `ISandboxSelector` 新增 `SelectAsync(policy, requireVerified)`，返回“选中的运行器 + 可用性”：
  - `Required`：只看首选类型，首选不可用 / 未验证就拒绝（`已拒绝降级执行`）；
  - `Preferred`：首选不可用或未验证时可以换用另一种沙箱，但绝不回退到无沙箱；
  - `requireVerified=true` 时未通过自检的候选不算可用（PowerShell 专用）。
- `RunAsync` 改为复用 `SelectAsync`（`requireVerified=false`），原有 S0/S1 测试语义不变。

### 1.6 作业服务分流

- `SandboxJobService` 先按策略选出运行器，再按类型准备 job：
  - AppContainer：`EnsureRoot(Sid)` / `EnsureJob(jobId, Sid)` + `SandboxHost`（原路径不变）；
  - Windows Sandbox：`CreateJob(jobId)`（不施加 ACL、不降级完整性级别）+ 映射目录 + `run.ps1`；
  - 两者共用：工作区快照 / manifest / diff / 变更集 / `OutcomeAvailable` / 结果解析。
- `SandboxDirectory` 拆出 `EnsureRoot()`（无 ACL）与 `CreateJob(jobId)`（只建 `input` / `work` / `output`）。
- `ReadResult` 的无结果提示改成与后端无关的措辞。
- 新增 `ISandboxJobService` 两个成员：
  - `GetAvailabilityAsync(SandboxKind)`：设置页按类型展示状态；
  - `RunWindowsSandboxSelfTestAsync()`：真的启动一次 VM 的自检。

### 1.7 自检

`SandboxJobService.RunWindowsSandboxSelfTestAsync`：

1. 不可用 → 返回 `Launched=false` + 原因，不抛异常；
2. 可用 → 建临时 job、跑一段脚本：`Write-Output 'SDEMO_WSB_OK'` + `Invoke-WebRequest`；
3. 三项结论：`Launched`（跑完 run.ps1 且 exit 0）、`PowerShellOk`、`NoNetwork`（`SDEMO_WSB_NET=blocked`）；
4. 全部通过才 `MarkVerified(true)`；任何一项失败都会把 Verified 清回 false；
5. 结束后删除自检 job 目录。

### 1.8 设置与界面（设置 → 工具设置 → 执行沙箱）

- 新增「首选沙箱」分段控件（AppContainer / Windows Sandbox），写回 `SandboxSettings.Preferred`；
- 两种沙箱各一行状态（可用 / 可用未验证 / 不可用 + 原因）；
- 新增「Windows Sandbox 内存（MB）」输入（2048–16384，写回 `WindowsSandboxMemoryMB`）；
- 新增「测试 Windows Sandbox」按钮（约 10–30 秒），显示启动 / PowerShell / 无网络三项结果；
- 自检失败时提供「改用 AppContainer」按钮（AppContainer 可用时）；
- 新增 `SandboxSettings.WindowsSandboxMemoryMB`（默认 4096，旧设置文件仍可加载）。

### 1.9 工具与文案

- `PowerShellToolPack` 审批框：Windows Sandbox 显示为
  `Windows Sandbox（VM，无网络，启动约 10–30 秒）`，AppContainer 不变；
- `PowerShellResultFormatter`：`Windows Sandbox（VM）` 与 `AppContainer` 区分，模型能看出是虚拟机；
- 工具描述把 ConstrainedLanguage 限定为「AppContainer 模式下」，避免对 WSB 作出错误承诺。

## 2. 测试

- `SDemo.DesktopAgent.Core.Tests`：**187 通过**（S3 为 187，新增默认值断言，无回归）。
- `SDemo.DesktopAgent.Sandbox.Tests`：**139 通过**（S3 为 81，新增 58）：
  - `WindowsSandboxConfigTests`（9）：网络 / vGPU / 剪贴板固定关闭、映射与只读标志、元素顺序、
    特殊字符转义、内存下限与默认值、目录缺失 / 相对路径拒绝、无 BOM；
  - `WindowsSandboxRunScriptTests`（18）：jobId / 工作目录替换、`..` / 引号 / 换行拒绝、结果与输出约定、
    包装脚本转义与 BOM，
    以及**把 `C:\job` 换成本地临时目录后在真实 PowerShell 上跑通**（验证脚本语法、`Start-Process` 参数、
    `result.json` 原子写、中文 UTF-8 输出）；
  - `WindowsSandboxAvailabilityTests`（13）：Home 各版本、Pro / Enterprise / Education、功能未启用提示、自检门槛；
  - `WindowsSandboxRunnerTests`（11）：正常执行与清理、超时、取消、缺 spec、Home 不可用、jobId 不匹配、
    映射目录越界、job 根目录越界、没启动 / 启动后无结果的区分、单实例排队（并发请求不会同时开两个 VM）；
  - `SandboxSelectorTests`（+3）：首选 WSB 选中、要求已验证时跳过未验证、强制首选未验证时拒绝降级；
  - `SandboxJobServiceTests`（+2）：强制 WSB 不可用时拒绝且不建 job、自动模式降级到 AppContainer 并真实执行；
  - `WindowsSandboxCompositionTests`：改为断言“本机真实检测结果”，并覆盖按类型查可用性与自检的不可用路径。
- 构建：`dotnet build SDemo.DesktopAgent.Wpf.slnf` 通过；
  `dotnet build src/SDemo.DesktopAgent.Maui -p:EnableAndroidTarget=true -f net10.0-android` 通过（接口变更未破坏 Android）。

## 3. 真机验收状态（重要）

本机环境：

| 项目 | 值 |
| --- | --- |
| 系统 | Windows 10 Pro 26H2，Build 26340.9616 |
| 用户 | 标准用户（Administrators deny-only） |
| `EditionID` | `Professional`（版本支持 Windows Sandbox） |
| `WindowsSandbox.exe` | **不存在** → `Containers-DisposableClientVM` 未启用 |
| Hypervisor | `HypervisorPresent = True` |

因此**“真的启动虚拟机”的验收项没有执行**，S4 的验证覆盖到：

- 可用性检测、`.wsb` / 脚本生成的正确性（含 XML 转义与元素顺序）；
- 运行器的状态机（轮询、超时、取消、单实例、清理），用假启动器模拟 VM；
- 生成的 `run.ps1` 在本机真实 PowerShell 上跑通（仅替换 `C:\job` 路径）；
- 作业服务分流与策略语义（含本机 WSB 不可用时的拒绝 / 降级路径）。

启用 Windows Sandbox（需要管理员 + 重启）：

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName "Containers-DisposableClientVM" -All
```

## 4. 与手册的偏差

1. **作业准备在 `SandboxJobService`，不在 Runner**。手册 §8.5 把“复制工作区 / 写脚本 / 启动”都放在
   `WindowsSandboxRunner.RunAsync`；实现时为了让两种沙箱复用 S2 的快照 / diff / 变更集 / 审批，
   只把**虚拟机生命周期与协议**放进 Runner，job 目录与工作区副本仍由作业服务负责。
2. **新增两个 `ISandboxJobService` 成员**：按类型查可用性、Windows Sandbox 自检。
   手册只描述设置页行为，没有规定接口；UI 项目不能引用 Windows 工程，只能经 Core 接口暴露。
3. **`ISandboxSelector` 新增 `SelectAsync`**：任务需要先知道“会用哪种沙箱”才能准备不同 job 目录。
   `Required` 收紧为“只看首选”，`Preferred` 才允许跨类型降级（与 S3 的语义一致）。
4. **自检包含断网验证**：手册只要求“启动一次小自检”，这里额外验证 `Networking=Disable` 真的生效；
   否则“Verified”会掩盖配置未生效的问题。
5. **超时结果细分**：`started.txt` 不存在 → `LaunchFailed`（功能 / 映射问题）；
   存在但没写 `result.json` → `Timeout`（脚本卡死）；两者提示不同。
6. **`SandboxSettings` 增加 `WindowsSandboxMemoryMB`**（默认 4096）。手册把 `MemoryInMB` 写死 4096；
   给用户可调更实用，下限仍由系统规定（2048）。
7. **进程退出兜底**：`AppDomain.ProcessExit` 终止仍在跑的实例。手册只要求“关闭应用必须终止沙箱进程树”，
   没有规定实现方式。
8. **结果协议**：Windows Sandbox 的 `result.json` 不内联 stdout / stderr（由宿主读 `stdout.txt` / `stderr.txt`），
   与 AppContainer 的 `SandboxHost` 行为保持一致，避免 8MB 文本进 JSON。

## 5. 已知限制 / 后续

- **无官方生命周期 API**：只能按“进程名 + 启动时间”杀进程。硬杀宿主进程 / 断电会留下 `WindowsSandbox*` 进程，
  由 S6 的 `SandboxJanitor` 在启动时兜底。
- 单实例排队最多 60 秒；并发请求超过等待时间返回「沙箱忙」，不会排队无限等。
- 每次 WSB 执行有 10–30 秒启动成本；这是强隔离的代价，审批框与设置页已提示。
- **Verified 只存在内存**：应用重启后需要重新点「测试 Windows Sandbox」才能使用 WSB
  （与手册“自检通过才 Verified”一致，避免把一次成功当成永久可信）。
- 组策略禁用 Windows Sandbox 时，只能靠“启动超时 / 自检失败”提示，没有专门的策略检测。
- PDF 解析仍只走 AppContainer（手册 §8.6，S5 范围）。
- `run.ps1` 超时后由宿主整机终止，无法在 VM 内优雅收尾（脚本仍可能留下半截输出）。
- job 目录、`run.ps1` 等残留的清理由 S6 的 `SandboxJanitor` 统一处理；`.wsb` 每次运行结束即删。

## 6. 手工验收清单（Pro / Enterprise 且已启用 WSB）

1. 设置 → 工具设置 → 执行沙箱：AppContainer 与 Windows Sandbox 各显示状态；Windows Sandbox 显示「可用（未验证）」。
2. 点「测试 Windows Sandbox」：约 10–30 秒后启动 / PowerShell / 无网络三项全部打勾，状态变为「可用（已验证）」。
3. 首选切到 Windows Sandbox、模式选「强制」：输入框打开 `>_` chip，让模型运行 `Write-Output 'hello'`；
   审批框显示 `Windows Sandbox（VM，无网络，启动约 10–30 秒）`，执行成功并返回 stdout。
4. 脚本里 `Invoke-WebRequest https://example.com` → 失败（断网）。
5. 脚本里读取 `C:\Users\<真实用户>\...` → 失败（VM 里没有这个路径）。
6. 脚本 `New-Item note.txt` → 变更面板出现 `A note.txt`，真实工作区在「应用选中」前不变。
7. 脚本 `Start-Sleep -Seconds 300`，超时设为 5 秒 → 约 65 秒后返回超时，任务管理器中 WindowsSandbox* 进程消失。
8. 连续触发两次执行 → 第二次排队等待，不会同时出现两个沙箱窗口。
9. 生成中点「停止」→ 审批框关闭、VM 被终止、任务管理器无残留进程。
10. 首选切回 AppContainer → PowerShell 仍可执行（回归 S1–S3 行为）。
