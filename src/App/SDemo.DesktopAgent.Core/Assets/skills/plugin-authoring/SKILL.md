---
name: 插件制作
description: 为 DesktopAgent 制作插件：声明式插件（plugin.json + 技能文件，不写 C#）与仓库内置插件（C# 类库 + 嵌入资源）。
icon: 插
when-to-use: 用户要新增 / 修改 / 排查 DesktopAgent 插件，或问 plugin.json 字段、四条入口、参数、技能文件怎么写时。
tags: 插件, plugin, manifest, skill, 技能
version: 1.0.0
---

# DesktopAgent 插件制作

DesktopAgent 的插件是**声明式**的：一个目录 = `plugin.json` + 若干 Markdown 技能文件，
不写 C#、不调工具、只产出文本。宿主把技能正文放进 system 消息，把用户输入单独放进 user 消息。

## 先判断走哪条路

- 用户要「给自己做一个插件」（润色 / 翻译 / 总结 / 语气改写…）：走**声明式插件**。
  读 `references/declarative.md`，直接复制 `templates/minimal/` 或 `templates/full/`。
- 用户要在**本仓库**里新增随应用发布的内置插件（C# 类库）：读 `references/builtin.md`。

## 声明式插件最短路径

1. 先确认四件事：一句话目标、挂哪几个入口（`sidebar` / `composer` / `message` / `selection`）、
   参数（要不要选语言 / 语气）、输出格式（`text` 或 `segments`）。
2. 在 `<插件目录>/<id>/` 下写 `plugin.json` 与 `skills/main.md`；
   `id` 必须匹配 `^[a-z][a-z0-9-]{1,31}$`（小写字母开头，只含小写字母、数字、连字符）。
3. `capabilities[].skill` 指向包内 `.md`；技能正文里用到的 `{{key}}` 必须在同一能力的
   `parameters` 里定义，否则能力加载失败。
4. 告诉用户：到「设置 → 插件 → 重新扫描」。宿主**没有文件监视**，改完必须重新扫描；
   已经打开的会话还要重新选一次插件（入口列表是扫描时生成的）。
5. 让用户逐条验收：输入框 chip、侧栏入口、助手消息底部按钮、划词菜单各试一次。

插件目录（Windows）：`%APPDATA%\SDemo\DesktopAgent\plugins\<id>\`；
Android：`<AppDataDirectory>/plugins/<id>/`。应用里可从「设置 → 插件 → 打开插件目录」直接打开。

## 硬规则（写错就加载失败）

- 插件只产出文本：**没有**工具调用、网络、文件、C# 代码；需要副作用的能力应先做成内置工具。
- 技能正文里**不要**写宿主的事：不要写「用户输入放在 `<待处理文本>` 里」，
  不要写 `<s>` / `<t>` 分段格式（`output.format = segments` 时宿主自动追加格式契约）。
- 技能路径必须留在插件目录内：不允许 `..`、绝对路径；只接受 `.md`。
- 占位符只支持 `{{key}}` 与 `{{key.label}}`；引用未定义参数 = 能力加载失败。
- `surface: "selection"` 只能配 `input: "selection"`；其它 surface 不能声明 `input: "selection"`。
- `contextParameter` 必须指向本能力的 `toggle` 参数，并且同时写 `allowHistory: true`。
- 清单允许 `//` 注释与尾逗号，字段名大小写不敏感；未知字段只警告，不阻断。
- 体积上限：`plugin.json` 512 KB、单个 skill 256 KB、图标 path 总长 4 KB（最多 8 条）。
- 同 `id` 的本地插件会覆盖内置插件；在不改应用代码的前提下，这是改内置「翻译 / 总结 / 简单问答」行为的唯一方式。

## 怎么落地文件

- 当前会话是「完全权限」（不套沙箱）时：用 `run_powershell` 把目录建在插件目录下，
  文件用 `Set-Content -Encoding utf8NoBOM` 写入。
- 工作区写 / 只读模式下 PowerShell 留在沙箱里，写 `%APPDATA%` 可能被拒绝：别硬试，
  直接把两个文件的完整内容给用户，说明保存到
  `%APPDATA%\SDemo\DesktopAgent\plugins\<id>\`（`plugin.json` 与 `skills\main.md`），
  或者先写到工作区再让用户复制；不要假装已经装好。

## 校验与排查

所有错误都在「设置 → 插件」里带**文件 + 行号**或**文件 + JSON 路径**展示，不会白屏。
常见报错对照表、按现象排查的顺序见 `references/validation.md`。

## 参考文件（按需读，不要整包遍历）

- `references/declarative.md`：包结构、加载规则、`plugin.json` 全字段、参数、四条入口、
  输出格式、执行与限制、安全边界。
- `references/skill-file.md`：技能 Markdown 的写法、占位符、frontmatter、常见反例。
- `references/validation.md`：报错对照表 + 调试清单。
- `references/examples.md`：润色 / 翻译（`segments`）/ 简单问答（关联上下文）/ 总结四份完整清单与技能。
- `references/builtin.md`：在本仓库新增内置插件（类库 / 嵌入资源 / DI 注册 / 测试）。
- `templates/minimal/`、`templates/full/`：可直接复制的 `plugin.json` 与 `skills/main.md`。
