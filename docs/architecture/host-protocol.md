# 宿主控制协议（DesktopAssembly ⇄ 应用）

> 状态：S0 草案，已确认方向，未实现
> 关联：[总体架构](desktop-shell.md) · [组件契约](widget-contract.md)
> 适用范围：DesktopAssembly 与所有受管应用之间的进程、IPC、窗口管控约定

---

## 1. 角色与边界

| 角色 | 位置 | 权限 |
| --- | --- | --- |
| **宿主** `DesktopAssembly.exe` | 常驻，`%APPDATA%\SDemo\DesktopAssembly\apps\` | 发现、校验、拉起、保活、杀进程、跨进程管控 native 窗口 |
| **应用** `<app>.exe` | 由宿主拉起或用户自行启动 | 持有自己的数据；暴露本机 HTTP；自建 native 窗口 |

协议的硬线：**宿主永远不读应用的数据库，应用永远不改自己的 z-order / 屏幕坐标。** 两侧沟通只经过本机的回环 HTTP，不共享内存、不注入 DLL、不挂全局钩子。

## 2. `app.json`（进程与 MCP 信息）

与 [widget.json](widget-contract.md) 分开：`app.json` 描述「怎么把进程跑起来」，`widget.json` 描述「窗口长什么样」。

> Schema：[app.schema.json](../../contracts/schemas/app.schema.json)（含 `startup` 策略）。

```json
{
  "schemaVersion": 1,
  "id": "sdemo.planner",
  "name": "待办",
  "version": "0.1.0",
  "executable": "payload\\Todo.exe",
  "arguments": ["--ipc"],
  "sha256": "9f2c…",
  "singleInstanceMutex": "SDemo.App.sdemo.planner",
  "startupTimeoutMs": 8000,
  "idleTimeoutMs": 300000,
  "startup": "onDemand",
  "minHostApiVersion": "1.0",
  "mcp": {
    "transport": "http",
    "path": "/mcp",
    "port": "runtime",
    "token": "runtime"
  }
}
```

| 字段 | 说明 |
| --- | --- |
| `executable` | 相对 `apps\<appId>\` 的路径，**不接受绝对路径与 `..`** |
| `arguments` | 数组，逐项传给进程，**不经 shell 拼接**（沿用现有「参数不经过 shell」的约定） |
| `sha256` | 首次安装时由用户确认并登记；之后每次启动前校验 |
| `startupTimeoutMs` | 拉起后等待 `runtime.json` 出现 + 健康检查通过的上限 |
| `idleTimeoutMs` | 无 MCP 调用、无组件可见时的退出延迟；`0` 表示常驻不退出 |
| `startup` | `onDemand`（默认）/ `host`（壳启动后拉起）/ `manual`（只由用户或 Agent 拉起）；用户可用 `widgets.json.startupOverride` 覆盖（Q16） |
| `mcp` | 仅供 MainAgent 发现，宿主只透传、不解析工具语义 |

## 3. 发现与启动状态机

```
Discovered → Validated → Launching → Handshaking → Ready ⇄ Idle → Stopped
                 │                        │            │
                 └────────────► Failed ◄──┴────────────┘
```

| 状态 | 判定 | 失败处理 |
| --- | --- | --- |
| Discovered | 扫描到 `apps\*\app.json` + `widget.json` | 目录缺失 → 跳过 |
| Validated | 两份清单通过校验、exe 存在、sha256 一致、互斥量名合法 | 不一致 → 提示「修复/移除」，**不拉起** |
| Launching | 进程已创建，等待 runtime 文件 | 超时 → 杀掉并用退避重启（1s / 2s / 5s，最多 3 次） |
| Handshaking | `GET /v1/health` + `POST /v1/handshake` | 401 / 版本不符 → Failed（不重试） |
| Ready | 健康检查通过、组件可显示 | — |
| Idle | 组件隐藏且超过 `idleTimeoutMs` 无调用 | 发 `POST /v1/shutdown`，超时后 `TerminateProcess` |

> 应用可通过 `POST /v1/widgets/{id}/request { "kind": "idleHold", "value": true }` 声明「暂不空闲退出」；宿主只存内存标志，进程退出即清（见 [host-endpoints](host-endpoints.md) §6、DA-95）。

启动顺序：宿主先显示**骨架态**（组件外框 + 图标 + 「正在启动」），进程就绪后再切换到真实窗口。绝不允许白窗或无提示的 1 秒空白。

## 4. 单实例与 `runtime.json`

应用启动后立即：

1. 打开命名互斥量 `SDemo.App.<appId>`；已存在则**不新建窗口**，直接读既有 `runtime.json` 连上去；
2. 绑定 `127.0.0.1:0`（系统分配端口），**不写死端口**；
3. 写 `%LOCALAPPDATA%\SDemo\DesktopAssembly\runtime\<appId>.json`，ACL 设为仅当前用户。

```json
{
  "pid": 24812,
  "port": 51423,
  "token": "…",
  "startedAt": "2026-10-28T23:40:12+08:00",
  "appVersion": "0.1.0",
  "hostApiVersion": "1.0"
}
```

- 宿主每隔 2s 轮询该文件（最长 `startupTimeoutMs`）；文件里的 `pid` 已退出时删除残留文件再重试。
- **token 来源**：宿主拉起时通过环境变量 `SDEMO_APP_TOKEN` 传入（32 字节 base64url），应用写进 `runtime.json` 供宿主读回；用户自行双击启动时由应用生成。两种路径下 token 都**只在本机、只在本次进程存活期间有效**，重启即换。
- 端口与 token 必须写在 `%LOCALAPPDATA%`：它们绑定本机进程，**不可随 `%APPDATA%` 漫游**。

> 宿主自身的监听器、`host.json`、`client-token` 与 Agent 使用的 client 面端点，见 [host-endpoints.md](host-endpoints.md)；本节只定义每个应用的 `runtime.json`。

## 5. 握手与鉴权

- 所有请求带 `Authorization: Bearer <token>`；除 `/v1/health` 外全部校验。
- 只监听 `127.0.0.1`，禁止 `0.0.0.0` / `::`；不设 CORS（浏览器不该访问到它）。
- 失败 3 次以上，宿主视为被劫持，直接停止该应用并把组件置为错误态。

```
POST /v1/handshake
{ "hostVersion": "0.1.0", "hostApiVersion": "1.0", "widgetId": "sdemo.planner.widget" }

200 OK
{ "appVersion": "0.1.0", "hostApiVersion": "1.0", "capabilities": ["native-window", "notifications"] }
```

`capabilities` 用来做**双向降级**：宿主认识但应用不支持的能力（例如后续的悬浮角标），宿主自行关闭，不问用户。

## 6. 端点表

### 6.1 宿主 → 应用（`http://127.0.0.1:{port}`）

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/v1/health` | 存活探测，返回 `{ ok, pid, since }` |
| POST | `/v1/handshake` | 交换版本与能力 |
| POST | `/v1/shutdown` | 优雅退出 `{ graceMs: 2000 }` |
| POST | `/v1/widgets/{id}/command` | 宿主指令：`show` / `hide` / `focus` / `refresh` / `themeChanged` / `configChanged` |
| GET | `/v1/widgets/{id}/state` | 组件状态（用于宿主显示占位与诊断） |

### 6.2 应用 → 宿主（`SDEMO_HOST_URL`）

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| POST | `/v1/apps/{appId}/register` | native 组件上报窗口：`{ widgetId, hwnd, dpiAware: "per-monitor-v2" }` |
| POST | `/v1/apps/{appId}/unregister` | 窗口销毁 |
| POST | `/v1/notify` | 请求宿主弹通知 `{ title, body, level }` |
| POST | `/v1/widgets/{id}/request` | 声明意图：`layer` / `alwaysOnTop` / `clickThrough` 的**切换请求**（宿主可拒绝） |
| GET | `/v1/host/info` | 宿主版本、主题、强调色、语言、当前显示器集合 |

**注意**：`request` 是「请求」不是「设置」。宿主是唯一的策略执行者，收到非法请求（例如 native 组件请求 `layer=desktop`）返回 `403 HOST_POLICY_DENIED`，并在日志里记一条。

> 以上是 **app 面**（应用 token）。**client 面**（Agent 的 `GET /v1/host`、`GET /v1/apps`、`POST ensure`）与两类 token 的配对规则见 [host-endpoints.md](host-endpoints.md) §5–§7。

## 7. 窗口管控白名单（仅 `native`）

宿主对别的进程的窗口**只允许**做以下 6 件事：

| # | Win32 操作 | 用途 |
| --- | --- | --- |
| 1 | `SetWindowPos` | 位置、尺寸、z-order（`HWND_TOPMOST` / `HWND_NOTOPMOST` / `HWND_BOTTOM`） |
| 2 | `GetWindowRect` | 读取实际矩形（拖动后回写布局状态） |
| 3 | `SetWindowLongPtr(GWL_EXSTYLE)` | 只允许改 `WS_EX_TOOLWINDOW`（不进任务栏/Alt+Tab）与 `WS_EX_TRANSPARENT`（穿透开关） |
| 4 | `ShowWindow` | `SW_HIDE` / `SW_SHOWNOACTIVATE`（显示但不抢焦点） |
| 5 | `PostMessage(WM_CLOSE)` | 优雅关窗 |
| 6 | `SetWindowDisplayAffinity` | 可选：用户开启「截屏隐藏」时 |

**明令禁止**：

- `SetParent` / 修改 `GWLP_HWNDPARENT` —— 跨进程改父窗或属主在 Windows 上会导致 DPI、输入法、焦点的一系列问题；
- 改 `WS_CHILD` 等窗口本质样式；
- `SetWindowsHookEx` 全局钩子、`SendMessage` 跨进程同步阻塞调用；
- 接管应用的消息循环、替应用处理 `WM_DPICHANGED`。

应用侧对应义务：**必须** PerMonitorV2 DPI 感知；**必须**自己处理拖动与位置变化并回写宿主（`widgets.json` 由宿主独占写入，应用只上报）。

## 8. 位置、多屏与 DPI

| 规则 | 说明 |
| --- | --- |
| 存储单位 | DIP（设备无关像素），不是物理像素 |
| 存储键 | `显示器标识 + 分辨率 + DPI` 的三元组哈希 |
| 载入校验 | 存的矩形必须与**当前**某个显示器至少有 80×80 可见；否则回落到 `widget.json` 的锚点位置 |
| 显示器变更 | 监听 `WM_DISPLAYCHANGE`，重新校验全部组件 |
| 显示器拔插 | 消失的那块上的组件迁到主屏右下角，不叠在一起（按 `id` 排序错开 24px） |
| 吸附 | 松手时距边 < `snapPx` 吸附；吸附值本身也持久化 |

## 9. 生命周期与退出顺序

1. 宿主退出 → 对每个应用发 `shutdown`（等 2s）→ 超时 `TerminateProcess` → 保存 `widgets.json` → 退出。
2. 应用崩溃 → 宿主显示占位卡片，按 §3 退避重启；重启成功后组件回到原位。
3. 应用主动退出（例如主窗口关闭且达到 idle）→ 组件隐藏、布局保留，下次拉起后恢复。
4. 用户卸载 → 清 `apps\<appId>\`，**保留** `%APPDATA%\SDemo\Apps\<appId>\` 数据，并在卸载确认框里明说保留路径。

## 10. 安全模型

| 面 | 措施 |
| --- | --- |
| 可执行文件 | 首次安装需用户确认并登记 sha256；之后哈希不符一律拒绝启动并红字提示（不提供「仍然启动」） |
| 路径 | `executable` 必须落在 `apps\<appId>\` 内；解析后校验真实路径，拒绝符号链接/junction 跳出去 |
| 传输 | 仅回环、随机端口、随机 token、进程级有效 |
| 权限 | 组件 `permissions` 里的每项由宿主在设置页展示与授权（`http-localhost` 默认给，`notifications` 首次用时询问） |
| 升级 | 升级 = 替换 `apps\<appId>\` 目录 + 重新登记哈希；旧哈希保留一条以便回滚 |
| 数据 | 宿主不接触应用数据目录；应用数据权限继承用户目录 ACL |

## 11. 错误码

| 码 | 含义 | 宿主行为 |
| --- | --- | --- |
| `HOST_POLICY_DENIED` | 应用请求了非法策略（如 native 要桌面层） | 拒绝，记日志，不改状态 |
| `HOST_VERSION_MISMATCH` | 握手版本不符 | Failed，不重试，提示升级 |
| `HOST_TOKEN_INVALID` | 鉴权失败（累计 3 次） | 停止应用并置错误态 |
| `APP_START_TIMEOUT` | 启动超时 | 退避重启 |
| `APP_CRASHED` | 启动后异常退出 | 退避重启，3 次后转错误态 |
| `APP_HEALTH_FAILED` | 健康检查连续失败 | 同上 |
| `WIDGET_WINDOW_LOST` | native 窗口句柄失效（宿主发现 `IsWindow` 为假） | 通知应用重建窗口，保留布局 |
| `TODO_CONFLICT` 等业务码 | 见 [todo-mcp-surface.md](todo-mcp-surface.md) | 宿主透传，不解释 |

## 12. 日志与诊断

- 宿主日志：`%LOCALAPPDATA%\SDemo\logs\desktop-assembly\yyyy-MM-dd.log`，按天分文件、单文件上限 10MB。
- 每个应用在自己的日志里记 `requestId`，宿主记录的 `requestId` 与之对应，排障时可以串起一次调用。
- 托盘菜单提供「组件诊断」：列出每个组件的状态、最后错误、端口与 pid、一键复制诊断信息。
