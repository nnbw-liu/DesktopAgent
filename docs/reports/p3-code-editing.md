# P3 — 代码编辑工具链（第 3 期）

> 状态：✅ 完成（2026-10-22） · 估时 4 人日 · 前置：P1（建议在 P2 后做，便于复用测试夹具） · 出口：能改代码、diff 可审
> 关联总纲：[vibe-coding-roadmap.md](vibe-coding-roadmap.md) · 决策：`apply_patch` 为主 / 写前必读 · 报告：[p3-report.md](p3-report.md)

## 1. 本期要解决的问题

现有文件工具只够「看小说」，不够「改代码」：

- `read_file` 整文件读、超 64KB 直接拒绝、结果再截到 8000 字符（[FileToolPack.cs](../../src/Tools/SDemo.DesktopAgent.Tools.Files/FileToolPack.cs:111)），大文件读不全、也没有行号；
- `write_file` 只能整文件覆盖（[FileToolPack.cs](../../src/Tools/SDemo.DesktopAgent.Tools.Files/FileToolPack.cs:119)），改一行也要重写全文，既费 token 又容易误删；
- 没有 grep / glob / 目录树，模型只能靠猜路径；
- 一次生成里改多个文件会弹多次审批，且审批框只显示“完整内容”，不是 diff。

本期把工具面从「文件读写」升级为「代码检索 + 补丁编辑」，并加上**读前写**约束：没读过的文件不许改。

## 2. 范围

**做**

- 按行读取（offset/limit + 行号 + 截断元信息）；
- 文本搜索（正则 + glob + 结果上限 + 上下文行）；
- 路径枚举（glob / 目录树，带排除规则）；
- 统一 diff 补丁（多文件、全有或全无、模糊匹配、失败回传上下文）；
- 读前写注册表：`apply_patch` / `write_file` 前校验「本轮或本会话读过且文件未变」；
- 编辑审批显示 diff（而不是完整文件）；
- 本次生成结束后的改动摘要（文件、+/−、状态）。

**不做**

- 批量编辑合并成一次审批 → P4（先看真实任务里的审批次数再决定）；
- 回滚点 / 撤销 → P5；
- LSP 诊断、符号索引、语义搜索 → 本周期不做；
- 语法高亮 → 不做。

## 3. 设计要点

### 3.1 工具面调整

拆成两个包，设置页仍按包开关：

| 包 | 工具 | 风险 | 审批 |
| --- | --- | --- | --- |
| `files`（只读） | `list_files`、`list_tree`、`read_file`、`search_files`、`glob_files`、`workspace_info` | 低 | 无 |
| `edits`（写入） | `apply_patch`、`write_file` | 中 | 每次（可用设置关闭） |

- 两个包都 `BuiltIn: true`，默认启用；旧设置里只有 `files` 时按默认补 `edits`；
- 工具描述要短、参数要少——这是给小模型留的余地（P6 会进一步裁剪）。

### 3.2 按行读取

```csharp
read_file(path, offset = 1, limit = 400)
```

返回：

```text
# src/Api/Options.cs · 共 312 行 · 本次 120–160 · 接续用 offset=161
  120|     public string? Endpoint { get; set; }
  121|
...
  160| }
```

- `limit` 上限 2000，默认 400；越界自动收敛；
- 二进制检测（前 8KB 含 `\0`）直接报错“看起来是二进制文件”；
- 文件 > 8MB 直接拒绝；二进制检测见 §3.2；
- 续读提示必须带下一段的 `offset`，减少模型试探。

### 3.3 搜索

```csharp
search_files(pattern, glob = "**/*", maxResults = 80, contextLines = 0, caseSensitive = false)
```

- 纯 .NET 实现，不依赖外部 `rg`；正则超时 1 秒；
- 排除：`.git`、`.vs`、`bin`、`obj`、`node_modules`、`.tmp`、`TestResults`；单文件 ≤ 512KB；跳过二进制；
- 输出 `相对路径:行号: 内容`（行内截断 300 字符）；同文件最多 20 条；总上限 80；
- 无结果时返回“未找到；建议放宽 glob 或换关键词”，而不是空字符串。

```csharp
glob_files(pattern, maxResults = 200)   // 只返回路径
list_tree(path = ".", depth = 2, maxEntries = 200)
```

`glob` 支持 `**`、`*`、`?`；大小写不敏感（Windows）；结果按路径排序。

### 3.4 补丁

```csharp
apply_patch(patch, expectedHash = null)
```

- 标准 unified diff，可多文件：

```diff
--- a/src/Api/Options.cs
+++ b/src/Api/Options.cs
@@ -120,6 +120,7 @@
     public string? Endpoint { get; set; }
+    public bool EnableCache { get; set; }
```

- **全有或全无**：先解析、校验路径、计算所有文件的新内容，全部成功才落盘；
- 路径校验沿用 `SandboxPathGuard`（相对路径、无 `..`、无重解析点）；
- 匹配策略：精确 → 忽略行尾空白 → 以内容窗口做全文漂移搜索；失败时返回**最近的相似片段（前后 5 行）**，让模型自己修正；
- `expectedHash` 可选：传入时先校验当前文件 hash；不匹配直接拒绝并提示“文件已被改动，请重新读取”；
- 保留原换行风格（CRLF/LF）与文件末尾换行状态；
- 落盘用临时文件 + 原子替换（复用 `write_file` 的做法）；
- 返回：`已修改 2 个文件：src/A.cs (+8 -2)、src/B.cs (+1 -1)`；失败：`未写入任何文件：src/A.cs 第 3 个 hunk 未匹配…`。

### 3.5 读前写

```csharp
public interface IFileReadRegistry
{
    void RecordRead(string sessionId, string relativePath, string hash);
    ReadRecord? Find(string sessionId, string relativePath);
    void Clear(string sessionId);
}
```

- 单例、按 session 分桶、TTL 2 小时；`read_file` / `search_files` 命中文件 / `apply_patch` 成功后都登记；
- `apply_patch` / `write_file` 前检查：
  - 本会话读过该路径（TTL 内）；
  - 登记 hash 与当前文件 hash 一致（文件没被外部改动）；
  - 不满足 → 返回明确错误 + 建议（“先 read_file 再改”）；
- 新建文件（不存在）不受此限制，但仍需审批；
- 重启后注册表清空，等价于“必须重新读”，可接受。

### 3.6 审批显示 diff

- `ToolApprovalKind` 新增 `FileEdit`；`Detail` 放统一 diff 文本；
- `ToolApprovalHost.razor` 对 `FileEdit` 用等宽字体渲染 diff，`+` 行绿色、`-` 行红色（CSS 变量，跟随主题）；
- 大 diff 只显示前 200 行 + “共 N 行，已截断显示”，但**实际应用的是完整补丁**；
- `AllowSessionRemember = false`：编辑类操作不允许“本会话自动允许”；
- 多个文件时 Summary 列出全部路径；拒绝则不写任何文件。

### 3.7 改动摘要

- 每次生成结束后，从 `ToolTrace` 汇总本次写入/修改的文件，渲染为一张 `EditSummaryPanel`（消息下方或底部）：
  `本次改动 3 个文件 · +42 −17`，展开看每个文件的 diff；
- 数据来自工具返回与轨迹，不额外扫描；
- 与 P5 的回滚点对接：面板上预留「撤销本次改动」按钮位置（P5 上线）。

## 4. 任务拆解

| # | 任务 | 交付物 | 涉及文件 | 估时 | 状态 |
| --- | --- | --- | --- | --- | --- |
| 3.1 | 包拆分与描述 | `files` / `edits` 两个包、默认值、旧设置兼容 | `FileToolPack.cs`、`ToolSettings.cs` | 0.25 | ✅ |
| 3.2 | 按行读取 | `read_file(offset, limit)`、行号、续读提示、二进制/大文件规则 | `FileToolPack.cs` | 0.5 | ✅ |
| 3.3 | 搜索与枚举 | `search_files`、`glob_files`、`list_tree`、排除与上限 | `Core/Tools/WorkspaceSearch.cs` | 1.0 | ✅ |
| 3.4 | 补丁解析 | `UnifiedPatchParser`、多文件、路径校验 | `Core/Editing/UnifiedPatch.cs` | 0.5 | ✅ |
| 3.5 | 补丁应用 | 匹配 / 模糊 / 保留换行 / 原子写 / 失败上下文 | `Core/Editing/PatchApplier.cs` | 1.0 | ✅ |
| 3.6 | 读前写 | `IFileReadRegistry` + 校验 + 错误文案 | `Core/Editing/FileReadRegistry.cs`、两个工具 | 0.5 | ✅ |
| 3.7 | 审批 diff | `FileEdit` 类型、宿主渲染、CSS | `IToolApprovalService.cs`、`ToolApprovalHost.razor` | 0.5 | ✅ |
| 3.8 | 改动摘要 | `EditSummaryPanel.razor` + 轨迹汇总 | UI | 0.5 | ✅ |
| 3.9 | 文档与报告 | `docs/architecture/code-editing.md`、`p3-report.md` | docs | 0.25 | ✅ |

## 5. 测试清单

**单测**

- `ReadFileRangeTests`：偏移/上限、越界收敛、行号格式、续读提示、二进制拒绝、>64KB 单行。
- `WorkspaceSearchTests`：正则、glob、大小写、排除目录、单文件上限、结果上限、超时、无结果文案。
- `PatchParserTests` / `PatchApplierTests`：单文件、多文件、多 hunk、CRLF、无末尾换行、空 hunk、非法头、绝对路径 / `..` / UNC / junction 拒绝；精确 / 空白容错 / 漂移 / 全有或全无 / hash。
- `ReadRegistryTests`：TTL、hash 变化、未读拒绝、新建文件放行、session 隔离。
- `EditToolPackTests`：`FileEdit` 的 `AllowSessionRemember=false`；拒绝后无写入；坏补丁格式提示；新建文件。
- `EditSummaryTests`：多文件汇总、失败不计数。
- `WorkspacePathTests`：绝对路径 / UNC / 设备路径 / `..` / NUL / junction 全部拒绝。

**集成 / 手工**

- 本仓库任务 A：「设置页加一个开关并落库」——统计工具调用次数、是否有整文件覆盖、diff 是否可审、构建是否通过。
- 本仓库任务 B：「给 `read_file` 加参数并补单测」——验证模型能否自己找到测试文件并只改必要行。
- 恶意输入：补丁里带 `../../`、`C:\Windows\...`、UNC、junction —— 全部拒绝且不落盘。

## 6. 验收标准

- [x] 任务 A：≤ 12 次工具调用完成；`dotnet build SDemo.DesktopAgent.Wpf.slnf` 通过；diff 审批里能看到每个 hunk。
- [x] 不存在「未读先写」的成功路径；未读时错误文案能引导模型先读。
- [x] 多文件补丁任一文件失败时，其他文件**零写入**（用临时目录断言）。
- [x] 大文件（> 64KB）可按行读完并编辑；读取结果不再被 8K 字符无提示截断（截断必须带元信息）。
- [x] 补丁失败返回的上下文足以让模型在 1 次重试内修正（用 3 个坏补丁样本验证）。
- [x] 新增测试 ≥ 25 个；现有测试全绿。

## 7. 风险与回退

| 风险 | 处理 |
| --- | --- |
| 补丁匹配脆弱（空格、换行） | 三级匹配 + 3 行 fuzz；失败回传上下文；不做“尽力猜” |
| 搜索扫盘慢 | 排除目录 + 文件大小上限 + 结果上限 + 30s 缓存可选 |
| 模型仍偏爱 `write_file` | 工具描述明确“改已有文件用 apply_patch”；`write_file` 对已存在文件要求先读 |
| 审批 diff 太长 | 只显示前 200 行，但应用完整补丁；Summary 列文件清单 |
| 读注册表内存增长 | 按 session 分桶 + TTL + 会话关闭清理 |

## 8. PR 拆分

1. **PR1**：包拆分 + 按行读 + 搜索/枚举 + 单测。
2. **PR2**：补丁解析与应用 + 读前写 + 单测。
3. **PR3**：审批 diff + 改动摘要 + 文档/报告。

## 9. 完成定义（DoD）

- [x] 两个本仓库任务跑通并记录工具调用序列；
- [x] 安全用例（穿越、junction、UNC）全部拒绝；
- [x] 总纲状态表改为 ✅，P4 的「审批次数」基线有数据；
- [x] `docs/architecture/code-editing.md` 写清工具语义、限制、给模型的建议。
