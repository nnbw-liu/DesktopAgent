# SDemo.DesktopAgent 沙箱接入开发手册（S1–S6）

> 版本：1.0  
> 状态：设计基线，待实施  
> 适用范围：WPF 宿主 + MAUI Windows 宿主；Android 不在本手册实施范围  
> 前置：工具包注册表（Tool Pack / ToolRegistry）已落地；若尚未落地，先完成第 4 章的“S0 公共骨架”和第 7.2 节的最小工具包契约  
> 核心约束：PowerShell、PDF 解析等高风险能力**必须**运行在沙箱内；没有可用沙箱时拒绝执行，不回退到无沙箱直跑

---

## 目录

1. [手册使用说明](#1-手册使用说明)
2. [阶段总览](#2-阶段总览)
3. [设计基线](#3-设计基线)
4. [S0：公共骨架](#4-s0公共骨架)
5. [S1：AppContainer 可行性验证](#5-s1appcontainer-可行性验证)
6. [S2：沙箱文件协议与工作区副本](#6-s2沙箱文件协议与工作区副本)
7. [S3：PowerShell 工具接入](#7-s3powershell-工具接入)
8. [S4：Windows Sandbox](#8-s4windows-sandbox)
9. [S5：PDF Helper](#9-s5pdf-helper)
10. [S6：加固与交付](#10-s6加固与交付)
11. [测试计划](#11-测试计划)
12. [发布、灰度与回滚](#12-发布灰度与回滚)
13. [附录](#13-附录)

---

## 1. 手册使用说明

### 1.1 这本手册解决什么

这本手册把“给 DesktopAgent 接入沙箱”拆成可独立合并的 6 个阶段。每个阶段都包含：

- 目标与交付物
- 前置条件
- 文件级改造清单
- 关键接口 / 伪代码
- 测试用例
- 验收标准
- 故障排查与回退

### 1.2 怎么用

1. **不要跳阶段**。S1 是决策门：如果 AppContainer 里 PowerShell 起不来，后面 S2/S3 的方案要改，必须先停下来重新评估。
2. **每个阶段一个分支 / 一个 PR**。建议分支名：
   - `feature/sandbox-s1-appcontainer`
   - `feature/sandbox-s2-protocol`
   - `feature/sandbox-s3-powershell`
   - `feature/sandbox-s4-wsb`
   - `feature/sandbox-s5-pdf`
   - `feature/sandbox-s6-hardening`
3. **每个 PR 必须带测试**。Windows 专属测试用 `[Trait("Category", "WindowsSandbox")]` 之类标记，CI 可按机器能力过滤。
4. **代码路径以仓库根为基准**。本手册中的 `src/...` 都相对 `C:\Code\SDemo\DesktopAgent`。
5. **先读第 3 章的安全不变量**。任何阶段都不得破坏它们。

### 1.3 术语

| 术语 | 含义 |
| --- | --- |
| 宿主进程 | WPF / MAUI 主进程，运行 Blazor UI 与 Tool Pack |
| 沙箱进程 | 在 AppContainer 或 Windows Sandbox 中运行的进程 |
| SandboxHost | 沙箱内的 console helper，负责执行脚本 / 解析 PDF 并写结果 |
| 工作区副本 | 复制到沙箱 `work` 目录的工作区快照，不是真实工作区 |
| 变更集 | 沙箱运行后 `work` 相对快照新增 / 修改 / 删除的文件列表 |
| 应用改动 | 用户确认后，把变更集写回真实工作区 |
| 决策门 | 某阶段不满足条件时必须停下来重新评估的检查点 |

---

## 2. 阶段总览

| 阶段 | 目标 | 关键产出 | 决策门 |
| --- | --- | --- | --- |
| S0 | 公共契约、项目骨架、设置、DI | `Sandbox.Contracts`、`ISandboxRunner`、`ISandboxJobService`、设置模型 | 无 |
| S1 | 验证 AppContainer 能否跑 cmd / PowerShell | `AppContainerSandboxRunner`、`JobObject`、ACL、自检 | **PowerShell 必须能跑；否则停止并改路线** |
| S2 | 建立宿主 ↔ 沙箱的文件协议 | `SandboxHost`、`job.json` / `result.json`、工作区快照、diff、应用改动 | 变更集可计算、可应用、不能直写真实工作区 |
| S3 | 把 PowerShell 接入工具链 | `PowerShellToolPack`、通用审批、沙箱结果面板 | 无沙箱不执行；审批不可绕过 |
| S4 | 可选的强隔离模式 | `WindowsSandboxRunner`、`.wsb` 生成、单实例队列 | Pro/Enterprise 可用；Home 明确不可用 |
| S5 | PDF 读取与附件提取 | `SandboxHost` PDF 命令、`PdfToolPack`、附件 sidecar | 解析崩溃不拖垮主进程；无网络 |
| S6 | 清理、审计、安全回归、发布 | `SandboxJanitor`、审计日志、回归测试、文档 | 安全测试全过；可回滚 |

---

## 3. 设计基线

### 3.1 威胁模型

| 要防的 | 优先级 | 控制 |
| --- | --- | --- |
| 读取用户文件、浏览器数据、凭据 | 高 | AppContainer 文件隔离；只给沙箱目录授权 |
| 把数据外传 | 高 | AppContainer 不授予网络 capability；Windows Sandbox `Networking=Disable` |
| 静默修改 / 删除工作区 | 高 | 工作区只读快照或可写副本；变更集必须用户确认后应用 |
| 死循环、fork 炸弹、内存爆炸 | 中 | Job Object：进程数、内存、超时、`KILL_ON_JOB_CLOSE` |
| PDF/Office 解析器漏洞 | 中 | 独立 `SandboxHost` 进程 + AppContainer；崩溃隔离 |
| 内核级逃逸 | 低概率 / 高影响 | AppContainer 不是内核边界；强隔离用 Windows Sandbox / Hyper-V |
| 提示词注入诱导执行 | 高 | 审批框展示真实脚本；工具结果定界；不允许自动批准高风险工具 |
| 供应链依赖漏洞 | 中 | 锁定包版本；解析器放 helper；定期更新 |

### 3.2 选型结论

| 方案 | 结论 | 原因 |
| --- | --- | --- |
| **AppContainer + Job Object** | **默认** | Win10/11 全版本可用；进程级文件 / 注册表 / 网络 / 凭据隔离；启动快；可嵌入应用 |
| **Windows Sandbox** | 可选强隔离 | 仅 Pro/Enterprise/Education；Home 不支持；启动慢；单实例；但内核级隔离 |
| Docker / Windows 容器 | 不推荐 | 重、许可复杂、镜像大、不适合桌面 Agent |
| Sandboxie-Plus | 不推荐 | 第三方驱动 / 服务，分发与维护成本高 |
| 云沙箱（E2B/Fly/Modal） | 本阶段不接 | 本地文件、隐私、网络延迟不匹配；且多为 Linux |
| WASM | 不适用 | 跑不了 PowerShell / Windows 文件 API |

参考：

- [AppContainer isolation](https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation)
- [Launch an AppContainer](https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer)
- [Windows Sandbox](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/)
- [Use and configure Windows Sandbox](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)
- [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)

### 3.3 架构

```text
┌──────────────────────────────────────────────────────────────┐
│ 宿主进程（WPF / MAUI Windows）                                │
│                                                              │
│  ChatRoom / ToolApprovalHost / SandboxChangesPanel           │
│        │                                                     │
│        ▼                                                     │
│  PowerShellToolPack ──► SandboxJobService                    │
│        │                     │                               │
│        │                     ├─ WorkspaceSnapshot / Diff     │
│        │                     ├─ SandboxSelector              │
│        │                     └─ ISandboxRunner               │
│        │                              │                      │
│        │              ┌───────────────┴───────────────┐      │
│        │              ▼                               ▼      │
│        │       AppContainerSandboxRunner       WindowsSandbox│
│        │              │                               │      │
│        │              ▼                               ▼      │
│        │       SandboxHost.exe（沙箱内）        run.ps1（VM） │
│        │              │                               │      │
│        │              ├─ powershell.exe               │      │
│        │              └─ PdfPig                       │      │
│        │                                              │      │
│        └──────────────► 结果 / 变更集 ◄───────────────┘      │
│                              │                               │
│                    用户审批后写回真实工作区                   │
└──────────────────────────────────────────────────────────────┘
```

### 3.4 数据流

1. 模型调用 `run_powershell` / `pdf_read`。
2. Tool Pack 校验参数，生成 `SandboxJobRequest`。
3. `SandboxJobService` 创建 job 目录：
   - `input/`：脚本 / PDF / `job.json`
   - `work/`：工作区副本（PowerShell）
   - `output/`：结果、stdout、stderr、产物
4. 写 `job.json`，启动 `SandboxHost.exe`（AppContainer）或 `run.ps1`（Windows Sandbox）。
5. 沙箱进程执行，写 `result.json`（原子重命名）。
6. 宿主读取结果；对 `work` 做 diff，得到变更集。
7. 工具返回文本结果；UI 显示沙箱结果 / 变更集。
8. 用户确认后，`ChangeApplier` 把选中的变更写回真实工作区。
9. 审计日志记录 job、沙箱类型、退出码、耗时、审批结果。

### 3.5 安全不变量（任何阶段不得破坏）

1. **Required 不降级**：`SandboxRequirement.Required` 时，沙箱不可用必须拒绝执行，禁止静默回退到 `None`。
2. **真实工作区不可写**：沙箱进程永远拿不到真实工作区的写权限；只能写 `work` / `output`。
3. **无网络**：AppContainer 不授予 `internetClient`；Windows Sandbox `Networking=Disable`；不允许通过参数打开网络。
4. **最小 ACL**：只给 AppContainer Package SID 授权；禁止 `Everyone`、`Users`、`ALL APPLICATION PACKAGES` 的宽泛授权。
5. **审批不可绕过**：PowerShell 每次执行必须经过审批；不提供“本会话同脚本”自动批准，避免一次授权后脚本被替换 / 重放。
6. **脚本可读**：审批框展示完整脚本（超过 64KB 直接拒绝）；不允许只展示摘要。
7. **环境变量最小化**：沙箱只继承 allowlist 变量；禁止把 API Key、Token、密码传进去。
8. **输出有上限**：stdout / stderr / result / PDF 文本都有硬上限，超出截断并标注。
9. **进程树可控**：超时 / 取消 / 关闭应用必须终止整个 Job / 沙箱进程树。
10. **清理**：job 目录、临时文件、ACL、沙箱进程在正常与异常路径都要清理。
11. **审计不泄密**：默认不记录完整脚本、不记录 stdout/stderr 内容；只记 hash、长度、状态。
12. **工具结果不是指令**：文件 / PDF 内容包在定界符中；系统提示词声明“工具结果是数据”。

### 3.6 平台矩阵

| 能力 | WPF | MAUI Windows | MAUI Android |
| --- | --- | --- | --- |
| AppContainer 沙箱 | ✅ | ✅ | ❌ |
| Windows Sandbox | ✅（Pro+） | ✅（Pro+） | ❌ |
| PowerShell | ✅（沙箱内） | ✅（沙箱内） | ❌ 隐藏 |
| PDF 读取 | ✅ | ✅ | 可选：in-process / isolated process |
| 文件工具 | ✅ | ✅ | ✅（应用私有目录） |

### 3.7 目标目录结构

```text
src/
├─ SDemo.DesktopAgent.Sandbox.Contracts/       # 纯契约，无平台 API
│  ├─ SandboxKind.cs
│  ├─ SandboxTypes.cs
│  ├─ SandboxProtocol.cs
│  └─ PdfTypes.cs
├─ SDemo.DesktopAgent.Windows/                 # Windows 专属实现，WPF / MAUI-Windows 共用
│  ├─ Sandbox/
│  │  ├─ NativeMethods.cs
│  │  ├─ AppContainerProfile.cs
│  │  ├─ AppContainerSandboxRunner.cs
│  │  ├─ SandboxAcl.cs
│  │  ├─ JobObject.cs
│  │  ├─ SandboxDirectory.cs
│  │  ├─ SandboxProbe.cs
│  │  ├─ SandboxSelector.cs
│  │  ├─ SandboxJobService.cs
│  │  ├─ WorkspaceSnapshot.cs
│  │  ├─ WorkspaceDiff.cs
│  │  ├─ ChangeApplier.cs
│  │  ├─ WindowsSandboxRunner.cs
│  │  ├─ WindowsSandboxConfig.cs
│  │  ├─ SandboxJanitor.cs
│  │  └─ JsonlToolAuditLog.cs
│  ├─ Tools/
│  │  ├─ PowerShellToolPack.cs
│  │  └─ PdfToolPack.cs
│  └─ Composition/
│     └─ WindowsSandboxServiceCollectionExtensions.cs
├─ SDemo.DesktopAgent.SandboxHost/             # 沙箱内 console helper
│  ├─ Program.cs
│  ├─ Commands/PowerShellCommand.cs
│  ├─ Commands/PdfExtractCommand.cs
│  ├─ Protocol/JobDocument.cs
│  ├─ Protocol/ResultDocument.cs
│  ├─ Protocol/ResultWriter.cs
│  └─ Security/PathGuard.cs
└─ ...（现有 Core / UI / WPF / MAUI）
tests/
├─ SDemo.DesktopAgent.Core.Tests/              # 契约 / 策略 / diff 单元测试
└─ SDemo.DesktopAgent.Sandbox.Tests/           # Windows 集成测试
docs/
└─ sandbox-development-manual.md                # 本文件
```

---

### 3.8 文件读写工具的边界

文件工具（`list_files` / `read_file` / `write_file` / `workspace_info`）**不是进程执行**，不需要也不应该走 AppContainer；它们在主进程内运行，靠“路径 jail + 写入审批”控制：

- 读：所有路径先解析 reparse point（符号链接 / junction），最终目标必须仍在允许根内；不能只用 `StartsWith` 做字符串前缀判断。
- 写：先写临时文件，再原子替换；写前弹审批；v1 不做删除。
- 不把真实工作区 ACL 授予 AppContainer SID；沙箱只拿 `work` 副本。
- PDF / 附件作为只读根传入 `ToolContext.ReadOnlyRoots`；不能借此读取任意路径。
- 文件工具不继承沙箱超时 / 内存限制；需要单独的输出上限与异常处理。

这部分不新增阶段，但在 S3 接入 `ToolContext` 时要把 `ResolveWorkspacePath` 升级为“解析链接后再判断”，并在 S6 的安全回归中覆盖 `..`、符号链接、junction、UNC、设备路径。

## 4. S0：公共骨架

> S0 不在 S1–S6 的业务范围内，但它是所有阶段的地基。建议单独一个 PR，先合入。

### 4.1 目标

- 新增沙箱契约项目与 Windows 实现项目。
- 定义 `ISandboxRunner`、`ISandboxSelector`、`ISandboxJobService`。
- 定义 `SandboxSettings` 并接入 `AppSettings`。
- 扩展 `IAppPaths`，新增 `SandboxRoot`：Windows 返回 `%LOCALAPPDATA%\SDemo\DesktopAgent\sandbox`，Android 返回 `AppDataDirectory/sandbox`；两个宿主各补一份实现。
- 完成 DI 注册骨架；Android 使用 `UnavailableSandboxRunner`。

### 4.2 新增项目

在仓库根执行：

```powershell
dotnet new classlib -n SDemo.DesktopAgent.Sandbox.Contracts -f net10.0
dotnet new classlib -n SDemo.DesktopAgent.Windows -f net10.0-windows10.0.19041.0
dotnet new console -n SDemo.DesktopAgent.SandboxHost -f net10.0-windows10.0.19041.0
dotnet new xunit -n SDemo.DesktopAgent.Sandbox.Tests -f net10.0-windows10.0.19041.0

dotnet sln SDemo.DesktopAgent.sln add `
  src/SDemo.DesktopAgent.Sandbox.Contracts/SDemo.DesktopAgent.Sandbox.Contracts.csproj `
  src/SDemo.DesktopAgent.Windows/SDemo.DesktopAgent.Windows.csproj `
  src/SDemo.DesktopAgent.SandboxHost/SDemo.DesktopAgent.SandboxHost.csproj `
  tests/SDemo.DesktopAgent.Sandbox.Tests/SDemo.DesktopAgent.Sandbox.Tests.csproj
```

项目引用：

| 项目 | 引用 |
| --- | --- |
| `Sandbox.Contracts` | 无 |
| `Core` | `Sandbox.Contracts` |
| `Windows` | `Core`、`Sandbox.Contracts` |
| `SandboxHost` | `Sandbox.Contracts`（不要引用 Core，保持轻量） |
| `Sandbox.Tests` | `Windows`、`Core`、`Sandbox.Contracts` |

同时把 `Windows`、`SandboxHost` 加进 `SDemo.DesktopAgent.Wpf.slnf`，保证没有 MAUI workload 也能构建。

### 4.3 契约代码

`src/SDemo.DesktopAgent.Sandbox.Contracts/SandboxTypes.cs`：

```csharp
namespace SDemo.DesktopAgent.Sandbox;

public enum SandboxKind
{
    None,
    JobObject,
    AppContainer,
    WindowsSandbox,
    Remote
}

public enum SandboxRequirement
{
    None,
    Preferred,
    Required
}

public enum SandboxFailureCode
{
    None,
    NotAvailable,
    ProbeFailed,
    LaunchFailed,
    Timeout,
    Canceled,
    OutputLimit,
    ProtocolError,
    HostCrashed,
    WorkspaceTooLarge,
    PathEscape
}

public sealed record SandboxLimits
{
    public int TimeoutSeconds { get; init; } = 30;
    public int MemoryLimitMB { get; init; } = 512;
    public int JobMemoryLimitMB { get; init; } = 1024;
    public int MaxProcesses { get; init; } = 8;
    public int MaxOutputBytes { get; init; } = 8 * 1024 * 1024;
}

public sealed record SandboxRequest
{
    public required string JobId { get; init; }
    public required SandboxKind Kind { get; init; }
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public required string OutputDirectory { get; init; }
    public SandboxLimits Limits { get; init; } = new();
}

public sealed record SandboxResult
{
    public required string JobId { get; init; }
    public required SandboxKind Kind { get; init; }
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
    public bool Canceled { get; init; }
    public SandboxFailureCode Failure { get; init; }
    public IReadOnlyList<string> Artifacts { get; init; } = [];
    public TimeSpan Duration { get; init; }
    public string? Message { get; init; }
}

public sealed record SandboxAvailability(
    SandboxKind Kind,
    bool Available,
    bool Verified,
    string? Reason,
    string? Detail);

public sealed record SandboxPolicy(
    SandboxRequirement Requirement = SandboxRequirement.Required,
    SandboxKind Preferred = SandboxKind.AppContainer,
    bool WorkspaceReadOnly = false);

public enum SandboxChangeKind
{
    Added,
    Modified,
    Deleted
}

public sealed record SandboxChange(
    string RelativePath,
    SandboxChangeKind Kind,
    long Size,
    bool IsBinary,
    string? Diff);

public sealed record SandboxJobOutcome(
    string SessionId,
    string JobId,
    SandboxResult Result,
    IReadOnlyList<SandboxChange> Changes,
    string JobDirectory);
```

`src/SDemo.DesktopAgent.Sandbox.Contracts/PdfTypes.cs`：

```csharp
namespace SDemo.DesktopAgent.Sandbox;

public sealed record PdfPageText(int Number, string Text);

public sealed record PdfExtraction(
    bool Ok,
    int PageCount,
    bool Encrypted,
    bool HasTextLayer,
    IReadOnlyList<PdfPageText> Pages,
    bool Truncated,
    string? Message);
```

`src/SDemo.DesktopAgent.Sandbox.Contracts/SandboxProtocol.cs`：

```csharp
namespace SDemo.DesktopAgent.Sandbox.Protocol;

public static class SandboxProtocol
{
    public const int Version = 1;
    public const string JobFileName = "job.json";
    public const string ResultFileName = "result.json";
    public const string PdfResultFileName = "pdf.json";
}

public enum SandboxJobMode
{
    PowerShell,
    PdfExtract,
    PdfInfo
}

public sealed record SandboxJobDocument
{
    public int ProtocolVersion { get; init; } = SandboxProtocol.Version;
    public required string JobId { get; init; }
    public required SandboxJobMode Mode { get; init; }
    public string? ScriptFile { get; init; }
    public string? InputFile { get; init; }
    public string? Pages { get; init; }
    public int MaxChars { get; init; } = 20_000;
    public string WorkingDirectory { get; init; } = "work";
    public int TimeoutSeconds { get; init; } = 30;
    public int MaxOutputBytes { get; init; } = 8 * 1024 * 1024;
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record SandboxResultDocument
{
    public int ProtocolVersion { get; init; } = SandboxProtocol.Version;
    public required string JobId { get; init; }
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public bool Canceled { get; init; }
    public bool Truncated { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public IReadOnlyList<string> Artifacts { get; init; } = [];
    public string? Error { get; init; }
}
```

### 4.4 Core 接口

`src/SDemo.DesktopAgent.Core/Abstractions/ISandboxRunner.cs`：

```csharp
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

public interface ISandboxRunner
{
    SandboxKind Kind { get; }

    Task<SandboxAvailability> ProbeAsync(CancellationToken cancellationToken = default);

    Task<SandboxResult> RunAsync(
        SandboxRequest request,
        CancellationToken cancellationToken = default);
}
```

`src/SDemo.DesktopAgent.Core/Abstractions/ISandboxSelector.cs`：

```csharp
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

public interface ISandboxSelector
{
    Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<SandboxResult> RunAsync(
        SandboxRequest request,
        SandboxPolicy policy,
        CancellationToken cancellationToken = default);
}
```

`src/SDemo.DesktopAgent.Core/Abstractions/ISandboxJobService.cs`：

```csharp
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

public interface ISandboxJobService
{
    event EventHandler<SandboxJobOutcome>? OutcomeAvailable;

    Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<SandboxJobOutcome> RunPowerShellAsync(
        string sessionId,
        string script,
        string? workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken = default);

    Task<PdfExtraction> ExtractPdfAsync(
        string path,
        string? pages,
        int maxChars,
        CancellationToken cancellationToken = default);
}
```

### 4.5 设置模型

`src/SDemo.DesktopAgent.Core/Models/SandboxSettings.cs`：

```csharp
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Models;

public sealed class SandboxSettings
{
    public bool Enabled { get; set; } = true;

    public SandboxRequirement Requirement { get; set; } = SandboxRequirement.Required;

    public SandboxKind Preferred { get; set; } = SandboxKind.AppContainer;

    public int TimeoutSeconds { get; set; } = 30;

    public int MemoryLimitMB { get; set; } = 512;

    public int JobMemoryLimitMB { get; set; } = 1024;

    public int MaxProcesses { get; set; } = 8;

    public bool WorkspaceReadOnly { get; set; }

    public int WorkspaceCopyLimitMB { get; set; } = 200;

    public bool RetainScriptsInAuditLog { get; set; }

    public int AuditRetentionDays { get; set; } = 7;
}
```

在 `AppSettings` 增加：

```csharp
public SandboxSettings Sandbox { get; set; } = new();
```

### 4.6 DI 骨架

Windows 宿主注册：

```csharp
services.AddSingleton<AppContainerProfile>();
services.AddSingleton<AppContainerSandboxRunner>();
services.AddSingleton<ISandboxRunner>(sp => sp.GetRequiredService<AppContainerSandboxRunner>());
services.AddSingleton<SandboxSelector>();
services.AddSingleton<ISandboxJobService, SandboxJobService>();
services.AddSingleton<WindowsSandboxRunner>();
services.AddSingleton<SandboxJanitor>();
```

Android 宿主注册：

```csharp
services.AddSingleton<ISandboxRunner, UnavailableSandboxRunner>();
services.AddSingleton<ISandboxJobService, UnavailableSandboxJobService>();
```

`UnavailableSandboxRunner` 返回：

```csharp
new SandboxAvailability(
    SandboxKind.None,
    Available: false,
    Verified: false,
    Reason: "当前平台不支持 Windows 沙箱",
    Detail: null);
```

### 4.7 S0 验收

- [ ] `dotnet build SDemo.DesktopAgent.Wpf.slnf` 通过。
- [ ] `dotnet test tests/SDemo.DesktopAgent.Core.Tests` 通过。
- [ ] WPF 与 MAUI-Windows 都能解析 `ISandboxJobService`。
- [ ] Android 构建（若开启）不受影响。
- [ ] 设置文件里出现 `sandbox` 节点；旧设置文件仍能加载。
- [ ] 没有新增任何实际的进程执行代码。

---

## 5. S1：AppContainer 可行性验证

### 5.1 目标

证明以下三件事：

1. 能在未打包的 Win32 进程里创建 AppContainer 并启动子进程。
2. 子进程不能读取用户文件、不能写真实工作区、不能联网。
3. **PowerShell 能在 AppContainer 中运行**；如果不行，必须在进入 S2/S3 前停下来改路线。

### 5.2 交付物

- `SDemo.DesktopAgent.Windows/Sandbox/NativeMethods.cs`
- `AppContainerProfile.cs`
- `SandboxAcl.cs`
- `JobObject.cs`
- `SandboxDirectory.cs`
- `AppContainerSandboxRunner.cs`
- `SandboxProbe.cs`
- 设置页“执行沙箱”自检区
- `tests/SDemo.DesktopAgent.Sandbox.Tests` 的 Windows 集成测试

### 5.3 前置条件

- .NET 10 SDK，支持 `net10.0-windows10.0.19041.0`。
- Windows 10 2004+ / Windows 11。
- 用**标准用户**测试，不要用管理员；管理员会掩盖权限问题。
- 准备两个哨兵文件：
  - `%USERPROFILE%\sdemo-sandbox-sentinel.txt`
  - `<workspace>\sdemo-sandbox-sentinel.txt`
- 如果要测试 Windows Sandbox，先确认系统版本是 Pro / Enterprise / Education，并已启用：
  ```powershell
  Enable-WindowsOptionalFeature -Online -FeatureName "Containers-DisposableClientVM" -All
  ```
  （需要管理员 + 重启；Home 不支持。）

### 5.4 实施步骤

#### 5.4.1 编写 NativeMethods

`NativeMethods.cs` 只放 P/Invoke 与结构体，不放业务逻辑。需要覆盖：

- `userenv.dll`：`CreateAppContainerProfile`、`DeriveAppContainerSidFromAppContainerName`、`DeleteAppContainerProfile`
- `advapi32.dll`：`ConvertSidToStringSid`、`FreeSid`
- `kernel32.dll`：`InitializeProcThreadAttributeList`、`UpdateProcThreadAttribute`、`DeleteProcThreadAttributeList`、`CreateProcess`、`ResumeThread`、`WaitForSingleObject`、`GetExitCodeProcess`、`TerminateProcess`、`CreateJobObject`、`SetInformationJobObject`、`AssignProcessToJobObject`、`TerminateJobObject`、`CloseHandle`、`CreateFile`
- 可选：`CreatePipe`、`SetHandleInformation`

关键结构体：

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct SECURITY_CAPABILITIES
{
    public IntPtr AppContainerSid;
    public IntPtr Capabilities;
    public uint CapabilityCount;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct STARTUPINFOEX
{
    public STARTUPINFO StartupInfo;
    public IntPtr lpAttributeList;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct STARTUPINFO
{
    public int cb;
    public string? lpReserved;
    public string? lpDesktop;
    public string? lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_INFORMATION
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public int dwProcessId;
    public int dwThreadId;
}
```

常量以 Windows SDK 头文件为准；常用值：

```csharp
internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
internal const uint CREATE_SUSPENDED = 0x00000004;
internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
internal const uint STARTF_USESTDHANDLES = 0x00000100;
internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
internal const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
internal const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
internal const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
internal const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
```

> `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES` 等 attribute 常量不要手抄错；优先从 `Vanara.PInvoke` 或 SDK 头文件核对。

#### 5.4.2 创建 AppContainer Profile

`AppContainerProfile.cs`：

```csharp
public sealed class AppContainerProfile : IDisposable
{
    public const string ProfileName = "SDemo.DesktopAgent.Sandbox";

    public SecurityIdentifier Sid { get; }
    public string SidString { get; }
    public IntPtr SidPointer { get; }

    public static AppContainerProfile Ensure();
    public void Dispose();
}
```

实现要点：

1. 调用 `CreateAppContainerProfile(ProfileName, "SDemo DesktopAgent Sandbox", "Runs untrusted tools", null, 0, out sid)`。
2. 返回 `HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)` 时，改用 `DeriveAppContainerSidFromAppContainerName(ProfileName, out sid)`。
3. 用 `ConvertSidToStringSid` 生成日志用字符串。
4. SID 指针按 SDK 要求释放；字符串用 `LocalFree`。
5. 首次创建失败（权限 / 策略）时，不要抛出到 UI；记录 `SandboxAvailability` 的 `Reason`，让 PowerShell Pack 隐藏。
6. 卸载时调用 `DeleteAppContainerProfile`（安装器或 S6 的清理入口）。

#### 5.4.3 沙箱目录与 ACL

`SandboxDirectory.cs` 负责：

```text
%LOCALAPPDATA%\SDemo\DesktopAgent\sandbox\
├─ host\                 # 自包含 SandboxHost，只读
└─ jobs\{jobId}\
   ├─ input\             # 只读：job.json / script.ps1 / document.pdf
   ├─ work\              # 可写：工作区副本
   ├─ home\              # 可写：沙箱内的 USERPROFILE / TEMP
   ├─ output\            # 可写：result.json / stdout / stderr / 产物
   └─ manifest.json      # 宿主侧，沙箱不可见
```

ACL：

| 目录 | AppContainer SID 权限 |
| --- | --- |
| `sandbox` 根 | `Traverse` + `ReadAttributes`（只给穿越，不给 `ListDirectory`） |
| `jobs` 根 | `Traverse` + `ReadAttributes`（避免枚举其他 job 名称） |
| `host` | Read & Execute |
| `input` | Read & Execute |
| `work` | Modify |
| `home` | Modify |
| `output` | Modify |
| `jobs\{jobId}` 根 | `Traverse` + `ReadAttributes`（不给 `ListDirectory`；`manifest.json` 放这里，沙箱读不到内容） |
| 真实工作区 | 不授权 |

> 父目录没有 `Traverse` 时，AppContainer 打不开子目录，典型表现是 `CreateProcess` 返回 5 / 0x80070005。`sandbox`、`jobs`、`jobs\{jobId}` 三级都要给穿越权限；只给 `Traverse` + `ReadAttributes`，不要给 `ListDirectory`，这样沙箱知道自己的 job 路径，但枚举不了其他 job，也读不到 `manifest.json` 的内容。父目录的 ACE 不要设置继承（`InheritanceFlags.None`），否则会把穿越权限扩散到所有子孙；`host` / `input` / `work` / `home` / `output` 再单独授权。
>
> 如果 `sandbox` 仍然打不开，检查 `%LOCALAPPDATA%\SDemo` 和 `%LOCALAPPDATA%\SDemo\DesktopAgent` 这两级父目录是否允许 Package SID 穿越；必要时只给它们 `Traverse` + `ReadAttributes`，不要给 `ListDirectory`。更稳妥的做法是把 sandbox 根放在层级更短的专用目录，减少祖先 ACL 变量。

`SandboxAcl.cs`：

```csharp
public static void GrantDirectory(
    string path,
    SecurityIdentifier sid,
    FileSystemRights rights)
{
    var info = new DirectoryInfo(path);
    var security = info.GetAccessControl();

    security.AddAccessRule(new FileSystemAccessRule(
        sid,
        rights,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        AccessControlType.Allow));

    info.SetAccessControl(security);
}

public static void GrantTraverse(string path, SecurityIdentifier sid)
{
    var info = new DirectoryInfo(path);
    var security = info.GetAccessControl();

    security.AddAccessRule(new FileSystemAccessRule(
        sid,
        FileSystemRights.Traverse | FileSystemRights.ReadAttributes,
        InheritanceFlags.None,
        PropagationFlags.None,
        AccessControlType.Allow));

    info.SetAccessControl(security);
}
```

注意：

- 不要调用 `SetAccessRuleProtection` 去掉继承；只追加 ACE。
- 如果 `DirectoryInfo.GetAccessControl` 在 .NET 10 不可用，给 `SDemo.DesktopAgent.Windows.csproj` 加 `System.IO.FileSystem.AccessControl` 包。
- 删除 job 目录会一并删掉 ACE；不要给真实工作区加 ACE。
- 用 `icacls` 在测试里验证：只出现 Package SID，不出现 `Everyone` / `Users` / `ALL APPLICATION PACKAGES`。

#### 5.4.4 Job Object

`JobObject.cs`：

```csharp
public sealed class JobObject : IDisposable
{
    private readonly SafeJobHandle _handle;

    public JobObject(SandboxLimits limits);
    public void Assign(IntPtr processHandle);
    public void Terminate();
    public void Dispose();
}
```

设置两次 `SetInformationJobObject`：

1. `JobObjectExtendedLimitInformation`：
   - `LimitFlags = KILL_ON_JOB_CLOSE | PROCESS_MEMORY | JOB_MEMORY | ACTIVE_PROCESS | DIE_ON_UNHANDLED_EXCEPTION`
   - `ProcessMemoryLimit = limits.MemoryLimitMB * 1024 * 1024`
   - `JobMemoryLimit = limits.JobMemoryLimitMB * 1024 * 1024`
   - `ActiveProcessLimit = limits.MaxProcesses`
2. `JobObjectBasicUIRestrictions`（可选，但建议）：
   - `READCLIPBOARD | WRITECLIPBOARD | SYSTEMPARAMETERS | DISPLAYSETTINGS | GLOBALATOMS | DESKTOP | EXITWINDOWS`

关键顺序：

```text
CreateProcess(CREATE_SUSPENDED)
  → AssignProcessToJobObject
  → ResumeThread
```

不要先 Resume 再 Assign，否则子进程可能在被管控前创建孙进程。

> **PowerShell 版本**：优先用 `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`（5.1）跑通 AppContainer；它位于系统目录，ACL 最简单。`pwsh.exe`（7.x）在 `Program Files`，可能需要额外授予 AppContainer SID 读+执行权限，并且依赖 .NET 运行时目录；作为第二阶段再验证。不要同时上两个版本，先把 5.1 跑通。

#### 5.4.5 启动 AppContainer 进程

`AppContainerSandboxRunner.RunAsync` 步骤：

1. 校验 `request.JobId`（只允许 `[A-Za-z0-9-]`，长度 8–64）。
2. 确保 `OutputDirectory` 存在，且在当前 job 目录下。
3. 创建 `JobObject`。
4. 确保 profile SID 可用。
5. 创建 stdout / stderr 文件的可继承句柄：
   - 用 `CreateFile` + `SECURITY_ATTRIBUTES { bInheritHandle = true }`，或 `CreatePipe` + 异步读取。
   - 设置 `STARTF_USESTDHANDLES`。
6. 构建环境块（`CREATE_UNICODE_ENVIRONMENT`）：
   - 只放 allowlist：`SystemRoot`、`windir`、`PATH`、`ComSpec`、`PATHEXT`、`TEMP`、`TMP`、`USERPROFILE`、`HOMEDRIVE`、`HOMEPATH`、`PSModulePath`（必要时）。
   - `TEMP` / `TMP` / `USERPROFILE` 指向 `home`。
   - 不要继承 `OPENAI_API_KEY`、`AZURE_*`、`AWS_*`、`GITHUB_TOKEN` 等敏感变量。
7. 初始化 `STARTUPINFOEX`：
   ```csharp
   var si = new STARTUPINFOEX();
   si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
   si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
   si.StartupInfo.hStdOutput = stdoutHandle.DangerousGetHandle();
   si.StartupInfo.hStdError = stderrHandle.DangerousGetHandle();
   si.StartupInfo.hStdInput = IntPtr.Zero;
   ```
8. 初始化属性列表：
   ```csharp
   var capabilities = new SECURITY_CAPABILITIES
   {
       AppContainerSid = profile.SidPointer,
       Capabilities = IntPtr.Zero,
       CapabilityCount = 0
   };
   attributeList.Update(SecurityCapabilities, ref capabilities);
   si.lpAttributeList = attributeList.DangerousGetHandle();
   ```
9. 调用 `CreateProcess`：
   ```csharp
   CreateProcess(
       null,
       commandLine,
       IntPtr.Zero,
       IntPtr.Zero,
       inheritHandles: true,
       creationFlags: EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT,
       environmentBlock,
       workingDirectory,
       ref si,
       out var processInfo);
   ```
10. `job.Assign(processInfo.hProcess)`，然后 `ResumeThread(processInfo.hThread)`。

> **命令行安全**：不要把模型生成的脚本文本直接拼进 `CreateProcess` 的命令行。脚本必须先写到 `input\script.ps1`，命令行只传 `-File <固定路径>`；所有参数按 Windows 命令行转义规则处理。这样既避免注入，也让审批框展示的内容与实际执行内容一致。
11. 等待：
    - `WaitForSingleObject(processInfo.hProcess, timeoutMs)`。
    - `WAIT_TIMEOUT` → `job.Terminate()`，等待退出，返回 `TimedOut = true`。
    - `CancellationToken` 注册 `job.Terminate()`。
12. 读取 stdout / stderr 文件；按 `MaxOutputBytes` 截断。
13. `GetExitCodeProcess`；组装 `SandboxResult`。
14. `finally` 中关闭句柄、终止 Job、删除临时输出（按策略保留）。

`CreateProcess` 失败时，返回 `Failure = LaunchFailed`，`Message` 带 Win32 错误码（`Marshal.GetLastWin32Error()`）。

#### 5.4.6 Probe 与自检

`SandboxProbe.cs`：

```csharp
public sealed record SandboxProbeReport(
    bool CmdOk,
    bool PowerShellOk,
    bool CannotReadUserProfile,
    bool CannotWriteWorkspace,
    bool NoNetwork,
    string? Detail);

public sealed class SandboxProbe
{
    public Task<SandboxAvailability> ProbeAsync(CancellationToken ct);
    public Task<SandboxProbeReport> SelfTestAsync(CancellationToken ct);
}
```

`ProbeAsync` 必须快：

1. `cmd.exe /c echo SDEMO_SANDBOX_OK` → 检查退出码与输出。
2. `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Write-Output SDEMO_PS_OK"` → 检查输出。
3. 返回：
   - 两者都过：`Available = true, Verified = true`
   - cmd 过、PowerShell 不过：`Available = true, Verified = false, Reason = "PowerShell 在 AppContainer 中不可用"`
   - cmd 不过：`Available = false, Reason = ...`

> **Verified 是 PowerShell 的启用门槛**：`Available = true` 只表示 AppContainer 能启动进程；`PowerShellToolPack` 必须要求 `Available && Verified`，否则不允许执行 PowerShell。不要因为 cmd 能跑就放行 PowerShell。

`SelfTestAsync` 额外跑负向用例：

| 用例 | 命令 | 期望 |
| --- | --- | --- |
| 读用户目录 | `cmd /c type "<真实用户目录>\sdemo-sandbox-sentinel.txt"` | 非 0 / 拒绝访问 |
| 写工作区 | `cmd /c echo x > "<workspace>\sdemo-sandbox-sentinel.txt"` | 非 0 / 拒绝访问 |
| 联网 | `powershell -Command "try { Invoke-WebRequest https://example.com -TimeoutSec 3; exit 0 } catch { exit 1 }"` | 退出码非 0 |
| 子进程 | PowerShell 里 `Start-Process cmd -ArgumentList '/c echo child' -Wait` | 成功，且进程在 Job 内 |
| 超时 | `powershell -Command "Start-Sleep -Seconds 60"` | 30 秒内被终止 |
| 内存 | 分配大数组 | 进程失败 / 被限制，不拖垮宿主 |

> **不要用沙箱内的 `%USERPROFILE%` 测“读用户目录”**：沙箱环境里的 `USERPROFILE` 已指向 `home`，用它测试会误判为“读不到真实用户目录”。必须由宿主把真实用户目录的绝对路径作为参数传进测试命令。
>
> 负向测试会创建哨兵文件；测试结束删除。不要把哨兵文件留在用户目录。

#### 5.4.7 设置页自检区

在“设置 → 工具设置”里新增一节“执行沙箱”（S1 阶段可以先不新增独立 tab）：

```text
执行沙箱
  状态：AppContainer 可用 / 不可用
  PowerShell：已验证 / 未验证 / 不兼容
  [运行自检]

  自检结果：
   ✓ 基本进程
   ✓ PowerShell
   ✓ 不能读取用户目录
   ✓ 不能写工作区
   ✓ 无网络
   ✗ 子进程限制（原因…）
```

要求：

- 自检按钮在后台线程跑，UI 显示进度。
- 结果持久化到内存即可；重启后重新探测。
- 不可用时显示具体原因，不要只显示“失败”。
- 自检失败不能导致应用崩溃；捕获所有异常。

### 5.5 测试

`tests/SDemo.DesktopAgent.Sandbox.Tests`：

- `AppContainerProfileTests`
  - 能创建 / 复用 profile；SID 字符串非空。
- `SandboxAclTests`
  - 授权后 `icacls` 输出只包含 Package SID；删除目录后 ACE 消失。
- `JobObjectTests`
  - 子进程创建孙进程；`Terminate` 后全部退出。
  - 内存限制生效。
- `AppContainerRunnerTests`
  - `cmd echo` 成功，stdout 正确。
  - PowerShell `Write-Output` 成功 / 或记录为不支持。
  - 读真实用户目录哨兵失败（用宿主传入的绝对路径，不用沙箱内 `%USERPROFILE%`）。
  - 写工作区哨兵失败。
  - 网络请求失败。
  - 超时后进程树不存在。
  - 取消后进程树不存在。
  - 中文输出不乱码（至少 `Write-Output "中文"`）。
- `SandboxProbeTests`
  - 缓存与重新探测。
  - 失败原因可读。

测试标记：

```csharp
[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
```

CI 没有 Windows 时跳过；Windows CI 可以先只跑 `cmd` 相关，PowerShell 兼容性用专用机器。

### 5.6 验收标准

- [ ] `cmd.exe` 能在 AppContainer 中启动并返回输出。
- [ ] `powershell.exe` 能启动；或明确记录“不支持”，并触发决策门。
- [ ] 沙箱进程不能读取 `%USERPROFILE%\sdemo-sandbox-sentinel.txt`。
- [ ] 沙箱进程不能写真实工作区。
- [ ] 沙箱进程不能访问网络。
- [ ] 超时 / 取消能终止整个进程树，无孤儿进程。
- [ ] 内存 / 进程数限制生效。
- [ ] 设置页能显示状态与自检结果。
- [ ] 标准用户（非管理员）下同样通过。

### 5.7 故障排查

| 现象 | 可能原因 | 处理 |
| --- | --- | --- |
| `CreateProcess` 返回 5 / 拒绝访问 | ACL 没给 Package SID；或 profile SID 不对 | 用 `icacls` 检查；确认 SID 是 `S-1-15-2-...` |
| `CreateAppContainerProfile` 返回 `ERROR_ALREADY_EXISTS` | profile 已存在 | 改用 `DeriveAppContainerSidFromAppContainerName` |
| `cmd` 能跑，PowerShell 起不来 | AppContainer 裁掉了 PowerShell 需要的注册表 / GAC / 模块路径 | 先试 `-NoProfile`、清空 `PSModulePath`、给 `System32\WindowsPowerShell` 读+执行；仍失败则走决策门 |
| PowerShell 输出乱码 | 控制台编码 / 文件编码不是 UTF-8 | 子进程写 UTF-8；父进程按 UTF-8 读；PowerShell 脚本头设置 `[Console]::OutputEncoding` |
| 超时后还有进程 | 没有 Assign 到 Job，或子进程 breakaway | 先 `CREATE_SUSPENDED`，Assign 后再 Resume；不要设置 `BREAKAWAY_OK` |
| 网络仍然可用 | 不小心给了 `internetClient` capability | capability 数组必须为空；检查 profile 创建参数 |
| 读到了用户目录 | ACL 给错，或 AppContainer 没生效 | 用 `whoami /groups` / token 检查；确认进程在 AppContainer 中 |
| `SetAccessControl` 拒绝 | 当前用户对目录没有写 ACL 权限 | 目录放在 `%LOCALAPPDATA%`；不要给 `Program Files` 加 ACE |

### 5.8 S1 决策门

完成 S1 后，必须记录以下结论：

| 结果 | 后续路线 |
| --- | --- |
| cmd + PowerShell 都通过 | 进入 S2，默认 AppContainer |
| cmd 通过，PowerShell 不通过 | 暂停 S2/S3；评估：① 先做 S4，用 Windows Sandbox 跑 PowerShell；② 禁用 PowerShell，只做 PDF；③ 继续限时排查兼容性 |
| cmd 都不通过 | 不要继续；检查 profile / ACL / 系统策略；或改为“仅 Windows Sandbox”路线 |

建议写一份 `docs/sandbox-spike-report.md`（可选），记录：

- 测试机器版本 / 版本号 / 是否管理员
- PowerShell 版本（5.1 / 7.x）
- 成功 / 失败的命令与错误码
- 采用的路线

---

## 6. S2：沙箱文件协议与工作区副本

### 6.1 目标

- 建立宿主与沙箱之间的稳定文件协议（`job.json` / `result.json`）。
- 实现 `SandboxHost`：沙箱内执行脚本、捕获输出、写结果。
- 实现工作区快照、diff、变更集。
- 实现“应用改动”审批与写回；沙箱永远不能直写真实工作区。

### 6.2 目录布局

```text
%LOCALAPPDATA%\SDemo\DesktopAgent\sandbox\
├─ host\
│  └─ SDemo.DesktopAgent.SandboxHost.exe   # 自包含发布，只读授权
└─ jobs\{jobId}\
   ├─ input\
   │  ├─ job.json
   │  ├─ script.ps1
   │  └─ document.pdf
   ├─ work\                                 # 工作区副本，可写
   ├─ home\                                 # 沙箱内用户目录
   ├─ output\
   │  ├─ result.json
   │  ├─ stdout.txt
   │  ├─ stderr.txt
   │  └─ pdf.json
   └─ manifest.json                         # 宿主侧，沙箱不可见
```

> 根目录通过 `IAppPaths.SandboxRoot` 获取，不要在业务代码里硬编码 `%LOCALAPPDATA%`；Android 返回 `AppDataDirectory/sandbox`，但没有 PowerShell / Windows Sandbox。

`jobId` 由宿主生成，格式建议：

```text
yyyyMMdd-HHmmss-{8位随机}
```

只允许 `[A-Za-z0-9-]`；不要用用户输入拼接路径。

### 6.3 协议

#### 6.3.1 job.json

```json
{
  "protocolVersion": 1,
  "jobId": "20261021-153000-ab12cd34",
  "mode": "PowerShell",
  "scriptFile": "input/script.ps1",
  "workingDirectory": "work",
  "timeoutSeconds": 30,
  "maxOutputBytes": 8388608,
  "environment": {
    "PSModulePath": ""
  }
}
```

字段说明：

> JSON 选项要使用 `JsonStringEnumConverter`（大小写不敏感），否则 `mode` 会被序列化成数字，协议对不上。

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `protocolVersion` | ✅ | 当前为 1；不匹配直接拒绝 |
| `jobId` | ✅ | 与目录名一致 |
| `mode` | ✅ | `PowerShell` / `PdfExtract` / `PdfInfo` |
| `scriptFile` | PowerShell | 相对路径，必须在 `input` 下 |
| `inputFile` | PDF | 相对路径，必须在 `input` 下 |
| `pages` | PDF | `1-5,8`；默认前 5 页 |
| `maxChars` | PDF | 默认 20000，最大 40000 |
| `workingDirectory` | ✅ | 相对路径，必须在 `work` 下 |
| `timeoutSeconds` | ✅ | 1–120 |
| `maxOutputBytes` | ✅ | 1–8MB |
| `environment` | | 仅允许附加 allowlist 变量 |

#### 6.3.2 result.json

```json
{
  "protocolVersion": 1,
  "jobId": "20261021-153000-ab12cd34",
  "exitCode": 0,
  "timedOut": false,
  "canceled": false,
  "truncated": false,
  "stdout": "...",
  "stderr": "...",
  "durationMs": 812,
  "artifacts": ["work/report.md"],
  "error": null
}
```

写入规则：

1. 先写 `result.json.tmp`。
2. `File.Move(tmp, result.json, overwrite: true)`。
3. 宿主只轮询 `result.json`，看到即认为完整。
4. 宿主读到后校验 `protocolVersion` 与 `jobId`；不匹配 → `ProtocolError`。

#### 6.3.3 协议安全

- 所有相对路径必须用 `Path.GetFullPath(Path.Combine(jobRoot, relative))` 校验，且必须位于允许的子目录。
- 拒绝 `..`、绝对路径、UNC、设备路径、`\\?\`、符号链接逃逸。
- 拒绝 `jobId` 与目录名不一致。
- 拒绝未知 `mode`。
- 结果大小有上限；超过 `maxOutputBytes` 截断，设置 `truncated=true`。

### 6.4 SandboxHost

#### 6.4.1 项目与发布

`SDemo.DesktopAgent.SandboxHost` 是 console 应用：

```text
SDemo.DesktopAgent.SandboxHost.exe run --job C:\job\input\job.json
```

发布方式（推荐自包含，避免 AppContainer 访问 .NET 运行时目录的问题）：

```powershell
dotnet publish src/SDemo.DesktopAgent.SandboxHost `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false `
  -o artifacts/sandbox-host
```

首次运行时，宿主把 `artifacts/sandbox-host` 复制到：

```text
%LOCALAPPDATA%\SDemo\DesktopAgent\sandbox\host\
```

并对 `host` 目录给 AppContainer SID `Read & Execute`。

> 如果使用 framework-dependent 发布，需要验证 AppContainer 能否读取 `C:\Program Files\dotnet\shared`；读不到就改自包含。

#### 6.4.2 Program

```csharp
internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "run" && args[1] == "--job")
        {
            return RunJob(args[2]);
        }

        Console.Error.WriteLine("Usage: SandboxHost run --job <path>");
        return 2;
    }

    private static int RunJob(string jobPath)
    {
        // 1. 读取 job.json
        // 2. 校验协议版本 / 路径
        // 3. 按 mode 分发
        // 4. 写 result.json（无论成功失败）
        // 5. 返回 0 / 3
    }
}
```

要求：

- `jobRoot` 由 `job.json` 的路径推导（`input` 的父目录），不要相信 `job.json` 里出现的绝对路径。
- 任何异常都要写 `result.json`，包含 `error` 字段；不要只写 stderr。
- `SandboxHost` 自己不能再启动一个不受 Job 管控的子进程；PowerShell 子进程由它启动，继承 AppContainer / Job。
- 隐藏控制台窗口：宿主用 `CREATE_NO_WINDOW` 启动它。

#### 6.4.3 PowerShellCommand

```csharp
internal static class PowerShellCommand
{
    public static async Task<ResultDocument> RunAsync(
        JobDocument job,
        string jobRoot,
        CancellationToken cancellationToken)
    {
        // 1. 校验 scriptFile 在 input 下
        // 2. 校验 workingDirectory 在 work 下
        // 3. ProcessStartInfo:
        //    FileName = "powershell.exe"
        //    Arguments = -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <script>
        //    WorkingDirectory = work
        //    RedirectStandardOutput / Error = true
        //    UseShellExecute = false, CreateNoWindow = true
        // 4. 异步读 stdout/stderr，避免管道死锁
        // 5. 超时 → Kill(entireProcessTree: true)
        // 6. 写 result.json
    }
}
```

PowerShell 脚本头建议注入（不影响用户脚本语义）：

```powershell
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
```

#### 6.4.4 结果写入

`ResultWriter.cs`：

```csharp
public static void WriteAtomic(string path, SandboxResultDocument document)
{
    var json = JsonSerializer.Serialize(document, JsonOptions);
    var tmp = path + ".tmp";
    File.WriteAllText(tmp, json, new UTF8Encoding(false));
    File.Move(tmp, path, overwrite: true);
}
```

### 6.5 工作区快照

#### 6.5.1 复制选项

```csharp
public sealed class WorkspaceCopyOptions
{
    public IReadOnlySet<string> ExcludedDirectories { get; init; } = new HashSet<string>(
        [".git", ".vs", "bin", "obj", "node_modules", ".tmp", ".idea"],
        StringComparer.OrdinalIgnoreCase);

    public long MaxBytes { get; init; } = 200 * 1024 * 1024;
    public int MaxFiles { get; init; } = 20_000;
    public bool FollowSymlinks { get; init; }
}
```

#### 6.5.2 复制与 manifest

`WorkspaceSnapshot.Copy`：

1. 递归枚举工作区；跳过排除目录、reparse point（符号链接 / junction）。
2. 计算总大小 / 文件数；超过上限则抛 `WorkspaceTooLargeException`，不要静默截断。
3. 复制到 `work`，保留相对路径。
4. 生成 `manifest.json`（放在 job 根，不映射给沙箱）：

```json
{
  "protocolVersion": 1,
  "entries": [
    {
      "path": "src/App.cs",
      "length": 1234,
      "lastWriteUtc": "2026-10-21T07:30:00Z",
      "sha256": "…"
    }
  ],
  "skipped": ["link-to-outside"]
}
```

规则：

- 文件 ≤ 4MB：计算 SHA-256。
- 文件 > 4MB：只记 length + lastWriteUtc，`sha256 = null`；diff 可能漏掉“同大小同时间”的修改，需在文档中说明。
- 二进制文件也复制；diff 时不展示文本差异。
- 路径统一使用 `/` 分隔，避免 Windows / Unix 差异。

#### 6.5.3 工作区大小超限

返回明确错误：

```text
工作区过大（已扫描 312 MB，上限 200 MB）。
请缩小工作区，或在设置中提高上限；不要直接运行。
```

不要为了“能跑”悄悄只复制一部分——模型会以为文件存在。

### 6.6 Diff 与变更集

`WorkspaceDiff.Compute`：

1. 枚举 `work` 的文件（跳过排除目录）。
2. 与 manifest 对比：
   - 不在 manifest → `Added`
   - 在 manifest 但 `work` 不存在 → `Deleted`
   - length / lastWrite / sha256 不一致 → `Modified`
3. 对每个变更：
   - 大小 ≤ 256KB 且非二进制 → 生成 diff 文本（建议用 `DiffPlex`，MIT）。
   - 二进制 / 大文件 → `IsBinary = true`，只给大小。
   - diff 最多 500 行；超出截断。
4. 二进制判断：读前 8KB，出现 `0x00` 即视为二进制。

`SandboxChange` 已在 S0 定义。

### 6.7 应用改动

`ChangeApplier.cs`：

```csharp
public sealed record ApplyResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Conflicts);

public Task<ApplyResult> ApplyAsync(
    string workspaceRoot,
    WorkspaceManifest manifest,
    IReadOnlyList<SandboxChange> changes,
    IReadOnlySet<string> selected,
    CancellationToken ct);
```

步骤：

1. 对每个选中的变更，校验相对路径：
   - `Path.GetFullPath(Path.Combine(workspaceRoot, relative))`
   - 必须以 `workspaceRoot + separator` 为前缀。
   - 拒绝绝对路径、`..`、UNC、设备路径。
2. 检查路径上是否有 reparse point；如果有，解析目标并确认仍在工作区内；否则跳过。
3. 冲突检测：
   - 当前真实文件 hash 与 manifest 中的 hash 不一致 → `Conflict`，不要覆盖。
   - 当前文件不存在但 manifest 存在 → `Conflict`。
4. `Added` / `Modified`：
   - 在目标目录写 `*.tmp`，再 `File.Move(tmp, target, overwrite: true)`。
   - 保留原文件权限？不要改 ACL；只替换内容。
5. `Deleted`：
   - S2 **不自动删除**。只在 UI 列出，应用删除放到 S6，并要求二次确认。
6. 返回 `Applied` / `Skipped` / `Conflicts`；UI 显示。

### 6.8 UI：变更集面板

新增 `src/SDemo.DesktopAgent.UI/Components/Sandbox/SandboxChangesPanel.razor`：

- 订阅 `ISandboxJobService.OutcomeAvailable`。
- 按 `SessionId` 过滤，只显示当前会话。
- 内容：
  - 运行摘要：沙箱类型、退出码、耗时、是否超时。
  - stdout / stderr 折叠区（最多 200 行）。
  - 文件列表：`A` / `M` / `D` + 路径 + 大小。
  - 点击文件显示 diff。
  - 复选框选择；`应用选中` / `全部丢弃`。
- 应用时调用 `ChangeApplier`；成功后移除条目并刷新。
- 关闭应用 / 切换会话：未应用的条目保留在内存；重启后丢弃（S6 清理 job 目录）。

### 6.9 测试

- `WorkspaceSnapshotTests`
  - 排除目录不复制。
  - 符号链接不跟随。
  - 超过大小 / 文件数上限返回错误。
  - manifest 内容正确。
- `WorkspaceDiffTests`
  - Added / Modified / Deleted。
  - 二进制不生成文本 diff。
  - 大文件用 size + mtime。
  - diff 截断。
- `ChangeApplierTests`
  - 正常应用。
  - 路径穿越拒绝。
  - 符号链接拒绝。
  - 冲突检测。
  - 原子写。
- `SandboxHostTests`
  - 协议版本不匹配。
  - 路径逃逸。
  - 超时。
  - 输出截断。
  - result.json 原子写。

### 6.10 验收标准

- [ ] PowerShell 脚本能读写 `work`，不能写真实工作区。
- [ ] 运行后能计算变更集；diff 可读。
- [ ] 用户能选择文件并应用；未选中的不写回。
- [ ] 冲突文件被跳过并提示。
- [ ] 工作区过大有明确错误。
- [ ] `result.json` 不会出现半截 JSON。
- [ ] 沙箱进程退出后，`work` / `output` 仍可供审阅；清理由 S6 统一处理。

---

## 7. S3：PowerShell 工具接入

### 7.1 目标

- 新增 `PowerShellToolPack`，把 `run_powershell` 暴露给模型。
- 执行前必须审批；审批框展示脚本、工作目录、超时、沙箱类型。
- 没有沙箱时工具不可用 / 直接拒绝。
- 沙箱结果与变更集在 UI 中可见、可应用。
- Android 完全不出现该工具。

### 7.2 前置：工具包注册表

S3 假设以下接口已存在（来自工具包方案）：

```csharp
public interface IAgentToolPack
{
    AgentToolPackDescriptor Descriptor { get; }
    IReadOnlyList<AITool> Create(ToolContext context);
}

public sealed record ToolContext(
    string SessionId,
    string WorkspaceRoot,
    IReadOnlyList<string> ReadOnlyRoots,
    ISettingsService Settings,
    IToolApprovalService Approvals,
    ISandboxJobService SandboxJobs,
    IToolAuditLog Audit);
```

如果工具包注册表尚未落地，先完成它；不要为了 S3 把 PowerShell 硬编码回 `AgentToolbox` 的枚举，否则后续会再次返工。

### 7.3 Tool Pack 定义

`src/SDemo.DesktopAgent.Windows/Tools/PowerShellToolPack.cs`：

```csharp
public sealed class PowerShellToolPack : IAgentToolPack
{
    public AgentToolPackDescriptor Descriptor { get; } = new(
        Id: "powershell",
        Name: "PowerShell",
        Description: "在沙箱中运行 PowerShell 脚本，不能访问真实工作区、网络和用户文件。",
        IconPath: "...",
        Risk: AgentToolRisk.High,
        Order: 30,
        DefaultEnabled: false,
        WindowsOnly: true,
        Functions:
        [
            new AgentToolFunctionDescriptor(
                Id: "run_powershell",
                Name: "运行 PowerShell",
                Risk: AgentToolRisk.High,
                RequiresApproval: true)
        ]);

    public IReadOnlyList<AITool> Create(ToolContext context)
    {
        // 每次 Run 创建一个绑定 context 的函数对象，避免单例 Pack 持有上一个会话的上下文。
        var functions = new PowerShellFunctions(context, this);

        return
        [
            AIFunctionFactory.Create(
                functions.RunPowerShellAsync,
                name: "run_powershell",
                description: "在工作区副本中运行 PowerShell 脚本；改动需要用户确认后才会写回真实工作区。")
        ];
    }

    private sealed class PowerShellFunctions(ToolContext context, PowerShellToolPack pack)
    {
        [Description("要执行的 PowerShell 脚本，最多 64KB。")]
        public Task<string> RunPowerShellAsync(
            string script,
            [Description("工作目录，相对工作区；默认工作区根目录。")] string? workingDirectory,
            [Description("超时秒数，1–120，默认 30。")] int timeoutSeconds,
            CancellationToken cancellationToken)
            => pack.RunPowerShellAsync(context, script, workingDirectory, timeoutSeconds, cancellationToken);
    }
}
```

> **不要用 lambda 直接创建 AIFunction**：`AIFunctionFactory` 的参数说明来自方法参数的 `[Description]` 元数据；lambda 没有这些元数据，模型看到的 schema 会缺少字段说明。用上面的 per-run `PowerShellFunctions` 类，既保留描述，又避免把上一个会话的 `ToolContext` 缓存进单例。

参数建议：

| 参数 | 类型 | 默认 | 约束 |
| --- | --- | --- | --- |
| `script` | string | — | 1–64KB |
| `workingDirectory` | string? | `.` | 相对路径，必须在工作区内 |
| `timeoutSeconds` | int | 30 | 1–120 |

### 7.4 审批

#### 7.4.1 通用审批服务

`src/SDemo.DesktopAgent.Core/Abstractions/IToolApprovalService.cs`：

```csharp
public enum ToolApprovalKind
{
    FileWrite,
    ProcessExecution,
    ApplyChanges
}

public enum ToolApprovalDecision
{
    Deny,
    ApproveOnce,
    ApproveForSession
}

public sealed record ToolApprovalRequest(
    string ToolId,
    ToolApprovalKind Kind,
    string Title,
    string Summary,
    string Detail,
    AgentToolRisk Risk,
    bool AllowSessionRemember,
    string? SessionKey);

public interface IToolApprovalService
{
    ToolApprovalRequest? Current { get; }
    event Action? Changed;

    Task<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default);

    void Resolve(ToolApprovalDecision decision);
    void CancelAll();
}
```

要求：

- 内部用 `SemaphoreSlim(1,1)` 串行化；同一时间只显示一个请求。
- `ApproveForSession` 仅用于未来的低风险工具；PowerShell 必须传 `AllowSessionRemember = false`，不生成 `SessionKey`。
- 工具取消 / 用户停止生成 → `CancelAll()` → 所有 pending 请求返回 `Deny`。
- 不持久化授权；应用重启后全部失效。

#### 7.4.2 PowerShell 审批请求

```csharp
var request = new ToolApprovalRequest(
    ToolId: "run_powershell",
    Kind: ToolApprovalKind.ProcessExecution,
    Title: "运行 PowerShell 脚本",
    Summary: $"沙箱：{availability.Kind}；目录：{workingDirectory ?? "."}；超时：{timeoutSeconds}s；无网络",
    Detail: script,
    Risk: AgentToolRisk.High,
    AllowSessionRemember: false,
    SessionKey: null);

var decision = await context.Approvals.RequestAsync(request, ct);
if (decision == ToolApprovalDecision.Deny)
{
    return "用户拒绝了这次 PowerShell 执行，未做任何修改。";
}
```

> 构造请求前先解析并校验 `workingDirectory`；审批框的 `Summary` 必须显示校验后的路径，不能显示一个还没校验的 `..` 相对路径。脚本本体和实际工作目录都必须是用户看到的那个。

审批 UI：

- `Detail` 用等宽字体、`white-space: pre-wrap`、可滚动。
- 不渲染 Markdown；避免脚本里的 Markdown 干扰阅读。
- 显示风险标签“高”“可访问沙箱内的文件副本，不能访问真实工作区 / 网络”。
- 按钮：`拒绝` / `仅本次`。PowerShell 不显示“本会话同脚本”。

#### 7.4.3 审批宿主位置

新增：

```text
src/SDemo.DesktopAgent.UI/Components/Approvals/ToolApprovalHost.razor
```

挂在 `AppShell` 顶层，而不是每个 `ChatRoom`：

- 切换会话时审批框不会消失。
- 文件写入（`FileWriteApprovalService`）也迁移到这个通用宿主。
- `ChatRoom` 里原来的 `_pendingWrite` / `ResolveWrite` 删除或改为兼容适配。

### 7.5 执行与结果

`SandboxJobService.RunPowerShellAsync`：

1. 从 `SandboxSettings` 读 policy。
2. `GetAvailabilityAsync`；**必须 `Available && Verified`**，否则返回 `SandboxFailureCode.NotAvailable`。`Available` 只代表 AppContainer 能启动进程；`Verified` 才代表 PowerShell 实测可跑，不能拿 cmd 的可用性放行 PowerShell。
3. 创建 job 目录；ACL；复制工作区；写 `script.ps1` / `job.json`。
4. `SandboxSelector.RunAsync`。
5. 读 `result.json`；计算变更集。
6. 触发 `OutcomeAvailable`（带 `SessionId`）。
7. 返回 `SandboxJobOutcome`。

工具返回文本建议：

```text
沙箱：AppContainer
退出码：0
超时：否
耗时：812 ms

--- stdout ---
...

--- stderr ---
...

--- 沙箱内改动（尚未写入真实工作区）---
M src/App.cs
A out/report.md

请在界面中确认应用这些改动；不要声称已经写入真实工作区。
```

特殊情况：

| 情况 | 返回 |
| --- | --- |
| 沙箱不可用 | `ERROR: 沙箱不可用，PowerShell 已禁用。` |
| 用户拒绝 | `用户拒绝了这次执行，未做任何修改。` |
| 超时 | `已终止进程树；以下是截止终止前的输出。` |
| 输出截断 | 末尾加 `…（输出已截断）` |
| 无变更 | `沙箱内没有文件改动。` |
| 工作区过大 | `ERROR: 工作区过大（...），未执行。` |
| 协议错误 | `ERROR: 沙箱返回了无效结果。` |

### 7.6 UI：沙箱结果面板

在 S2 的 `SandboxChangesPanel` 基础上增加：

- 运行状态条：`运行中` / `成功` / `失败` / `超时` / `已拒绝`。
- stdout / stderr 折叠。
- “应用选中”按钮；应用后条目变为“已应用”。
- “丢弃”按钮；删除 job 目录（或交给 S6 清理）。
- 失败时显示错误码与原因，不只显示“失败”。

`ChatRoom`：

- 订阅 `ISandboxJobService.OutcomeAvailable`。
- 按 `SessionId` 保存待处理 outcome。
- 在消息区底部 / 输入框上方渲染面板。
- 生成中收到 outcome 时 `InvokeAsync(StateHasChanged)`。

### 7.7 设置

在“设置 → 工具设置”增加：

```text
PowerShell
  [✓] 启用 PowerShell 工具（默认关闭）
  超时 [30] 秒
  内存上限 [512] MB
  沙箱模式 ( ) 自动  ( ) 强制  ( ) 关闭（仅开发）
  [✓] 每次执行都需要确认（固定开启，不可关闭）

  状态：AppContainer 可用；PowerShell 已验证
  [运行沙箱自检]
```

约束：

- “每次执行都需要确认”在 UI 上不允许关闭；设置模型里也不提供关闭开关。
- “沙箱模式 = 关闭”只在 Debug 构建显示，且红色警告。
- 没有可用沙箱时，启用复选框禁用，tooltip 显示原因。

### 7.8 Android

- `PowerShellToolPack` 只在 Windows 宿主注册；Core 不注册。
- Android `ISandboxJobService` 返回不可用；UI 不显示 PowerShell chip。
- 如果 Core 层注册了 Pack，`Descriptor.WindowsOnly` 必须让 `ToolRegistry` 过滤。

### 7.9 测试

- `PowerShellToolPackTests`
  - 无沙箱 → 返回错误，不调用 runner。
  - 审批拒绝 → 不调用 runner。
  - 审批通过 → 调用 runner，返回格式化结果。
  - 超时 → 结果带超时说明。
  - 变更集 → 结果包含文件列表。
- `ToolApprovalServiceTests`
  - 队列串行。
  - PowerShell 请求 `AllowSessionRemember = false`；第二次执行仍弹审批。
  - 取消 → Deny。
  - 多请求不丢。
- UI 手工测试：
  - 输入“用 PowerShell 列出当前目录”，确认审批框、执行、结果。
  - 拒绝后模型收到拒绝文本，不执行。
  - 脚本写文件 → 面板出现变更 → 应用 → 真实工作区更新。
  - 停止生成 → 沙箱进程被终止。
  - 切换会话 → 审批框不消失。
- 安全测试：
  - 脚本尝试 `Get-Content $env:USERPROFILE\...` → 失败。
  - 脚本尝试 `Invoke-WebRequest` → 失败。
  - 脚本尝试写真实工作区 → 失败。
  - 脚本尝试 `Start-Process powershell` → 仍在沙箱 / Job 内。

### 7.10 验收标准

- [ ] 模型能看到 `run_powershell`，但默认不启用。
- [ ] 启用后每次执行都弹审批；无法绕过。
- [ ] 没有沙箱时工具不可用，模型收到明确错误。
- [ ] 脚本输出、错误、退出码、超时都能展示。
- [ ] 变更集可应用 / 丢弃；应用前真实工作区不变。
- [ ] 停止生成能终止沙箱进程树。
- [ ] Android 不显示 PowerShell。
- [ ] 审计记录 job、沙箱类型、脚本 hash、审批结果。

---

## 8. S4：Windows Sandbox

### 8.1 目标

- 在 Pro / Enterprise / Education 上提供内核级强隔离模式。
- 复用 S2 的工作区副本 / 变更集 / 审批流程。
- Home 上明确不可用，不尝试“凑合”。
- 单实例排队，避免并发启动。

### 8.2 可用性检测

`WindowsSandboxAvailability.cs`：

```csharp
public static SandboxAvailability Detect()
{
    var exe = Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe");
    var edition = Registry.GetValue(
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
        "EditionID",
        null) as string;

    var isHome = edition is "Core" or "CoreSingleLanguage" or "CoreCountrySpecific";

    if (isHome)
    {
        return new SandboxAvailability(
            SandboxKind.WindowsSandbox,
            Available: false,
            Verified: false,
            Reason: "Windows Home 不支持 Windows Sandbox",
            Detail: edition);
    }

    if (!File.Exists(exe))
    {
        return new SandboxAvailability(
            SandboxKind.WindowsSandbox,
            Available: false,
            Verified: false,
            Reason: "Windows Sandbox 功能未启用",
            Detail: "需要管理员启用 Containers-DisposableClientVM 并重启");
    }

    return new SandboxAvailability(
        SandboxKind.WindowsSandbox,
        Available: true,
        Verified: false,
        Reason: null,
        Detail: "可运行自检以验证");
}
```

注意：

- 不要用 `Get-WindowsOptionalFeature -Online` 做启动检测（需要管理员）。
- `WindowsSandbox.exe` 存在性 + EditionID 足够做“可能可用”；`Verified` 只有在自检跑通后为 true。
- 自检会启动 VM，比较慢，只在用户点“测试”时执行。

### 8.3 .wsb 配置

`WindowsSandboxConfig.cs` 生成：

```xml
<Configuration>
  <Networking>Disable</Networking>
  <vGPU>Disable</vGPU>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <MemoryInMB>4096</MemoryInMB>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>C:\...\jobs\job-123\input</HostFolder>
      <SandboxFolder>C:\job\input</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>C:\...\jobs\job-123\work</HostFolder>
      <SandboxFolder>C:\job\work</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>C:\...\jobs\job-123\output</HostFolder>
      <SandboxFolder>C:\job\output</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\job\input\run.ps1</Command>
  </LogonCommand>
</Configuration>
```

要求：

- 路径 XML 转义（`&`、`<`、`>`、`"`）。
- 三个目录必须已存在，否则容器启动失败。
- `Networking=Disable` 必须写；不要依赖默认值（默认是 Enable）。
- `ClipboardRedirection=Disable` 降低数据外传面。
- `ProtectedClient` 可选；开启后复制粘贴受限，需测试映射目录是否仍可用。

### 8.4 run.ps1

`input/run.ps1` 模板：

```powershell
$ErrorActionPreference = 'Continue'
$stdout = 'C:\job\output\stdout.txt'
$stderr = 'C:\job\output\stderr.txt'
$result = 'C:\job\output\result.json'
$script = 'C:\job\input\script.ps1'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$code = 1

try {
    $p = Start-Process -FilePath 'powershell.exe' `
        -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$script) `
        -WorkingDirectory 'C:\job\work' `
        -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr
    $code = $p.ExitCode
}
catch {
    $_ | Out-File -FilePath $stderr -Encoding utf8 -Append
    $code = 1
}

$sw.Stop()
$payload = [ordered]@{
    protocolVersion = 1
    jobId = '{{JOB_ID}}'   # 生成 run.ps1 时替换为真实 jobId
    exitCode = $code
    timedOut = $false
    canceled = $false
    truncated = $false
    durationMs = [int]$sw.Elapsed.TotalMilliseconds
    artifacts = @()
    error = $null
}

$payload | ConvertTo-Json -Depth 4 | Set-Content -Path $result -Encoding utf8

# 可选：让沙箱自行关闭；也可以由宿主终止进程
# shutdown.exe /s /t 0
```

注意：

- `jobId` 推荐在生成 `run.ps1` 时直接写死（替换 `{{JOB_ID}}`），不要依赖 `.wsb` 的 `LogonCommand` 去传环境变量。
- `Start-Process -RedirectStandardOutput` 要求路径可写；`output` 必须可写。
- 如果 PowerShell 子进程卡住，宿主超时后终止整个沙箱；`run.ps1` 无法自行清理。
- 结果写入后再关闭沙箱；不要在 `result.json` 写完前 `shutdown`。

### 8.5 Runner

`WindowsSandboxRunner.RunAsync`：

1. `WindowsSandboxAvailability.Detect()`；不可用 → `NotAvailable`。
2. `SemaphoreSlim(1,1)` 排队；等待超过 60 秒 → “沙箱忙”。
3. 创建 job 目录；复制工作区；写 `script.ps1` / `run.ps1` / `.wsb`。
4. 启动：
   ```csharp
   var process = Process.Start(new ProcessStartInfo
   {
       FileName = Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe"),
       Arguments = $"\"{wsbPath}\"",
       UseShellExecute = true
   });
   ```
5. 轮询 `output/result.json`：
   - 每 200ms 检查一次。
   - 超时 = `request.Limits.TimeoutSeconds + 60`（启动 VM 需要额外时间）。
   - 读 `stdout.txt` / `stderr.txt`；截断。
6. 清理：
   - 读取结果后，终止 `WindowsSandboxClient` / `WindowsSandbox` 进程（按启动时间 + 单实例假设）。
   - 如果 `run.ps1` 自己调用了 `shutdown`，等待进程退出即可。
   - 删除 `.wsb`；job 目录保留给变更集审阅。
7. 返回 `SandboxResult { Kind = WindowsSandbox }`。

注意：

- Windows Sandbox 没有官方生命周期 API；终止进程是工程手段，必须在文档中标注。
- 只允许一个实例；不要尝试并行启动。
- 启动失败 / 超时 / 结果缺失都要返回明确 `Failure`，不要卡死。

### 8.6 Selector

`SandboxSelector.RunAsync`：

```text
if policy.Requirement == None:
    return NotAvailable          # 本手册不允许 None 执行 PowerShell
if policy.Preferred == WindowsSandbox and WSB available:
    return wsb.RunAsync(...)
if AppContainer available and (not requiresPowerShell or verified):
    return appContainer.RunAsync(...)
if policy.Requirement == Required:
    return NotAvailable
return NotAvailable              # Preferred 也不回退到无沙箱
```

规则：

- PowerShell：`Requirement = Required`；`Preferred` 由设置决定；必须 `Available && Verified`。
- PDF：`Requirement = Preferred`；S5 只走 AppContainer，需要 `Available`（不要求 PowerShell 的 `Verified`）。Windows Sandbox 的 PDF helper 不在 S5 范围，Selector 对 PDF 不返回 WSB。
- 文件工具：不走 selector。

### 8.7 设置与 UI

- 设置 → 工具设置 → 执行沙箱：
  - 首选：`AppContainer` / `Windows Sandbox`
  - 状态：可用 / 不可用 / 未验证
  - `[测试 Windows Sandbox]` 按钮（启动一次小自检，约 10–30 秒）
- 审批框显示：
  - `Windows Sandbox（无网络，启动约 10–30 秒）`
  - 执行中显示“正在启动沙箱……”
- 失败时：
  - “Windows Sandbox 启动失败：功能未启用 / 版本不支持 / 超时”
  - 提供“改用 AppContainer”按钮（如果可用）。

### 8.8 测试

- 单元：
  - `.wsb` XML 生成 / 转义。
  - Edition 检测（mock 注册表）。
  - 路径不存在时报错。
- 手工（Pro/Enterprise）：
  - 启动沙箱，跑 `Write-Output`。
  - 确认无网络。
  - 确认只能访问映射目录。
  - 超时后沙箱关闭。
  - 单实例排队。
- Home：
  - 显示不可用，不启动。
- CI：
  - 只跑 XML / 检测单元测试；VM 测试不放进普通 CI。

### 8.9 验收标准

- [ ] Pro/Enterprise 上能跑 PowerShell；结果正确。
- [ ] Home 上明确不可用，不尝试启动。
- [ ] 无网络、无剪贴板、vGPU 关闭。
- [ ] 单实例排队；并发请求不互相踩。
- [ ] 超时 / 取消能关闭沙箱，无残留进程。
- [ ] 变更集仍走 S2 的 diff / 应用流程。
- [ ] 文档写明“无官方生命周期 API”的限制。

---

## 9. S5：PDF Helper

### 9.1 目标

- 用 `SandboxHost` 解析 PDF，避免 PdfPig 直接跑在主进程。
- 提供 `pdf_info` / `pdf_read` 工具。
- 上传 PDF 时提取前几页文本，`AgentMessageMapper` 自动内联。
- 解析崩溃、损坏 PDF、加密 PDF 都不拖垮主应用。

### 9.2 依赖

`SDemo.DesktopAgent.SandboxHost.csproj`：

```xml
<PackageReference Include="UglyToad.PdfPig" Version="..." />
```

版本在实施时锁定；不要用浮动版本。

### 9.3 PDF 命令

`Commands/PdfExtractCommand.cs`：

```csharp
public static PdfExtraction Extract(
    string inputPath,
    string pages,
    int maxChars)
{
    using var document = PdfDocument.Open(inputPath, new ParsingOptions
    {
        UseLenientParsing = true,
        SkipMissingFonts = true
    });

    var pageNumbers = PageRange.Parse(pages, document.NumberOfPages);
    var result = new List<PdfPageText>();
    var total = 0;

    foreach (var number in pageNumbers)
    {
        var page = document.GetPage(number);
        var text = page.Text ?? string.Empty;

        if (total + text.Length > maxChars)
        {
            text = text[..Math.Max(0, maxChars - total)];
            result.Add(new PdfPageText(number, text));
            total = maxChars;
            break;
        }

        result.Add(new PdfPageText(number, text));
        total += text.Length;
    }

    return new PdfExtraction(
        Ok: true,
        PageCount: document.NumberOfPages,
        Encrypted: false,
        HasTextLayer: result.Any(p => !string.IsNullOrWhiteSpace(p.Text)),
        Pages: result,
        Truncated: total >= maxChars,
        Message: null);
}
```

`PdfInfo`：

- 打开文档，读页数、`document.Information`。
- 抽样前 3 页判断是否有文本层。
- 加密：捕获 `PdfDocumentEncryptedException`（或等价异常），返回 `Encrypted = true`。
- 损坏：捕获异常，返回 `Ok = false, Message = "PDF 解析失败：..."`。

页范围：

```csharp
public static IReadOnlyList<int> Parse(string? value, int pageCount)
{
    // 默认 1..min(5, pageCount)
    // 支持 "1-5,8"
    // 去重、升序
    // 超出 pageCount 报错
    // 最多 50 页
}
```

### 9.4 SandboxHost 模式

`job.json`：

```json
{
  "protocolVersion": 1,
  "jobId": "20261021-153000-ab12cd34",
  "mode": "PdfExtract",
  "inputFile": "input/document.pdf",
  "pages": "1-5",
  "maxChars": 20000,
  "timeoutSeconds": 30,
  "maxOutputBytes": 8388608
}
```

输出 `output/pdf.json`：

```json
{
  "ok": true,
  "pageCount": 12,
  "encrypted": false,
  "hasTextLayer": true,
  "truncated": false,
  "pages": [
    { "number": 1, "text": "..." }
  ],
  "message": null
}
```

`PdfInfo` 模式可以复用 `PdfExtract` 但只取 `maxChars=200` 的抽样，或单独实现 `PdfInfo`。

### 9.5 PdfToolPack

`src/SDemo.DesktopAgent.Windows/Tools/PdfToolPack.cs`：

- `pdf_info(path)`
- `pdf_read(path, pages?, maxChars?)`

路径解析：

- 允许根：
  - 当前工作区
  - 当前会话附件目录（只读）
- 拒绝绝对路径、`..`、符号链接逃逸。
- 文件大小上限：25MB（附件）/ 50MB（工具）；超过直接报错。
- 页数上限：单次 50 页；总字符 40K。

执行：

1. 复制 PDF 到 `job/input/document.pdf`。
2. 写 `job.json`。
3. AppContainer 跑 `SandboxHost run --job ...`。
4. 读 `pdf.json`。
5. 返回格式化文本：

```text
PDF: report.pdf
页数: 12
加密: 否
文本层: 有
页范围: 1-5

--- page 1 ---
...

--- page 2 ---
...

（已截断）
```

特殊情况：

| 情况 | 返回 |
| --- | --- |
| 加密 | `PDF 已加密，当前不支持在对话中传密码。` |
| 无文本层 | `未检测到文本层；当前未启用 OCR，无法读取扫描件内容。` |
| 解析失败 | `PDF 解析失败：<message>` |
| Helper 崩溃 | `PDF 解析进程异常退出，已隔离；请更换文件或页范围重试。` |
| 超时 | `PDF 解析超时，已终止。` |

### 9.6 附件 sidecar

`ChatAttachment` 增加可选属性：

```csharp
public string? ExtractedTextPath { get; init; }
public int? PageCount { get; init; }
public bool? HasTextLayer { get; init; }
```

`AttachmentStore.SaveAsync`：

1. 保存原文件。
2. 如果是 `.pdf`，调用 `IPdfAttachmentExtractor.ExtractAsync(path, maxPages: 3, maxChars: 16384, ct)`。
3. 成功时写 sidecar：`<pdf path>.txt`，并设置 `ExtractedTextPath`。
4. 失败 / 超时：不阻塞上传，保留二进制提示；记录日志。

`AgentMessageMapper.TryReadText`：

- 如果附件是 PDF 且 `ExtractedTextPath` 存在且文件存在 → 读 sidecar。
- 否则保留现有逻辑（二进制只给路径）。
- sidecar 读取也受 `AttachmentCharsPerFile` / `AttachmentCharsTotal` 限制。

注意：

- 提取在沙箱中进行；不要在 UI 进程直接 `PdfDocument.Open`。
- 如果沙箱不可用：跳过提取，不启用 PDF 工具；UI 提示“PDF 文本提取需要沙箱”。
- 上传 25MB PDF 可能让上传变慢；给提取设置 10 秒超时，超时则跳过，后续用 `pdf_read` 工具重试。

### 9.7 测试

- `PdfExtractTests`
  - 文本 PDF：页数、文本、页范围。
  - 扫描 PDF：`HasTextLayer = false`。
  - 加密 PDF：`Encrypted = true`。
  - 损坏 PDF：`Ok = false`，不崩溃。
  - 页范围越界 / 格式错误。
  - 字符截断。
- `SandboxHostTests`
  - Helper 崩溃时父进程收到 `HostCrashed`。
  - 超时。
  - 路径逃逸。
- `AttachmentTests`
  - PDF sidecar 生成。
  - 非 PDF 不提取。
  - 提取失败不阻塞。
- 手工：
  - 上传 PDF → 提问 → 模型能引用文本。
  - 扫描件 → 明确提示无文本层。
  - 加密 PDF → 明确提示。

### 9.8 验收标准

- [ ] `pdf_info` / `pdf_read` 可用；路径限制有效。
- [ ] 加密 / 扫描件 / 损坏 PDF 有明确提示。
- [ ] Helper 崩溃不影响主应用。
- [ ] 上传 PDF 能自动带前 3 页文本。
- [ ] 无网络；无真实工作区写权限。
- [ ] 大 PDF 有页数 / 字符 / 大小上限。

---

## 10. S6：加固与交付

### 10.1 清理与删除变更

#### 10.1.1 删除变更

S2 只列出 `Deleted`，不自动删除；S6 才允许应用删除，并且必须满足：

1. 用户在变更面板里显式勾选 `Deleted` 文件；默认不选。
2. 二次确认框写明“将从真实工作区移除 N 个文件”，列出路径。
3. 不永久删除：移动到 `sandbox\trash\{jobId}\{relativePath}`，保留原相对路径；清理交给 `SandboxJanitor`。
4. 删除前做冲突检测：当前文件 hash 必须与 manifest 一致；不一致则跳过。
5. 只删文件，不递归删目录；目录为空时可选删除，但不作为默认行为。
6. 删除失败 → `Skipped`，不要静默忽略。
7. 审计记录 `changeKind = Deleted` 与路径 hash，不记录文件内容。

#### 10.1.2 清理

`SandboxJanitor`：

- 应用启动时：
  - 删除 `sandbox/jobs` 下超过 24 小时的目录。
  - 删除前检查是否有未应用变更；有则跳过，并在 UI 标记“待处理”，不要静默删掉用户的 diff。
  - 删除残留 `*.tmp` / `.wsb`。
  - 不按进程名杀全局进程；只清理 job 目录。
- 应用退出 / 取消：
  - 终止 Job / 沙箱进程。
  - 删除临时脚本。
- 卸载：
  - `DeleteAppContainerProfile("SDemo.DesktopAgent.Sandbox")`。
  - 删除 `sandbox` 根目录。
- 设置页提供“清理沙箱数据”按钮（需确认）。

### 10.2 审计

`JsonlToolAuditLog`：

- 路径：`IAppPaths.DataRoot\logs\tools-{yyyyMMdd}.jsonl`（Windows 默认 `%APPDATA%\SDemo\DesktopAgent\logs`）
- 字段：

```json
{
  "ts": "2026-10-21T15:30:00+08:00",
  "sessionId": "...",
  "toolId": "run_powershell",
  "jobId": "...",
  "sandbox": "AppContainer",
  "scriptHash": "sha256:...",
  "scriptPreview": null,
  "exitCode": 0,
  "timedOut": false,
  "durationMs": 812,
  "stdoutBytes": 120,
  "stderrBytes": 0,
  "changeCount": 2,
  "approval": "ApproveOnce"
}
```

规则：

- 默认不记录完整脚本；`RetainScriptsInAuditLog = true` 时才写 `scriptPreview`（前 4KB）。
- 不记录 stdout/stderr 内容，只记长度。
- 每天一个文件；保留 `AuditRetentionDays` 天；单文件超过 10MB 轮转。
- 写入加锁；失败不影响工具执行。
- UI 提供“打开日志目录”。

### 10.3 安全回归

必须覆盖：

- [ ] `Required` 时无沙箱 → 拒绝，不执行。
- [ ] 沙箱进程读 `%USERPROFILE%` 哨兵 → 失败。
- [ ] 沙箱进程写真实工作区 → 失败。
- [ ] 沙箱进程联网 → 失败。
- [ ] 沙箱环境不含 API Key / Token。
- [ ] 脚本路径 / 工作目录 / PDF 路径的 `..`、绝对路径、符号链接 → 拒绝。
- [ ] 超时 / 取消 → 进程树清空。
- [ ] 输出超限 → 截断，宿主不 OOM。
- [ ] Worker 崩溃 → 主进程存活，返回 `HostCrashed`。
- [ ] 审批拒绝 → 无副作用。
- [ ] 变更集应用冲突 → 不覆盖用户修改。
- [ ] Windows Sandbox `Networking=Disable` 生效。
- [ ] 审计不包含完整脚本 / stdout。

### 10.4 性能

- 工作区复制：
  - 排除 `.git` / `node_modules` / `bin` / `obj`。
  - 串行复制，1MB 缓冲；进度回调。
  - 上限 200MB；超过直接报错。
- AppContainer profile 缓存，不要每次重建。
- SandboxHost 自包含发布，放在本地数据目录，避免每次复制。
- 可用性探测缓存 10 分钟；设置变更 / 点“自检”时刷新。
- PDF 只提取需要的页；默认 5 页。

### 10.5 文档

- 更新 `README.md`：
  - “技能与工具”下增加“沙箱”小节。
  - 说明 PowerShell 默认关闭、必须沙箱、无沙箱拒绝。
  - 说明 Windows Sandbox 需要 Pro/Enterprise。
  - 说明 PDF 附件前 3 页自动提取。
- 新增 `docs/sandbox-troubleshooting.md`（可选）：
  - 常见错误码与处理。
- 本手册保持更新；阶段完成后勾选验收项。

### 10.6 发布与回滚

功能开关：

```text
Sandbox.Enabled = true
PowerShell.DefaultEnabled = false
PDF.AutoExtract = true
```

灰度：

- 先内部机器（Win10 Home + Win11 Pro 各一台）。
- 再小范围用户。
- 观察崩溃率、沙箱启动失败率、审批取消率。

回滚：

- 关闭 `PowerShell` Pack（设置或远程开关），不影响文件 / PDF。
- 关闭 `Sandbox.Enabled` 时，PowerShell 直接不可用，不回到无沙箱执行。
- 最坏情况：移除 `PowerShellToolPack` 注册，保留 PDF。

### 10.7 完成定义（S6）

- [ ] 所有安全回归通过。
- [ ] 清理 / 审计 / 日志可用。
- [ ] 文档与设置说明完整。
- [ ] Win10 Home、Win11 Pro、标准用户三套环境手工验证。
- [ ] 无沙箱时 PowerShell 不可用，行为可解释。
- [ ] 发布 / 回滚步骤演练过一次。

---

## 11. 测试计划

### 11.1 单元测试（Core.Tests）

| 测试 | 内容 |
| --- | --- |
| `SandboxPolicyTests` | Required / Preferred / None 的选择逻辑 |
| `SandboxProtocolTests` | 协议版本、路径校验、未知 mode |
| `SandboxChangeTests` | diff 结果序列化 / 反序列化 |
| `PageRangeTests` | `1-5,8`、越界、重复、倒序 |
| `WorkspaceDiffTests` | Added / Modified / Deleted / 二进制 / 大文件 |
| `ChangeApplierTests` | 路径穿越 / 冲突 / 原子写 |
| `ToolApprovalServiceTests` | 队列、会话授权、取消 |
| `PowerShellResultFormatterTests` | 工具结果文本格式 |
| `AuditRedactionTests` | 默认不写完整脚本 / stdout |

### 11.2 Windows 集成测试（Sandbox.Tests）

| 测试 | 内容 |
| --- | --- |
| `AppContainerProfileTests` | 创建 / 复用 / SID |
| `SandboxAclTests` | 只给 Package SID；删除目录后清理 |
| `JobObjectTests` | 进程树、内存、超时 |
| `AppContainerRunnerTests` | cmd / PowerShell / 负向文件 / 网络 / 取消 |
| `SandboxHostTests` | job.json / result.json / 路径逃逸 / 崩溃 |
| `WorkspaceSnapshotTests` | 排除 / 符号链接 / 上限 |
| `ChangeApplierTests` | 正常 / 冲突 / 符号链接 |
| `WindowsSandboxConfigTests` | XML 生成 / 转义 / 目录存在 |
| `PdfExtractTests` | 文本 / 扫描 / 加密 / 损坏 |

### 11.3 手工测试矩阵

| 环境 | 必须验证 |
| --- | --- |
| Windows 10 Home（标准用户） | AppContainer 可用；PowerShell 兼容性；Windows Sandbox 不可用 |
| Windows 11 Pro（标准用户） | AppContainer；Windows Sandbox 自检 / 执行 |
| Windows 11 Enterprise | 同上；组策略 / AppLocker 下行为 |
| 中文系统 | PowerShell 中文输出不乱码；中文路径 |
| 无网络环境 | 搜索 / PowerShell 网络行为符合预期 |
| 安装了第三方杀软 | AppContainer 启动不被拦截；错误可读 |

### 11.4 CI

- `dotnet test tests/SDemo.DesktopAgent.Core.Tests`：每次 PR。
- `dotnet test tests/SDemo.DesktopAgent.Sandbox.Tests --filter Category=Sandbox`：Windows runner。
- Windows Sandbox 测试不放进普通 CI；用专用 Pro 机器 / 手工。
- 发布前跑一次完整手工矩阵。

### 11.5 负向安全矩阵

| 攻击 | 期望 |
| --- | --- |
| 脚本读取 `%USERPROFILE%\.ssh\id_rsa` | 拒绝访问 |
| 脚本写 `C:\Users\...\Documents` | 拒绝访问 |
| 脚本 `Invoke-WebRequest` | 失败 / 超时 |
| 脚本启动子 PowerShell | 仍在沙箱 / Job |
| 脚本 `Start-Process -Verb RunAs` | 不弹 UAC / 失败；至少不能提权 |
| PDF 路径 `..\..\Windows\...` | 拒绝 |
| 符号链接指向工作区外 | 拒绝 |
| result.json 半截 / 伪造 | 拒绝并报 ProtocolError |
| 超时脚本持续 spawn | 进程树被终止 |
| 输出 1GB | 截断，不 OOM |

---

## 12. 发布、灰度与回滚

### 12.1 发布前检查

- [ ] S1 决策门记录已归档。
- [ ] 所有阶段验收项勾选。
- [ ] 安全回归全过。
- [ ] 在 Home / Pro / Enterprise 各跑一次。
- [ ] 在标准用户（非管理员）下跑一次。
- [ ] 审计日志可读、可清理。
- [ ] 文档已更新。

### 12.2 灰度

1. 内部机器：1 台 Home、1 台 Pro。
2. 小范围用户：开启 `Sandbox.Enabled`，PowerShell 默认关闭。
3. 观察：
   - AppContainer 启动失败率
   - PowerShell 兼容性失败率
   - 审批取消率
   - 沙箱导致的崩溃 / 卡死
4. 无异常后再默认开放 PowerShell 选项（仍需用户手动启用）。

### 12.3 回滚

| 问题 | 回滚动作 |
| --- | --- |
| AppContainer 在某些机器不可用 | 关闭 PowerShell Pack；PDF 保留（helper 仍可用） |
| PowerShell 兼容性差 | 默认隐藏；只保留 Windows Sandbox 路线 |
| Windows Sandbox 自动化不稳定 | 关闭 S4；不影响 AppContainer |
| PDF helper 崩溃 | 关闭 PDF 工具 / 自动提取；保留文件工具 |
| 严重安全问题 | 关闭 `Sandbox.Enabled`；PowerShell 不可用，不回到无沙箱 |

---

## 13. 附录

### 附录 A：P/Invoke 清单

| DLL | API | 用途 |
| --- | --- | --- |
| `userenv.dll` | `CreateAppContainerProfile` | 创建 profile |
| `userenv.dll` | `DeriveAppContainerSidFromAppContainerName` | 复用 profile |
| `userenv.dll` | `DeleteAppContainerProfile` | 卸载清理 |
| `advapi32.dll` | `ConvertSidToStringSid` | SID 日志 |
| `advapi32.dll` | `FreeSid` | 释放 SID |
| `kernel32.dll` | `InitializeProcThreadAttributeList` | 属性列表 |
| `kernel32.dll` | `UpdateProcThreadAttribute` | 设置 security capabilities |
| `kernel32.dll` | `DeleteProcThreadAttributeList` | 清理 |
| `kernel32.dll` | `CreateProcess` | 启动沙箱进程 |
| `kernel32.dll` | `ResumeThread` | 恢复挂起进程 |
| `kernel32.dll` | `WaitForSingleObject` | 等待 / 超时 |
| `kernel32.dll` | `GetExitCodeProcess` | 退出码 |
| `kernel32.dll` | `TerminateProcess` | 兜底终止 |
| `kernel32.dll` | `CreateJobObject` | Job |
| `kernel32.dll` | `SetInformationJobObject` | 限制 |
| `kernel32.dll` | `AssignProcessToJobObject` | 加入 Job |
| `kernel32.dll` | `TerminateJobObject` | 终止进程树 |
| `kernel32.dll` | `CreateFile` | 可继承 stdout/stderr |
| `kernel32.dll` | `CloseHandle` | 清理 |

> 常量与结构体布局以 Windows SDK 头文件为准。若不想手写，可使用 `Vanara.PInvoke` 系列包，但要在 PR 中说明依赖与版本。

### 附录 B：.wsb 模板

```xml
<Configuration>
  <Networking>Disable</Networking>
  <vGPU>Disable</vGPU>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <MemoryInMB>4096</MemoryInMB>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>{INPUT}</HostFolder>
      <SandboxFolder>C:\job\input</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>{WORK}</HostFolder>
      <SandboxFolder>C:\job\work</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>{OUTPUT}</HostFolder>
      <SandboxFolder>C:\job\output</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\job\input\run.ps1</Command>
  </LogonCommand>
</Configuration>
```

### 附录 C：JSON Schema

#### job.json

```json
{
  "protocolVersion": 1,
  "jobId": "string",
  "mode": "PowerShell | PdfExtract | PdfInfo",
  "scriptFile": "input/script.ps1",
  "inputFile": "input/document.pdf",
  "pages": "1-5,8",
  "maxChars": 20000,
  "workingDirectory": "work",
  "timeoutSeconds": 30,
  "maxOutputBytes": 8388608,
  "environment": {}
}
```

#### result.json

```json
{
  "protocolVersion": 1,
  "jobId": "string",
  "exitCode": 0,
  "timedOut": false,
  "canceled": false,
  "truncated": false,
  "stdout": "string",
  "stderr": "string",
  "durationMs": 812,
  "artifacts": ["work/report.md"],
  "error": null
}
```

### 附录 D：错误码与用户提示

| 错误码 | 含义 | 用户提示 |
| --- | --- | --- |
| `SANDBOX_UNAVAILABLE` | 没有可用沙箱 | 沙箱不可用，PowerShell 已禁用 |
| `SANDBOX_PROBE_FAILED` | 沙箱自检失败 | 沙箱自检失败：{reason} |
| `SANDBOX_LAUNCH_FAILED` | 启动进程失败 | 沙箱启动失败（错误码 {code}） |
| `SANDBOX_TIMEOUT` | 执行超时 | 执行超时，已终止进程树 |
| `SANDBOX_CANCELED` | 用户取消 | 已取消 |
| `SANDBOX_OUTPUT_LIMIT` | 输出超限 | 输出过大，已截断 |
| `SANDBOX_PROTOCOL_ERROR` | 结果协议错误 | 沙箱返回了无效结果 |
| `SANDBOX_HOST_CRASHED` | helper 崩溃 | 沙箱进程异常退出，已隔离 |
| `SANDBOX_WORKSPACE_TOO_LARGE` | 工作区过大 | 工作区过大，请缩小或提高上限 |
| `SANDBOX_PATH_ESCAPE` | 路径越界 | 路径超出允许范围 |
| `PDF_ENCRYPTED` | PDF 加密 | PDF 已加密，当前不支持 |
| `PDF_NO_TEXT_LAYER` | 无文本层 | 未检测到文本层，可能需要 OCR |
| `PDF_PARSE_FAILED` | 解析失败 | PDF 解析失败：{message} |

### 附录 E：阶段检查清单

#### S0

- [ ] Contracts / Windows / SandboxHost / Tests 项目
- [ ] Core 接口
- [ ] SandboxSettings
- [ ] DI 骨架
- [ ] Android 不可用实现
- [ ] 构建通过

#### S1

- [ ] Profile
- [ ] ACL
- [ ] Job Object
- [ ] AppContainer Runner
- [ ] Probe
- [ ] 设置页自检
- [ ] 负向安全测试
- [ ] 决策门记录

#### S2

- [ ] job.json / result.json
- [ ] SandboxHost
- [ ] 工作区快照 / manifest
- [ ] diff / 变更集
- [ ] ChangeApplier
- [ ] 变更面板
- [ ] 测试

#### S3

- [ ] PowerShellToolPack
- [ ] 通用审批
- [ ] 审批宿主
- [ ] 工具结果格式
- [ ] 设置
- [ ] 无沙箱拒绝
- [ ] Android 隐藏
- [ ] 测试

#### S4

- [ ] 可用性检测
- [ ] .wsb 生成
- [ ] WindowsSandboxRunner
- [ ] 单实例队列
- [ ] 超时 / 清理
- [ ] Pro / Home 手工测试

#### S5

- [ ] PDF helper
- [ ] pdf_info / pdf_read
- [ ] 附件 sidecar
- [ ] 加密 / 扫描 / 损坏处理
- [ ] 崩溃隔离
- [ ] 测试

#### S6

- [ ] 删除变更（二次确认 + 移入 trash）
- [ ] Janitor
- [ ] 审计
- [ ] 安全回归
- [ ] 性能
- [ ] 文档
- [ ] 发布 / 回滚演练

### 附录 F：参考资料

- [AppContainer isolation](https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation)
- [Launch an AppContainer](https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer)
- [AppContainer for legacy apps](https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-for-legacy-applications-)
- [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)
- [Windows Sandbox](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/)
- [Use and configure Windows Sandbox](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)
- [PdfPig](https://github.com/UglyToad/PdfPig)
- [DiffPlex](https://github.com/mmanela/diffplex)

---

## 附：建议的提交拆分

| PR | 内容 | 依赖 |
| --- | --- | --- |
| PR-1 | S0 契约 + 项目骨架 | 无 |
| PR-2 | S1 AppContainer Runner + Probe + 自检 | PR-1 |
| PR-3 | S2 SandboxHost + 协议 + 快照 / diff | PR-2 |
| PR-4 | S3 PowerShell Tool Pack + 审批 + 面板 | PR-3 |
| PR-5 | S4 Windows Sandbox Runner | PR-2 |
| PR-6 | S5 PDF Helper + 附件提取 | PR-3 |
| PR-7 | S6 删除变更 / 清理 / 审计 / 文档 | PR-4、PR-6 |

每个 PR 的“完成定义”：

1. 代码通过 `dotnet build SDemo.DesktopAgent.Wpf.slnf`。
2. 相关单元 / 集成测试通过。
3. 手册中对应阶段的验收项勾选。
4. 没有新增未处理的 `TODO` / `NotImplementedException`。
5. 安全不变量未被破坏。
