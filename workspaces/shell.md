# 工作区：DesktopAssembly（桌面外壳）（规划中）

> 状态：🚧 未开工，目录尚未创建。
> 规划：`docs/architecture/desktop-shell.md` · `docs/architecture/widget-contract.md` · `docs/architecture/host-protocol.md`
> 工程结构见 `docs/architecture/repository-layout.md` §4（每个应用五件套：Contracts / Storage / Core / Mcp / Host）。

## 职责

常驻托盘：扫描组件清单、按需拉起应用、跨进程管控 native 窗口、位置与多屏、崩溃重启。不存业务数据。

## 开工前先做三件事

1. 建目录与工程：`src/Shell/`
2. 建 `SDemo.Shell.slnf`，并在根 `AGENTS.md` 的命令区登记
3. 把本文件换成真实说明（目录地图 / 必读契约 / 命令 / 坑），并把 `workspaces/shell.json` 的 `status` 改为 `active`

## 铁律提醒

- **零模型凭证**：不引入 `IChatClient` / `ApiKey`，需要模型时经 MCP 请求 DesktopAgent
- **与其它应用零引用**：只存 `sdemo.<app>/<entity>/<id>` 形式的 URI 字符串
- **删除一律软删**：`deleted_at` + 回收站，MCP 写操作落审计