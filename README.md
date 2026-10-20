# SDemo.DesktopAgent

Blazor Hybrid 多宿主应用：**一套 Razor 组件 + 一套业务服务**，同时挂在 **WPF** 与 **.NET MAUI**（Windows / Android）两个外壳里。

| 工程 | 目标框架 | 作用 |
| --- | --- | --- |
| `SDemo.DesktopAgent.Core` | `net10.0` | 模型、服务、平台接口；不含任何 UI / 平台 API |
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
├─ SDemo.DesktopAgent.Wpf.slnf          # 只含桌面端三个工程，没装 MAUI workload 也能构建
└─ src/
   ├─ SDemo.DesktopAgent.Core/                   # 平台无关：模型 + 服务 + 接口
   │  ├─ Models/                                 # 会话 / 消息 / 附件 / 设置 / 工作区
   │  ├─ Data/ChatDbContext.cs                   # SQLite + EF Core：sessions / messages
   │  ├─ Abstractions/                           # IAppPaths / ISecureStore / ISystemThemeProvider / IFolderPickerService
   │  ├─ Services/                               # ChatStore、SettingsService、SkillCatalog、AgentChatService…
   │  │  └─ ByteSize.cs                          # 文件大小文案（Core 与 UI 共用）
   │  └─ Composition/AgentCoreServiceCollectionExtensions.cs   # AddDesktopAgentCore()
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
3. **底部功能区**：左侧是「＋ 上传文件」和常用技能（最多直接显示 3 个，超出的进「更多」）；右侧依次是**搜索**、**文件**两个可同时点亮的图标按钮、模型选择器、发送按钮。`IsGenerating` 为 true 时发送按钮切换为停止图标（`.is-stop`）。模型选择会记到设置里（`lastProviderId` / `lastModelId`），下次启动自动选中。

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

右侧整页，顶部分区切换（通用 / 模型 / 工具 / 插件），各分区独立滚动：

**通用设置**

- **外观**：跟随系统 / 浅色 / 深色。写入 `AppSettings.Theme`，`ThemeService.SetPreference()` 立即切换 WPF 资源字典与 WebView，`SettingsService.Save()` 落盘；“跟随系统”时仍响应 `WM_SETTINGCHANGE`。
- **语言**：目前只有简体中文（`en-US` 选项禁用，等资源本地化接入）。
- **版本信息**：应用版本、.NET 运行时、操作系统、WebView2 版本。

**模型设置**
- 提供商列表；列表滚动区独立，**「添加模型提供商」固定在列表底部**（列表超出高度时也在视口底部，不会跟着滚动）。
- 点击后该按钮位置变成 `ProviderEditor` 表单：ProviderId、显示名称（为空时水印跟随 ProviderId）、API 地址、API Key（可切换显示/隐藏）、模型目录。
- 卡片上的「编辑」可以直接改地址 / Key / 模型目录，不必删了重建。
- 模型目录支持两种方式：**获取模型**（`GET {baseUrl}/models`，标准 OpenAI 返回 `data[].id`）与**手动添加**（回车或点「添加」），已添加的模型以 chip 形式展示、可单个移除。
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

## 技能与工具

输入框底部现在是「上传文件 + 常用技能」和右侧的「工具 + 模型 + 发送」：

- **＋ 上传文件**：多选，附件显示在 Header（新聊天室在工作区选择器下面），发送后随消息落库。
- **常用技能**：直接读取技能库，最多同时显示 3 个，超出的放进「更多」菜单；点击后**只显示选中的那一个 chip**（带 `✕` 取消），其余技能隐藏，下一次发送把该技能全文注入系统提示词。
- **模型工具（多选）**：右侧是「联网搜索」「文件读写」两个图标按钮，并排显示，各自独立开关、**可以同时启用**；点亮即启用，再点取消。启用了哪些，本次请求就只下发哪些工具；都不点亮 = 不下发任何工具。

| 工具 | 下发的工具 | 实现 |
| --- | --- | --- |
| 联网搜索 | `web_search` | 默认 Bing RSS；可在「设置 → 工具设置」换成自定义搜索 API（地址 + Key） |
| 文件读写 | `list_files` / `read_file` / `write_file` / `workspace_info` | 当前会话所属工作区；历史聊天用 `%USERPROFILE%\Documents\SDemo Desktop Agent`。路径被限制在根目录内，防止 `../` 越界；写文件仍会先弹确认框 |

### 技能格式

技能就是一个 Markdown 文件，**文件名就是技能名**。推荐使用标准的 YAML frontmatter，输入框的技能列表会优先取其中的 `description` 作为悬浮说明：

```markdown
---
name: 总结
description: 一句话说明这个技能做什么、什么时候用。
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
- **写文件会先弹确认框**（路径 + 内容预览，可拒绝）；确认逻辑走 `FileWriteApprovalService`，工具线程 await 用户选择，停止生成/关闭页面按拒绝处理。
- 写文件是唯一有副作用的工具，工作区路径固定，不接受绝对路径与 `..` 越界。

## 插件

插件是**零代码**的声明式能力包：一个目录 + `plugin.json` + 若干 `.md` 技能文件，不需要编译、不需要重装应用。完整设计见 [docs/plugin-system.md](docs/plugin-system.md)。

目录：

| 来源 | 位置 | 说明 |
| --- | --- | --- |
| 本地 | `%APPDATA%\SDemo\DesktopAgent\plugins\<目录>\` | 用户自制；同 `id` 时覆盖内置插件 |
| 内置 | `<exe 目录>\plugins\<目录>\` | 随应用发布，仓库里是根目录的 `plugins/`，构建时复制 |

当前进度：**M0（加载器）+ M1（输入框）+ M5（内置翻译插件）已完成**。

| 阶段 | 状态 | 说明 |
| --- | --- | --- |
| M0 骨架 | ✅ | 扫描、解析、校验、排序、去重；「设置 → 插件」只读列表能看到可用 / 不可用及原因（文件 + 行号 / JSON 路径） |
| M1 输入框 | ✅ | 选中插件后发送走插件 Run：chip + 内联参数、会话记忆、助手消息带「翻译 · SDemo」归属 |
| M2 侧栏 | ✅ | 「新建聊天室」下方的插件入口区，点入口新建预选该插件的空会话（`OriginMountId = ActiveMountId`） |
| M3 气泡 | ⬜ | 对既有回答做加工，产物是派生卡片 |
| M4 设置 | ⬜ | 启用开关、显示上限、打开插件目录；目前设置页只是只读列表 |
| M5 内置翻译 | ✅ | `plugins/translate/`（三条入口只有输入框能跑，另外两条等 M2 / M3） |
| M6 双语对照 | ⬜ | 目前 `segments` 输出按纯文本处理 |

已实现的接缝：

- `Core/Plugins/PluginCatalog.cs`：扫内置 + 本地两个目录，**吞掉所有解析异常**（坏插件只降级自己），本地覆盖同 `id` 的内置插件，结果缓存并支持 `Scan()` 重新扫描。
- `Core/Plugins/PluginManifest.cs`：清单 DTO 与反序列化选项（允许 `//` 注释、尾逗号、大小写不敏感、UTF-8 BOM），未知字段按 JSON 路径记警告。
- `Core/Plugins/PluginValidator.cs`：id 格式与唯一性、skill 路径越界（`Path.GetFullPath` + 前缀比较，必须 `.md`）、占位符必须有参数定义、参数 `kind` / `options`、`minHostVersion`、`plugin.json` 512 KB 与 skill 256 KB 上限、图标只收 SVG `path` 数据。
- `Core/Plugins/PluginModels.cs`：`PluginDescriptor` / `Capability` / `Mount` / `Parameter` 描述符，入口组合键 `<pluginId>/<mountId>`。
- `Core/Plugins/PluginParameterResolver.cs`：会话记忆 → `default` → `defaultFrom`（`system-language` / `system-locale`）→ `fallback` → 第一个 option；无效记忆值不会把会话卡死。
- `Core/Plugins/PluginPromptComposer.cs`：skill 去 frontmatter、替换 `{{key}}` / `{{key.label}}`；用户输入单独成条并包进 `<待处理文本>`，`<` 全角转义。
- `Core/Plugins/PluginRunner.cs`：单轮、无历史（除非能力打开 `allowHistory`）、无工具；超 60,000 字符直接报错而不是发出去。
- `Core/Services/AgentChatService.cs`：抽出 `RunAsync(AgentRunPlan)`，普通对话与插件 Run 共用同一条请求组装。
- `Core/Services/ChatStore.cs`：`sessions` 四个插件列 + `messages.PluginJson`；`UpgradeSchemaAsync` 现在**两张表各扫一遍**，老库升级不报 `no such column`。
- `UI/Components/Chat/ChatComposer.razor` + `PluginParameterControl.razor`：全局 / 插件模式切换、chip 带 `✕`、最多内联 2 个参数（`select` / `text` / `number` / `toggle`；多选未实现）。
- `UI/Components/Sidebar/PluginRail.razor`：侧栏入口区（图标 + 名称，最多 6 个 + 「更多插件」），点击走 `AppShell.StartNewChat(mount)`，与「新建聊天室」同一条路径。
- `UI/Components/Plugins/PluginIcon.razor`：按清单里的 SVG `path` 数据渲染图标，没有 path 时退回首字。
- `UI/Components/Settings/PluginSettings.razor`：M0 的只读列表（M4 会换成正式管理页）。

使用方式：重启应用后，侧栏「新建聊天室」下方出现「翻译」入口，点一下就是预选翻译的空会话；输入框底部也有「翻译」chip，点它进入插件模式，选目标语言后发送即可。切到别的会话再回来，插件与语言仍然保留（会话级记忆）。「无参数插件点一下就执行」要等 M3 的气泡按钮。

**测试**：`dotnet test tests/SDemo.DesktopAgent.Core.Tests`（78 个用例：清单解析、路径穿越、占位符、去重、超体积、`minHostVersion`、参数校验、DI 注册、参数解析顺序、提示词组装、老库补列、Run 请求组装、侧栏入口过滤、内置 translate 清单）。

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
