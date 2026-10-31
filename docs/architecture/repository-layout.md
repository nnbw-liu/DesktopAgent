# 仓库规划（单仓多产品）

> 状态：规划稿，2026-10-31
> 关联：[品牌蓝图](sdemo-brand-blueprint.md) · [总体架构](desktop-shell.md) · [工作台规划](workbench-roadmap.md)
> 前置事实：当前仓库 `SDemo.DesktopAgent`，20 个 src 工程 + 3 个测试工程 + 12 个 tools 控制台，CI 只构建 `SDemo.DesktopAgent.Wpf.slnf`。

---

## 1. 结论

**一个仓库，四个可独立发布的产品。** 不拆多仓。

| 判断 | 结论 |
| --- | --- |
| 仓库数量 | **1 个**（mono-repo） |
| 产品数量 | 4 个可部署物：Agent / Shell / 3 个 App（App 各自独立发布） |
| 工程组织 | 现有 Agent 工程**一个都不移**；新增 `src/Shared`、`src/Shell`、`src/Apps` 三个根 |
| 解决方案入口 | 1 个全量 `.sln` + 5 个 `.slnf`（每个产品一个 + 全量一个） |
| 版本 | 产品级独立版本；跨产品的兼容用 `hostApiVersion` 单独管 |

## 2. 为什么不做多仓

四个理由，按权重排：

1. **契约在一起演进**。`widget.json`、宿主协议、MCP 工具面、URI 规范是四个产品共享的同一份契约。多仓意味着要么发 NuGet 包（每次改字段都要发一轮版本），要么复制粘贴（版本漂移）。**契约漂移是组件生态最常见的死法。**
2. **跨产品改动必须原子**。给 `widget.json` 加一个字段，要同时改宿主解析、应用清单、JSON Schema、文档。单仓一个 PR 搞定；多仓要三个 PR 加人工对齐。
3. **单人维护**。一套 CI、一个 issue 列表、一条发布流程。多仓是三倍的管理开销，换不来任何收益。
4. **构建时间不是瓶颈**。现在全量构建约几十秒级，远没到需要拆仓的规模。

**什么时候才该拆**（触发条件，写下来避免以后凭感觉决策）：

- 单个构建超过 10 分钟且无法靠 slnf 切分缓解；
- 出现第三方贡献者，需要独立的权限与 CODEOWNERS；
- 某个产品要独立授权（不同 License）或独立公开；
- 需要独立私有仓（安全或合规原因）。

在此之前，**"独立发布"靠打包脚本 + 独立版本号实现，不需要独立仓库。**

## 2.5 四个新产品要不要各开一个仓库

逐个判定，不看口号看依赖：

| 产品 | 新开仓库？ | 判定理由 |
| --- | --- | --- |
| **DesktopAssembly**（桌面外壳） | ❌ 绝对不要 | 它是 `widget.json` 与宿主协议的**定义方**。若独立成仓，三个应用要么发 NuGet 包拿契约、要么复制粘贴；每次改协议都要跨仓对齐 |
| **sdemo.planner** | ❌ 不要 | 与 kb / meeting 平级，共用 `Shared.Foundation`、`Shared.Sqlite`、`Shared.Mcp`。独立成仓就要在包里拿到共享层，三份版本漂移 |
| **sdemo.kb** | ❌ 不要 | 同上；且它的检索算法与 Agent 的 `WorkspaceMemoryIndex` 有可复用关系，同仓才能两边对照 |
| **sdemo.meeting** | ❌ 不要（唯一例外见下） | 依赖 whisper 运行时与音频管线，体积最大，但仍与 Shared 强耦合 |

**唯一可能触发独立仓的情形**：`sdemo.meeting` 将来要作为独立产品对外分发（不同 License / 给外部用户单独下载）。即便到那天，也建议只拆这一个，不要一次拆五个。

**反例推演**——假设真按“一产品一仓”拆了，做一件很常见的事：给 `widget.json` 加一个 `opacity.min` 字段。需要：

1. `sdemo-contracts` 仓提交 → 发 NuGet（版本 0.2）→ 等私有 feed 生效；
2. `sdemo-shell` 仓升级包 → 改解析 → 发版；
3. `planner` / `kb` / `meeting` 三仓分别升级包 → 改清单 → 各发一版；
4. `docs` 仓改文档；
5. 四台机器恢复依赖、重跑 CI。

**5 个仓、5 个 PR、1 次包发布、4 次依赖升级。** 同一个改动在单仓里是 **1 个 PR、1 条 CI**。这类改动一个月会发生十几次。

**如果最终还是要拆，最小拆法是 2 个仓，不是 5 个：**

| 仓 | 内容 | 前置条件 |
| --- | --- | --- |
| `sdemo-contracts` | `Shared.*` + 各产品契约 + JSON Schema + 协议文档 | 私有 NuGet feed（GitHub Packages 即可）、语义化版本纪律、下游 `restore` 权限 |
| `sdemo-products` | Shell + 三个 App + Agent | 依赖上面的包，按包版本升级 |

拆的**时机**（任一满足）：出现外部贡献者需分权限 / `meeting` 要独立分发 / 单仓构建超过 10 分钟且 slnf 也救不回来。

拆的**前提**是本方案里的边界纪律已经落实（§5 依赖规则、§8 独立产物、§6 共享准入）。**边界干净的单仓拆成多仓约 1–2 天/产品；边界混乱的单仓拆起来是重写。**

> ⚠️ 一个具体提醒：`sdemo.meeting` 将来会带 whisper 模型（数百 MB）。**模型文件不许进 git**——放 `.gitignore`，首次运行按需下载并校验 sha256。仓库体积一旦被二进制污染，拆仓/克隆/CI 缓存都会长期变慢。

## 3. 目标目录树

```
SDemo.DesktopAgent/                 ← 仓库名暂不改（见 §12 Q1）
├─ .archify/                        ← 图表产物，纳入版本管理
├─ .github/workflows/
│  ├─ ci.yml                        ← 现有，扩为矩阵
│  ├─ contracts.yml                 ← 新增：清单/架构/迁移三类校验
│  └─ release.yml                   ← 新增：tag → 三套产物
├─ Directory.Build.props            ← 已有：公共属性（版本保留在此）
├─ Directory.Packages.props         ← 新增：中央包版本管理（CPM，收敛 62 处内联版本）
├─ global.json                      ← 新增：钉住 SDK（10.0.x + rollForward）
├─ .editorconfig                    ← 新增：与 Nullable/分析器配套
├─ SDemo.DesktopAgent.sln           ← 全量入口（改名与否见 Q1）
├─ docs/architecture/               ← 契约与设计（本目录）
├─ src/
│  ├─ (现有 Agent 工程，物理路径不动)   ← Contracts / Agents / Plugins / Context /
│  │                                    Editing / Execution / Storage / Sandbox /
│  │                                    Tools / App / Platform / Hosts
│  ├─ Shared/                       ← 新增：跨产品基础设施
│  │  ├─ SDemo.Shared.Contracts         ← 跨产品 DTO / 错误码 / URI 规范（零实现）
│  │  ├─ SDemo.Shared.Foundation        ← UUIDv7 / HLC / 内容哈希 / JSON 选项
│  │  ├─ SDemo.Shared.Sqlite            ← 连接工厂 / FTS5 助手 / outbox / 迁移约定
│  │  └─ SDemo.Shared.Mcp               ← MCP server 宿主 + client 封装
│  ├─ Shell/                        ← 新增：DesktopAssembly
│  │  ├─ SDemo.Shell.Contracts          ← widget.json / 宿主协议 / 错误码（纯契约）
│  │  ├─ SDemo.Shell                    ← 注册表 / 进程管家 / 窗口管控
│  │  ├─ SDemo.Shell.Wpf                ← 托盘、面板、组件设置表单
│  │  └─ SDemo.Shell.Host               ← exe（SDemo.Shell.exe）
│  └─ Apps/                         ← 新增：每个应用一组
│     ├─ Planner/  (Contracts / Storage / Core / Mcp / Host)
│     ├─ Kb/       (同上)
│     └─ Meeting/  (同上 + Asr + Audio)
├─ tests/
│  ├─ (现有 3 个测试工程，原地保留)
│  ├─ Shared/                       ← 新增：Foundation / Sqlite / Mcp 的单测
│  ├─ Shell/                        ← 窗口策略、协议、清单校验
│  └─ Apps/Planner|Kb|Meeting/      ← Storage / Core / Mcp 契约快照
├─ tools/                           ← 现有评测工具 + 三个新工具（见 §11）
└─ eval/
```

**关键取舍：现有工程一个都不搬。** 把 20 个 Agent 工程挪到 `src/Agent/` 会让 200+ 处相对引用、`.slnf`、文档链接、CI 路径全部失效，收益只是"目录好看"。改用**解决方案虚拟分组**（§6）在 IDE 里达到同样的观感，成本为零。

## 4. 每个应用的标准模板（5 个工程）

三个应用必须长得一样，否则三个月后各自演化成三种架构。

| 工程 | 职责 | 允许依赖 | 禁止 |
| --- | --- | --- | --- |
| `SDemo.App.<x>.Contracts` | 领域模型、服务接口、MCP 工具 DTO、错误码 | `Shared.Contracts` | 任何实现、EF、UI |
| `SDemo.App.<x>.Storage` | `DbContext`、迁移、仓储、outbox | `Contracts`、`Shared.Sqlite` | 业务规则、MCP |
| `SDemo.App.<x>.Core` | 应用服务：去重、重复规则、草稿流水线、影响面计算 | `Storage`、`Contracts` | Http、UI、进程 |
| `SDemo.App.<x>.Mcp` | MCP server 装配：工具签名 → Core 调用 → 结果信封 | `Core`、`Shared.Mcp` | 领域逻辑 |
| `SDemo.App.<x>.Host` | exe：单实例、内核模式、主窗口（Blazor）、native 组件窗口 | `Core`、`Mcp`、`Shell.Contracts` | 直接写 SQL |

Why 拆开 `.Mcp`：工具面是给模型看的**对外契约**，把它和领域逻辑混在一个工程里，最后一定出现"为了工具方便改了领域模型"。物理隔离是最便宜的约束。

`Host` 里先放 Razor 与组件窗口；只有当 UI 组件超过 ~20 个、或需要 bUnit 测试时才拆 `Ui` 工程。

## 5. 依赖规则（可机检）

```
Shared.Contracts ──► 所有人（只读契约）
Shared.Foundation ──► 应用/股东
     ▲
Shell.Contracts ──► Shell.*、Apps.*.Host
     ▲
App.<x>.Contracts ──► App.<x>.*
```

四条硬规则：

1. **Agent 不依赖任何 `App.*`**（Agent 只通过 MCP 访问它们）；
2. **`App.*` 不依赖 Agent**，也不依赖另一个 `App.*`（零横向引用）；
3. **`Shell` 只依赖 `Shell.Contracts`**，不认识任何具体应用；
4. **`Shared.*` 不得依赖任何产品工程**（只被引用）。

规则 1–4 由新增的 `tools/arch-guard` 在 CI 里检查：扫描所有 `.csproj` 的 `ProjectReference`，建图，命中禁止边就失败并打印"哪条边、被哪条规则禁止"。**不靠人记。**

## 6. 共享代码的准入规则

这是 mono-repo 唯一会真正腐烂的地方，所以先立规矩：

| 层 | 准入 | 反例 |
| --- | --- | --- |
| `Shared.Contracts` | 只有 DTO、接口、枚举、错误码常量；**零依赖、零实现** | 放一个 `Helper` 类 |
| `Shared.Foundation` | 纯算法/基础设施，可独立单测（UUIDv7、HLC、内容哈希） | 依赖 EF 或 Http |
| `Shared.Sqlite` | 连接与索引基建（FTS5 建表、2-gram 分词、outbox 序列化） | 建具体业务表 |
| `Shared.Mcp` | MCP 协议封装 | 认识任何具体工具名 |

三条纪律：

- **禁止 `SDemo.Common` / `SDemo.Utils` / `SDemo.Helpers`**——出现即评审。没有主语的工程必然变成垃圾场。
- **Rule of Three**：同一个类型要被第 3 处使用时才下沉；两处时复制粘贴更便宜，因为两处会分叉演化。
- **下沉必须伴随单测**：进 `Shared.*` 的代码没有测试就不许进。

## 7. 解决方案与 slnf 矩阵

| 文件 | 内容 | 用途 |
| --- | --- | --- |
| `SDemo.DesktopAgent.sln` | 全部工程（虚拟分组：Agent / Shell / Apps / Shared / tests / tools） | 全量构建、IDE 总览 |
| `SDemo.DesktopAgent.Wpf.slnf` | 现有 Agent 桌面端 | **CI 现有门禁，路径不变** |
| `SDemo.Shell.slnf` | Shell 4 工程 + Shared | Shell 独立构建/发布 |
| `SDemo.App.Planner.slnf` | Planner 5 工程 + Shared + Shell.Contracts | 应用独立构建/发布 |
| `SDemo.App.Kb.slnf` / `SDemo.App.Meeting.slnf` | 同上 | 同上 |
| `SDemo.Workbench.slnf` | Shell + 3 App + Shared + Agent Contracts | 跨产品联调构建 |

每个 `.slnf` 必须**自包含可构建**（含它依赖的全部契约与 Shared 工程）——这是"能独立发布"的技术前提。

解决方案虚拟分组用 `dotnet sln` 不好摆，直接在 VS 里拖，或用脚本改 `.sln` 的 solution folder；这一步是**纯展示层**，失败也不影响构建。

## 8. 版本与发布

| 对象 | 版本来源 | 递增时机 |
| --- | --- | --- |
| Agent | `Directory.Build.props` 的 `Version` | 功能发布 |
| Shell | 同一处 `Version`（与 Agent 同批发布时一致） | 功能发布 |
| 每个 App | 各自 `.csproj` 的 `Version` | 各自功能发布 |
| `hostApiVersion` | 独立常量（`1.0`） | **只在破坏兼容时**主版本 +1 |
| `schemaVersion`（manifest / db） | 各清单内常量 | 清单或表结构不兼容变更 |
| MCP `toolsVersion` | 各 App 常量 | 工具签名破坏性变化 |

**关键：产品版本 ≠ 兼容版本。** App 可以从 0.1 升到 0.9，只要没破坏协议，`hostApiVersion` 一直是 1.0。这条分开，才能让三方各自迭代而不互相锁死。

产物（`tools/pack.ps1`）：

```
dist/SDemo-Agent-1.2.0.zip          ← Agent 便携版
dist/SDemo-Shell-1.2.0.zip          ← Shell（含 apps 目录模板）
dist/SDemo-Planner-0.3.0.zip        ← apps/sdemo.planner/（app.json + widget.json + payload + exe）
dist/SDemo-Kb-0.1.0.zip
dist/SDemo-Meeting-0.1.0.zip
```

打包脚本做四件事：`dotnet publish`（RID=win-x64、自包含策略与现有一致）→ 生成/校验清单 → 算 `sha256` 回填 `app.json` → `Compress-Archive`。**sha256 必须由脚本回填**，不能手写——这是宿主启动校验的依据。

## 9. 数据库与迁移边界

| 规则 | 说明 |
| --- | --- |
| 每个应用一个 DbContext、一个迁移目录 | 禁止跨应用共享 DbContext |
| 迁移策略不统一 | Agent 沿用现有 `EnsureCreated` + 补列；**新应用一律用真实 EF migrations**（`EnsureCreated` 无法演进） |
| 迁移在应用启动时执行 | 加应用级互斥锁，保证只有一个进程迁移 |
| CI 检查 | `dotnet ef migrations has-pending-model-changes`（EF 9+）；不支持时退化为模型与快照 diff |
| 数据目录 | `%APPDATA%\SDemo\Apps\<appId>\`；卸载不删 |

Agent 与 App 的迁移策略不同是**故意的**：Agent 已有大量线上库不能重来，新应用没有历史包袱，直接上正规迁移。

## 10. 测试分层

| 层 | 归属 | 内容 |
| --- | --- | --- |
| 单元 | 各工程 | `Shared.Foundation`（HLC/UUIDv7/哈希）、`App.Core`（去重/影响面/outbox） |
| 存储 | `tests/Apps/*.Storage` | 临时 sqlite 文件（不用 InMemory，因为要验 FTS5 与迁移） |
| 契约快照 | `tests/*.Mcp` | 把工具 schema 与 `widget.json` 序列化后比对快照；改工具必须显式更新快照 |
| 协议 | `tests/Shell` | 起一个 fake app server，验握手/鉴权/超时/降级 |
| UI | `bUnit` | 沿用现有套路 |

**契约快照测试是这套东西的安全网**：三个应用 + 宿主 + Agent 之间的所有隐性约定，都靠快照比对暴露"我不小心改了对外形状"。

## 11. 新增工具（`tools/`）

| 工具 | 状态 | 作用 |
| --- | --- | --- |
| `tools/check-repo-guards.ps1` | ✅ 已落地 | 单一入口：体量、入口存在、脏文件、工作区清单、依赖边、契约同改 |
| `tools/check-doc-links.ps1` | ✅ 已落地 | 全部 `.md` 的相对链接必须可解析 |
| `tools/contract-lint` | ⬜ 待做 | 用 JSON Schema 校验 `widget.json` / `app.json` / MCP 工具面 |
| `tools/pack` | ⬜ 待做 | 打包 + 回填 sha256（§8） |
| `tools/new-app`（可选） | ⬜ 待做 | 从模板目录生成新应用骨架，避免第三个应用长歪 |

依赖边检查（原计划的 `arch-guard`）已并入 `check-repo-guards.ps1` 的 `dependency-edges` 一项：扫描所有 `.csproj` 的 `ProjectReference` 解析成路径后按规则判定，`src/Shell` 与 `src/Apps` 建成后自动生效。

## 12. 迁移步骤（每步都能编译、能跑 CI）

| 步 | 内容 | 风险 |
| --- | --- | --- |
| **M0** | 加 `global.json`、`Directory.Packages.props`（收敛 62 处内联版本）、`.editorconfig`；CI 增加缓存键；`.gitignore` 补 `TestResults/` | 低（纯基建） |
| **M1** | 加 `src/Shared/*`（Contracts / Foundation / Sqlite / Mcp），只创建不引用；配单测 | 低 |
| **M2** | 加 `tools/arch-guard`，把 §5 规则跑起来（此时只有 Agent 工程，会全绿） | 低 |
| **M3** | 加 `src/Shell/*` 四工程 + `SDemo.Shell.slnf` + Hello Widget（P1 spike） | **中**：窗口策略的第一次落地 |
| **M4** | 加 `src/Apps/Planner/*` 五工程 + slnf | 中 |
| **M5** | 加 `contracts.yml`（清单/Schema/迁移校验）+ 契约快照测试骨架 | 低 |
| **M6** | 加 `tools/pack` + `release.yml`；跑一次 tag 演练 | 中：产物路径与清单回填 |
| **M7** | Kb / Meeting 按模板复制（用 `new-app` 或手动） | 低 |

顺序原则：**基建先于产品，校验先于扩张。** M0–M2 三天内完成，之后每个产品落地时门槛已经在了。

顺带清理两处历史遗留：`src/SDemo.DesktopAgent.Tools.Todo/`（只剩 `obj` 的空目录，真实工程在 `src/Tools/` 下）应删除；`.tmp/` 与 `TestResults/` 确认在 `.gitignore` 里。

## 13. 风险

| # | 风险 | 缓解 |
| --- | --- | --- |
| R1 | 虚拟分组失效，目录依旧难读 | 接受；物理迁移单独排期，不在功能期做 |
| R2 | CPM 收敛一次性改动 62 处，可能引入版本冲突 | M0 单独一个 PR，只跑构建不改逻辑 |
| R3 | 三个应用复制模板后各自跑偏 | `arch-guard` 管依赖边，契约快照管形状；结构靠模板 |
| R4 | 打包脚本手写 sha256 出错 | sha256 只由脚本生成，CI 校验清单与二进制一致 |
| R5 | 仓库名与内容不符（叫 DesktopAgent 却是 Workbench） | Q1 决定；改名一次性完成，越快越便宜 |

## 14. 待拍板

| # | 议题 | 选项 | 建议 |
| --- | --- | --- | --- |
| Q1 | 仓库/解决方案是否改名 | (a) 现在改成 `SDemo` / `SDemo.Workbench.sln`（b) 保持 `DesktopAgent` 名字 | **(a) 现在改**。此时只有一条 CI、一个克隆，改造成本最低；越往后越贵 |
| Q2 | 三个应用的 exe 命名 | (a) `SDemo.Planner.exe` / `SDemo.Kb.exe` / `SDemo.Meeting.exe`（b) 短名 `Planner.exe` 等 | **(a)**。与互斥量、目录名、清单 id 语义一致 |
| Q3 | 现有 Agent 工程是否物理迁入 `src/Agent/` | (a) 不迁，虚拟分组（b) 迁 | **(a)** 不迁；等有专门的重构窗口再议 |
