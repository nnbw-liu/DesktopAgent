# Office 文档（Word / Excel / PowerPoint）

把 `.docx` / `.xlsx` / `.pptx` 当成「可读可写的内容源」：读进来是 Markdown，写出去按扩展名生成对应的 Office 文件。目标不是像素级还原，而是让模型能可靠地理解需求、整理结论并产出文档。

## 能力一览

| 工具 | 作用 | 风险 |
| --- | --- | --- |
| `office_info` | 只读结构：Word 标题、Excel 工作表 / 行数、PPT 页数与页标题 | 低 |
| `office_read` | 读成 Markdown，支持 `sheet` / `range` / `slides` / `maxChars` | 低 |
| `office_write` | Markdown 写成 `.docx` / `.xlsx` / `.pptx`，覆盖需 `overwrite=true` | 中，走审批 |

工具包 id 是 `office`，内置、默认开启；在「设置 → 工具设置」可以整包关闭。

## 上传即读

`＋ 上传文件` 选择 Office 文档时，`AttachmentStore` 会调用 `IOfficeAttachmentExtractor`：

1. 解析文档并转成 Markdown（最多 24000 字符）；
2. 在源文件旁写一个 `<文件名>.office.md` sidecar；
3. 把 sidecar 路径存进 `ChatAttachment.ExtractedTextPath`，随消息进入上下文。

所以把 PPT 拖进聊天室后直接问「总结这份 PPT 的需求并列出开发计划」，模型第一轮就能看到内容，不需要先猜路径。内容被截断时，模型可以继续调用 `office_read` 读指定的幻灯片或工作表范围。解析失败 / 加密 / 旧版格式只会保留原始二进制附件，不影响上传。

## 读：Markdown 映射

### Word（.docx）

- `Heading1`–`Heading6` 或「整段加粗且字号明显大于正文」→ `#`–`######`
- 普通段落 → 段落；Word 列表或手打 `•` / `1.` → `-` / `1.`
- 表格 → Markdown 表格（首行表头）
- 文档属性（字体、颜色、页眉页脚、批注）不还原

### Excel（.xlsx）

- 每张工作表：`## 工作表名` + Markdown 表格
- 共享字符串、内联字符串、布尔、数字都读取；日期格式（内置或自定义格式）转 `yyyy-MM-dd[ HH:mm]`
- 公式优先读缓存值；没有缓存值时输出 `=公式原文`
- 默认每张表最多 200 行（`maxRows` 可调，最大 2000），列数按实际使用范围
- `range` 支持 `A1:F40`；`sheet` 支持名称或从 1 开始的序号

### PowerPoint（.pptx）

- 每页：`## 第 N 页 · 标题`
- 标题占位符优先；没有占位符时用第一个有文字的文本框
- 正文段落按层级输出列表，图片只输出数量（`（本页含 N 张图片，未提取图片内容）`）
- 备注输出为 `> 备注：…`
- `slides` 支持 `1-5,8`

## 写：Markdown 约定

### Word

````markdown
# 需求总结

一段正文，支持 **加粗**、*斜体*、`代码`、[链接文字](url)。

## 背景

- 要点一
- 要点二

| 模块 | 说明 |
| --- | --- |
| 登录 | 账号密码 |

> 引用

```csharp
// 代码块
```
````

写入时会创建 `Heading1`–`Heading6` 样式，Word 导航窗格能识别；表格带边框与表头底色。

### Excel

```markdown
## 一期

| 编号 | 需求 | 优先级 |
| --- | --- | --- |
| 1 | 登录 | 高 |
| 2 | 导出 | 中 |

## 二期

| 编号 | 需求 |
| --- | --- |
| 3 | 报表 |
```

- `## 名称` 是工作表名（非法字符会被移除，重名自动加序号，最长 31 字符）
- 没有 Markdown 表格时按 CSV / TSV / 分号分隔文本解析
- 纯数字写成数值单元格；前导 0、长数字、日期保持文本，避免 Excel 改形

### PowerPoint

```markdown
# 项目背景

- 现状
- 问题

---

# 需求分析

- 登录
- 导出
```

- `---` 分页；不写 `---` 时，每个一级标题开始新的一页
- 每页第一个标题是页标题，其余标题 / 段落 / 列表 / 代码 / 引用进入正文
- 表格按行拍平成「列1 | 列2 | 列3」文本

## 安全与边界

- **路径 jail**：读取允许「工作区 + 当前会话附件目录」，写入只允许工作区；拒绝绝对路径越界、UNC、`..`、符号链接 / junction。
- **大小上限**：工作区文档 50 MB，上传附件 25 MB。
- **审批**：`office_write` 默认弹确认框，显示目标路径与 Markdown 预览；拒绝时不落盘。写临时文件成功后才替换目标，失败不会留下半截文件。
- **格式**：只支持 OOXML。旧版 `.doc` / `.xls` / `.ppt` 需要先另存为 `.docx` / `.xlsx` / `.pptx`。
- **不执行宏**：解析用 `DocumentFormat.OpenXml`，不运行 VBA / 外部程序。
- **不保留版式**：字体、颜色、主题、动画、图表数据、SmartArt 不在还原范围内。

## 典型用法

1. **PPT → 需求 + 计划**：上传 PPT，问「总结每页的需求，按优先级列出开发计划和风险」。模型读 sidecar（或 `office_read` 分批读），用 `todo_write` 落计划。
2. **Excel → 分析**：`office_info` 看工作表，`office_read` 带 `sheet` + `range` 读重点数据，再回答。
3. **Markdown → 周报 Word**：`office_write` 到 `周报/第 12 周.docx`，写完 `office_read` 抽查标题与表格。
4. **需求清单 → Excel**：用 `## 一期` / `## 二期` 分工作表，每张表一个 Markdown 表格。
5. **方案 → 评审 PPT**：用 `---` 分页，每页一个 `#` 标题和若干要点。

## 代码位置

- 工具包：`src/Tools/SDemo.DesktopAgent.Tools.Office/`
  - `OfficeToolPack.cs`：工具定义、审批、审计、路径校验
  - `OfficeReader.cs` / `OfficeWriter.cs`：三种格式的读写
  - `MarkdownBlocks.cs`：写入用的块解析与行内样式
  - `OfficeAttachmentExtractor.cs`：上传时自动提取
- 注册：`AgentCoreServiceCollectionExtensions.AddOfficeTools()`
- 上下文：`AgentMessageMapper` 对 Office sidecar 使用 ` ```markdown ` 围栏
- 测试：`tests/SDemo.DesktopAgent.Core.Tests/OfficeToolPackTests.cs`
