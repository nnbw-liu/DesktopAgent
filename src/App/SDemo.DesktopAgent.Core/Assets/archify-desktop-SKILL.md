---
name: archify
description: "用 Archify 生成架构图、流程图、时序图、ERD、类图、时间线等可展开的独立 HTML（内联 SVG、深浅色、导出），也可以把答案做成一页解释器。DesktopAgent 适配版：完整上游流程放在 references/upstream-SKILL.md，CLI 一律用 run_skill_script 调 bin/archify.mjs。"
license: Apache-2.0
metadata:
  version: "3.2.0"
  upstream: "3.2.0"
  adapter: "1"
---

# Archify（DesktopAgent 适配）

这是一份入口技能。上游 3.2.0 的正文已移到
`references/upstream-SKILL.md`；不要一次读完，按当前步骤取章节。

## 先读什么

1. 画新图：读 `references/upstream-SKILL.md` 的 “Choose the artifact” 与
   “Native diagram authoring” 两节，先确定图型与候选结构。
2. 需要代码证据：再读 `references/repository-authoring.md`。
3. 候选校验失败：只读诊断 `code` 指向的 `references/` 章节（例如
   `delivery-contract.md#failed-finalize-and-candidate-repair`），不要整篇读。

## 跑 CLI

只用 `run_skill_script`，不要拼 `node` 或 PowerShell 命令行：

```text
run_skill_script(
  skill = "archify",
  script = "bin/archify.mjs",
  args = ["finalize", "<type>", ".archify/<type>-<slug>-<timestamp>/candidate.json",
          ".archify/<type>-<slug>-<timestamp>/<slug>.html", "--quality", "showcase", "--json"])
```

- `args` 必须是相对工作区的路径；不能是绝对路径、不能含 `..`。
- 首次候选与修复都复用同一个 `.archify/<type>-<slug>-<timestamp>/` 目录。
- 已经跑过 `browser-check` 的候选，再跑时给新的 `--out-dir .archify/<...>/review-2`，
  不要覆盖上一份浏览器证据。
- 不要加 `--open`；DesktopAgent 由用户点“用浏览器打开”，不由脚本自动弹窗。
- 不要自动联网检查更新；宿主已注入 `ARCHIFY_UPDATE_CHECK_DISABLED=1`。

## 失败与交付

- 非零退出码不是成功。先读 stdout 的 `failedStage`、`diagnostics[].code` 与
  `supportedFixes`，只改受影响的节点 / 关系 / 字段，然后重跑完整 `finalize`。
- 同一问题最多重跑 2 次；仍失败就如实报告失败阶段与诊断，不要伪造交付。
- 交付前确认 receipt `status=pass`，且 `gates` 的 `validate`、`deliver`、`check`、
  `browser-check` 全为 `pass`；`visualReview` 不是人工视觉复核。
- 最终回复给：图型、相对产物路径、闸门结果、是否做过视觉复核。产物只放
  `.archify/`，不要写工作区外。

## 许可

上游 Archify 3.2.0 为 Apache-2.0；包根保留 `LICENSE`、`NOTICE` 与
`THIRD_PARTY_NOTICES.md`。品牌图标的商标与第三方许可见后者；不要把商标当作
Archify 授权的一部分。
