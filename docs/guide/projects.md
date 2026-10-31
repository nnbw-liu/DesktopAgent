# 项目结构、运行与多宿主

> 由根 README 拆出（2026-10-31），内容与原章节一致，仅调整链接前缀。

| 工程 | 目标框架 | 作用 |
| --- | --- | --- |
| `SDemo.DesktopAgent.Sandbox.Contracts` | `net10.0` | 沙箱协议 / record / enum，零依赖 |
| `SDemo.DesktopAgent.Contracts` | `net10.0` | 模型、设置 DTO、跨模块接口（不含实现） |
| `SDemo.DesktopAgent.Agents` | `net10.0` | MAF / OpenAI 的唯一出入口（`IAgentRuntime`） |
| `SDemo.DesktopAgent.Plugins.Abstractions` | `net10.0` | 插件描述符、内容源、`IPluginPackage` |
| `SDemo.DesktopAgent.Plugins.Runtime` | `net10.0` | 插件扫描 / 校验 / 注册表 / 提示词组装 |
| `SDemo.DesktopAgent.Plugin.Translate` | `net10.0` | 内置翻译插件（清单与技能为嵌入资源） |
| `SDemo.DesktopAgent.Plugin.Summarize` | `net10.0` | 内置总结插件（清单与技能为嵌入资源） |
| `SDemo.DesktopAgent.Plugin.QuickAsk` | `net10.0` | 内置简单问答插件（四入口 + 关联上下文开关） |
| `SDemo.DesktopAgent.Context` | `net10.0` | 上下文预算 / 裁剪 / 压缩 / 状态包 |
| `SDemo.DesktopAgent.Editing` | `net10.0` | 路径守卫 / 补丁 / 读前写 / checkpoint |
| `SDemo.DesktopAgent.Execution` | `net10.0` | 内循环策略、验证门、构建 / 测试解析 |
| `SDemo.DesktopAgent.Storage` | `net10.0` | EF Core + SQLite 会话库 |
| `SDemo.DesktopAgent.Sandbox` | `net10.0` | 沙箱的平台中立部分（工作区副本 / diff / 格式化） |
| `SDemo.DesktopAgent.Tools.Abstractions` | `net10.0` | `IAgentToolPack` / `ToolContext` / 注册表 / 路由 |
| `SDemo.DesktopAgent.Tools.Files` / `.Search` / `.Edits` / `.Todo` | `net10.0` | 各内置工具包，一个能力一个类库 |
| `SDemo.DesktopAgent.Tools.Windows` | `net10.0-windows...` | PowerShell / PDF / run_command |
| `SDemo.DesktopAgent.Sandbox.Windows` | `net10.0-windows...` | AppContainer / Job Object / Windows Sandbox |
| `SDemo.DesktopAgent.Execution.Windows` | `net10.0-windows...` | 直跑进程 / 验证执行 / 热工作区 |
| `SDemo.DesktopAgent.Windows` | `net10.0-windows...` | Windows 组合根：注册上面三个平台模块 |
| `SDemo.DesktopAgent.Core` | `net10.0` | 应用编排与组合根；不含 AI SDK / 插件/工具实现 |
| `SDemo.DesktopAgent.UI` | `net10.0`（Razor 类库） | 所有 `.razor` 组件 + `wwwroot`（css / js） |
| `SDemo.DesktopAgent` | `net10.0-windows10.0.19041.0` | WPF 宿主：自绘标题栏、托盘、DPAPI、注册表主题 |
| `SDemo.DesktopAgent.Maui` | `net10.0-windows10.0.19041.0`（默认）/ `net10.0-android`（`EnableAndroidTarget=true` 时） | MAUI 宿主：Windows 与 Android 共用同一套界面 |

- 命名空间统一为 `SDemo.DesktopAgent.*`，两个宿主引用同一批组件，`@inject`、组件名、CSS 类都不需要分叉
- 平台差异收敛到 5 个接口：`IAppPaths`、`ISecureStore`、`ISystemThemeProvider`、`IFolderPickerService`、`IAppEnvironmentService`
- 主题：跟随系统（WPF 读注册表 / MAUI 读 `RequestedTheme`），也可固定浅色 / 深色；WPF 侧同步替换资源字典
- 模型接入：Microsoft Agent Framework（`Microsoft.Agents.AI` 1.25.0），OpenAI 兼容协议，流式输出
- 依赖注入：Core 提供 `AddDesktopAgentCore()`，各宿主再补齐平台实现；Blazor 组件与外壳共享同一个容器

## 目录结构

```
DesktopAgent/
├─ SDemo.DesktopAgent.sln
├─ SDemo.DesktopAgent.Wpf.slnf          # 桌面端工程（含全部模块），没装 MAUI workload 也能构建
├─ docs/
│  ├─ architecture/                      # 设计文档：上下文 / 插件 / 沙箱 / 小模型 / 任务循环
│  ├─ features/                          # 使用与开发指南（插件制作、沙箱排障）
│  └─ reports/                           # 阶段计划、验收报告与基准数据
├─ src/
│  ├─ Contracts/                         # 零实现契约层
│  │  ├─ SDemo.DesktopAgent.Contracts/          # 模型 / 设置 DTO / 跨模块接口
│  │  └─ SDemo.DesktopAgent.Sandbox.Contracts/  # 沙箱协议（零依赖）
│  ├─ Agents/                            # MAF + OpenAI 唯一出入口
│  ├─ Plugins/                           # 插件契约 + 运行时 + 每个内置插件一个类库
│  ├─ Context/                           # 上下文引擎：预算 / 裁剪 / 压缩 / 状态包
│  ├─ Editing/                           # 路径守卫 / 补丁 / 读前写 / checkpoint
│  ├─ Execution/                         # 内循环策略 + Windows 执行实现
│  ├─ Storage/                           # ChatDbContext + ChatStore（EF Core / SQLite）
│  ├─ Sandbox/                           # 中立层 / Windows 实现 / SandboxHost
│  ├─ Tools/                             # 工具契约 + 各工具包（含 Windows 工具）
│  ├─ App/                               # 应用层
│  │  ├─ SDemo.DesktopAgent.Core/               # 编排 + 组合根
│  │  └─ SDemo.DesktopAgent.UI/                 # Razor 类库：两个宿主共用的界面
│  ├─ Platform/                          # Windows 平台组合根（只做 DI + SandboxHost payload）
│  └─ Hosts/                             # 宿主外壳
│     ├─ Wpf/                            # WPF 宿主（SDemo.DesktopAgent.csproj）
│     └─ Maui/                           # MAUI 宿主（Windows / Android）
└─ tools/                                # 评测与基准控制台（解决方案 tools 分组）
```

各模块的职责与依赖顺序：

```
Contracts → Editing / Context / Execution / Sandbox / Agents / Plugins
          → Tools（各包）→ Storage → Core（组合根）→ UI / Hosts
```

- `Core` 只做编排与组合，不直接引用 MAF / OpenAI，也不承载插件或工具实现。
- 平台相关实现集中在三个 Windows 工程：`Sandbox.Windows`、`Execution.Windows`、`Tools.Windows`；`Platform/Windows` 只负责把它们注册进 DI。
- 内置插件（`Plugins/Plugin.*`）以嵌入资源发布，新增插件 = 新建类库 + 注册 `IPluginPackage`。

## 运行

WPF 宿主（主入口，免安装 exe 形态）：

```powershell
dotnet run --project src/Hosts/Wpf
```

MAUI 宿主（默认**只构建 Windows**，`TargetFrameworks` 里只有 `net10.0-windows10.0.19041.0`）：

```powershell
dotnet run --project src/Hosts/Maui        # 需要 maui-windows workload
dotnet build src/Hosts/Maui -f net10.0-windows10.0.19041.0
```

Android 目标是**按需打开**的（`EnableAndroidTarget`，默认关闭）。没有 JDK 17 + Android SDK 时不要打开，
否则 restore 会因为 android workload / SDK 缺失直接失败，连 Windows 也一起调试不了：

```powershell
# 需要 Android 时：先装好 JDK 17 + Android SDK，再显式打开目标
dotnet build src/Hosts/Maui -p:EnableAndroidTarget=true -f net10.0-android
```

只构建桌面端（没有 MAUI workload 时用这个）：

```powershell
dotnet build SDemo.DesktopAgent.Wpf.slnf
```

或者用 Visual Studio 打开 `SDemo.DesktopAgent.sln`，把 `SDemo.DesktopAgent`（WPF）或 `SDemo.DesktopAgent.Maui` 设为启动项目后 F5。
MAUI 宿主的调试目标选 **Windows Machine**（`Properties/launchSettings.json` 里只提供这一个 profile）；
工具栏里的 Android 设备 / 模拟器项只有在 `EnableAndroidTarget=true` 后才会出现。

> **为什么默认不把 net10.0-android 放进 `TargetFrameworks`**：NuGet restore 会对列表里**每一个** TFM 做 workload 校验，
> 只要列表里有 android，即使 F5 的目标是 Windows，还原阶段也会因为缺 android workload / Android SDK 报错
> （典型提示：`NETSDK1147: 要构建此项目，必须安装以下工作负载: android`，或 `XA5300: Android SDK 未找到`），
> 整个启动直接失败。只在真正要出 Android 包时再打开这个开关。

> 需要机器上已安装 **Microsoft Edge WebView2 Runtime**（Win10/11 通常已预装；两个宿主共用）。
> 未安装时 WPF 端会弹提示并退出，下载地址：https://developer.microsoft.com/microsoft-edge/webview2/

调试时如果不方便改系统主题，可以用环境变量强制预览（WPF 宿主）：

```powershell
$env:SDEMO_FORCED_THEME = 'light'   # 或 dark；仅 Debug 构建生效
dotnet run --project src/Hosts/Wpf
```

两个宿主同时运行时，用 `SDEMO_DATA_ROOT` 隔离数据，避免抢同一个 SQLite：

```powershell
$env:SDEMO_DATA_ROOT = "$env:TEMP\sdemo-sandbox"      # 设置 / 会话 / 上传都落到这里
$env:SDEMO_WORKSPACE_ROOT = "$env:TEMP\sdemo-workspace"
```

## 多宿主

- **界面**：`SDemo.DesktopAgent.UI` 里的组件和 `wwwroot` 由两个宿主共同引用；`index.html` 各自保留一份，
  通过 `_content/SDemo.DesktopAgent.UI/...` 访问共享的 css / js。
- **服务**：`AddDesktopAgentCore()` 注册所有平台无关服务，宿主只补 5 个平台实现（见上表）。
- **数据（Windows）**：MAUI 宿主的 `MauiAppPaths` 刻意指向 `%APPDATA%\SDemo\DesktopAgent`，和 WPF 端共用
  `settings.json`、会话库与附件；API Key 两边都用 DPAPI，密文可以互相解开。
  ⚠️ 两个进程**同时**写同一个 SQLite 仍可能锁库，正式使用建议只开一个，或用 `SDEMO_DATA_ROOT` 分开。
- **Android**：数据落在应用私有目录；`IFolderPickerService` 返回 `null`（系统没有「选文件夹」UI），
  工作区退化为 `AppDataDirectory/workspace`；API Key 用 AES-GCM + 应用私有密钥文件。
- **窗口外壳**：WPF 的自绘标题栏 / 托盘 / `WM_GETMINMAXINFO` 只属于 WPF 宿主；MAUI 宿主用平台默认窗口，
  移动端没有标题栏。Blazor 组件里**不要**假设存在标题栏或托盘。
