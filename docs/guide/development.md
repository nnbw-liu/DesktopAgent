# 开发约定、回归与注意事项

> 由根 README 拆出（2026-10-31），内容与原章节一致，仅调整链接前缀。

## 可信度与回归（R1）

R1 不新增用户功能，补齐的是「敢改」的基础设施。报告与清单：

- [docs/reports/r1-validation.md](../reports/r1-validation.md)：规模化验收（30 工具 / 20 技能 / 20 归档）与 8K / 32K 真机结果，数字全部来自运行器。
- [docs/reports/r1-ui-checklist.md](../reports/r1-ui-checklist.md)：WebView2 手动验收清单。
- [docs/reports/ui-baseline/](../reports/ui-baseline/)：组件渲染截图基线（同一份 Razor + `app.css`）。

常用命令：

```powershell
# 上下文 / 技能 / 归档的规模化验收（离线；写 eval/results/r1/summary.json）
dotnet run --project tools/r1-validate -c Release -- --out eval/results/r1

# 想额外验证「带工具 schema 的请求真能发出」（读 %APPDATA% 里的 provider/Key）
dotnet run --project tools/r1-validate -c Release -- --out eval/results/r1 --live --provider local

# Blazor 组件回归（bUnit：占用环 / 分段明细 / 压缩分隔线 / 验证徽标 / 无修改回灌）
dotnet test tests/SDemo.DesktopAgent.UI.Tests

# 截图基线：先生成 HTML，再用系统 WebView2 运行时截成 PNG
$env:SDEMO_UI_BASELINE=1; dotnet test tests/SDemo.DesktopAgent.UI.Tests --filter UiBaselineTests
dotnet run --project tools/ui-baseline/WebViewShot -c Release -- docs/reports/ui-baseline
```

可观测性补全：摘要请求的输入 / 输出 token 进入上下文报告（分解面板显示「摘要成本」）；每轮记录真实审批次数（评测的自动审批不计入）；侧栏「用量与成本」以应用内弹窗展示全局统计（价格在 `settings.json` 的模型条目里配 `inputPricePerMillion` / `outputPricePerMillion`）；设置 → 上下文新增「路由模型」选择。

## 开发约定

**侧栏列表**：工作区来自设置的 `workspaces`（`Workspace` 模型），会话由 `AppShell` 在启动时通过 `ChatStore` 从 SQLite 加载（工作区库 + 历史库）；`Sidebar` 只负责展示与回调（新建 / 打开 / 重命名 / 删除 / 置顶 / 添加工作区），不直接改数据。工作区行点击时“有没有会话”的判断在 `Sidebar.HandleWorkspaceClick`，真正的新建仍由 `AppShell.NewChatInWorkspace` 完成。

**设置**：新增设置项时加到 `AppSettings`，界面改动后调用 `ISettingsService.Save()`；需要跨组件即时生效的用 `SettingsService.Changed` 事件（聊天页的模型下拉就是这么刷新的）。

**新增服务**：平台无关的放 `SDemo.DesktopAgent.Core/Services`，在 `AgentCoreServiceCollectionExtensions.AddDesktopAgentCore()` 里注册；带平台 API 的先在 `Core/Abstractions` 定义接口，再在 WPF / MAUI 两个宿主各写一份实现并注册。Razor 组件用 `@inject` 取用，外壳通过构造函数注入。

**宿主 ↔ Blazor 互操作**：
- Blazor → .NET：直接注入服务调用（主题同步就是例子）。
- .NET → Blazor：共享服务 + 事件，组件里用 `InvokeAsync` 切回渲染器线程。
- JS 互操作：脚本放 `SDemo.DesktopAgent.UI/wwwroot/js/`，用 `IJSRuntime` 调用；`index.html` 里必须用 `_content/SDemo.DesktopAgent.UI/...` 引用。

## 已知注意事项

- 会话库改为 **EF Core 版本化迁移**（`src/Storage/SDemo.DesktopAgent.Storage/Migrations`）：新库直接 `Migrate()`；老库先补 `EnsureCreated` 时代的缺列，再把 `InitialCreate` 记为已应用（基线化），之后只跑真正新增的迁移。FTS5 知识库表是唯一例外，仍由 `ArchiveSchema` 在迁移后按需建（FTS5 缺失只影响知识库）。新增表结构请用 `dotnet ef migrations add`，不要再手写补列。
- 上传附件会把可读内容内联给模型：文本文件直接内联，PDF 走沙箱提取前几页，`.docx` / `.xlsx` / `.pptx` 自动转成 Markdown sidecar（各默认 24000 字符）；图片 / 其他二进制只给文件名、大小和路径。单个附件上限 25 MB、一次最多 10 个。
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
  dotnet build src/Hosts/Maui -p:EnableAndroidTarget=true -f net10.0-android `
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
