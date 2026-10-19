# SDemo.DesktopAgent

Blazor Hybrid + WPF 桌面应用骨架（.NET 10 / WebView2）。

- 命名空间 / 程序集：`SDemo.DesktopAgent`
- 目标框架：`net10.0-windows10.0.19041.0`（Windows 10 2004 及以上）
- 界面：Razor 组件渲染在 `BlazorWebView` 中，窗口外壳（标题栏、状态栏）由 WPF 绘制
- 主题：跟随 Windows「应用模式」（浅色 / 深色），也可在设置里固定浅色 / 深色；WPF 与 Blazor 同步切换
- 模型接入：Microsoft Agent Framework（`Microsoft.Agents.AI` 1.25.0），OpenAI 兼容协议，流式输出
- 依赖注入：WPF 与 Blazor 共享同一个 `IServiceProvider`

## 布局

```
┌──────────────────────────────────────────────────────────────┐
│ 标题栏（WPF 自绘，可拖动 / 双击最大化 / 右侧最小化·最大化·关闭）│
├───────────────────┬──────────────────────────────────────────┤
│ 侧栏 Header       │ 主工作区                                  │
│  · 品牌标识       │  · 启动即进入新建的空聊天室               │
│  · 新建聊天室 ────┼─▶ 空聊天室：欢迎语 + 输入框，垂直居中      │
├───────────────────┤  · 有记录：聊天室名称 / 消息区 / 底部输入框│
│ 侧栏 Content      │  · 有记录：聊天室名称 / 消息区 / 底部输入框│
│  · 工作区（含其下  │  · 设置：右侧整页（通用 / 模型 / 工具）   │
│    的聊天室）     │                                          │
│   ↑ 该区域独立滚动 │                                          │
├───────────────────┤                                          │
│ 侧栏 Footer       │                                          │
│  · 设置           │                                          │
├───────────────────┴──────────────────────────────────────────┤
│ 状态栏（WPF 原生）：Blazor Hybrid · WPF … .NET 版本           │
└──────────────────────────────────────────────────────────────┘
```

侧栏固定 268px，`Header` 与 `Footer` 不滚动，只有中间的 `Content` 区域滚动；聊天室按工作区分组挂在各自的工作区下面。

## 目录结构

```
DesktopAgent/
├─ SDemo.DesktopAgent.sln
└─ src/
   └─ SDemo.DesktopAgent/
      ├─ App.xaml / App.xaml.cs              # 入口：DI 容器、主题初始化、WebView2 运行时检查
      ├─ MainWindow.xaml / .xaml.cs          # 自绘标题栏 + BlazorWebView + 状态栏
      ├─ app.manifest                        # DPI 感知、支持的操作系统
      ├─ _Imports.razor                      # Razor 全局 using
      ├─ Components/
      │  ├─ AppShell.razor                   # 根组件：布局、视图切换（聊天 / 设置）、主题同步
      │  ├─ Sidebar/
      │  │  ├─ Sidebar.razor                 # 侧栏三段式结构 + 新建 / 打开会话 / 设置回调
      │  │  └─ SidebarSection.razor          # 可收起 / 展开的分组列表
      │  ├─ Chat/
      │  │  ├─ ChatRoom.razor                # 聊天室：空态与有记录共用 + 流式生成
      │  │  └─ ChatComposer.razor            # 组合输入框：Header / 多行输入 / Skills + 发送
      │  └─ Settings/
      │     ├─ SettingsView.razor            # 设置页骨架：通用 / 模型 / 工具
      │     ├─ GeneralSettings.razor         # 外观、语言、版本信息
      │     ├─ ModelSettings.razor           # 提供商列表 + 底部固定的「添加模型提供商」
      │     ├─ ProviderEditor.razor          # 添加/编辑表单：Id / 名称 / 地址 / Key / 模型目录
      │     ├─ ToolsSettings.razor           # 联网搜索后端、文件写入确认
      │     └─ SettingsSection.cs            # 设置分区枚举
      ├─ Models/
      │  ├─ ChatMessage.cs                   # 消息模型（角色 + 内容）
      │  ├─ ChatSession.cs                   # 会话模型（标题、时间、消息列表）
      │  ├─ AppSettings.cs                   # 设置根对象（主题 / 语言 / 提供商）
      │  └─ ModelProvider.cs                 # 模型提供商与模型目录
      ├─ Services/
      │  ├─ IAppEnvironmentService.cs        # 应用 / 运行环境信息（含 WebView2 版本）
      │  ├─ AppEnvironmentService.cs
      │  ├─ ISettingsService.cs              # 设置读写（%APPDATA%\SDemo\DesktopAgent\settings.json）
      │  ├─ SettingsService.cs
      │  ├─ IThemeService.cs                 # 主题来源 + 外观偏好（System / Light / Dark）
      │  ├─ ThemeService.cs
      │  ├─ ModelCatalogService.cs           # GET {baseUrl}/models 读取模型目录
      │  └─ AgentChatService.cs              # Agent Framework 流式调用（OpenAI 兼容）
      ├─ Theme/
      │  └─ ThemeResources.cs                # 运行时替换 WPF 主题资源字典
      ├─ Themes/
      │  ├─ Dark.xaml                        # 深色 WPF 画笔
      │  ├─ Light.xaml                       # 浅色 WPF 画笔
      │  └─ Chrome.xaml                      # 标题栏按钮等与主题无关的样式
      └─ wwwroot/
         ├─ index.html                       # BlazorWebView 宿主页面
         ├─ css/app.css                      # 界面样式（含深/浅色变量）
         └─ js/
            ├─ theme.js                      # 供 .NET 调用：切换 data-theme
            ├─ chat.js                       # 消息区滚到底部
            └─ composer.js                   # 输入框键盘约定（Enter 发送 / Shift、Ctrl+Enter 换行）
```

## 运行

```powershell
dotnet run --project src/SDemo.DesktopAgent
```

或用 Visual Studio 打开 `SDemo.DesktopAgent.sln`，把 `SDemo.DesktopAgent` 设为启动项目后 F5。

> 需要机器上已安装 **Microsoft Edge WebView2 Runtime**（Win10/11 通常已预装）。
> 未安装时应用会弹提示并退出，下载地址：https://developer.microsoft.com/microsoft-edge/webview2/

调试时如果不方便改系统主题，可以用环境变量强制预览：

```powershell
$env:SDEMO_FORCED_THEME = 'light'   # 或 dark；仅 Debug 构建生效
dotnet run --project src/SDemo.DesktopAgent
```

## 聊天室与输入框

点击侧栏「新建聊天室」→ 右侧切到空聊天室；点击「历史聊天」中的条目 → 右侧切到对应聊天室。两种形态由同一个 `ChatRoom` 组件根据 `ChatSession.Messages` 是否为空切换：

| 状态 | 结构 |
| --- | --- |
| 空聊天室 | 欢迎语 + 输入框整体垂直居中（输入框水平居中、最大宽 760px） |
| 有记录 | 顶部聊天室名称（左对齐）→ 中间消息区（独立滚动）→ 底部输入框 |

`ChatComposer` 是组合输入框，自下而上分三块：

1. **Header**：`HeaderContent` 参数；未传内容时整块不渲染、不占高度（后续可放附件、上下文标签）。
2. **输入区**：`<textarea>` 多行输入，`field-sizing: content` 自动增高（上限 200px 后内部滚动）。
3. **底部功能区**：左侧 Skills 占位按钮（技能 / 搜索 / 文件），右侧的模型选择器紧贴发送按钮左边；`IsGenerating` 为 true 时发送按钮切换为停止图标（`.is-stop`）。模型选择会记到设置里（`lastProviderId` / `lastModelId`），下次启动自动选中。

输入为空时发送按钮禁用。键盘约定由 `js/composer.js` 处理（`document` 捕获阶段监听，元素重渲染后依然有效）：

| 按键 | 行为 |
| --- | --- |
| `Enter` | 发送（`preventDefault`，不插入换行） |
| `Shift + Enter` | 换行（浏览器默认行为） |
| `Ctrl + Enter` | 换行（浏览器默认不插入，脚本在光标处插入 `\n` 并同步给 Blazor） |

输入法组合中（`event.isComposing`）的回车不触发发送。

**发送后的行为**：

1. 用户消息写入 `ChatSession.Messages`，界面立刻从「空态居中」切到「有记录布局」。
2. 首条消息用于生成会话标题（首行前 18 字）与时间，并把它插入左侧「历史聊天」列表顶部——新建的聊天室在发送前不会出现在列表里。
3. 清空输入框，消息区滚到底部。
4. 若已配置模型（输入框 Header 里的模型选择器），调用 Agent Framework 流式生成，助手消息边生成边追加；生成中发送按钮变为停止，点击可中断（保留已生成的部分）。
5. **思维链**：模型返回 `reasoning_content` 时（如 DeepSeek 系列），MAF 会把它拆成 `TextReasoningContent`；应用把正文与思维链分开接收，消息上方显示「思考过程 · N 字」，默认展开、可折叠。普通模型没有该字段则不显示。

会话目前只存在内存中（重启后历史会话会回到种子数据，重命名 / 置顶 / 删除也会重置）。

## 工作区与会话列表

- **工作区**：持久化在设置的 `workspaces` 里（默认工作区 = `我的文档\SDemo DesktopAgent`）。侧栏工作区行 hover 出现「＋ 新建聊天室」和「⋯ 更多（重命名 / 删除）」；点击行本身会把它设为当前工作区。
- **聊天室挂在所属工作区下面**（缩进一级），不再单独出现在「历史聊天」里；置顶的会话排在该工作区最前。
- 重命名只改显示名，不动目录；删除工作区不会删磁盘文件；只剩一个工作区时不允许删除。
- **工作区选择器**：只在「新建聊天室」（空会话）时出现在输入框上方，下拉列表底部是「＋ 选择工作区…」，点了会打开系统文件夹选择框，选中的目录会建成新工作区并选中。发送第一条消息后选择器随空态一起消失。
- **会话行**：左侧是会话图标（SVG），置顶标记固定在右侧；「置顶 / 更多（重命名 / 删除）」只在鼠标移入时出现（选中状态不会常亮）。重命名是行内输入（Enter 保存 / Esc 取消）；删除当前会话会回到新的空聊天室。
- 文件工具的根目录跟着当前聊天室所属工作区走（`WorkspaceContext`）。

## 窗口外观

- **图标**：`Assets/app.ico`（由脚本生成的 7 个尺寸 16–256，渐变圆角方块 + 白色 D），同时用于 exe（`ApplicationIcon`）和窗口（`Window.Icon`）。
- **圆角**：Windows 11 下通过 `DWMWA_WINDOW_CORNER_PREFERENCE` 设成圆角（`DwmSetWindowAttribute`），内容 Border 同步 8px 圆角；最大化时切回直角。
- **最小尺寸**：`MinWidth=1024`、`MinHeight=680`，窗口默认 1240×800。
- 最大化尺寸仍由 `WM_GETMINMAXINFO` 钉到显示器工作区。

## 设置页

右侧整页，顶部分区切换，两个分区各自独立滚动：

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

输入框底部三个 chip 是工具开关（默认全关，开启后高亮），下一次发送生效：

| Chip | 工具 | 实现 |
| --- | --- | --- |
| 技能 | `list_skills` / `read_skill` | 读取 `%APPDATA%\SDemo\DesktopAgent\skills\*.md`，模型先看技能说明再按步骤执行 |
| 搜索 | `web_search` | 默认 Bing RSS；可在「设置 → 工具设置」换成自定义搜索 API（地址 + Key） |
| 文件 | `list_files` / `read_file` / `write_file` / `workspace_info` | 工作区 `%USERPROFILE%\Documents\SDemo DesktopAgent`，路径被限制在工作区内，防止 `../` 越界 |

- 工具通过 `AsAIAgent(tools: …)` 挂到 agent 上；`ChatClientAgent` 默认会自动执行工具调用并继续生成，不需要手写循环。
- 已用本地 mock 验证：开工具后请求体带 `tools` 数组；模型返回 `tool_calls` 后应用执行函数，并把 `role: tool` 的结果回传，再产出最终回复。
- **写文件会先弹确认框**（路径 + 内容预览，可拒绝）；确认逻辑走 `FileWriteApprovalService`，工具线程 await 用户选择，停止生成/关闭页面按拒绝处理。
- 写文件是唯一有副作用的工具，工作区路径固定，不接受绝对路径与 `..` 越界。

## 主题实现

- **唯一来源**：`ThemeService` 读取 `HKCU\...\Themes\Personalize\AppsUseLightTheme`（0 = 深色）。
- **系统切换通知**：`MainWindow` 挂接 `WM_SETTINGCHANGE`，`lParam` 为 `ImmersiveColorSet` 时调用 `App.RefreshTheme()` 重新探测。
- **WPF 侧**：`ThemeResources.Apply()` 替换 `Application.Resources.MergedDictionaries` 里的 `Themes/Dark.xaml` 或 `Themes/Light.xaml`；标题栏、状态栏全部用 `DynamicResource` 引用画笔，因此立即生效。
- **Blazor 侧**：`AppShell` 订阅 `IThemeService.ThemeChanged`，通过 JS 互操作调用 `window.sdTheme.apply("dark" | "light")`，把主题写到 `<html data-theme="…">`；CSS 变量随之切换。
- **首帧不闪**：`css/app.css` 里深色为默认值，并用 `@media (prefers-color-scheme: light)` 兜底，使 Blazor 接管前也显示正确配色；WebView2 的 `DefaultBackgroundColor` 也按当前主题设置。
- 新增配色时，`Themes/Dark.xaml`、`Themes/Light.xaml` 与 `css/app.css` 中三处变量需保持一致。

## 开发约定

**侧栏列表**：`_workspaces` 是侧栏内的占位数据；历史聊天来自 `AppShell` 的 `_sessions`（种子数据，见 `CreateSeedSessions()`），接入真实存储时换成服务注入，`Sidebar` 只负责展示与回调（`OnNewChat` / `OnOpenSession` / `OnOpenSettings`）。

**设置**：新增设置项时加到 `AppSettings`，界面改动后调用 `ISettingsService.Save()`；需要跨组件即时生效的用 `SettingsService.Changed` 事件（聊天页的模型下拉就是这么刷新的）。

**新增服务**：`Services/` 下定义接口与实现，在 `App.xaml.cs` 的 `CreateServiceProvider()` 中注册；Razor 组件用 `@inject` 取用，WPF 侧通过构造函数注入。

**WPF ↔ Blazor 互操作**：
- Blazor → .NET：直接注入服务调用（主题同步就是例子）。
- .NET → Blazor：共享服务 + 事件，组件里用 `InvokeAsync` 切回渲染器线程。
- JS 互操作：脚本放 `wwwroot/js/`，用 `IJSRuntime` 调用。

## 已知注意事项

- `TargetFramework` 带 `10.0.19041.0`：WebView2 组合渲染控件需要 Windows SDK 投影（`Microsoft.Windows.SDK.NET`），改成纯 `net10.0-windows` 会在启动时抛 `FileNotFoundException`。
- WebView2 用户数据目录指向 `%LOCALAPPDATA%\SDemo\DesktopAgent\WebView2`，避免安装目录只读时启动失败。
- 窗口为无边框自绘标题栏，最大化尺寸由 `WM_GETMINMAXINFO` 钉到显示器工作区，避免内容被裁切；代价是最大化按钮不出现 Windows 11 的贴靠布局浮层（Snap Layouts）。
- 标题栏按钮使用 `Segoe Fluent Icons`（Win11 内置），并以 `Segoe MDL2 Assets` 作为回退。
- 模型 provider 的 BaseUrl 必须带 `/v1`（SDK 只拼 `/chat/completions`）；只填域名时表单会自动补全。
- 火山方舟 **Agent Plan** 的 `/api/plan/v3` 不提供 `/models`（返回 404），模型只能手动添加；平台版 `/api/v3` 才有模型列表接口。
- 设置里的 API Key 用 Windows DPAPI 加密，仅当前用户可解密；换用户/换机器需要重新填写。
- **不要在 WebView2 的事件回调里同步弹模态对话框**（`ShowDialog`）：会跑嵌套消息循环并重入 WebView2，触发 `EmbeddedBrowserWebView.dll` 的原生断言（0x80000003）直接闪退。`FolderPickerService` 用 `Dispatcher.BeginInvoke` 把对话框推迟到下一轮消息循环再打开。
- 自定义搜索接口按 `Authorization: Bearer <key>` + `{"query":…, "top_k":5}` 调用；如果服务端返回 HTTP 200 但 `ResponseMetadata.Error`（例如 `invalid_api_key`），UI 会原样显示错误，需要换成该服务自己的 Key。
