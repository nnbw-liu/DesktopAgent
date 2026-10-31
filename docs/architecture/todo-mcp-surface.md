# 待办 MCP 工具面

> 状态：S0 草案，已确认方向，未实现
> 关联：[总体架构](desktop-shell.md) · [宿主控制协议](host-protocol.md)
> 对端：MainAgent（MCP client）· 待办内核进程（MCP server）

---

## 1. 定位

待办的**语义归应用**：工具叫什么、参数怎么走、什么算重复，都由待办应用定义。MainAgent 只负责「发现 server → 暴露工具 → 按风险策略审批 → 转发调用 → 记账」。

这跟现有内置工具链的关系是并存的：内置工具（`files` / `edits` / `todo_write` 等）仍走 `IAgentToolPack`，MCP 工具是**另一条并列的工具来源**。**不要把 MCP 工具塞进现有工具包体系**，否则工具路由、上下文预算、审批三处都要改。

> 特别提醒：现有的 `todo_write`（[TodoToolPack.cs](../../src/Tools/SDemo.DesktopAgent.Tools.Todo/TodoToolPack.cs)）是**会话级任务计划**，活在单轮对话里、随会话落 `sessions.TodosJson`。本文件里的 `todo_*` 是**跨会话持久待办**。两者同名不同义，工具名与界面位置都必须分开，不能合并。

## 2. 传输与鉴权

| 项 | 约定 |
| --- | --- |
| 传输 | MCP Streamable HTTP（单端点 `POST /mcp`），**不用 stdio** |
| 地址 | `http://127.0.0.1:{port}/mcp`，端口由应用随机绑定后写进 `runtime.json` |
| 鉴权 | `Authorization: Bearer <token>`，token 来自 `runtime.json`（见 [host-protocol](host-protocol.md) §4） |
| 为什么不是 stdio | stdio 意味着「客户端拉起进程」，与「应用常驻、宿主保活」冲突；且同一份 DB 会被两次拉起 |

MainAgent 侧的 server 注册项存 `settings.json`：

```json
{
  "id": "sdemo.planner",
  "displayName": "待办",
  "transport": "http",
  "urlFrom": "runtime:sdemo.planner",
  "enabled": true,
  "approval": { "mode": "risk", "alwaysAllow": ["todo_list", "todo_get", "todo_stats"] }
}
```

`enabled=false` 时 MainAgent 完全不连；连不上时只禁用该 server，不影响其它工具。

## 3. 工具清单

| 工具 | 风险 | 审批 | 写库 | 说明 |
| --- | --- | --- | --- | --- |
| `todo_list` | 低 | 否 | 否 | 按条件查条目，默认只返回未完成 |
| `todo_get` | 低 | 否 | 否 | 单条详情（含来源与事件摘要） |
| `todo_create` | 中 | 否 | 是 | 建条目，单次最多 5 条 |
| `todo_update` | 中 | 否 | 是 | 改标题/日期/优先级/备注，必须带 `expectedRevision` |
| `todo_complete` | 低 | 否 | 是 | 勾完成（幂等） |
| `todo_reopen` | 低 | 否 | 是 | 重新打开 |
| `todo_delete` | **高** | **总是** | 是（软删） | 删除；不可关闭审批 |
| `todo_bulk_update` | **高** | **≥5 条时** | 是 | 批量改状态/日期/列表 |
| `todo_clear_list` | **高** | **总是** | 是（软删） | 清空/归档整个列表 |
| `todo_plan_from_text` | 低 | 否 | 否（只产草稿） | 把一段需求文本拆成待办初版 |
| `todo_accept_draft` | 中 | **≥10 条时** | 是 | 采纳草稿（可只采纳部分） |
| `todo_search` | 低 | 否 | 否 | 全文检索标题与备注 |
| `todo_stats` | 低 | 否 | 否 | 今日到期 / 逾期 / 完成率 |

`meeting_*`（纪要 → 待办）本版**不实现**，只在 schema 里保留 `source=meeting` 这个取值。

## 4. 关键工具签名

### 4.1 `todo_list`

```json
{
  "name": "todo_list",
  "description": "查询待办条目。默认只返回未完成项；按截止时间与优先级排序。",
  "inputSchema": {
    "type": "object",
    "properties": {
      "status": { "type": "string", "enum": ["open", "doing", "done", "all"], "default": "open" },
      "listId": { "type": "string" },
      "dueBefore": { "type": "string", "format": "date-time" },
      "tag": { "type": "string" },
      "q": { "type": "string", "description": "标题/备注关键词" },
      "limit": { "type": "integer", "minimum": 1, "maximum": 50, "default": 20 }
    }
  }
}
```

返回 `items[]`，每项含 `id / title / status / priority / dueAt / listId / revision / source / sourceExcerpt`。

### 4.2 `todo_create`

```json
{
  "name": "todo_create",
  "inputSchema": {
    "type": "object",
    "required": ["items"],
    "properties": {
      "items": {
        "type": "array",
        "minItems": 1,
        "maxItems": 5,
        "items": {
          "type": "object",
          "required": ["title"],
          "properties": {
            "title": { "type": "string", "maxLength": 200 },
            "notes": { "type": "string", "maxLength": 4000 },
            "dueAt": { "type": "string", "format": "date-time" },
            "priority": { "type": "integer", "minimum": 0, "maximum": 4, "default": 2 },
            "listId": { "type": "string" }
          }
        }
      },
      "clientRequestId": { "type": "string", "description": "幂等键，10 分钟内重复即返回首次结果" }
    }
  }
}
```

**每次调用必须带来源**：由 MainAgent 自动注入 `source="ai"` + `sourceSessionId` + `sourceExcerpt`（取用户原话）。应用不接受 `source="manual"` 的自称，避免 AI 生成的条目伪装成用户手动录入。

### 4.3 `todo_update` / `todo_complete` / `todo_delete`

三者共用乐观并发：`expectedRevision` **必填**。版本对不上返回 `TODO_CONFLICT` 并附当前条目，让模型决定重试还是放弃——**不要做静默覆盖**，多端同步下这是数据丢失的主要来源。

```json
{ "id": "…", "expectedRevision": 42, "patch": { "dueAt": "2026-11-01T18:00:00+08:00" } }
```

`todo_delete` 额外带 `reason`（自由文本，进审计），返回 `{ "id": "…", "deletedAt": "…", "restorableUntil": "…" }`。

### 4.4 `todo_plan_from_text`

```json
{
  "name": "todo_plan_from_text",
  "description": "把一段需求/会议文本拆成可执行待办，产出草稿。不写正式待办。",
  "inputSchema": {
    "type": "object",
    "required": ["text"],
    "properties": {
      "text": { "type": "string", "maxLength": 20000 },
      "hint": { "type": "string", "description": "例如「按里程碑拆」「只要本周能做的」" },
      "listId": { "type": "string" }
    }
  }
}
```

返回：

```json
{
  "ok": true,
  "data": {
    "draftId": "dr_01H…",
    "items": [
      { "draftItemId": "di_1", "title": "…", "dueAt": null, "priority": 2,
        "excerpt": "原始文本中的对应片段", "suspectDuplicateOf": null }
    ],
    "questions": ["第 3 条没写负责人，要指定吗？"]
  }
}
```

三个约定：

1. **只产草稿，不写 `todos`**。模型想把结果直接入库，必须走 `todo_accept_draft`，且 `≥10 条`触发审批。
2. `excerpt` 必填——每条待办都要能回答「你是从哪句话来的」。这是整个 provenance 设计的目的。
3. `suspectDuplicateOf` 由应用侧做相似度判断后填入，不是让模型猜。

## 5. 结果与错误信封

成功：

```json
{ "ok": true, "requestId": "r_…", "revision": 42, "data": { }, "warnings": ["2 条与已有条目疑似重复"] }
```

失败：

```json
{ "ok": false, "requestId": "r_…", "code": "TODO_CONFLICT", "message": "条目已被修改",
  "hint": "重新 todo_get 后重试", "details": { "currentRevision": 43 } }
```

| 码 | 含义 | 模型该怎么处理 |
| --- | --- | --- |
| `TODO_NOT_FOUND` | id 不存在或已物理清理 | 不要重试，向用户确认 |
| `TODO_CONFLICT` | 版本不匹配 | 先 `todo_get` 再重试一次，仍失败则告知用户 |
| `TODO_VALIDATION` | 参数不合法（长度/日期格式） | 修正参数 |
| `TODO_LIMIT` | 超出条数/频率上限 | 拆分调用 |
| `TODO_DUPLICATE_SUSPECT` | 判为疑似重复，未写入 | 让用户裁决 |
| `TODO_LOCKED` | 数据库被占用（同步写入中） | 等 1s 重试，最多 2 次 |
| `MCP_UNAUTHORIZED` | token 失效 | 报告连接失败，不要反复重试 |

错误信息必须是**给模型看的中文短句 + 可执行的 hint**，不要抛原始异常文本（会进上下文并浪费 token）。

## 6. 风险分级与审批

MainAgent 侧的策略映射：

| 模式 | 行为 |
| --- | --- |
| `risk`（默认） | 按下表自动判定 |
| `all` | 所有写操作都要确认（保守用户） |
| `none` | **不允许**对高风险工具生效；高风险永远确认 |

| 情形 | 是否弹确认 | 可被 `alwaysAllow` 关闭 |
| --- | --- | --- |
| 读类（list/get/search/stats/plan_from_text） | 否 | —（本来就不弹） |
| 单条 create / update / complete / reopen | 否 | 是 |
| `todo_delete` | **是** | **否** |
| `todo_bulk_update` 且影响 ≥5 条 | **是** | **否** |
| `todo_bulk_update` 且 <5 条 | 否 | 是 |
| `todo_clear_list` | **是** | **否** |
| `todo_accept_draft` 且 ≥10 条 | **是** | **否** |

审批弹窗必须展示：工具名、条目数量、**前 5 条的标题**（不是全部）、以及「这条操作可撤销」。不要只弹一行「AI 想调用 todo_delete」。

审计沿用现有 `IToolAuditLog` 口径：每次都记 server / 工具 / 参数摘要 / 条数 / 结果码 / 耗时，**参数里只存摘要不存全文**（备注可能很长）。

## 7. 写入约束

| 约束 | 值 | 理由 |
| --- | --- | --- |
| 标题 | ≤200 字符 | 与现有 `TodoItem.MaxContentChars` 对齐 |
| 备注 | ≤4000 字符 | 防上下文与文件体积爆掉 |
| 单次 create | ≤5 条 | 防止模型一口气灌 50 条 |
| 单次响应 | ≤50 条 | 控制工具结果进上下文的体积 |
| 幂等窗口 | 10 分钟（`clientRequestId`） | 工具循环重发不会产生重复条目 |
| 时间格式 | ISO-8601 带偏移 | 多端/多时区必须显式带偏移，禁止「明天」这种自然语言直接落库 |
| 删除 | 只软删，回收站保留 7 天 | 高风险操作必须有退路 |
| 撤销 | 每条写操作可通过 `todo_events` 反向执行 | 低风险静默写入的前提 |

## 8. 草稿流程（AI 生成初版 → 人调整）

```
用户：把这段需求拆成待办
  → MainAgent 调 todo_plan_from_text（低风险，不弹窗）
  → 应用返回 draftId + items（含 excerpt / 疑似重复）
  → MainAgent 渲染成派生卡片（复用现有 DerivedCard）
  → 用户逐条 ✓ / ✎ / ✕，或「全部采纳」
  → 采纳走 todo_accept_draft（≥10 条弹确认）
  → 写入 todos，带 source=ai + sourceSessionId + sourceExcerpt
```

选这条路的原因：现有派生卡片机制本来就是「产物默认不进上下文、用户显式采纳」的设计（见 [DerivedCard.razor](../../src/App/SDemo.DesktopAgent.UI/Components/Plugins/DerivedCard.razor)），与「待办初版必须人确认」完全同构，几乎不用新造 UI。草稿保留在应用侧（`ai_drafts` 表），用户改完再采纳，**不会重复请求模型**。

## 9. 非目标

| 不做 | 原因 |
| --- | --- |
| 暴露 SQL / 表结构 | 应用的数据模型不给模型看，工具面就是全部契约 |
| 文件路径读写 | 待办不碰磁盘上的用户文件 |
| 跨应用调用（待办去调会议） | 应用之间不直连，各自对 MainAgent 暴露工具 |
| v1 的推送/订阅（SSE 变更流） | 模型是拉模式，推送带来生命周期问题；有需要时再加 `todo_changes_since(revision)` |
| 账号体系 | 本机回环 + token 已足够；身份是同步那层的事 |
| `source` 让模型自报 | 来源必须由 MainAgent 侧注入，否则 provenance 可伪造 |
