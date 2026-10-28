# 完整示例

以下清单与技能都能直接落地（复制到 `<插件目录>/<id>/`，再「重新扫描」）。
更短的骨架见 `templates/minimal/`，带参数与四入口的骨架见 `templates/full/`。

## 1. 润色（无参数，三个入口）

`polish/plugin.json`：

```json
{
  "id": "polish",
  "name": "润色",
  "version": "1.0.0",
  "description": "把文字改得更通顺，不改变原意。",
  "icon": { "text": "润" },
  "capabilities": [
    { "id": "polish", "name": "润色", "skill": "skills/polish.md", "privacy": { "sendsText": true } }
  ],
  "mounts": [
    { "id": "composer", "surface": "composer", "capability": "polish", "order": 30 },
    { "id": "bubble", "surface": "message", "capability": "polish", "input": "message", "order": 30 },
    { "id": "selection", "surface": "selection", "capability": "polish", "input": "selection", "order": 30 }
  ]
}
```

`polish/skills/polish.md`：

```markdown
---
name: 润色
description: 把文字改得更通顺，不改变原意。
version: 1.0
---

你是一位中文编辑。请润色用户提供的文本。

规则：
- 不改变原意，不增删事实。
- 修正语病、口语化表达与重复用词。
- 保持原有的段落结构与 Markdown 格式。
- 只输出润色后的全文，不要解释改了什么。
```

要点：一个能力挂三个入口；没有参数时点按钮直接跑，不会弹面板。

## 2. 双语翻译（`segments` + 语言参数）

`translate-lite/plugin.json`：

```json
{
  "id": "translate-lite",
  "name": "翻译（轻量）",
  "version": "1.0.0",
  "description": "把文本翻译成指定语言，支持双语对照。",
  "icon": { "text": "译" },
  "capabilities": [
    {
      "id": "translate",
      "name": "翻译",
      "skill": "skills/translate.md",
      "privacy": { "sendsText": true },
      "output": { "format": "segments" },
      "parameters": [
        {
          "key": "target",
          "label": "目标语言",
          "kind": "select",
          "defaultFrom": "system-language",
          "fallback": "en",
          "options": [
            { "value": "zh-Hans", "label": "简体中文" },
            { "value": "ja", "label": "日本語" },
            { "value": "en", "label": "English" }
          ]
        }
      ]
    }
  ],
  "mounts": [
    { "id": "composer", "surface": "composer", "capability": "translate", "order": 10 },
    { "id": "bubble", "surface": "message", "capability": "translate", "input": "message", "order": 10 },
    { "id": "selection", "surface": "selection", "capability": "translate", "input": "selection", "order": 10 }
  ]
}
```

`translate-lite/skills/translate.md`：

```markdown
---
name: 翻译
description: 把输入文本翻译成指定语言。
version: 1.0
---

你是一位专业翻译。把用户提供的文本翻译成 {{target.label}}（{{target}}）。

规则：
- 准确、通顺、符合目标语言的表达习惯。
- 保留原文的段落、列表、引用与 Markdown 结构。
- 代码块内的代码、命令、标识符不翻译；只翻译代码块外的说明文字。
- 不要添加原文没有的解释、前言或后记。
```

要点：`segments` 的分段格式由宿主追加，技能正文**只写翻译规则**；
`defaultFrom: "system-language"` 让默认目标语言跟随系统，`fallback` 兜底。

## 3. 简单问答（关联上下文开关）

`quick-ask-lite/plugin.json`：

```json
{
  "id": "quick-ask-lite",
  "name": "简单问答（轻量）",
  "version": "1.0.0",
  "description": "跳过技能与工具，直接给出简洁回答；可选择是否关联上下文。",
  "icon": { "text": "问" },
  "capabilities": [
    {
      "id": "quick-ask",
      "name": "简单问答",
      "skill": "skills/quick-ask.md",
      "allowHistory": true,
      "contextParameter": "useContext",
      "privacy": { "sendsText": true },
      "parameters": [
        { "key": "useContext", "label": "关联上下文", "kind": "toggle", "default": "true" }
      ]
    }
  ],
  "mounts": [
    { "id": "sidebar", "surface": "sidebar", "capability": "quick-ask", "order": 5 },
    { "id": "composer", "surface": "composer", "capability": "quick-ask", "order": 5 },
    { "id": "bubble", "surface": "message", "capability": "quick-ask", "input": "message", "order": 5 },
    { "id": "selection", "surface": "selection", "capability": "quick-ask", "input": "selection", "order": 5 }
  ]
}
```

`quick-ask-lite/skills/quick-ask.md`：

```markdown
---
name: 简单问答
description: 跳过技能与工具，直接给出简洁、准确的回答。
version: 1.0
---

你是一个直接、可靠的问答助手。用户提供的文本可能是一个问题，也可能是一段需要解释、核对或补充的内容。

规则：
- 是问题就直接回答；是陈述或片段就解释它、指出关键点或给出结论。
- 直接给答案，不要复述问题，不要写「好的」「以下是」之类的前言。
- 简洁优先：默认三句以内；确实需要步骤、列表或代码时再展开。
- 不使用工具、不读取文件、不调用外部服务。
- 信息不足时明确说明缺少什么，不要编造。
- 输出语言与文本一致；保留必要的 Markdown 格式。
```

要点：`allowHistory: true` 与 `contextParameter: "useContext"` 必须成对出现，
且 `useContext` 必须是本能力的 `toggle` 参数；参数为 `false` 的这一轮不带历史。

## 4. 总结（`text` + 三个入口）

```json
{
  "id": "digest",
  "name": "要点总结",
  "version": "1.0.0",
  "description": "压成一句话结论加三到五条要点。",
  "icon": { "text": "总" },
  "capabilities": [
    {
      "id": "digest",
      "name": "要点总结",
      "skill": "skills/digest.md",
      "privacy": { "sendsText": true },
      "parameters": [
        {
          "key": "maxPoints",
          "label": "要点条数",
          "kind": "select",
          "default": "3",
          "options": [
            { "value": "3", "label": "3 条" },
            { "value": "5", "label": "5 条" }
          ]
        }
      ]
    }
  ],
  "mounts": [
    { "id": "composer", "surface": "composer", "capability": "digest", "order": 40 },
    { "id": "bubble", "surface": "message", "capability": "digest", "input": "message", "order": 40 },
    { "id": "selection", "surface": "selection", "capability": "digest", "input": "selection", "order": 40 }
  ]
}
```

```markdown
---
name: 要点总结
description: 压成一句话结论加三到五条要点。
version: 1.0
---

你是一位信息整理编辑。把用户提供的文本压缩成「一句话结论 + 要点列表」。
要点最多 {{maxPoints}} 条（参数值是 {{maxPoints}}）。

规则：
- 先给一句话结论，再列要点，每条不超过一行。
- 只保留原文有的事实，不补充背景、不做评价。
- 数字、专有名词、日期保持原样。
- 不要写「以下是总结」之类的前言。
```

