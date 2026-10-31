# 技能、插件与主题

> 由根 README 拆出（2026-10-31），内容与原章节一致，仅调整链接前缀。

## 技能与工具
输入框底部现在是「上传文件 + 常用技能」和右侧的「工具 + 模型 + 发送」：

- **先判断做什么**：每轮先分「对话 / 问答 / 任务」。聊天、寒暄、致谢不注入任何工具，直接回答；问答只给读 / 搜；只有明确要改文件、跑命令的任务才给写与执行工具。工具是「按需」而不是「每轮都用」。

- **＋ 上传文件**：多选，附件显示在 Header（新聊天室在工作区选择器下面），发送后随消息落库。
- **常用技能**：直接读取技能库，最多同时显示 3 个，超出的放进「更多」菜单；每个 chip 左侧是技能图标（frontmatter 的 `icon`，没写就取名字首字）。点击后**只显示选中的那一个 chip**（带 `✕` 取消），其余技能隐藏，下一次发送把该技能全文注入系统提示词。某个技能不想占位置时，在「设置 → 技能」里关掉它的「在输入框显示」：chip 不再出现，但 `/技能名` 与 `/` 菜单照常可用。
- **模型工具**：右侧只保留「联网搜索」这类可选外部工具，点亮即启用。文件读写、Office 文档是内置能力，默认开启，不再占输入框 chip；在「设置 → 工具 → 代码与文件工具」可按 `files`（只读）/ `edits`（写入）/ `office`（文档）分包关闭。
- **权限与沙箱**：工具 chip 最左侧是两个聊天室级开关——「完全权限」（跳过确认 + 关沙箱）和「沙箱」。详见上文「聊天室级权限」。

| 工具 | 下发的工具 | 实现 |
| --- | --- | --- |
| 联网搜索 | `web_search` | 默认 Bing RSS；可在「设置 → 工具 → 联网搜索」换成自定义搜索 API（地址 + Key） |
| `files`（只读，内置） | `list_files` / `list_tree` / `read_file` / `search_files` / `glob_files` / `workspace_info` | 按行读带行号与续读；搜索 / glob 纯 .NET；排除 `bin` / `obj` / `.git` 等；路径限制在工作区内 |
| `office`（内置） | `office_info` / `office_read` / `office_write` | Word / Excel / PowerPoint 以 Markdown 进出：读取时还原标题、段落、列表、表格、幻灯片与备注；写入按扩展名生成 .docx / .xlsx / .pptx；上传附件时自动提取 Markdown sidecar，模型可直接分析 |
| `edits`（写入，内置） | `apply_patch` / `write_file` | `apply_patch` 应用 unified diff（多文件、全有或全无）；写已有文件前必须先 `read_file`，且审批框显示 diff |
| `command`（直跑，默认关闭） | `run_command` | 白名单命令（dotnet build / test 等）在宿主直跑；参数不经过 shell、每次审计、可本会话授权；`run_powershell` 默认仍在沙箱，只有在聊天室里关掉沙箱才直跑 |

### Office 文档（docx / xlsx / pptx）

- **读**：`office_read` 把文档转成 Markdown。Word 保留标题层级（内置 Heading 样式，手动加粗大字号也会识别）、段落、列表与表格；Excel 每张工作表一个 `## 名称` + Markdown 表格，支持 `sheet` / `range` / `maxRows`；PowerPoint 每页一个 `## 第 N 页 · 标题`，要点为列表，备注单独一行，图片只报数量。
- **先看结构**：`office_info` 返回 Word 标题结构、Excel 工作表与行数、PPT 页数与页标题。大文档先看结构，再用 `slides` / `sheet` / `range` 分批读。
- **写**：`office_write` 的 `content` 是 Markdown。Word 直接写标题 / 段落 / 列表 / 表格 / 代码块；Excel 用 Markdown 表格（`## 名称` 是工作表名，也可直接贴 CSV）；PowerPoint 用 `---` 分页、`#` / `##` 当页标题。只能写工作区，默认不覆盖已有文件（`overwrite=true` 才覆盖），每次写入都走审批。
- **上传即读**：`＋ 上传文件` 选 .docx / .xlsx / .pptx 时，会在旁边写一份 `.office.md` 提取文本（默认前 24000 字符，失败不影响上传）。把 PPT 拖进来直接问"总结需求和计划"，内容已经在上下文里；要读被截断的部分再调 `office_read`。
- **格式边界**：只支持 OOXML（.docx / .xlsx / .pptx），旧版 .doc / .xls / .ppt 需先另存；不还原字体、颜色、动画、图表数据，Excel 公式读缓存值（无缓存时读公式原文）。


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

PowerShell 与 PDF 解析默认在沙箱里运行；沙箱不可用时直接拒绝执行，**不会**自动退回主进程直跑。
只有用户在聊天室输入框里主动关掉沙箱（或在调试构建里从设置页关闭），那一间才会改成宿主直跑。
完整开发手册见 [docs/architecture/detail/sandbox-development-manual.md](../architecture/detail/sandbox-development-manual.md)，
S6 验收与偏差见 [docs/reports/sandbox-s6-report.md](../reports/sandbox-s6-report.md)，
UI 逐项验收记录见 [docs/reports/sandbox-ui-acceptance-s6.md](../reports/sandbox-ui-acceptance-s6.md)，
常见问题见 [docs/features/sandbox-troubleshooting.md](../features/sandbox-troubleshooting.md)。

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
  输入框只保留联网搜索这类可选外部工具。PowerShell 每次执行仍需审批；沙箱不可用时直接拒绝。
  设置页仍可全局关闭某个内置工具。
- **直跑（无沙箱）**：在聊天室输入框里关掉沙箱（或直接选「完全权限」）后，这一间的 PowerShell 改为
  宿主直跑——同一份脚本经 `-EncodedCommand` 交给 `powershell.exe`，工作目录仍在工作区内，
  超时 1–120 秒、输出按上限截断，改动立即落到真实工作区；审计里记为 `sandbox: None`、`mode: direct`。
  审批不因此消失：只有「完全权限」才会跳过确认。PDF 解析依赖 AppContainer，关沙箱后不可用。
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

插件是**零代码**的声明式能力包：一个目录 + `plugin.json` + 若干 `.md` 技能文件，不需要编译、不需要重装应用。完整设计见 [docs/architecture/detail/plugin-system.md](../architecture/detail/plugin-system.md)；**自己写插件请看 [插件开发手册](../features/plugin-authoring.md)**（清单字段、参数、入口、双语对照、报错对照表、可复制模板）。

应用还自带一个 `plugin-authoring` 内置技能（启动时补进技能库）：直接对 Agent 说「帮我做一个插件」，它会按手册规则写清单与技能，并用 `templates/` 里的模板带你验收；内置插件的 C# 开发路径也在技能包的 `references/builtin.md` 里。详见 [插件开发手册 §16](../features/plugin-authoring.md#16-让-agent-自己做插件内置技能)。

目录：

| 来源 | 位置 | 说明 |
| --- | --- | --- |
| 本地 | `%APPDATA%\SDemo\DesktopAgent\plugins\<目录>\` | 用户自制；同 `id` 时覆盖内置插件 |
| 内置 | 编译进 `SDemo.DesktopAgent.Plugin.*` 类库 | 清单与技能是嵌入资源，不落盘、不需要复制目录；Android 与 Windows 一致 |

每个内置插件是一个独立类库：`plugin.json` / `skills/*.md` 通过 `EmbeddedResource LogicalName` 固定为插件内相对路径，类里只需一行继承 `EmbeddedPluginPackage`。新增内置插件 = 新建类库 + 在 `AddDesktopAgentCore()` 里注册 `IPluginPackage`。

当前进度：**M0–M7 全部完成**（加载器、输入框、侧栏、气泡、设置、内置翻译、双语对照、划词菜单、简单问答、上下文开关）。

| 阶段 | 状态 | 说明 |
| --- | --- | --- |
| M0 骨架 | ✅ | 扫描、解析、校验、排序、去重；坏插件只降级自己，失败条目带文件 + 行号 / JSON 路径 |
| M1 输入框 | ✅ | 选中插件后发送走插件 Run：chip + 内联参数、会话记忆、助手消息带「翻译 · SDemo」归属 |
| M2 侧栏 | ✅ | 「新建聊天室」下方的插件入口区，点入口新建预选该插件的空会话（`OriginMountId = ActiveMountId`） |
| M3 气泡 | ✅ | 助手消息底部的插件按钮 → 派生卡片（复制 / 重试 / 删除）；会话图标跟随 `LastPluginId`，普通推理后复位；派生内容默认不进上下文，可点「加入上下文」 |
| M4 设置 | ✅ | 「设置 → 插件」：总开关、输入框/侧栏/划词上限、参数记忆、默认呈现、每个插件的启用/输入框/侧栏/气泡/选中/顺序、打开目录、重新扫描；改动即时生效。侧栏依赖输入框（关掉输入框后侧栏入口一并隐藏） |
| M5 内置翻译 | ✅ | `SDemo.DesktopAgent.Plugin.Translate`（三条入口都可用） |
| M6 双语对照 | ✅ | 宿主追加格式契约、`<s>/<t>` 解析、宽屏两列 / 窄屏上下、对照 ↔ 纯译文纯渲染切换（零请求）、模型不按格式返回时降级并提示 |
| M7 划词 + 简单问答 | ✅ | 消息正文选中文本 → 浮动插件菜单；`selection` 入口与「选中」开关；`SDemo.DesktopAgent.Plugin.QuickAsk` 四入口；`allowHistory` + `contextParameter` 按轮控制上下文；卡片「加入上下文」 |

已实现的接缝：

- `Plugins.Runtime/PluginCatalog.cs`：内置包 + 本地目录两种来源，**吞掉所有解析异常**（坏插件只降级自己），本地覆盖同 `id` 的内置插件，结果缓存并支持 `Scan()` 重新扫描。
- `Plugins.Abstractions/IPluginPackage.cs`：`IPluginPackage` / `IPluginContentSource` / `EmbeddedPluginPackage`；目录源与嵌入资源源共用同一套校验。
- `Plugins.Runtime/PluginManifest.cs`：清单 DTO 与反序列化选项（允许 `//` 注释、尾逗号、大小写不敏感、UTF-8 BOM），未知字段按 JSON 路径记警告。
- `Plugins.Runtime/PluginValidator.cs`：id 格式与唯一性、skill 路径越界（相对路径 + 全路径前缀比较，必须 `.md`）、占位符必须有参数定义、参数 `kind` / `options`、`minHostVersion`、`plugin.json` 512 KB 与 skill 256 KB 上限、图标只收 SVG `path` 数据。
- `Plugins.Abstractions/PluginModels.cs`：`PluginDescriptor` / `Capability` / `Mount` / `Parameter` 描述符，入口组合键 `<pluginId>/<mountId>`；四类 surface：`sidebar` / `composer` / `message` / `selection`。
- `Plugins.Runtime/PluginRegistry.cs`：把扫描结果与用户设置合并成「有效视图」——总开关、插件启用、输入框 / 侧栏 / 气泡 / 选中开关、顺序、显示上限；设置或重新扫描后立即刷新，UI 只从这里取入口。**侧栏依赖输入框**：关掉输入框入口后侧栏入口也隐藏；**划词独立**，不受输入框影响。
- `Contracts/Models/PluginPreferences.cs`：`settings.json` 的 `plugins` 节点（含每个插件的 `enabled` / `inComposer` / `inSidebar` / `inMessage` / `inSelection` / `order`、`maxInSelection` 与隐私确认列表）。旧设置文件没有该节点时用默认值，不写迁移。
- `Plugins.Runtime/PluginParameterResolver.cs`：会话记忆 → `default` → `defaultFrom`（`system-language` / `system-locale`）→ `fallback` → 第一个 option；无效记忆值不会把会话卡死。
- `Plugins.Runtime/PluginPromptComposer.cs`：skill 去 frontmatter、替换 `{{key}}` / `{{key.label}}`；用户输入单独成条并包进 `<待处理文本>`，`<` 全角转义；`output = segments` 的能力由宿主追加 `<s>/<t>` 格式契约。
- `Plugins.Runtime/SegmentedOutputParser.cs`：解析 `<s>原文</s><t>译文</t>`（流式中丢弃未闭合尾标签、数量不等以少为准、多出来的译文并入最后一段）；一条完整配对都没有时降级为纯译文。附带流式累积器 `SegmentedOutputAccumulator`。
- `Core/Plugins/PluginRunner.cs`：单轮、无工具；历史必须同时满足能力 `allowHistory` 与「关联上下文」开关；超 60,000 字符直接报错而不是发出去。
- `Core/Services/AgentChatService.cs`：抽出 `RunAsync(AgentRunPlan)`，普通对话与插件 Run 共用同一条请求组装。
- `Core/Services/ChatStore.cs`：`sessions` 四个插件列 + `messages.PluginJson` / `DerivedJson`；`UpgradeSchemaAsync` **两张表各扫一遍**，老库升级不报 `no such column`；加载时把上次没跑完的 `Running` 卡片收尾成 `Failed`。
- `UI/Components/Chat/ChatComposer.razor` + `PluginParameterControl.razor`：全局 / 插件模式切换、chip 带 `✕`、最多内联 2 个参数（`select` / `text` / `number` / `toggle`；多选未实现）。
- `UI/Components/Sidebar/PluginRail.razor`：侧栏入口区（图标 + 名称，最多 6 个 + 「更多插件」），点击走 `AppShell.StartNewChat(mount)`，与「新建聊天室」同一条路径。
- `UI/Components/Sidebar/Sidebar.razor`：会话图标跟随 `LastPluginId`（插件被删除时回默认图标）。
- `UI/Components/Plugins/`：`PluginIcon`（只渲染 SVG `path`）、`PluginActionButton`（无参数点击即跑 / 有参数展开面板，OK / Cancel / Esc / 点外部）、`PluginActionBar`（最多内联 2 个，其余进「⋯」）、`DerivedCard`（流式文本、复制 / 重试 / 删除、加入 / 移出上下文）、`SelectionActionBar` + `SelectionSnapshot`（划词浮条与选区快照）、`SegmentedOutput`（双语对照宽屏两列 / 窄屏上下，纯译文模式只渲染译文）。
- `UI/wwwroot/js/chat.js`：`readSelection` 读取同一条消息正文内的选区（视口坐标 + `data-message-id`），跨消息 / 空选区返回 null。
- `Context/AgentMessageMapper.cs`：请求上下文由正文、附件、工具轨迹组成；派生卡片默认不参与，只有用户点「加入上下文」且状态为 Done 的卡片才追加。
- `UI/Components/Settings/PluginSettings.razor`：总开关 / 上限 / 参数记忆 / 默认呈现 / 每个插件的启用、入口、顺序，加「选中」开关、划词上限、「打开插件目录」「重新扫描」和失败条目；改动即时生效。
- 隐私：`privacy.sendsText` 的插件首次执行前弹一次确认（取消则不跑），确认结果记在 `plugins.privacyAcknowledged`。

使用方式：侧栏「新建聊天室」下方有「翻译」「总结」「简单问答」入口，点一下就是预选该插件的空会话；输入框底部的插件 chip 选中后发送走插件 Run；任意助手消息底部（时间右侧）的插件图标点一下，无参数直接跑、有参数弹出面板，产物是挂在原消息下的卡片。在聊天正文里选中文本、松开鼠标会出现划词菜单，菜单里的插件由「设置 → 插件 → 选中」控制；划词产物同样挂在选区所在消息下。派生卡片默认不进入下一轮上下文，卡片上的「加入上下文」可以显式提升；「简单问答」的「关联上下文」开关决定这一轮是否带历史。`segments` 能力的卡片与消息都带「对照 / 译文」开关，切换只是重新渲染、不重新请求；呈现方式按会话记忆，新会话默认值在「设置 → 插件」。想关掉或调整上限也在「设置 → 插件」。

**测试**：`dotnet test tests/SDemo.DesktopAgent.Core.Tests`（当前 958 个用例）+ `dotnet test tests/SDemo.DesktopAgent.UI.Tests`（154 个用例）+ `dotnet test tests/SDemo.DesktopAgent.Sandbox.Tests`（Windows 222 通过 / 1 跳过），覆盖上下文、压缩、文件读取 / 搜索 / 补丁、读前写、直跑命令（沙箱与宿主直跑）、构建 / 测试解析、todo / 验证门 / checkpoint / 循环保护、对话 / 任务判定、小模型模式 / 路由 / 工具修复、插件 / 划词 / 上下文、内置技能安装、UI 组件、权限与沙箱作用域、沙箱与路径安全等。

## 主题实现

- **唯一来源**：`ThemeService` 读取 `HKCU\...\Themes\Personalize\AppsUseLightTheme`（0 = 深色）。
- **系统切换通知**：`MainWindow` 挂接 `WM_SETTINGCHANGE`，`lParam` 为 `ImmersiveColorSet` 时调用 `App.RefreshTheme()` 重新探测。
- **WPF 侧**：`ThemeResources.Apply()` 替换 `Application.Resources.MergedDictionaries` 里的 `Themes/Dark.xaml` 或 `Themes/Light.xaml`；标题栏、状态栏全部用 `DynamicResource` 引用画笔，因此立即生效。
- **Blazor 侧**：`AppShell` 订阅 `IThemeService.ThemeChanged`，通过 JS 互操作调用 `window.sdTheme.apply("dark" | "light")`，把主题写到 `<html data-theme="…">`；CSS 变量随之切换。
- **首帧不闪**：`css/app.css` 里深色为默认值，并用 `@media (prefers-color-scheme: light)` 兜底，使 Blazor 接管前也显示正确配色；WebView2 的 `DefaultBackgroundColor` 也按当前主题设置。
- 新增配色时，`Themes/Dark.xaml`、`Themes/Light.xaml` 与 `css/app.css` 中三处变量需保持一致。
