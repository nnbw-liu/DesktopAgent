# 沙箱 S6 UI 逐项验收报告

日期：2026-10-22
环境：Windows 11 Pro 26H2（Build 26200）、标准用户（Administrators deny-only）、.NET SDK 10.0.401、
WebView2 155.0.4283.62、Windows Sandbox 功能已启用。
被测版本：`master`（S0–S6 已合并）+ 本次修复（SandboxHost payload 复制）。

## 1. 验收方式

- 启动 WPF 宿主真实 UI，使用 WebView2 远程调试端口（CDP）对真实 DOM 逐项点击，并在文件系统 / 设置文件 /
  沙箱目录侧核对结果；不是只跑组件单测。
- 为便于定位，启动时设置了 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`；
  这只影响本次调试，不进入产品代码。
- 点击路径：设置 → 工具设置；工作区下拉；输入框 PowerShell chip；审批弹窗；沙箱变更面板。

## 2. 结论

| 项目 | 结果 |
| --- | --- |
| 设置页 / 工具设置导航 | 通过 |
| AppContainer 自检 | 通过（7 项全绿） |
| Windows Sandbox 自检 | 通过（27.3 秒，启动 / PowerShell / 无网络） |
| Windows Sandbox 实际执行脚本 | **部分通过**：VM 启动、退出码 0，但 stdout 为空（P2） |
| 审计：保留正文 / 保留天数 / 打开日志目录 | 通过（设置落盘、资源管理器打开 `logs`） |
| Janitor「清理过期数据」 | 通过（删除 1 个过期 job，跳过 1 个有未应用 diff 的 job） |
| 「清理沙箱数据」二次确认 | 通过（删除 7 个 job + 1 个 trash，profile 删除，host 保留） |
| 删除变更：勾选 / 二次确认 / trash / applied.json | 通过（用宿主侧模拟删除生成 Deleted 变更） |
| 关闭变更面板写入“已处理” | 通过（applied.json 追加被忽略的路径） |
| 真实 AppContainer 脚本删除文件 | **失败**（P1，见下） |
| SandboxHost payload 完整性 | **失败 → 已修复**（P0） |

## 3. 关键证据

### 3.1 AppContainer 自检

设置页显示：

```text
自检全部通过。
基本进程：能在沙箱内启动 cmd
PowerShell：能执行脚本并正确输出 UTF-8 中文
隔离：不能读取用户目录里的文件
隔离：不能写入工作区
隔离：没有网络访问
子进程：允许在 Job 内启动
超时：到点终止整棵进程树
```

### 3.2 Windows Sandbox 自检

```text
Windows Sandbox 自检通过（耗时 27.3 秒）。
启动：能启动 Windows Sandbox 虚拟机并跑完 run.ps1
PowerShell：能在虚拟机内执行脚本
网络：虚拟机没有网络访问
```

首次启动 Windows Sandbox 时出现过环境弹窗：

```text
更新失败。继续使用经典 Windows 沙盒。
Error 0x80080008. 当 OLE 服务与对象服务器通讯时，后者已经停止
```

点「否」后经典沙盒正常启动；之后的自检与实际执行不再出现该弹窗。这是本机 Windows Sandbox 的环境问题，
不是应用代码问题，但会让第一次自检超时失败，排查文档已记录。

### 3.3 实际 Windows Sandbox 执行

- 审批框正确显示：`沙箱：Windows Sandbox（VM，无网络，启动约 10–30 秒）`。
- 执行结果：`执行成功`，退出码 0，无工作区变更。
- 缺陷：脚本 `Get-Location; Write-Output ws-ok` 的 `stdout.txt` 只有 2 字节，模型看到空输出。
  `output/result.json` 中 `stdout` 也是空字符串。

### 3.4 删除变更面板

由于 P1（脚本内删除失败），本次用一个可控方式生成真实的 Deleted 变更：

1. 脚本只执行 `Start-Sleep -Seconds 15; Write-Output "wait-done"`；
2. 在沙箱运行期间由宿主删除 `job\work\delete-me.txt`；
3. diff 得到 `Deleted` 变更，变更面板正常展示。

面板行为核对：

- 删除项默认**不勾选**；勾选后按钮变为「应用选中（2）」。
- 二次确认文案：`将从真实工作区移除 1 个文件：delete-me.txt`。
- 应用后：真实工作区文件消失；`sandbox\trash\{jobId}\delete-me.txt` 出现且内容一致；
  `applied.json` 写入 `delete-me.txt`。
- 点击面板关闭后，剩余未应用路径也会写入 `applied.json`（本次为 `Microsoft/Windows/PowerShell/ModuleAnalysisCache`），
  Janitor 不会因为“还有未应用 diff”永久保留该 job。

### 3.5 Janitor 与彻底清理

- 「清理过期数据」：删除 1 个无变更的过期 job，跳过 1 个有未应用 diff 的 job，提示
  `有 1 个作业还有未应用的变更，已跳过：janitoruitest1`。
- 「清理沙箱数据…」：二次确认后提示
  `已清空沙箱数据：7 个作业、1 个回收站条目、0 个临时文件；AppContainer profile 已删除。`
  核对：`jobs` / `trash` 为空，`host` 保留；可用性刷新后 AppContainer 重建 profile 并恢复“可用”。

### 3.6 审计

- 打开「保留脚本正文」后 `settings.json` 的 `retainScriptsInAuditLog` 立即变为 `true`，关闭后恢复 `false`。
- 保留天数从 7 改为 5 后落盘，再改回 7。
- 「打开日志目录」成功打开 `C:\Users\23179\AppData\Roaming\SDemo\DesktopAgent\logs`。

## 4. 发现的问题

### P0（已修复）SandboxHost payload 缺少 managed DLL

- 现象：从 `bin\Debug` 直接运行应用时，AppContainer 启动失败：
  `The application to execute does not exist: ...\sandbox\host\SDemo.DesktopAgent.SandboxHost.dll`。
- 根因：`SandboxHostInstaller` 优先找应用目录下的 `sandbox-host`，找不到就退回应用目录；WPF / MAUI
  输出目录里只有 host 的 exe / deps / runtimeconfig，没有 `SDemo.DesktopAgent.SandboxHost.dll`，
  安装器把不完整 payload 复制到 `sandbox\host`。`dotnet publish` 或测试输出目录恰好完整，所以此前没暴露。
- 修复：新增 `src/SDemo.DesktopAgent.SandboxHost/SandboxHostPayload.targets`，由 WPF 与 MAUI-Windows
  在 Build / Publish 后把 host 完整输出复制到 `sandbox-host\`；复制后校验 managed dll 存在，否则构建失败。
- 验证：重新构建后 `bin\...\sandbox-host\SDemo.DesktopAgent.SandboxHost.dll` 存在；应用启动后
  `sandbox\host` 被覆盖为完整 payload，PowerShell 从 HostCrashed 恢复为正常执行。

### P1（未修复，阻塞 S6 删除能力）AppContainer 内 PowerShell 的相对路径删除 / 重命名失败

- 现象：脚本内 `Remove-Item 'doomed.txt'`、`cmd /c del doomed.txt`、`Rename-Item`、删除目录都返回
  `Access is denied`；但 `New-Item` / `Set-Content` 正常，`[System.IO.File]::Delete('doomed.txt')` 正常。
- 已排除：work 目录和文件对 AppContainer SID 的 DACL 是 `(F)`；`Everyone` 显式 `(F)` 也无效；
  文件设为 Low integrity 也无效；`SetLowIntegrity` 不是原因。
- 关键线索：wrapper 把 PowerShell 当前位置设为 `SDEMO:\`（自定义 PSDrive）。在该位置下相对路径删除会失败；
  这是 S2 为绕过 AppContainer 内 `Set-Location` 的访问检查而做的设计。
- 影响：真实用户脚本用 `Remove-Item` 删除文件不会产生 `Deleted` 变更，S6 的删除面板在实际脚本路径上不可用。
  本次删除面板验收用宿主侧删除模拟了 diff。
- 建议后续：不要在 PSDrive 下执行脚本（例如给祖先目录补 Traverse 后 `Set-Location` 真实路径），
  或为删除类命令提供受限的 provider / host 删除通道；修复后补真实 AppContainer 删除 e2e 测试。

### P2（未修复）Windows Sandbox 实际执行 stdout 为空

- 现象：VM 内脚本退出码 0，`output/stdout.txt` 只有 2 字节，`result.json.stdout` 为空；
  模型 / 用户看不到脚本输出。自检只校验了启动 / 执行 / 网络，没有校验 stdout 内容。
- 影响：Windows Sandbox 模式下工具结果不可读，只能看到退出码与变更集。
- 建议后续：核对 `run.ps1` 的 `Start-Process -RedirectStandardOutput` 在映射目录上的行为，
  改为直接 `& script | Out-File` 或让 `run.ps1` 自己写 `result.json.stdout`；自检增加 stdout 断言。

### P3（未修复）PowerShell 启动在 work 副本里写 ModuleAnalysisCache

- 现象：每次执行后变更集出现
  `Microsoft/Windows/PowerShell/ModuleAnalysisCache`（约 121KB，二进制），需要用户手动取消勾选。
- 影响：污染变更面板；用户误选会把缓存写回真实工作区。
- 建议后续：设置 `$env:PSModuleAutoAnalysis` 或 `-NoProfile` 之外再禁用分析缓存；
  或在 diff 中排除 `Microsoft/Windows/PowerShell/ModuleAnalysisCache`。

### P4（已修复）Windows Sandbox 可用性测试在本机安装功能后失败

- `WindowsSandboxAvailabilityTests.本机检测结果自洽` 假设“可用 ⇒ Reason 为 null”，但未自检时
  `Reason = "尚未运行 Windows Sandbox 自检。"`。安装 Windows Sandbox 后该用例暴露。
- 修复：可用且未验证时断言 Reason 包含“自检”；已完成自检时仍断言 null。

## 5. 未覆盖

- Win10 Home 环境未验证（本机是 Win11 Pro 26H2）；Win11 Pro + 标准用户已覆盖。
- Windows Sandbox 内**文件变更** / Windows Sandbox 下的删除面板未验证（本次 WS 执行是只读脚本）。
- 管理员账户、组策略禁用沙箱、Defender / ASR 拦截等场景未验证。
- 长期运行（不重启应用）时 Janitor 不会周期性清理；本次只验证启动清理与手动清理。
- 审计 10MB 轮转只在单测覆盖，未在 UI 里生成 10MB 日志。

## 6. 验收后的产品调整（2026-10-22）

按后续需求，文件读写 / PDF / PowerShell 改为**内置能力**（`AgentToolPackDescriptor.BuiltIn`）：

- 输入框不再显示这三个 chip，只保留联网搜索这类可选外部工具。
- 文件 / PDF / PowerShell 默认开启；PowerShell 的 `DefaultEnabled` 从 false 改为 true。
- 每轮生成时 ChatRoom 自动把可用内置包加入工具列表，由模型自行决定是否调用；
  用户仍可在设置页全局关闭某个内置工具。
- 实测（同一套真实 UI）：输入框只剩「联网搜索」；新会话直接说“列出当前工作区文件”，
  模型自动调用 `list_files` 并返回 `hello.txt`；直接说“用 PowerShell 运行 Get-Location”，
  无需 chip 即弹出 PowerShell 审批框。
- 内置包可用性仍受设置 / 沙箱状态约束：沙箱不可用时不会把 PowerShell / PDF 交给模型。

## 7. 复现 P0 / P1

P0：

```powershell
dotnet build SDemo.DesktopAgent.Wpf.slnf
Test-Path src\SDemo.DesktopAgent\bin\Debug\net10.0-windows10.0.19041.0\sandbox-host\SDemo.DesktopAgent.SandboxHost.dll
# 修复前 False；修复后 True
```

P1（真实 AppContainer）：

```powershell
dotnet test tests\SDemo.DesktopAgent.Sandbox.Tests --filter "FullyQualifiedName~AppContainer作业删除文件产生Deleted变更"
```

该用例在修复 P1 前应保持失败 / 暂不加入套件；修复后用同一用例做回归。
