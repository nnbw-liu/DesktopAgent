# P2 报告：`run_skill_script` 与 Archify 真实验收

> 日期：2026-10-27
> 分支：`feature/archify-p0`
> 状态：**完成**
> 前置：[P0 探针](p0-archify.md) · [P1 技能包](p1-skills.md) · 设计：[archify-integration.md](../architecture/archify-integration.md)

---

## 1. 交付内容

| 能力 | 实现 |
| --- | --- |
| 脚本解析 | `SkillCatalog.ResolveScript` 只允许目录包 `bin/`、`scripts/` 下的 `.mjs` / `.js`，单脚本上限 4 MB |
| 路径安全 | 包内相对路径 jail；拒绝绝对路径 / UNC / 盘符 / `..` / 控制字符；逐段拒绝重解析点 |
| 参数策略 | `SkillScriptPolicy`：≤32 个参数、单参数 ≤2048、总长 ≤4096、拒绝绝对路径与 `..`、cwd 在工作区内 |
| 直跑门禁 | 必须打开 `DirectRunEnabled` 且 `Mode == AllowlistDirect`；`node` 必须在 `ExecutionSettings.Allowlist` |
| Node / Edge | Windows 执行器解析 PATH / ProgramFiles / nvm；Edge / Chrome 解析后只对 Archify 注入 |
| 环境 | `ARCHIFY_UPDATE_CHECK_DISABLED=1`；Archify 额外 `ARCHIFY_CHROME`、`ARCHIFY_CHROME_NO_SANDBOX=1`；不继承宿主 API Key |
| 审批 | 首次运行与脚本哈希变化都请求审批；展示完整 argv / 哈希 / Node / 浏览器 / cwd / 超时；可本会话授权 |
| 会话授权 | key = `skill-script:<skill>:<relative>:<sha256>`，脚本变化自动重新审批 |
| 审计 | `ScriptSha256`、`CommandSha256`、cwd、退出码、耗时、`Mode=direct`、`Sandbox=None`；不写正文 / argv 明文 |
| 失败语义 | 非零退出码的工具结果以“执行失败”开头，trace 标 error；stdout / stderr 原样带回 |
| 工具暴露 | 新增 `ToolTaskProfile.Skill`；技能意图 / 手选技能时把 `run_skill_script` 放到可用名额最前 |
| UI | 审批卡风险说明、工具卡 Terminal 映射、助手时间线脚本摘要、技能 / 工具设置页 Node 状态 |

---

## 2. 关键代码

- `src/Contracts/SDemo.DesktopAgent.Contracts/Services/ISkillScriptRunner.cs`
  - `SkillScriptRequest`、`SkillScriptHostInfo`、`SkillScriptRunResult`、`ISkillScriptRunner`。
- `src/Contracts/SDemo.DesktopAgent.Contracts/Services/ISkillLibrary.cs`
  - `SkillScriptStatus`、`SkillScriptTarget`、`SkillScriptResolution`、`ResolveScript`。
- `src/App/SDemo.DesktopAgent.Core/Services/SkillCatalog.cs`
  - `ResolveScript`、共享的包内路径 jail、SHA-256。
- `src/Execution/SDemo.DesktopAgent.Execution/SkillScriptPolicy.cs`
  - 直跑 / 白名单 / argv / cwd / 超时。
- `src/Execution/SDemo.DesktopAgent.Execution.Windows/SkillScriptRunner.cs`
  - Node / Edge 检测、环境注入、`IDirectProcessRunner` 直跑。
- `src/Tools/SDemo.DesktopAgent.Tools.Skills/SkillToolPack.cs`
  - `run_skill_script`：审批、会话授权、审计、输出格式化。
- `src/Tools/SDemo.DesktopAgent.Tools.Abstractions/CapabilityRouter.cs`
  - `RunSkillScriptTool`、skill 意图、`ToolTaskProfile.Skill` 优先位。
- `src/App/SDemo.DesktopAgent.UI/...`
  - `SkillsSettings.razor` 显示 Node / 浏览器状态；`ToolsSettings.razor` 直跑说明覆盖技能脚本。
- 探针：`tools/p2-archify/Program.cs`；报告：`.tmp/p2-workspace/p2-probe-report.json`。

---

## 3. 行为说明

### 3.1 一次运行经过哪些门

```text
run_skill_script(skill, script, args, cwd, timeout)
  → SkillCatalog.ResolveScript        包内路径 + 扩展名 + 4MB + SHA-256
  → SkillToolPack                     模型禁用包检查
  → SkillScriptPolicy                 直跑开关 / node 白名单 / argv / cwd / 超时
  → SkillScriptHostInfo               没有 Node 就直接给可恢复提示，不弹审批
  → 审批（首跑 / 哈希变化）            会话授权 key 含脚本哈希
  → SkillScriptRunner                 node <绝对脚本> <argv...>，cwd=工作区
  → DirectProcessRunner               Job Object、超时、输出截断
  → 工具结果 + JSONL 审计
```

脚本路径由宿主解析后传给 Node；模型始终只传 `skill` + 包内相对路径，**拿不到任意绝对路径入口**。

### 3.2 为什么参数不允许绝对路径

Archify 只需要 `finalize architecture candidate.json output.html --quality showcase --json` 这类相对路径。
禁止绝对路径 / UNC / 盘符 / `..` 后：

- 脚本不能借 argv 读写工作区外文件；
- 输出仍由 cwd 决定，落在工作区内；
- 不用 shell 转义，argv 原样传给 Node。

### 3.3 审批与审计

- 首跑弹审批；用户选“本会话授权”后，同一脚本哈希不再弹。
- 脚本变化（SHA-256 变）会重新弹，避免“批过一次旧脚本，新脚本静默执行”。
- 审计只写哈希，不写脚本正文 / argv 明文；`ScriptPreview` 仍受 `RetainScriptsInAuditLog` 控制。

### 3.4 环境

- 所有技能脚本：`ARCHIFY_UPDATE_CHECK_DISABLED=1`（不影响非 Archify 脚本）。
- Archify 来源（skill 名或 source 含 `archify`）：额外注入
  - `ARCHIFY_CHROME`：宿主解析到的 Edge / Chrome；
  - `ARCHIFY_CHROME_NO_SANDBOX=1`：P0 已确认 Edge 渲染进程与直跑 Job Object 不兼容。
- 不注入 `PATH` 之外的宿主秘密；`DirectProcessRunner` 的白名单本身也不含 API Key。

---

## 4. 真实 Archify 验收

探针：`tools/p2-archify`；技能根：`.tmp/archify-repo`（内含 `archify/` 目录包）；工作区：`.tmp/p2-workspace`。

环境：

- Node `v24.21.0`（`C:\Program Files\nodejs\node.exe`）
- Edge `C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`
- Archify `bin/archify.mjs`：288,781 B，sha256 `3cebcee3fa339e3b98deacae8cb9a5570525e003d8823368d304d904ba9ceecd`

流程：坏候选（`components: []`）→ `finalize` 失败 → 修复候选 → 重跑 → 交付。
探针按模型会走的调用序列驱动工具（两次 `run_skill_script` + 中间修候选），没有接真实模型；
这样验收的是执行链路、审批、审计与 Archify 本身，而不是某次模型输出。

| 阶段 | 退出码 | 状态 | 证据 |
| --- | --- | --- | --- |
| 坏候选 | 1 | `fail` | `failedStage=validate`，`diagnostics[0].code=schema/minItems`，463 ms |
| 修复后 | 0 | `pass` | validate / deliver / check / browser-check 全 pass，3,967 ms |

产物：`p2.html`，846,230 B，sha256 `c1258e24c71a62ce137f769043eedd089533f8e1a8c4bd5c6f06ce59ad24a4d5`；
`out/review-2/p2.browser-check.json` 存在且 `ok=true`。

审批 / 审计：

- 审批请求 1 次；用户会话授权 1 条；第二次运行未再弹审批。
- 审计 2 条：`Mode=direct`、`WorkingDirectory=.`、脚本哈希均为
  `sha256:3cebcee3…`，`CommandSha256` 两次不同（argv 不同）。

> 完整机器可读证据：`.tmp/p2-workspace/p2-probe-report.json`（未入库，按需重跑探针）。

---

## 5. 测试

| 套件 | 结果 |
| --- | --- |
| `SDemo.DesktopAgent.Core.Tests` | **915 通过**（新增脚本解析 / 策略 / 工具审批 / 路由用例） |
| `SDemo.DesktopAgent.UI.Tests` | **136 通过**（技能设置注入执行器、Terminal 映射） |
| `SDemo.DesktopAgent.Sandbox.Tests` | **219 通过，1 跳过**（跳过项为既有 AppContainer 用例） |

真实探针另行验证；无 mock 执行器参与最终交付链路。

---

## 6. 限制与后续

- **Node 必须存在**：启动时检测；没有 Node 时 `run_skill_script` 给可恢复提示，不弹审批。
- **宿主直跑不隔离网络**：审批卡已明示；Archify 更新检查关闭，但脚本本身仍可联网。
- **没有逐技能开关**：当前由直跑总开关 + 脚本审批控制；P3 做安装器时再加“该技能允许脚本”的显式开关。
- **P3**：把 Archify 3.2.0 安装到 `skills/`、补 Apache-2.0 / 第三方声明、产物打开与干净机器复现。
