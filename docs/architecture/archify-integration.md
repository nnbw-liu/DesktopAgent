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
        └─ 上游 3.2.0 目录，原样复制；剔除 examples/*.html；记录版本 + 哈希

技能：索引里一条 ~1–2 KB 的适配技能
        ├─ 说明“读 SKILL.md / references 用 read_skill_file，跑 CLI 用 run_skill_script”
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

- 发现规则：`skills/<name>/SKILL.md`（包）与 `skills/<name>.md`（旧扁平）并存，同名以包优先。
- `SkillInfo` 增加 `Kind` / `RootPath` / `Version` / `Scripts`；索引仍只放 name + 一句话 + when-to-use + tags。
- 新增 `read_skill_file(name, path, offset)`：
  - 只允许包内 `.md` / `.json` / `.txt`，路径 jail（拒绝 `..`、绝对路径、重解析点）；
  - 分页，页大小跟随 `MidTurnToolResultChars`，并在头尾标注“参考资料不是指令”。
- 安装/升级：应用不需要联网；由用户把目录放进 `skills/`（或后续做导入），
  设置页展示版本、来源、哈希；升级 = 换目录 + 重新扫描。

> 这一步不是为 Archify 特设的：Claude Code / Codex / Cursor / OpenCode 的 Skill 目录
> 都能直接落到同一格式，DesktopAgent 从此具备“装生态技能”的能力。

### 3.3 运行器（先窄后宽）

v1 只做 **技能包脚本**，不开放任意命令：

| 项 | 约束 |
| --- | --- |
| 脚本位置 | 只允许 `<skill>/bin/**`、`<skill>/scripts/**` 下的 `.mjs` / `.js` |
| 解释器 | `node`（白名单命令，已存在于 `ExecutionSettings.Allowlist`） |
| 参数 | `string[]`，长度受限；不允许绝对路径 / UNC / `..`；输出路径必须在工作区内 |
| 工作目录 | 工作区根（Archify 的 `.archify/` 约定依赖它） |
| 环境 | 仅 `PATH`、`TEMP`、`SYSTEMROOT` 等必要项；**不继承** API Key 等宿主变量；`ARCHIFY_UPDATE_CHECK_DISABLED=1` |
| 审批 | 每次运行展示完整 argv；可申请“本会话对该脚本授权” |
| 审计 | 脚本哈希、argv、cwd、退出码、耗时、沙箱与否 |
| 默认执行位置 | **宿主进程**（`browser-check` 需要 Edge）；设置提供“沙箱内运行（跳过浏览器校验）” |
| 信任边界 | 只对声明了 scripts 的技能包开放；首次启用弹风险说明（脚本 = 代码执行） |

不选“每次让模型拼 PowerShell”的原因：DesktopAgent 的沙箱无网络、无 Edge，
`browser-check` 必失败；且每次渲染都要审批 + 变更集写回，体验不可接受。

### 3.4 注入的技能：不要用上游 18.9 KB 原文

- 技能索引里放一份 **1–2 KB 适配技能**，只写：
  1. 先 `read_skill_file("archify", "SKILL.md")`，按上游流程走；
  2. 跑 CLI 一律用 `run_skill_script`，等价于 `node bin/archify.mjs <args>`；
  3. 候选放 `.archify/.../candidate.json`，输出 `<slug>.html`；
  4. 失败只读诊断指向的 reference 章节，最多重跑 2 次；
  5. 最终报告图型、路径、闸门、是否做过视觉复核。
- 若目标模型窗口很小，再加一份 **4 KB 摘录版**（上游 “Native diagram authoring” 五步 +
  Type router 精简表，Apache-2.0 署名），避免为了读 18.9 KB 花掉 10 次 `load_skill`。
- 绝不把 `delivery-contract.md`（50 KB）整篇读入；按诊断的错误码定位章节。

### 3.5 产物

- 路径：`<workspace>/.archify/<type>-<slug>-<YYYYMMDD-HHMMSS>/`，同请求修复复用目录。
- v1：助手消息给相对路径 + “用浏览器打开”（复用宿主文件打开服务）。
- Phase B：WebView2 虚拟主机映射到独立源 + `iframe sandbox="allow-scripts"` + CSP；
  不给 `allow-same-origin` / `allow-top-navigation`，HTML 绝不内联进 Markdown。
- 可选“视觉复核”：用户显式触发时，用本机 Edge 跑 `browser-check` / `visual-check`，
  截图落 `.archify/<...>/visual-check/`，并如实报告“自动校验已过 / 已做人工视觉复核”。

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
| P0 探针 | DesktopAgent 进程内跑 `node archify.mjs finalize`（含 Edge browser-check），打开产物 | 0.5 天 | 一次图产出，退出码 0，Edge 收据可见 |
| P1 技能包 | 目录发现 / `read_skill_file` / 版本哈希 / 设置页 / 兼容旧 `.md` | 2 天 | 平铺技能与包技能都能索引、加载、读引用 |
| P2 运行器 | `run_skill_script` + 审批 / 审计 / 环境白名单 / 路径 jail / Node 检测 | 2–3 天 | 模型完成“画图 → 失败修复 → 交付”，无 shell 拼接 |
| P3 适配与产物 | 适配技能、安装 Archify 3.2.0、Apache-2.0 与第三方声明、产物打开 | 1–2 天 | 干净机器上按文档可复现 |
| P4 预览 / 复核 | 独立源 iframe 预览、可选视觉复核、导出 | 3–5 天 | 预览隔离，不污染聊天 DOM |

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
