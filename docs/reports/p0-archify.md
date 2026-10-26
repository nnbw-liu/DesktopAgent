# P0 探针报告：Archify 3.2.0 接入 DesktopAgent

> 日期：2026-10-26
> 分支：`feature/archify-p0`
> 状态：**通过（带一个待决策项）**
> 设计依据：[archify-integration.md](../architecture/archify-integration.md)
> 探针代码：`tools/p0-archify/`（`dotnet run --project tools/p0-archify -c Release`）

---

## 1. 目的与验收

P0 只回答一个问题：**DesktopAgent 的生产直跑链能不能承载 Archify 3.2.0，并让 Edge 真正跑完 `browser-check`？**

验收标准（来自设计文档 P0）：

- [x] 在 .NET 侧用 `DirectProcessRunner` 跑 `node bin/archify.mjs finalize …`
- [x] 固定 argv、工作区 cwd、120 s 超时、输出上限
- [x] 四道闸门 `validate / deliver / check / browser-check` 全部 pass
- [x] 产物 HTML 落盘、sha256 与收据一致
- [x] 用宿主文件打开服务打开产物

---

## 2. 环境

| 项 | 值 |
| --- | --- |
| OS | Windows 10.0.19045（`Environment.OSVersion`） |
| .NET | net10.0-windows10.0.19041.0 |
| Node | v24.21.0（`node` 在 PATH，直跑器解析） |
| 浏览器 | Microsoft Edge 155.0.4283.62（`C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`） |
| Archify | 上游 `81c6be8`，Skill 3.2.0，`skill-release.json` 记录版本 |
| 安装布局 | `%LOCALAPPDATA%\SDemo\DesktopAgent\archify\3.2.0`（按设计） |
| 运行时体积 | 191 个文件 / **5,341,294 字节**（已剔除 `examples/*.html`，源目录 17.3 MB） |
| 工作区 | `.tmp/p0-workspace`（`.tmp/` 已被忽略） |

---

## 3. 探针做了什么

1. 把上游 `archify/` 目录按安装布局复制到 `%LOCALAPPDATA%`，剔除 `examples/*.html`（12.2 MB），记录版本与 `bin/archify.mjs` 的 sha256。
2. 在工作区按 SKILL.md 约定建 `.archify/architecture-web-app-<时间戳>/`，用上游 `examples/web-app.architecture.json` 当候选，并把 `meta.output` 改成相对输出路径。
3. 用**生产代码** `DirectProcessRunner`（无 shell、argv 数组、环境白名单、Job Object、超时终止）执行：

   ```text
   node <install>\bin\archify.mjs finalize architecture <candidate> <output> --quality showcase --json
   ```

   cwd = 工作区；超时 120 s；stdout 上限 400 000 字符。
4. 解析 finalize 收据：闸门、产物路径 / 大小 / sha256、`browser-check` 侧车里的浏览器可执行文件、诊断、耗时。
5. 校验产物存在且 sha256 一致；用生产 `ShellFileLauncher` 打开 HTML。
6. 报告写到 `<workspace>/.archify-p0/report-<时间戳>.json|md`。

> 探针不在 WPF 进程内，但用的是同一份 `DirectProcessRunner` 与文件打开服务；
> 与 WebView2 / Blazor 无关的进程、路径、超时、浏览器链都在这里覆盖。

---

## 4. 结果

| # | 配置 | 退出码 | 耗时 | `browser-check` | 浏览器证据 |
| --- | --- | --- | --- | --- | --- |
| A | 直跑器原样（环境白名单） | 2 | 1 525 ms | **skipped** | unavailable |
| B | + 注入 `ARCHIFY_CHROME`（Edge 路径） | 1 | 18 321 ms | **fail** | available，CDP `Page.enable` 超时 15 s |
| C | + `ARCHIFY_CHROME_NO_SANDBOX=1` | **0** | **3 493 ms** | **pass** | available，Edge 155 |
| C' | C 的重复运行（验证稳定性） | 0 | 3 674 / 3 685 / 3 771 ms | pass | available |

C 的收据（`report-20261026-165712.json`；生产 Job Object 配置：16 进程 / 2 GB / UI 限制）：

```text
gates      validate=pass deliver=pass check=pass browser-check=pass
artifact   .archify/architecture-web-app-20261026-165708/web-app.html
           846 197 bytes, sha256 与收据一致
手选打开   True（ShellFileLauncher → 系统浏览器）
```

C 的 `browser-check` 侧车还包含 1440×900 / 1600×1000 / 1920×1080 等视口、深浅色、
`readerLayout`、`minimumProjectedNodeTextPx`、`viewerChromeOk` 等证据；这些字段说明
Edge 里的检查是真实执行的，不是“跳过”。

**基准对比**：只跑 `validate/deliver/check`（A）约 1.5 s；加上 Edge 浏览器检查（C）约 3.5 s。
也就是说，把完整质量闸门留在流程里，代价约 2 秒。

---

## 5. 探到的两个真实问题

### 5.1 直跑器的环境白名单会把 `PROGRAMFILES` 清掉

`DirectProcessRunner.ApplyEnvironment` 只保留 `PATH / TEMP / APPDATA / LOCALAPPDATA / SystemRoot …`
等少量变量。Archify 的 `findChrome()` 依次看：

1. `ARCHIFY_CHROME`
2. `PROGRAMFILES` / `PROGRAMFILES(X86)` / `LOCALAPPDATA` 下的 Chrome / Chromium
3. PATH 里的 `chrome` / `chromium`（Edge 在本机不在 PATH）

而 3.2 的 Edge 回退需要的 `PROGRAMFILES(X86)` 被白名单清掉了，于是 A 直接
`chrome/unavailable`，`finalize` 以退出码 2 结束。

**处理**：给 `IDirectProcessRunner.RunAsync` 增加一个可选的宿主环境注入参数
（`IReadOnlyDictionary<string,string>? environment`，放在末尾，不传则行为不变）。
宿主要先把 Edge 解析出来，再以 `ARCHIFY_CHROME` 注入；模型接触不到这个参数。
实现与假实现（`tools/routing-eval`、`CommandSandbox.Tests`）已同步。

`ARCHIFY_UPDATE_CHECK_DISABLED=1` 也走同一条通道：探针不联网，收据里不再出现 GitHub 更新检查。

### 5.2 Edge 在直跑器的 Job Object 里起不来渲染进程

只注入 `ARCHIFY_CHROME`（B）后，Edge 能启动、CDP 能握手，但 `Page.enable` 15 秒超时，
进程仍在、没有渲染。逐步排查：

| 尝试 | 结果 |
| --- | --- |
| 把 Job 限额从 16 进程 / 2 GB 提到 64 / 4 GB | 仍失败（说明与限额无关） |
| 去掉 Job 的 UI 限制 | 仍失败 |
| 只保留 `KILL_ON_JOB_CLOSE`、去掉所有资源限额 | 仍失败 |
| 用同样的“剥掉环境变量”方式在 Job 外直接跑 `browser-check` | **通过**（说明环境变量也不是根因） |
| 保留原 Job 配置，给 Edge 加 `--no-sandbox`（`ARCHIFY_CHROME_NO_SANDBOX=1`） | **通过** |

结论：**Chromium/Edge 的渲染沙箱与外部 Job Object 不兼容**；这是 Chromium 的已知行为类别，
不是 DesktopAgent 限额问题。最后一次 C 运行是在**恢复生产 Job 配置（16 进程 / 2 GB / UI 限制）并做干净重建**之后跑的，
四道闸门仍全部通过，因此限额无需调整。

可选的 P1 决策（见 §6）：

1. 接受 `ARCHIFY_CHROME_NO_SANDBOX=1`：最简单，保留 Job Object 的整树终止；浏览器侧失去渲染进程沙箱。
2. `browser-check` 单独放到 Job 外跑：保留浏览器沙箱，但要新增“不受 Job 约束的短进程”通道，安全模型变复杂。
3. 不跑 `browser-check`：最保守，但丢掉 3.2 最有价值的真实浏览器证据。

> 探针当前选 1，并在代码里注明“P1 决策项”。无论选哪个，`validate/deliver/check` 都不受影响。

---

## 6. 对 P1 的直接影响

| P0 结论 | P1 要落的东西 |
| --- | --- |
| 宿主必须解析浏览器并注入 `ARCHIFY_CHROME` | 技能包/运行器统一提供“宿主环境注入”，不向模型暴露；设置里可覆盖路径 |
| `ARCHIFY_UPDATE_CHECK_DISABLED=1` 有效 | 运行器默认关闭更新检查；联网必须由用户显式开启 |
| 运行时 5.34 MB / 191 文件 | 安装/校验/版本哈希可以实现；剔除示例 HTML 的裁剪规则固定下来 |
| 3.5 s 完成“渲染 + 四视口浏览器检查” | 交互上可接受；P1 不需要为性能做特殊优化 |
| 关闭更新检查后仍有 `visualReviewRecommendation` | 技能应区分“自动闸门通过”与“未做人工视觉复核”，不要混报 |
| `finalize` 的 `meta.output` 必须与命令行输出一致 | 运行器要约束输出路径在工作区 `.archify/` 内，并写入候选前校验 |
| 探针不在 WPF 进程内 | P1 可在真实会话里做一次端到端冒烟；进程层面的风险已经排除 |

---

## 7. 复现

```powershell
# 上游 clone（已有可跳过）
git clone --depth 1 git@github.com:tt-a1i/archify.git .tmp/archify-repo

# 跑探针（默认安装、工作区、Edge 自动探测；--no-open 不弹浏览器）
dotnet run --project tools/p0-archify -c Release -- --no-open

# 只看这次报告
Get-Content .tmp/p0-workspace/.archify-p0/report-*.md
```

探针可重复执行：每次新建 `.archify/architecture-web-app-<时间戳>/`，旧产物不覆盖。

---

## 8. 回归

- 全解决方案 `dotnet build -c Release`：0 错误。
- 全解决方案 `dotnet test -c Release`：Core 876、UI 133、Sandbox 219 通过，1 个 AppContainer 用例在非交互环境跳过。
- 接口变更只新增可选参数，所有既有调用点（`CommandToolPack`、`WorkspaceWarmupService`、
  `VerificationRunner`）无需修改；两个假实现已同步签名。

---

## 9. 结论

**P0 通过。** 生产直跑链可以完成 Archify 3.2.0 的完整交付，包括真实 Edge 浏览器检查，
单次约 3.5 s，产物与收据 sha256 一致，文件打开服务可用。

需要 P1 拍板的只有一件事：**Edge 的 `--no-sandbox` 取舍**。
其余结论都已转成 P1 的具体约束（§6），没有阻塞项。
