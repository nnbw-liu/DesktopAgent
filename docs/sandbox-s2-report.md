# 沙箱 S2 实施报告

日期：2026-10-21
分支：`feature/sandbox-s2-workspace`
基线：`feature/sandbox-s1-appcontainer`（S1 PR #8）

## 1. 完成内容

### 1.1 文件协议与 SandboxHost

- `SandboxProtocol` 增加到 v1：`job.json` / `result.json`，统一 camelCase + 枚举字符串。
- 新增 `src/SDemo.DesktopAgent.SandboxHost`（`net10.0-windows10.0.19041.0` 控制台程序）：
  - `run --job <job.json>`；
  - 校验协议版本、`jobId` 与 job 目录一致；
  - PowerShell 模式：脚本放入 `input\script.ps1`，输出写 `output\stdout.txt` / `stderr.txt`；
  - PDF 模式在 S2 明确返回“PDF 模式在 S5 实现”，不静默执行；
  - 结果先写 `result.json.tmp` 再 `File.Move(..., overwrite: true)`，不会留下半截 JSON；
  - 无法读取 / 解析 job 时退出码 3，用法错误退出码 2。
- `SandboxHostInstaller` 把 host 从 `SDEMO_SANDBOX_HOST_SOURCE`、`AppContext.BaseDirectory\sandbox-host` 或输出目录复制到 `sandbox\host\SDemo.DesktopAgent.SandboxHost.exe`。
- Windows / Sandbox.Tests 构建都会把 host 输出复制到 `sandbox-host`，桌面宿主无需手工发布即可找到。

### 1.2 工作区副本、manifest 与 diff

- `WorkspaceSnapshot`：
  - 工作区复制到 `work`，同时备份到 host-only 的 `base`；
  - 跳过 `.git`、`.vs`、`bin`、`obj`、`node_modules`、`.tmp`、`.idea`、reparse point；
  - 文件 ≤4MB 存 SHA-256，超过则存长度 + 最后写入时间；
  - 默认上限 200MB / 20000 文件，超出抛 `WorkspaceTooLargeException`。
- `WorkspaceManifest` 保存 job 根目录，记录路径、长度、时间、哈希与 `applied` 标记，原子保存。
- `WorkspaceDiff` 计算 Added / Modified / Deleted；二进制按 8KB 内 NUL 判断；文本 diff 只对 ≤256KB、≤1200 行的文件生成，输出最多 500 行。
- `TextDiff` 使用 LCS 生成可读统一 diff。

### 1.3 应用变更

- `ChangeApplier`：
  - 只应用 Added / Modified；Deleted 在 S2 只展示、不删除；
  - 校验相对路径必须落在真实工作区内，拒绝 `..`、绝对路径、UNC、设备路径；
  - 拒绝 reparse point；
  - 按 manifest 重新计算哈希 / 时间，检测快照后工作区是否被改动；
  - **先整体预检，任一冲突则一个文件都不写**；
  - 写入使用同目录临时文件 + `File.Replace` / `File.Move` 原子替换；
  - 成功后把 `manifest.json` 对应条目标记 `applied = true`。

### 1.4 UI

- 新增 `SandboxChangesPanel`，挂在 `ChatRoom` 输入框上方：
  - 按 `SessionId` 过滤 `OutcomeAvailable`，切换会话不会串作业；
  - 显示执行状态、退出码、错误输出、变更文件列表（新增 / 修改 / 删除）、大小与文本 diff；
  - 默认勾选所有可应用文件，支持全选、逐项勾选、查看单项 diff；
  - “应用选中”弹确认框；确认后调用 `ChangeApplier`；
  - 冲突时整体中止并列出冲突文件；成功后从未处理列表移除已应用项；
  - “收起”只关闭面板，job 目录保留供审阅，清理由 S6 处理。
- `SandboxJobOutcome` 新增 `WorkspaceRoot`，应用变更时写回**作业开始时的工作区**，不受之后切换工作区影响。

### 1.5 安全修正

调试 AppContainer 写入时发现并修复两个真实缺陷：

1. **AppContainer 低完整性**：仅给 `work` / `home` / `output` 加 ACL 不够，进程仍是 Low integrity，写入被完整性策略拒绝。现对这三个目录设置 Low mandatory label SACL。
2. **ACL 幂等判断漏权限位**：旧逻辑只比较“SID + 继承方式”，父目录已有继承的只读 ACE 时会误以为已授权，导致 `work` 实际只有 RX、PowerShell 写入被拒。现改为同时比较权限位，缺 Modify 时会补显式 ACE。

另：AppContainer 内 PowerShell 5.1 的 `Set-Location` 会因 `NativeFileExists` 的路径检查失败（即使目录可读写）。host 改用 `New-PSDrive` 把 `work` 映射为 `SDEMO:` 再切换，相对路径仍落在工作目录内；这是 S2 的已知兼容性处理，脚本中的 `$PWD` 会显示 `SDEMO:\`。

## 2. 测试

- `SDemo.DesktopAgent.Core.Tests`：155 通过。
  - 路径守卫、快照上限、manifest 原子保存、diff、二进制、冲突、路径逃逸、整体中止。
- `SDemo.DesktopAgent.Sandbox.Tests`：63 通过。
  - Host：正常执行、退出码、中文输出、超时、输出截断、协议版本、jobId 不匹配、路径逃逸、PDF S5、未知模式。
  - JobService：AppContainer 端到端执行、变更集、真实工作区不被直写、工作区超大拒绝。
  - ACL：根 / host / input / work 权限、低权限继承下的 Modify 补齐、幂等。

## 3. 与手册的偏差

1. `WorkspaceSnapshot.CreateAsync` 同时接收 `base` 与 `work` 目录，`base` 为 host-only 快照，用于 diff 与冲突检测。
2. `ChangeApplier.ApplyAsync` 额外接收 `workDirectory`，用于读取副本与定位 `manifest.json`。
3. `WorkspaceManifestEntry` 增加 `Applied` 字段。
4. 冲突策略从“跳过冲突文件、继续应用其他文件”收紧为“任一冲突整体中止”，与手册“冲突时中止全部、不能覆盖”的安全目标一致。
5. PowerShell 使用 `SDEMO:` PSDrive 代替 `Set-Location <物理路径>`。
6. `SandboxJobOutcome` 增加 `WorkspaceRoot`。

## 4. 已知限制 / 后续

- AppContainer 内 PowerShell 运行在 `ConstrainedLanguage`，依赖 .NET 方法调用的脚本会被拒绝；S3 工具说明中需要提示用户。
- Deleted 变更 S2 不自动删除，S4 再实现回收站式删除。
- 沙箱目录暂不自动清理，S6 `SandboxJanitor` 统一处理。
- 超时只保证 Job Object 整棵进程树被终止；host 自身结果写入依赖 runner 的超时余量（job timeout + 30s）。
