# 沙箱故障排查

本文对应 S1–S6 的 Windows 沙箱实现。开发细节见
[sandbox-development-manual.md](../architecture/detail/sandbox-development-manual.md)，S6 验收见
[sandbox-s6-report.md](../reports/sandbox-s6-report.md)。

## 先看这三处

1. 设置 → 执行与沙箱 → 执行沙箱：`AppContainer` / `Windows Sandbox` 的状态与原因。
2. 点「执行沙箱自检」：会真实启动 `cmd` + PowerShell，逐项显示隔离结果。
3. 设置 → 执行与沙箱 → 审计与数据：日志目录、清理入口、被跳过的未应用作业。

## 常见现象

### “AppContainer：不可用：创建 AppContainer profile 失败（HRESULT …）”

- `0x80070005`（拒绝访问）：当前账户被策略禁止创建 AppContainer，或使用了受限账户。
  标准用户通常可用；域策略 / 安全软件拦截时需要管理员放开。
- `0x800700B7`：profile 已存在，实现会自动派生 SID，不是错误。
- 每次自检失败都会缓存原因；修好策略后点「执行沙箱自检」或重启应用，缓存即刷新（可用性探测缓存 10 分钟）。

### “基础进程无法在 AppContainer 内启动” / 启动返回 203

- 203 常见原因是沙箱环境缺少 `LOCALAPPDATA`；当前实现固定注入 `home\AppData\Local`。
- 若仍失败，检查 `%LOCALAPPDATA%\SDemo\DesktopAgent\sandbox\host` 下的 `SandboxHost.exe` 是否存在，
  以及安全软件是否拦截了 AppContainer 进程启动。
- 沙箱不可用时 PowerShell / PDF 工具会直接显示不可用，不会降级到主进程执行。

### Windows Sandbox 显示“不可用”或自检失败

- 仅 Windows Pro / Enterprise / Education 且已在「启用或关闭 Windows 功能」中勾选
  “Windows 沙箱”才可用；家庭版没有该功能。
- 虚拟机启动约 10–30 秒；首次自检会创建 `.wsb` 并等待 `run.ps1` 写结果。
- 自检没通过时 PowerShell 不会切到虚拟机；可以继续用 AppContainer，或点「改用 AppContainer」。

### 输入框里的工具 chip 是灰的

- 设置 → 执行与沙箱：对应工具包被关闭，或沙箱未通过自检。
- 还需要在输入框打开对应 chip（`powershell` / `pdf`）才会交给模型。
- 沙箱总开关关闭时所有高风险工具都不可用，这是预期行为。

### PDF 读取失败

- “PDF 已加密”：需要先解密；沙箱不会尝试破解。
- “未检测到文本层”：扫描件需要 OCR，当前版本没有实现（`ocr=true` 会被明确拒绝）。
- “页范围超出实际页数” / “单次最多读取 50 页”：按提示缩小范围。
- PDF 默认只读前 5 页；附件自动提取固定前 3 页、16,384 字符。

### 变更面板：应用被中止 / 删除被跳过

- 新增 / 修改：真实工作区在沙箱运行后被改过，预检发现冲突会整体中止，一个文件都不写。
  确认无误后重新运行，或手动合并。
- 删除：只删除仍是快照版本的文件；用户改过、文件已消失、路径含 `..` / 符号链接都会跳过。
- 删除的文件在 `sandbox\trash\<jobId>\<相对路径>`，7 天后由清理器删除；需要找回时先拷贝出来。
- 不支持删除目录；目录类变更不会出现在可勾选列表里。

### 沙箱数据越来越大

- 启动时自动清理超过 24 小时的 job、残留 `*.tmp` / `*.wsb`（超过 1 小时）、7 天前的 trash。
- 有未应用变更的 job 会被保留；在变更面板点「应用」或「忽略」后才可能被清掉。
- 设置 → 执行与沙箱 → 审计与数据 → 「清理过期数据」可手动触发；「清理沙箱数据…」会清空 jobs / trash 并删除
  AppContainer profile，需要二次确认。

### 审计日志在哪里 / 为什么没有脚本内容

- `%APPDATA%\SDemo\DesktopAgent\logs\tools-yyyyMMdd.jsonl`（设置页可一键打开）。
- 默认只记 tool / job / sandbox / 脚本 hash 与长度 / 退出码 / 审批结果；不记脚本正文，也不记
  stdout / stderr 内容。需要排查时可临时打开“保留脚本正文（前 4KB）”。
- 保留天数默认 7 天；超过 10MB 的文件会轮转为 `tools-yyyyMMdd-{n}.jsonl`。

## 彻底卸载 / 重置

1. 关闭应用，确认没有正在运行的沙箱作业。
2. 删除 `%LOCALAPPDATA%\SDemo\DesktopAgent\sandbox`（包含 host、jobs、trash）。
3. 删除 AppContainer profile：使用设置页的「清理沙箱数据…」；它会调用
   `DeleteAppContainerProfile("SDemo.DesktopAgent.Sandbox")` 并清空 jobs / trash。
   该入口是可重复执行的：profile 不存在时按成功处理。
4. 如需清空审计与设置：删除 `%APPDATA%\SDemo\DesktopAgent\logs` 与 `settings.json`。

## 收集诊断信息

- 设置页自检结果（含 `Detail`）。
- `%LOCALAPPDATA%\SDemo\DesktopAgent\sandbox\jobs` 下失败作业的 `output\result.json`
  （应用成功后 job 会被删除；可在 `SandboxChangesPanel` 保留期间查看）。
- 审计日志最近几行（注意默认不含脚本正文）。
- `winver`、是否为管理员、是否启用 Windows 沙箱功能。
