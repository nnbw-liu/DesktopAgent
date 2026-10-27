# P3：Archify 3.2.0 适配、安装与产物打开

> 分支：`feature/archify-p0` · 日期：2026-10-27 · 上游：`81c6be8d87090670c45d08a8f97f9d29c5af0bec`
> 设计：[archify-integration.md](../architecture/archify-integration.md) §3.4 / §3.5 / §5

## 1. 结论

P3 把 Archify 从“能用探针跑”推进到“装进技能库、按适配入口用、产物可打开”：

- 安装器把上游 3.2.0 包复制到 `<DataRoot>/skills/archify/`，剔除 `examples/*.html`，
  保留 `bin/`、`references/`、`schemas/`、`examples/*.json`、`assets/`、`LICENSE`、
  `NOTICE`、`LICENSE-MIT`、`JetBrainsMono-OFL.txt`、`THIRD_PARTY_NOTICES.md`。
- 上游 18.9 KB 的 `SKILL.md` 原样移到 `references/upstream-SKILL.md`；根目录写入
  DesktopAgent 适配入口（约 2.7 KB），索引仍只有一条 `archify` 技能。
- 设置页新增「安装 Archify 3.2.0」：选仓库根或 `archify/` 目录，安装后重新扫描；
  失败只给可恢复的一句话，不抛异常。
- 助手消息里的 `.archify/.../*.html` 自动出现「用浏览器打开」按钮；打开前按工作区根
  收窄到 `.archify/` 并校验文件存在，再交给宿主的 `IFileLauncherService`。
- 从**已安装**的技能包重跑 P2 验收，仍以 `status=pass` 产出 846,230 B 的 HTML；
  证明安装布局、`run_skill_script` 与产物路径能串起来。

## 2. 安装布局

```text
<DataRoot>/skills/archify/
  SKILL.md                         # DesktopAgent 适配入口（约 2.7 KB）
  desktop-install.json             # 上游版本、适配版本、来源、安装时间、文件数/字节
  skill-release.json               # 上游版本 3.2.0（catalog 读版本/来源）
  bin/archify.mjs                  # 288,781 B，sha256 3cebcee3…
  references/
    upstream-SKILL.md              # 上游 SKILL.md，18,877 B
    …
  schemas/  assets/  examples/*.json
  LICENSE  NOTICE  LICENSE-MIT  THIRD_PARTY_NOTICES.md  assets/JetBrainsMono-OFL.txt
```

示例 HTML 占约 12.2 MB，安装时剔除；实装 **192 个文件、5.1 MB**。目录哈希仍由
技能设置页后台计算，首次扫描大包时不会卡 UI。

**适配入口的边界**：

- CLI 一律 `run_skill_script("archify", "bin/archify.mjs", args)`；`args` 只允许相对
  工作区路径，不拼 shell。
- 输出限定 `.archify/<type>-<slug>-<timestamp>/`；不 `--open`，不自动联网；宿主注入
  `ARCHIFY_UPDATE_CHECK_DISABLED=1`。
- 失败按 stdout 的 `failedStage` / `diagnostics[].code` 读对应 reference，最多重跑 2 次；
  不以非零退出码冒充成功。
- 上游正文按 `read_skill_file` 的预算分页读，不整篇塞进上下文。

## 3. 安装入口

### 3.1 设置页

「设置 → 技能库 → 安装 Archify 3.2.0」：

1. 选择一个上游目录：
   - 仓库根（含 `archify/`），或
   - 技能包目录（含 `SKILL.md`、`bin/archify.mjs`）。
2. 安装器校验 `skill-release.json` 版本必须为 `3.2.0`；不匹配直接拒绝，避免把
   未验证的版本写进技能库。
3. 复制到临时目录 → 写适配入口 / 许可 / 清单 → 替换 `skills/archify/`；
   重复安装会覆盖旧包（不保留备份）。
4. 重新扫描，技能列表出现 `archify`、`v3.2.0` 与来源。

### 3.2 命令行（干净机器复现）

前置：.NET 10 SDK、Node.js ≥ 18、Microsoft Edge（`browser-check` 需要）。

```powershell
git clone git@github.com:tt-a1i/archify.git .tmp/archify-repo
git -C .tmp/archify-repo checkout 81c6be8d87090670c45d08a8f97f9d29c5af0bec

dotnet run --project tools/p3-archify -c Release -- --skills-root .tmp/p3-skills
```

`tools/p3-archify` 会安装并检查：适配入口、上游正文、许可 / NOTICE / 第三方声明、
catalog 版本、`bin/archify.mjs` 解析。报告写在 `<skills-root>/p3-probe-report.json`。

真实应用安装时把 `--skills-root` 换成
`$env:APPDATA\SDemo\DesktopAgent\skills`（也就是设置页按钮的目标），或直接点设置页按钮。

## 4. 产物打开

- `ArtifactLinkParser` 只从正文提取 `.archify/.../*.html/.htm`；拒绝绝对路径、
  `..` 段、非 `.archify` 路径。
- `ArtifactLinks` 按当前工作区根拼绝对路径，校验仍在 `.archify/` 下且 `File.Exists`，
  再调用 `IFileLauncherService.OpenFileAsync`；Windows 实现走 Shell，`.html` 交给
  默认浏览器。
- 不内联 HTML、不自动弹窗；打开失败给一句状态（文件不存在 / 只允许 `.archify` /
  环境不支持 / 已交给默认浏览器）。

## 5. 验收证据

### 5.1 安装探针

```text
上游目录：C:\Code\SDemo\DesktopAgent\.tmp\archify-repo
技能库：C:\Code\SDemo\DesktopAgent\.tmp\p3-skills
已安装 Archify 3.2.0：192 个文件，5.1 MB。
适配入口：2753 B；上游正文：18877 B
脚本 sha256：3cebcee3fa339e3b98deacae8cb9a5570525e003d8823368d304d904ba9ceecd
catalog：archify / Package / 3.2.0 / https://github.com/tt-a1i/archify
P3 安装检查通过。
```

报告：`.tmp/p3-skills/p3-probe-report.json`。

### 5.2 从已安装包跑 P2

```powershell
dotnet run --project tools/p2-archify -c Release --no-build -- `
  --skill-root .tmp/p3-skills --workspace .tmp/p3-workspace
```

结果：

```text
模式：直跑（Node，参数不经过 shell）；耗时：3761ms
退出码：0；状态：pass
artifact：p2.html，846230 B
sha256：c1258e24c71a62ce137f769043eedd089533f8e1a8c4bd5c6f06ce59ad24a4d5
gates：validate / deliver / check / browser-check 全 pass
```

这次用的就是安装后的 `skills/archify/bin/archify.mjs`，不是 `.tmp/archify-repo`。

### 5.3 测试

| 项目 | 结果 |
| --- | --- |
| Core | 921 通过 |
| UI | 141 通过 |
| Sandbox | 219 通过 + 1 跳过 |
| 全解决方案构建 | 成功（0 警告） |

新增测试：

- `ArchifyInstallerTests`（6）：安装布局、直接选包目录、重装覆盖/无临时残留、
  非 3.2.0 拒绝、缺包提示、已适配包不能再当上游。
- `ArtifactLinkParserTests`（3）：提取/去重、拒绝绝对与越界路径、反斜杠归一化。
- `ArtifactLinksTests`（2）：真实文件交给宿主绝对路径、文件不存在不启动进程。
- `SkillsSettingsTests` 增加安装器 / 文件夹选择器的 DI 依赖，仍覆盖技能列表、版本、哈希。

## 6. 许可与第三方声明

- 上游：Apache-2.0；包根保留 `LICENSE`、`NOTICE`、`LICENSE-MIT`。
- 第三方品牌图标：`THIRD_PARTY_NOTICES.md` 随包保留（Simple Icons 及各商标许可见该文件）。
- 字体：`assets/JetBrainsMono-OFL.txt` 随包保留。
- 安装器不改变许可文件；若安装源缺 `LICENSE` / `NOTICE` 则拒绝安装。
- 适配入口只做路由与约束，不复制上游正文；上游正文仍带原 frontmatter 与许可信息。

## 7. 已知限制与后续

- 安装源不随仓库分发：干净机器需要克隆上游并检出固定提交；应用本身不联网。
- 适配入口约 2.7 KB（中文 UTF-8），高于设计里 1–2 KB 的目标，但只有一条索引项；
  如上下文极紧，可再压到 2 KB 左右。
- 产物打开用系统默认 `.html` 关联；若用户把 `.html` 关联到编辑器，不会强行拉起 Edge。
  Phase B 的应用内 iframe 预览与独立源隔离留给 P4。
- `examples/locales` 仍有约 0.18 MB，可按需再裁；当前 5.1 MB 可接受。
- 无自动升级；升级 = 重新选择目录 + 覆盖安装 + 重新扫描。
