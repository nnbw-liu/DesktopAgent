# 工作区：sdemo.meeting（会议）（规划中）

> 状态：🚧 未开工，目录尚未创建。
> 规划：`docs/architecture/meeting-design.md` · `docs/architecture/sdemo-brand-blueprint.md`
> 工程结构见 `docs/architecture/repository-layout.md` §4（每个应用五件套：Contracts / Storage / Core / Mcp / Host）。

## 职责

录音、本地转写、议题/决议/候选、分析履历；会议横切多个项目，不隶属任何项目。

## 开工前先做三件事

1. 建目录与工程：`src/Apps/Meeting/`
2. 建 `SDemo.Meeting.slnf`，并在根 `AGENTS.md` 的命令区登记
3. 把本文件换成真实说明（目录地图 / 必读契约 / 命令 / 坑），并把 `workspaces/meeting.json` 的 `status` 改为 `active`

## 铁律提醒

- **零模型凭证**：不引入 `IChatClient` / `ApiKey`，需要模型时经 MCP 请求 DesktopAgent
- **与其它应用零引用**：只存 `sdemo.<app>/<entity>/<id>` 形式的 URI 字符串
- **删除一律软删**：`deleted_at` + 回收站，MCP 写操作落审计