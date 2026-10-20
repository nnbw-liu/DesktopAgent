# 插件系统设计（v1）

> 状态：M0–M6 全部实现（加载器 / 输入框 / 侧栏 / 气泡 / 设置 / 内置翻译 / 双语对照）
> 适用工程：`SDemo.DesktopAgent`（WPF 宿主 + MAUI 宿主，共用 Core / UI）
> 首个插件：翻译（`translate`）
> 相关代码：`src/SDemo.DesktopAgent.Core`、`src/SDemo.DesktopAgent.UI`、`src/SDemo.DesktopAgent.Core/Data/ChatDbContext.cs`

---

## 目录

1. [已确认的决策](#1-已确认的决策)
2. [目标与非目标](#2-目标与非目标)
3. [概念模型](#3-概念模型)
4. [插件包结构](#4-插件包结构)
5. [`plugin.json` 规范](#5-pluginjson-规范)
6. [技能（skill）规范](#6-技能skill规范)
7. [参数（Parameter）规范](#7-参数parameter规范)
8. [界面行为](#8-界面行为)
9. [执行模型](#9-执行模型)
10. [双语对照](#10-双语对照)
11. [状态与持久化](#11-状态与持久化)
12. [设置与管理](#12-设置与管理)
13. [安全与隐私](#13-安全与隐私)
14. [如何制作一个插件](#14-如何制作一个插件)
15. [内置「翻译」插件完整示例](#15-内置翻译插件完整示例)
16. [验收清单](#16-验收清单)
17. [实施计划（M0–M4）](#17-实施计划m0m4)
18. [后续演进](#18-后续演进)

---

## 1. 已确认的决策

| # | 议题 | 结论 |
| --- | --- | --- |
| 1 | 气泡上运行插件的产物 | **追加派生卡片**（derived artifact）：不新增对话轮、不改写原消息、**不进入后续上下文** |
| 2 | 侧栏插件入口新建的会话 | 与「新建聊天室」**同一套逻辑**，只额外预选插件；未选工作区进「历史聊天」，选了工作区进工作区 |
| 3 | 插件模式的生命周期 | **随会话持久**，输入框里的 chip 始终带 `✕`，可随时取消 |
| 4 | 用户自制插件 | **允许**：把 `plugin.json` + skill 文件丢进插件目录即可生效，无需编译 |
| 5 | 译文呈现 | **默认双语对照，可切纯译文**；一次运行、切换零成本（不重新请求模型） |

上一轮方案里未获答复的建议，按原推荐执行，本文已全部吸收：

- 三分状态字段 `OriginMountId` / `ActiveMountId` / `LastPluginId`，不合并。
- 派生结果默认不进入上下文（避免译文污染下一轮推理）。
- 参数按会话记忆，重开聊天室仍记得上次的目标语言。
- 助手气泡显示插件归属标记（`翻译 · SDemo`）。
- 单个插件加载失败只降级自己，不影响其它插件与主流程。
- 图标只接受 SVG `path` 数据，不接受整段 SVG 标记。
- 参数值只进提示词模板，不参与路径、文件名、SQL 拼接。
- `privacy.sendsText = true` 的插件首次使用时提示一次。
- 面板支持 Esc 关闭、点外部关闭、焦点管理。
- 空会话的插件模式必须能一键 `✕` 退出。

---

## 2. 目标与非目标

### 目标

1. **通用**：新增一个插件只写 JSON + Markdown，不改 C#、不改 UI。
2. **多入口**：同一份能力可挂到侧栏、输入框、气泡三个界面，通过配置决定显不显示。
3. **零代码可制作**：翻译、润色、总结、改写、解释代码这类插件，不需要程序集、不需要工具、没有加载风险。
4. **状态可恢复**：会话记住「上次用了谁」「输入框选的是谁」「上次选的语言」。
5. **可降级**：插件写坏、被删除、版本不兼容，只影响它自己。

### 非目标（v1 明确不做）

| 不做 | 原因 |
| --- | --- |
| 程序集插件（native） | 需要 `AssemblyLoadContext`、权限模型、签名；等出现「要读数据库/调私有 API」的插件再上 |
| MCP / 进程外插件 | 协议与生命周期复杂度高，v1 没有收益 |
| UI 插件（往侧栏/设置里塞自定义 Razor 组件） | Blazor Hybrid 下动态加载 RCL 风险大，且绝大多数需求用「描述符 + 通用面板」就能覆盖 |
| 联网安装 / 插件市场 | 先解决本地目录与手工分发 |
| 插件自带工具（function calling） | 与上面两条绑定，v1 的插件只产生文本 |

> **判断标准**：如果一个新需求能用「私有 skill + 参数 + 格式化输出」表达，它就是声明式插件；一旦需要读文件、发请求、碰系统状态，就应该先做成内置工具，而不是急着开放给插件。

---

## 3. 概念模型

```
Plugin（包：目录 + plugin.json）
  ├── Capability（能力：一份 skill + 一组参数 + 输出约定）
  └── Mount（入口：把能力挂到某个界面，并声明输入从哪来）

Run（一次执行） = Mount + 用户输入 + 参数值 → 渲染提示词 → 单轮模型调用 → 产物
```

| 概念 | 回答的问题 | 载体 | 有代码 |
| --- | --- | --- | --- |
| Plugin | 这是什么、谁做的、装在哪 | 目录 + `plugin.json` | 否 |
| Capability | 它能做什么、按什么步骤做 | skill 文件 + 参数 schema | 否 |
| Mount | 在哪儿能用到它、点了之后输入是什么 | `mounts[]` 配置 | 否 |
| Parameter | 用户要选什么、默认值是什么 | `parameters[]` schema | 否 |
| Run | 这一次执行的输入、参数、产物 | 运行时对象 | 是（宿主） |

三条硬规则：

1. **能力定义一次，入口配置多处。** 不要在侧栏、输入框、气泡里各写一套翻译逻辑。
2. **插件只产出文本，不产生副作用。** 有副作用的是「工具」，属于另一套体系（见 §13）。
3. **UI 不认识具体插件。** 组件只消费 `Capability` / `Mount` / `Parameter` 描述符，`translate` 三个字不允许出现在组件代码里。

### 三个会话级状态字段（不要合并）

| 字段 | 含义 | 写入时机 | 用途 |
| --- | --- | --- | --- |
| `OriginMountId` | 这个会话是从哪个入口创建的 | 创建会话时 | 侧栏点「翻译」进来的空会话，默认选中它 |
| `ActiveMountId` | 输入框当前选中的插件 | 用户选/取消时；随会话持久 | 决定这一轮走插件还是普通对话 |
| `LastPluginId` | 最后一次**实际运行**的插件 | 每次 Run 开始；普通推理开始时清空 | 侧栏会话图标 |

举例：从侧栏「翻译」进来 → 三个字段都是 `translate`；用户点 `✕` 取消 → `ActiveMountId = null`，`OriginMountId` 仍保留；此时普通地问一句并发送 → `LastPluginId` 被清空，图标回到默认。三者语义完全不同，合并任意两个都会丢失行为。

---

## 4. 插件包结构

### 目录

```
%APPDATA%\SDemo\DesktopAgent\plugins\        ← 用户插件根目录（Windows）
<AppDataDirectory>/plugins/                  ← Android 同一相对路径
└── translate\
    ├── plugin.json                          ← 必需
    ├── icon.svg                             ← 可选（也可直接写在 plugin.json 里）
    └── skills\
        └── translate.md                     ← 能力引用的技能
```

内置插件（随应用发布）走**完全相同的清单格式**，只是从应用内的只读目录加载，`source = "builtin"`。这样「加载器」这条接缝从第一天就被内置插件验证。

### 加载规则

- 扫描 `plugins/*/plugin.json`，**目录名不要求等于 id**，但建议一致。
- 解析失败的插件不参与运行，在「设置 → 插件」里以红色错误条目展示（只影响自己）。
- 加载顺序：`builtin` 在前，本地在后；同类按 `order` 升序，`order` 相同按名称排序。
- 同一 id 出现两次：后加载的本地插件覆盖内置插件（便于用户改内置插件的行为），并在设置页标注「已覆盖」。
- 扫描时机：应用启动、进入设置页、点「重新扫描」。**不做**文件监视（WebView 宿主下 `FileSystemWatcher` 的线程与重入问题不值得）。

### id 与命名

| 项 | 规则 |
| --- | --- |
| `id` | `^[a-z][a-z0-9-]{1,31}$`，全局唯一 |
| Mount / Capability 的 `id` | `^[a-z][a-z0-9-]{1,31}$`，插件内唯一 |
| 组合键 | `<pluginId>/<mountId>`，用于设置、记忆、状态存储 |
| 显示名 | 允许中文、空格、Emoji，最长 24 字符 |

---

## 5. `plugin.json` 规范

### 5.1 顶层字段

| 字段 | 必填 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- | --- |
| `id` | ✅ | string | — | 见 id 规则 |
| `name` | ✅ | string | — | 显示名（侧栏、chip、按钮 tooltip） |
| `version` | | string | `"1.0.0"` | 语义化版本，展示用 |
| `description` | | string | `""` | 一句话说明，设置页展示 |
| `icon` | | object | 首字母 | `{ "paths": ["M…"], "text": "译" }`，见 §13 |
| `minHostVersion` | | string | — | 高于当前应用版本时禁用并提示 |
| `capabilities` | ✅ | array | — | 至少 1 个 |
| `mounts` | ✅ | array | — | 至少 1 个 |

### 5.2 `capabilities[]`

| 字段 | 必填 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- | --- |
| `id` | ✅ | string | — | 插件内唯一 |
| `name` | ✅ | string | — | 展示名 |
| `skill` | ✅ | string | — | 相对插件目录的 `.md` 路径，**不允许 `..` 与绝对路径** |
| `allowGlobalSkills` | | bool | `false` | 是否把全局技能一并注入（默认关，符合「插件只用自己的 skill」） |
| `allowHistory` | | bool | `false` | 是否带会话历史（默认关，避免上下文污染） |
| `output` | | object | `{ "format": "text" }` | `text` 或 `segments`（见 §10） |
| `privacy` | | object | `{}` | `{ "sendsText": true }` 时首次使用弹提示 |
| `parameters` | | array | `[]` | 见 §7 |

### 5.3 `mounts[]`

| 字段 | 必填 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- | --- |
| `id` | ✅ | string | — | 插件内唯一 |
| `surface` | ✅ | enum | — | `sidebar` / `composer` / `message` |
| `capability` | ✅ | string | — | 必须指向本插件已定义的 capability |
| `input` | | enum | 按 surface 推断 | `composer` / `message` / `selection`（预留） |
| `order` | | int | `100` | 升序，仅同 surface 内比较 |
| `show` | | bool | `true` | 默认是否显示；用户设置可覆盖 |

`input` 的默认值：

- `surface = composer` → `composer`（输入框里的文字）
- `surface = message` → `message`（该条消息的正文）
- `surface = sidebar` → `composer`（先建空会话，等用户输入）

### 5.4 最小可用清单

```json
{
  "id": "polish",
  "name": "润色",
  "version": "1.0.0",
  "capabilities": [
    { "id": "polish", "name": "润色", "skill": "skills/polish.md" }
  ],
  "mounts": [
    { "id": "composer", "surface": "composer", "capability": "polish" }
  ]
}
```

### 5.5 解析细节

- 文件编码：UTF-8（带不带 BOM 都接受）。
- 大小上限：`plugin.json` 512 KB，单个 skill 256 KB，icon 路径总长 4 KB。
- **允许 `//` 注释与尾逗号**（`JsonSerializerOptions { ReadCommentHandling = Skip, AllowTrailingCommas = true }`），配置文件是手写的，不该因为一个逗号全盘失败。
- 字段大小写：`camelCase` 为准，反序列化时大小写不敏感。
- 未知字段：忽略，但记一条警告（便于发现拼写错误）。

### 5.6 校验清单（全部在加载时执行）

| 校验 | 失败行为 |
| --- | --- |
| id 格式与唯一性 | 整个插件置为不可用 |
| `capabilities` / `mounts` 非空 | 整个插件置为不可用 |
| `mount.capability` 能解析到本插件的 capability | 该 mount 不可用，其余继续 |
| skill 路径存在且不越界 | 该 capability 不可用 |
| skill 中占位符 `{{x}}` 在参数中已定义 | 该 capability 不可用（防静默空值） |
| 参数 `kind` 合法、`select` 必须有 `options` | 该 capability 不可用 |
| 同一插件内 mount / capability 的 id 不重复 | 整个插件置为不可用 |
| `minHostVersion` ≤ 当前版本 | 插件禁用，设置页提示 |

---

## 6. 技能（skill）规范

skill 是**插件私有的提示词资产**，只在选中该插件时注入，默认不向全局技能列表暴露。

### 结构

```markdown
---
name: 翻译
description: 把输入文本翻译成指定语言，只输出译文。
version: 1.0
---

（正文：角色、步骤、约束、输出要求）
```

- frontmatter 里的 `name` / `description` / `version` 用于设置页展示；正文才是提示词。
- frontmatter 可以省略，但强烈建议保留。
- 正文中的 `{{key}}` 与 `{{key.label}}` 会在运行时被替换成参数值与显示名。
- **未定义的占位符 = 加载失败**，不允许静默变成空字符串。

### 占位符

| 写法 | 含义 | 例 |
| --- | --- | --- |
| `{{target}}` | 参数原始值 | `ja` |
| `{{target.label}}` | 参数选项的显示名 | `日本語` |

### 输入组织（宿主负责，插件作者不用写）

运行时把用户输入单独放进 user 消息，并用固定定界符包住，降低提示词注入风险：

```text
<待处理文本>
（这里才是用户输入）
</待处理文本>
```

规则：

- **skill 只进 system 消息**，用户文本只进 user 消息，二者永不拼接。
- 输入文本里如果出现 `<s>` / `<t>` / `<待处理文本>` 等定界符号，做全角转义（`<` → `＜`），不影响翻译但能防止伪造分段。
- 插件作者不需要、也不应该在 skill 里写「用户输入会放在哪里」——宿主已经约定好了。

### 写作建议

1. 先写「你是谁」，再写「按什么步骤」，最后写「禁止什么」。
2. 输出格式写死（「只输出译文，不要解释」），比事后裁剪便宜得多。
3. 不要要求模型「先思考再输出」，思维链会混进正文；需要推理的任务放气泡里跑，别放输入框。
4. 长度控制在 4 KB 以内，越长的指令越容易被稀释。

---

## 7. 参数（Parameter）规范

同一份参数 schema 被三处消费：**输入框内联控件**、**气泡展开面板**、**设置页的插件默认值**。这是整个设计里复用率最高的部分。

### 字段

| 字段 | 必填 | 类型 | 说明 |
| --- | --- | --- | --- |
| `key` | ✅ | string | `^[a-z][a-zA-Z0-9_]*$`，模板里用 `{{key}}` |
| `label` | ✅ | string | 控件标签 |
| `kind` | ✅ | enum | `select` / `multiselect` / `toggle` / `text` / `number` |
| `options` | `select`/`multiselect` 必填 | array | `[{ "value": "ja", "label": "日本語" }]` |
| `default` | | string | 字面默认值 |
| `defaultFrom` | | string | 动态默认值，目前支持 `system-language` / `system-locale` |
| `fallback` | | string | 动态默认值解析不出来时的兜底 |
| `required` | | bool | 默认 `true` |
| `placeholder` | | string | `text` / `number` 的水印 |
| `maxLength` | | int | `text` 的最大长度，默认 2000 |

### 默认值解析顺序

```
会话记忆值 → default → defaultFrom 解析结果 → fallback → 第一个 option
```

`system-language` 的映射：

| 系统区域 | 映射到 | 常见取值 |
| --- | --- | --- |
| `zh-CN` / `zh-SG` / `zh-Hans-*` | `zh-Hans` | 简体中文 |
| `zh-TW` / `zh-HK` / `zh-MO` / `zh-Hant-*` | `zh-Hant` | 繁體中文 |
| `ja*` | `ja` | 日本語 |
| `fr*` | `fr` | Français |
| `ko*` | `ko` | 한국어 |
| `en*` 及其它 | `en` | English |

解析用 `CultureInfo.CurrentUICulture`，桌面与 Android 行为一致。切换系统语言的会话不重算——已保存的记忆值优先。

### 记忆

- 每个会话每个插件每个参数记忆一个值：`PluginMemory["translate/translate/target"] = "ja"`。
- 键的格式是 `<pluginId>/<mountId>/<key>`；`ui.` 前缀保留给宿主（例如 `ui.mode` 记对照/纯译文），插件参数不要以 `ui.` 开头。
- 记忆对象是**会话**，不是全局：A 会话在译日文，B 会话仍按系统语言。
- 设置里可以关闭 `rememberParameterValues`，关闭后只用 manifest 默认值。

### 校验

- `select` 的 `default` / 记忆值如果不在 `options` 里 → 回落到第一个 option，不报错（用户可能刚改过插件）。
- `number` 只接受 `-?\d+(\.\d+)?`，超范围回落到 `default`。
- `required = true` 时不允许空值；空值直接禁用「执行」按钮，而不是发一个缺参数的请求。

---

## 8. 界面行为

### 8.1 侧栏入口（surface = sidebar）

位置：侧栏 Header 的「新建聊天室」按钮下方、与「工作区」区块之间的分割线上方。

```text
┌─────────────────────────┐
│  D  SDemo DesktopAgent  │
│  ＋ 新建聊天室            │
│  ─────────────────────  │  ← 分割线
│  [译] 翻译               │  ← 插件入口区
│  [✎] 润色                │
│  ─────────────────────  │
│  工作区                  │
│    …                     │
└─────────────────────────┘
```

- 条目 = 图标（16 px）+ 名称，样式复用现有 `.sidebar-item`。
- 数量上限 `maxInSidebar`（默认 6），超出进「更多插件」菜单。
- 点击 → 与「新建聊天室」走**同一条路径**（`AppShell.StartNewChat`），只多做两件事：`OriginMountId = ActiveMountId = <mount>`，并写入空会话。
- 之后是否落库、落到历史还是工作区，完全沿用现有规则（[首次发送消息才落库](../README.md)）。
- 分割线用 `.plugin-rail` 的上下 `border` 实现，不新增 DOM 层级。

### 8.2 输入框（surface = composer）

输入框左侧功能区有两种模式，互斥：

```text
全局模式：
  [＋上传]  [翻译] [润色] [总结] [更多 ▾]        [🔍] [📁] [模型] [发送]

插件模式（无参数）：
  [＋上传]  [译 翻译 ✕]                          [🔍] [📁] [模型] [发送]

插件模式（有参数）：
  [＋上传]  [译 翻译 ✕]  [目标语言 日本語 ▾]     [🔍] [📁] [模型] [发送]
```

规则：

1. **选中后只保留一个 chip**，全局技能区整块隐藏（`allowGlobalSkills = false` 时也不注入）。
2. chip 上永远有 `✕`；点击回全局模式，同时把 `ActiveMountId` 落库为 `null`。
3. 参数 ≤ 2 个时全部内联；> 2 个时只内联前 2 个，其余收进「设置…」浮层。
4. 参数为 `select` 且选项 ≤ 8 个时直接用下拉；更多时用可搜索浮层（v1 未实现则仍用下拉）。
5. 显示数量：`maxInComposer`（默认 3）与技能列表一致，超出进「更多」。
6. 插件模式下点发送 → **走插件 Run**，不是普通对话（见 §9）。
7. 生成中禁止切换插件（按钮 `disabled`），避免半途换 prompt。
8. 从侧栏进来的空会话取消选中后，就是一个普通空会话，不再有任何「翻译」痕迹——除了 `OriginMountId` 这个内部字段。

### 8.3 气泡按钮（surface = message）

位置：助手消息底部元信息行（`时间 · tokens · 工具 · 耗时 · 复制 · 分支`），插件按钮排在复制之前，用插件自己的图标。

```text
SDemo  这条回答的正文……
        14:32 · 1,203 tokens · 0.9s  [译] [复制] [分支]

点击后（有参数）：
        14:32 · 1,203 tokens · 0.9s  [译 目标语言 日本語 ▾  OK  Cancel]
```

状态机：

```text
idle ──click──▶ 有参数?
                 ├── 否 ──▶ running ──▶ done / failed / canceled
                 └── 是 ──▶ panel ──┬── OK     ──▶ running
                                    ├── Cancel ──▶ idle
                                    └── Esc / 点外部 ──▶ idle
```

- **无参数直接跑**，不弹任何面板，符合「点击即执行」。
- **有参数才展开**：按钮所在行原地拉长，控件出现在右侧同一行；面板用 `position: relative` 的容器，不遮挡气泡正文。
- 窄屏（≤ 760 px）：面板改为气泡下方整行展开，控件纵向排列。
- 运行中：按钮禁用并显示旋转指示；同一会话同一时间只允许一个 Run（单飞）。
- 一条消息有多个插件按钮时：只渲染**当前启用插件**的按钮（最多 2 个），其余进「⋯」菜单。
- 产物：在原气泡下方追加派生卡片（见 §10）。

### 8.4 会话图标与消息归属

会话图标：

| 状态 | 图标 |
| --- | --- |
| 从未用过插件 | 默认聊天图标 |
| 最后一次 Run 来自插件 | 该插件图标 |
| 普通推理开始 | 立即回到默认图标 |
| 插件已被删除 / 解析失败 | 回默认图标，不显示空洞 |

助手消息的归属标记：角色名从 `SDemo` 变为 `翻译 · SDemo`，并在前面加 12 px 插件图标。这解决「同一会话里既有普通回答又有翻译回答」时的辨识问题。

### 8.5 边缘态

| 情况 | 行为 |
| --- | --- |
| 未配置任何模型 | 点插件直接提示「去设置 → 模型」，不创建 Run |
| 插件在运行中被删除 | 当前 Run 继续（内容已在内存），下次列表刷新时消失 |
| 会话记忆里的语言已被插件移除 | 回落到第一个 option |
| 面板打开时切换会话 | 面板关闭，状态丢弃 |
| 生成中切换会话 | 沿用现有逻辑：取消上一轮 |

---

## 9. 执行模型

### 9.1 两种 Run，两种产物

| 触发 | 产物 | 是否进历史 | 是否进上下文 | 说明 |
| --- | --- | --- | --- | --- |
| 输入框选中插件后发送 | **一条助手消息**（带 `PluginId`） | 是 | 是（它就是这一轮回答） | 下一轮普通对话会看到它 |
| 气泡按钮 | **派生卡片**（挂在原消息下） | 是（持久化） | **否** | 只是对既有内容的加工 |

> 为什么不把气泡运行也做成「一条新消息」：译文会污染后续推理、历史被切碎、用户分不清原文与译文。

### 9.2 流水线

```text
RunRequest
  ├─ 0. 前置校验：插件可用 / mount 可用 / 参数合法 / 模型已配置
  ├─ 1. 参数取值：记忆 → default → defaultFrom → fallback
  ├─ 2. 组装 AgentRunPlan
  │      system = 插件 skill（占位符已替换）+ [optional] 全局技能
  │      user   = 定界包裹后的输入文本
  │      history= allowHistory ? 会话历史 : 空
  │      tools  = 空（v1 声明式插件不支持工具）
  ├─ 3. 长文本分段（> 4000 字按段落切块，顺序执行）
  ├─ 4. 流式执行 → 解析输出（text / segments）
  ├─ 5. 收尾：写产物、写 LastPluginId、写指标
  └─ 6. 失败/取消：保留已生成部分，标状态，可重试
```

### 9.3 上下文预算

| 项 | 上限 |
| --- | --- |
| 单次输入文本 | 60,000 字符（超出分段，见下） |
| 单段输入 | 4,000 字符 |
| 输出 | 不主动截断，但超过 32,000 字符时提示「结果过长，已折叠」 |
| `allowHistory = true` 时的历史 | 最近 10 条，且总字符 ≤ 24,000 |

### 9.4 长文本分段

- 按空行切段，拼到 ≤ 4,000 字符为一块；单段本身超长时按句号/换行硬切。
- 顺序执行，UI 显示「正在翻译 2/5 段…」。
- 每块的 segments 依次拼接；text 模式直接拼接译文。
- 任一块失败：保留已完成部分，卡片标记「部分失败 · 重试剩余」。

### 9.5 取消与重试

- 取消通过 `CancellationToken` 透传，行为与现有生成一致（`OperationCanceledException`）。
- 已生成内容保留，状态置 `Canceled`。
- 「重试」沿用同一 mount + 参数 + 输入；「重试剩余」只跑失败的块。

### 9.6 模型

- 使用当前会话选中的 provider + model；没有则报错引导去设置。
- 插件不指定模型，也不写死温度等参数（v1）。后续如需，加 `runtime: { temperature, maxTokens }`。

---

## 10. 双语对照

### 10.1 一次运行，两种呈现

要求是「双语对照 + 纯译文，可切换」。如果切换时重新请求模型，既慢又费钱，所以**让模型一次输出带结构的结果，UI 负责切换**。

能力声明：

```json
"output": { "format": "segments" }
```

宿主自动在 system 提示词末尾追加**格式契约**（插件作者不用写、也不允许改）：

```text
输出格式（必须严格遵守）：
把内容按段落逐段处理，每一段输出一对标签，先原文后译文：
<s>原文段落</s>
<t>译文段落</t>
逐段重复。标签外不要输出任何文字，不要使用代码块包裹这些标签。
```

### 10.2 解析规则

1. 扫描 `<s>…</s>` 与 `<t>…</t>`，按出现顺序配成 `(source, target)`。
2. 尾部未闭合的标签在流式中丢弃，并显示「翻译中…」。
3. `<s>` 与 `<t>` 数量不等：以较少的为准；多出来的 `<t>` 并入最后一段译文。
4. 一条完整的配对都没有：**降级为纯文本**，整段输出当作译文，「对照」开关禁用并提示「该结果不支持对照」。

### 10.3 呈现

宽屏（> 760 px）对照模式：

```text
┌ 译文 · 日本語            [对照 | 译文]  [复制] [重试] ┐
│ ┌───────────────────────┬───────────────────────┐ │
│ │ The quick brown fox…  │ 素早い茶色の狐が…      │ │
│ ├───────────────────────┼───────────────────────┤ │
│ │ It jumps over…        │ それは飛び越える…      │ │
│ └───────────────────────┴───────────────────────┘ │
└──────────────────────────── 3.4s · 1,208 tokens ─────┘
```

窄屏：按段落上下排列，原文用弱化色，译文用正常色。

纯译文模式：只渲染 `<t>` 的内容，段落间距与普通消息一致。

### 10.4 切换与记忆

- 切换是**纯渲染**，不触发任何请求。
- 切换状态存 `PluginMemory["translate/translate/ui.mode"]`，同一会话保持；设置里可选择「新会话默认模式」。
- 默认：**对照**（决策 5）。
- 输入框模式的 Run 同样产出 segments，助手消息底部也会出现「对照 / 译文」开关（因为它是一条普通消息，段落结构存在 `PluginJson` 里）。

### 10.5 降级路径

| 情况 | 处理 |
| --- | --- |
| 模型不按格式输出 | 整段当译文，禁用对照，卡片底部提示「本次结果未按段落返回」 |
| 输出里混入解释性文字 | 标签外的文本忽略；若整条都没有标签，同上 |
| 分段翻译（多块） | 每块的 segments 依次拼接，块与块之间插入空段落 |

---

## 11. 状态与持久化

### 11.1 领域模型

```csharp
public sealed class ChatSession
{
    // 现有字段…
    public string? OriginMountId { get; set; }   // "translate/sidebar"
    public string? ActiveMountId { get; set; }   // "translate/composer"
    public string? LastPluginId { get; set; }    // "translate"
    public Dictionary<string, string> PluginMemory { get; init; } = new();
}

public sealed class ChatMessage
{
    // 现有字段…
    public PluginMessageInfo? Plugin { get; set; }      // 本轮由谁生成
    public List<DerivedArtifact> Derived { get; init; } = [];  // 派生卡片
}

public sealed record PluginMessageInfo(
    string PluginId, string MountId, string CapabilityId,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<OutputSegment>? Segments);

public sealed record DerivedArtifact(
    string Id, string PluginId, string MountId, string CapabilityId,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<OutputSegment>? Segments,
    string Text,                 // 纯译文；无 segments 时即全文
    DerivedState State,          // Running / Done / Canceled / Failed
    string? Error,
    string ModelId, long DurationMs, int InputTokens, int OutputTokens,
    DateTime CreatedAt);

public sealed record OutputSegment(string Source, string Target);
```

### 11.2 数据库

`sessions` 表新增：

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `OriginMountId` | TEXT NULL | |
| `ActiveMountId` | TEXT NULL | |
| `LastPluginId` | TEXT NULL | |
| `PluginMemoryJson` | TEXT NULL | `{ "translate/translate/target": "ja" }` |

`messages` 表新增：

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `PluginJson` | TEXT NULL | `PluginMessageInfo` |
| `DerivedJson` | TEXT NULL | `DerivedArtifact[]` |

⚠️ **迁移有两个坑**：

1. 现有 `ChatStore.UpgradeSchemaAsync` 只扫 `messages` 表，`sessions` 的新列不会被补，必须改成「两张表各扫一遍、各自维护列清单」。
2. 记录 ↔ 领域模型的映射有**两处**（`ToRecord` / `ToModel`），新增字段要一起改，否则会出现「界面有、重启没了」。

派生卡片存的是**文本快照**，不是对插件或 skill 的引用——插件卸载后历史仍可读。

### 11.3 设置

```json
"plugins": {
  "enabled": true,
  "maxInComposer": 3,
  "maxInSidebar": 6,
  "rememberParameterValues": true,
  "defaultOutputMode": "bilingual",
  "entries": [
    { "id": "translate", "enabled": true, "inSidebar": true, "inComposer": true, "inMessage": true, "order": 10 }
  ]
}
```

`entries` 只在用户改过设置时写入；插件默认值来自 manifest 的 `show` / `order`。插件被删除后条目保留（下次装回来仍然生效），但不在界面上展示。

> 入口开关的依赖关系：**侧栏依赖输入框**。侧栏入口只是「新建一个预选该插件的会话」，
> 输入框入口关闭后进去也看不到插件，所以 `inComposer = false` 时 `inSidebar` 一律视为关闭
> （设置页把侧栏那一项灰掉并给原因）。`inMessage` 是气泡按钮的独立开关，与输入框无关。

---

## 12. 设置与管理

新增「插件」设置分区（`SettingsSection.Plugins`），与「通用 / 模型 / 工具」并列。

```text
设置 › 插件

  [重新扫描]  [打开插件目录]

  内置
  ┌───────────────────────────────────────────────┐
  │ [译] 翻译          1.0.0   内置                │
  │      把文本翻译成指定语言                       │
  │      侧栏 [✓]   输入框 [✓]   顺序 [10]  [启用] │
  └───────────────────────────────────────────────┘

  本地
  ┌───────────────────────────────────────────────┐
  │ [!] 我的插件        —     加载失败              │
  │     plugin.json 第 12 行：mounts[0].capability  │
  │     指向不存在的 capability "translate"         │
  └───────────────────────────────────────────────┘

  输入框最多显示 [3] 个 · 侧栏最多显示 [6] 个
  [✓] 记住每个会话上次选择的参数
  新会话译文默认显示  ( ) 对照  ( ) 纯译文
```

要点：

- 失败条目显示**具体原因与位置**，不是一句「加载失败」。
- 「打开插件目录」用 `Process.Start` 打开资源管理器（Android 不提供，按钮隐藏）。
- 修改开关立即生效（下次渲染即刷新），不需要重启。
- 用户自制的插件标注来源「本地」，并提示：插件内容会进入系统提示词，只安装你信任的插件。

---

## 13. 安全与隐私

| 面 | 风险 | 措施 |
| --- | --- | --- |
| 图标 | SVG 里塞 `<script>` / `onload` / `<foreignObject>` | **只接受 `paths` 字符串数组**，渲染时 `<path d="…">`；`d` 只允许 `[MmLlHhVvCcSsQqTtAaZz0-9.,\-\s]`，最多 8 条、总长 4 KB |
| skill 路径 | `../../..` 读到系统文件 | `Path.GetFullPath` 后必须以插件目录为前缀，且扩展名必须是 `.md` |
| 参数值 | 被拼进路径 / 文件名 / 命令 | 参数只做模板替换；v1 的声明式插件没有任何文件或进程操作 |
| 提示词注入 | 用户文本伪装成指令 | skill 只进 system，输入只进 user 并定界；输入中的定界符做全角转义 |
| 插件文本 | 第三方插件 = 提示词投毒面 | v1 只支持本地目录安装，设置页明确提示风险；后续如需分发再加签名 |
| 隐私 | 文本会发给模型服务商 | `privacy.sendsText = true` 时首次使用弹一次提示，可在设置里关闭 |
| 资源 | 超大文件拖垮启动 | 清单 512 KB / skill 256 KB / icon 4 KB 上限，超限即拒绝并提示 |
| 稳定性 | 一个坏插件拖垮列表 | 逐插件、逐 capability 隔离解析，失败只降级自己 |

明确不做的：沙箱、权限声明、签名校验（都是 native 插件才需要的东西）。

---

## 14. 如何制作一个插件

> 目标：不写一行 C#，10 分钟内做出一个可用插件。
> **面向作者的完整手册（含报错对照表与模板）见 [plugin-authoring.md](plugin-authoring.md)**；本节保留设计视角的最短路径。

### 14.1 准备

1. 启动应用 →「设置 → 插件」→ 点「打开插件目录」。
2. 目录形如 `%APPDATA%\SDemo\DesktopAgent\plugins\`。
3. 新建一个文件夹，名字随意（建议和插件 id 一致），例如 `polish`。

### 14.2 写清单

`plugins/polish/plugin.json`：

```json
{
  "id": "polish",
  "name": "润色",
  "version": "1.0.0",
  "description": "把文字改得更通顺，不改变原意。",
  "capabilities": [
    {
      "id": "polish",
      "name": "润色",
      "skill": "skills/polish.md"
    }
  ],
  "mounts": [
    { "id": "composer", "surface": "composer", "capability": "polish" },
    { "id": "bubble", "surface": "message", "capability": "polish", "input": "message" }
  ]
}
```

这段配置的效果：输入框底部出现一个「润色」chip；每条助手消息底部出现一个润色按钮；两者都调同一个能力。

### 14.3 写技能

`plugins/polish/skills/polish.md`：

```markdown
---
name: 润色
description: 把文字改得更通顺，不改变原意。
version: 1.0
---

你是一位中文编辑。请润色用户提供的文本。

规则：
- 不改变原意，不增删事实。
- 修正语病、口语化表达与重复用词。
- 保持原有的段落结构与 Markdown 格式。
- 只输出润色后的全文，不要解释改了什么。
```

### 14.4 让它生效

「设置 → 插件」→「重新扫描」→ 列表里出现「润色 · 本地」。

测试两条路径：

- 输入框里选中「润色」→ 输入一段话 → 发送。
- 随便找一条助手回答 → 点底部的润色按钮。

### 14.5 加参数

以「语气」为例，在 capability 里加 `parameters`：

```json
"parameters": [
  {
    "key": "tone",
    "label": "语气",
    "kind": "select",
    "default": "neutral",
    "options": [
      { "value": "neutral", "label": "中性" },
      { "value": "formal",  "label": "正式" },
      { "value": "casual",  "label": "口语" }
    ]
  }
]
```

然后在 skill 正文里使用：

```markdown
把语气调整为「{{tone.label}}」（代码：{{tone}}）。
```

保存 → 重新扫描 → 输入框的 chip 右侧出现下拉框，气泡上的按钮点击后会展开面板。

### 14.6 双语对照

在 capability 上加一行：

```json
"output": { "format": "segments" }
```

宿主会自动要求模型分段输出原文 + 译文，卡片上出现「对照 / 译文」开关。你**不需要**在 skill 里写任何格式说明。

### 14.7 调试

| 现象 | 排查 |
| --- | --- |
| 列表里没有 | 目录层级错了（必须是 `plugins/<文件夹>/plugin.json`）；或 JSON 有语法错误，看设置页的错误行号 |
| 显示「加载失败」 | 按提示改；常见是 `mounts[0].capability` 写错、skill 路径写错 |
| chip 出现了但点了没反应 | 没配置模型；或参数必填但没给默认值 |
| 输出格式乱 | skill 里要求了「先分析再输出」，把分析部分删掉 |
| 改了 skill 不生效 | 点「重新扫描」；已打开的会话需要重新选一次插件 |

### 14.8 可复制的模板

```text
<插件目录>/
├── plugin.json
└── skills/
    └── main.md
```

```json
{
  "id": "my-plugin",
  "name": "我的插件",
  "version": "0.1.0",
  "description": "一句话说明。",
  "capabilities": [
    { "id": "main", "name": "我的插件", "skill": "skills/main.md" }
  ],
  "mounts": [
    { "id": "composer", "surface": "composer", "capability": "main" }
  ]
}
```

```markdown
---
name: 我的插件
description: 一句话说明。
version: 0.1.0
---

你是……。请按以下步骤处理用户提供的文本：

1. ……
2. ……

约束：
- 只输出结果，不要解释过程。
```

---

## 15. 内置「翻译」插件完整示例

### 15.1 `plugin.json`

```json
{
  "id": "translate",
  "name": "翻译",
  "version": "1.0.0",
  "description": "把文本翻译成指定语言，支持双语对照。",
  "icon": {
    "paths": [
      "M3.5 5h6M6.5 3.2v1.8M9.5 5c0 3.6-2.2 6-5 7.4M5.8 8.2c1 2.1 2.7 3.7 4.7 4.6",
      "M12.6 9.4 15.8 16.6M15.8 16.6h-6.4"
    ]
  },
  "capabilities": [
    {
      "id": "translate",
      "name": "翻译",
      "skill": "skills/translate.md",
      "allowGlobalSkills": false,
      "allowHistory": false,
      "privacy": { "sendsText": true },
      "output": { "format": "segments" },
      "parameters": [
        {
          "key": "target",
          "label": "目标语言",
          "kind": "select",
          "defaultFrom": "system-language",
          "fallback": "en",
          "options": [
            { "value": "zh-Hans", "label": "简体中文" },
            { "value": "zh-Hant", "label": "繁體中文" },
            { "value": "ja",      "label": "日本語" },
            { "value": "fr",      "label": "Français" },
            { "value": "en",      "label": "English" },
            { "value": "ko",      "label": "한국어" }
          ]
        }
      ]
    }
  ],
  "mounts": [
    { "id": "sidebar",  "surface": "sidebar",  "capability": "translate", "order": 10 },
    { "id": "composer", "surface": "composer", "capability": "translate", "order": 10 },
    { "id": "bubble",   "surface": "message",  "capability": "translate", "input": "message", "order": 10 }
  ]
}
```

### 15.2 `skills/translate.md`

```markdown
---
name: 翻译
description: 把输入文本翻译成指定语言。
version: 1.0
---

你是一位专业翻译。把用户提供的文本翻译成 {{target.label}}（{{target}}）。

规则：
- 准确、通顺、符合目标语言的表达习惯。
- 保留原文的段落、列表、引用与 Markdown 结构。
- 代码块内的代码、命令、标识符不翻译；只翻译代码块外的说明文字。
- 专有名词首次出现时用「译名（原文）」的形式。
- 如果原文已经是 {{target.label}}，原样返回。
- 不要添加原文没有的解释、前言或后记。
```

### 15.3 三条入口的行为

| 入口 | 操作 | 结果 |
| --- | --- | --- |
| 侧栏 | 点「翻译」 | 新建空会话，输入框已选中翻译 + 语言下拉（系统语言）；选不选工作区都按「新建聊天室」的规则 |
| 输入框 | 选中翻译 → 输入 → 发送 | 生成一条助手消息，角色显示「翻译 · SDemo」；会话图标变翻译；消息底部有「对照 / 译文」开关 |
| 气泡 | 点翻译图标 → 选语言 → OK | 原消息下出现派生卡片；不新增对话轮；后续对话看不到这张卡片 |

### 15.4 一次完整交互（时序）

```text
用户：侧栏点「翻译」
宿主：StartNewChat(origin=translate/sidebar, active=translate/composer)
UI  ：输入框 [译 翻译 ✕] [目标语言 简体中文 ▾]

用户：粘贴一段英文，发送
宿主：① 用户消息入库
      ② 组装 plan：skill(translate) + target=zh-Hans + 输入文本
      ③ 流式返回 <s>…</s><t>…</t>…
      ④ 助手消息入库（PluginJson 含 segments），LastPluginId=translate
      ⑤ 侧栏图标 → 翻译
UI  ：消息体显示双语对照（默认），可切纯译文

用户：在另一条旧回答上点翻译 → 日本語 → OK
宿主：派生 Run（input=该消息正文）
      卡片状态 Running → Done（segments 3 段）
      LastPluginId=translate（不变）
      会话记忆记住 target=ja
UI  ：卡片可切对照/纯译文，可复制、重试、删除
```

### 15.5 翻译插件的边界处理

| 场景 | 行为 |
| --- | --- |
| 原文已是目标语言 | skill 规则原样返回；不额外判断 |
| 超长文本 | 按段落切块，逐块翻译，卡片显示进度 |
| 中英混排、代码块 | 保留代码块，规则已写在 skill 里 |
| 用户中途取消 | 保留已译部分，标「已取消」，可重试 |
| 系统语言是 `de-DE` | 不在选项里 → 回落 `fallback: en` |
| 目标语言选项后续被删 | 记忆值失效，回落到第一个选项 |

---

## 16. 验收清单

### 功能

- [ ] 侧栏出现插件入口区，位于「新建聊天室」与「工作区」之间，有明确分割线。
- [ ] 点侧栏「翻译」→ 新建空会话，输入框预选翻译，语言 = 系统语言。
- [ ] 该会话不选工作区时进入「历史聊天」，选了工作区时进入工作区（与新建聊天室一致）。
- [ ] 输入框插件列表最多显示 `maxInComposer` 个，超出进「更多」。
- [ ] 选中插件后只剩一个 chip，带 `✕`；点 `✕` 回到全局模式。
- [ ] 插件模式发送后，助手消息显示插件归属（`翻译 · SDemo`）。
- [ ] 会话图标在插件 Run 后变为插件图标，在普通推理后恢复默认。
- [ ] 气泡按钮无参数时点击即执行；有参数时展开面板，OK / Cancel 生效。
- [ ] 派生卡片不新增对话轮，且不出现在下一轮的请求上下文里（可用抓包或日志验证）。
- [ ] 对照 / 纯译文切换不产生新的模型请求。

### 边界

- [ ] 一个坏插件不影响其它插件加载。
- [ ] 插件被删除后，历史派生卡片仍可读，图标回默认。
- [ ] 未配置模型时点插件有明确引导。
- [ ] 取消生成后保留部分结果，可重试。
- [ ] 长文本（> 4000 字）分块执行，有进度。
- [ ] 模型不按分段格式输出时，降级为纯译文并禁用对照开关。

### 数据

- [ ] 重启后：会话图标、输入框选中的插件、目标语言都还在。
- [ ] 老库升级后不报 `no such column`（`sessions` 与 `messages` 都要补列）。
- [ ] 插件目录为空时，界面与现在完全一致（无插件不显示空区域）。

### 交互

- [ ] 面板 Esc 关闭、点外部关闭、焦点回到触发按钮。
- [ ] 窄屏（≤ 760 px）下面板纵向排列，不横向溢出。
- [ ] 生成中禁止切换插件。

---

## 17. 实施计划（M0–M4）

### 17.1 总览

| 阶段 | 目标 | 交付物 | 依赖 | 估时 |
| --- | --- | --- | --- | --- |
| **M0 骨架** | 能把插件列出来，且报错精准 | 清单模型 / Catalog / 校验器 / 参数 schema | — | 1–2 天 |
| **M1 输入框** | 插件能真正跑通一轮对话 | composer mount / 运行器 / 会话状态落库 | M0 | 2–3 天 |
| **M2 侧栏** | 有入口，能带着插件新建会话 | 入口区 / `OriginMountId` | M1 | 1–2 天 |
| **M3 气泡** | 能对既有回答做加工 | 派生卡片 / `LastPluginId` / 会话图标 | M1 | 3–4 天 |
| **M4 设置** | 可管、可关、可排错 | 插件管理页 / 上限 / 重新扫描 | M0 | 1–2 天 |
| M5 内置翻译 | 用真实插件跑通三条入口 | `plugin.json` + skill + 图标 | M1–M3 | 0.5 天 |
| M6 双语对照 | segments 输出与切换 | 解析器 / 渲染 / 记忆 | M3 | 2 天 |

依赖关系：

```text
M0 ──┬──▶ M1 ──┬──▶ M2
     │         └──▶ M3
     └──▶ M4
```

- **M0 是唯一硬前置**：没有清单解析，后面全是空转。
- **M1 之后 M2 / M3 / M4 可并行**，三者改的文件基本不重叠（Sidebar / ChatRoom + 消息模型 / 设置页）。
- **M5 是验收用例，不是开发阶段**：它检验 M1–M3 的接缝是否真的通用。
- 每阶段一个分支、一个 PR，独立可回滚；M0 / M1 完成即可关掉特性开关，行为退回现状。

**每个阶段的 Definition of Done**：

1. 不破坏现有能力（全局技能、工具开关、模型设置、分支、复制）。
2. §16 中对应验收项全部通过。
3. 错误提示是中文、可定位（文件 + 位置），不是裸异常。
4. 关掉 `plugins.enabled` 后，界面与现在完全一致。

---

### 17.2 M0 骨架

**目标**：扫描 `plugins/`，解析清单并校验，把「可用 / 不可用 + 原因」交给上层。

**做完能演示**：手写一个插件文件夹 → 重新进入设置就能看到它；故意写错的地方能看到具体原因。

**任务**

- [x] `IAppPaths.PluginsRoot`：WPF / MAUI-Windows 落在 `%APPDATA%\SDemo\DesktopAgent\plugins`，Android 落在 `AppDataDirectory/plugins`
- [x] `PluginManifest` 模型 + 反序列化选项（允许 `//` 注释、尾逗号、大小写不敏感）
- [x] `PluginCatalog.Scan()`：内置目录 + 本地目录，排序、去重、本地覆盖内置
- [x] `PluginValidator`：id 格式、skill 路径越界、占位符、参数 `kind`、`minHostVersion`、文件体积
- [x] 占位符提取与校验（`{{key}}` / `{{key.label}}`）
- [x] `PluginLoadResult`：可用列表 + 失败列表（含所属文件与位置）
- [x] 单元测试：合法最小清单、缺 skill、路径穿越、未定义占位符、重复 id、超体积
- [x] 临时可见性：设置页先渲染一个只读列表（正式 UI 留给 M4）

**关键实现点**

- 所有解析异常在 Catalog 内捕获，**绝不让扫描抛到 UI 线程**——一个坏插件不能白屏。
- 路径校验用 `Path.GetFullPath` + 前缀比较（Windows 大小写不敏感），扩展名必须是 `.md`。
- 未知字段只记警告、不阻断，既方便发现拼写错误，又不至于让插件因为多写一个字段就废掉。

**验收**

- 删除 `skills/translate.md` → 该 capability 标红，同插件的其它 capability 仍可用。
- `plugin.json` 少一个引号 → 只有该插件失败，其余正常。
- `"skill": "../../secret.md"` → 拒绝并提示越界。
- `"skill": "skills/x.md"` 里写了 `{{nope}}` → 提示未定义占位符。

**风险**：清单格式后续要演进 → 反序列化层带 `schemaVersion`（缺省 1），领域模型与 DTO 分离。

---

### 17.3 M1 输入框

**目标**：输入框选中插件后发送，走插件 Run，产出带归属的助手消息，且状态跨会话保留。

**做完能演示**：选「翻译」→ 粘贴英文 → 发送 → 得到译文；切走再切回，插件与目标语言还在。

**任务**

- [x] 从 `AgentChatService` 抽出 `RunAsync(plan)`：instructions / messages / tools / metrics
- [x] `PluginPromptComposer`：skill 渲染、占位符替换、输入定界包裹
- [x] `PluginParameterResolver`：记忆 → `default` → `defaultFrom` → `fallback`，含系统语言映射
- [x] `PluginRunner.RunComposerAsync`：单轮、无历史、无全局技能（按 capability 开关）
- [x] `ChatComposer`：插件区、全局/插件模式切换、`✕`、参数内联（先只做 `select`）
- [x] `ChatRoom`：`_activeMountId`、切会话恢复、发送分叉、写 `PluginMessageInfo`
- [x] 持久化：`sessions` 四个新列 + `UpgradeSchemaAsync` 扩展到 sessions + 两处映射
- [x] 会话记忆写回（参数值、`ui.mode`）

**关键实现点**

- 插件模式**不注入全局技能**、不带历史（除非 capability 显式打开）。
- 生成中禁止切换插件，避免半途换 prompt。
- 现在 `ChatRoom.OnParametersSet` 切会话时会把技能/工具清空（见 `ChatRoom.razor` 的 `_sessionId` 分支），这里必须改成「按会话恢复」，否则插件状态一换会话就没。

**验收**

- 助手消息角色显示「翻译 · SDemo」，`PluginJson` 有值且含参数快照。
- 切到别的会话再回来，插件与语言仍是原值。
- 点 `✕` 后发送的是普通对话，技能区恢复显示。
- 未配置模型时点插件有明确提示，不产生空 Run。

**风险**：老库缺列会启动报错 → **M1 的第一个提交先改 `UpgradeSchemaAsync`（sessions 也要扫）**，再动 UI。

---

### 17.4 M2 侧栏

**目标**：侧栏出现插件入口，点击即新建一个预选该插件的会话。

**做完能演示**：点「翻译」→ 新会话，输入框已选中翻译；不选工作区发消息进「历史聊天」，选了工作区进工作区。

**任务**

- [x] `PluginRail.razor`：条目（图标 + 名称）、上下分割线、上限 6 + 「更多插件」
- [x] `AppShell.StartNewChat` 增加可选 `mount` 参数（不新写一套新建逻辑）
- [x] 写入 `OriginMountId = ActiveMountId = <mount>`
- [x] `Sidebar` 接收 mounts 并接线，样式复用 `.sidebar-item`
- [x] 空会话取消预选后不残留任何插件痕迹（除内部字段）

**关键实现点**

- 不要复制「工作区有没有会话」那套判断，**只调用同一个新建方法**，把 mount 作为参数传进去。
- 侧栏入口是「新建会话」，不是「切换当前会话的插件」；后者只在输入框里做。
- 数量上限与「更多」的交互直接复用技能列表的实现，保持一致。

**验收**

- 点入口 → 空会话、输入框已预选、工作区选择器仍在。
- 选了工作区再发消息 → 会话出现在该工作区下。
- 不选工作区 → 会话进「历史聊天」。

**风险**：与「点击空工作区直接新建会话」的逻辑重复 → 统一收口到 `StartNewChat(workspace, mount)`。

---

### 17.5 M3 气泡

**目标**：对任意一条助手消息执行插件，产物是挂在它下面的派生卡片；会话图标跟随最后使用的插件。

**做完能演示**：翻一条旧回答 → 卡片出现 → 重启还在；下一轮普通对话里没有这张卡片；侧栏图标变成翻译图标。

**任务**

- [x] `PluginIcon.razor`：`paths` 白名单渲染 + `text` 兜底
- [x] `PluginActionButton` + 展开面板：OK / Cancel / Esc / 点外部
- [x] `DerivedCard.razor`：先做 `text` 模式（对照在 M6）
- [x] `PluginRunner.RunArtifactAsync` + 写 `ChatMessage.Derived`
- [x] `messages.DerivedJson` 列 + 映射
- [x] `LastPluginId` 写（Run 开始）/ 清（普通推理开始）+ 侧栏图标
- [x] 单飞约束：同一会话同一时间只允许一个 Run
- [ ] 上限：每条消息最多 5 张卡片，单张文本 ≤ 32,000 字符

**关键实现点**

- 构建请求历史时只读 `Content`，**`Derived` 天然不进上下文**——但要补一条单测把这个约定钉住，防止以后有人「顺手」把它拼进去。
- 面板用相对定位容器，展开时不影响下面的消息布局；窄屏改为向下展开。
- 图标只接受 `path d` 数据，渲染前跑白名单正则（见 §13）。

**验收**

- 任意旧回答都能翻译，卡片出现在原气泡下方，原文不变。
- 重启应用，卡片仍在。
- 下一轮普通对话的请求体里没有译文（抓包或日志验证）。
- 普通推理后图标复位为默认；插件 Run 后变插件图标。

**风险**：`DerivedJson` 膨胀 → 张数与长度双上限；渲染卡顿 → 除最新一张外默认折叠。

---

### 17.6 M4 设置

**目标**：插件可见、可关、可排序、可排错。

**做完能演示**：关掉「翻译」→ 侧栏与输入框里的入口立刻消失；改显示上限立即生效；坏插件显示具体原因。

**任务**

- [x] `SettingsSection.Plugins` + `SettingsView` 新增分区
- [x] `PluginSettings.razor`：列表（内置 / 本地 / 加载失败）、启用开关、侧栏 / 输入框开关、顺序
- [x] 全局配置：总开关 `plugins.enabled`、`maxInComposer`、`maxInSidebar`、`rememberParameterValues`、默认输出模式
- [x] 「重新扫描」与「打开插件目录」（Android 隐藏后一个）
- [x] 设置变更 → `SettingsService.Changed` → composer / sidebar / rail 即时刷新
- [x] 失败条目展示文件与位置

**关键实现点**

- 旧 `settings.json` 没有 `plugins` 节点时必须能用默认值启动，不写迁移代码。
- 插件被删除后设置条目保留（重装即恢复），但界面上不展示。
- 开关改动要立即反映到当前会话（正在用的插件被关掉 → 回到全局模式）。

**验收**

- 关掉翻译 → 两个入口消失，已打开的会话提示「插件已停用」并回到全局模式。
- 改上限 3 → 5 → 输入框立即变化。
- 坏插件条目显示原因与位置，且不影响其它插件。

**风险**：设置项与 manifest 默认值打架 → 以「用户显式设置 > manifest `show` / `order` > 内置默认」为准。

---

### 17.7 收尾（M5–M6）

| 阶段 | 内容 | 验收 |
| --- | --- | --- |
| **M5 内置翻译** | 把 `plugin.json` + `translate.md` + 图标放进内置插件目录 | 三条入口全通，无需手工建目录（清单与 skill 已就位，M2 / M3 完成后三条入口才全通） |
| **M6 双语对照** | 格式契约、`SegmentedOutputParser`、`SegmentedOutput.razor`、切换记忆、降级 | 切换零请求；模型不按格式输出时降级为纯译文并提示（已实现） |

M5 建议在 M1 完成后就开始（先手工放目录），别等 M3/M4 全绿——它是检验「通用设计」是否成立的唯一标准。

---

### 17.8 风险登记

| 风险 | 触发 | 影响 | 缓解 |
| --- | --- | --- | --- |
| 老库缺列 | 升级后启动 | 直接崩溃 | M1 首提交扩 `UpgradeSchemaAsync`；坏库跳过不影响其它库 |
| 派生内容污染上下文 | 误把 `Derived` 拼进 history | 推理变差、token 上涨 | 只读 `Content` + 单测断言 |
| 模型不按 segments 输出 | 换模型 / 超长文本 | 对照不可用 | 降级为纯译文并提示，不抛错 |
| 插件提示词过长 | 用户堆指令 | 成本与延迟上升 | 建议 4 KB、硬上限 256 KB |
| 界面状态丢失 | 切会话 / 重启 | 体验断裂 | 会话级字段 + §16 验收项 |
| 与全局技能混淆 | 两套 chip 同时出现 | 界面混乱 | 插件模式互斥隐藏技能区 |
| 图标注入 | 第三方 SVG | XSS | 只收 `path` 数据 + 白名单 |

### 17.9 文件清单

#### 新增文件

```text
src/SDemo.DesktopAgent.Core/Plugins/
  PluginManifest.cs          清单模型 + JSON 反序列化（允许注释与尾逗号）
  PluginCatalog.cs           扫描 / 校验 / 排序 / 变更事件
  PluginModels.cs            Capability、Mount、Parameter 描述符
  PluginValidator.cs         路径、占位符、id、版本校验
  PluginHostVersion.cs       宿主版本与 minHostVersion 比较
  PluginRegistry.cs          目录结果 + 用户设置合并成运行时有效视图
  PluginParameterResolver.cs 默认值解析、系统语言映射、会话记忆读写
  PluginRunner.cs            Run 编排、分段、取消、产物写入
  PluginPromptComposer.cs    skill 渲染、定界包裹、格式契约追加
  SegmentedOutputParser.cs   <s>/<t> 解析与降级

src/SDemo.DesktopAgent.Core/Models/
  PluginPreferences.cs       settings.json 的 plugins 节点（开关 / 上限 / 每插件覆盖 / 隐私确认）

src/SDemo.DesktopAgent.Core/Services/
  AgentMessageMapper.cs      请求上下文组装（只读正文与附件，派生卡片不参与）

tests/SDemo.DesktopAgent.Core.Tests/
  PluginCatalogTests.cs      扫描 / 校验 / 去重 / 降级 / 体积上限
  PluginManifestTests.cs     注释、尾逗号、大小写、语法错误行号、未知字段
  PluginModelTests.cs        问题定位文案、参数显示名、宿主版本比较
  CompositionTests.cs        DI 注册与插件目录默认值

src/SDemo.DesktopAgent.UI/Components/Plugins/
  PluginIcon.razor           图标渲染（paths / text 兜底）
  PluginChip.razor           输入框 chip（选中态 + ✕）
  PluginParameterControl.razor  单个参数的控件渲染
  PluginParameterPanel.razor    面板（内联 / 展开两种布局）
  PluginRail.razor           侧栏入口区
  PluginActionButton.razor   气泡按钮 + 展开状态机
  DerivedCard.razor          派生卡片（对照 / 纯译文切换）
  SegmentedOutput.razor      分段渲染（宽屏两列 / 窄屏上下）

src/SDemo.DesktopAgent.UI/Components/Settings/
  PluginSettings.razor       插件管理页
```

#### 改造文件

| 文件 | 改动 |
| --- | --- |
| `Core/Models/ChatSession.cs` | 加 `OriginMountId` / `ActiveMountId` / `LastPluginId` / `PluginMemory` |
| `Core/Models/ChatMessage.cs` | 加 `Plugin` / `Derived` |
| `Core/Models/AppSettings.cs` | 加 `Plugins` 设置块 |
| `Core/Data/ChatDbContext.cs` | 两张表各加列 |
| `Core/Services/ChatStore.cs` | 补列逻辑扩展到 `sessions`；两处映射补字段 |
| `Core/Services/AgentChatService.cs` | 抽出「按 plan 执行」的底层方法，供普通对话与插件 Run 共用 |
| `Core/Abstractions/IAppPaths.cs` | 加 `PluginsRoot` |
| `Core/Composition/AgentCoreServiceCollectionExtensions.cs` | 注册 Catalog / Runner / Resolver |
| `UI/Components/Chat/ChatComposer.razor` | 插件区、模式切换、参数内联 |
| `UI/Components/Chat/ChatRoom.razor` | Run 分叉、派生渲染、状态持久化（当前切会话会清空选择，需改） |
| `UI/Components/Sidebar/Sidebar.razor` | 入口区、会话图标 |
| `UI/Components/Settings/SettingsSection.cs` / `SettingsView.razor` | 新分区 |
| `README.md` | 补一节「插件」并链接本文 |

---

## 18. 后续演进

| 方向 | 触发条件 | 要点 |
| --- | --- | --- |
| native 插件（程序集） | 出现「读数据库 / 调私有 API」的插件 | `AssemblyLoadContext` + 权限声明 + 崩溃隔离 + 版本兼容 |
| 工具型插件（function calling） | 插件需要主动获取外部信息 | 现有 `AgentToolbox` → `ToolRegistry`，工具与技能彻底分家 |
| MCP | 想复用生态里现成的 server | 作为 native 插件的一种传输方式，技能与设置仍走自有清单 |
| 插件市场 / 安装包 | 有分发给别人的需求 | zip + 清单签名 + 版本约束 + 更新检查 |
| UI 插槽 | 设置页需要插件自定义表单 | 先做 `settings.schema` 的描述符渲染，别加载 Razor 组件 |
| 插件级默认值 | 全局偏好而非会话记忆 | `settings` 里支持 per-plugin 的 `defaults` |

---

## 附：术语表

| 术语 | 含义 |
| --- | --- |
| Plugin | 插件包：一个目录 + `plugin.json` |
| Capability | 能力：skill + 参数 + 输出格式 |
| Mount | 入口：能力在某个界面上的挂载配置 |
| Surface | 界面类型：`sidebar` / `composer` / `message` |
| Run | 一次执行 |
| Derived artifact | 派生卡片：挂在消息下的加工结果，不进上下文 |
| Global skill | 全局技能：`skills/` 根目录下的 md，不绑定插件 |
| Segment | 对照模式下的一对「原文 / 译文」 |
