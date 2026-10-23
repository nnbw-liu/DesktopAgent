# 沙箱 S5 实施报告：PDF Helper

## 1. 完成内容

### 1.1 契约与页范围

- 新增 `PdfPageRange`：
  - `Parse(value, pageCount)` / `TryParse` / `TryValidateSyntax`；
  - 支持 `1-5,8`，默认 `1..min(5, pageCount)`；去重、升序；
  - 单次最多 50 页；页号从 1 开始；超出真实页数报错；
  - 错误信息为中文（`页范围格式不正确…` / `页范围超出 PDF 实际页数…` / `单次最多读取 50 页…`）。
- 新增 `PdfLimits`：单次 50 页、40,000 字符、默认 20,000；
  附件 25MB / 工具 50MB；附件提取 3 页、16,384 字符；`pdf_info` 抽样 3 页。
- `PdfResultDocument`（`pdf.json`）字段：`ok / pageCount / encrypted / hasTextLayer / truncated / pages / message / jobId / protocolVersion`。
- `SandboxProtocol` 新增 `PdfInputFileName = "document.pdf"`；`PdfResultFileName = "pdf.json"` 在 S0 已存在。

### 1.2 SandboxHost PDF 命令

`src/Sandbox/SDemo.DesktopAgent.SandboxHost/PdfCommand.cs`：

- `inputFile` 经 `SandboxPathGuard.ResolveUnder(jobRoot, ..., "input")` 校验，拒绝 `..` / 越界；
- `PdfDocument.Open` 使用 `UseLenientParsing = true`、`SkipMissingFonts = true`；
- `PdfExtract`：按页范围取 `page.Text`，按 `maxChars` 截断并置 `Truncated`；
- `PdfInfo`：返回页数 + 抽样前 3 页判断文本层；
- `PdfDocumentEncryptedException` → `encrypted: true`；其它异常 → `ok: false` + `PDF 解析失败：…`；
- 结果原子写 `output/pdf.json`（`.tmp` + `File.Move(overwrite: true)`），同时写 `result.json` 报告执行状态；
- `ResultWriter.WriteAtomic<T>` 泛型化，PDF / 脚本共用。

`job.json` 预留 `ocr` 字段：S5 没有任何 OCR 实现，显式 `ocr=true` 会被 host 按协议错误拒绝
（`OCR 尚未实现（ocr=true 暂不支持）。`），而不是悄悄退化成“只读文本层”。

`Program.Validate` 对 PDF 模式要求 `inputFile`，并按 `PdfExtract` / `PdfInfo` 分发。
依赖锁定 `PdfPig` 0.1.16（见 §3 偏差 1）。

### 1.3 宿主作业服务

`SandboxJobService.ExtractPdfAsync`：

1. 沙箱总开关关闭 → `ERROR: 执行沙箱已在设置中关闭。`；
2. 路径规范化、拒绝沙箱目录内的文件、扩展名 / 空文件 / 大小（50MB）校验；
3. 页范围先做语法校验，真实页数由 helper 复核；
4. **只选择 AppContainer runner**：不读 `SandboxSettings.Preferred`，不要求 `Verified`，只要求 `Available` + profile 可用；
5. 复制 PDF 到 `job/input/document.pdf`，写 `job.json`（`PdfExtract`、`pages`、`maxChars`、`timeout` 1–120）；
6. `CreateAppContainerRequestAsync`（超时 +30 秒）执行 `SandboxHost run --job`；
7. `ReadPdfResult` 读回 `pdf.json`，校验 `protocolVersion` 与 `jobId`；
8. `finally` 删除 job 目录（不产生变更集，也不把用户文档留在沙箱目录）。

`ReadPdfResult` 区分：超时 / 取消 / 未启动 / 协议错误 / helper 崩溃 / `pdf.json` 无效。
基础设施类失败统一加 `ERROR:` 前缀，解析失败与加密不加前缀——模型因此能区分“没执行”和“文件本身读不出”。

### 1.4 工具包与格式化

`src/Platform/SDemo.DesktopAgent.Windows/Tools/PdfToolPack.cs`：

- `pdf_info(path)` / `pdf_read(path, pages?, maxChars?)`；
- `WindowsOnly`、默认启用、`Risk = Low`、无需审批，结果写审计（`ToolId` / `Approval = "NotRequired"` / `Error` / `StdoutBytes`）；
- 允许根：当前工作区 + `ToolContext.ReadOnlyRoots`（当前会话附件目录）；
- 相对路径按允许根依次解析；绝对路径必须落在某个允许根内；
- 拒绝 NUL、UNC / 设备路径、`..`、符号链接 / junction；
- 大小上限按根区分：工作区 50MB，附件目录 25MB（与上传上限一致）；
- 只接受 `.pdf`。

`PdfResultFormatter`：

```text
PDF: report.pdf
页数: 12
加密: 否
文本层: 有
页范围: 1-3,5

--- page 1 ---
...

（已截断）
```

加密 / 无文本层 / 解析失败 / helper 崩溃 / 超时的文案与手册 §9.5 一致。
`pdf_info` 不输出分页文本、也不显示“页范围”（它的 `pages` 只是抽样，不是用户请求的区间）。

聊天指令补充：PDF 在沙箱中解析、加密 / 扫描件读不出文本时照实说明、不要编造内容。

### 1.5 附件 sidecar 与上下文

- `IPdfAttachmentExtractor` / `SandboxPdfAttachmentExtractor`：
  - 仅 `.pdf`；沙箱关闭或 `PdfAutoExtract = false` 时直接跳过；
  - 10 秒超时，请求 `1-3` 页、16,384 字符；失败 / 超时 / 无文本层都返回 `null`，不阻塞上传。
- `AttachmentStore`：
  - 保存原文件后尝试提取，成功时写 `<pdf path>.txt`（`PdfSidecar`，UTF-8 无 BOM）；
  - 把 `ExtractedTextPath` / `PageCount` / `HasTextLayer` 写入 `ChatAttachment`；
  - 删除附件时 PDF、sidecar 一起删除。
- `AgentMessageMapper`：
  - 优先读 sidecar，仍受 `AttachmentCharsPerFile` / `AttachmentCharsTotal` 限制；
  - PDF 附件总是附带 `（PDF 共 N 页，文件路径：…）`，模型可以继续用 `pdf_read` 读其他页；
  - 无文本层 → OCR 提示；提取未完成 → `pdf_read` 重试提示。
- `AgentChatService` 通过 `AgentAttachmentRoots.Build` 把 `{UploadsRoot}\{sessionId}` 作为只读根传给工具：
  会话 id 带分隔符 / `..` 或目录不存在时不授予，避免把可读范围扩到 uploads 之外。

### 1.6 设置与界面

- `SandboxSettings.PdfAutoExtract`（默认 `true`，旧设置文件仍可加载）。
- 设置 → 工具设置新增「PDF」区：
  - 启用 PDF 工具开关（默认开启）；
  - 上传时自动提取前 3 页开关；
  - AppContainer 状态行（可用 / 不可用 + 原因）；
  - 上限与 OCR 说明。
- 输入框 PDF chip：需要沙箱总开关打开且 AppContainer `Available`（不要求 `Verified`）；
  不可用时禁用并给出原因。
- 附件 chip 在输入框与消息里显示 `· N 页`。
- DI（Windows 组合）：注册 `IAgentToolPack -> PdfToolPack`、`IPdfAttachmentExtractor -> SandboxPdfAttachmentExtractor`；
  Android 只有 `UnavailableSandbox`，不引用 Windows 工程，因此不会注册 PDF 工具（符合预期）。

### 1.7 升级路径

`SandboxHostInstaller` 原本只判断 `sandbox\host\SDemo.DesktopAgent.SandboxHost.exe` 是否存在。
S5 给 host 新增了 PdfPig 依赖，S4 留下的旧安装会缺 DLL，导致所有 PDF 作业失败。
现改为 `IsPayloadCurrent` 比对发布目录与已安装目录的文件集合 / 大小 / 最后写入时间，
不一致就覆盖复制；同一份 payload 仍然只复制一次。

## 2. 测试

- `SDemo.DesktopAgent.Core.Tests`：**225 通过**（S4 为 187，新增 38）：
  - `PdfPageRangeTests`：默认页数、区间 / 逗号 / 空格 / 去重 / 升序、`0` / 倒序 / 非法格式 / 越界 / 超过 50 页；
  - `PdfResultFormatterTests`：页数、页范围压缩（`1-3,5`）、截断、无文本层、加密、失败原因、`pdf_info` 不输出分页；
  - `PdfAttachmentContextTests`：sidecar 内联 + 页数 + 路径、扫描件 OCR 提示、提取失败重试提示、普通文本附件不受影响；
  - `PdfSidecarTests`：路径约定、UTF-8 无 BOM、目标目录不存在时返回 `null`；
  - `AgentAttachmentRootsTests`：合法 session 目录、目录不存在、非法 session id（`..` / 分隔符 / 空串）；
  - `SandboxSettingsTests`：默认 `PdfAutoExtract = true`。
- `SDemo.DesktopAgent.Sandbox.Tests`：**193 通过**（S4 为 139，新增 54）：
  - `PdfExtractTests`（19）：直接运行 `SandboxHost`，覆盖页范围、默认 5 页、字符截断、空白页 / 无文本层、
    损坏 / 加密、`PdfInfo`、`inputFile` 逃逸与缺失、文件不存在、`pdf.json` 原子写、OCR 预留位显式拒绝；
  - `PdfJobServiceTests`（7）：真实 AppContainer 端到端，覆盖指定页 / 默认页数 / 越界 / 加密 / 损坏 /
    沙箱关闭 / 非 PDF 与不存在文件，并断言 job 目录无残留；
  - `PdfToolPackTests`（16）：工作区与附件根、绝对路径允许根内、越界 / `..` / UNC 拒绝、扩展名、空文件、
    50MB / 25MB 上限、符号链接、`pdf_info` 参数、沙箱不可用文案与审计、工具描述；
  - `SandboxPdfAttachmentExtractorTests`（6）：默认页数 / 字符、截断提示、关闭自动提取、关闭沙箱、失败 / 无文本层、非 PDF；
  - `SandboxHostInstallerTests`（4）：缺 exe、payload 一致复用、新增 DLL、目标文件被替换；
  - `WindowsSandboxCompositionTests`（+1）：PDF 工具包与附件提取器都注册。
- 构建：
  - `dotnet build SDemo.DesktopAgent.Wpf.slnf` 通过（0 警告 / 0 错误）；
  - `dotnet build src/Hosts/Maui -f net10.0-windows10.0.19041.0` 通过；
  - Android 目标未能在本机验证：当前 SDK 缺 `android` workload（`NETSDK1147`）。
    Maui 工程的 Windows 专属引用（Windows 工程、沙箱、PDF 提取器）都带
    `TargetPlatformIdentifier == 'windows'` 条件，Android 不会引用。

## 3. 真机验收状态（重要）

| 项目 | 值 |
| --- | --- |
| 系统 | Windows 10 Pro 26H2（Build 26340.9616） |
| 用户 | 标准用户（Administrators deny-only） |
| AppContainer | 可用，PDF 端到端用例通过（`PdfJobServiceTests`） |

- 加密样本是手工构造的最小 PDF（带 `/Encrypt`、空密码无法解开），只验证“加密分支被正确识别”，
  不是真实加密工具产物；
- 扫描件用空白页模拟（没有文本层），不是真正的图片 PDF；
- **未执行**：手工上传 PDF / 扫描件 / 加密 PDF 的 UI 流程，以及“模型在对话中引用 PDF 文本”；
  留给 §5 手工验收。

## 4. 与手册的偏差

1. **包 id**：手册写 `UglyToad.PdfPig`，实际包 id 是 `PdfPig`（命名空间仍是 `UglyToad.PdfPig`），锁定 `0.1.16`。
2. **绝对路径**：手册 §9.5 写“拒绝绝对路径”；实现允许**落在允许根内**的绝对路径，
   根外的绝对路径仍然拒绝。原因：上下文里的附件路径、`pdf_read` 的后续重试都用绝对路径，
   一律拒绝会让工具无法读取刚上传的文件。安全性由“根 + 逐段重解析点检查”保证。
3. **`pdf.json` 必填 `jobId` / `protocolVersion`**：手册示例没有这两个字段；实现要求并由宿主校验，
   防止读到其他 job / 旧版本的结果。
4. **`pdf_info` 不读元数据**：手册要求读 `document.Information`；实现只返回页数、文本层、抽样页，
   避免为元数据扩张 `PdfExtraction`（S6 如需要再加）。
5. **`Truncated` 语义**：手册示例为 `total >= maxChars`；实现只在真的截掉字符时置 `true`，
   正好读满上限不算截断。
6. **附件提取接口**：手册签名为 `ExtractAsync(path, maxPages, maxChars, ct)`；实现为
   `ExtractAsync(path, ct)`，页数 / 字符上限固定在 extractor（3 页 / 16K），避免 UI 层传任意值。
7. **提取失败静默**：手册要求“记录日志”；`AttachmentStore` 没有 logger，失败直接保留二进制附件，
   由上下文提示与设置页说明兜底。
8. **job 目录立即删除**：手册未明确 PDF job 的保留策略；实现解析完即删（不产生变更集），
   S6 janitor 不需要额外处理 PDF。
9. **PDF 默认启用**：手册未规定默认值；S3 的 PowerShell 默认关闭，而 PDF 只读、无审批，
   默认开启以便开箱即用，用户可在设置中关闭。
10. **聊天指令**：新增 PDF 相关一句，手册未要求，属于提示词加固。

以上第 1–4 项（绝对路径允许根内放开、PDF 默认启用、`pdf_info` 不读元数据、job 目录即时删除）
已在评审中与用户确认保留；OCR 相关见 §5。

## 5. 已知限制 / 后续

- **OCR 预留、尚未实现**：
  - 协议层已在 `job.json` 保留 `ocr` 字段，`SandboxHost` 对 `ocr=true` 明确拒绝，避免静默忽略；
  - 后续接入时只需在 `PdfCommand` 内按 `ocr` 分支调用 OCR 引擎（建议仍在 helper 进程内运行，保持崩溃隔离与无网络），
    在 `pdf.json` 增加 `ocrUsed` / 引擎标识，并在设置页加“启用 OCR”开关与相应文案；
  - 当前扫描件只能明确提示“无文本层”，不会假装读到了内容。
- 加密 PDF 不支持密码；后续可让用户输入密码，经宿主内存传给 helper（不要写进 `job.json`）。
- `page.Text` 是 PdfPig 的阅读顺序近似，复杂多栏排版可能串行；S6 之后再考虑 `ContentOrderTextExtractor`。
- 附件固定提取前 3 页 / 16K，暂不可配置。
- `pdf_info` 无标题 / 作者 / 创建时间。
- 每次 PDF 调用都会 `ProbeAsync` AppContainer；冷启动约 1–2 秒，可在 S6 缓存可用性。
- 50MB PDF 复制到 job 目录有临时磁盘峰值（解析后立即删除）。
- helper 崩溃时只能看到 `result.json` 缺失 / 退出码，具体崩溃原因需要 S6 的诊断日志。
- Windows Sandbox 不提供 PDF helper（S5 范围如此）；`Preferred = WindowsSandbox` 时 PDF 仍走 AppContainer。

## 6. 手工验收清单

1. 设置 → 工具设置 → PDF：确认工具开关、自动提取开关、AppContainer 状态行与上限说明。
2. 上传文本 PDF：
   - 输入框附件 chip 显示大小与 `N 页`；
   - 发送消息后模型能引用前 3 页文本；
   - `%LOCALAPPDATA%\SDemo\DesktopAgent\uploads\<sessionId>\*.pdf.txt` 存在且为 UTF-8。
3. 打开 PDF chip，要求“读第 5-6 页”：模型调用 `pdf_read`，回答包含这些页的文本；
   工具结果里能看到 `页范围: 5-6` 与 `--- page 5 ---`。
4. 扫描件（无文本层）：消息里出现“未检测到文本层 / OCR”提示，模型不会编造内容。
5. 加密 PDF：出现“PDF 已加密，当前不支持在对话中传密码。”
6. 损坏 PDF：出现“PDF 解析失败：…”；主程序不崩溃。
7. 请求 `..\..\Windows\win.ini`、`\\server\share\a.pdf`、`notes.txt`、不存在的文件：都被明确拒绝。
8. 关闭执行沙箱：PDF chip 变为不可用并给出原因；上传 PDF 仍成功，只是没有 sidecar。
9. 解析期间用任务管理器确认：只有 AppContainer 内的 `SandboxHost.exe` 在解析；主进程模块中没有 PdfPig。
10. 解析结束后 `%LOCALAPPDATA%\SDemo\DesktopAgent\sandbox\jobs` 为空。
11. 断网后仍能解析（PDF 工具不需要网络）。
12. 从 S4 升级：保留旧的 `sandbox\host`，启动后确认它被新 payload 覆盖（目录里出现 PdfPig DLL），PDF 可用。
