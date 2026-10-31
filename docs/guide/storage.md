# 数据存储

> 由根 README 拆出（2026-10-31），内容与原章节一致，仅调整链接前缀。

## 数据存储

设置分三层：全局（设置弹窗）、工作区（侧栏工作区「⋯ → 工作区设置」）、聊天室（聊天室右上角齿轮，以及输入框里的「完全权限 / 沙箱」开关）。每层只保存自己显式改过的项，未设置的项继续继承上级；优先级为 **聊天室 > 工作区 > 通用**。作用域行标题旁的感叹号会显示属性说明与上级当前值。

设置与会话分开存：

| 数据 | 位置 | 说明 |
| --- | --- | --- |
| 设置（主题 / 提供商 / 工作区列表 / 搜索 / 写入确认） | `%APPDATA%\SDemo\DesktopAgent\settings.json` | `System.Text.Json`；API Key 用 DPAPI 加密 |
| 历史聊天（不归属工作区） | `%APPDATA%\SDemo\DesktopAgent\chats\history.db` | SQLite + EF Core |
| 每个工作区的聊天 | `%APPDATA%\SDemo\DesktopAgent\chats\workspaces\{workspaceId}.db` | 一个工作区一个库 |

> 上表是 Windows 宿主的位置（WPF 与 MAUI-Windows 共用）。Android 宿主把根目录换成
> `FileSystem.AppDataDirectory`，API Key 改用 AES-GCM（应用私有密钥文件），其余结构一致。

- 两个表：`sessions`（Id / Title / Meta / WorkspaceId / IsPinned / UpdatedAt / ScopedSettingsJson）和 `messages`（Id / SessionId / Order / Role / Content / Reasoning / AttachmentsJson / CreatedAt / DurationMs / InputTokens / OutputTokens / ToolCallsJson）；删除会话级联删除消息。附件文件本身在 `%APPDATA%\SDemo\DesktopAgent\uploads\{sessionId}\`，数据库只存路径与元信息。
- 新建聊天室在发出第一条消息前不落库；首条消息写入时在对应库创建会话。
- 用户消息立即写库；助手消息在流式结束（正常结束 / 停止 / 出错）后写一次，避免每个增量都落盘。
- 置顶、重命名、删除会话都会立即写库；删除工作区时先把该库中的会话搬到 `history.db`（`WorkspaceId` 置空），再删除工作区 DB 文件。
- 写操作通过 `ChatStore` 串行执行；建表用 `Database.EnsureCreated()`，并在打开旧库时自动补 `AttachmentsJson` 列（新增列以外的表结构变更仍需手动迁移）。
- 启动时 `AppShell` 从历史库 + 所有工作区库加载会话；某个库损坏 / 被占用时跳过它，不影响其他库。
