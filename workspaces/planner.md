# 工作区：sdemo.planner（待办 + 日历）（规划中）

> 状态：🚧 未开工，目录尚未创建。
> 规划：`docs/architecture/todo-mcp-surface.md` · `docs/architecture/sdemo-brand-blueprint.md`
> 工程结构见 `docs/architecture/repository-layout.md` §4（每个应用五件套：Contracts / Storage / Core / Mcp / Host）。

## 职责

todo（可完成）与 event（日程）同库不同表；AI 草稿审阅后入库；ICS 导出与只读订阅。

## 开工前先做三件事

1. 建目录与工程：`src/Apps/Planner/`
2. 建 `SDemo.Planner.slnf`，并在根 `AGENTS.md` 的命令区登记
3. 把本文件换成真实说明（目录地图 / 必读契约 / 命令 / 坑），并把 `workspaces/planner.json` 的 `status` 改为 `active`

## 铁律提醒

- **零模型凭证**：不引入 `IChatClient` / `ApiKey`，需要模型时经 MCP 请求 DesktopAgent
- **与其它应用零引用**：只存 `sdemo.<app>/<entity>/<id>` 形式的 URI 字符串
- **删除一律软删**：`deleted_at` + 回收站，MCP 写操作落审计