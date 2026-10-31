# Agent 工作区规划（让 agent 只读该读的）

> 状态：规划稿，2026-10-31
> 关联：[仓库规划](repository-layout.md) · [品牌蓝图](sdemo-brand-blueprint.md)
> 前提：仓库不拆（单仓多产品），但**喂给 agent 的上下文必须按工作区物理收窄**。

---

## 1. 问题量化

不是感觉，是数字：

| 项 | 体积 | 文件数 | 后果 |
| --- | --- | --- | --- |
| `.tmp/` | **1.65 GB** | **22,592** | 内含整棵 DSH 源码 + `node_modules`；任何全仓 glob / grep 都会淹进来 |
| `.archify/*.html` | 1.5 MB | 2 | 每张 74 万字节的生成物 |
| `TestResults/core.trx` | 841 KB | 1 | 测试日志留在工作区 |
| `docs/` | 1.68 MB | 85 | `architecture` 513 KB / 22 篇，`reports` 394 KB / 40 篇 |
| `README.md` | 66 KB | 1 | ≈2 万 token；多数 agent 把它当第一入口 |

真实代码区（排除 tmp/obj/bin/.vs/node_modules）只有 **14 MB / 1,135 文件**。也就是说**98% 的体积是噪音**。

> **2026-10-31 已清理（W1 + W2）**：`.tmp/`（1.65 GB / 22,592 文件）移至仓外 `F:\AI\Code\SDemo\_scratch\tmp-20261031`，`TestResults/` 移出，二者已进 `.gitignore`；README 66 KB → **5.1 KB**，正文拆成 `docs/guide/` 七篇；三份超大文档进 `docs/architecture/detail/`；reports 基准 JSON 进 `docs/reports/data/`；基准脚本改为写仓外。
> 现状：**1,143 文件 / 13 MB**，全绿构建（0 警告 0 错误），**100 个文档 0 死链**（`tools/check-doc-links.ps1`）。
> 默认读路径代价：`docs/architecture` 从 513 KB 降到 **229 KB**。

按中文 3 字节/字、约 1 token/字估算，一个 agent 开场读 README + 三份架构文档，还没动代码就烧掉 **10 万 token 量级**。这就是"烧 token 很厉害"的来源。

## 2. 四层收窄模型

| 层 | 手段 | 解决什么 |
| --- | --- | --- |
| L1 物理 | 把噪音移出工作区（`.tmp` / `TestResults` / 生成物） | 让 glob、grep、搜索不再命中垃圾 |
| L2 路由 | 根 `AGENTS.md` → 每工作区 `AGENTS.md` | 让 agent 知道"只需读这些"，而不是把全仓读一遍 |
| L3 瘦身 | README 拆分、大文档归档、reports 移位 | 单个必读文件的 token 从 2 万降到 2 千 |
| L4 工具 | `workspaces/*.json` + `tools/ws.ps1` + CI 体积守卫 | 让"只读该读的"变成默认，而不是靠自觉 |

缺任何一层都不成立：只有 L2 会把 agent 指向一个 66KB 的 README；只有 L1 则 agent 仍然不知道产品边界在哪。

## 3. 工作区定义

一级 **6 个工作区**，`agent` 内部再分两个二级区（它已是 20 个工程，仍然最大）。

| id | 覆盖路径 | 构建 | 测试 |
| --- | --- | --- | --- |
| `shared` | `src/Shared/` `tests/Shared/` + `docs/architecture/` 中的**契约文档** | `SDemo.Shared.slnf` | `tests/Shared` |
| `agent-core` | `src/Context` `src/Editing` `src/Execution` `src/Sandbox` `src/Storage` `src/Tools` `src/Agents` `src/Plugins` + `tests/*Core*` `tests/*Sandbox*` | `SDemo.DesktopAgent.Wpf.slnf` | Core / Sandbox |
| `agent-ui` | `src/App/` `src/Hosts/` `src/Platform/` `tests/*UI*` | `SDemo.DesktopAgent.Wpf.slnf` | UI（bUnit） |
| `shell` | `src/Shell/` `tests/Shell/` | `SDemo.Shell.slnf` | `tests/Shell` |
| `planner` | `src/Apps/Planner/` `tests/Apps/Planner/` | `SDemo.Planner.slnf` | 同左 |
| `kb` | `src/Apps/Kb/` `tests/Apps/Kb/` | `SDemo.Kb.slnf` | 同左 |
| `meeting` | `src/Apps/Meeting/` `tests/Apps/Meeting/` | `SDemo.Meeting.slnf` | 同左 |

**跨区依赖怎么处理**：应用工作区可以**只读** `src/Shared/` 与 `src/Shell/Contracts/`（它们要依赖契约），但**不得修改**——想改契约就切到 `shared` 工作区。这条写进每个 `AGENTS.md`，也由 CI 检查（改 `shared` 的 PR 必须带契约文档更新）。

## 4. `AGENTS.md` 规范

三层文件，各司其职，**禁止互相复制**：

| 文件 | 上限 | 只放什么 | 不放什么 |
| --- | --- | --- | --- |
| 根 `AGENTS.md` | ≤ 100 行 | 仓库是什么、产品表、五条铁律、常用命令、禁区、文档地图 | 任何设计细节 |
| `<工作区>/AGENTS.md` | ≤ 120 行 | 本区职责一句话、目录地图（≤15 行）、必读契约（≤3 个链接）、构建/测试命令、本区特有的坑 | 复制设计文档内容 |
| `docs/architecture/*.md` | ≤ 60 KB | 设计本身 | 命令速查 |

**为什么禁止复制**：设计文档会改，`AGENTS.md` 里复制一份就变成双份维护；而且复制的内容每次会话都被全额计费。`AGENTS.md` 只做**指针**。

占位策略：还没建的工作区先放一个 10 行的占位 `AGENTS.md`（写明"未开工，规划见 X"），避免 agent 找不到入口就去翻全仓。

## 5. 机器可读的工作区清单

`workspaces/<id>.json`，给工具和 agent 共用：

```json
{
  "id": "planner",
  "title": "sdemo.planner（待办 + 日历）",
  "status": "planned",
  "paths": ["src/Apps/Planner", "tests/Apps/Planner"],
  "readOnlyPaths": ["src/Shared", "src/Shell/Contracts"],
  "entry": "src/Apps/Planner/AGENTS.md",
  "build": "dotnet build SDemo.Planner.slnf -c Release",
   "test": "dotnet test tests/Apps/Planner -c Release",
  "deny": [".tmp", ".archify", "docs/reports", "TestResults", "obj", "bin"]
}
```

`tools/ws.ps1` 做三件事：

```powershell
ws.ps1 list                 # 列出工作区与状态
ws.ps1 planner              # 打印该区的入口摘要（AGENTS.md 路径 + 命令 + 只读区）
ws.ps1 planner -Sparse      # 可选：git sparse-checkout 到该区（高级用法，见 §7）
```

## 6. 文档瘦身（L3）

| 动作 | 具体 | 省下 |
| --- | --- | --- |
| README 拆分 | `README.md` 压到 ≤8 KB（定位 / 30 秒快速开始 / 目录 / 文档索引）；原章节体拆进 `docs/guide/` | 66 KB → 8 KB（约省 1.8 万 token） |
| 超大文档归档 | `dsh-reasoning-migration.md`(125KB)、`sandbox-development-manual.md`(99KB)、`plugin-system.md`(75KB) 移到 `docs/architecture/detail/`，`AGENTS.md` 标注"仅在改该模块时读" | 默认路径少 300 KB |
| 数据文件移位 | `docs/reports/*.json`（eval 与基准数据，约 200 KB）移到 `docs/reports/data/`，只在查历史结论时按文件名取 | 默认路径少 200 KB |
| 生成物禁读 | `.archify/*.html` 保留（交付物），`AGENTS.md` 明确"要改图请改 `candidate.json` 重跑" | 避免误读 74 万字节 |
| 测试日志出仓 | `TestResults/` 进 `.gitignore`，并删除现有文件 | 841 KB |

## 7. 并行会话：worktree + sparse-checkout

让 agent（"大肥鱼"）在独立目录里跑，**从文件系统层面就看不到别的产品**：

```powershell
git worktree add ..\ws-planner -b agent/planner
cd ..\ws-planner
git sparse-checkout set --cone src/Apps/Planner src/Shared src/Shell/Contracts tests/Apps/Planner docs/architecture
```

好处：

- 物理隔离：`ls` 就只有本区文件，agent 想读别的产品也读不到；
- 并行安全：另一个 agent 在 `ws-kb` 里干活，互不覆盖工作区；
- token 上限由目录结构决定，不靠提示词约束。

代价：切产品要重建 worktree；跨区改动仍需回主工作区。**这是"重型"选项，按需使用，不设为默认。**

## 8. CI 守卫（L4）

实现：`tools/check-repo-guards.ps1`（本地可直接跑，退出码即结果）+ `.github/workflows/contracts.yml`（PR / push master 触发，不需要 .NET，单作业几十秒）。

| 检查 | 规则 | 失败信息 |
| --- | --- | --- |
| 入口存在 | 每个 `workspaces/*.json` 的 `entry` 文件必须存在，`paths` 目录必须存在 | 指出缺哪个工作区入口 / 失效路径 |
| 入口体量 | 根 `AGENTS.md` ≤ 100 行，其它 ≤ 120 行 | 打印实际行数 |
| 文档体量 | `docs/architecture/*.md` ≤ 60 KB（`detail/` 除外）；`docs/guide/*.md` ≤ 30 KB；README ≤ 8 KB | 指出超标文件与拆分建议 |
| 死链 | 全部 `.md` 的相对链接必须可解析（`tools/check-doc-links.ps1`） | 列出文件与目标 |
| 脏文件 | 禁止 `TestResults/`、`.tmp/` 被提交 | 列出被提交的路径 |
| 依赖边 | 产品间零引用：App↔App、App→Agent/Shell、Shell→Agent/App、Agent→App | 打印违规边 `A → B` |
| 契约同改 | 改了 `src/Shared/**` 就必须同时改 `docs/architecture/**` | 列出改动的 Shared 文件 |

依赖边检查是 §5 那条规则的机器化版本。`src/Shell` 与 `src/Apps` 还没建，这条现在恒过；一旦建起来就自动收紧，不需要再改脚本。

## 9. 验收指标

| 指标 | 现状 | 目标 |
| --- | --- | --- |
| 工作区裸体积 | 1.65 GB（含 .tmp） | ≤ 20 MB |
| 全仓文件数（含噪音） | 22,592 | ≤ 1,500 |
| "开门成本"（读入口 + 1 份契约） | ~10 万 token | ≤ 6 千 token |
| 单个必读文档 | 66 KB | ≤ 8 KB |
| 默认 glob 命中数 | 上万 | ≤ 200 |

## 10. 落地步骤

| 步 | 内容 | 工时 |
| --- | --- | --- |
| W0 | 根 `AGENTS.md`（✅ 已建） | — |
| W1 | ✅ **2026-10-31 完成**：`.tmp` 移仓外、`TestResults` 移出并进 `.gitignore`、`.agent/spill/` 忽略。reports 数据移位并入 W2（涉及 8 篇文档 13 处链接） | 0.5 天 |
| W2 | ✅ **2026-10-31 完成**：README 66 KB→5.1 KB + `docs/guide/` 七篇；三份超大文档进 `detail/`；reports 数据进 `data/`（13 处链接已改）；`run-8k.ps1` 改写仓外；新增 `tools/check-doc-links.ps1`（100 文档 0 死链） | 1 天 |
| W3 | `workspaces/*.json` + `tools/ws.ps1` + 已存在区的 `AGENTS.md`（shared / agent-core / agent-ui） | 0.5 天 |
| W4 | `contracts.yml` 守卫 | 0.5 天 |
| W5 | 新建产品时同步建工作区入口（Shell/Planner/Kb/Meeting） | 随产品 |

**W1 是收益最大的一步**：只删/移 `.tmp`，全仓文件数就从 2.2 万降到 1.5 千，任何 agent 的乱逛成本立刻降一个量级。

## 11. 风险

| # | 风险 | 缓解 |
| --- | --- | --- |
| R1 | `AGENTS.md` 与设计文档重复，双份维护 | 规范里禁止；CI 检查入口体量 |
| R2 | 删 `.tmp` 影响正在运行的工具 | 先确认占用，再**移动**到仓外目录，不做直接删除 |
| R3 | 拆了文档但 agent 仍读全仓 | 靠 W1 的物理清场 + 入口里的明确禁令；必要时用 §7 的 worktree |
| R4 | 工作区划分过细，跨区改动变麻烦 | 一级只 6 个；跨区改动回主工作区，不设第二套规则 |
| R5 | 新建产品忘了建入口，agent 四处乱翻 | `contracts.yml` 检查 `workspaces/*.json` 的 entry 存在 |
