# 插件开发手册

> 面向「不给应用写一行 C#，只写 `plugin.json` + Markdown」的插件作者。
> 完整设计理由见 [plugin-system.md](../architecture/detail/plugin-system.md)；本手册只讲**当前版本实际能做什么、怎么做、踩坑怎么查**。
>
> 应用还自带一个同名内置技能（`plugin-authoring`）：直接让 Agent「帮我做一个插件」即可，
> 它会按本手册的规则写清单、写技能并带你验收。见 [§16](#16-让-agent-自己做插件内置技能)。

---

## 目录

1. [5 分钟做一个插件](#1-5-分钟做一个插件)
2. [包结构与加载规则](#2-包结构与加载规则)
3. [plugin.json 参考](#3-pluginjson-参考)
4. [技能（skill）文件](#4-技能skill文件)
5. [参数与默认值](#5-参数与默认值)
6. [四条入口：侧栏 / 输入框 / 气泡 / 划词](#6-四条入口侧栏--输入框--气泡--划词)
7. [输出格式：纯文本与双语对照](#7-输出格式纯文本与双语对照)
8. [执行模型与限制](#8-执行模型与限制)
9. [安全与隐私](#9-安全与隐私)
10. [设置与优先级](#10-设置与优先级)
11. [校验规则与报错对照表](#11-校验规则与报错对照表)
12. [调试清单](#12-调试清单)
13. [可复制模板](#13-可复制模板)
14. [当前版本不支持的能力](#14-当前版本不支持的能力)
15. [附录：设置与记忆键](#15-附录设置与记忆键)
16. [让 Agent 自己做插件（内置技能）](#16-让-agent-自己做插件内置技能)

---

## 1. 5 分钟做一个插件

以「润色」为例：

**① 找到插件目录**：应用里「设置 → 插件」，点「打开插件目录」（Windows 上是 `%APPDATA%\SDemo\DesktopAgent\plugins`）。

**② 新建文件夹** `polish/`，写入 `plugin.json`：

```json
{
  "id": "polish",
  "name": "润色",
  "version": "1.0.0",
  "description": "把文字改得更通顺，不改变原意。",
  "capabilities": [
    { "id": "polish", "name": "润色", "skill": "skills/polish.md" }
  ],
  "mounts": [
    { "id": "composer", "surface": "composer", "capability": "polish" },
    { "id": "bubble", "surface": "message", "capability": "polish", "input": "message" }
  ]
}
```

**③ 写入 `skills/polish.md`**：

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

**④ 回到「设置 → 插件」点「重新扫描」**，列表里出现「润色 · 本地」即成功。

**⑤ 试两条路径**：

- 输入框底部出现「润色」chip，选中后输入文字发送 → 得到一条带「润色 · SDemo」归属的回答。
- 任意助手消息底部（时间右侧）出现润色按钮，点一下 → 原消息下方出现派生卡片。

> 想试划词：在 `mounts` 里再加一条 `{ "id": "selection", "surface": "selection", "capability": "polish" }`，
> 重新扫描后在「设置 → 插件」打开该插件的「选中」，到聊天正文里选中一段文字、松开鼠标即可看到浮动菜单。

> 提示：插件不用重启应用；但**已经打开的会话**需要重新选一次插件（入口列表是扫描时生成的）。

---

## 2. 包结构与加载规则

```text
<插件目录>/
├── plugin.json          ← 必需，UTF-8（带不带 BOM 都行）
├── icon.svg             ← 可选，不会被读取（图标写在 plugin.json 里）
└── skills/
    └── main.md          ← 能力引用的技能文件
```

| 来源 | 位置 | 说明 |
| --- | --- | --- |
| 本地 | `%APPDATA%\SDemo\DesktopAgent\plugins\<目录>\` | 用户自制；Android 是 `<AppDataDirectory>/plugins` |
| 内置 | `src/Plugins/SDemo.DesktopAgent.Plugin.<名字>/`（编译进类库） | 清单与技能是嵌入资源；同 `id` 的本地插件仍可覆盖 |

加载规则：

- 只认 `<插件目录>/<任意文件夹>/plugin.json`，**目录名不要求等于 id**（建议一致）；没有 `plugin.json` 的子目录会被静默跳过。
- 同一个 `id`：**本地覆盖内置**，内置那条在设置页仍显示并标注「已覆盖」。
- 两个本地插件同 `id`：按顺序保留第一个，另一个标「重复」且不可用。
- 排序：内置在前、本地在后；同来源按 `order` 升序、再按名称。
- 扫描时机：应用启动、进入设置页点「重新扫描」。**没有文件监视**，改完记得重新扫描。

体积上限（超限直接拒绝并提示）：

| 文件 | 上限 |
| --- | --- |
| `plugin.json` | 512 KB |
| 单个 skill | 256 KB |
| 图标 path 数据总长 | 4 KB，最多 8 条 |

---

## 3. `plugin.json` 参考

文件允许 `//` 注释与尾逗号，字段名大小写不敏感；**未知字段只警告、不阻断**（设置页会列出具体 JSON 路径，方便发现拼写错误）。

### 3.1 顶层

| 字段 | 必填 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- | --- |
| `id` | ✅ | string | — | `^[a-z][a-z0-9-]{1,31}$`，全局唯一 |
| `name` | ✅ | string | — | 显示名，最长 24 字符（超出会截断并警告），支持中文/空格/Emoji |
| `version` | | string | `"1.0.0"` | 展示用，不参与比较 |
| `description` | | string | `""` | 设置页里的一句话说明 |
| `icon` | | object | 首字 | 见 §3.5 |
| `minHostVersion` | | string | — | 高于当前应用版本时插件被禁用并提示 |
| `capabilities` | ✅ | array | — | 至少 1 个 |
| `mounts` | ✅ | array | — | 至少 1 个 |

`minHostVersion` 写 `"1.2.0"` 这种形式（`v` 前缀、`-beta` 后缀会被忽略）。

### 3.2 `capabilities[]`

| 字段 | 必填 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- | --- |
| `id` | ✅ | string | — | 插件内唯一 |
| `name` | ✅ | string | — | 展示名 |
| `skill` | ✅ | string | — | 相对插件目录的 `.md` 路径；**不允许 `..` 或绝对路径** |
| `allowGlobalSkills` | | bool | `false` | 是否额外注入全局技能（v1 的输入框插件模式不注入，保留字段） |
| `allowHistory` | | bool | `false` | 是否允许带会话历史（还要看 `contextParameter`；默认关） |
| `contextParameter` | | string | — | 控制「是否关联上下文」的 toggle 参数 key；必须同时 `allowHistory: true` |
| `output` | | object | `{"format":"text"}` | `text` 或 `segments`（双语对照） |
| `privacy` | | object | `{}` | `{"sendsText": true}` 时首次使用弹确认 |
| `parameters` | | array | `[]` | 见 §5 |

### 3.3 `mounts[]`

| 字段 | 必填 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- | --- |
| `id` | ✅ | string | — | 插件内唯一 |
| `surface` | ✅ | enum | — | `sidebar` / `composer` / `message` / `selection` |
| `capability` | ✅ | string | — | 必须指向本插件已定义的 capability |
| `input` | | enum | 按 surface 推断 | `composer` / `message` / `selection` |
| `order` | | int | `100` | 升序；同一个 surface 内比较 |
| `show` | | bool | `true` | 默认是否显示；用户设置可以覆盖 |

`input` 的推断：

- `surface=sidebar` / `composer` → `composer`
- `surface=message` → `message`
- `surface=selection` → `selection`

规则：

- `surface=selection` 只能使用 `input=selection`（一般直接省略）；
- 其它 surface 不允许声明 `input: "selection"`，否则该入口加载失败；
- 划词菜单是独立入口，不受「输入框」开关影响；只受插件级启用和「选中」开关控制。

### 3.4 一个能力挂多个入口

能力只定义一次，入口想挂几个挂几个：

```json
"capabilities": [
  { "id": "translate", "name": "翻译", "skill": "skills/translate.md",
    "output": { "format": "segments" },
    "parameters": [
      { "key": "target", "label": "目标语言", "kind": "select",
        "defaultFrom": "system-language", "fallback": "en",
        "options": [
          { "value": "zh-Hans", "label": "简体中文" },
          { "value": "en", "label": "English" }
        ] }
    ] }
],
"mounts": [
  { "id": "sidebar",   "surface": "sidebar",   "capability": "translate", "order": 10 },
  { "id": "composer",  "surface": "composer",  "capability": "translate", "order": 10 },
  { "id": "bubble",    "surface": "message",   "capability": "translate", "input": "message", "order": 10 },
  { "id": "selection", "surface": "selection", "capability": "translate", "order": 10 }
]
```

### 3.5 图标

```json
"icon": {
  "paths": ["M3.5 5h6M6.5 3.2v1.8M9.5 5c0 3.6-2.2 6-5 7.4"],
  "text": "译"
}
```

- **只接受 SVG 的 `path` 数据**（`d` 属性），不接受整段 `<svg>` 标记。
- 字符白名单：`MmLlHhVvCcSsQqTtAaZz`、数字、`.`、`,`、`-`、空白；出现其它字符的那条 path 会被丢弃并警告。
- 最多 8 条、总长 ≤ 4 KB；超限丢弃图标。
- 全部无效且没有 `text` 时回退到插件名首字；`text` 建议 1 个字。

---

## 4. 技能（skill）文件

skill 是**插件私有的提示词资产**，只在运行该插件时注入 system 消息，不进入全局技能列表。

```markdown
---
name: 翻译
description: 把输入文本翻译成指定语言。
version: 1.0
---

（正文：角色、步骤、约束、输出要求）
```

- frontmatter 可选，但建议保留 `name` / `description`（设置页与「⋯」菜单展示用）。
- **frontmatter 不会发给模型**，只有正文会。
- 正文里的 `{{key}}` / `{{key.label}}` 会被替换成参数值 / 选项显示名；**引用了不存在的参数 = 能力加载失败**（不会静默变空）。
- 用户输入**不会**拼进 skill：宿主会把它单独放进 user 消息，并用 `<待处理文本>` 包住（输入里的 `<` 会转成全角 `＜`）。
- 所以 skill 里**不要**自己写「用户输入会放在 <待处理文本> 里」，也不要把输入格式写进 system。

写作建议：

1. 先写「你是谁」，再写「按什么步骤」，最后写「禁止什么」。
2. 输出格式写死（「只输出译文，不要解释」），比事后裁剪便宜。
3. 不要要求模型「先分析再输出」——思维链会混进正文。
4. 控制在 4 KB 以内，越长的指令越容易被稀释。

---

## 5. 参数与默认值

同一份参数 schema 被三处消费：输入框内联控件、气泡展开面板、设置页的默认值说明。

### 5.1 字段

| 字段 | 必填 | 类型 | 说明 |
| --- | --- | --- | --- |
| `key` | ✅ | string | `^[a-z][a-zA-Z0-9_]*$`；模板里用 `{{key}}` |
| `label` | ✅ | string | 控件标签（缺失会用 key 代替并警告） |
| `kind` | ✅ | enum | `select` / `multiselect` / `toggle` / `text` / `number` |
| `options` | `select`/`multiselect` 必填 | array | `[{"value":"ja","label":"日本語"}]` |
| `default` | | string | 字面默认值 |
| `defaultFrom` | | string | `system-language` 或 `system-locale` |
| `fallback` | | string | 动态默认值解析不出来时的兜底 |
| `required` | | bool | 默认 `true` |
| `placeholder` | | string | `text` / `number` 的水印 |
| `maxLength` | | int | `text` 最大长度，默认 2000 |

`multiselect` 的 schema 能通过校验，**但当前界面不支持多选控件**（面板里显示「多选暂不支持」），请优先用 `select` 或 `toggle`。

### 5.2 默认值解析顺序

```text
会话记忆值 → default → defaultFrom 解析结果 → fallback → 第一个 option
```

- 记忆值或 `default` 不在 `options` 里时视为「没有」，继续往后回落；
- `select` 最终一定落在 `options` 里（最差是第一个）；
- `text` / `number` 没有可用值时为空字符串；
- 用户在设置里关掉「记住每个会话上次选中的参数」后，只走 manifest 默认值，不读也不写记忆。

### 5.3 系统语言映射（`defaultFrom: "system-language"`）

| 系统区域 | 映射到 |
| --- | --- |
| `zh-CN` / `zh-SG` / `zh-Hans-*` | `zh-Hans` |
| `zh-TW` / `zh-HK` / `zh-MO` / `zh-Hant-*` | `zh-Hant` |
| `ja*` | `ja` |
| `fr*` | `fr` |
| `ko*` | `ko` |
| 其它（含 `en*`） | `en` |

`system-locale` 直接返回区域名（如 `zh-CN`），适合需要完整区域的场景。

### 5.4 记忆

- 每个会话、每个插件、每个参数记一个值：`translate/composer/target = "ja"`；
- 键格式 `<pluginId>/<mountId>/<key>`，`ui.` 前缀保留给宿主；
- 记忆跟着**会话**走：A 会话译日文，B 会话仍按系统语言；
- 切换插件参数不会影响别的插件。

### 5.5 界面上的表现

- 输入框：最多内联 **2 个**参数；更多参数继续用默认值，chip 旁会提示「还有 N 项参数」；
- 气泡：点击按钮时若有参数，展开面板（OK / Cancel / Esc / 点外部关闭）；
- 生成过程中参数锁定，不能切换插件或改参数。

---

### 5.6 关联上下文开关（`contextParameter`）

想让用户自己决定「这一轮要不要带会话历史」，声明一个 toggle 参数，并让能力指向它：

```json
"capabilities": [
  {
    "id": "quick-ask",
    "name": "简单问答",
    "skill": "skills/quick-ask.md",
    "allowHistory": true,
    "contextParameter": "useContext",
    "parameters": [
      { "key": "useContext", "label": "关联上下文", "kind": "toggle", "default": "true" }
    ]
  }
]
```

- `allowHistory` 是总闸：没有它，`contextParameter` 不生效（校验会报错）。
- `contextParameter` 必须是本能力里已定义的 `toggle` 参数。
- 参数值为 `false` 时这一轮不带历史；缺省或非法值按「打开」处理。
- 历史只在**入口本身允许**时才会带（见 §8）；划词 / 气泡默认也是关的，打开后才带。
- 参数值仍然按会话记忆：勾选一次，之后同一会话默认沿用。

---

## 6. 四条入口：侧栏 / 输入框 / 气泡 / 划词

| 入口 | 点了之后 | 产物 | 上限 |
| --- | --- | --- | --- |
| 侧栏 `sidebar` | **新建一个空会话**，输入框预选该插件 | 之后的发送走插件 Run | `maxInSidebar`，默认 6 个，超出进「更多插件」 |
| 输入框 `composer` | 选中 chip 后发送 | 一条带插件归属的助手消息（`翻译 · SDemo`） | `maxInComposer`，默认 3 个，超出进「更多」 |
| 气泡 `message` | 对这条助手消息做加工 | 挂在原消息下的**派生卡片** | 每条消息最多 5 张；按钮内联 2 个，其余进「⋯」 |
| 划词 `selection` | 在聊天记录里选中文本、鼠标抬起，点浮动菜单里的插件 | 以选中文本为输入的**派生卡片**，挂在选区所在消息下 | `maxInSelection`，默认 4 个，超出进「更多」 |

行为要点：

- **侧栏依赖输入框**：设置里关掉「输入框」后，侧栏入口一起隐藏（侧栏会进入预选插件的会话，进去看不到插件就没有意义）。
- 四个入口可以独立开关（设置里「输入框 / 侧栏 / 气泡 / 选中」）；划词独立于输入框。
- 输入框模式：选中插件后全局技能区隐藏；chip 上的 `✕` 随时取消。
- 气泡 / 划词模式：**不新增对话轮、不改写原消息**；卡片可复制 / 重试 / 删除。
- 派生卡片**默认不进入后续上下文**；卡片上的「加入上下文」按钮可以把内容加进去（只影响之后的请求，不会重新请求模型）。
- 划词只处理**单条消息正文内**的选区；跨消息、空选区、选中时间 / 按钮都不弹菜单；滚动、切会话、点空白或按关闭按钮会收起。
- 会话图标跟随「最后一次实际运行的插件」；普通推理开始后立即复位。
- 生成中禁止切换插件；同一会话同一时间只允许一个 Run。

---

## 7. 输出格式：纯文本与双语对照

### 7.1 `text`（默认）

模型返回什么就显示什么，按 Markdown 渲染。

### 7.2 `segments`（双语对照）

能力里声明：

```json
"output": { "format": "segments" }
```

宿主会**自动**在 system 提示词末尾追加格式契约，**你不需要（也不应该）在 skill 里写格式说明**：

```text
输出格式（必须严格遵守）：
把内容按段落逐段处理，每一段输出一对标签，先原文后译文：
<s>原文段落</s>
<t>译文段落</t>
逐段重复。标签外不要输出任何文字，不要使用代码块包裹这些标签。
```

解析规则（宿主实现，了解即可）：

1. 按出现顺序把 `<s>` / `<t>` 配成段落；
2. 流式过程中未闭合的尾部标签先丢弃；
3. `<s>` 与 `<t>` 数量不等时以较少的为准，多出来的 `<t>` 并入最后一段译文；
4. 一条完整配对都没有 → **降级为纯译文**，卡片提示「本次结果未按段落返回」，对照开关禁用；
5. 超长结果截断到 32,000 字符（保留最后一段提示）。

界面：

- 宽屏（>760px）：两列表格，左原文（弱化色）右译文；
- 窄屏：上下排列，原文在上、译文在下；
- 卡片和助手消息都带「对照 / 译文」开关，**切换只重新渲染，不会重新请求模型**；
- 切换状态按插件记在会话记忆里（`translate/ui.mode`），新会话默认值取「设置 → 插件 → 新会话译文默认呈现」。

> `segments` 只适合「逐段原文 + 译文」的能力（翻译、对照改写）。润色、总结这类只该输出一份结果的插件，请用 `text`。

---

## 8. 执行模型与限制

一次 Run 的流程：

```text
参数取值 → 渲染 skill（system）→ 包装输入（user）→ 单轮调用 → 产物
```

| 项 | 行为 |
| --- | --- |
| 轮数 | 单轮，不自动多轮 |
| 历史 | 只有 `allowHistory: true` 且「关联上下文」为开时才带最近 10 条 / ≤24,000 字符；输入框取当前用户消息之前，气泡取消息之前，划词取选区所在消息及之前 |
| 工具 | v1 插件**不支持**工具/函数调用，插件只产出文本 |
| 派生内容 | 默认不进入后续上下文；用户点「加入上下文」后，从下一轮开始追加到所属消息之后（仅 Done 且有文本的卡片） |
| 全局技能 | 插件模式不注入（`allowGlobalSkills` 保留字段，当前不生效） |
| 输入长度 | 单次 ≤60,000 字符；超出直接报错（长文本分块尚未实现），不会偷偷截断 |
| 输出长度 | 卡片 / 消息正文超过 32,000 字符截断并提示 |
| 取消 | 用输入框的停止按钮取消；已生成部分保留，状态标「已取消」 |
| 失败 | 错误以可读文本展示；卡片可「重试」 |
| 模型 | 用当前会话选中的 provider + model；插件不能指定模型或温度 |
| 长文本 | > 4,000 字不分块，仍是一次请求（受 60,000 上限约束） |

---

## 9. 安全与隐私

| 面 | 规则 |
| --- | --- |
| skill 路径 | 必须位于插件目录内、以 `.md` 结尾；`..` 与绝对路径直接拒绝 |
| 图标 | 只收 `path` 数据 + 字符白名单，杜绝 `<script>` / 事件属性 |
| 提示词注入 | skill 只进 system，用户文本只进 user 并定界，`<` 全角转义 |
| 参数值 | 只做模板替换，不参与路径 / 文件名 / SQL |
| 体积 | 清单 512 KB、skill 256 KB、图标 4 KB，超限拒绝 |
| 隐私提示 | `privacy.sendsText: true` 时首次执行前弹一次确认（可取消）；确认结果记在 `plugins.privacyAcknowledged` |
| 分发 | v1 只支持本地目录安装，没有签名与市场；**只安装你信任的插件** |

---

## 10. 设置与优先级

「设置 → 插件」里的全局项：

| 设置 | 默认 | 作用 |
| --- | --- | --- |
| 启用插件 | 开 | 总开关；关掉后四处入口全部消失 |
| 输入框最多显示 | 3 | 超出进「更多」 |
| 侧栏最多显示 | 6 | 超出进「更多插件」 |
| 划词菜单最多显示 | 4 | 超出进「更多」 |
| 记住每个会话上次选中的参数 | 开 | 关掉后只用 manifest 默认值 |
| 新会话译文默认呈现 | 对照 | `segments` 能力的默认呈现方式 |

每个插件还有：启用、输入框、侧栏、气泡、选中、顺序。

`选中` 是划词菜单的独立开关：关掉后该插件不再出现在选区浮动条里，但输入框 / 气泡入口不受影响。

优先级：**用户显式设置 > manifest 的 `show` / `order` > 内置默认**。

- `order` 数字小的排前面；清空表示用 manifest 里的 `order`；
- 插件被停用后，已打开的会话会提示「当前不可用（已停用或被删除）」并回到普通对话；重新启用后会话恢复插件模式；
- 插件被删除后设置条目保留，重新装回来仍然生效。

---

## 11. 校验规则与报错对照表

所有问题都会在「设置 → 插件」里显示**文件 + 行号**或**文件 + JSON 路径**，不会白屏。

### 11.1 整个插件不可用（红色「加载失败」）

| 报错 | 原因 |
| --- | --- |
| `清单无法解析：… 第 N 行` | JSON 语法错误（少引号、少括号） |
| `plugin.json 超过 512 KB 上限` | 清单太大 |
| `缺少 id` / `id 必须匹配 ^[a-z][a-z0-9-]{1,31}$` | id 缺失或格式不合法 |
| `缺少 name` | 没有显示名 |
| `capabilities 至少要有一个能力` | 空数组或没写 |
| `mounts 至少要有一个入口` | 空数组或没写 |
| `capabilities 的 id 重复` / `mounts 的 id 重复` | 插件内 id 不唯一 |
| `清单 schemaVersion=2，本应用只支持 1` | 清单版本过新 |
| `插件 id "x" 与 <目录> 重复，已忽略` | 同来源两个插件同 id |

### 11.2 只有某个能力不可用（插件仍在列表里）

| 报错 | 原因 |
| --- | --- |
| `缺少 skill 路径` | capability 没写 `skill` |
| `skill 路径越界：不允许 ..` / `不允许绝对路径` | 试图读插件目录外的文件 |
| `skill 必须是 .md 文件` | 扩展名不对 |
| `skill 文件不存在：skills/x.md` | 路径写错或文件没放进去 |
| `skill 超过 256 KB 上限` | 文件太大 |
| `占位符 {{x}} 没有对应的参数定义`（带 skill 行号） | skill 用了没声明的参数 |
| `参数 "x" 的 kind 必须是 …` | kind 拼错 |
| `参数 "x" 是 Select，必须提供 options` | 下拉没有选项 |
| `output.format 只能是 text 或 segments` | 格式值写错 |
| `参数 "x" 的 defaultFrom "y" 不支持` | 只支持 `system-language` / `system-locale` |
| `contextParameter "x" 没有对应的参数定义` | 指向了不存在的参数 key |
| `contextParameter "x" 必须指向 toggle 参数` | 指向的不是 `kind: "toggle"` |
| `contextParameter 需要同时设置 allowHistory: true` | 只写了开关，没打开历史总闸 |

### 11.3 只有某个入口不可用

| 报错 | 原因 |
| --- | --- |
| `capability "x" 在本插件里不存在` | `mounts[].capability` 写错 |
| `surface 必须是 sidebar / composer / message / selection` | surface 拼错 |
| `surface selection 只能使用 input selection` | 选中入口写成了别的 input |
| `input selection 只能用于 surface selection` | 在气泡 / 输入框入口上声明了 `input: "selection"` |
| `capability "x" 自身加载失败，这个入口无法使用` | 能力本身有问题，先修能力 |

### 11.4 只警告、不影响运行

- `未知字段 "foo"` —— 拼写错误或用了不支持的字段；
- `name 超过 24 个字符，已截断`；
- `icon.paths[i] 含非法字符，已忽略这条 path`；
- `参数 "x" 的 default 不在 options 里`（运行时会回落）；
- `无法识别的占位符 {{…}}`（写法只支持 `{{key}}` 与 `{{key.label}}`）。

---

## 12. 调试清单

| 现象 | 排查 |
| --- | --- |
| 列表里没有插件 | 目录层级必须是 `plugins/<文件夹>/plugin.json`；json 有语法错误看设置页行号 |
| 显示「加载失败」 | 按报错改；最常见是 `mounts[].capability` 或 `skill` 路径写错 |
| chip 出现了但点了没反应 | 没配置模型（去「设置 → 模型设置」）；或参数必填但没默认值 |
| 侧栏入口没了但输入框有 | 检查「输入框」开关是否被关掉（侧栏依赖它） |
| 气泡按钮不见了 | 检查该插件的「气泡」开关与总开关 |
| 划词菜单不出现 | 检查「选中」开关；只支持单条消息正文内的选区，空选区 / 跨消息 / 选中时间按钮不会弹 |
| 卡片上没有「加入上下文」 | 只有 Done 且有文本的卡片才显示；运行中 / 失败 / 取消的卡片不能加入 |
| 输出格式乱/带 `<s>` 字样 | `segments` 能力要检查 skill 是否要求了「先分析再输出」；多余解释会被忽略但会稀释指令 |
| 改了 skill 不生效 | 点「重新扫描」；已打开的会话需要重新选一次插件 |
| 设置改了没反应 | 入口列表与参数在下次渲染时刷新；若仍无变化，重新扫描一次 |
| 插件在生成中被删除 | 当前 Run 会继续（内容已在内存），下次刷新消失 |

调试技巧：

- 先在设置页确认插件的「入口」一行是否四类都有；
- 用一个**最小 skill**（只写一句「把输入原样返回」）排除提示词问题；
- 参数先用 `default` 写死，跑通后再换成 `defaultFrom`。

---

## 13. 可复制模板

### 13.1 最小（只有输入框入口）

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

### 13.2 带参数 + 四个入口

```json
{
  "id": "tone",
  "name": "语气改写",
  "version": "1.0.0",
  "description": "按指定语气改写文本。",
  "icon": { "text": "语" },
  "capabilities": [
    {
      "id": "tone",
      "name": "语气改写",
      "skill": "skills/tone.md",
      "privacy": { "sendsText": true },
      "parameters": [
        {
          "key": "tone",
          "label": "语气",
          "kind": "select",
          "default": "neutral",
          "options": [
            { "value": "neutral", "label": "中性" },
            { "value": "formal", "label": "正式" },
            { "value": "casual", "label": "口语" }
          ]
        },
        {
          "key": "keepFormat",
          "label": "保留格式",
          "kind": "toggle",
          "default": "true"
        }
      ]
    }
  ],
  "mounts": [
    { "id": "sidebar",   "surface": "sidebar",   "capability": "tone", "order": 20 },
    { "id": "composer",  "surface": "composer",  "capability": "tone", "order": 20 },
    { "id": "bubble",    "surface": "message",   "capability": "tone", "input": "message", "order": 20 },
    { "id": "selection", "surface": "selection", "capability": "tone", "order": 20 }
  ]
}
```

```markdown
---
name: 语气改写
description: 按指定语气改写文本。
version: 1.0
---

你是一位文字编辑。把用户提供的文本改写成「{{tone.label}}」语气（代码：{{tone}}）。

规则：
- 不改变原意，不增删事实。
- 保留格式：{{keepFormat}}（true 表示保留段落与 Markdown 结构）。
- 只输出改写后的全文，不要解释。
```

### 13.3 双语对照（`segments`）

```json
{
  "id": "translate",
  "name": "翻译",
  "version": "1.0.0",
  "capabilities": [
    {
      "id": "translate",
      "name": "翻译",
      "skill": "skills/translate.md",
      "output": { "format": "segments" },
      "privacy": { "sendsText": true },
      "parameters": [
        {
          "key": "target",
          "label": "目标语言",
          "kind": "select",
          "defaultFrom": "system-language",
          "fallback": "en",
          "options": [
            { "value": "zh-Hans", "label": "简体中文" },
            { "value": "ja", "label": "日本語" },
            { "value": "en", "label": "English" }
          ]
        }
      ]
    }
  ],
  "mounts": [
    { "id": "composer", "surface": "composer", "capability": "translate" },
    { "id": "bubble", "surface": "message", "capability": "translate", "input": "message" }
  ]
}
```

skill 里**不要写** `<s>` / `<t>` 或「分段输出」——宿主会自动加契约。你只需要写清翻译规则。

### 13.4 划词 + 关联上下文（内置「简单问答」同款）

```json
{
  "id": "quick-ask",
  "name": "简单问答",
  "version": "1.0.0",
  "description": "跳过技能与工具，直接给出简洁回答；可选择是否关联上下文。",
  "icon": { "text": "问" },
  "capabilities": [
    {
      "id": "quick-ask",
      "name": "简单问答",
      "skill": "skills/quick-ask.md",
      "allowHistory": true,
      "contextParameter": "useContext",
      "privacy": { "sendsText": true },
      "parameters": [
        { "key": "useContext", "label": "关联上下文", "kind": "toggle", "default": "true" }
      ]
    }
  ],
  "mounts": [
    { "id": "sidebar",   "surface": "sidebar",   "capability": "quick-ask", "order": 5 },
    { "id": "composer",  "surface": "composer",  "capability": "quick-ask", "order": 5 },
    { "id": "bubble",    "surface": "message",   "capability": "quick-ask", "input": "message", "order": 5 },
    { "id": "selection", "surface": "selection", "capability": "quick-ask", "order": 5 }
  ]
}
```

```markdown
---
name: 简单问答
description: 跳过技能与工具，直接给出简洁、准确的回答。
version: 1.0
---

你是一个直接、可靠的问答助手。用户提供的文本可能是一个问题，也可能是一段需要解释、核对或补充的内容。

规则：
- 是问题就直接回答；是陈述或片段就解释它、指出关键点或给出结论。
- 直接给答案，不要复述问题，不要写「好的」「以下是」之类的前言。
- 简洁优先：默认三句以内；确实需要步骤、列表或代码时再展开。
- 不使用工具、不读取文件、不调用外部服务。
- 信息不足时明确说明缺少什么，不要编造。
- 输出语言与文本一致；保留必要的 Markdown 格式。
```

> `allowHistory` + `contextParameter` 的组合需要宿主版本支持（当前版本已支持）；
> 只写 `contextParameter` 而不写 `allowHistory: true` 会让能力加载失败。

---

## 14. 当前版本不支持的能力

| 想做 | 当前状态 |
| --- | --- |
| 编写 C# / 原生插件 | ❌ v1 只有声明式插件 |
| 插件调用工具 / 函数 / 网络 / 文件 | ❌ 插件只产出文本；需要副作用的能力应先做成内置工具 |
| 多选参数 | ⚠️ schema 合法，界面显示「多选暂不支持」 |
| 选中文本作为输入（`surface: "selection"`） | ✅ 已支持，见 §3.3 / §6 |
| 自定义模型 / 温度 / maxTokens | ❌ 用会话当前模型 |
| 长文本自动分块 | ❌ 上限 60,000 字符 |
| 联网安装 / 插件市场 / 签名 | ❌ 只支持本地目录 |
| UI 插件（自定义面板 / 设置表单） | ❌ 只有描述符 + 通用面板 |

---

## 15. 附录：设置与记忆键

### 15.1 `settings.json` 的 `plugins` 节点

```json
"plugins": {
  "enabled": true,
  "maxInComposer": 3,
  "maxInSidebar": 6,
  "maxInSelection": 4,
  "rememberParameterValues": true,
  "defaultOutputMode": "Bilingual",
  "entries": [
    {
      "id": "translate",
      "enabled": true,
      "inSidebar": true,
      "inComposer": true,
      "inMessage": true,
      "inSelection": true,
      "order": 10
    }
  ],
  "privacyAcknowledged": ["translate"]
}
```

- `entries` 只在用户改过设置时写入；`null` 表示没设过，用 manifest 默认；
- `defaultOutputMode`：`Bilingual` / `Translation`；
- 旧版 `settings.json` 没有 `plugins` 节点时自动用默认值，无需迁移。

### 15.2 会话记忆键

| 键 | 含义 |
| --- | --- |
| `<pluginId>/<mountId>/<key>` | 某个参数上次选的值，例如 `translate/composer/target` |
| `<pluginId>/ui.mode` | 对照 / 译文的上次选择（`bilingual` / `translation`），同一插件四个入口共用 |

`ui.` 前缀属于宿主，插件参数不要以 `ui.` 开头。

### 15.3 会话与消息上的插件字段（只看不用改）

| 字段 | 含义 |
| --- | --- |
| `OriginMountId` | 会话是从哪个入口创建的 |
| `ActiveMountId` | 输入框当前选中的插件 |
| `LastPluginId` | 最后一次实际运行的插件（侧栏图标用它） |
| `PluginMemory` | 上面的记忆键值对 |
| `messages.PluginJson` | 这条回答由哪个插件、用什么参数生成（含段落） |
| `messages.DerivedJson` | 挂在消息下的派生卡片；含 `InputText`（划词输入）与 `IncludeInContext`（是否已加入上下文），默认不进上下文 |

---

## 16. 让 Agent 自己做插件（内置技能）

应用启动时会把一个名为 `plugin-authoring` 的技能包补进技能库
（`%APPDATA%\SDemo\DesktopAgent\skills\plugin-authoring\`），所以「怎么做一个插件」这件事模型自己就知道。

### 16.1 怎么用

- 直接说「帮我做一个润色插件 / 把总结插件改成带语气参数」，或者
- 在输入框输入 `/plugin-authoring` 手选该技能（会全文注入），或者
- 不提技能名也行：输入里出现「插件」「plugin.json」时，它会进入模型的技能索引。

Agent 会先和你确认四件事（目标、挂哪几个入口、参数、输出格式），再写 `plugin.json` 与
`skills/main.md`；能写插件目录时直接建目录，不能写时给你完整文件内容与保存路径。
它**不会**替你点「重新扫描」，写完仍要到「设置 → 插件」扫描并逐条验收。

### 16.2 包里有什么

| 文件 | 内容 |
| --- | --- |
| `SKILL.md` | 工作流、硬规则（哪些写法必然加载失败）、落地文件的方式 |
| `references/declarative.md` | 包结构、加载规则、`plugin.json` 全字段、参数、四条入口、执行限制 |
| `references/skill-file.md` | 插件技能文件的写法、占位符、常见反例 |
| `references/validation.md` | 报错对照表与按现象排查 |
| `references/examples.md` | 润色 / 翻译（`segments`）/ 简单问答（关联上下文）/ 总结四份完整清单与技能 |
| `references/builtin.md` | 在本仓库新增内置插件（C# 类库 / 嵌入资源 / DI 注册 / 测试） |
| `templates/minimal/`、`templates/full/` | 可直接复制、能通过校验的插件模板 |

### 16.3 维护

- 技能源码在 `src/App/SDemo.DesktopAgent.Core/Assets/skills/plugin-authoring/`，
  构建时按 `skills/plugin-authoring/**` 作为嵌入资源打进 Core 程序集。
- 启动时由 `BundledSkillInstaller` 按 `desktop-install.json` 里的版本决定是否重装。
  **改内容必须同时递增 `BundledSkillInstaller.BundledVersion`**，否则已安装的旧副本不会被替换。
- 技能库里已有同名但**不是内置安装**的目录时保持原样：用户手写的同名技能优先。
- 技能里的规则必须与实现一致。改校验规则时同步改 `references/validation.md`，
  并跑 `dotnet test tests/SDemo.DesktopAgent.Core.Tests --filter BundledSkillTests`
  （这些用例会校验模板能通过 `PluginCatalog`，并检查 SKILL.md 引用的文件都存在）。

---

*手册版本：与 M0–M7 实现同步。发现问题或想补充示例，改 `docs/features/plugin-authoring.md` 即可。*
