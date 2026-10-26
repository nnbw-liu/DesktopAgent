# P1 报告：技能目录包与 `read_skill_file`

> 日期：2026-10-26
> 分支：`feature/archify-p0`
> 状态：**完成**
> 前置：[P0 探针](p0-archify.md) · 设计：[archify-integration.md](../architecture/archify-integration.md)

---

## 1. 交付内容

| 能力 | 实现 |
| --- | --- |
| 目录包发现 | `skills/<name>/SKILL.md` 与旧式 `skills/<name>.md` 并存，同名包优先；根目录 `SKILL.md`、无清单目录不误判 |
| 包正文 | `load_skill(name)` 读 `SKILL.md`，仍按 `MidTurnToolResultChars` 分页 |
| 包内引用 | 新增 `read_skill_file(name, path, offset)`，读取 `references/`、`schemas/` 等 `.md` / `.json` / `.txt` |
| 路径安全 | 拒绝绝对路径 / UNC / `..` / 盘符 / 数据流 / 保留设备名；逐段拒绝重解析点；单文件上限 1 MB |
| 元数据 | `SkillInfo` 增加 `Kind` / `RootPath` / `Version` / `Source`；版本优先 `skill-release.json`，其次 frontmatter |
| 版本哈希 | `SkillCatalog.Snapshot` 计算文件数、总字节与目录内容 SHA-256；设置页在后台算，不卡 UI |
| 暴露 | `read_skill_file` 与 `load_skill` 成对作为元工具；Compact 仍不超过 4 个工具 |
| 设置页 | 新增「技能」分区：目录、扫描结果、目录包/单文件标记、版本、来源、文件数、哈希、打开目录 |
| 兼容 | 旧 `.md` 技能照常索引/加载；包名带点（`my.skill`）不会被误截断 |

---

## 2. 关键代码

- `src/Contracts/SDemo.DesktopAgent.Contracts/Services/ISkillLibrary.cs`
  - `SkillKind`、`SkillFileStatus`、`SkillFileReadResult`、`ReadFile`。
- `src/App/SDemo.DesktopAgent.Core/Services/SkillCatalog.cs`
  - 目录包发现、包优先、frontmatter / release 解析、路径 jail、快照哈希。
- `src/Tools/SDemo.DesktopAgent.Tools.Skills/SkillToolPack.cs`
  - `load_skill` 保持原语义；新增 `read_skill_file`，错误按状态给可恢复提示。
- `src/Tools/SDemo.DesktopAgent.Tools.Abstractions/CapabilityRouter.cs`
  - 两个技能元工具成对暴露，并计入名额。
- `src/App/SDemo.DesktopAgent.UI/Components/Settings/SkillsSettings.razor`
  - 技能设置分区。
- 测试：`SkillCatalogTests`、`SkillToolPackTests`、`CapabilityRouterTests`、`ToolExposureProfileTests`、
  `ToolPresentationTests`。

---

## 3. 行为说明

### 3.1 发现与优先级

```text
skills/
  archify/SKILL.md          ← 包：索引为 archify
  archify/references/*.md   ← 包内引用，不单独进索引
  summary.md                ← 旧式单文件
  archify.md                ← 与包同名时被忽略
```

- 包的 `RootPath` 是包目录；单文件的 `RootPath` 是技能库根目录。
- 只扫描一层：`skills/foo/bar/SKILL.md` 不会被当成技能 `bar`。

### 3.2 `read_skill_file` 的约束

- 路径参数是相对包根的路径，分隔符 `\` 会规范成 `/`。
- 允许 `.md` / `.json` / `.txt`；其他扩展名直接拒绝，避免把图片/二进制读进上下文。
- 上限 1 MB；超过时提示改读更小的章节文件。
- 所有错误都返回一句可恢复的提示，不让模型猜：
  - 没这个技能 → 先 `load_skill` 或看索引；
  - 没这个文件 → 先读 `SKILL.md` 拿清单；
  - 单文件技能 → 直接 `load_skill`；
  - 路径越界 → 只允许包内相对路径。

### 3.3 暴露成本

`load_skill` 与 `read_skill_file` 都是元工具，**各占一个名额**：

- Full：MaxExposed 6 时，真实工具最多 3 个（再算兜底入口）。
- Compact：4 个名额里技能工具占 2 个，真实工具 2 个。

这是有意的取舍：技能索引已经进了系统提示，模型必须能取正文；目录包还必须能取引用，
否则“装了包却读不到 references”。如果后续 Compact 实测不够用，优先把 `read_skill_file`
改成按需 `search_capabilities` 补入，而不是把两个都藏起来。

---

## 4. 验收

### 4.1 自动化

| 项 | 结果 |
| --- | --- |
| `dotnet build SDemo.DesktopAgent.sln -c Release` | 0 错误（2 个既有 CA1416 警告在 `tools/p1-baseline`） |
| Core.Tests | **896 通过**（新增包发现 / 优先级 / 引用 / 越界 / 分页 / 哈希等用例） |
| UI.Tests | 135 通过（含 `read_skill_file` 卡片形态与技能设置页） |
| Sandbox.Tests | 219 通过，1 个 AppContainer 用例在非交互环境跳过 |

### 4.2 覆盖到的边界

- 包与同名扁平文件：只索引包。
- 根目录 `SKILL.md` / 无 `SKILL.md` 的目录：不索引。
- `../secret.md`、绝对路径、`C:\Windows\win.ini`、`ok.md:stream`：全部 `InvalidPath`。
- `.png`：`UnsupportedExtension`；不存在的引用：`FileNotFound`；单文件技能：`FlatSkill`。
- 长引用分页续读，最后一页不再给 `offset`。
- `disable-model-invocation`：`load_skill` 与 `read_skill_file` 都拒绝。
- 包名带点：`my.skill` 不被截断。
- 快照：文件数/字节正确，内容变化后哈希变化。

> 自动化用例使用临时目录里的合成技能包；真实 Archify 3.2.0 目录的装入与运行在 P3 / P2 完成。

---

## 5. 已知限制 / P2 前要处理的点

1. **`read_skill_file` 尚未做“同一次工具结果内不重复读同一文件”的缓存**——预算已经分页，
   重复读会重复花字符；P2 实测后如果常见再加会话级短缓存。
2. **没有“列出包内文件”接口**：模型只能按 `SKILL.md` 的清单读。Archify 的清单足够明确；
   如果后续有包不写清单，再补 `read_skill_file(name, path: "")` 的目录列表。
3. **哈希是整目录内容哈希**：5 MB 的 Archify 包热读约 50 ms；首次读 18 MB 的未裁剪仓库副本
   在本机冷缓存下约 8 s（磁盘 / 杀毒首扫）。设置页已改成后台计算，但大包首次打开仍会看到
   “计算中…”；P3 安装时按设计裁掉 `examples/*.html`，正常只有 5 MB 左右。
4. **脚本执行尚未开放**：P2 的 `run_skill_script` 才会真正跑 Archify CLI；P1 只保证读得到
   `SKILL.md` 与 references。

---

## 5.1 真实 Archify 包冒烟

用 `.tmp/archify-repo` 当技能根目录（其中 `archify/` 是完整上游包，未裁剪）：

```text
archify | Package | v3.2.0 | https://github.com/tt-a1i/archify | Create validated architecture…
快照: 206 文件 / 18,147,415 字节 / 51 ms（热缓存）/ sha256 b51cd0eaf59c…
正文: 18,723 字符
引用: Ok references/repository-authoring.md / 7,996 字符
缺文件: FileNotFound
```

说明 frontmatter 的嵌套 `metadata.version`、`skill-release.json` 的版本/来源、
18.9 KB 正文与 references 读取都能在真实包上工作。

---

## 6. 下一步（P2）

- `run_skill_script(skill, script, args)`：只允许 `<skill>/bin/**`、`<skill>/scripts/**` 的
  `.mjs` / `.js`，宿主解析 Node，argv 数组，cwd = 工作区，环境注入
  `ARCHIFY_UPDATE_CHECK_DISABLED=1` 与已确认的 `ARCHIFY_CHROME` / `ARCHIFY_CHROME_NO_SANDBOX=1`。
- 每次运行展示完整 argv 的审批 + 审计（脚本哈希、参数、耗时、退出码）。
- 输出路径限定 `.archify/`；不 `--open`、不自动联网。
- P3 适配技能 + 安装 Archify 3.2.0 + 许可证声明 + 产物打开。
