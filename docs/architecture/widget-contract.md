# 组件契约（`widget.json`）

> 状态：S0 草案，已确认方向，未实现
> 关联：[总体架构](desktop-shell.md) · [宿主控制协议](host-protocol.md)
> 适用范围：`sdemo.planner` 及后续所有要挂到桌面上的应用

---

## 1. 这份契约解决什么

一份 `widget.json` 让 **DesktopAssembly 不用认识任何具体应用**，就能知道：

- 这个组件长什么样、多大、能拖到哪里、在哪一层；
- 它是谁渲染的（应用自己开窗，还是宿主开 WebView2）；
- 它需要什么权限、什么时候该拉起应用；
- 宿主版本不够时怎么优雅降级。

**边界**：组件清单只描述「渲染属性 + 布局意图 + 生命周期」，不含业务语义。待办有几种状态、字段怎么叫，宿主一概不知道，也不许知道。

## 2. 目录约定

```
%APPDATA%\SDemo\DesktopAssembly\apps\<appId>\
├─ app.json        ← 进程与 MCP 信息（定义见 host-protocol.md §3）
├─ widget.json     ← 本文
├─ icon.png        ← 可选，建议 256×256；同时用于托盘与组件标题
└─ payload\        ← wasm 组件的 web 产物根目录（native 组件可为空目录）
   └─ index.html
```

一个应用 v1 只支持**一个**组件。`widget.json` 出现多个（数组）留给后续，本版解析器遇到数组直接判非法，避免出现过早的复杂度。

## 3. 完整示例（待办小组件）

```json
{
  "schemaVersion": 1,
  "id": "sdemo.planner.widget",
  "appId": "sdemo.planner",
  "name": "待办",
  "description": "桌面待办清单：随时记一条，勾掉一条。",
  "version": "0.1.0",
  "hostApiVersion": "1.0",

  "renderer": "native",
  "native": {
    "process": "Todo.exe",
    "layered": true,
    "resizable": true,
    "minSize": { "width": 280, "height": 240 }
  },

  "wasm": null,

  "size": { "width": 320, "height": 420, "maxWidth": 640, "maxHeight": 900 },

  "layer": "floating",

  "position": {
    "mode": "anchored",
    "corner": "top-right",
    "offsetX": 24,
    "offsetY": 24,
    "snapPx": 12,
    "rememberPerMonitor": true
  },

  "behavior": {
    "alwaysOnTopDefault": true,
    "dragMode": "app",
    "clickThrough": "toggleWithHotkey",
    "hideBehindFullscreenApp": true,
    "opacity": { "default": 0.92, "min": 0.55 },
    "keepAlive": "onDemand"
  },

  "focus": {
    "stealOnShow": false,
    "activateOnClick": true
  },

  "theme": {
    "mode": "auto",
    "followAccent": true,
    "surface": "acrylic"
  },

  "activation": {
    "hotkey": "Ctrl+Alt+T",
    "launchOnStartup": false,
    "showInTray": true
  },

  "permissions": ["http-localhost", "notifications"],

  "config": {
    "type": "object",
    "properties": {
      "showCompleted": { "type": "boolean", "title": "显示已完成", "default": false },
      "maxItems": { "type": "integer", "title": "最多显示条数", "minimum": 3, "maximum": 30, "default": 8 }
    }
  }
}
```

## 4. 字段表

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `schemaVersion` | ✅ | 本文版本，当前 `1`。解析器遇到更高版本直接跳过（不猜测） |
| `id` | ✅ | 反向域名，`^[a-z0-9]+(\.[a-z0-9-]+)+$`，全局唯一 |
| `appId` | ✅ | 对应 `apps\<appId>\` 目录与 `app.json` 的 `id`，必须一致 |
| `name` / `description` / `version` | ✅ | 展示用；`version` 是组件版本，与 exe 版本独立 |
| `hostApiVersion` | ✅ | 形如 `"1.0"`；宿主只做「主版本相同 + 次版本 ≤ 宿主」的判定 |
| `renderer` | ✅ | `native` 或 `wasm`，见 §5 |
| `native` | 条件 | `renderer=native` 时必填 |
| `wasm` | 条件 | `renderer=wasm` 时必填；`{ "webRoot": "payload", "entry": "index.html" }` |
| `size` | ✅ | 原子单位为 DIP；宿主按监视器 DPI 换算 |
| `layer` | ✅ | `floating`（悬浮）/ `desktop`（贴桌面层）；`desktop` 仅允许 `wasm` |
| `position` | ✅ | 默认位置与记忆策略 |
| `behavior` | ✅ | 层级、拖动、穿透、透明度、保活 |
| `focus` | ✅ | 焦点策略，见 §7 |
| `theme` | ✅ | 主题跟随与表面材质 |
| `activation` | ⬜ | 快捷键、开机自启、托盘入口 |
| `permissions` | ⬜ | 缺省为空数组 = 最小权限 |
| `config` | ⬜ | 设置表单的 JSON Schema 子集，见 §8 |

未知字段**一律忽略**（向前兼容）；必填缺失或类型不符 → 判非法，整份清单不加载。

## 5. 两种 renderer 的行为差异（最关键的一节）

| 维度 | `native` | `wasm` |
| --- | --- | --- |
| 窗口由谁创建 | **应用进程** | **宿主** |
| 宿主做的事 | 跨进程做 z-order / 位置 / 显隐 / 穿透位切换 | 创建窗口、挂 WebView2、加载 `payload\index.html`、全部布局 |
| 半透明 | ✅ `AllowsTransparency=true`（即 `WS_EX_LAYERED`） | ⚠️ WebView2 **不支持分层窗口**，只能不透明或 Win11 亚克力后备 |
| 点击穿透 | ✅ 与分层窗口天然兼容 | ❌ 不支持 |
| 贴桌面层（`layer=desktop`） | ❌ 需要跨进程 `SetParent`，禁止 | ✅ 宿主自渲染，可以 `SetParent` 到 `WorkerW` |
| 独立进程/崩溃隔离 | ✅ 组件挂了不影响其它组件 | ⚠️ 同进程，靠 WebView2 的渲染进程兜底 |
| 首屏 | 快 | 慢（Blazor WASM 首次 ~1s+，之后走缓存） |
| 加载用程序集 | 不需要 | 不需要（加载静态 web 产物） |

**为什么 `native` 不允许贴桌面层**：`SetParent` 到另一个进程的 `Progman`/`WorkerW` 是唯一能做「桌面之上、所有窗口之下」的手段，但它会在 DPI 缩放、输入法候选框、焦点切换上反复出问题，且 Explorer 重启后句柄失效。这条能力只对宿主自渲染的 `wasm` 组件开放。

**为什么 `native` 不需要宿主加载程序集**：应用自己做窗口，宿主只发 `SetWindowPos` / `SetWindowLongPtr`。这避免了 `AssemblyLoadContext` 版本矩阵这一整摊事——这是当初选 native 的主要原因之一。

## 6. 布局声明（宿主执行，应用不碰）

| 声明 | 宿主行为 |
| --- | --- |
| `layer=floating` | 窗口置顶（`alwaysOnTopDefault` 决定初始值）；用户可右键切「置顶 / 普通」 |
| `layer=desktop` | 仅 `wasm`：`SetParent` 到 `WorkerW`；与置顶互斥，界面里灰掉置顶选项 |
| `position.mode=anchored` | 首次出现在指定角 + 偏移；拖动后转为 `remember` |
| `position.mode=remember` | 用 `widgets.json` 里存的矩形；载入时校验该矩形仍落在**当前**的某个显示器上，否则回落到锚点 |
| `snapPx` | 拖动松手时距离屏幕边缘 < `snapPx` 则吸附 |
| `rememberPerMonitor` | 按「显示器标识 + 分辨率」分组记忆位置；换显示器不丢布局 |
| `size` | 宿主不做内容布局，只约束窗口最小/最大尺寸 |
| `opacity` | native：应用自绘透明度，宿主只传值；wasm：宿主设窗口不透明度 |
| `hideBehindFullscreenApp` | 检测到前台是全屏窗口时降低 z-order 或隐藏，退出全屏后恢复 |

## 7. 焦点与「不可最小化」

`focus.stealOnShow = false`：宿主显示组件时用 `SWP_NOACTIVATE`，不会把你正在写的字切走。
`focus.activateOnClick = true`：用户主动点击时正常激活，输入框能打字（**不要**图省事加 `WS_EX_NOACTIVATE`，那会导致组件里的输入框永远拿不到焦点）。

「不可最小化」的落地组合（宿主负责，应用不用写）：

- `ShowInTaskbar = false` + `WS_EX_TOOLWINDOW`：不进任务栏、不进 Alt+Tab；
- 窗口无最小化按钮，且宿主拦截 `WM_SYSCOMMAND / SC_MINIMIZE`；
- 位置与尺寸不随主窗口联动。

> ⚠️ Win+D（显示桌面）是否会折叠这类窗口需要**实测**。拦截 `SC_MINIMIZE` 是已知做法之一，但不保证覆盖所有输入路径；S1 里必须专门验证，验证前不要对用户承诺「永远可见」。

## 8. `config`：设置表单的 Schema 子集

宿主用描述符渲染设置表单，**不加载任何应用代码**。只支持：

| 类型 | 控件 |
| --- | --- |
| `boolean` | 开关 |
| `integer` / `number` | 数字输入（带 `minimum` / `maximum`） |
| `string` | 单行输入（可选 `enum` → 下拉） |
| `string` + `format: "path"` | 文件/目录选择器（只读展示） |

不支持 `object` / `array` / `oneOf`。需要复杂表单的应用请在自己的主窗口里做，`config` 只承载「在桌面上顺手改一两个开关」这类需求。

## 9. 校验与降级

| 情况 | 宿主行为 |
| --- | --- |
| `widget.json` 语法错误 / 必填缺失 | 跳过该组件，`widgets.json` 保留其布局，托盘菜单显示「1 个组件加载失败（查看原因）」 |
| `id` 重复 | 后加载的判失败（按目录名顺序） |
| `schemaVersion` 更高 | 跳过并提示「需要更新 DesktopAssembly」 |
| `hostApiVersion` 主版本不同 | 禁用该组件，不影响其它组件 |
| `layer=desktop` 但 `renderer=native` | 判非法，整份清单不加载（契约冲突必须显式报错，不要静默纠正） |
| 应用 exe 不存在 / 哈希不符 | 不启动；「修复 / 移除」两个入口（见 host-protocol §3） |
| 组件窗口创建失败 | 宿主显示占位卡片（图标 + 组件名 + 错误摘要），布局状态保留 |

失败一律**隔离到单个组件**，沿用 [插件系统](detail/plugin-system.md) 「坏一个不影响整体」的既有原则。

## 10. 与现有代码的对应关系

| 现有概念 | 组件契约里的对应物 |
| --- | --- |
| `PluginHostVersion`（[PluginHostVersion.cs](../../src/Plugins/SDemo.DesktopAgent.Plugins.Abstractions/PluginHostVersion.cs)） | `hostApiVersion`，同样的「主版本相同 + 次版本 ≤ 宿主」判定 |
| `PluginValidator`（[PluginValidator.cs](../../src/Plugins/SDemo.DesktopAgent.Plugins.Runtime/PluginValidator.cs)） | 清单校验器的写法可以直接照抄：逐字段校验、错误聚合成列表、不抛异常 |
| `PluginCatalog` 的扫描与降级 | 组件注册表的扫描与降级 |
| 设置里的作用域覆盖 | 组件 `config` 的值存 `widgets.json`，不进应用设置 |

**不要复用插件系统的目录与清单**：插件是「给聊天用的声明式能力」，组件是「挂在桌面上的独立进程 UI」。两者生命周期、权限模型、失败域都不同，混在一起会让插件加载器背上一层它不该有的责任。
