# S1 AppContainer 可行性验证记录

> 阶段：S1（对应 `docs/sandbox-development-manual.md` §5）
> 日期：2026-10-21
> 结论：**cmd 与 PowerShell 5.1 均能在 AppContainer 内运行，隔离能力符合预期，进入 S2，默认 AppContainer 路线。**

## 1. 测试环境

| 项目 | 值 |
| --- | --- |
| 系统 | Windows 10 Pro 26H2，Build 26340.9616 |
| 用户 | `pc-8700k\12105`，标准用户（Medium 完整性，Administrators 仅 deny-only，非管理员） |
| .NET SDK | 10.0.400 |
| Windows PowerShell | 5.1.26100.9596（Desktop） |
| 代码位置 | `F:\AI\Code\SDemo\DesktopAgent` |

用标准用户而非管理员测试，避免高完整性掩盖 ACL / AppContainer 问题。

## 2. 验证方式

1. 独立 spike（`.tmp/acspike2`，不提交）先验证 API 串：profile → SID → ACL → `STARTUPINFOEX` + `SECURITY_CAPABILITIES` → `CreateProcess` → Job Object。
2. 把 spike 结论固化为 S1 实现，并用自动化集成测试覆盖：
   ```powershell
   dotnet test tests\SDemo.DesktopAgent.Sandbox.Tests\SDemo.DesktopAgent.Sandbox.Tests.csproj
   ```
   结果：50 个用例全部通过（含真实进程启动）。另有 Core 单元测试 136 个通过。

## 3. 结果

| 检查项 | 结果 | 证据 |
| --- | --- | --- |
| 创建 / 复用 AppContainer profile（标准用户） | 通过 | SID `S-1-15-2-3218280481-…`，重复 `Ensure()` 返回同一 SID |
| `cmd.exe` 在 AppContainer 内启动 | 通过 | `echo SDEMO_SANDBOX_OK`，exit 0 |
| Windows PowerShell 5.1 在 AppContainer 内运行 | 通过 | `Write-Output`，exit 0 |
| 中文输出 UTF-8 不乱码 | 通过 | `Write-Output '中文'`，父进程按 UTF-8 读回 |
| 读 `%USERPROFILE%\sdemo-sandbox-sentinel.txt` | 拒绝 | exit 1，`拒绝访问`；stdout 无哨兵内容 |
| 写真实工作区 | 拒绝 | exit 1，`拒绝访问`；工作区文件未被改写 |
| 网络请求 | 拒绝 | `Invoke-WebRequest https://example.com` exit 1 |
| Job 内启动子进程 | 通过 | `Start-Process` / `cmd /c` 成功 |
| 超时终止 | 通过 | 2s 超时 → `TimedOut=true`，Job 整棵树终止 |
| 取消终止 | 通过 | 取消 1s 后 5s 定时写标记的脚本未产生文件 |
| 进程数限制 | 通过 | `MaxProcesses=1` 时 `Start-Process` 失败（exit 7） |
| 内存限制 | 通过 | `MemoryLimitMB=128` 时 300MB 分配抛出并 exit 7 |

## 4. 关键问题与结论

### 4.1 自定义环境块导致 `CreateProcess` 返回 203（ERROR_ENVVAR_NOT_FOUND）

最小环境块（`SystemRoot` / `PATH` / `TEMP` / `USERPROFILE` / `PSModulePath` 等）配合 AppContainer 时 `CreateProcess` 返回 203；
同样的环境块在不带 AppContainer capability 时却能成功。逐步二分后确认：

- **`LOCALAPPDATA` 是 AppContainer 启动路径的必需变量**，缺失即 203；
- 把 `LOCALAPPDATA` / `APPDATA` 指向 job 的 `home\AppData\Local`、`home\AppData\Roaming` 后启动成功，
  且不会把用户真实目录暴露给沙箱进程。

最终 `SandboxEnvironment` 固定提供：`SystemRoot`、`windir`、`SystemDrive`、`ComSpec`、`PATH`、
`PATHEXT`、`TEMP`/`TMP`（→ `home\temp`）、`USERPROFILE`/`HOMEDRIVE`/`HOMEPATH`（→ `home`）、
`LOCALAPPDATA`/`APPDATA`（→ `home\AppData`）、`PSModulePath`（仅系统模块目录）。
不传 `USERNAME`、`USERDOMAIN`、API Key / Token 等宿主变量。

### 4.2 PowerShell 兼容性

PowerShell 5.1 在 AppContainer 内可直接运行，不需要额外 capability；为了输出正确：

- 脚本头设置 `[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false)` 与 `$OutputEncoding`；
- 父进程按 UTF-8 读取 `stdout.txt` / `stderr.txt`；
- `PSModulePath` 指向 `%SystemRoot%\System32\WindowsPowerShell\v1.0\Modules`，保证
  `Invoke-WebRequest` 等系统模块可用（若置空会让网络测试因“模块找不到”而假通过）。

### 4.3 ACL 与目录

- 根目录 / `jobs` / job 根：`Traverse + ReadAttributes`（不继承）；
- `host` / `input`：`ReadAndExecute`（继承）；
- `work` / `home` / `output`：`Modify`（继承）；
- `%LOCALAPPDATA%\SDemo` 与 `…\DesktopAgent` 只加 `Traverse + ReadAttributes`，便于沙箱进程穿过祖先目录；
  不改 `%LOCALAPPDATA%` 本身，也不动真实工作区 ACL。
- `home\AppData\Local`、`home\AppData\Roaming`、`home\temp` 必须在 `home` 的继承 ACE 之后创建，
  否则拿不到继承权限。

### 4.4 进程树与输出

- `CreateProcess` 使用 `CREATE_SUSPENDED`，先 `AssignProcessToJobObject` 再 `ResumeThread`；
- Job 设置 `KILL_ON_JOB_CLOSE`、`ACTIVE_PROCESS`、`PROCESS_MEMORY`、`JOB_MEMORY`，并尝试 UI 限制；
- 输出写 `output\stdout.txt` / `stderr.txt`（不使用管道，避免 8MB 级输出把管道塞死）；
- 超时 / 取消调用 `TerminateJobObject`，父进程句柄在 `finally` 中关闭。

## 5. 与手册的偏差

1. 手册把 `SandboxProbe` 放在 Windows 项目，但设置页需要 `SandboxProbeReport`；
   报告类型放在 `Sandbox.Contracts`，`ISandboxJobService` 增加 `RunSelfTestAsync`。
2. `AppContainerProfile` 不再作为 DI 单例直接解析（构造期抛异常会拖垮设置页），
   改为 `AppContainerProfileProvider` 惰性创建并缓存失败原因；自检负责把原因展示给用户。
3. 手册建议 `PSModulePath` 置空；实测为了让网络 / 模块类 cmdlet 可用且负向测试不被“模块缺失”污染，
   改为只保留系统模块目录（仍不暴露用户模块）。

## 6. 后续

- 按 S2 实现 `job.json` / `result.json` 协议、`SandboxHost`、工作区副本与 diff；
- S4 再评估 Windows Sandbox（Pro / Enterprise / Education），Home 版明确不可用；
- 设置页“执行沙箱自检”已接入，手工验收时可对照本记录逐项查看。
