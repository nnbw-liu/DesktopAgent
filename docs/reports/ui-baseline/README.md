# UI 截图基线（R1.2）

这些 PNG 是**组件渲染基线**：由 bUnit 渲染真实 Razor 组件得到 HTML，再用系统已安装的 WebView2 运行时截图。
同一份 `app.css`、同一套组件，但**不含 WPF 外壳**（侧栏、托盘、窗口边框）。
WebView2 里的真实交互仍按 [r1-ui-checklist.md](../r1-ui-checklist.md) 人工走查。

| 文件 | 状态 |
| --- | --- |
| `01-empty.png` | 空态：欢迎语、输入框、工具 chip |
| `02-chat.png` | 有记录：消息、工具统计、`审批 2 次`、验证徽标 |
| `03-settings.png` | 设置：上下文 / 压缩 / 路由模型 |
| `04-compaction.png` | 压缩：分隔线 + 分段明细（含摘要成本） |

## 重新生成

```powershell
# 1. 渲染 HTML（写 docs/reports/ui-baseline/*.html，并同步 app.css）
$env:SDEMO_UI_BASELINE = '1'
dotnet test tests/SDemo.DesktopAgent.UI.Tests -c Release --filter "FullyQualifiedName~UiBaselineTests"
Remove-Item Env:SDEMO_UI_BASELINE

# 2. 用 WebView2 截图（需要已安装 WebView2 运行时；Windows 11 默认自带）
dotnet run --project tools/ui-baseline/WebViewShot -c Release -- docs/reports/ui-baseline
```

比对时只看颜色 / 间距 / 文案是否明显漂移，不做像素级 diff；像素差异不作为失败条件。
