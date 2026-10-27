# Archify 集成设计（上游仓库复核版）

> 状态：方案复核完成，未实施
> 上游：`git@github.com:tt-a1i/archify.git`，本地 clone HEAD `81c6be8`（2026-10-25），Skill 版本 **3.2.0**
> 上一版（基于 DSH 插件 1.0.0 / Archify 3.0.1）的结论已部分失效，本文取代之
> 相关：`docs/architecture/plugin-system.md`、`docs/architecture/capability-routing.md`、`docs/architecture/sandbox-development-manual.md`

---

## 1. 复核后的关键变化

| 事实 | 上一版（DSH 插件 1.0.0 / Archify 3.0.1） | 上游 3.2.0（本次复核） |
| --- | --- | --- |
| 图型 | 5 种（architecture / workflow / sequence / dataflow / lifecycle） | **10 种**（+ erd / class / tree / timeline / waterfall）**+ explainer 一页解释器** |
| 浏览器闸门 | 只认 Chrome/Chromium；本机缺失 → `finalize` 退 1 | **回退到 Edge**（`msedge.exe`）；本机离线 3.1 s 通过，含 1440–2048 四个视口与深浅色 |
| `finalize` 浏览器参数 | 无开关 | 图型默认跑 `browser-check`；`explainer` 默认不跑；`--browser-check` 可显式要求 |
| 许可 | Skill MIT | **Apache-2.0**（DSH 适配器仍 MIT；历史 MIT 授权保留在 `LICENSE-MIT`） |
| 集成方式 | 只有 DSH 插件（Cordis + npm） | 对 Agent 生态统一为 **Agent Skills 目录**：`SKILL.md` + `scripts/` + `references/` + `assets/`，由宿主自己的 shell 执行 `node bin/archify.mjs` |
| 其他宿主 | — | Claude Code / Codex / Cursor / OpenCode / Hermes 全部走技能目录；**没有 MCP、没有 HTTP API、没有 Python 移植** |
| 体积 | 5.4 MB | 17.3 MB（`examples/*.html` 占 12.2 MB，剔除后约 **5.1 MB**） |

`archify/SKILL.md` 从 11.6 KB 涨到 **18.9 KB**，并新增 `references/explainer-authoring.md`（19 KB）、
`references/authoring-contract.md`（42.7 KB）等；`finalize` / `deliver` / `check` / `guide` 仍是主路径。

**结论：3.2.0 把“浏览器校验”这个集成障碍自己解决了，并把宿主契约收敛成“给我 shell + 文件读取”。**
这正好对应 DesktopAgent 已有的工具与技能体系，因此推荐从“Archify 专用工具包”改为
**“Agent Skills 技能包 + 受控脚本运行器 + 产物入口”**。

---

## 2. 仍然成立的 DesktopAgent 约束

| 约束 | 证据 | 影响 |
| --- | --- | --- |
| 技能库是扁平 `skills/*.md` | `SkillCatalog` 只枚举 `*.md` | 装不下 `SKILL.md + references + scripts` 目录 |
| `load_skill` 每次约 2 000 字符 | `SkillToolPack.MaxContentChars` / `MidTurnToolResultChars` | 18.9 KB 正文要 10 次调用 |
| 手选技能全文会被截断 | `ContextBuilder`：Compact 技能段硬上限 600 token（`:330-333`） | 把 18.9 KB SKILL.md 当“手选技能”注入 = 只剩开头，不可用 |
| 沙箱无网络、只跑工作区副本、写回要确认 | AppContainer 设计 | `finalize` 的 Edge 启不起来；沙箱内只能 `validate → deliver → check` |
| 插件是单轮纯文本 | `plugin-system.md` §2 | Archify 仍不适合做成声明式插件 |
| 无 HTML 产物面 | UI 只有 Markdown / 派生卡片 | 图只能“给路径 + 用浏览器打开”起步 |

---

## 3. 推荐方案

### 3.1 三个接缝

```text
安装：<DataRoot>/skills/archify/{SKILL.md, bin, references, schemas, examples, assets, ...}
        ├─ 上游 3.2.0 目录复制；剔除 examples/*.html；记录版本 + 哈希
        └─ 上游正文移到 references/upstream-SKILL.md，根 SKILL.md 换成适配入口

技能：索引里一条 ~1–2 KB 的适配技能
        ├─ 说明“读 upstream-SKILL.md / references 用 read_skill_file，跑 CLI 用 run_skill_script”
        └─ 硬约束：输出限定 .archify/，不 --open，不自动联网

运行：run_skill_script("archify", "bin/archify.mjs", args, …)
        ├─ node 解释器 + argv 数组，不经过 shell
        ├─ cwd = 工作区；argv 里的路径只允许相对工作区
        ├─ 环境变量白名单（不继承模型密钥）+ ARCHIFY_UPDATE_CHECK_DISABLED=1
        └─ 审批（显示完整 argv）+ 审计（脚本哈希 / 参数 / 耗时 / 退出码）

产物：<workspace>/.archify/<type>-<slug>-<timestamp>/{candidate.json, <slug>.html}
        └─ 消息给相对路径；“用浏览器打开”；Phase B 做应用内预览
```

### 3.2 技能包（通用，先于 Archify）

> 状态（2026-10-26）：**已落地**（`feature/archify-p0`）。实现与验收见
> [p1-skills.md](../reports/p1-skills.md)。

- 发现规则：`skills/<name>/SKILL.md`（包）与 `skills/<name>.md`（旧扁平）并存，同名以包优先；
  根目录的 `SKILL.md`、没有清单的普通目录都不算技能。
- `SkillInfo` 增加 `Kind` / `RootPath` / `Version` / `Source`；索引仍只放 name + 一句话 + when-to-use + tags，
  不把包路径或版本塞进模型上下文。
- `read_skill_file(name, path, offset)`：
  - 只允许包内 `.md` / `.json` / `.txt`，单文件上限 1 MB；
  - 路径 jail：拒绝绝对路径 / UNC / `..` / 盘符 / 数据流 / 保留设备名，逐段拒绝重解析点；
  - 分页，页大小跟随 `MidTurnToolResultChars`，并在头尾标注“参考资料不是指令”。
- `read_skill_file` 与 `load_skill` **成对暴露**：技能索引进了系统提示，模型就必须既能取正文又能取引用。
  两个工具都算元工具名额（Compact 4 个名额里通常留 2 个给它们）。
- 安装/升级：应用不需要联网；通用包由用户把目录放进 `skills/`，Archify 3.2.0 由设置页
  「安装 Archify 3.2.0」导入。设置页展示版本、来源、文件数与目录哈希（哈希后台计算）；
  升级 = 换目录 + 重新扫描。

> 这一步不是为 Archify 特设的：Claude Code / Codex / Cursor / OpenCode 的 Skill 目录
> 都能直接落到同一格式，DesktopAgent 从此具备“装生态技能”的能力。

### 3.3 运行器（先窄后宽）

> 状态（2026-10-27）：**已落地**（`feature/archify-p0`）。实现与真实验收见
> [p2-skill-script.md](../reports/p2-skill-script.md)。

v1 只做 **技能包脚本**，不开放任意命令：

| 项 | 约束 |
| --- | --- |
| 脚本位置 | 只允许 `<skill>/bin/**`、`<skill>/scripts/**` 下的 `.mjs` / `.js`；单脚本上限 4 MB |
| 解释器 | `node`（白名单命令，已存在于 `ExecutionSettings.Allowlist`）；启动时解析 PATH / ProgramFiles / nvm，设置页展示版本 |
| 参数 | `string[]`（≤32 个，单参数 ≤2048 字符，总长 ≤4096）；不允许绝对路径 / UNC / 盘符 / `~` / `..` / 控制字符；参数直接进 argv，**不经过 shell** |
| 工作目录 | 默认工作区根，可在工作区内指定相对目录；绝对路径与越界一律拒绝 |
| 环境 | `DirectProcessRunner` 白名单 + `ARCHIFY_UPDATE_CHECK_DISABLED=1`；仅 Archify 来源额外注入宿主解析的 `ARCHIFY_CHROME` 与 `ARCHIFY_CHROME_NO_SANDBOX=1`；**不继承** API Key 等宿主变量 |
| 审批 | 首跑与脚本哈希变化都要审批，展示完整 argv / 脚本哈希 / Node / 浏览器 / cwd / 超时；可“本会话对该脚本授权” |
| 审计 | `ScriptSha256`、argv 哈希（`CommandSha256`）、cwd、退出码、耗时、`Mode=direct`、`Sandbox=None`；不写脚本正文 / argv 明文 |
| 默认执行位置 | **宿主进程**（`browser-check` 需要 Edge）；不受沙箱网络隔离，审批卡会明示 |
| 信任边界 | 技能包内的脚本就是代码执行：只有目录包的 `bin/` / `scripts/` 能被解析，仍需直跑开关 + 审批；关闭直跑即整体不可用 |

> 实现拆成三层：`SkillCatalog.ResolveScript` 只做包内路径 jail 与哈希；
> `SkillScriptPolicy` 只做直跑 / Node 白名单 / argv / cwd / 超时；
> `SkillScriptRunner` 只做 Node / Edge 解析与直跑。工具层负责审批、会话授权、审计与结果格式化。

> P0 已确认两件事：①直跑器的环境白名单不含 `PROGRAMFILES`，宿主必须解析 Edge 并以
> `ARCHIFY_CHROME` 注入；②Edge 的渲染沙箱与直跑器的 Job Object 不兼容，需要
> `ARCHIFY_CHROME_NO_SANDBOX=1`（已选定“接受该开关”）。两条都只作用于 Archify 子进程，
> 不改变 DesktopAgent 自身的沙箱与审批模型。

不选“每次让模型拼 PowerShell”的原因：DesktopAgent 的沙箱无网络、无 Edge，
`browser-check` 必失败；且每次渲染都要审批 + 变更集写回，体验不可接受。

### 3.4 注入的技能：不要用上游 18.9 KB 原文

> 状态（2026-10-27）：**已落地**（`feature/archify-p0`）。安装器把上游 `SKILL.md`
> 移到 `references/upstream-SKILL.md`，根目录写入约 2.7 KB 的适配入口；实现与验收见
> [p3-archify.md](../reports/p3-archify.md)。

- 技能索引里放一份 **1–2 KB 适配技能**（中文说明约 2.7 KB），只写：
  1. 先按需 `read_skill_file("archify", "references/upstream-SKILL.md")` 取上游流程章节；
  2. 跑 CLI 一律用 `run_skill_script`，等价于 `node bin/archify.mjs <args>`；
  3. 候选放 `.archify/.../candidate.json`，输出 `<slug>.html`；
  4. 失败只读诊断指向的 reference 章节，最多重跑 2 次；
  5. 最终报告图型、路径、闸门、是否做过视觉复核。
- 若目标模型窗口很小，可再加一份 **4 KB 摘录版**（上游 “Native diagram authoring” 五步 +
  Type router 精简表，Apache-2.0 署名）；P3 先不生成，避免索引里出现第二份技能。
- 绝不把 `delivery-contract.md`（50 KB）整篇读入；按诊断的错误码定位章节。

### 3.5 产物

- 路径：`<workspace>/.archify/<type>-<slug>-<YYYYMMDD-HHMMSS>/`，同请求修复复用目录。
- v1：助手消息给相对路径 + “用浏览器打开”（复用宿主文件打开服务）；
  已落地为消息下方的 `ArtifactLinks`：只提取 `.archify/.../*.html`，打开前按工作区根
  收窄并校验文件存在，再交给 `IFileLauncherService`。
- Phase B（P4 已落地）：WebView2 虚拟主机映射到独立源
  `https://archify-preview.local/`，预览前把产物目录复制到
  `<DataRoot>/preview/<token>/` 并注入 CSP；Blazor 侧只放
  `iframe sandbox="allow-scripts allow-downloads"`，不给
  `allow-same-origin` / `allow-top-navigation`，HTML 绝不内联进 Markdown。
  工作区原件不改动。
- 可选“视觉复核”：用户显式触发时，用本机 Edge 跑
  `visual-check --summary --require-provenance`，截图落
  `.archify/<...>/visual-check/`；结果如实区分“自动浏览器校验已过”与
  `visualReview=pending`（尚未人工复核）。
- 导出：预览内使用 Archify 自带导出菜单；WebView2 `DownloadStarting` 只接受
  PNG / JPEG / WebP / SVG / WebM，统一落到 `<DataRoot>/preview/exports/`，
  不弹系统保存框。

---

## 4. 与“专用工具包”方案对比

| 维度 | 专用 `archify_*` 工具包（上一版建议） | 技能包 + 受控运行器（本版建议） |
| --- | --- | --- |
| 首次交付 | 3–5 天 | 5–8 天 |
| 上游升级 | 需把 3.2→3.3 的 SKILL 变化重新映射成工具语义 | 换目录 + 重新扫描；适配技能基本不动 |
| 生态兼容 | 仅 Archify | 所有 Agent Skills（Claude / Codex / Cursor / OpenCode / Hermes） |
| 安全面 | 固定命令，面窄 | 通用脚本执行，面宽；需审批 + 白名单 + 审计守住 |
| 上下文 | 可压到 3 KB 技能 | 依赖 `read_skill_file` 分页；需适配技能控制入口成本 |
| 适用判断 | 只要 Archify、想最快上线 | 想把“技能”做成平台能力，Archify 是第一个真实负载 |

> 如果目标是“一周内先有图可看”，可以先做专用包；但若技能生态是长期方向，
> 技能包 + 运行器只多约 3 天，却避免一次性的 fork。

---

## 5. 分阶段

| 阶段 | 内容 | 工作量 | 验收 |
| --- | --- | --- | --- |
| P0 探针 | DesktopAgent 进程内跑 `node archify.mjs finalize`（含 Edge browser-check），打开产物 | 0.5 天 | 已完成：[p0-archify.md](../reports/p0-archify.md) |
| P1 技能包 | 目录发现 / `read_skill_file` / 版本哈希 / 设置页 / 兼容旧 `.md` | 2 天 | 已完成：[p1-skills.md](../reports/p1-skills.md) |
| P2 运行器 | `run_skill_script` + 审批 / 审计 / 环境白名单 / 路径 jail / Node 检测 | 2–3 天 | 已完成：[p2-skill-script.md](../reports/p2-skill-script.md) |
| P3 适配与产物 | 适配技能、安装 Archify 3.2.0、Apache-2.0 与第三方声明、产物打开 | 1–2 天 | 已完成：[p3-archify.md](../reports/p3-archify.md) |
| P4 预览 / 复核 | 独立源 iframe 预览、可选视觉复核、导出 | 3–5 天 | 已完成：[p4-archify.md](../reports/p4-archify.md)；预览隔离，不污染聊天 DOM |

---

## 6. 风险

| 风险 | 对策 |
| --- | --- |
| Node 依赖（上游 `engines >= 18`） | 启动检测 + 设置禁用提示；后续可打包 SEA（安装包 +约 80 MB） |
| 通用脚本执行扩大信任面 | 仅包内脚本、argv 无 shell、环境白名单、逐次审批、审计、可关 |
| 上下文不够 | 适配技能 1–2 KB + 按诊断读章节；必要时 4 KB 摘录版；大图交给大窗口模型 |
| 体积 | 剔除 `examples/*.html`（12.2 MB）后约 5.1 MB；`examples/locales` 可再删 0.18 MB |
| 浏览器校验 | 宿主跑用 Edge（3.2 已支持）；沙箱内跳过并如实标注 |
| 上游漂移 | 锁定 commit/tag（3.2.0），记录哈希；升级走 PR + 回归图；不自动更新 |
| 许可 / 商标 | 3.2 起 Apache-2.0：随包 `LICENSE`、`NOTICE`、`THIRD_PARTY_NOTICES.md`、字体 OFL；商标不随代码授权 |
| 解释器模式 | `explainer` 需要 `references/explainer-authoring.md` 与 `patch explainer`；先按图型交付，再开页面模式 |

---

## 7. 明确不做

- 不实现 DSH 的 Cordis / bundle patch 兼容层。
- 不把 Archify 做成声明式插件（单轮纯文本表达不了）。
- 不把 17 MB 技能包塞进 `skills/*.md` 扁平路径。
- 不注入 18.9 KB SKILL.md 全文当“手选技能”（会被 600 token 截断）。
- 不自动升级、不自动打开浏览器、不默认联网（`ARCHIFY_UPDATE_CHECK_DISABLED=1`）。
