# 声明式插件参考

> 目标：不写一行 C#，只写 `plugin.json` + Markdown，就能做出一个可用插件。

## 1. 包结构与加载规则

```text
<插件目录>/
├── plugin.json          ← 必需，UTF-8（带不带 BOM 都行）
└── skills/
    └── main.md          ← 能力引用的技能文件
```

| 来源 | 位置 |
| --- | --- |
| 本地 | `%APPDATA%\SDemo\DesktopAgent\plugins\<目录>\`；Android 是 `<AppDataDirectory>/plugins` |
| 内置 | `src/Plugins/SDemo.DesktopAgent.Plugin.<名字>/`（编译进类库，清单与技能是嵌入资源） |

加载规则：

- 只认 `<插件目录>/<任意文件夹>/plugin.json`；目录名不要求等于 `id`（建议一致），
  没有 `plugin.json` 的子目录会被静默跳过。
- 同 `id`：**本地覆盖内置**（内置那条仍显示，标注「已覆盖」）；两个本地同 `id` 时保留第一个，
  另一个标「重复」且不可用。
- 排序：内置在前、本地在后；同来源按 `order` 升序，再按名称。
- 扫描时机：应用启动、设置页「重新扫描」。**没有文件监视**，改完必须重新扫描。

体积上限：`plugin.json` 512 KB、单个 skill 256 KB、图标 path 总长 4 KB（最多 8 条）。

## 2. `plugin.json` 顶层

文件允许 `//` 注释与尾逗号，字段名大小写不敏感；未知字段只警告（设置页会给出 JSON 路径）。

| 字段 | 必填 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- | --- |
| `id` | ✅ | string | — | `^[a-z][a-z0-9-]{1,31}$`，全局唯一 |
| `name` | ✅ | string | — | 显示名，最长 24 字符（超出截断并警告） |
| `version` | | string | `"1.0.0"` | 展示用，不参与比较 |
| `description` | | string | `""` | 设置页里的一句话说明 |
| `icon` | | object | 名字首字 | 见 §6 |
| `minHostVersion` | | string | — | 高于当前应用版本时插件被禁用并提示 |
| `capabilities` | ✅ | array | — | 至少 1 个 |
| `mounts` | ✅ | array | — | 至少 1 个 |

## 3. `capabilities[]`

| 字段 | 必填 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- | --- |
| `id` | ✅ | string | — | 插件内唯一，同样匹配 id 规则 |
| `name` | ✅ | string | — | 展示名 |
| `skill` | ✅ | string | — | 相对插件目录的 `.md` 路径；不允许 `..` 或绝对路径 |
| `allowGlobalSkills` | | bool | `false` | 是否额外注入全局技能（保留字段，当前不生效） |
| `allowHistory` | | bool | `false` | 历史总闸；还要看 `contextParameter` |
| `contextParameter` | | string | — | 控制「是否关联上下文」的 toggle 参数 key；必须同时 `allowHistory: true` |
| `output` | | object | `{"format":"text"}` | `text` 或 `segments`（双语对照） |
| `privacy` | | object | `{}` | `{"sendsText": true}` 时首次使用弹一次确认 |
| `parameters` | | array | `[]` | 见 §5 |

## 4. `mounts[]` 与四条入口

| 字段 | 必填 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- | --- |
| `id` | ✅ | string | — | 插件内唯一 |
| `surface` | ✅ | enum | — | `sidebar` / `composer` / `message` / `selection` |
| `capability` | ✅ | string | — | 必须指向本插件已定义的 capability |
| `input` | | enum | 按 surface 推断 | `composer` / `message` / `selection` |
| `order` | | int | `100` | 升序；同一个 surface 内比较 |
| `show` | | bool | `true` | 默认是否显示；用户设置可覆盖 |

`input` 推断：`sidebar` / `composer` → `composer`；`message` → `message`；`selection` → `selection`。

| 入口 | 点了之后 | 产物 | 上限 |
| --- | --- | --- | --- |
| `sidebar` | **新建一个空会话**，输入框预选该插件 | 之后的发送走插件 Run | `maxInSidebar`，默认 6 |
| `composer` | 选中 chip 后发送 | 一条带插件归属的助手消息（`翻译 · SDemo`） | `maxInComposer`，默认 3 |
| `message` | 对这条助手消息做加工 | 挂在原消息下的派生卡片 | 每条消息最多 5 张 |
| `selection` | 聊天记录里选中文本、鼠标抬起后点浮动菜单 | 以选中文本为输入的派生卡片 | `maxInSelection`，默认 4 |

行为要点：

- 一个能力可以挂多个入口，但能力只定义一次。
- **侧栏依赖输入框**：设置里关掉「输入框」后，侧栏入口一起隐藏。
- 四个入口可独立开关（设置里「输入框 / 侧栏 / 气泡 / 选中」）；划词独立于输入框。
- 气泡 / 划词**不新增对话轮、不改写原消息**；卡片可复制 / 重试 / 删除。
- 派生卡片**默认不进入后续上下文**；卡片上的「加入上下文」由用户显式提升。
- 划词只处理**单条消息正文内**的选区；跨消息、空选区、选中时间 / 按钮都不弹菜单。
- 生成中禁止切换插件；同一会话同一时间只允许一个 Run。

## 5. 参数 `parameters[]`

同一份 schema 被三处消费：输入框内联控件、气泡展开面板、设置页的默认值说明。

| 字段 | 必填 | 类型 | 说明 |
| --- | --- | --- | --- |
| `key` | ✅ | string | `^[a-z][a-zA-Z0-9_]*$`；模板里用 `{{key}}` |
| `label` | ✅ | string | 控件标签 |
| `kind` | ✅ | enum | `select` / `multiselect` / `toggle` / `text` / `number` |
| `options` | `select` / `multiselect` 必填 | array | `[{"value":"ja","label":"日本語"}]` |
| `default` | | string | 字面默认值 |
| `defaultFrom` | | string | `system-language` 或 `system-locale` |
| `fallback` | | string | 动态默认值解析不出来时的兜底 |
| `required` | | bool | 默认 `true` |
| `placeholder` | | string | `text` / `number` 的水印 |
| `maxLength` | | int | `text` 最大长度，默认 2000 |

默认值解析顺序：

```text
会话记忆值 → default → defaultFrom 解析结果 → fallback → 第一个 option
```

- 记忆值或 `default` 不在 `options` 里时视为「没有」，继续往后回落；
- `select` 最终一定落在 `options` 里（最差是第一个）；`text` / `number` 没有可用值时为空字符串；
- 记忆键是 `<pluginId>/<mountId>/<key>`，`ui.` 前缀保留给宿主，插件参数不要以 `ui.` 开头。

`defaultFrom: "system-language"` 的映射：`zh-CN` / `zh-SG` / `zh-Hans-*` → `zh-Hans`；
`zh-TW` / `zh-HK` / `zh-MO` / `zh-Hant-*` → `zh-Hant`；`ja*` → `ja`；`fr*` → `fr`；`ko*` → `ko`；
其它（含 `en*`）→ `en`。`system-locale` 直接返回区域名（如 `zh-CN`）。

`multiselect` 的 schema 能通过校验，**但界面显示「多选暂不支持」**，优先用 `select` 或 `toggle`。

### 关联上下文开关

```json
"allowHistory": true,
"contextParameter": "useContext",
"parameters": [
  { "key": "useContext", "label": "关联上下文", "kind": "toggle", "default": "true" }
]
```

- `allowHistory` 是总闸：没有它，`contextParameter` 不生效（校验报错）。
- `contextParameter` 必须是本能力里已定义的 `toggle` 参数。
- 参数值为 `false` 时这一轮不带历史；缺省或非法值按「打开」处理。
- 历史只在入口本身允许时才会带（见 §8）。

## 6. 图标

```json
"icon": {
  "paths": ["M3.5 5h6M6.5 3.2v1.8M9.5 5c0 3.6-2.2 6-5 7.4"],
  "text": "译"
}
```

- **只接受 SVG 的 `path` 数据**（`d` 属性），不接受整段 `<svg>` 标记。
- 字符白名单：`MmLlHhVvCcSsQqTtAaZz`、数字、`.`、`,`、`-`、空白；非法 path 会被丢弃并警告。
- 最多 8 条、总长 ≤ 4 KB；超限丢弃图标。全部无效且没有 `text` 时回退到插件名首字。
- `text` 建议 1 个字。

## 7. 输出格式

### `text`（默认）

模型返回什么就显示什么，按 Markdown 渲染。

### `segments`（双语对照）

能力里声明：

```json
"output": { "format": "segments" }
```

宿主会**自动**在 system 提示词末尾追加格式契约（`<s>原文</s>` / `<t>译文</t>` 逐段输出），
**不要**在 skill 里写这些标签或分段说明。解析规则（宿主实现，了解即可）：

1. 按出现顺序把 `<s>` / `<t>` 配成段落；流式中未闭合的尾部标签先丢弃；
2. 数量不等时以较少的为准，多出来的 `<t>` 并入最后一段译文；
3. 一条完整配对都没有 → 降级为纯译文，卡片提示「本次结果未按段落返回」，对照开关禁用；
4. 超长结果截断到 32,000 字符。

`segments` 只适合「逐段原文 + 译文」的能力（翻译、对照改写）；润色、总结这类只输出一份结果的用 `text`。

## 8. 执行模型与限制

```text
参数取值 → 渲染 skill（system）→ 包装输入（user）→ 单轮调用 → 产物
```

| 项 | 行为 |
| --- | --- |
| 轮数 | 单轮，不自动多轮 |
| 历史 | 仅 `allowHistory: true` 且「关联上下文」为开时带最近 10 条 / ≤24,000 字符 |
| 工具 | v1 插件**不支持**工具 / 函数调用，插件只产出文本 |
| 派生内容 | 默认不进上下文；用户点「加入上下文」后从下一轮开始生效 |
| 全局技能 | 插件模式不注入（`allowGlobalSkills` 保留字段，当前不生效） |
| 输入长度 | 单次 ≤60,000 字符；超出直接报错（长文本分块尚未实现），不会偷偷截断 |
| 输出长度 | 卡片 / 消息正文超过 32,000 字符截断并提示 |
| 取消 | 用输入框的停止按钮取消；已生成部分保留，状态标「已取消」 |
| 模型 | 用当前会话选中的 provider + model；插件不能指定模型或温度 |

## 9. 安全与隐私

| 面 | 规则 |
| --- | --- |
| skill 路径 | 必须位于插件目录内、以 `.md` 结尾；`..` 与绝对路径直接拒绝 |
| 图标 | 只收 `path` 数据 + 字符白名单，杜绝 `<script>` / 事件属性 |
| 提示词注入 | skill 只进 system，用户文本只进 user 并定界，`<` 全角转义 |
| 参数值 | 只做模板替换，不参与路径 / 文件名 / SQL |
| 体积 | 清单 512 KB、skill 256 KB、图标 4 KB，超限拒绝 |
| 隐私 | `privacy.sendsText: true` 时首次执行前弹一次确认（可取消） |
| 分发 | v1 只支持本地目录安装，没有签名与市场；**只安装你信任的插件** |

## 10. 设置与优先级

「设置 → 插件」的全局项：启用插件、输入框 / 侧栏 / 划词菜单最多显示数、
记住每个会话上次选中的参数、新会话译文默认呈现。每个插件另有：启用、输入框、侧栏、气泡、选中、顺序。

优先级：**用户显式设置 > manifest 的 `show` / `order` > 内置默认**。
插件停用后，已打开的会话会提示「当前不可用（已停用或被删除）」并回到普通对话。
