# 工作区：sdemo.kb（知识库）（规划中）

> 状态：🚧 未开工，目录尚未创建。
> 规划：`docs/architecture/kb-versioning.md` · `docs/architecture/sdemo-brand-blueprint.md`
> 工程结构见 `docs/architecture/repository-layout.md` §4（每个应用五件套：Contracts / Storage / Core / Mcp / Host）。

## 职责

文档版本链 + 块级内容寻址 + 差异与引用追溯；只存只找，不持有模型凭证。

## 开工前先做三件事

1. 建目录与工程：`src/Apps/Kb/`
2. 建 `SDemo.Kb.slnf`，并在根 `AGENTS.md` 的命令区登记
3. 把本文件换成真实说明（目录地图 / 必读契约 / 命令 / 坑），并把 `workspaces/kb.json` 的 `status` 改为 `active`

## 铁律提醒

- **零模型凭证**：不引入 `IChatClient` / `ApiKey`，需要模型时经 MCP 请求 DesktopAgent
- **与其它应用零引用**：只存 `sdemo.<app>/<entity>/<id>` 形式的 URI 字符串
- **删除一律软删**：`deleted_at` + 回收站，MCP 写操作落审计