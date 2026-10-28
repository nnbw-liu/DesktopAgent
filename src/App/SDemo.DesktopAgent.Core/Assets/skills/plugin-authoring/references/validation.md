# 校验、报错与调试

所有问题都会在「设置 → 插件」里显示**文件 + 行号**或**文件 + JSON 路径**，不会白屏。

## 1. 整个插件不可用（红色「加载失败」）

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

## 2. 只有某个能力不可用（插件仍在列表里）

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

## 3. 只有某个入口不可用

| 报错 | 原因 |
| --- | --- |
| `capability "x" 在本插件里不存在` | `mounts[].capability` 写错 |
| `surface 必须是 sidebar / composer / message / selection` | surface 拼错 |
| `surface selection 只能使用 input selection` | 选中入口写成了别的 input |
| `input selection 只能用于 surface selection` | 在气泡 / 输入框入口上声明了 `input: "selection"` |
| `capability "x" 自身加载失败，这个入口无法使用` | 能力本身有问题，先修能力 |

## 4. 只警告、不影响运行

- `未知字段 "foo"` —— 拼写错误或用了不支持的字段；
- `name 超过 24 个字符，已截断`；
- `icon.paths[i] 含非法字符，已忽略这条 path`；
- `参数 "x" 的 default 不在 options 里`（运行时会回落）；
- `无法识别的占位符 {{…}}`（写法只支持 `{{key}}` 与 `{{key.label}}`）。

## 5. 按现象排查

| 现象 | 排查 |
| --- | --- |
| 列表里没有插件 | 目录层级必须是 `plugins/<文件夹>/plugin.json`；JSON 语法错误看设置页行号 |
| 显示「加载失败」 | 按报错改；最常见是 `mounts[].capability` 或 `skill` 路径写错 |
| chip 出现了但点了没反应 | 没配置模型（去「设置 → 模型设置」）；或参数必填但没默认值 |
| 侧栏入口没了但输入框有 | 检查「输入框」开关是否被关掉（侧栏依赖它） |
| 气泡按钮不见了 | 检查该插件的「气泡」开关与总开关 |
| 划词菜单不出现 | 检查「选中」开关；只支持单条消息正文内的非空选区 |
| 卡片上没有「加入上下文」 | 只有 Done 且有文本的卡片才显示；运行中 / 失败 / 取消的卡片不能加入 |
| 输出格式乱 / 带 `<s>` 字样 | `segments` 能力要检查 skill 是否要求了「先分析再输出」；多余解释会稀释指令 |
| 改了 skill 不生效 | 点「重新扫描」；已打开的会话需要重新选一次插件 |
| 设置改了没反应 | 入口列表与参数在下次渲染时刷新；仍无变化就重新扫描一次 |
| 插件在生成中被删除 | 当前 Run 会继续（内容已在内存），下次刷新消失 |

## 6. 调试技巧

- 先在设置页确认插件的「入口」一行是否四类都有；
- 用一个**最小 skill**（只写一句「把输入原样返回」）排除提示词问题；
- 参数先用 `default` 写死，跑通后再换成 `defaultFrom`；
- 一次只改一个文件（先清单、再 skill、最后参数），避免同时引入多个错误；
- 拿不准时直接对照 `templates/minimal/`，保证清单结构正确后再加能力。
