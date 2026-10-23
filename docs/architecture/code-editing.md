# 代码编辑工具链（P3）

> 适用：`files` / `edits` 两个内置工具包。目标是让模型「先读、再改、改得小、改得可审」。
> 计划与验收：[plans/p3-code-editing.md](../reports/p3-code-editing.md) · 实测报告：[plans/p3-report.md](../reports/p3-report.md)

## 1. 工具面

| 包 | 工具 | 作用 | 审批 |
| --- | --- | --- | --- |
| `files`（只读） | `list_files` | 列目录直接子项 | 无 |
| | `list_tree` | 列目录树（深度 / 条数上限） | 无 |
| | `read_file` | 按行读取，带行号与续读提示 | 无 |
| | `search_files` | 正则搜索文件内容 | 无 |
| | `glob_files` | 按 glob 只列路径 | 无 |
| | `workspace_info` | 返回工作区根目录 | 无 |
| `edits`（写入） | `apply_patch` | 应用 unified diff（可多文件） | 每次（可在设置关闭） |
| | `write_file` | 整文件写入（新建 / 必须整体重写） | 每次 |

两个包都是 `BuiltIn = true`、默认启用。旧设置只写了 `files` 时，`edits` 按默认值补上；设置页可分别开关。

## 2. `read_file`

```csharp
read_file(path, offset = 1, limit = 400)
```

返回格式：

```text
# src/Api/Options.cs · 共 312 行 · 本次 120–160 · 接续用 offset=161
  120|     public string? Endpoint { get; set; }
  121|
...
  160| }
```

规则：

- `offset` 从 1 开始，越界自动收敛到文件范围；`limit` 上限 2000，默认 400。
- 结果带总行数、本次范围；没读完时一定带「接续用 offset=N」，减少模型试探。
- 单行超过 500 字符会截成前 500 字符 + `…`；整个结果上限 30000 字符，超出时带截断说明和下一个 `offset`。
- 文件读取上限 8MB；超过直接拒绝。二进制（前 8KB 含 `\0`）拒绝并提示。
- CRLF 归一化为 LF 后编号，输出里不把 `\r` 带进内容。
- 读取成功即向读前写注册表登记「路径 + 当前 SHA-256」。

## 3. 搜索与枚举

```csharp
search_files(pattern, glob = "**/*", maxResults = 80, contextLines = 0, caseSensitive = false)
glob_files(pattern, maxResults = 200)
list_tree(path = ".", depth = 2, maxEntries = 200)
```

- 纯 .NET 正则，不依赖 `rg`；单次匹配 1 秒超时；单文件上限 512KB；跳过二进制。
- 默认排除 `.git`、`.vs`、`bin`、`obj`、`node_modules`、`.tmp`、`TestResults`。
- `search_files` 输出 `相对路径:行号: 内容`，行内截断 300 字符；同一文件最多 20 条，总数不超过 `maxResults`；`contextLines` 显示命中行前后上下文。
- 无命中返回「未找到；建议放宽 glob 或换关键词」，不返回空字符串。
- `glob` 支持 `**`、`*`、`?`；Windows 下大小写不敏感，结果按路径排序。
- `list_tree` 的 `depth` 是展开的目录层数：`depth=1` 只列根目录直接子项，`depth=2` 再展开一层；文件在遍历到其父目录时列出。目录名带 `/`，结果截到 `maxEntries` 并提示。

搜索命中文件也会登记读前写（因为模型已经看到内容）；`glob_files` / `list_tree` 只返回路径，不登记。

## 4. `apply_patch`

```csharp
apply_patch(patch, expectedHash = null)
```

必须是标准 unified diff，支持多文件、多 hunk：

```diff
--- a/src/Api/Options.cs
+++ b/src/Api/Options.cs
@@ -120,3 +120,4 @@
     public string? Endpoint { get; set; }
+    public bool EnableCache { get; set; }
     public int Timeout { get; set; }
```

也可以新建文件（`--- /dev/null`）。**不支持删除或重命名**；需要删除时用 `write_file` 写空内容并走审批。

解析规则：

- 每个文件必须有 `--- ` / `+++ ` 头；路径必须是工作区相对路径，拒绝绝对路径、UNC、`..`、设备路径、重解析点。
- 每个 hunk 必须有 `@@ -旧起始,旧行数 +新起始,新行数 @@`；行首只能是空格、`+`、`-`、`\`。
- 可以不写 `*** Begin Patch` / `*** End Patch`；写了会被忽略。`*** Update File:` 这种 Copilot 写法不支持，仍会报错并提示标准格式。
- 删除文件（`+++ /dev/null`）直接拒绝。

应用规则：

- **全有或全无**：先解析、校验所有路径、计算所有新内容；任一文件失败则零写入。
- 匹配顺序：精确匹配 → 忽略行尾空白；找不到时在全文按内容窗口搜索，允许 hunk 头行号漂移。
- 失败返回文件、第几个 hunk、期望内容片段、最近相似片段（前后 5 行）和「先 read_file」建议，错误信息足够模型重试一次。
- 保留原换行风格（CRLF / LF）和文件末尾是否有换行；写入用同目录临时文件 + 原子替换。
- 成功后返回 `已修改 N 个文件：a (+X -Y)、b (+Z -W)`，并追加 `[EDIT] 路径 +X -Y` 行；这些行是改动摘要的数据源。
- `expectedHash` 传入时校验现有文件；不匹配返回「文件已被改动，请重新 read_file」。新建文件忽略该参数。

## 5. 读前写

```csharp
public interface IFileReadRegistry
{
    void RecordRead(string sessionId, string relativePath, string hash);
    ReadRecord? Find(string sessionId, string relativePath);
    void Clear(string sessionId);
}
```

- 单例、按会话分桶、TTL 2 小时；重启后清空（等价于必须重新读）。
- `read_file`、`search_files` 命中文件、`apply_patch` 成功后都会登记。
- `apply_patch` / `write_file` 改已有文件前检查：
  1. 本会话在 TTL 内读过；
  2. 登记的 hash 与当前文件一致。
- 不满足 → 明确拒绝并提示「先 read_file」；新建文件不受限制，但仍需审批。
- 设置里关闭「写文件前确认」只跳过审批，不跳过读前写。

## 6. 审批与改动摘要

- `ToolApprovalKind.FileEdit`，`Detail` 是完整 unified diff；UI 用等宽字体、`+` 绿 / `-` 红 / hunk 蓝。
- 界面最多显示前 200 行，超出提示「共 N 行，已截断显示；实际应用完整补丁」；应用的是完整补丁。
- `AllowSessionRemember = false`：编辑操作没有「本会话自动允许」；多文件时 Summary 列出全部路径，拒绝则零写入。
- 一轮生成结束后，`EditSummaryPanel` 从工具轨迹聚合：`本次改动 N 个文件 +A −R`，展开按文件列 `+/-`。
- 轨迹最多保留 100 条；模型可见的最近轨迹仍由 `ToolTraceFormatter` 压到 20 条。超过 100 次工具调用的极端轮次，摘要可能不完整。

## 7. 给模型的建议（工具描述之外）

1. 改代码先 `read_file`，不要凭记忆写补丁；大文件用 `offset` 续读。
2. 找文件先用 `search_files` / `glob_files`，不要猜路径。
3. 改已有文件优先 `apply_patch`；一次改多个文件合成一个补丁，减少审批次数。
4. 补丁带 3 行上下文；不要用 `*** Begin Patch`，不要删除文件。
5. 失败时读错误里的相似片段，修正后重试一次；仍失败就重新 `read_file`。
6. 只有新建文件或必须整体重写时才用 `write_file`。

## 8. 限制

- 不做 LSP / 语义索引 / 语法高亮；搜索是文本正则。
- 不做删除、重命名、二进制编辑；不做批量审批合并（P4）。
- 不做撤销 / 回滚点（P5）；`EditSummaryPanel` 预留了位置，但没有「撤销」按钮。
- 模糊匹配是全文内容窗口搜索，不是严格的「3 行 fuzz」；对重复片段仍可能选错位置，靠 `expectedHash` 和读前写兜底。
- UI 只做构建 + 启动冒烟，没有 bUnit / 截图比对。

## 9. 验证

```powershell
dotnet test tests\SDemo.DesktopAgent.Core.Tests\SDemo.DesktopAgent.Core.Tests.csproj
dotnet build SDemo.DesktopAgent.Wpf.slnf
```

真机任务工具：`tools/p3-live`（只读用户设置里的 provider / key，不改设置）。

```powershell
$env:P3_WORKSPACE = "F:\path\to\workspace"
$env:P3_PROMPT = "先 read_file 再 apply_patch ..."
dotnet run --project tools\p3-live -c Release
```
