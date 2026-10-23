# SDemo.DesktopAgent

Blazor Hybrid 多宿主应用：**一套 Razor 组件 + 一套业务服务**，同时挂在 **WPF** 与 **.NET MAUI**（Windows / Android）两个外壳里。

| 工程 | 目标框架 | 作用 |
| --- | --- | --- |
| `SDemo.DesktopAgent.Sandbox.Contracts` | `net10.0` | 沙箱协议 / record / enum，零依赖 |
| `SDemo.DesktopAgent.Contracts` | `net10.0` | 模型、设置 DTO、跨模块接口（不含实现） |
| `SDemo.DesktopAgent.Agents` | `net10.0` | MAF / OpenAI 的唯一出入口（`IAgentRuntime`） |
| `SDemo.DesktopAgent.Plugins.Abstractions` | `net10.0` | 插件描述符、内容源、`IPluginPackage` |
| `SDemo.DesktopAgent.Plugins.Runtime` | `net10.0` | 插件扫描 / 校验 / 注册表 / 提示词组装 |
| `SDemo.DesktopAgent.Plugin.Translate` | `net10.0` | 内置翻译插件（清单与技能为嵌入资源） |
| `SDemo.DesktopAgent.Plugin.Summarize` | `net10.0` | 内置总结插件（清单与技能为嵌入资源） |
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

## 布局

```
┌──────────────────────────────────────────────────────────────┐
│ 标题栏（WPF 自绘，可拖动 / 双击最大化 / 右侧最小化·最大化·关闭）│
├───────────────────┬──────────────────────────────────────────┤
│ 侧栏 Header       │ 主工作区                                  │
│  · 品牌标识       │  · 启动即进入新建的空聊天室               │
│  · 新建聊天室 ────┼─▶ 空聊天室：欢迎语 + 输入框，垂直居中      │
├───────────────────┤  · 有记录：聊天室名称 / 消息区 / 底部输入框│
│ 侧栏 Content      │  · 设置：右侧整页（通用 / 模型 / 工具）   │
│  · 工作区         │                                          │
│    └ 其下聊天室   │                                          │
│  · 历史聊天       │                                          │
│   ↑ 该区域独立滚动 │                                          │
├───────────────────┤                                          │
│ 侧栏 Footer       │                                          │
│  · 设置           │                                          │
├───────────────────┴──────────────────────────────────────────┤
│ 状态栏（WPF 原生）：Blazor Hybrid · WPF … .NET 版本           │
└──────────────────────────────────────────────────────────────┘
```

侧栏固定 268px，`Header` 与 `Footer` 不滚动，只有中间的 `Content` 区域滚动。

## 目录结构

```
DesktopAgent/
├─ SDemo.DesktopAgent.sln
├─ SDemo.DesktopAgent.Wpf.slnf          # 桌面端工程（含全部模块），没装 MAUI workload 也能构建
└─ src/
   ├─ SDemo.DesktopAgent.Sandbox.Contracts/      # 沙箱协议（零依赖）
   ├─ SDemo.DesktopAgent.Contracts/              # 模型 / 设置 DTO / 跨模块接口
   │  ├─ Models/                                 # 会话 / 消息 / 附件 / 设置 / 工作区
   │  ├─ Abstractions/                           # IAppPaths / ISecureStore / 沙箱 / 审批接口
   │  ├─ Services/                               # ISettingsService / IModelCatalogService…
   │  ├─ Context/                                # ISummaryChatTransport / AgentMode / 工具轨迹 DTO
   │  └─ Execution/                              # VerificationRecord / 验证与 checkpoint 设置
   ├─ SDemo.DesktopAgent.Agents/                 # MAF + OpenAI 唯一出入口
   │  ├─ AgentRuntime.cs                         # IAgentRuntime / AgentInvocation / 流式事件
   │  ├─ MafAgentRuntime.cs                      # OpenAI → 裁剪装饰器 → FICC → AsAIAgent
   │  ├─ OpenAiSummaryChatTransport.cs           # 摘要的非流式调用
   │  └─ ModelCatalogService.cs                  # GET /models
   ├─ SDemo.DesktopAgent.Plugins.Abstractions/   # 插件描述符 / IPluginPackage / 内容源
   ├─ SDemo.DesktopAgent.Plugins.Runtime/        # 扫描 / 校验 / 注册表 / 提示词 / 分段解析
   ├─ SDemo.DesktopAgent.Plugin.Translate/       # 内置翻译（plugin.json + skills 为嵌入资源）
   ├─ SDemo.DesktopAgent.Plugin.Summarize/       # 内置总结（同上）
   ├─ SDemo.DesktopAgent.Context/                # 上下文引擎：预算 / 裁剪 / 压缩 / 状态包
   ├─ SDemo.DesktopAgent.Editing/                # 路径守卫 / 补丁 / 读前写 / checkpoint
   ├─ SDemo.DesktopAgent.Execution/              # 内循环策略：命令 / 验证门 / 结果解析
   ├─ SDemo.DesktopAgent.Storage/                # ChatDbContext + ChatStore（EF Core / SQLite）
   ├─ SDemo.DesktopAgent.Sandbox/                # 工作区副本 / 快照 / diff / 结果格式化
   ├─ SDemo.DesktopAgent.Tools.Abstractions/     # IAgentToolPack / ToolContext / 注册表 / 路由
   ├─ SDemo.DesktopAgent.Tools.Files/            # list / read / search / glob
   ├─ SDemo.DesktopAgent.Tools.Search/           # web_search
   ├─ SDemo.DesktopAgent.Tools.Edits/            # apply_patch / write_file / code_edit
   ├─ SDemo.DesktopAgent.Tools.Todo/             # todo_write
   ├─ SDemo.DesktopAgent.Core/                   # 应用编排 + 组合根
   │  ├─ Services/                               # SettingsService、SkillCatalog、AgentChatService、AgentSessionRunner…
   │  ├─ Plugins/PluginRunner.cs                 # 插件 Run 的编排（依赖 IAgentChatService）
   │  ├─ Tools/                                  # 审批 / 审计的宿主实现
   │  └─ Composition/AgentCoreServiceCollectionExtensions.cs   # AddDesktopAgentCore()
   ├─ SDemo.DesktopAgent.Windows/                # Windows 组合根（只做 DI + SandboxHost payload）
   ├─ SDemo.DesktopAgent.Sandbox.Windows/        # AppContainer / Job Object / Windows Sandbox / 清理
   ├─ SDemo.DesktopAgent.Execution.Windows/      # 直跑进程 / 验证 / 热工作区
   ├─ SDemo.DesktopAgent.Tools.Windows/          # PowerShell / PDF / run_command
   ├─ SDemo.DesktopAgent.UI/                     # Razor 类库：两个宿主共用的界面
   │  ├─ _Imports.razor
   │  ├─ Components/                             # AppShell / Sidebar / Chat / Settings
   │  ├─ Services/AttachmentStore.cs             # IBrowserFile → 落盘（依赖 Blazor 类型，放这里）
   │  └─ wwwroot/                                # css/app.css、js/*.js（经 _content/ 访问）
   ├─ SDemo.DesktopAgent/                        # WPF 宿主
   │  ├─ App.xaml / App.xaml.cs                  # DI 组装、WebView2 检查、主题初始化
   │  ├─ MainWindow.xaml / .xaml.cs              # 自绘标题栏 + BlazorWebView + 状态栏
   │  ├─ Services/WindowsAppPaths.cs             # %APPDATA%\SDemo\DesktopAgent
   │  ├─ Services/DpapiSecureStore.cs            # DPAPI 加密 API Key
   │  ├─ Services/RegistrySystemThemeProvider.cs # 读「应用模式」注册表
   │  ├─ Services/FolderPickerService.cs         # OpenFolderDialog（延迟到下一轮消息循环）
   │  ├─ Services/WebView2EnvironmentService.cs  # 运行环境信息 + WebView2 版本
   │  ├─ Services/TrayIconService.cs             # 托盘：显示 / 退出
   │  ├─ Theme/ + Themes/                        # WPF 主题资源字典
   │  └─ wwwroot/index.html                      # 引用 _content/SDemo.DesktopAgent.UI/…
   └─ SDemo.DesktopAgent.Maui/                   # MAUI 宿主（Windows / Android）
      ├─ MauiProgram.cs                          # AddMauiBlazorWebView + 平台实现
      ├─ App.xaml / MainPage.xaml                # BlazorWebView 挂同一个 AppShell
      ├─ Services/MauiAppPaths.cs                # Windows 与 WPF 共用目录；Android 用 AppDataDirectory
      ├─ Services/MauiSecureStore.cs             # Windows: DPAPI；Android: AES-GCM
      ├─ Services/MauiFolderPickerService.cs     # Windows: FolderPicker；Android: 返回 null
      ├─ Services/MauiSystemThemeProvider.cs     # RequestedTheme → 浅色 / 深色
      ├─ Platforms/Android|Windows/              # MainActivity / MauiWinUIApplication
      ├─ Resources/                              # 图标、启动图
      └─ wwwroot/index.html
```

## 运行

WPF 宿主（主入口，免安装 exe 形态）：

```powershell
dotnet run --project src/SDemo.DesktopAgent
```

MAUI 宿主（默认**只构建 Windows**，`TargetFrameworks` 里只有 `net10.0-windows10.0.19041.0`）：

```powershell
dotnet run --project src/SDemo.DesktopAgent.Maui        # 需要 maui-windows workload
dotnet build src/SDemo.DesktopAgent.Maui -f net10.0-windows10.0.19041.0
```

Android 目标是**按需打开**的（`EnableAndroidTarget`，默认关闭）。没有 JDK 17 + Android SDK 时不要打开，
否则 restore 会因为 android workload / SDK 缺失直接失败，连 Windows 也一起调试不了：

```powershell
# 需要 Android 时：先装好 JDK 17 + Android SDK，再显式打开目标
dotnet build src/SDemo.DesktopAgent.Maui -p:EnableAndroidTarget=true -f net10.0-android
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
dotnet run --project src/SDemo.DesktopAgent
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

## 聊天室与输入框

点击侧栏「新建聊天室」→ 右侧切到空聊天室（不归属工作区）；点击「历史聊天」或工作区下的会话 → 右侧切到对应聊天室。两种形态由同一个 `ChatRoom` 组件根据 `ChatSession.Messages` 是否为空切换：

| 状态 | 结构 |
| --- | --- |
| 空聊天室 | 欢迎语 + 输入框整体垂直居中（输入框水平居中、最大宽 760px） |
| 有记录 | 顶部聊天室名称（左对齐）→ 中间消息区（独立滚动）→ 底部输入框 |

助手消息按 **Markdown** 渲染（`MarkdownRenderer`，基于 [Markdig](https://github.com/xoofx/markdig) 1.4.0）：标题、段落、粗斜体、列表、GFM 表格、任务列表、行内代码 / 代码块、引用、删除线、裸链接等；用户消息仍按纯文本显示（保留换行）。模型输出里的原始 HTML 会被转义，链接只保留 `http(s)` / `mailto`；点击链接由 BlazorWebView 的默认 `UrlLoading` 策略（外部地址 `OpenExternally`）交给系统默认浏览器，不会把 Blazor 界面导航走。表格外面套了可横向滚动的容器，长表格不会撑破消息区。

`ChatComposer` 是组合输入框，自下而上分三块：

1. **Header**：`HeaderContent` 参数放工作区（空聊天室）；上传的附件由 `ChatComposer` 追加在它下面，没有内容时整块不渲染、不占高度。
2. **输入区**：`<textarea>` 多行输入，`field-sizing: content` 自动增高（上限 200px 后内部滚动）。
3. **底部功能区**：左侧是「＋ 上传文件」和常用技能（每个 chip 左侧带一个字形图标，最多直接显示 3 个，超出的进「更多」，菜单里同样是「图标 + 名称 + 说明」）；右侧依次是**搜索**、**文件**两个可同时点亮的图标按钮、模型选择器、发送按钮。`IsGenerating` 为 true 时发送按钮切换为停止图标（`.is-stop`）。模型选择会记到设置里（`lastProviderId` / `lastModelId`），下次启动自动选中。

上传的附件会复制到 `%APPDATA%\SDemo\DesktopAgent\uploads\{sessionId}\`，显示在输入框 Header 里（新建聊天室时在工作区选择器下面）；发送后附件随消息持久化，消息气泡里也会显示。文本类附件（`.md` / `.txt` / `.json` / 常见代码后缀）会内联进模型上下文（单个最多 16K 字符、总量 48K），二进制附件只把名称 / 大小 / 路径告诉模型。也可以把文件直接**拖进输入框**：JS 把 drop 的文件写进 Blazor 的 file input，再走同一套上传逻辑。

消息底部会显示元信息（`msg-meta`）：

- 用户消息：时间 + 复制按钮。
- 助手消息：时间、token 总量（同时显示输入 token，hover 显示输入 / 输出明细）、工具调用总次数（hover 显示每个工具的次数）、耗时、复制按钮、**分支按钮**（从这条消息复制上下文，生成一个新的聊天室）。
- token / 工具统计来自 `AgentRunMetrics`：token 从流式返回的 `UsageContent` 累加，工具次数由 `AgentToolbox` 在每次工具调用时计数。服务端没有返回 usage 时只显示耗时。

输入为空时发送按钮禁用。键盘约定由 `js/composer.js` 处理（`document` 捕获阶段监听，元素重渲染后依然有效）：

| 按键 | 行为 |
| --- | --- |
| `Enter` | 发送（`preventDefault`，不插入换行） |
| `Shift + Enter` | 换行（浏览器默认行为） |
| `Ctrl + Enter` | 换行（浏览器默认不插入，脚本在光标处插入 `\n` 并同步给 Blazor） |

输入法组合中（`event.isComposing`）的回车不触发发送。

**发送后的行为**：

1. 用户消息写入 `ChatSession.Messages`，界面立刻从「空态居中」切到「有记录布局」。
2. 首条消息用于生成会话标题（首行前 18 字）与时间；会话在发送前不会出现在列表里，发送后按归属落到「历史聊天」或对应工作区下（置顶的排最前）。
3. 清空输入框，消息区滚到底部。
4. 若已配置模型（输入框底部功能区里的模型选择器），调用 Agent Framework 流式生成，助手消息边生成边追加；生成中发送按钮变为停止，点击可中断（保留已生成的部分）。
5. **思维链**：模型返回 `reasoning_content` 时（如 DeepSeek 系列），MAF 会把它拆成 `TextReasoningContent`；应用把正文与思维链分开接收，消息上方显示「思考过程 · N 字」，默认展开、可折叠。普通模型没有该字段则不显示。

会话持久化到 SQLite（按工作区分库，见「数据存储」），重启后保留；新建的空聊天室在首次发送消息前不落库。

## 数据存储

设置与会话分开存：

| 数据 | 位置 | 说明 |
| --- | --- | --- |
| 设置（主题 / 提供商 / 工作区列表 / 搜索 / 写入确认） | `%APPDATA%\SDemo\DesktopAgent\settings.json` | `System.Text.Json`；API Key 用 DPAPI 加密 |
| 历史聊天（不归属工作区） | `%APPDATA%\SDemo\DesktopAgent\chats\history.db` | SQLite + EF Core |
| 每个工作区的聊天 | `%APPDATA%\SDemo\DesktopAgent\chats\workspaces\{workspaceId}.db` | 一个工作区一个库 |

> 上表是 Windows 宿主的位置（WPF 与 MAUI-Windows 共用）。Android 宿主把根目录换成
> `FileSystem.AppDataDirectory`，API Key 改用 AES-GCM（应用私有密钥文件），其余结构一致。

- 两个表：`sessions`（Id / Title / Meta / WorkspaceId / IsPinned / UpdatedAt）和 `messages`（Id / SessionId / Order / Role / Content / Reasoning / AttachmentsJson / CreatedAt / DurationMs / InputTokens / OutputTokens / ToolCallsJson）；删除会话级联删除消息。附件文件本身在 `%APPDATA%\SDemo\DesktopAgent\uploads\{sessionId}\`，数据库只存路径与元信息。
- 新建聊天室在发出第一条消息前不落库；首条消息写入时在对应库创建会话。
- 用户消息立即写库；助手消息在流式结束（正常结束 / 停止 / 出错）后写一次，避免每个增量都落盘。
- 置顶、重命名、删除会话都会立即写库；删除工作区时先把该库中的会话搬到 `history.db`（`WorkspaceId` 置空），再删除工作区 DB 文件。
- 写操作通过 `ChatStore` 串行执行；建表用 `Database.EnsureCreated()`，并在打开旧库时自动补 `AttachmentsJson` 列（新增列以外的表结构变更仍需手动迁移）。
- 启动时 `AppShell` 从历史库 + 所有工作区库加载会话；某个库损坏 / 被占用时跳过它，不影响其他库。

## 工作区与会话列表

侧栏分成两个相对独立的区域：

**工作区**：显式创建（没有默认工作区），每个工作区对应一个本地目录，持久化在设置的 `workspaces` 里。

- 工作区分组标题右侧的 **＋**（`SidebarSection.HeaderAction`）添加工作区，点了打开系统文件夹选择框；同路径会复用已有工作区。
- 点击工作区行：**该工作区还没有任何会话时，直接进入属于它的新建聊天室**（空工作区就是默认入口）；有会话时只是折叠 / 展开它下面的聊天室。
- 工作区行 hover 出现「＋ 新建聊天室」和「⋯ 更多（重命名 / 删除）」；「＋」无论有没有会话都会新开一个聊天室。
- 重命名只改显示名，不动目录；删除工作区不会删磁盘文件，它下面的会话回到「历史聊天」。
- 在工作区里新建的聊天室，发送第一条消息后挂在该工作区下面（缩进一级，置顶的排最前）。

**历史聊天**：顶部「新建聊天室」创建的会话不归属任何工作区，都进这里；工作区里新建的会话不会出现在这里。

- **工作区选择器**：空会话时在输入框的 Header 里（左上角，与输入文字左对齐）；下拉列表在下方展开，底部是「＋ 选择工作区…」，选中即把当前会话归到该工作区（也可以选已有工作区）。发送第一条消息后 Header 随空态一起收起。
- **会话行**：左侧是会话图标（SVG）；右侧的置顶按钮在鼠标移入时出现，已置顶的会话按钮常亮（再点一次取消置顶），「更多（重命名 / 删除）」同样只在移入时出现。重命名是行内输入（Enter 保存 / Esc 取消）；删除当前会话会回到新的空聊天室。会话正在生成回复时，标题下方的时间会换成带脉冲圆点的「推理中…」。
- 文件工具的根目录：工作区里的会话用工作区目录，历史聊天用 `我的文档\SDemo DesktopAgent`（`WorkspaceContext`）。

## 窗口外观

- **图标**：`Assets/app.ico`（由脚本生成的 7 个尺寸 16–256，渐变圆角方块 + 白色 D），同时用于 exe（`ApplicationIcon`）和窗口（`Window.Icon`）。
- **圆角**：Windows 11 下通过 `DWMWA_WINDOW_CORNER_PREFERENCE` 设成圆角（`DwmSetWindowAttribute`），内容 Border 同步 8px 圆角；最大化时切回直角。
- **最小尺寸**：`MinWidth=1024`、`MinHeight=680`，窗口默认 1240×800。
- 最大化尺寸仍由 `WM_GETMINMAXINFO` 钉到显示器工作区。

## 系统托盘

- 启动后托盘常驻一个图标，和 exe / 窗口共用 `Assets/app.ico`。
- 标题栏关闭按钮（以及 Alt+F4）**不会退出程序**，而是把窗口收进托盘继续运行；第一次收起时会弹一条托盘提示。要真正退出，用托盘右键菜单的「退出」。
- 托盘图标的左键单击 / 双击恢复窗口；右键菜单：显示主窗口 / 退出。
- 注销、关机走 `App.OnSessionEnding`，不会被“关闭即隐藏”的逻辑拦住。
- 实现：WinForms `NotifyIcon`（`TrayIconService`）。项目同时开启 `UseWPF` 与 `UseWindowsForms`（共享 `Microsoft.WindowsDesktop.App`），并在 csproj 里用 `<Using Remove="System.Windows.Forms" />`、`<Using Remove="System.Drawing" />` 去掉 WinForms 注入的全局 using，避免和 WPF 的 `Application` / `MessageBox` 冲突。

## 设置页

右侧整页，顶部分区切换（通用 / 模型 / 上下文 / 工具 / 插件），各分区独立滚动：

**通用设置**

- **外观**：跟随系统 / 浅色 / 深色。写入 `AppSettings.Theme`，`ThemeService.SetPreference()` 立即切换 WPF 资源字典与 WebView，`SettingsService.Save()` 落盘；“跟随系统”时仍响应 `WM_SETTINGCHANGE`。
- **语言**：目前只有简体中文（`en-US` 选项禁用，等资源本地化接入）。
- **版本信息**：应用版本、.NET 运行时、操作系统、WebView2 版本。

**上下文设置**

- **预算与裁剪**：启用预算、显示占用环、预算占窗口比例、保留轮次（普通 / 小模型）、工具轨迹回放预算、附件内联上限、pin 上限。
- **压缩与摘要**：自动压缩、摘要模型（默认当前会话模型）、摘要上限、单次摘要输入上限、摘要超时。
- **工作记忆状态包**：启用开关、普通 / 小模型 token 上限。
- **高级**：工具循环中途裁剪、400 溢出自动重试、单条工具结果上限、重置 token 校准。

**模型设置**
- 提供商列表；列表滚动区独立，**「添加模型提供商」固定在列表底部**（列表超出高度时也在视口底部，不会跟着滚动）。
- 点击后该按钮位置变成 `ProviderEditor` 表单：ProviderId、显示名称（为空时水印跟随 ProviderId）、API 地址、API Key（可切换显示/隐藏）、模型目录。
- 卡片上的「编辑」可以直接改地址 / Key / 模型目录，不必删了重建。
- 模型目录支持两种方式：**获取模型**（`GET {baseUrl}/models`，标准 OpenAI 返回 `data[].id`）与**手动添加**（回车或点「添加」），已添加的模型以分行列表展示、可单个移除。
- 每个模型右侧有窗口档位（自动 / 8K / 32K / 128K / 200K）；选「自动」按模型名推断，拿不准选 8K 只会让预算更保守。档位随 `ModelEntry.Profile` 落盘，决定上下文预算。
- 获取失败会翻译成可读提示：`404` 表示该服务没有 `/models` 接口（**火山方舟 Agent Plan 的 `/api/plan/v3` 就是这种**），此时把模型 ID 手动加进目录即可。
- 保存前校验 ProviderId 唯一、地址为绝对 http(s)；只填域名时自动补 `/v1`。
- 移除提供商需要点两次（先变「确认移除」再删），避免误触。

**工具设置**

- **联网搜索**：默认「必应 RSS」（免密钥，`https://www.bing.com/search?q=…&format=rss`）；也可以切到「自定义 API」，填接口地址与 Key（同样 DPAPI 加密），按 `POST {"query": …, "top_k": 5}` 调用，返回 JSON 即可（解析是通用的：找第一个对象数组，取 title/url/snippet 一类字段）。
- 两种后端都有「测试搜索」按钮，结果直接显示在下方。
- **文件写入**：「写入前需要确认」开关，默认开启；开启时模型调用 `write_file` 会先弹确认框（路径 + 内容预览），拒绝则不落盘。

设置文件：`%APPDATA%\SDemo\DesktopAgent\settings.json`（损坏时自动备份为 `.bak` 并回退默认值）。

> **API Key 用 Windows DPAPI（当前用户）加密**，JSON 里只有 `apiKeyProtected`；旧版本的明文 `apiKey` 在启动时自动迁移。换 Windows 用户或换机器后密文无法解开，需要重新填写 Key。

## 模型接入（Microsoft Agent Framework）

包：`Microsoft.Agents.AI` + `Microsoft.Agents.AI.OpenAI`（均 1.25.0，会带入 `OpenAI` 2.14.0、`Microsoft.Extensions.AI` 等传递依赖）。

```csharp
var client = new OpenAIClient(new ApiKeyCredential(provider.ApiKey),
    new OpenAIClientOptions { Endpoint = new Uri(provider.BaseUrl) });
var agent = client.GetChatClient(modelId)
                  .AsAIAgent(instructions: "...", name: "SDemo Agent");

await foreach (var update in agent.RunStreamingAsync(messages, cancellationToken: ct))
    // update.Text 是增量文本
```

要点：

- 1.25.0 的创建方法是 **`AsAIAgent`**（`OpenAI.Chat` 命名空间），没有 `CreateAIAgent`；响应类型是 `AgentResponse` / `AgentResponseUpdate`。
- **BaseUrl 必须带 `/v1`**：OpenAI SDK 把它当基址，只在后面拼 `chat/completions`，不会补 `/v1`（这是最常见的 404 原因）。
- 取消通过 `CancellationToken`，抛 `TaskCanceledException`（`OperationCanceledException` 子类），先于通用 `Exception` 捕获；HTTP 错误是 `System.ClientModel.ClientResultException`。
- 每次请求都新建 agent（当前实现简单直接）；后续可按 provider+model 缓存，减少构建与连接开销。
- 包体积：framework-dependent 输出约 +10MB；WPF/Blazor Hybrid 默认不裁剪，**不要开** `PublishTrimmed`/AOT（反射与序列化路径未验证）。

## 上下文预算、压缩与工作记忆

每次请求都会先估算 token、按预算裁剪，再交给 Agent Framework；原始消息不删除，数据库里的历史保持完整。实现说明见 [docs/context-budget.md](docs/context-budget.md) 与 [docs/context-compaction.md](docs/context-compaction.md)。

- **单一入口**：`ContextBuilder` 是唯一组装请求的地方（普通对话 → `AgentSessionRunner`，插件 → `PluginRunner` 的 passthrough）。
- **预算**：`total = (window - 输出预留 - 安全余量) * 0.85`；8K 窗口下输入预算约 4.9K，保留最近 6 轮（小模型 4 轮）。
- **裁剪顺序**：保留最近轮次 → 从最旧整轮淘汰 → 历史附件改桩 → 工具轨迹限额 → pin 截断；当前输入永不淘汰。
- **压缩**：L1 开始淘汰历史时，先把被淘汰的一段做成增量摘要（固定 6 节、带来源序号），再带摘要重建；模型失败 / 超时 / 格式不符走确定性兜底。原文只隐藏不删除。
- **状态包**：每轮把工作区结构（深度 2、≤120 项、30 秒缓存）、最近改动（含 sha256 前缀）、最近工具动作与验证状态放进系统块，默认 800 token（小模型 500）。
- **工具循环**：`ContextBudgetChatClient` 在 FICC 内层，第二次请求前强裁过长工具结果、必要时整块淘汰历史，避免「工具结果一多就 400」。
- **溢出重试**：识别到上下文溢出时按半预算只重试一次，并把服务端提示的窗口记进 `context-calibration.json`。
- **计量校准**：用服务端 `usage.input_tokens` 修正启发式估算（EMA，连续 3 次偏差过大自动停用）；「设置 → 上下文」可重置。
- **UI**：输入框右下角是占用环（估算 / 输入预算），>80% 变黄、>95% 变红；点开看系统提示词 / 技能 / 历史 / 附件 / 轨迹 / 摘要 / 状态包 / pin 的分段明细。被压缩的位置有分隔线，点开可只读查看原文、单条 pin；会话 `⋯` 菜单有「压缩现在 / 清空摘要」。宿主提示（溢出重试、需压缩、自动摘要）显示在消息区末尾，不会进入上下文。
- **模型窗口**：模型编辑器里每个模型可单独选「自动 / 8K / 32K / 128K / 200K」，自动档按模型名与家族推断，拿不准就选 8K（只会更保守）。

## 内循环（P4）

构建 / 测试默认进沙箱；在「设置 → 工具设置 → 内循环」显式打开直跑后，`run_command` 可以跳过沙箱复制的等待。直跑只放行白名单可执行文件，参数不经过 shell，每次执行写审计；安全命令可「本会话允许」，高风险命令永不提供。默认关闭，关闭时 `run_powershell` 与沙箱行为不变。构建 / 测试结果会解析成验证记录，落库并进入状态包。详见 [docs/inner-loop.md](docs/inner-loop.md)。

## 任务循环（P5）

多步任务可以用 `todo_write` 维护计划（整体替换、会话级持久化、标题下面板展开）；有编辑但之后没有成功 build/test 时会显示「未验证改动」徽标，可手动运行验证。每次编辑前会创建 checkpoint，`EditSummaryPanel` 可「撤销最近一次改动」，冲突（检查点后又被外部修改）会先要求确认。同一工具调用连续 3 次会收到宿主提示，单轮超过 8 次会被阻断并要求用文本总结。详见 [docs/task-loop.md](docs/task-loop.md)。

## 小模型模式（P6）

8K / 无工具 / 名称含 `3b`、`4b`、`7b` 等模型自动进入 Compact：短系统提示（≤250 token）、技能 ≤600 token、最多 4 个工具、关键词路由、最大 6 次工具调用；可手动切回完整模式。提供 `code_edit` 单工具模式（read/search/patch 三合一）与工具参数修复函数。实测 `qwen3-4b-thinking-2507` 在 8K/Compact 下无法完成工具任务（思考 token 占满输出），不建议把 4B thinking 模型用于工具任务；详见 [small-model-mode.md](docs/small-model-mode.md)。

## 技能与工具
输入框底部现在是「上传文件 + 常用技能」和右侧的「工具 + 模型 + 发送」：

- **＋ 上传文件**：多选，附件显示在 Header（新聊天室在工作区选择器下面），发送后随消息落库。
- **常用技能**：直接读取技能库，最多同时显示 3 个，超出的放进「更多」菜单；每个 chip 左侧是技能图标（frontmatter 的 `icon`，没写就取名字首字）。点击后**只显示选中的那一个 chip**（带 `✕` 取消），其余技能隐藏，下一次发送把该技能全文注入系统提示词。
- **模型工具**：右侧只保留「联网搜索」这类可选外部工具，点亮即启用。文件读写是内置能力，默认开启，不再占输入框 chip；在「设置 → 工具设置」可按 `files`（只读）/ `edits`（写入）分包关闭。

| 工具 | 下发的工具 | 实现 |
| --- | --- | --- |
| 联网搜索 | `web_search` | 默认 Bing RSS；可在「设置 → 工具设置」换成自定义搜索 API（地址 + Key） |
| `files`（只读，内置） | `list_files` / `list_tree` / `read_file` / `search_files` / `glob_files` / `workspace_info` | 按行读带行号与续读；搜索 / glob 纯 .NET；排除 `bin` / `obj` / `.git` 等；路径限制在工作区内 |
| `edits`（写入，内置） | `apply_patch` / `write_file` | `apply_patch` 应用 unified diff（多文件、全有或全无）；写已有文件前必须先 `read_file`，且审批框显示 diff |
| `command`（直跑，默认关闭） | `run_command` | 白名单命令（dotnet build / test 等）在宿主直跑；参数不经过 shell、每次审计、可本会话授权；`run_powershell` 仍在沙箱 |

### 技能格式

技能就是一个 Markdown 文件，**文件名就是技能名**。推荐使用标准的 YAML frontmatter，输入框的技能列表会优先取其中的 `description` 作为悬浮说明、`icon` 作为 chip 上的图标（一个字符或 emoji，不写就取技能名首字）：

```markdown
---
name: 总结
description: 一句话说明这个技能做什么、什么时候用。
icon: 总
version: 1.0
---

# 总结

## 何时使用
## 输入
## 工作步骤
## 输出格式（严格）
## 约束
## 示例
```

`description` 建议一句话写清“做什么 + 何时使用”。技能**不再通过 `list_skills` / `read_skill` 工具调用**：点选后由 `AgentChatService` 把技能全文拼进系统提示词，输出格式更稳定。

内置了三个技能（`%APPDATA%\SDemo\DesktopAgent\skills\`）：

- `总结.md`：一句话结论 → 关键要点 → 决策与依据 → 待办 → 风险与未决问题。
- `代码解释.md`：一句话概括 → 逐段说明 → 输入输出 → 边界与风险。
- `会议纪要.md`：会议结论 → 决策清单 → 待办 → 风险与待确认。

- 工具通过 `AsAIAgent(tools: …)` 挂到 agent 上；`ChatClientAgent` 默认会自动执行工具调用并继续生成，不需要手写循环。
- 已用本地 mock 验证：开工具后请求体带 `tools` 数组；模型返回 `tool_calls` 后应用执行函数，并把 `role: tool` 的结果回传，再产出最终回复。
- **写文件 / 应用补丁会先弹确认框**：`write_file` 显示路径与内容预览，`apply_patch` 显示统一 diff（`+` / `-` / hunk），均可拒绝；工具线程 await 用户选择，停止生成/关闭页面按拒绝处理。
- 文件写入与 PowerShell 是仅有的副作用工具；工作区路径固定，不接受绝对路径与 `..` 越界，junction 也会拒绝。

### 沙箱执行（S1–S6 已完成）

PowerShell 与 PDF 解析都在沙箱里运行；沙箱不可用时直接拒绝执行，**不会**退回主进程直跑。
完整开发手册见 [docs/sandbox-development-manual.md](docs/sandbox-development-manual.md)，
S6 验收与偏差见 [docs/sandbox-s6-report.md](docs/sandbox-s6-report.md)，
UI 逐项验收记录见 [docs/sandbox-ui-acceptance-s6.md](docs/sandbox-ui-acceptance-s6.md)，
常见问题见 [docs/sandbox-troubleshooting.md](docs/sandbox-troubleshooting.md)。

| 阶段 | 状态 | 能力 |
| --- | --- | --- |
| S1 AppContainer | ✅ | Job Object + Package SID ACL；无网络、不能读用户目录、只能读写 job 副本 |
| S2 工作区副本 | ✅ | `base` / `work` 快照 + manifest + diff；变更面板确认后原子写回，冲突整体中止 |
| S3 PowerShell | ✅ | `run_powershell` 工具；每次执行都要审批；超时 / 内存 / 进程数 / 输出上限 |
| S4 Windows Sandbox | ✅ | 可选虚拟机级隔离；仅 Pro / Enterprise / Education，自检后才会启用 |
| S5 PDF | ✅ | `pdf_read` / `pdf_info`；PdfPig 在 SandboxHost 内解析；加密 / 扫描件明确报错 |
| S6 加固与交付 | ✅ | 删除进回收站、启动清理、审计保留与轮转、设置页维护入口、安全回归 |

关键路径（可用环境变量覆盖）：

| 内容 | 默认位置 |
| --- | --- |
| 沙箱根 | `%LOCALAPPDATA%\SDemo\DesktopAgent\sandbox`（`SDEMO_SANDBOX_ROOT`） |
| 作业 / 回收站 | `sandbox\jobs\<jobId>` / `sandbox\trash\<jobId>` |
| 审计日志 | `%APPDATA%\SDemo\DesktopAgent\logs\tools-yyyyMMdd.jsonl` |

S6 行为要点：

- **内置工具无需 chip**：文件读写、PDF、PowerShell 默认开启，模型根据任务自行调用，
  输入框只保留联网搜索这类可选外部工具。PowerShell 每次执行仍需审批；沙箱不可用时直接拒绝，
  不会无沙箱直跑。设置页仍可全局关闭某个内置工具。
- **删除类变更**：默认不勾选；确认后移动到 `sandbox\trash\{jobId}\{相对路径}`，不永久删除。
  删除前校验文件仍是快照版本，用户改过就跳过；目录删除不支持。
- **自动清理**：启动时删除超过 24 小时的 job、1 小时前的残留 `*.tmp` / `*.wsb`、7 天前的 trash；
  有未应用变更的 job 会保留，设置页会列出被跳过的 job。
- **审计**：默认只记元数据与脚本 hash / 长度，不记脚本正文、stdout / stderr；保留 7 天，
  单文件超过 10MB 轮转。设置页可打开日志目录。
- **PDF 附件**：上传时自动提取前 3 页（16,384 字符）；可在设置关闭，之后仍可用 `pdf_read`
  按需读取。PDF 工具默认读前 5 页，扫描件没有 OCR。
- **清理沙箱数据**：设置页提供二次确认的清空入口，会删除 jobs / trash / 临时文件与 AppContainer profile；
  `host` 程序保留，下次使用自动重建 profile。

## 插件

插件是**零代码**的声明式能力包：一个目录 + `plugin.json` + 若干 `.md` 技能文件，不需要编译、不需要重装应用。完整设计见 [docs/plugin-system.md](docs/plugin-system.md)；**自己写插件请看 [插件开发手册](docs/plugin-authoring.md)**（清单字段、参数、入口、双语对照、报错对照表、可复制模板）。

目录：

| 来源 | 位置 | 说明 |
| --- | --- | --- |
| 本地 | `%APPDATA%\SDemo\DesktopAgent\plugins\<目录>\` | 用户自制；同 `id` 时覆盖内置插件 |
| 内置 | 编译进 `SDemo.DesktopAgent.Plugin.*` 类库 | 清单与技能是嵌入资源，不落盘、不需要复制目录；Android 与 Windows 一致 |

每个内置插件是一个独立类库：`plugin.json` / `skills/*.md` 通过 `EmbeddedResource LogicalName` 固定为插件内相对路径，类里只需一行继承 `EmbeddedPluginPackage`。新增内置插件 = 新建类库 + 在 `AddDesktopAgentCore()` 里注册 `IPluginPackage`。

当前进度：**M0–M6 全部完成**（加载器、输入框、侧栏、气泡、设置、内置翻译、双语对照）。

| 阶段 | 状态 | 说明 |
| --- | --- | --- |
| M0 骨架 | ✅ | 扫描、解析、校验、排序、去重；坏插件只降级自己，失败条目带文件 + 行号 / JSON 路径 |
| M1 输入框 | ✅ | 选中插件后发送走插件 Run：chip + 内联参数、会话记忆、助手消息带「翻译 · SDemo」归属 |
| M2 侧栏 | ✅ | 「新建聊天室」下方的插件入口区，点入口新建预选该插件的空会话（`OriginMountId = ActiveMountId`） |
| M3 气泡 | ✅ | 助手消息底部的插件按钮 → 派生卡片（复制 / 重试 / 删除）；会话图标跟随 `LastPluginId`，普通推理后复位；派生内容不进上下文 |
| M4 设置 | ✅ | 「设置 → 插件」：总开关、输入框/侧栏上限、参数记忆、默认呈现、每个插件的启用/输入框/侧栏/气泡/顺序、打开目录、重新扫描；改动即时生效。侧栏依赖输入框（关掉输入框后侧栏入口一并隐藏） |
| M5 内置翻译 | ✅ | `SDemo.DesktopAgent.Plugin.Translate`（三条入口都可用） |
| M6 双语对照 | ✅ | 宿主追加格式契约、`<s>/<t>` 解析、宽屏两列 / 窄屏上下、对照 ↔ 纯译文纯渲染切换（零请求）、模型不按格式返回时降级并提示 |

已实现的接缝：

- `Plugins.Runtime/PluginCatalog.cs`：内置包 + 本地目录两种来源，**吞掉所有解析异常**（坏插件只降级自己），本地覆盖同 `id` 的内置插件，结果缓存并支持 `Scan()` 重新扫描。
- `Plugins.Abstractions/IPluginPackage.cs`：`IPluginPackage` / `IPluginContentSource` / `EmbeddedPluginPackage`；目录源与嵌入资源源共用同一套校验。
- `Plugins.Runtime/PluginManifest.cs`：清单 DTO 与反序列化选项（允许 `//` 注释、尾逗号、大小写不敏感、UTF-8 BOM），未知字段按 JSON 路径记警告。
- `Plugins.Runtime/PluginValidator.cs`：id 格式与唯一性、skill 路径越界（相对路径 + 全路径前缀比较，必须 `.md`）、占位符必须有参数定义、参数 `kind` / `options`、`minHostVersion`、`plugin.json` 512 KB 与 skill 256 KB 上限、图标只收 SVG `path` 数据。
- `Plugins.Abstractions/PluginModels.cs`：`PluginDescriptor` / `Capability` / `Mount` / `Parameter` 描述符，入口组合键 `<pluginId>/<mountId>`。
- `Plugins.Runtime/PluginRegistry.cs`：把扫描结果与用户设置合并成「有效视图」——总开关、插件启用、输入框 / 侧栏 / 气泡开关、顺序、显示上限；设置或重新扫描后立即刷新，UI 只从这里取入口。**侧栏依赖输入框**：关掉输入框入口后侧栏入口也隐藏（侧栏本来就只是进入预选插件的会话）。
- `Contracts/Models/PluginPreferences.cs`：`settings.json` 的 `plugins` 节点（含每个插件的 `enabled` / `inComposer` / `inSidebar` / `inMessage` / `order` 与隐私确认列表）。旧设置文件没有该节点时用默认值，不写迁移。
- `Plugins.Runtime/PluginParameterResolver.cs`：会话记忆 → `default` → `defaultFrom`（`system-language` / `system-locale`）→ `fallback` → 第一个 option；无效记忆值不会把会话卡死。
- `Plugins.Runtime/PluginPromptComposer.cs`：skill 去 frontmatter、替换 `{{key}}` / `{{key.label}}`；用户输入单独成条并包进 `<待处理文本>`，`<` 全角转义；`output = segments` 的能力由宿主追加 `<s>/<t>` 格式契约。
- `Plugins.Runtime/SegmentedOutputParser.cs`：解析 `<s>原文</s><t>译文</t>`（流式中丢弃未闭合尾标签、数量不等以少为准、多出来的译文并入最后一段）；一条完整配对都没有时降级为纯译文。附带流式累积器 `SegmentedOutputAccumulator`。
- `Core/Plugins/PluginRunner.cs`：单轮、无历史（除非能力打开 `allowHistory`）、无工具；超 60,000 字符直接报错而不是发出去。
- `Core/Services/AgentChatService.cs`：抽出 `RunAsync(AgentRunPlan)`，普通对话与插件 Run 共用同一条请求组装。
- `Core/Services/ChatStore.cs`：`sessions` 四个插件列 + `messages.PluginJson` / `DerivedJson`；`UpgradeSchemaAsync` **两张表各扫一遍**，老库升级不报 `no such column`；加载时把上次没跑完的 `Running` 卡片收尾成 `Failed`。
- `UI/Components/Chat/ChatComposer.razor` + `PluginParameterControl.razor`：全局 / 插件模式切换、chip 带 `✕`、最多内联 2 个参数（`select` / `text` / `number` / `toggle`；多选未实现）。
- `UI/Components/Sidebar/PluginRail.razor`：侧栏入口区（图标 + 名称，最多 6 个 + 「更多插件」），点击走 `AppShell.StartNewChat(mount)`，与「新建聊天室」同一条路径。
- `UI/Components/Sidebar/Sidebar.razor`：会话图标跟随 `LastPluginId`（插件被删除时回默认图标）。
- `UI/Components/Plugins/`：`PluginIcon`（只渲染 SVG `path`）、`PluginActionButton`（无参数点击即跑 / 有参数展开面板，OK / Cancel / Esc / 点外部）、`PluginActionBar`（最多内联 2 个，其余进「⋯」）、`DerivedCard`（流式文本、复制 / 重试 / 删除）、`SegmentedOutput`（双语对照宽屏两列 / 窄屏上下，纯译文模式只渲染译文）。
- `Core/Services/AgentMessageMapper.cs`：请求上下文只由消息正文与附件组成——**派生卡片永远不进去**，并有一条单测钉住这个约定。
- `UI/Components/Settings/PluginSettings.razor`：总开关 / 上限 / 参数记忆 / 默认呈现 / 每个插件的启用、入口、顺序，加「打开插件目录」「重新扫描」和失败条目；改动即时生效。
- 隐私：`privacy.sendsText` 的插件首次执行前弹一次确认（取消则不跑），确认结果记在 `plugins.privacyAcknowledged`。

使用方式：侧栏「新建聊天室」下方有「翻译」入口，点一下就是预选翻译的空会话；输入框底部的「翻译」chip 选中后发送走插件 Run；任意助手消息底部（时间右侧）的插件图标点一下，无参数直接跑、有参数弹出语言面板，产物是挂在原消息下的卡片，重启仍在、也不会进入下一轮上下文。`segments` 能力的卡片与消息都带「对照 / 译文」开关，切换只是重新渲染、不重新请求；呈现方式按会话记忆，新会话默认值在「设置 → 插件」。想关掉或调整上限也在「设置 → 插件」。

**测试**：`dotnet test tests/SDemo.DesktopAgent.Core.Tests`（当前 548 个用例）+ `dotnet test tests/SDemo.DesktopAgent.Sandbox.Tests`（Windows 214 通过 / 1 跳过），覆盖上下文、压缩、文件读取 / 搜索 / 补丁、读前写、直跑命令、构建 / 测试解析、todo / 验证门 / checkpoint / 循环保护、小模型模式 / 路由 / 工具修复、插件、沙箱与路径安全等。

## 主题实现

- **唯一来源**：`ThemeService` 读取 `HKCU\...\Themes\Personalize\AppsUseLightTheme`（0 = 深色）。
- **系统切换通知**：`MainWindow` 挂接 `WM_SETTINGCHANGE`，`lParam` 为 `ImmersiveColorSet` 时调用 `App.RefreshTheme()` 重新探测。
- **WPF 侧**：`ThemeResources.Apply()` 替换 `Application.Resources.MergedDictionaries` 里的 `Themes/Dark.xaml` 或 `Themes/Light.xaml`；标题栏、状态栏全部用 `DynamicResource` 引用画笔，因此立即生效。
- **Blazor 侧**：`AppShell` 订阅 `IThemeService.ThemeChanged`，通过 JS 互操作调用 `window.sdTheme.apply("dark" | "light")`，把主题写到 `<html data-theme="…">`；CSS 变量随之切换。
- **首帧不闪**：`css/app.css` 里深色为默认值，并用 `@media (prefers-color-scheme: light)` 兜底，使 Blazor 接管前也显示正确配色；WebView2 的 `DefaultBackgroundColor` 也按当前主题设置。
- 新增配色时，`Themes/Dark.xaml`、`Themes/Light.xaml` 与 `css/app.css` 中三处变量需保持一致。

## 开发约定

**侧栏列表**：工作区来自设置的 `workspaces`（`Workspace` 模型），会话由 `AppShell` 在启动时通过 `ChatStore` 从 SQLite 加载（工作区库 + 历史库）；`Sidebar` 只负责展示与回调（新建 / 打开 / 重命名 / 删除 / 置顶 / 添加工作区），不直接改数据。工作区行点击时“有没有会话”的判断在 `Sidebar.HandleWorkspaceClick`，真正的新建仍由 `AppShell.NewChatInWorkspace` 完成。

**设置**：新增设置项时加到 `AppSettings`，界面改动后调用 `ISettingsService.Save()`；需要跨组件即时生效的用 `SettingsService.Changed` 事件（聊天页的模型下拉就是这么刷新的）。

**新增服务**：平台无关的放 `SDemo.DesktopAgent.Core/Services`，在 `AgentCoreServiceCollectionExtensions.AddDesktopAgentCore()` 里注册；带平台 API 的先在 `Core/Abstractions` 定义接口，再在 WPF / MAUI 两个宿主各写一份实现并注册。Razor 组件用 `@inject` 取用，外壳通过构造函数注入。

**宿主 ↔ Blazor 互操作**：
- Blazor → .NET：直接注入服务调用（主题同步就是例子）。
- .NET → Blazor：共享服务 + 事件，组件里用 `InvokeAsync` 切回渲染器线程。
- JS 互操作：脚本放 `SDemo.DesktopAgent.UI/wwwroot/js/`，用 `IJSRuntime` 调用；`index.html` 里必须用 `_content/SDemo.DesktopAgent.UI/...` 引用。

## 已知注意事项

- 会话库用 `Database.EnsureCreated()` 建表，并在打开旧库时自动补 `AttachmentsJson` 列；**其它表结构变更仍没有迁移**，需要手动处理或删库重建。
- 上传附件目前只把文本内容内联给模型；图片 / PDF 等二进制文件只会给模型文件名、大小和路径，模型看不到内容。单个附件上限 25 MB、一次最多 10 个。
- 关闭窗口默认把应用收进托盘，而不是退出；要恢复“关闭即退出”，把 `MainWindow.OnClosing` 里的隐藏逻辑去掉即可。
- Markdown 代码块目前只有等宽字体和底色，**没有语法高亮**（Markdig 不内置；需要的话可以再接 highlight.js 或 ColorCode）。
- `TargetFramework` 带 `10.0.19041.0`：WebView2 组合渲染控件需要 Windows SDK 投影（`Microsoft.Windows.SDK.NET`），改成纯 `net10.0-windows` 会在启动时抛 `FileNotFoundException`。
- WebView2 用户数据目录指向 `%LOCALAPPDATA%\SDemo\DesktopAgent\WebView2`，避免安装目录只读时启动失败。
- 窗口为无边框自绘标题栏，最大化尺寸由 `WM_GETMINMAXINFO` 钉到显示器工作区，避免内容被裁切；代价是最大化按钮不出现 Windows 11 的贴靠布局浮层（Snap Layouts）。
- 标题栏按钮使用 `Segoe Fluent Icons`（Win11 内置），并以 `Segoe MDL2 Assets` 作为回退。
- 模型 provider 的 BaseUrl 必须带 `/v1`（SDK 只拼 `/chat/completions`）；只填域名时表单会自动补全。
- 火山方舟 **Agent Plan** 的 `/api/plan/v3` 不提供 `/models`（返回 404），模型只能手动添加；平台版 `/api/v3` 才有模型列表接口。
- 设置里的 API Key 用 Windows DPAPI 加密，仅当前用户可解密；换用户/换机器需要重新填写。
- **不要在 WebView2 的事件回调里同步弹模态对话框**（`ShowDialog`）：会跑嵌套消息循环并重入 WebView2，触发 `EmbeddedBrowserWebView.dll` 的原生断言（0x80000003）直接闪退。`FolderPickerService` 用 `Dispatcher.BeginInvoke` 把对话框推迟到下一轮消息循环再打开。
- 自定义搜索接口按 `Authorization: Bearer <key>` + `{"query":…, "top_k":5}` 调用；如果服务端返回 HTTP 200 但 `ResponseMetadata.Error`（例如 `invalid_api_key`），UI 会原样显示错误，需要换成该服务自己的 Key。
- **MAUI 宿主需要 workload**：只调 Windows 时装 `dotnet workload install maui-windows` 即可；要出 Android 包再加 `maui-android`
  （并把 `EnableAndroidTarget` 打开）。别直接装 `maui`——它会顺带拉 iOS / MacCatalyst 的运行时包（几个 GB，Windows 上还用不到）。Android 目标还需要 JDK 17 与 Android SDK：

  ```powershell
  # 首次准备 Android SDK / JDK（之后构建要加 -p:EnableAndroidTarget=true）
  dotnet build src/SDemo.DesktopAgent.Maui -p:EnableAndroidTarget=true -f net10.0-android `
    -t:InstallAndroidDependencies `
    -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk" `
    -p:JavaSdkDirectory="$env:LOCALAPPDATA\Android\jdk" `
    -p:AcceptAndroidSDKLicenses=true
  ```

- **VS 里的 MAUI 启动目标**：`Properties/launchSettings.json` 只暴露 `Windows Machine`，F5 不会再让你误选 Android 模拟器；
  如果 VS 的工具栏仍然显示 Android 目标，说明工程里 `TargetFrameworks` 又带上了 `net10.0-android`（检查 `EnableAndroidTarget` 是否被 env/命令行打开）。

- **两端共用数据时注意锁库**：WPF 与 MAUI-Windows 指向同一个 `%APPDATA%\SDemo\DesktopAgent`，两个进程同时写同一个会话库会 `database is locked`；并行调试用 `SDEMO_DATA_ROOT` 隔离。
- **Android 的能力降级**：没有托盘、没有「选文件夹」对话框（`IFolderPickerService` 返回 `null`，工作区固定为应用私有目录）、没有自绘标题栏；窄屏（≤760px）下侧栏改为顶部的可滚动面板（`app.css` 末尾）。
- MAUI 的 `BlazorWebView` 在 Windows 上同样用 WebView2，所以 `_content/` 静态资源、CSS、JS 与 WPF 端完全一致；Android 换成系统 WebView，`env(safe-area-inset-*)` 依赖 `viewport-fit=cover`（`Maui/wwwroot/index.html` 已设置）。
