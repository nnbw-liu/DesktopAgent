# 沙箱 S6 实施报告：加固与交付

对应手册 §10（加固与交付）与 §11.6 / 附录 E S6 清单。

## 1. 完成内容

### 1.1 删除类变更：回收站而不是永久删除

- `ChangeApplier` 新增重载：
  `ApplyAsync(..., IReadOnlySet<string> selected, string? trashDirectory, CancellationToken)`。
  旧重载保留（`trashDirectory = null`），删除仍按 S2 行为跳过，避免破坏既有调用方。
- 删除流程：
  1. 校验相对路径（拒绝绝对路径、`..`、NUL / UNC、符号链接 / junction）；
  2. 与 manifest 比对长度 / hash / 最后写入时间，确认文件仍是快照版本；
  3. 目标不存在或用户改过 → 记入 `Skipped`，不删除；
  4. 移动到 `{sandbox}\trash\{jobId}\{relativePath}`（保留相对路径，便于找回）；
  5. 写入 `applied.json`，Janitor 据此判断 job 是否还有未处理变更。
- 删除不递归：目录不会出现在可勾选变更里；`WorkspaceDiff` 仍只输出文件级变更。
- 新增 / 修改的冲突语义保持不变：只要有一条写冲突，整次写回中止，一个文件都不写。
- `SandboxJobOutcome` 新增 `TrashDirectory`，由 `SandboxJobService` 在作业完成时填充；
  `SandboxDirectory` 新增 `TrashRoot` / `GetTrashDirectory(jobId)`。

界面（`SandboxChangesPanel.razor`）：

- 删除项默认不勾选；没有 trash 目录（旧结果 / 非 Windows 实现）时禁用复选框。
- 二次确认文案明确区分“新增 / 修改写回”与“删除移到 trash”，并逐个列出将移除的文件。
- 关闭面板（忽略）时把当前显示的变更路径记为“已处理”，避免 Janitor 永久保留该 job。

### 1.2 SandboxJanitor

`src/SDemo.DesktopAgent.Windows/Sandbox/SandboxJanitor.cs` 从占位实现改为真实清理器：

| 对象 | 策略 |
| --- | --- |
| `jobs\<jobId>` | 超过 24 小时且没有未处理变更才删除 |
| 未处理变更 | 读取 `manifest.json` + `applied.json`，重新算 diff；有未记录路径 → 跳过并在报告里列出 |
| 残留 `*.tmp` / `*.wsb` | 超过 1 小时删除；跳过 `host` 与 `trash` |
| `trash\<jobId>` | 超过 7 天删除 |
| 彻底清理 | 删除全部 jobs / trash 条目 / 残留文件，并删除 AppContainer profile（host 保留） |

- 判定“未处理”时若 manifest / base / work 缺失、JSON 损坏或 IO 失败，一律按“有未处理变更”保留，宁可多留。
- 历史 S2–S5 作业没有 `applied.json`：只要算得出 diff 就会被保留，不会误删用户没确认的内容。
- Janitor 把最后一次清理结果缓存为 `LastReport`；启动清理跳过的 job 会在设置页显示为“待处理”，
  手动清理也会即时显示。
- 清理不按进程名杀进程；Windows Sandbox 残留文件由文件清理覆盖，不做全局进程终止。
- `ISandboxMaintenance` 在 Core 中定义，Windows 注册 `SandboxJanitor`，Android 注册空实现；
  WPF 与 MAUI Windows 在启动时调用 `CleanupAsync()`，失败不影响启动。
- 设置页新增“清理过期数据”“清理沙箱数据…”（二次确认），后者会调用
  `DeleteAppContainerProfile("SDemo.DesktopAgent.Sandbox")` 并 `ResetProfile` 缓存。

### 1.3 审计日志

`JsonlToolAuditLog`：

- 保留天数读取 `SandboxSettings.AuditRetentionDays`（默认 7，限制 1–365），
  进程内首次写入时删除更早的 `tools-*.jsonl`。
- 单文件达到 10MB 后轮转为 `tools-yyyyMMdd-{n}.jsonl`，再继续写当天主文件；
  轮转失败则继续追加，优先保证不丢记录。
- 仍然默认不写脚本正文、不写 stdout / stderr；`RetainScriptsInAuditLog` 打开时只写前 4KB。
- `ToolAuditEntry` 新增 `changes` 明细：每条只记 `kind`（`Deleted` / `Modified` / `Added`）与
  `pathSha256`（相对路径的 SHA-256），不记路径原文或文件内容；`run_powershell` 完成时填充。
- IO 异常继续吞掉，审计失败不影响工具执行。
- 设置页新增“打开日志目录”（`IAppEnvironmentService.OpenFolder`，WPF / MAUI Windows 实现）
  与保留天数、保留脚本正文开关。

### 1.4 性能与体验

- 可用性探测缓存从 5 分钟调整为 10 分钟（手册 §10.5）。
- 工作区快照继续使用 `WorkspaceCopyOptions` 默认值：跳过 `.git / .vs / bin / obj / node_modules / .tmp / .idea`，
  200MB / 20,000 文件上限，超限直接报错；不跟随符号链接。
- `SandboxHost` 自包含发布继续复用 `sandbox\host`，`SandboxHostInstaller` 按 exe / 长度 / 时间增量复制。
- AppContainer profile 进程内缓存复用；PowerShell 每次执行仍走审批。
- PDF 默认读前 5 页（`PdfPageRange.DefaultPages`），附件自动提取固定前 3 页、16,384 字符（S5 既有行为）。

### 1.5 文档

- `README.md`：新增“沙箱执行（S1–S6 已完成）”状态表、路径、S6 行为要点。
- 新增 `docs/sandbox-troubleshooting.md`：常见故障、变更冲突、trash 找回、审计与清理、卸载 / 诊断。
- 本报告记录验收状态、偏差、已知限制、发布 / 回滚演练。

## 2. 测试

| 套件 | 结果 |
| --- | --- |
| `SDemo.DesktopAgent.Core.Tests` | **231 通过**（S5 为 225；新增 3 个删除应用测试 + 3 个审计保留 / 轮转 / 变更明细测试） |
| `SDemo.DesktopAgent.Sandbox.Tests` | **200 通过 + 1 跳过**（S5 为 193；新增 6 个 Janitor 测试；P1 删除回归位暂时 Skip） |
| 合计 | **431 通过，1 跳过，0 失败** |

新增 / 覆盖重点：

- `ChangeApplierTests`：删除移动到 trash、用户改过后跳过、删除路径穿越拒绝、`applied.json` 记录。
- `SandboxJanitorTests`：过期 job 清理、未处理变更跳过、已处理 job 可清理、`tmp` / `wsb` 清理边界
  （host / trash 跳过）、trash 7 天、彻底清理 + profile 删除。
- `ToolAuditLogTests`：保留天数清理、10MB 轮转、变更明细只写 kind / 路径 hash。
- 既有安全用例继续通过：`SandboxEnvironmentTests`（无 API Key / 用户变量）、
  `AppContainerSandboxRunnerTests`（不能读用户目录、超时、取消）、`JobObjectTests`（进程 / 内存上限）、
  `SandboxHostTests`（超时、输出截断、协议错、路径逃逸、PDF 缺 input、OCR 拒绝）、
  `PdfToolPackTests`（路径 / 符号链接 / 大小）、`WindowsSandboxConfigTests`（网络 / 剪贴板 / vGPU 关闭）。

构建：

- `dotnet build SDemo.DesktopAgent.Wpf.slnf`：成功，0 警告 / 0 错误。
- `dotnet build src/SDemo.DesktopAgent.Maui/... -f net10.0-windows10.0.19041.0`：成功。
- Android TFM 仍因本机缺少 workload 无法构建（S5 已知问题，与 S6 改动无关）。

### 2.1 安全回归矩阵

| 风险 | 现有保护 | 覆盖 |
| --- | --- | --- |
| 沙箱不可用时降级直跑 | Selector / 工具包只接受 `Available`；Service 拒绝执行 | `SandboxSelectorTests`、`PowerShellToolPackTests`、`PdfToolPackTests` |
| 读取用户目录 / 工作区外文件 | AppContainer Package SID ACL；路径 jail；拒绝 reparse point | `AppContainerSandboxRunnerTests`、`SandboxAclTests`、`SandboxPathGuardTests` |
| 环境变量泄密（API Key / Token） | 环境白名单 + 沙箱内 HOME | `SandboxEnvironmentTests` |
| 联网 | AppContainer 无 internet capability；`.wsb` 固定 `Networking=Disable` | 自检 `NoNetwork`、`WindowsSandboxConfigTests` |
| 资源耗尽 | Job Object 内存 / 进程数 / 超时 / 输出上限 | `JobObjectTests`、`SandboxHostTests` |
| Worker 崩溃拖垮主进程 | helper 独立进程；无 `result.json` → `HostCrashed` | `SandboxHostTests`（缺 result 场景由 `ReadResult` 映射） |
| PDF 路径逃逸 / 恶意文件 | host 内 `ResolveUnder(jobRoot, ..., "input")`；工具包双重校验 | `PdfExtractTests`、`PdfToolPackTests` |
| 误删用户文件 | 删除前 hash 比对；移入 trash；默认不勾选；二次确认 | `ChangeApplierTests` |
| 清理误删未确认变更 | Janitor 只删“无未处理 diff”的 job | `SandboxJanitorTests` |
| 审计泄密 | 默认不写脚本正文 / stdout / stderr | `ToolAuditLogTests` |

## 3. 真机验收状态（重要）

- 本机：Windows 11 Pro 26H2（Build 26200），标准用户（Administrators deny-only），.NET SDK 10.0.401；
  Windows Sandbox 功能已启用。
- AppContainer 真实集成测试（Sandbox 套件）全部通过，包括 runner、自检、PDF e2e、Job Object。
- Windows Sandbox：已完成功能安装；自检通过（27.3 秒，启动 / PowerShell / 无网络），实际只读脚本执行退出码 0；
  但 stdout 为空（P2），文件变更 / 删除未在 VM 内验证。
- **Win10 Home 未验证**：本机是 Win11 Pro 26H2；Win11 Pro + 标准用户的 UI / 沙箱验收已完成。
- 已做一轮真实 UI 点击验收，详见
  [sandbox-ui-acceptance-s6.md](sandbox-ui-acceptance-s6.md)：
  设置、审计、清理、彻底清理、删除面板（宿主模拟 diff）均通过。
- **UI 验收发现的阻塞项**：AppContainer 内 `Remove-Item` 相对路径失败（P1）、Windows Sandbox stdout 为空（P2）、
  host payload 缺 dll（P0，已修复）。P1 修复前，真实脚本删除不会产生 `Deleted` 变更，S6 删除能力不算完成。
- 发布 / 回滚演练：仓库没有安装包 / 发布流水线，本次只做文档化演练（§6），未实际发布。
- 本阶段 PR：#13 `https://github.com/nnbw-liu/DesktopAgent/pull/13`（stacked 在 S5 PR #12 之上）。

## 4. 与手册的偏差

1. **trash 保留时长**：手册只要求“移到 trash”，未给期限。实现取 7 天（与审计默认一致），
   由 `SandboxJanitor.TrashMaxAge` 常量控制，后续可提到设置项。
2. **“清理沙箱数据”保留 host**：手册的卸载流程是删除整个沙箱根；设置页入口为了下次可用性，
   只删 jobs / trash / 临时文件与 profile，保留 `sandbox\host`。真正卸载仍由用户删除目录完成。
3. **profile 删除入口**：仓库没有安装器 / 卸载脚本，`DeleteAppContainerProfile` 只在设置页清理入口调用；
   卸载脚本如需复用，可直接调用同一 P/Invoke。
4. **快照复制进度**：手册性能节提到 1MB 缓冲 + 进度回调；当前仍用 `File.Copy`（OS 缓冲）且没有进度 UI，
   200MB 上限与排除规则已生效。没有为了进度条重写复制路径。
5. **未处理变更的判定**：用 `applied.json`（应用或忽略的路径）而不是 manifest 的 `Applied` 标志。
   Added 文件不在 manifest 里，单靠 `Applied` 无法判断，故新增标记文件。
6. **Janitor 触发时机**：只在应用启动与设置页手动触发；应用长期不重启不会周期性清理。
7. **目录删除**：不支持，保持手册默认（不做递归删除）。
8. **Windows Sandbox 残留进程**：不按进程名全局杀；只清理文件 / 目录。硬杀或断电留下的虚拟机进程需用户手动处理。

## 5. 已知限制 / 后续

- **P1（阻塞删除）**：AppContainer 内 PowerShell 当前目录是 `SDEMO:` PSDrive，`Remove-Item` / `Rename-Item`
  的相对路径会 `Access is denied`（`[IO.File]::Delete` 正常）。真实脚本因此不会产生 `Deleted` 变更；
  修复前 S6 删除能力不完整，回归位 `AppContainer作业删除文件产生Deleted变更` 暂时 Skip。
- **P2**：Windows Sandbox 实际执行脚本退出码 0，但 `stdout.txt` / `result.json.stdout` 为空；VM 内输出不可读。
- **P3**：PowerShell 启动会在 work 副本写 `Microsoft/Windows/PowerShell/ModuleAnalysisCache`，污染变更面板。
- **P0（已修复）**：WPF / MAUI 输出目录缺少 `sandbox-host` 完整 payload；已加构建复制与 dll 校验。
- 扫描件 OCR 仍未实现：`job.json` 的 `ocr` 字段保留，`ocr=true` 会被明确拒绝；后续接 OCR 引擎时
  在 `SandboxHost` 内实现并保持“无网络、只读 input”的约束。
- trash 没有 UI 浏览 / 一键还原；用户需到 `sandbox\trash\<jobId>\` 手动取回。
- 审计轮转按“写入前检查”，单条记录可能让文件略超 10MB；命名 `-1`、`-2` 递增。
- 删除冲突按文件跳过而不是整批中止：一个删除被跳过，其余删除仍会执行；写入冲突仍是整批中止。
- 变更面板关闭后 job 仍保留到 24 小时；频繁产生大工作区副本的用户可手动“清理过期数据”。
- Android 端仍是 `UnavailableSandbox*`：高风险工具不可用，UI 的维护入口为空实现。

## 6. 发布 / 回滚演练（文档化）

### 发布前

- [x] `dotnet build SDemo.DesktopAgent.Wpf.slnf` 0 警告 / 0 错误
- [x] Core / Sandbox 套件全部通过
- [x] MAUI Windows 目标可构建
- [x] README / 故障排查 / 本报告更新
- [ ] Win10 Home + Win11 Pro + 标准用户手工验收（Win11 Pro + 标准用户已验收；本机无 Win10 Home）
- [ ] Windows Sandbox 真机 e2e（自检通过；实际脚本 stdout 为空，P2）

### 发布

- 合并 PR 后按现有 WPF / MAUI 发布流程出包；无数据库迁移，协议仍为 v1。
- 新增文件 `applied.json`、`trash\` 目录对旧版本无影响；旧版本忽略它们。
- 默认设置：沙箱开启、Required + AppContainer；文件 / PDF / PowerShell 作为内置能力默认开启，
  由模型按需调用（S6 UI 验收后调整），PowerShell 仍每次审批。

### 回滚

1. 回退到 S5 合并点；旧版本不读 `applied.json`，trash 目录会被当作普通残留数据保留。
2. 如需立即止血：设置页关闭对应工具包（`powershell` / `pdf`），不需要回滚代码。
3. 如 Janitor 误删（理论上只会删 24 小时前且无未处理 diff 的 job）：从备份恢复工作区；
   审计日志仍可查 job / 工具 / 退出码。
4. 回滚后再次升级：S6 会清理旧 job，不会尝试恢复已删除的 trash（超过 7 天即删）。

## 7. 手工验收清单（手册附录 E S6）

- [ ] 删除变更：二次确认 + 移入 trash（应用 / trash / 标记已通过 UI 验收；真实脚本删除被 P1 阻塞）
- [x] Janitor：24h / tmp / wsb / trash / 未处理变更保护（单测 + UI 清理验收）
- [x] 审计：保留天数、10MB 轮转、默认不落脚本正文（单测 + UI 开关 / 打开目录验收）
- [x] 安全回归：矩阵见 §2.1，既有真实沙箱用例全部通过
- [x] 性能：探测缓存 10 分钟、排除目录、200MB 上限、host / profile 复用
- [x] 文档：README / 故障排查 / 本报告
- [ ] 发布 / 回滚演练：已文档化，未实际发布（仓库无发布流水线）
- [ ] Win10 Home + Win11 Pro + 标准用户三环境手工验证：Win11 Pro + 标准用户已验收，Win10 Home 无环境
- [ ] P1：AppContainer 内 `Remove-Item` 相对路径失败，修复后启用跳过的回归测试
